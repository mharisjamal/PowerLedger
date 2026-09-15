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
internal sealed record LoopOptions(TimeSpan SensorTimeout, TimeSpan CalibrationSaveEvery, CalibrationOptions Calibration)
{
    public static LoopOptions Default { get; } = new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10), new CalibrationOptions());
}

/// <summary>What the loop needs from the machine and the host, so tests can supply fakes.</summary>
/// <param name="Sensors">Builds a sensor set, on the sensor thread; again after every resume or abandoned set.</param>
/// <param name="Inventory">Detects the hardware, at start and on every resume.</param>
/// <param name="SystemUptime">How long Windows has been running, to tell a boot from a service restart.</param>
/// <param name="SystemShuttingDown">True once the service control manager has announced a shutdown.</param>
/// <param name="DatabaseNotice">What start-up had to do to the database, for the status screen.</param>
internal sealed record LoopEnvironment(
    Func<ISensorSet> Sensors, Func<InventoryFacts> Inventory, Func<TimeSpan> SystemUptime, Func<bool> SystemShuttingDown, string? DatabaseNotice);

/// <summary>
/// The service's heart (spec §3, §6, §7). Once a sample interval it reads the sensors, runs the model, feeds the
/// learner, buffers the reading and pushes it to subscribers. At each minute boundary it writes the batch, folds the
/// finished minute and hour, and runs whatever housekeeping is due. Between ticks it carries out suspend, resume,
/// settings and calibration commands. Nothing else touches the sensor set, the model, the learner, the buffer, the
/// sessions or the settings, so none of them needs a lock.
/// </summary>
internal sealed class SamplingLoop : BackgroundService
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly string Version =
        typeof(SamplingLoop).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private readonly SqliteDatabase _database;
    private readonly LoopEnvironment _environment;
    private readonly LoopCommands _commands;
    private readonly LiveFeed _feed;
    private readonly StatusBoard _board;
    private readonly TimeProvider _clock;
    private readonly ILogger<SamplingLoop> _log;
    private readonly LoopOptions _options;
    private readonly RawSampleRepository _raw;
    private readonly InventoryRepository _inventory;
    private readonly SettingsStore _settingsStore;
    private readonly SessionTracker _sessions;
    private readonly CalibrationKeeper _calibration;
    private readonly Rollups _rollups;
    private readonly RetentionRunner _retention;
    private readonly WriteBuffer _buffer;
    private readonly SensorWorker _worker;
    private readonly TickClock _tickClock;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ServiceSettings _settings = ServiceSettings.Default;
    private InventoryFacts? _facts;
    private PowerModel? _model;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _minute;
    private DateTimeOffset? _foldFrom;
    private bool _suspended;
    private long _ticks;
    private long _databaseBytes;
    private int _failures;

    public SamplingLoop(
        SqliteDatabase database, LoopEnvironment environment, LoopCommands commands, LiveFeed feed, StatusBoard board,
        TimeProvider clock, ILogger<SamplingLoop> log, LoopOptions? options = null)
    {
        _database = database;
        _environment = environment;
        _commands = commands;
        _feed = feed;
        _board = board;
        _clock = clock;
        _log = log;
        _options = options ?? LoopOptions.Default;
        _raw = new RawSampleRepository(database);
        _inventory = new InventoryRepository(database);
        _settingsStore = new SettingsStore(new SettingsRepository(database));
        _sessions = new SessionTracker(new SessionRepository(database));
        _calibration = new CalibrationKeeper(new CalibrationRepository(database), _options.Calibration, _options.CalibrationSaveEvery);
        _rollups = new Rollups(_raw, new AggregateRepository(database));
        _retention = new RetentionRunner(database, clock.LocalTimeZone);
        _buffer = new WriteBuffer(_raw.InsertBatch);
        _worker = new SensorWorker(environment.Sensors, _options.SensorTimeout, clock);
        _tickClock = new TickClock(clock);
    }

    /// <summary>Readings recorded since start. Rises only once a tick is completely done.</summary>
    public long Ticks => Interlocked.Read(ref _ticks);

    private TimeSpan Interval => TimeSpan.FromSeconds(_settings.SampleIntervalSeconds);

    private double GapThreshold => EnergyIntegrator.GapThresholdFor(_settings.SampleIntervalSeconds);

    /// <summary>
    /// Completes once the loop has started and its timer runs, so every tick the clock gives from then on is seen; fails
    /// if start-up failed. .NET runs ExecuteAsync on the thread pool, so StartAsync returns before this.
    /// </summary>
    internal Task Ready => _ready.Task;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            Start();
        }
        catch (Exception error)
        {
            _log.LogCritical(error, "PowerLedger could not start");
            _ready.TrySetException(error);
            throw;
        }
        var timer = new PeriodicTimer(Interval, _clock);
        _ready.TrySetResult();
        Task<bool>? tick = null;
        Task<bool>? inbox = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                tick ??= timer.WaitForNextTickAsync(stop).AsTask();
                inbox ??= _commands.Reader.WaitToReadAsync(stop).AsTask();
                await Task.WhenAny(tick, inbox).ConfigureAwait(false);
                if (stop.IsCancellationRequested) break;

                if (inbox.IsCompleted)
                {
                    var open = await inbox.ConfigureAwait(false);
                    inbox = null;
                    if (!open) break;
                    while (_commands.Reader.TryRead(out var command))
                    {
                        if (!Handle(command)) continue;
                        timer.Dispose();                           // a new interval, or a machine that just woke
                        timer = new PeriodicTimer(Interval, _clock);
                        tick = null;
                    }
                }

                if (tick is { IsCompleted: true })
                {
                    var running = await tick.ConfigureAwait(false);
                    tick = null;
                    if (!running) break;
                    if (!_suspended) await TickAsync(stop).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            timer.Dispose();
            Stop();
        }
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
        _rollups.CatchUp(now, TimeSpan.FromHours(_settings.RawRetentionHours), GapThreshold);
        var reason = _sessions.Start(now, _environment.SystemUptime(), _raw.Latest());
        _log.LogInformation("PowerLedger {Version} started ({Reason})", Version, reason);
        Detect(now, stored);
        RefreshDatabaseSize();
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
        _settings = settings;
        _facts = facts;
        _calibration.Use(facts.Hash, now);
        _model = ModelFactory.Build(settings, facts, _calibration.Learner);
        _board.Publish(settings);
    }

    private async Task TickAsync(CancellationToken stop)
    {
        var now = _clock.GetUtcNow();
        try
        {
            var result = await _worker.ReadAsync(now, _tickClock.Measure(now, _settings.SampleIntervalSeconds), stop).ConfigureAwait(false);
            if (result is null)
            {
                _log.LogWarning("A sensor read took longer than {Timeout}; the sensor set was replaced", _options.SensorTimeout);
                return;
            }
            _tickClock.Commit(now);
            var reading = _model!.Evaluate(result.Sample);
            _calibration.Learner.Observe(result.Sample, reading.Components.Cpu, reading.Components.Gpu, reading.Components.Display);
            _buffer.Add(reading);
            var frame = Frames.From(reading, result.Sample);
            _feed.Publish(frame);
            OnMinute(now);
            var ticks = Ticks + 1;
            Publish(result, frame, ticks);
            Interlocked.Exchange(ref _ticks, ticks);
            _failures = 0;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _failures++;
            if ((_failures & (_failures - 1)) == 0) _log.LogError(error, "A tick failed ({Failures} in a row)", _failures);
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
        if (purged is not null) _log.LogInformation("Retention removed {Raw} raw rows and {Minutes} minute rows", purged.RawDeleted, purged.MinutesDeleted);
        RefreshDatabaseSize();
    }

    /// <summary>
    /// Writes the buffer, then folds every unfolded minute before <paramref name="upTo"/>. When the write fails nothing is
    /// folded, because a minute folded from half its rows would stay wrong; the next write that succeeds folds the backlog.
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
        _rollups.FoldRange(from, upTo, GapThreshold);
        _foldFrom = null;
    }

    private void Publish(TickResult result, ReadingFrame frame, long ticks) => _board.Publish(new ServiceStatus(
        Version, _startedAt, ticks, [.. result.Health.Select(Frames.From)], result.SuspectCount, _worker.Abandoned,
        _calibration.Status(), _facts?.Hash ?? "", _databaseBytes, _buffer.Problem, _environment.DatabaseNotice, frame));

    /// <returns>True when the tick timer must start over: the sample interval changed, or the machine woke, and a tick
    /// the timer queued while it slept must not fire before the rebuilt sensors have had a full interval.</returns>
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
                    break;
                case ResumeCommand:
                    Resume(now);
                    restartTimer = true;
                    break;
                case ApplySettingsCommand apply:
                    restartTimer = Apply(apply.Settings);
                    break;
                case ResetCalibrationCommand:
                    _calibration.Reset(now);
                    _log.LogInformation("Calibration reset for hardware {Hash}", _calibration.Hash);
                    break;
            }
            command.Complete();
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
        WriteAndFold(Rollups.Floor(now, Minute) + Minute);
        _calibration.Save(now);
        _sessions.End(now, SessionReason.Suspend);
        _suspended = true;
        _log.LogInformation("Suspended");
    }

    private void Resume(DateTimeOffset now)
    {
        _worker.Rebuild();
        Detect(now, _settings);
        _sessions.Resume(now);
        _tickClock.MarkResumed();
        _suspended = false;
        _log.LogInformation("Resumed");
    }

    private bool Apply(ServiceSettings settings)
    {
        if (settings.Validate() is { } problem) throw new ArgumentException(problem, nameof(settings));
        var intervalChanged = settings.SampleIntervalSeconds != _settings.SampleIntervalSeconds;
        _settingsStore.Save(settings);
        _settings = settings;
        _model = ModelFactory.Build(settings, _facts!, _calibration.Learner);
        _board.Publish(settings);
        return intervalChanged;
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
            _worker.Dispose();
            _commands.Close();
        }
    }

    private void RefreshDatabaseSize()
    {
        static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        _databaseBytes = Size(_database.Path) + Size(_database.Path + "-wal");
    }
}
