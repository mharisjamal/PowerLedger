using PowerLedger.Core;
using PowerLedger.Sensors;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// The energy each app used, hour by hour (Plan AE, research §9), worked out inside the loop's own tick on the sensor
/// thread, with no timer of its own. A look at every process (<see cref="IProcessSource.Read"/>) gives what each did since
/// the last look, and the ticks' processor and graphics energy over that time, exactly as the integrator booked it into
/// the minute and hour rows, is split among the apps (<see cref="AppSplit"/>): the parts' resting draw to idle, tick by
/// tick; drivers to Windows, busy time no running process explains to closed programs, the rest by each process's cycles;
/// the graphics by each process's time on their engines, as the GPU load counters read it, by the sensors or, where they do
/// not read them, here. So an hour's apps add up to its hour row's processor and graphics energy, gaps left out the same way.
/// A look walks every thread on the machine, about 2 ms of the processor's time at 300 processes, so it is taken at most
/// every <see cref="LookEvery"/>: every tick at the loop's unwatched pace, and every fifth while someone watches. The ticks in
/// between are pooled and split by the look that ends them, over the same time; a gap, a write and a stop take a look at
/// once. Apps are keyed by image path, Store package or one of the special keys (<see cref="AppKeys"/>), and tagged by the
/// account that runs them (<see cref="AppUsers"/>), which the App reads to pool other people's: Windows' own processes and
/// the kernel's are one app, the machine's, and a process is opened for its image and account once, when it is new. The
/// hour in memory is written whole with each minute's readings, and an hour an earlier run wrote
/// carries on from its rows. Never shared, never synced. Single-threaded: the loop's thread.
/// </summary>
/// <param name="lookEvery">How long the ticks pool at most before a look; <see cref="LookEvery"/> when left out, nought for
/// a look every tick.</param>
/// <param name="engines">Windows' GPU Engine counters, read with the looks, at most every <see cref="LookEvery"/>, on a PC
/// whose sensors do not read them already (one whose every card NVIDIA's library reads), so a card's watts go by each
/// process's time on its engines there too; the processor's split is followed only when they cannot be read.</param>
internal sealed class AppWatch(
    IProcessSource processes, AppHoursRepository repository, Action<List<uint>>? foreground = null, TimeSpan? lookEvery = null, IEngineCounters? engines = null)
    : IDisposable
{
    /// <summary>The least time between two looks, the loop's unwatched pace.</summary>
    public static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(5);

    private const int MostNames = 4096;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    /// <summary>The kernel's own processes, which have no image file: with the idle process, which is never an app, they
    /// are the ones Windows lists by these names in session 0.</summary>
    private static readonly HashSet<string> KernelProcesses = new(StringComparer.OrdinalIgnoreCase) { "System", "Registry", "Secure System", "Memory Compression" };

    private readonly double _lookEverySeconds = (lookEvery ?? LookEvery).TotalSeconds;
    private readonly List<ProcessCounts> _listed = [];
    private readonly List<Tracked> _live = [];
    private readonly Dictionary<uint, Tracked> _tracked = [];
    private readonly Dictionary<DateTimeOffset, AppHour> _hours = [];
    private readonly List<Pool> _pools = [];
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly List<uint> _front = [];
    private readonly RestingWatts _cpuRest = new();
    private readonly RestingWatts _gpuRest = new();
    private readonly RestingWatts _graphicsRest = new();
    private ProcessorTimes _processors;
    private GraphicsWork? _graphics;
    private GraphicsWork? _engineWork;
    private DateTimeOffset? _enginesReadAt;
    private DateTimeOffset? _tickAt;
    private bool _looked;
    private long _looks;
    private long _ticks;
    private double _pooledSeconds;
    private DateTimeOffset? _hourNow;

    /// <summary>Looks taken, for the tests and the cost check.</summary>
    public long Looks => _looks;

    /// <summary>Takes the first look, which the first tick's work is measured from, and the engine counters' first read.</summary>
    public void Start()
    {
        Look(out _);
        ReadEngines();
    }

    /// <summary>
    /// Books one tick: <paramref name="reading"/>'s processor and graphics energy, integrated with the gap threshold the
    /// rollups fold its minute with (nought for a tick that reaches back across a sleep). Its resting part goes to idle at
    /// once and its time in front to the app in front; the rest waits for the look that ends the pool. A gap books nothing
    /// and takes a look at once, so the next tick's work starts afresh.
    /// </summary>
    public void Observe(Reading reading, Sample sample, double gapSeconds)
    {
        var energy = EnergyIntegrator.Integrate(reading, gapSeconds);
        var seconds = energy.OnSeconds;
        var rest = new RestWatts(_cpuRest.Watts, _gpuRest.Watts, _graphicsRest.Watts);
        Learn(reading, sample, seconds);
        _graphics = sample.Graphics;
        _tickAt = reading.Timestamp;
        _ticks++;
        if (seconds > 0)
        {
            var hour = HourOf(reading.Timestamp);
            hour.Changed = true;
            var hours = seconds / 3600;
            var cpuWh = Positive(energy.CpuWh);
            var gpuWh = Positive(energy.GpuWh);
            var idleCpu = Math.Min(cpuWh, rest.Cpu * hours);
            var idleGpu = Math.Min(gpuWh, rest.Gpu * hours);
            Add(hour, AppKeys.Idle, idleCpu, idleGpu);
            var working = cpuWh - idleCpu;
            var pool = PoolOf(hour);
            pool.Working += working;
            pool.Graphics += sample.IGpuW is { } graphics && double.IsFinite(graphics) ? Math.Min(working, Positive(graphics - rest.IGpu) * hours) : 0;
            pool.Card += gpuWh - idleGpu;
            pool.Seconds += seconds;
            _pooledSeconds += seconds;
            Front(hour, seconds);
        }
        // A beat Windows put off a little must not leave the unwatched pace a look short every other tick.
        if (seconds <= 0 || _pooledSeconds >= _lookEverySeconds * 0.9) Settle();
    }

    /// <summary>Writes every hour that changed since it was last written, each whole, after a look settles the pool, and
    /// forgets those before the hour the ticks are in once written. Throws what storage throws; what failed is written next
    /// time.</summary>
    public void Write()
    {
        if (_pools.Count > 0) Settle();
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
    /// Takes a look and splits the pooled ticks of each hour by what the processes did since the last one (see
    /// <see cref="AppSplit"/>; the pool's resting part went to idle tick by tick, so it is split with nothing at rest). Each
    /// app running at the look ran through the pool. Without a look to split by, as when Windows will not list the
    /// processes, the pool goes to Other apps, since some app drew it.
    /// </summary>
    private void Settle()
    {
        var looked = Look(out var work);
        var engineWork = _graphics ?? OwnEngineWork();
        if (_pools.Count == 0) return;
        var (card, graphics) = Engines(engineWork);
        foreach (var pool in _pools)
        {
            var hour = pool.Hour;
            if (!looked)
            {
                Add(hour, AppKeys.Other, pool.Working, pool.Card);
                continue;
            }
            var energy = new TickEnergy(pool.Working, pool.Card, pool.Seconds, pool.Graphics > 0 ? pool.Graphics * 3600 / pool.Seconds : null);
            var split = AppSplit.Of(energy, default, work, card, graphics);
            Add(hour, AppKeys.Idle, split.IdleCpuWh, split.IdleGpuWh);
            Add(hour, AppKeys.Windows, split.KernelCpuWh, split.KernelGpuWh);
            Add(hour, AppKeys.Closed, split.ClosedCpuWh, split.ClosedGpuWh);
            foreach (var process in _live)
            {
                var app = TotalsOf(process, hour);
                app.CpuWh += process.CycleDelta * split.CpuWhPerCycle;
                app.GpuWh += process.CycleDelta * split.GpuWhPerCycle;
                app.Run(_looks, pool.Seconds);
            }
            if (engineWork?.ByProcess is { } byProcess && (split.CpuWhPerGraphicsTime > 0 || split.GpuWhPerCardTime > 0))
            {
                foreach (var (pid, time) in Each(byProcess))
                {
                    if (!_tracked.TryGetValue(pid, out var process)) continue;      // closed since: counted as closed
                    var app = TotalsOf(process, hour);
                    app.CpuWh += (split.GraphicsByLoadEngines ? time.ProcessorLoad : time.ProcessorAny) * split.CpuWhPerGraphicsTime;
                    app.GpuWh += (split.CardByLoadEngines ? time.CardLoad : time.CardAny) * split.GpuWhPerCardTime;
                }
            }
        }
        _pools.Clear();
        _pooledSeconds = 0;
    }

    /// <summary>The engines' work where the sensors read no load counters: the counters read with the look, but at most
    /// once in <see cref="LookEvery"/> of the ticks' time, a look sooner, as a write's, keeping the last window.</summary>
    private GraphicsWork? OwnEngineWork()
    {
        if (engines is null) return null;
        if (_enginesReadAt is { } last && _tickAt is { } now && (now - last).TotalSeconds < _lookEverySeconds * 0.9) return _engineWork;
        ReadEngines();
        return _engineWork;
    }

    private void ReadEngines()
    {
        if (engines is null) return;
        _enginesReadAt = _tickAt;
        try
        {
            _engineWork = engines.Read();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _engineWork = null;                                     // the card's watts follow the processor's split meanwhile
        }
    }

    /// <summary>The tick's time in front, to the app of the process in front in each session, as the last look knows it.</summary>
    private void Front(AppHour hour, double seconds)
    {
        if (foreground is null) return;
        foreground(_front);
        foreach (var pid in _front)
        {
            if (_tracked.TryGetValue(pid, out var process)) TotalsOf(process, hour).Front(_ticks, seconds);
        }
    }

    private Pool PoolOf(AppHour hour)
    {
        foreach (var pool in _pools)
        {
            if (pool.Hour == hour) return pool;
        }
        var added = new Pool(hour);
        _pools.Add(added);
        return added;
    }

    private static double Positive(double x) => double.IsFinite(x) && x > 0 ? x : 0;

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
        _looks++;
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
            process.Seen = _looks;
            _live.Add(process);
            cycles += process.CycleDelta;
            explained += process.TimeDelta;
        }
        foreach (var (pid, process) in _tracked)
        {
            if (process.Seen != _looks) _tracked.Remove(pid);
        }

        var measured = _looked;
        if (measured) work = new ProcessorWork(cycles, Math.Max(0, times.Busy - _processors.Busy), explained, Math.Max(0, times.Drivers - _processors.Drivers));
        _processors = times;
        _looked = true;
        return measured;
    }

    /// <summary>What app a new process belongs to, and who runs it (review item 10: tagged by account, not by session, so
    /// a person over Remote Desktop has their own apps too). The kernel's processes, and any image under the Windows folder,
    /// are Windows', which is the machine's; a Store app is its package; anything else its image path. A process that
    /// cannot be opened goes by the name Windows lists it with.</summary>
    private Tracked Identify(ProcessCounts listed)
    {
        var process = new Tracked { Created = listed.CreateTime };
        if (listed.Pid == 4 || (listed.Session == 0 && KernelProcesses.Contains(processes.ImageName(listed.Name))))
        {
            process.Key = AppKeys.Windows;
            return process;
        }
        var image = processes.Image(listed.Pid, listed.CreateTime);
        process.User = AppUsers.Of(image?.UserSid);
        if (image?.Path is { Length: > 0 } path)
        {
            if (AppKeys.IsWindows(path.ToLowerInvariant(), processes.WindowsFolder))
            {
                process.Key = AppKeys.Windows;
                process.User = AppUsers.Machine;
                return process;
            }
            process.Path = path;
            // One key across an app's updates, its version folders left out (review item 5).
            process.Key = image.PackageFamily is { Length: > 0 } family ? AppKeys.Package(family) : AppKeys.ForPath(path);
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

    /// <summary>The totals a process's figures go into this hour: its app's, as run by its account (the App pools other
    /// people's when it reads them). Kept with the process until the hour changes, so a tick looks nothing up.</summary>
    private AppTotals TotalsOf(Tracked process, AppHour hour)
    {
        if (process.Totals is null || process.Hour != hour)
        {
            process.Totals = hour.For(process.User, process.Key, process.Path);
            process.Hour = hour;
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
        public int User;
        public AppHour? Hour;
        public AppTotals? Totals;
    }

    /// <summary>One hour's ticks since the last look, their resting part taken out: the processor's working energy, the
    /// part of it the processor's graphics drew on their rail, the cards' working energy, and how long the ticks ran.</summary>
    private sealed class Pool(AppHour hour)
    {
        public AppHour Hour { get; } = hour;
        public double Working;
        public double Graphics;
        public double Card;
        public double Seconds;
    }
}
