using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class SensorWorkerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Every_read_runs_on_the_loops_own_thread_against_one_set()
    {
        var built = new List<FakeSensorSet>();
        await using var loop = new Loop(_ => Add(built, new FakeSensorSet()), new FakeTimeProvider());
        for (var i = 0; i < 3; i++) (await loop.Read(Samples.T0.AddSeconds(i))).ShouldNotBeNull();
        var sets = Built(built);
        sets.Length.ShouldBe(1);
        sets[0].Reads.ShouldBe(3);
        sets[0].ThreadId.ShouldBe(loop.ThreadIds.Single());
        sets[0].ThreadId.ShouldNotBe(Environment.CurrentManagedThreadId);
        loop.PoolThreads.ShouldBe(0);
    }

    [Fact]
    public async Task The_watchdog_fires_only_on_an_overrun_so_a_quiet_loop_is_never_abandoned()
    {
        var clock = new FakeTimeProvider();
        var built = new List<FakeSensorSet>();
        await using var loop = new Loop(_ => Add(built, new FakeSensorSet()), clock);
        (await loop.Read(Samples.T0)).ShouldNotBeNull();
        clock.Advance(Timeout * 5);                                             // a long wait between reads, as while asleep
        (await loop.Read(Samples.T0.AddSeconds(60))).ShouldNotBeNull();
        loop.Worker.Abandoned.ShouldBe(0);
        Built(built).Length.ShouldBe(1);
        loop.ThreadIds.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_hung_read_abandons_the_thread_and_its_set_and_the_loop_goes_on_on_a_fresh_thread()
    {
        var clock = new FakeTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        var built = new List<FakeSensorSet>();
        await using var loop = new Loop(
            _ => Add(built, Built(built).Length == 0 ? new FakeSensorSet((t, d) => { gate.Wait(); return Samples.At(t, d); }) : new FakeSensorSet()),
            clock);
        try
        {
            var hung = loop.Read(Samples.T0);
            await WaitFor.True(() => Built(built) is [{ Reads: 1 }]);
            clock.Advance(Timeout - TimeSpan.FromMilliseconds(1));
            hung.IsCompleted.ShouldBeFalse();
            clock.Advance(TimeSpan.FromMilliseconds(1));
            (await hung).ShouldBeNull();
            loop.Worker.Abandoned.ShouldBe(1);

            (await loop.Read(Samples.T0.AddSeconds(1))).ShouldNotBeNull();
            var sets = Built(built);
            sets.Length.ShouldBe(2);
            loop.ThreadIds.Count.ShouldBe(2);
            sets[1].ThreadId.ShouldNotBe(sets[0].ThreadId);

            sets[0].Disposed.ShouldBeFalse();                                  // still stuck in its call
            gate.Set();
            await WaitFor.True(() => sets[0].Disposed);
            (await loop.Read(Samples.T0.AddSeconds(2))).ShouldNotBeNull();     // the stuck thread, back, took nothing over
            Built(built)[1].Reads.ShouldBe(2);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Building_a_set_has_its_own_longer_allowance_and_the_read_after_it_the_usual_one()
    {
        var clock = new FakeTimeProvider();
        using var building = new ManualResetEventSlim(false);
        using var reading = new ManualResetEventSlim(false);
        var built = new List<FakeSensorSet>();
        var builds = 0;
        await using var loop = new Loop(_ =>
        {
            if (Interlocked.Increment(ref builds) == 1) building.Wait(); // a first build on a busy machine
            return Add(built, Built(built).Length == 0 ? new FakeSensorSet((t, d) => { reading.Wait(); return Samples.At(t, d); }) : new FakeSensorSet());
        }, clock);
        try
        {
            var first = loop.Read(Samples.T0);
            await WaitFor.True(() => Volatile.Read(ref builds) == 1);
            clock.Advance(BuildTimeout - TimeSpan.FromSeconds(1));
            first.IsCompleted.ShouldBeFalse();
            building.Set();
            await WaitFor.True(() => Built(built) is [{ Reads: 1 }]);
            clock.Advance(Timeout - TimeSpan.FromMilliseconds(1));     // the read's own allowance starts once the set is built
            first.IsCompleted.ShouldBeFalse();
            clock.Advance(TimeSpan.FromMilliseconds(1));
            (await first).ShouldBeNull();
            loop.Worker.Abandoned.ShouldBe(1);

            var hungBuild = new FakeTimeProvider();
            using var never = new ManualResetEventSlim(false);
            var tries = 0;
            await using var stuck = new Loop(_ =>
            {
                if (Interlocked.Increment(ref tries) == 1) never.Wait();
                return new FakeSensorSet();
            }, hungBuild);
            var read = stuck.Read(Samples.T0);
            await WaitFor.True(() => Volatile.Read(ref tries) == 1);
            hungBuild.Advance(BuildTimeout - TimeSpan.FromMilliseconds(1));
            read.IsCompleted.ShouldBeFalse();
            hungBuild.Advance(TimeSpan.FromMilliseconds(1));
            (await read).ShouldBeNull();
            never.Set();
        }
        finally
        {
            building.Set();
            reading.Set();
        }
    }

    [Fact]
    public async Task A_set_is_built_with_a_token_cancelled_the_moment_the_set_is_abandoned_or_thrown_away()
    {
        var clock = new FakeTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        var built = new List<FakeSensorSet>();
        await using var loop = new Loop(
            retired => Add(built, Built(built).Length == 0
                ? new FakeSensorSet((t, d) => { gate.Wait(); return Samples.At(t, d); }) { Retired = retired }
                : new FakeSensorSet { Retired = retired }),
            clock);
        try
        {
            var hung = loop.Read(Samples.T0);
            await WaitFor.True(() => Built(built) is [{ Reads: 1 }]);
            Built(built)[0].Retired.IsCancellationRequested.ShouldBeFalse();
            clock.Advance(Timeout);
            (await hung).ShouldBeNull();
            Built(built)[0].Retired.IsCancellationRequested.ShouldBeTrue();
            Built(built)[0].Disposed.ShouldBeFalse();                           // still stuck in its call

            (await loop.Read(Samples.T0.AddSeconds(1))).ShouldNotBeNull();
            Built(built)[1].Retired.IsCancellationRequested.ShouldBeFalse();
            await loop.Run(worker => worker.Rebuild());
            Built(built)[1].Retired.IsCancellationRequested.ShouldBeTrue();
            Built(built)[1].Disposed.ShouldBeFalse();                           // the next read disposes it, under the watchdog

            (await loop.Read(Samples.T0.AddSeconds(2))).ShouldNotBeNull();
            Built(built)[1].Disposed.ShouldBeTrue();
            Built(built)[2].Retired.IsCancellationRequested.ShouldBeFalse();
            await loop.DisposeAsync();
            Built(built)[2].Retired.IsCancellationRequested.ShouldBeTrue();
            Built(built)[2].Disposed.ShouldBeTrue();
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task Interrupting_abandons_a_read_under_way_without_counting_it_and_leaves_an_idle_loop_alone()
    {
        var clock = new FakeTimeProvider();
        using var gate = new ManualResetEventSlim(false);
        var built = new List<FakeSensorSet>();
        await using var loop = new Loop(
            _ => Add(built, Built(built).Length == 1 ? new FakeSensorSet((t, d) => { gate.Wait(); return Samples.At(t, d); }) : new FakeSensorSet()),
            clock);
        try
        {
            (await loop.Read(Samples.T0)).ShouldNotBeNull();
            loop.Worker.Interrupt();
            loop.ThreadIds.Count.ShouldBe(1);
            await loop.Run(worker => worker.Rebuild());

            var hung = loop.Read(Samples.T0.AddSeconds(1));
            await WaitFor.True(() => Built(built) is [_, { Reads: 1 }]);
            loop.Worker.Interrupt();
            (await hung).ShouldBeNull();
            loop.Worker.Abandoned.ShouldBe(0);
            (await loop.Read(Samples.T0.AddSeconds(2))).ShouldNotBeNull();
            loop.ThreadIds.Count.ShouldBe(2);
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
        // and comes back having found none, though the Dell, which counts 33.69 W, is still attached.
        var clock = new FakeTimeProvider();
        var board = new MonitorBoard(MonitorBoardTests.Catalogue, clock);
        using var gate = new ManualResetEventSlim(false);
        var built = new List<FakeSensorSet>();
        await using var loop = new Loop(retired =>
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
        }, clock);
        Task<TickResult?> Read(int second) => loop.Read(Samples.T0.AddSeconds(second));
        try
        {
            (await Read(0)).ShouldNotBeNull();
            board.Watts(displayOn: true).Total.ShouldBe(MonitorBoardTests.DellAt(null), 1e-9);

            var hung = Read(1);
            await WaitFor.True(() => Built(built) is [{ Reads: 2 }]);
            clock.Advance(Timeout);
            (await hung).ShouldBeNull();

            if (!returnsBeforeTheNextRead) (await Read(12)).ShouldNotBeNull();      // a fresh set, which hands over the Dell it finds
            gate.Set();
            await WaitFor.True(() => Built(built)[0].Disposed);                     // only once the stuck read has returned
            board.Watts(displayOn: true).Total.ShouldBe(MonitorBoardTests.DellAt(null), 1e-9);

            for (var second = 13; second < 16; second++) (await Read(second)).ShouldNotBeNull();
            board.Watts(displayOn: true).Total.ShouldBe(MonitorBoardTests.DellAt(null), 1e-9);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task A_set_that_cannot_be_built_fails_the_read_and_is_tried_again_next_time()
    {
        var attempts = 0;
        await using var loop = new Loop(
            _ => ++attempts == 1 ? throw new InvalidOperationException("no sensors") : new FakeSensorSet(),
            new FakeTimeProvider());
        await Should.ThrowAsync<InvalidOperationException>(() => loop.Read(Samples.T0));
        (await loop.Read(Samples.T0.AddSeconds(1))).ShouldNotBeNull();
        attempts.ShouldBe(2);
        loop.Worker.Abandoned.ShouldBe(0);
    }

    [Fact]
    public async Task Disposing_the_worker_disposes_the_set()
    {
        var set = new FakeSensorSet();
        var loop = new Loop(_ => set, new FakeTimeProvider());
        await loop.Read(Samples.T0);
        await loop.DisposeAsync();
        set.Disposed.ShouldBeTrue();
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

    /// <summary>
    /// A loop as the sampling loop runs one on the worker: it carries out what the test asks, one thing at a time, on the
    /// worker's thread. A read the watchdog abandons answers null, as the loop sees nothing of it, and the asks go on being
    /// carried out on the fresh thread.
    /// </summary>
    private sealed class Loop : IAsyncDisposable
    {
        private readonly BlockingCollection<(Func<SensorWorker, TickResult?> Work, TaskCompletionSource<TickResult?> Done)> _asks = new();
        private readonly HashSet<int> _threads = [];
        private TaskCompletionSource<TickResult?>? _current;
        private int _poolThreads;
        private bool _disposed;

        public Loop(Func<CancellationToken, ISensorSet> factory, TimeProvider clock)
        {
            Worker = new SensorWorker(factory, Timeout, BuildTimeout, clock);
            Worker.Start(Run, onAbandoned: _ => Interlocked.Exchange(ref _current, null)?.TrySetResult(null));
        }

        public SensorWorker Worker { get; }

        public IReadOnlyCollection<int> ThreadIds
        {
            get
            {
                lock (_threads) return [.. _threads];
            }
        }

        public int PoolThreads => Volatile.Read(ref _poolThreads);

        public Task<TickResult?> Read(DateTimeOffset at) => Run(worker => worker.Read(at, 1));

        public Task<TickResult?> Run(Action<SensorWorker> work) => Run(worker =>
        {
            work(worker);
            return null;
        });

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await Run(worker =>
            {
                worker.Dispose();
                _asks.CompleteAdding();
                return null;
            });
        }

        private Task<TickResult?> Run(Func<SensorWorker, TickResult?> work)
        {
            var done = new TaskCompletionSource<TickResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _asks.Add((work, done));
            return done.Task;
        }

        private void Run()
        {
            lock (_threads) _threads.Add(Environment.CurrentManagedThreadId);
            if (Thread.CurrentThread.IsThreadPoolThread) Interlocked.Increment(ref _poolThreads);
            foreach (var (work, done) in _asks.GetConsumingEnumerable())
            {
                Volatile.Write(ref _current, done);
                try
                {
                    var result = work(Worker);
                    Interlocked.CompareExchange(ref _current, null, done);
                    done.TrySetResult(result);
                }
                catch (Exception error) when (error is not ReadAbandonedException)
                {
                    Interlocked.CompareExchange(ref _current, null, done);
                    done.TrySetException(error);
                }
            }
        }
    }
}
