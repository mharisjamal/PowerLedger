using PowerLedger.Core;
using PowerLedger.Sensors;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// The energy each app used, hour by hour (Plan AE, research §9), worked out inside the loop's own tick on the sensor
/// thread, with no timer of its own. Each tick it takes one look at every process (<see cref="IProcessSource.Read"/>) and
/// splits the tick's processor and graphics energy, exactly as the integrator booked it into the minute and hour rows,
/// among the apps (<see cref="AppSplit"/>): the parts' resting draw to idle, drivers to Windows, busy time no running
/// process explains to closed programs, the rest by each process's cycles, and the graphics by each process's time on their
/// engines, as the GPU load counters already read it. So an hour's apps add up to its hour row's processor and graphics
/// energy, gaps left out the same way. Apps are keyed by image path, Store package or one of the special keys
/// (<see cref="AppKeys"/>): Windows' own processes and the kernel's are one app, another signed in user's are another, and a
/// process is opened for its image once, when it is new. The hour in memory is written whole with each minute's readings,
/// and an hour an earlier run wrote carries on from its rows. Never shared, never synced. Single-threaded: the loop's thread.
/// </summary>
internal sealed class AppWatch(IProcessSource processes, AppHoursRepository repository, Action<List<uint>>? foreground = null) : IDisposable
{
    private const int MostNames = 4096;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>The kernel's own processes, which have no image file: with the idle process, which is never an app, they
    /// are the ones Windows lists by these names in session 0.</summary>
    private static readonly HashSet<string> KernelProcesses = new(StringComparer.OrdinalIgnoreCase) { "System", "Registry", "Secure System", "Memory Compression" };

    private readonly List<ProcessCounts> _listed = [];
    private readonly List<Tracked> _live = [];
    private readonly Dictionary<uint, Tracked> _tracked = [];
    private readonly Dictionary<DateTimeOffset, AppHour> _hours = [];
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly List<uint> _front = [];
    private readonly RestingWatts _cpuRest = new();
    private readonly RestingWatts _gpuRest = new();
    private readonly RestingWatts _graphicsRest = new();
    private ProcessorTimes _processors;
    private bool _looked;
    private long _tick;
    private uint? _console;
    private DateTimeOffset? _hourNow;

    /// <summary>Takes the first look, which the first tick's work is measured from.</summary>
    public void Start() => Look(out _);

    /// <summary>
    /// Books one tick: <paramref name="reading"/>'s processor and graphics energy, integrated with the gap threshold the
    /// rollups fold its minute with (nought for a tick that reaches back across a sleep), and what the processes did since
    /// the last look. A gap books nothing; the look still starts the next tick afresh.
    /// </summary>
    public void Observe(Reading reading, Sample sample, double gapSeconds)
    {
        var energy = EnergyIntegrator.Integrate(reading, gapSeconds);
        var looked = Look(out var work);
        var seconds = energy.OnSeconds;
        var rest = new RestWatts(_cpuRest.Watts, _gpuRest.Watts, _graphicsRest.Watts);
        Learn(reading, sample, seconds);
        if (seconds <= 0) return;

        var hour = HourOf(reading.Timestamp);
        hour.Changed = true;
        if (!looked)
        {
            BookUnsplit(hour, energy, rest);
            return;
        }

        var (card, graphics) = Engines(sample.Graphics);
        var split = AppSplit.Of(new TickEnergy(energy.CpuWh, energy.GpuWh, seconds, sample.IGpuW), rest, work, card, graphics);
        Add(hour, AppKeys.Idle, split.IdleCpuWh, split.IdleGpuWh);
        Add(hour, AppKeys.Windows, split.KernelCpuWh, split.KernelGpuWh);
        Add(hour, AppKeys.Closed, split.ClosedCpuWh, split.ClosedGpuWh);
        foreach (var process in _live)
        {
            var app = TotalsOf(process, hour);
            app.CpuWh += process.CycleDelta * split.CpuWhPerCycle;
            app.GpuWh += process.CycleDelta * split.GpuWhPerCycle;
            app.Run(_tick, seconds);
        }
        if (sample.Graphics?.ByProcess is { } byProcess && (split.CpuWhPerGraphicsTime > 0 || split.GpuWhPerCardTime > 0))
        {
            foreach (var (pid, time) in Each(byProcess))
            {
                if (!_tracked.TryGetValue(pid, out var process)) continue;          // closed since: already counted as closed
                var app = TotalsOf(process, hour);
                app.CpuWh += (split.GraphicsByLoadEngines ? time.ProcessorLoad : time.ProcessorAny) * split.CpuWhPerGraphicsTime;
                app.GpuWh += (split.CardByLoadEngines ? time.CardLoad : time.CardAny) * split.GpuWhPerCardTime;
            }
        }
        if (foreground is not null)
        {
            foreground(_front);
            foreach (var pid in _front)
            {
                if (_tracked.TryGetValue(pid, out var process)) TotalsOf(process, hour).Front(_tick, seconds);
            }
        }
    }

    /// <summary>Writes every hour that changed since it was last written, each whole, and forgets those before the hour the
    /// ticks are in once written. Throws what storage throws; what failed is written next time.</summary>
    public void Write()
    {
        foreach (var hour in _hours.Values)
        {
            if (!hour.Changed) continue;
            repository.ReplaceHour(hour.Start, hour.Rows(Name));
            hour.Changed = false;
        }
        foreach (var (start, hour) in _hours)
        {
            if (start != _hourNow && !hour.Changed) _hours.Remove(start);
        }
    }

    public void Dispose() => processes.Dispose();

    /// <summary>
    /// One look at every process: what each did since the last look, a process new since then counting everything it did,
    /// as it all happened since; the processors' busy and drivers' time likewise. A process is identified when it is first
    /// seen, by its id and start, so an id Windows gives to a later process is a new process; one no longer listed has
    /// ended. False when Windows would not list the processes, or there was no look before this one to measure from.
    /// </summary>
    private bool Look(out ProcessorWork work)
    {
        work = default;
        if (!processes.Read(_listed, out var times)) return false;
        _tick++;
        _console = processes.ConsoleSession() ?? _console;
        _live.Clear();
        ulong cycles = 0;
        long explained = 0;
        foreach (var listed in _listed)
        {
            if (listed.Pid == 0) continue;                         // the idle process: the processors' idle time, no app
            if (!_tracked.TryGetValue(listed.Pid, out var process) || process.Created != listed.CreateTime)
            {
                process = Identify(listed);
                _tracked[listed.Pid] = process;
                process.CycleDelta = _looked ? listed.Cycles : 0;
                process.TimeDelta = _looked ? Math.Max(0, listed.Time) : 0;
            }
            else
            {
                process.CycleDelta = listed.Cycles >= process.Cycles ? listed.Cycles - process.Cycles : 0;
                process.TimeDelta = Math.Max(0, listed.Time - process.Time);
            }
            process.Cycles = listed.Cycles;
            process.Time = listed.Time;
            process.Session = listed.Session;
            process.Seen = _tick;
            _live.Add(process);
            cycles += process.CycleDelta;
            explained += process.TimeDelta;
        }
        foreach (var (pid, process) in _tracked)
        {
            if (process.Seen != _tick) _tracked.Remove(pid);
        }

        var measured = _looked;
        if (measured) work = new ProcessorWork(cycles, Math.Max(0, times.Busy - _processors.Busy), explained, Math.Max(0, times.Drivers - _processors.Drivers));
        _processors = times;
        _looked = true;
        return measured;
    }

    /// <summary>What app a new process belongs to. The kernel's processes, and any image under the Windows folder, are
    /// Windows'; a Store app is its package; anything else its image path. A process that cannot be opened goes by the name
    /// Windows lists it with.</summary>
    private Tracked Identify(ProcessCounts listed)
    {
        var process = new Tracked { Created = listed.CreateTime };
        if (listed.Pid == 4 || (listed.Session == 0 && KernelProcesses.Contains(processes.ImageName(listed.Name))))
        {
            process.Key = AppKeys.Windows;
            return process;
        }
        var image = processes.Image(listed.Pid, listed.CreateTime);
        if (image?.Path is { Length: > 0 } path)
        {
            var lower = path.ToLowerInvariant();
            if (lower.StartsWith(processes.WindowsFolder, StringComparison.Ordinal))
            {
                process.Key = AppKeys.Windows;
                return process;
            }
            process.Path = path;
            process.Key = image.PackageFamily is { Length: > 0 } family ? AppKeys.Package(family) : lower;
            return process;
        }
        if (image?.PackageFamily is { Length: > 0 } package)
        {
            process.Key = AppKeys.Package(package);
            return process;
        }
        var name = processes.ImageName(listed.Name);
        process.Key = name.Length > 0 ? name.ToLowerInvariant() : AppKeys.Other;
        return process;
    }

    /// <summary>The totals a process's figures go into this hour: its app's, or other users' while it runs in a session
    /// that is neither the services' nor the one at the screen. Kept with the process until the hour or the session at the
    /// screen changes, so a tick looks nothing up.</summary>
    private AppTotals TotalsOf(Tracked process, AppHour hour)
    {
        var other = process.Session != 0 && _console is { } console && process.Session != console;
        if (process.Totals is null || process.Hour != hour || process.Other != other)
        {
            process.Totals = hour.For(other ? AppKeys.OtherUsers : process.Key, other ? null : process.Path);
            process.Hour = hour;
            process.Other = other;
        }
        return process.Totals;
    }

    /// <summary>The time on the cards' engines and on the processor's graphics' of processes still running and of those
    /// that closed since the counters' window; nothing where nothing reads them.</summary>
    private (EngineWork? Card, EngineWork? Graphics) Engines(GraphicsWork? work)
    {
        if (work?.ByProcess is not { } byProcess) return (null, null);
        long cardLoad = 0, cardAny = 0, deadCardLoad = 0, deadCardAny = 0, load = 0, any = 0, deadLoad = 0, deadAny = 0;
        foreach (var (pid, time) in Each(byProcess))
        {
            if (_tracked.ContainsKey(pid))
            {
                cardLoad += time.CardLoad;
                cardAny += time.CardAny;
                load += time.ProcessorLoad;
                any += time.ProcessorAny;
            }
            else
            {
                deadCardLoad += time.CardLoad;
                deadCardAny += time.CardAny;
                deadLoad += time.ProcessorLoad;
                deadAny += time.ProcessorAny;
            }
        }
        return (new EngineWork(cardLoad, deadCardLoad, cardAny, deadCardAny), new EngineWork(load, deadLoad, any, deadAny));
    }

    /// <summary>The parts' resting draw, learned from the ticks the machine did nothing: the processor and every graphics
    /// under <see cref="RestingWatts.QuietLoad"/>, a load nobody read counting as none.</summary>
    private void Learn(Reading reading, Sample sample, double seconds)
    {
        if (seconds <= 0) return;
        var atRest = Quiet(sample.CpuLoad) && Quiet(sample.DGpuLoad) && Quiet(sample.DGpuBusyLoad);
        _cpuRest.Observe(reading.Timestamp, reading.Components.Cpu, seconds, atRest);
        _gpuRest.Observe(reading.Timestamp, reading.Components.Gpu, seconds, atRest);
        if (sample.IGpuW is { } graphics) _graphicsRest.Observe(reading.Timestamp, graphics, seconds, atRest);

        static bool Quiet(double? load) => load is not { } l || (double.IsFinite(l) && l < RestingWatts.QuietLoad);
    }

    /// <summary>A tick with no look to split it by: its resting part to idle, and the rest to Other apps, since some app
    /// drew it.</summary>
    private static void BookUnsplit(AppHour hour, EnergySlice energy, RestWatts rest)
    {
        var hours = energy.OnSeconds / 3600;
        var idleCpu = Math.Min(Math.Max(0, energy.CpuWh), rest.Cpu * hours);
        var idleGpu = Math.Min(Math.Max(0, energy.GpuWh), rest.Gpu * hours);
        Add(hour, AppKeys.Idle, idleCpu, idleGpu);
        Add(hour, AppKeys.Other, Math.Max(0, energy.CpuWh) - idleCpu, Math.Max(0, energy.GpuWh) - idleGpu);
    }

    private static void Add(AppHour hour, string key, double cpuWh, double gpuWh)
    {
        if (cpuWh <= 0 && gpuWh <= 0) return;
        var app = hour.For(key, null);
        app.CpuWh += cpuWh;
        app.GpuWh += gpuWh;
    }

    /// <summary>The hour a tick counts in, by its timestamp as the hour rows have it, read back from storage the first time
    /// this run meets it, so a restart inside the hour, or a clock set back, adds to what was written.</summary>
    private AppHour HourOf(DateTimeOffset timestamp)
    {
        var start = Rollups.Floor(timestamp, Hour);
        _hourNow = start;
        if (!_hours.TryGetValue(start, out var hour))
        {
            hour = new AppHour(start, repository.ReadHour(start));
            _hours[start] = hour;
        }
        return hour;
    }

    /// <summary>An app's name for its row: its image's description where it has an image, a Store app's package name, or
    /// what it is keyed by. Asked only for the rows written, once an image.</summary>
    private string Name(AppTotals app)
    {
        if (app.Path is { } path)
        {
            if (_names.TryGetValue(path, out var name)) return name;
            name = processes.DisplayName(path);
            if (_names.Count >= MostNames) _names.Clear();
            _names[path] = name;
            return name;
        }
        if (app.Key.StartsWith(AppKeys.PackagePrefix, StringComparison.Ordinal))
        {
            var family = app.Key[AppKeys.PackagePrefix.Length..];
            var publisher = family.LastIndexOf('_');
            return publisher > 0 ? family[..publisher] : family;
        }
        return app.Key;
    }

    /// <summary>The pairs as a dictionary, whose enumerator boxes nothing: the load counters give one already.</summary>
    private static Dictionary<uint, GraphicsTime> Each(IReadOnlyDictionary<uint, GraphicsTime> byProcess)
        => byProcess as Dictionary<uint, GraphicsTime> ?? new Dictionary<uint, GraphicsTime>(byProcess);

    /// <summary>A process the loop has seen, from its first look to the look it is no longer in.</summary>
    private sealed class Tracked
    {
        public long Created;
        public uint Session;
        public string Key = AppKeys.Other;
        public string? Path;
        public ulong Cycles;
        public long Time;
        public long Seen;
        public ulong CycleDelta;
        public long TimeDelta;
        public AppHour? Hour;
        public bool Other;
        public AppTotals? Totals;
    }
}
