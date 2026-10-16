using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <param name="SensorTimeout">A sensor read that takes longer abandons the set (Plan B's watchdog rule).</param>
/// <param name="CalibrationSaveEvery">How often the learner is saved while the service runs.</param>
/// <param name="Calibration">Learner thresholds; the defaults are spec §5's.</param>
/// <param name="SensorBuildTimeout">Building a sensor set that takes longer abandons it. The first build after start loads
/// every vendor's library and asks WMI, which takes a couple of seconds on an idle machine and far longer on a busy one.</param>
internal sealed record LoopOptions(TimeSpan SensorTimeout, TimeSpan CalibrationSaveEvery, CalibrationOptions Calibration, TimeSpan SensorBuildTimeout)
{
    public static LoopOptions Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10), new CalibrationOptions(), TimeSpan.FromMinutes(1));
}

/// <summary>What the loop needs from the machine and the host, so tests can supply fakes.</summary>
/// <param name="Sensors">Builds a sensor set, on the sensor thread; again after every resume or abandoned set. The token
/// is cancelled once the set is abandoned or thrown away (see <see cref="SensorWorker"/>).</param>
/// <param name="Inventory">Detects the hardware, at start and once graphics adapters or batteries have come or gone.</param>
/// <param name="SystemUptime">How long Windows has been running, to tell a boot from a service restart.</param>
/// <param name="SystemShuttingDown">True once the service control manager has announced a shutdown.</param>
/// <param name="DatabaseNotice">What start-up had to do to the database, for the status screen.</param>
/// <param name="Watched">Whether anyone may be looking at the live figures (<see cref="ServiceSignals.Watched"/>), asked
/// whenever the loop picks its interval; always, when left out.</param>
/// <param name="Battery">The laptop battery's charge, read as the machine goes to sleep and as it wakes (see
/// <see cref="SleepKeeper"/>); null when there is none or it reports no watt-hours. Left out, nothing is learned.</param>
/// <param name="SignedIn">Whether anyone is signed in to Windows, asked as the machine goes to sleep: a Fast Startup
/// shutdown signs everyone out first (see <see cref="SleepKinds"/>). Left out, someone always is.</param>
/// <param name="Hibernated">When the machine hibernated since the time given, asked as it wakes; null when it slept. Left
/// out, it never did.</param>
/// <param name="Processes">What Windows says of the processes, for the energy by app (Plan AE, <see cref="AppWatch"/>),
/// opened on the sensor thread at start. Left out, nothing is worked out by app.</param>
/// <param name="Foreground">Fills a list with the process in front in each session whose App reported it lately (see
/// <see cref="ServiceSignals.ForegroundProcesses"/>), asked once a tick. Left out, no app is ever in front.</param>
/// <param name="Engines">Windows' GPU Engine counters for the energy by app, opened at start and read with its looks where
/// the sensors do not read them. Left out, a card's watts then follow the processor's split.</param>
internal sealed record LoopEnvironment(
    Func<CancellationToken, ISensorSet> Sensors, Func<InventoryFacts> Inventory, Func<TimeSpan> SystemUptime, Func<bool> SystemShuttingDown, string? DatabaseNotice,
    Func<bool>? Watched = null, Func<BatteryCharge?>? Battery = null, Func<bool>? SignedIn = null, Func<DateTimeOffset, DateTimeOffset?>? Hibernated = null,
    Func<IProcessSource>? Processes = null, Action<List<uint>>? Foreground = null, Func<IEngineCounters>? Engines = null);

/// <summary>
/// The service's heart (spec §3, §6, §7). Once a sample interval it reads the sensors, runs the model, feeds the
/// learner, buffers the reading and pushes it to subscribers. At each minute boundary it writes the batch, folds the
/// finished minute and hour, and runs whatever housekeeping is due. Between ticks it carries out suspend, resume,
/// settings and calibration commands. It all runs on one dedicated thread, the sensor worker's (perf review §7), which
/// sleeps in a single wait for its beat, a command or the stop, so a tick wakes one thread and no thread pool. Nothing else
/// touches the sensor set, the model, the learner, the buffer, the sessions or the settings, so none of them needs a lock;
/// when the watchdog gives up on a stuck read, a fresh thread takes the loop over and the stuck one never touches them
/// again. The monitor board is shared: the sensor thread writes what it detects and the pipe what the App reports, so the
/// board locks for itself.
/// </summary>
internal sealed class SamplingLoop : BackgroundService
{
    /// <summary>
    /// How often the loop reads while nobody looks at the live figures (perf review §4, research §3), unless the set interval
    /// is slower still. Energy stays exact at any interval, since the meters add energy up over the tick; only the live
    /// view and the raw rows grow coarser, and nobody is looking at the one.
    /// </summary>
    public static readonly TimeSpan UnwatchedInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long after a resume the sensor set is rebuilt (perf review §12). A machine in Modern Standby wakes
    /// briefly many times an hour; one that sleeps again within the minute never pays for a rebuild.</summary>
    public static readonly TimeSpan RebuildAfterResume = TimeSpan.FromMinutes(1);

    /// <summary>How long after the last device came or went the hardware is detected again: a dock brings several.</summary>
    public static readonly TimeSpan DevicesSettle = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly string Version =
        typeof(SamplingLoop).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private readonly SqliteDatabase _database;
    private readonly LoopEnvironment _environment;
    private readonly LoopCommands _commands;
    private readonly LiveFeed _feed;
    private readonly StatusBoard _board;
    private readonly MonitorBoard _monitors;
    private readonly TimeProvider _clock;
    private readonly ILogger<SamplingLoop> _log;
    private readonly LoopOptions _options;
    private readonly RawSampleRepository _raw;
    private readonly InventoryRepository _inventory;
    private readonly SettingsStore _settingsStore;
    private readonly NutSecret _nutSecret;
    private readonly SessionTracker _sessions;
    private readonly CalibrationKeeper _calibration;
    private readonly SleepKeeper _sleep;
    private readonly Rollups _rollups;
    private readonly RetentionRunner _retention;
    private readonly WriteBuffer _buffer;
    private readonly SensorWorker _worker;
    private readonly TickClock _tickClock;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ServiceSettings _settings = ServiceSettings.Default;
    private CancellationToken _stop;
    private IMetronome? _metronome;
    private bool _started;
    private InventoryFacts? _facts;
    private PowerModel? _model;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _minute;
    private DateTimeOffset? _foldFrom;
    private DateTimeOffset? _rebuildAt;
    private DateTimeOffset? _devicesChangedAt;
    private TimeSpan _interval;
    private bool _suspended;
    private bool _carryOverMonitors;
    private long _ticks;
    private long _databaseBytes;
    private int _failures;
    private AppWatch? _apps;
    private int _appFailures;
    private int _appWriteFailures;
    private DateTimeOffset? _lastWake;

    public SamplingLoop(
        SqliteDatabase database, LoopEnvironment environment, LoopCommands commands, LiveFeed feed, StatusBoard board,
        MonitorBoard monitors, TimeProvider clock, ILogger<SamplingLoop> log, LoopOptions? options = null)
    {
        _database = database;
        _environment = environment;
        _commands = commands;
        _feed = feed;
        _board = board;
        _monitors = monitors;
        _clock = clock;
        _log = log;
        _options = options ?? LoopOptions.Default;
        _raw = new RawSampleRepository(database);
        _inventory = new InventoryRepository(database);
        _settingsStore = new SettingsStore(new SettingsRepository(database));
        _nutSecret = new NutSecret(new SettingsRepository(database));
        _sessions = new SessionTracker(new SessionRepository(database));
        _calibration = new CalibrationKeeper(new CalibrationRepository(database), _options.Calibration, _options.CalibrationSaveEvery);
        _sleep = new SleepKeeper(new SettingsRepository(database), environment.Battery, log, environment.SignedIn, environment.Hibernated);
        _rollups = new Rollups(_raw, new AggregateRepository(database), new SessionRepository(database));
        _retention = new RetentionRunner(database, clock.LocalTimeZone);
        _buffer = new WriteBuffer(_raw.InsertBatch);
        _worker = new SensorWorker(environment.Sensors, _options.SensorTimeout, _options.SensorBuildTimeout, clock);
        _tickClock = new TickClock(clock);
    }

    /// <summary>Readings recorded since start. Rises only once a tick is completely done.</summary>
    public long Ticks => Interlocked.Read(ref _ticks);

    private double GapThreshold => GapThresholdFor(_settings);

    /// <summary>The gap threshold for readings taken under <paramref name="settings"/>, for the slowest interval the loop
    /// reads at, the set one or <see cref="UnwatchedInterval"/>: a minute (accuracy A17), so neither a stall nor a beat
    /// Windows put off counts as sleep. A tick the machine woke within is a gap whatever its length (see <see cref="Wakes"/>).</summary>
    internal static double GapThresholdFor(ServiceSettings settings)
        => EnergyIntegrator.GapThresholdFor(Math.Max(settings.SampleIntervalSeconds, UnwatchedInterval.TotalSeconds));

    /// <summary>The interval the loop wants now: the set one while anyone may be looking, otherwise
    /// <see cref="UnwatchedInterval"/> when that is slower.</summary>
    private TimeSpan WantedInterval()
    {
        var set = TimeSpan.FromSeconds(_settings.SampleIntervalSeconds);
        return _environment.Watched?.Invoke() != false || set >= UnwatchedInterval ? set : UnwatchedInterval;
    }

    /// <summary>
    /// Completes once the loop has started and its beat runs, so every tick the clock gives from then on is seen; fails
    /// if start-up failed. Start-up runs on the loop's own thread, so StartAsync returns before this.
    /// </summary>
    internal Task Ready => _ready.Task;

    /// <summary>Starts the loop's thread; the task ends when the loop has stopped and written everything.</summary>
    protected override Task ExecuteAsync(CancellationToken stop)
    {
        _stop = stop;
        _worker.Start(Run, OnAbandoned);
        stop.Register(_worker.Interrupt);                         // a read stuck as the service stops holds nothing up
        return _finished.Task;
    }

    /// <summary>
    /// The loop, on the sensor worker's thread: started once, then waiting in one wait for the stop, a command or the beat,
    /// in that order of precedence, so a command that arrives with a tick is carried out first. A thread that takes the loop
    /// over from one the watchdog gave up on picks it up where it stands.
    /// </summary>
    private void Run()
    {
        try
        {
            if (!_started)
            {
                try
                {
                    Start();
                }
                catch (Exception error)
                {
                    _log.LogCritical(error, "PowerLedger could not start");
                    _ready.TrySetException(error);
                    _finished.TrySetException(error);
                    _commands.Close();
                    return;
                }
                _metronome = Metronome.For(_clock);
                _interval = WantedInterval();
                _metronome.Start(_interval);
                _started = true;
                _ready.TrySetResult();
            }

            WaitHandle[] wakes = [_stop.WaitHandle, _commands.Arrived, _metronome!.Beat];
            while (WaitHandle.WaitAny(wakes) is var woken && woken != 0 && !_stop.IsCancellationRequested)
            {
                if (woken == 1) Carry();
                else if (!_suspended) Tick();
            }
        }
        catch (Exception error) when (error is not ReadAbandonedException)
        {
            _log.LogCritical(error, "The sampling loop failed");
            Finish(error);
            return;
        }
        Finish(null);
    }

    /// <summary>Carries out every command waiting, each completed only once its new beat runs (see <see cref="Handle"/>).</summary>
    private void Carry()
    {
        while (_commands.Reader.TryRead(out var command))
        {
            Pace(restart: Handle(command));
            command.Complete();                                    // only now (see Handle); one that failed stays failed
        }
    }

    /// <summary>Starts the beat over at the interval wanted now, when that changed or <paramref name="restart"/> says so;
    /// stops it while the machine sleeps.</summary>
    private void Pace(bool restart)
    {
        if (_suspended)
        {
            if (restart) _metronome!.Stop();
            return;
        }
        var wanted = WantedInterval();
        if (!restart && wanted == _interval) return;
        _interval = wanted;
        _metronome!.Start(wanted);
    }

    /// <summary>The watchdog gave up on a read; a fresh thread has the loop.</summary>
    private void OnAbandoned(bool overran)
    {
        if (overran) _log.LogWarning("A sensor read took longer than {Timeout}; the sensor set was replaced", _options.SensorTimeout);
    }

    /// <summary>Writes what is left and ends the loop: the stop is done once the readings are, and the sensors are let go
    /// last, so a set slow to close holds nothing up.</summary>
    private void Finish(Exception? failure)
    {
        Stop();
        if (failure is null) _finished.TrySetResult();
        else _finished.TrySetException(failure);
        _worker.Dispose();
        _metronome?.Dispose();
        _apps?.Dispose();
    }

    private void Start()
    {
        var now = _clock.GetUtcNow();
        _startedAt = now;
        _minute = Rollups.Floor(now, Minute);
        var stored = _settingsStore.Load();
        if (stored?.Validate() is { } problem)
        {
            _log.LogWarning("Stored settings were refused ({Problem}); starting from the defaults", problem);
            stored = null;
        }
        _settings = stored ?? ServiceSettings.Default;
        _sleep.Load();
        _rollups.CatchUp(now, TimeSpan.FromHours(_settings.RawRetentionHours), GapThreshold);
        var reason = _sessions.Start(now, _environment.SystemUptime(), _raw.Latest());
        _log.LogInformation("PowerLedger {Version} started ({Reason})", Version, reason);
        Detect(now, stored);
        // Settings from before monitors were detected wait for a tick that finds one; a first run has none to carry over.
        _carryOverMonitors = stored is not null && ProfilePolicy.HasMonitorsToCarryOver(_settings.Profile);
        RefreshDatabaseSize();
        StartApps();
    }

    /// <summary>Opens the processes and the GPU engine counters for the energy by app (Plan AE) and takes the first look.
    /// Should Windows refuse the processes, the service runs on without it; should it refuse the counters, without them.</summary>
    private void StartApps()
    {
        if (_environment.Processes is not { } open) return;
        IEngineCounters? engines = null;
        try
        {
            engines = _environment.Engines?.Invoke();
        }
        catch (Exception error)
        {
            _log.LogWarning(error, "The GPU engine counters cannot be read for the energy by app this run");
        }
        IProcessSource? processes = null;
        try
        {
            processes = open();
            var apps = new AppWatch(processes, new AppHoursRepository(_database), _environment.Foreground, engines: engines);
            apps.Start();
            _apps = apps;
        }
        catch (Exception error)
        {
            processes?.Dispose();
            _log.LogWarning(error, "The energy by app cannot be worked out this run");
        }
    }

    /// <summary>
    /// Books the tick's processor and graphics energy to the apps, with the gap threshold the rollups fold its minute with: a
    /// tick the machine woke within is a gap whatever its length (<see cref="Wakes"/>), so an hour's apps add up to its hour
    /// row. A failure here costs the apps that tick and never the reading, which is buffered already.
    /// </summary>
    private void ObserveApps(Reading reading, Sample sample, bool spansSleep)
    {
        if (_apps is null) return;
        try
        {
            var asleep = spansSleep || (_lastWake is { } woke && Wakes.Within(woke, reading));
            _apps.Observe(reading, sample, asleep ? 0 : GapThreshold);
            _appFailures = 0;
        }
        catch (Exception error)
        {
            _appFailures++;
            if ((_appFailures & (_appFailures - 1)) == 0) _log.LogWarning(error, "Sorting a tick's energy by app failed ({Failures} in a row)", _appFailures);
        }
    }

    /// <summary>Writes the hours of energy by app with the readings they were worked out from; what fails is written with
    /// the next minute's. A write failing minute after minute is logged at the 1st, 2nd, 4th, 8th failure and so on, as
    /// a failing tick is (review item 14).</summary>
    private void WriteApps()
    {
        if (_apps is null) return;
        try
        {
            _apps.Write();
            _appWriteFailures = 0;
        }
        catch (Exception error)
        {
            _appWriteFailures++;
            if ((_appWriteFailures & (_appWriteFailures - 1)) == 0)
                _log.LogWarning(error, "Writing the energy by app failed ({Failures} in a row)", _appWriteFailures);
        }
    }

    /// <summary>Detects the hardware, records it, and folds it into the profile when this is a different machine.</summary>
    private void Detect(DateTimeOffset now, ServiceSettings? stored)
    {
        var facts = _environment.Inventory();
        _inventory.Upsert(new InventoryRecord(facts.Hash, now, facts.ToJson()));
        var (settings, changed) = ProfilePolicy.Apply(stored, _settingsStore.ProfileHash(), facts);
        if (changed)
        {
            _settingsStore.Save(settings);
            _settingsStore.SaveProfileHash(facts.Hash);
            _log.LogInformation("Machine profile taken from detection for hardware {Hash}", facts.Hash);
        }
        _facts = facts;
        _board.Publish(facts);
        _calibration.Use(facts.Hash, now);
        Use(settings);
    }

    /// <summary>Puts settings already saved in force: the monitor board takes the user's choices and the profile's defaults,
    /// the model is built afresh, and the pipe answers with them.</summary>
    private void Use(ServiceSettings settings)
    {
        _settings = settings;
        _monitors.Choose(settings.Profile);
        _model = ModelFactory.Build(settings, _facts!, _calibration.Learner, _monitors);
        _board.Publish(settings);
    }

    private void Tick()
    {
        var now = _clock.GetUtcNow();
        try
        {
            CatchUpWithTheHardware(now);
            var spansSleep = _tickClock.SpansSleep;
            var result = _worker.Read(now, _tickClock.Measure(now, _interval.TotalSeconds));
            _tickClock.Commit(now);
            var reading = _model!.Evaluate(result.Sample, out var monitorWatts);
            reading = reading with { Measured = Frames.Measured(result.Sample, reading.TotalSource) };   // stored for data sharing
            // The tick that reaches back across a sleep carries what the machine drew asleep, booked over the sleep (A10).
            if (spansSleep) reading = reading with { AsleepW = _sleep.Watts(_settings.Profile.Chassis == ChassisKind.Laptop, reading.OnBattery) };
            var parts = reading.Components;
            // The battery delivers what the monitors running off the laptop draw, so they are taken out with the display, or
            // the learner would learn them into the rest of the laptop. The figure is the one the reading used. The first
            // tick after a sleep is not learned from: the meters' figures are averages over the sleep, the battery's is now.
            // Nor is a tick with the lid shut, whose panel draws nothing.
            if (!spansSleep && !result.Sample.PanelInactive) _calibration.Learner.Observe(result.Sample, parts.Cpu, parts.Gpu, parts.Display + monitorWatts.FromPc);
            _buffer.Add(reading);
            ObserveApps(reading, result.Sample, spansSleep);
            // What the time awake since a wake drew from the battery, until the battery is read again to learn the sleep.
            _sleep.Awake(now, reading.OnBattery, spansSleep ? 0 : EnergyIntegrator.Integrate(reading, GapThreshold).BatteryWh);
            var frame = Frames.From(reading, result.Sample);
            _feed.Publish(frame);
            OnMinute(now);
            var ticks = Ticks + 1;
            var monitors = _monitors.Status(result.Sample.DisplayOn);
            Publish(result, frame, ticks, monitors);
            if (_carryOverMonitors && monitors.Count > 0) CarryOverMonitors(monitors);
            Interlocked.Exchange(ref _ticks, ticks);
            _failures = 0;
        }
        catch (Exception error) when (error is not ReadAbandonedException)
        {
            _failures++;
            if ((_failures & (_failures - 1)) == 0) _log.LogError(error, "A tick failed ({Failures} in a row)", _failures);
        }
    }

    /// <summary>
    /// Before a read: once devices have settled, the hardware is detected again and the sensor set rebuilt for it; a minute
    /// after a resume, the set is rebuilt, since across a sleep a card's library can lose its card and a counter reset.
    /// </summary>
    private void CatchUpWithTheHardware(DateTimeOffset now)
    {
        if (_devicesChangedAt is { } changed && now - changed >= DevicesSettle)
        {
            _devicesChangedAt = null;
            Detect(now, _settings);
            _worker.Rebuild();
            _log.LogInformation("Hardware detected again after devices came or went");
        }
        if (_rebuildAt is { } due && now >= due)
        {
            _rebuildAt = null;
            _worker.Rebuild();
        }
    }

    private void OnMinute(DateTimeOffset now)
    {
        var minute = Rollups.Floor(now, Minute);
        if (minute == _minute) return;
        WriteAndFold(minute > _minute ? minute : _minute + Minute);   // a clock set back still folds the minute it left
        _minute = minute;
        _calibration.SaveIfDue(now);
        var purged = _retention.RunIfDue(now, RetentionOptions.Clamped(_settings.RawRetentionHours, _settings.HistoryRetentionYears));
        if (purged is not null)
            _log.LogInformation("Retention removed {Raw} raw rows, {Minutes} minute rows and {Apps} app rows", purged.RawDeleted, purged.MinutesDeleted, purged.AppHoursDeleted);
        RefreshDatabaseSize();
    }

    /// <summary>
    /// Writes the buffer and the energy by app worked out from it, then folds every unfolded minute before
    /// <paramref name="upTo"/>. When the write fails nothing is folded, because a minute folded from half its rows would stay
    /// wrong, and the apps wait too; the next write that succeeds folds the backlog and writes them.
    /// </summary>
    private void WriteAndFold(DateTimeOffset upTo)
    {
        var from = _foldFrom ?? _minute;
        if (!_buffer.Flush())
        {
            _foldFrom = from;
            _log.LogWarning("Writing readings failed: {Problem}", _buffer.Problem);
            return;
        }
        WriteApps();
        _rollups.FoldRange(from, upTo, GapThreshold);
        _foldFrom = null;
    }

    private void Publish(TickResult result, ReadingFrame frame, long ticks, IReadOnlyList<MonitorStatus> monitors)
    {
        _board.PublishDiscreteGpu(result.Sample.DGpuPresent);
        _board.Publish(new ServiceStatus(
            Version, _startedAt, ticks, [.. result.Health.Select(Frames.From)], result.SuspectCount, _worker.Abandoned,
            _calibration.Status(), _facts?.Hash ?? "", _databaseBytes, _buffer.Problem, _environment.DatabaseNotice, frame, monitors,
            Frames.PowerDevices(result.Sample)));
    }

    /// <summary>
    /// Carries the monitor settings from before monitors were detected over, and saves them (Plan J): whether a monitor the
    /// user hasn't chosen for counts, and a figure the user typed, which the monitors now attached are given. Tried at the
    /// first tick that finds a monitor, once a run: settings that could not be saved still hold the old count, so the next
    /// start tries again.
    /// </summary>
    private void CarryOverMonitors(IReadOnlyList<MonitorStatus> attached)
    {
        _carryOverMonitors = false;
        if (!ProfilePolicy.HasMonitorsToCarryOver(_settings.Profile)) return;         // settings applied since start chose already
        var settings = _settings with { Profile = ProfilePolicy.CarryOverMonitors(_settings.Profile, attached.Select(monitor => monitor.Key)) };
        if (settings.Validate() is { } problem)
        {
            _log.LogWarning("The old monitor settings could not be carried over ({Problem})", problem);
            return;
        }
        try
        {
            _settingsStore.Save(settings);
        }
        catch (Exception error)
        {
            _log.LogWarning(error, "The old monitor settings could not be saved; the next start will try again");
            return;
        }
        Use(settings);
        _log.LogInformation(
            "The old monitor settings were carried over: monitors {Counting} unless the user chooses otherwise, and {Typed} of the {Count} detected monitors take the figure typed for them",
            settings.Profile.CountMonitorsByDefault ? "count" : "are left out", settings.Profile.Monitors.Count, attached.Count);
    }

    /// <summary>
    /// Carries out a command, or fails it. The loop completes one that succeeds only after starting its beat over when
    /// asked to, so that, as with <see cref="Ready"/>, every tick the clock gives once the command is done is seen: a tick
    /// given before the new beat existed would be lost, and a test moves its fake clock the moment it is told.
    /// </summary>
    /// <returns>True when the beat must start over, or stop, whatever the interval: the machine is going to sleep, or it
    /// woke, and a tick queued while it slept must not fire before a full interval has passed since. A new interval, from
    /// settings or from someone starting or stopping looking, is picked after every command.</returns>
    private bool Handle(LoopCommand command)
    {
        var now = _clock.GetUtcNow();
        try
        {
            var restartTimer = false;
            switch (command)
            {
                case SuspendCommand:
                    Suspend(now);
                    restartTimer = true;
                    break;
                case ResumeCommand:
                    Resume(now);
                    restartTimer = true;
                    break;
                case ApplySettingsCommand apply:
                    Apply(apply.Settings);
                    break;
                case WatchersChangedCommand:
                    break;                                         // the interval is picked again after every command
                case DevicesChangedCommand:
                    _devicesChangedAt = now;
                    break;
                case ResetCalibrationCommand:
                    _calibration.Reset(now);
                    _log.LogInformation("Calibration reset for hardware {Hash}", _calibration.Hash);
                    break;
            }
            return restartTimer;
        }
        catch (Exception error)
        {
            _log.LogError(error, "{Command} failed", command.GetType().Name);
            command.Fail(error);
            return false;
        }
    }

    private void Suspend(DateTimeOffset now)
    {
        if (_suspended) return;
        _sleep.Suspending(now);                              // first, as close to the sleep as can be
        WriteAndFold(Rollups.Floor(now, Minute) + Minute);
        _calibration.Save(now);
        _sessions.End(now, SessionReason.Suspend);
        _suspended = true;
        _log.LogInformation("Suspended");
    }

    /// <summary>
    /// The machine woke (perf review §12). The hardware is the same, since a dock that changed while it slept is told
    /// through device notifications, and the sensor set goes on; only the display, whose brightness and monitors a sleep
    /// often changes, is read afresh, and the set is rebuilt a minute on, unless the machine sleeps again first.
    /// </summary>
    private void Resume(DateTimeOffset now)
    {
        if (_suspended) _sleep.Resumed(now);                 // first, as close to the wake as can be
        _worker.RefreshDisplay();
        _rebuildAt = now + RebuildAfterResume;
        _sessions.Resume(now);
        _lastWake = now;                                     // the session's start, which the rollups take the wake from
        _tickClock.MarkResumed();
        _suspended = false;
        _log.LogInformation("Resumed");
    }

    /// <summary>Puts new settings in force; a new interval takes effect when the loop picks its interval after the command.</summary>
    private void Apply(ServiceSettings settings)
    {
        if (settings.Validate() is { } problem) throw new ArgumentException(problem, nameof(settings));
        settings = _nutSecret.Take(settings);                // the UPS server's password is kept apart, never stored or sent
        _settingsStore.Save(settings);
        Use(settings);
    }

    private void Stop()
    {
        var now = _clock.GetUtcNow();
        try
        {
            if (!_suspended) WriteAndFold(Rollups.Floor(now, Minute) + Minute);
            _calibration.Save(now);
            _sessions.End(now, _environment.SystemShuttingDown() ? SessionReason.Shutdown : SessionReason.ServiceStop);
            _log.LogInformation("PowerLedger stopped after {Ticks} readings", Ticks);
        }
        catch (Exception error)
        {
            _log.LogError(error, "Could not finish writing while stopping");
        }
        finally
        {
            _commands.Close();
        }
    }

    private void RefreshDatabaseSize()
    {
        static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        _databaseBytes = Size(_database.Path) + Size(_database.Path + "-wal");
    }
}
