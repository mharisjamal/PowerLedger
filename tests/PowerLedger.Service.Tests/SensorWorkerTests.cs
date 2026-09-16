using Microsoft.Extensions.Time.Testing;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class SensorWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Every_read_runs_on_one_dedicated_thread_against_one_set()
    {
        var built = new List<FakeSensorSet>();
        using var worker = new SensorWorker(_ => Add(built, new FakeSensorSet()), Timeout, new FakeTimeProvider());
        for (var i = 0; i < 3; i++) (await worker.ReadAsync(Samples.T0.AddSeconds(i), 1, CancellationToken.None)).ShouldNotBeNull();
        var sets = Built(built);
        sets.Length.ShouldBe(1);
        sets[0].Reads.ShouldBe(3);
        sets[0].ThreadId.ShouldNotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public async Task A_hung_read_abandons_the_set_and_the_next_read_starts_a_fresh_one()
    {
        var clock = new FakeTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        var built = new List<FakeSensorSet>();
        using var worker = new SensorWorker(
            _ => Add(built, Built(built).Length == 0 ? new FakeSensorSet((t, d) => { gate.Wait(); return Samples.At(t, d); }) : new FakeSensorSet()),
            Timeout, clock);

        var hung = worker.ReadAsync(Samples.T0, 1, CancellationToken.None);
        await WaitFor.True(() => Built(built) is [{ Reads: 1 }]);
        clock.Advance(Timeout);
        (await hung).ShouldBeNull();
        worker.Abandoned.ShouldBe(1);

        (await worker.ReadAsync(Samples.T0.AddSeconds(1), 1, CancellationToken.None)).ShouldNotBeNull();
        var sets = Built(built);
        sets.Length.ShouldBe(2);

        sets[0].Disposed.ShouldBeFalse();   // still stuck in its call
        gate.Set();
        await WaitFor.True(() => sets[0].Disposed);
    }

    [Fact]
    public async Task A_set_is_built_with_a_token_cancelled_the_moment_the_set_is_abandoned_or_thrown_away()
    {
        var clock = new FakeTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        var built = new List<FakeSensorSet>();
        using var worker = new SensorWorker(
            retired => Add(built, Built(built).Length == 0
                ? new FakeSensorSet((t, d) => { gate.Wait(); return Samples.At(t, d); }) { Retired = retired }
                : new FakeSensorSet { Retired = retired }),
            Timeout, clock);
        try
        {
            var hung = worker.ReadAsync(Samples.T0, 1, CancellationToken.None);
            await WaitFor.True(() => Built(built) is [{ Reads: 1 }]);
            Built(built)[0].Retired.IsCancellationRequested.ShouldBeFalse();
            clock.Advance(Timeout);
            (await hung).ShouldBeNull();
            Built(built)[0].Retired.IsCancellationRequested.ShouldBeTrue();
            Built(built)[0].Disposed.ShouldBeFalse();                               // still stuck in its call

            (await worker.ReadAsync(Samples.T0.AddSeconds(1), 1, CancellationToken.None)).ShouldNotBeNull();
            Built(built)[1].Retired.IsCancellationRequested.ShouldBeFalse();
            worker.Rebuild();
            Built(built)[1].Retired.IsCancellationRequested.ShouldBeTrue();

            (await worker.ReadAsync(Samples.T0.AddSeconds(2), 1, CancellationToken.None)).ShouldNotBeNull();
            Built(built)[2].Retired.IsCancellationRequested.ShouldBeFalse();
            worker.Dispose();
            Built(built)[2].Retired.IsCancellationRequested.ShouldBeTrue();
        }
        finally
        {
            gate.Set();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_monitors_an_abandoned_set_finds_when_its_stuck_read_returns_never_reach_the_board(bool returnsBeforeTheNextRead)
    {
        // The service's wiring, with WMI played by the test. The first set hangs the second time it looks at the monitors,
        // and comes back having found none, though the Dell, which counts 28.32 W, is still attached.
        var clock = new FakeTimeProvider();
        var board = new MonitorBoard(MonitorBoardTests.Catalogue, clock);
        using var gate = new ManualResetEventSlim(false);
        var built = new List<FakeSensorSet>();
        using var worker = new SensorWorker(retired =>
        {
            var first = Built(built).Length == 0;
            var looks = 0;
            var display = new DisplaySource(() => new DisplayState(0.5, 2), () => true, refreshEvery: TimeSpan.Zero,
                monitors: () =>
                {
                    if (!first || ++looks == 1) return [MonitorBoardTests.Dell];
                    gate.Wait();
                    return [];
                },
                detected: found => board.Detected(found, retired));
            return Add(built, new FakeSensorSet((t, d) =>
            {
                display.Contribute(new SampleDraft());
                return Samples.At(t, d);
            }));
        }, Timeout, clock);
        Task<TickResult?> Read(int second) => worker.ReadAsync(Samples.T0.AddSeconds(second), 1, CancellationToken.None);
        try
        {
            (await Read(0)).ShouldNotBeNull();
            board.Watts(displayOn: true).ShouldBe(28.32, 1e-9);

            var hung = Read(1);
            await WaitFor.True(() => Built(built) is [{ Reads: 2 }]);
            clock.Advance(Timeout);
            (await hung).ShouldBeNull();

            if (!returnsBeforeTheNextRead) (await Read(12)).ShouldNotBeNull();      // a fresh set, which hands over the Dell it finds
            gate.Set();
            await WaitFor.True(() => Built(built)[0].Disposed);                     // only once the stuck read has returned
            board.Watts(displayOn: true).ShouldBe(28.32, 1e-9);

            for (var second = 13; second < 16; second++) (await Read(second)).ShouldNotBeNull();
            board.Watts(displayOn: true).ShouldBe(28.32, 1e-9);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Rebuild_throws_the_set_away_and_disposes_it()
    {
        var built = new List<FakeSensorSet>();
        using var worker = new SensorWorker(_ => Add(built, new FakeSensorSet()), Timeout, new FakeTimeProvider());
        await worker.ReadAsync(Samples.T0, 1, CancellationToken.None);
        worker.Rebuild();
        await WaitFor.True(() => Built(built)[0].Disposed);
        await worker.ReadAsync(Samples.T0.AddSeconds(1), 1, CancellationToken.None);
        Built(built).Length.ShouldBe(2);
    }

    [Fact]
    public async Task A_set_that_cannot_be_built_fails_the_read_and_is_tried_again_next_time()
    {
        var attempts = 0;
        using var worker = new SensorWorker(
            _ => ++attempts == 1 ? throw new InvalidOperationException("no sensors") : new FakeSensorSet(),
            Timeout, new FakeTimeProvider());
        await Should.ThrowAsync<InvalidOperationException>(() => worker.ReadAsync(Samples.T0, 1, CancellationToken.None));
        (await worker.ReadAsync(Samples.T0.AddSeconds(1), 1, CancellationToken.None)).ShouldNotBeNull();
        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task Disposing_the_worker_disposes_the_set()
    {
        var set = new FakeSensorSet();
        var worker = new SensorWorker(_ => set, Timeout, new FakeTimeProvider());
        await worker.ReadAsync(Samples.T0, 1, CancellationToken.None);
        worker.Dispose();
        await WaitFor.True(() => set.Disposed);
    }

    private static FakeSensorSet Add(List<FakeSensorSet> built, FakeSensorSet set)
    {
        lock (built) built.Add(set);
        return set;
    }

    /// <summary>The sets built so far, copied under the lock <see cref="Add"/> takes. A list counts an item before it stores
    /// it, so a read that doesn't take the lock can find a set counted that isn't there yet.</summary>
    private static FakeSensorSet[] Built(List<FakeSensorSet> built)
    {
        lock (built) return [.. built];
    }
}
