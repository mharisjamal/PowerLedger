using System.Runtime.ExceptionServices;
using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <param name="Sample">The validated sample.</param>
/// <param name="Health">Each source's health after this tick.</param>
/// <param name="SuspectCount">Suspect ticks since the set was built.</param>
internal sealed record TickResult(Sample Sample, IReadOnlyList<SourceHealth> Health, int SuspectCount);

/// <summary>Thrown on a loop thread whose read the watchdog gave up on, once the read returns: a fresh thread runs the loop
/// now, so this one must end at once and touch nothing the loop owns.</summary>
internal sealed class ReadAbandonedException : Exception
{
    public ReadAbandonedException()
        : base("The watchdog gave this read up; another thread runs the loop now.")
    {
    }
}

/// <summary>
/// The thread the sampling loop runs on, which is also the only thread that calls into a sensor set (Plan B's watchdog
/// rule; perf review §7: one thread, no thread pool hops). A read is watched by a timer armed only while it runs. One that
/// overruns its timeout abandons the thread, stuck in its call, together with its set, and starts the loop again on a fresh
/// thread, which builds a fresh set on its first read; the stuck thread disposes its set and ends once its call returns.
/// Building a set, which a busy machine can take many seconds over the first time, has an allowance of its own; the read
/// that follows has the usual one from when the set is ready.
/// Each set is built with a token that is cancelled the moment the set is abandoned or thrown away, before a fresh one can
/// be built, so whatever a stuck call passes on when it returns can be told apart and ignored. Everything but
/// <see cref="Interrupt"/> and <see cref="Abandoned"/> is called from the loop's own thread.
/// </summary>
internal sealed class SensorWorker : IDisposable
{
    private const int Idle = 0;
    private const int Reading = 1;
    private const int GivenUp = 2;

    [ThreadStatic]
    private static Lane? t_lane;

    private readonly Func<CancellationToken, ISensorSet> _factory;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _buildTimeout;
    private readonly TimeProvider _clock;
    private readonly ITimer _watchdog;
    private Action? _body;
    private Action<bool>? _onAbandoned;
    private Lane? _lane;
    private int _abandoned;

    /// <param name="timeout">How long a read may take.</param>
    /// <param name="buildTimeout">How long building a set, and disposing the one it replaces, may take.</param>
    public SensorWorker(Func<CancellationToken, ISensorSet> factory, TimeSpan timeout, TimeSpan buildTimeout, TimeProvider clock)
    {
        _factory = factory;
        _timeout = timeout;
        _buildTimeout = buildTimeout;
        _clock = clock;
        _watchdog = clock.CreateTimer(static worker => ((SensorWorker)worker!).OnWatchdog(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Threads abandoned because a read overran its timeout.</summary>
    public int Abandoned => Volatile.Read(ref _abandoned);

    /// <summary>
    /// Runs <paramref name="body"/> on a fresh thread now, and on another each time a read is abandoned, so the body must
    /// pick up wherever the loop stands. <paramref name="onAbandoned"/> is told first, on the thread that gave up, whether
    /// the read overran its timeout (true) or was interrupted (false). A <see cref="ReadAbandonedException"/> leaving the
    /// body ends its thread quietly; anything else would end the process, so the body must handle it.
    /// </summary>
    public void Start(Action body, Action<bool>? onAbandoned = null)
    {
        _body = body;
        _onAbandoned = onAbandoned;
        StartLane();
    }

    /// <summary>One tick's sample, read on the calling loop thread, with the set built first when there is none. Throws
    /// <see cref="ReadAbandonedException"/> when the read was abandoned while it ran; a read that fails throws its error.</summary>
    public TickResult Read(DateTimeOffset timestamp, double deltaSeconds)
    {
        var lane = t_lane ?? throw new InvalidOperationException("Only the loop's own thread reads the sensors.");
        TickResult? result = null;
        ExceptionDispatchInfo? failure = null;
        try
        {
            if (lane.Set is null || lane.Thrown is not null)
            {
                Arm(lane, _buildTimeout);
                if (lane.Thrown is { } old)
                {
                    lane.Thrown = null;
                    DisposeQuietly(old);
                }
                lane.Set ??= _factory(lane.Tokens.Token);
            }
            Arm(lane, _timeout);
            var set = lane.Set;
            result = new TickResult(set.Read(timestamp, deltaSeconds), set.Health, set.SuspectCount);
        }
        catch (ReadAbandonedException)
        {
            throw;
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
        }
        if (Interlocked.CompareExchange(ref lane.State, Idle, Reading) != Reading) throw GivenUpOn(lane);
        _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        failure?.Throw();
        return result!;
    }

    /// <summary>Throws the set away, as after a resume: its token is cancelled now, and the next read disposes it, under
    /// the watchdog like the read, and builds a fresh one.</summary>
    public void Rebuild()
    {
        var lane = t_lane ?? throw new InvalidOperationException("Only the loop's own thread rebuilds the sensors.");
        lane.Tokens.Cancel();
        if (lane.Set is { } set)
        {
            if (lane.Thrown is { } older) DisposeQuietly(older);
            lane.Thrown = set;
            lane.Set = null;
        }
        lane.Tokens = new CancellationTokenSource();
    }

    /// <summary>The machine woke: the set, if there is one yet, reads its displays afresh at the next read.</summary>
    public void RefreshDisplay() => t_lane?.Set?.RefreshDisplay();

    /// <summary>From any thread, as the service stops: a read under way is abandoned now, without counting as a hang, and
    /// the loop goes on on a fresh thread; an idle loop is left alone.</summary>
    public void Interrupt()
    {
        if (Volatile.Read(ref _lane) is { } lane) GiveUp(lane, overran: false);
    }

    /// <summary>On the loop's thread, once it is done: disposes the set and stops the watchdog.</summary>
    public void Dispose()
    {
        _watchdog.Dispose();
        t_lane?.DisposeSets();
    }

    /// <summary>Gives the call about to be made <paramref name="allowance"/> from now. The deadline is written before the
    /// lane counts as reading, so a watchdog that sees it reading never sees an older deadline.</summary>
    private void Arm(Lane lane, TimeSpan allowance)
    {
        Volatile.Write(ref lane.Deadline, _clock.GetTimestamp() + (long)(allowance.TotalSeconds * _clock.TimestampFrequency));
        if (Interlocked.CompareExchange(ref lane.State, Reading, Idle) == GivenUp) throw GivenUpOn(lane);
        _watchdog.Change(allowance, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The set goes with the thread that was given up on, which then ends.</summary>
    private static ReadAbandonedException GivenUpOn(Lane lane)
    {
        lane.DisposeSets();
        return new ReadAbandonedException();
    }

    private void StartLane()
    {
        var lane = new Lane();
        Volatile.Write(ref _lane, lane);
        new Thread(() =>
        {
            t_lane = lane;
            try
            {
                _body!();
            }
            catch (ReadAbandonedException)
            {
                // A fresh thread has the loop; this one just ends.
            }
        })
        { IsBackground = true, Name = "PowerLedger loop" }.Start();
    }

    /// <summary>The watchdog's timer fired. It may have been armed for a read that has finished since, or fire a little
    /// early by the monotonic clock, so the call under way is timed afresh and given whatever it has left.</summary>
    private void OnWatchdog()
    {
        if (Volatile.Read(ref _lane) is not { } lane || Volatile.Read(ref lane.State) != Reading) return;
        var left = TimeSpan.FromSeconds((Volatile.Read(ref lane.Deadline) - _clock.GetTimestamp()) / (double)_clock.TimestampFrequency);
        if (left > TimeSpan.Zero)
        {
            try
            {
                _watchdog.Change(left, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // The loop has finished.
            }
            return;
        }
        GiveUp(lane, overran: true);
    }

    private void GiveUp(Lane lane, bool overran)
    {
        if (Interlocked.CompareExchange(ref lane.State, GivenUp, Reading) != Reading) return;   // the read returned first
        lane.Tokens.Cancel();
        if (overran) Interlocked.Increment(ref _abandoned);
        _onAbandoned?.Invoke(overran);
        StartLane();
    }

    private static void DisposeQuietly(ISensorSet set)
    {
        try
        {
            set.Dispose();
        }
        catch (Exception)
        {
            // A set that fails to dispose must not take the loop, or the process, down with it.
        }
    }

    /// <summary>One loop thread and the sensor set it reads.</summary>
    private sealed class Lane
    {
        public int State;
        public long Deadline;
        public ISensorSet? Set;
        public ISensorSet? Thrown;
        public CancellationTokenSource Tokens = new();

        public void DisposeSets()
        {
            Tokens.Cancel();
            if (Thrown is { } thrown) DisposeQuietly(thrown);
            if (Set is { } set) DisposeQuietly(set);
            Thrown = Set = null;
        }
    }
}
