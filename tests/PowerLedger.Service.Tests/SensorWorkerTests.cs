using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class SensorWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Every_read_runs_on_one_dedicated_thread_against_one_set()
    {
        var built = new List<FakeSensorSet>();
        using var worker = new SensorWorker(() => Add(built, new FakeSensorSet()), Timeout, new FakeTimeProvider());
        for (var i = 0; i < 3; i++) (await worker.ReadAsync(Samples.T0.AddSeconds(i), 1, CancellationToken.None)).ShouldNotBeNull();
        built.Count.ShouldBe(1);
        built[0].Reads.ShouldBe(3);
        built[0].ThreadId.ShouldNotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public async Task A_hung_read_abandons_the_set_and_the_next_read_starts_a_fresh_one()
    {
        var clock = new FakeTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        var built = new List<FakeSensorSet>();
        using var worker = new SensorWorker(
            () => Add(built, built.Count == 0 ? new FakeSensorSet((t, d) => { gate.Wait(); return Samples.At(t, d); }) : new FakeSensorSet()),
            Timeout, clock);

        var hung = worker.ReadAsync(Samples.T0, 1, CancellationToken.None);
        await WaitFor.True(() => built.Count == 1 && built[0].Reads == 1);
        clock.Advance(Timeout);
        (await hung).ShouldBeNull();
        worker.Abandoned.ShouldBe(1);

        (await worker.ReadAsync(Samples.T0.AddSeconds(1), 1, CancellationToken.None)).ShouldNotBeNull();
        built.Count.ShouldBe(2);

        built[0].Disposed.ShouldBeFalse();   // still stuck in its call
        gate.Set();
        await WaitFor.True(() => built[0].Disposed);
    }

    [Fact]
    public async Task Rebuild_throws_the_set_away_and_disposes_it()
    {
        var built = new List<FakeSensorSet>();
        using var worker = new SensorWorker(() => Add(built, new FakeSensorSet()), Timeout, new FakeTimeProvider());
        await worker.ReadAsync(Samples.T0, 1, CancellationToken.None);
        worker.Rebuild();
        await WaitFor.True(() => built[0].Disposed);
        await worker.ReadAsync(Samples.T0.AddSeconds(1), 1, CancellationToken.None);
        built.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_set_that_cannot_be_built_fails_the_read_and_is_tried_again_next_time()
    {
        var attempts = 0;
        using var worker = new SensorWorker(
            () => ++attempts == 1 ? throw new InvalidOperationException("no sensors") : new FakeSensorSet(),
            Timeout, new FakeTimeProvider());
        await Should.ThrowAsync<InvalidOperationException>(() => worker.ReadAsync(Samples.T0, 1, CancellationToken.None));
        (await worker.ReadAsync(Samples.T0.AddSeconds(1), 1, CancellationToken.None)).ShouldNotBeNull();
        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task Disposing_the_worker_disposes_the_set()
    {
        var set = new FakeSensorSet();
        var worker = new SensorWorker(() => set, Timeout, new FakeTimeProvider());
        await worker.ReadAsync(Samples.T0, 1, CancellationToken.None);
        worker.Dispose();
        await WaitFor.True(() => set.Disposed);
    }

    private static FakeSensorSet Add(List<FakeSensorSet> built, FakeSensorSet set)
    {
        lock (built) built.Add(set);
        return set;
    }
}
