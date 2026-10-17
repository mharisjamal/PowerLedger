namespace PowerLedger.Core;

/// <summary>One tick's processor and graphics energy as the integrator books it (<see cref="EnergyIntegrator"/>), with what
/// the split needs besides: how long the tick ran and what the processor's graphics drew inside its package.</summary>
/// <param name="CpuWh">The tick's processor energy, the package with the graphics in it.</param>
/// <param name="GpuWh">The tick's discrete graphics energy, every card together.</param>
/// <param name="Seconds">How long the tick ran awake; nought for a gap.</param>
/// <param name="IGpuW">What the processor's graphics drew, from a rail of its own (Intel's PP1), which the package watts
/// already hold; null where nothing measures it.</param>
public readonly record struct TickEnergy(double CpuWh, double GpuWh, double Seconds, double? IGpuW);

/// <summary>What the parts draw at rest (<see cref="RestingWatts"/>): the processor package, the cards together, and the
/// processor's graphics on their rail.</summary>
public readonly record struct RestWatts(double Cpu, double Gpu, double IGpu);

/// <summary>What the processor did over a tick, every logical processor added up.</summary>
/// <param name="Cycles">The cycles every process still running used, added up.</param>
/// <param name="Busy">Busy time, in 100 ns units: kernel and user time less idle time, as GetSystemTimes counts it.</param>
/// <param name="Explained">The kernel and user time every process still running used, added up.</param>
/// <param name="Kernel">The time spent on drivers' interrupts and deferred procedure calls, which Windows charges to no
/// process.</param>
public readonly record struct ProcessorWork(ulong Cycles, long Busy, long Explained, long Kernel);

/// <summary>Time on one kind of graphics' engines over the counters' last window, in 100 ns units: on the shader engines
/// (3D and compute, <c>Load</c>) and on any engine, video and copy included (<c>Any</c>), by processes still running
/// (<c>Live</c>) and by processes that have closed since (<c>Dead</c>).</summary>
public readonly record struct EngineWork(long LiveLoad, long DeadLoad, long LiveAny, long DeadAny);

/// <summary>
/// How one tick's processor and graphics energy is split among apps (Plan AE, research §9). The parts' resting draw goes
/// to no app (<see cref="AppKeys.Idle"/>), as the greenhouse gas protocol splits idle power. On the processor, the working
/// part the processor's own graphics drew on their rail, where one measures it, goes by each process's time on their
/// engines; the rest by busy time, the drivers' share to Windows (<see cref="AppKeys.Windows"/>), the share no running
/// process explains to closed programs (<see cref="AppKeys.Closed"/>) and the rest by each running process's cycles. A
/// card's working part goes by each process's time on its shader engines, or on any engine when none ran; the time of
/// processes closed since is closed programs', and a card awake that nobody used draws for itself. Where nothing reads the
/// card's engines, its working part follows the processor's split. A process's share is its cycles times
/// <see cref="CpuWhPerCycle"/> and <see cref="GpuWhPerCycle"/>, plus its engine time times <see cref="CpuWhPerGraphicsTime"/>
/// and <see cref="GpuWhPerCardTime"/>; with the special keys' figures, the shares add up to the tick's energy exactly, so
/// an hour's apps add up to its hour row.
/// </summary>
public readonly record struct AppSplit(
    double IdleCpuWh, double IdleGpuWh,
    double KernelCpuWh, double KernelGpuWh,
    double ClosedCpuWh, double ClosedGpuWh,
    double CpuWhPerCycle, double GpuWhPerCycle,
    double CpuWhPerGraphicsTime, bool GraphicsByLoadEngines,
    double GpuWhPerCardTime, bool CardByLoadEngines,
    double ProcessorWh = 0, double GraphicsClosedCpuWh = 0)
{
    // ProcessorWh: the working part split by busy time and cycles (nought when nothing ran, which went to idle), for a
    // split by work above rest instead (Plan AF, QuietRates). GraphicsClosedCpuWh: the part of ClosedCpuWh that is closed
    // programs' time on the processor's graphics, which that split leaves as it is.
    /// <summary>A tick with no energy to split, as a gap is.</summary>
    public static AppSplit Nothing => default;

    /// <param name="card">Time on the discrete cards' engines; null where nothing reads them.</param>
    /// <param name="processorGraphics">Time on the processor's graphics' engines; null where nothing reads them.</param>
    public static AppSplit Of(TickEnergy energy, RestWatts rest, ProcessorWork processor, EngineWork? card, EngineWork? processorGraphics)
    {
        var cpuWh = Positive(energy.CpuWh);
        var gpuWh = Positive(energy.GpuWh);
        if (cpuWh == 0 && gpuWh == 0) return Nothing;
        var hours = Positive(energy.Seconds) / 3600;
        var shares = Shares.Of(processor);
        var split = Nothing;

        // The processor package: its rest, then what its graphics drew working, then the rest by busy time and cycles.
        var idleCpu = Math.Min(cpuWh, Positive(rest.Cpu) * hours);
        var working = cpuWh - idleCpu;
        var (graphicsByLoad, graphicsLive, graphicsDead) = Pick(processorGraphics);
        var graphicsTime = graphicsLive + graphicsDead;
        var graphicsWh = energy.IGpuW is { } igpu && double.IsFinite(igpu) && graphicsTime > 0
            ? Math.Min(working, Positive(igpu - Positive(rest.IGpu)) * hours)
            : 0;
        var closedCpu = graphicsTime > 0 ? graphicsWh * graphicsDead / graphicsTime : 0;
        var graphicsClosed = closedCpu;
        var processorWh = working - graphicsWh;
        if (shares.None)
        {
            idleCpu += processorWh;
        }
        else
        {
            closedCpu += processorWh * shares.Closed;
            split = split with
            {
                KernelCpuWh = processorWh * shares.Kernel, CpuWhPerCycle = PerCycle(processorWh * shares.Live, processor.Cycles), ProcessorWh = processorWh,
            };
        }
        split = split with
        {
            IdleCpuWh = idleCpu,
            ClosedCpuWh = closedCpu,
            CpuWhPerGraphicsTime = graphicsWh > 0 ? graphicsWh / graphicsTime : 0,
            GraphicsByLoadEngines = graphicsByLoad,
            GraphicsClosedCpuWh = graphicsClosed,
        };

        // The cards: their rest, then the working part by time on their engines, or by the processor's split.
        var idleGpu = Math.Min(gpuWh, Positive(rest.Gpu) * hours);
        var active = gpuWh - idleGpu;
        if (card is not null)
        {
            var (cardByLoad, cardLive, cardDead) = Pick(card);
            var cardTime = cardLive + cardDead;
            if (cardTime > 0)
                return split with { IdleGpuWh = idleGpu, ClosedGpuWh = active * cardDead / cardTime, GpuWhPerCardTime = active / cardTime, CardByLoadEngines = cardByLoad };
            return split with { IdleGpuWh = idleGpu + active };
        }
        if (shares.None) return split with { IdleGpuWh = idleGpu + active };
        return split with
        {
            IdleGpuWh = idleGpu,
            KernelGpuWh = active * shares.Kernel,
            ClosedGpuWh = active * shares.Closed,
            GpuWhPerCycle = PerCycle(active * shares.Live, processor.Cycles),
        };
    }

    /// <summary>The busy time no running process explains, as the split divides it: the drivers' interrupts and deferred
    /// calls, and closed programs', in 100 ns units.</summary>
    public static (double Drivers, double Closed) Unexplained(ProcessorWork work)
    {
        var unexplained = Math.Max(0, Math.Max(0, (double)work.Busy) - Math.Max(0, work.Explained));
        var drivers = Math.Min(unexplained, Math.Max(0, work.Kernel));
        return (drivers, unexplained - drivers);
    }

    /// <summary>Energy per cycle; the running processes' share is nought whenever no cycles were counted.</summary>
    private static double PerCycle(double wh, ulong cycles) => cycles > 0 ? wh / cycles : 0;

    /// <summary>The engine times the split goes by: the shader engines' when any ran, else every engine's.</summary>
    private static (bool ByLoad, double Live, double Dead) Pick(EngineWork? work)
    {
        if (work is not { } w) return (false, 0, 0);
        var load = Math.Max(0, w.LiveLoad) + (double)Math.Max(0, w.DeadLoad);
        return load > 0 ? (true, Math.Max(0, w.LiveLoad), Math.Max(0, w.DeadLoad)) : (false, Math.Max(0, w.LiveAny), Math.Max(0, w.DeadAny));
    }

    private static double Positive(double x) => double.IsFinite(x) && x > 0 ? x : 0;

    /// <summary>How the processor's working part divides: running processes, drivers, closed programs. They add up to one,
    /// or are all nought when nothing anyone can see ran.</summary>
    private readonly record struct Shares(double Live, double Kernel, double Closed)
    {
        public bool None => Live == 0 && Kernel == 0 && Closed == 0;

        public static Shares Of(ProcessorWork work)
        {
            double busy = Math.Max(0, work.Busy);
            double explained = Math.Max(0, work.Explained);
            var unexplained = Math.Max(0, busy - explained);
            var kernel = Math.Min(unexplained, Math.Max(0, work.Kernel));
            var closed = unexplained - kernel;
            var live = work.Cycles > 0 ? busy - unexplained : 0;
            var total = live + kernel + closed;
            if (total > 0) return new Shares(live / total, kernel / total, closed / total);
            return work.Cycles > 0 ? new Shares(1, 0, 0) : default;
        }
    }
}
