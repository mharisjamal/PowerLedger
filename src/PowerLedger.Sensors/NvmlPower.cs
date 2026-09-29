namespace PowerLedger.Sensors;

/// <summary>The NVML calls PowerLedger makes of one open GPU, so a test can stand in for the driver. Each answers NVML's
/// own result code.</summary>
internal interface INvmlDevice
{
    /// <summary>nvmlDeviceGetTotalEnergyConsumption: millijoules since the driver loaded, Volta and newer. May throw
    /// <see cref="EntryPointNotFoundException"/> on a driver too old to have it.</summary>
    int TotalEnergy(out ulong millijoules);

    /// <summary>nvmlDeviceGetPowerUsage: milliwatts, on Ampere and newer an average over the last second.</summary>
    int PowerUsage(out uint milliwatts);

    /// <summary>nvmlDeviceGetUtilizationRates: the GPU's busy percent.</summary>
    int Utilisation(out uint gpuPercent);
}

/// <summary>
/// One NVIDIA GPU's watts and load. On Volta and newer the card keeps a monotonic energy counter
/// (<c>nvmlDeviceGetTotalEnergyConsumption</c>, millijoules since the driver loaded), and the watts are the energy it gained
/// since the previous read over the time between the reads, so every figure is exact over its tick, as the CPU's energy meter
/// is, rather than NVML's own one-second average of its choosing. The power figure (<c>nvmlDeviceGetPowerUsage</c>) is kept
/// for every read the counter can't answer: the first, which has no interval yet; one after the counter went backwards,
/// which a driver restart or a wrap does and which starts the next interval; one after a gap longer than
/// <see cref="LongestInterval"/>, as while Windows had the card switched off and it was not asked; one too soon after the
/// last for the counter to have moved; one the counter declined; and every read of a card older than Volta, whose counter
/// is not supported and is not asked again. Each card has its own; single-threaded, as the sampling loop owns it.
/// </summary>
internal sealed class NvmlPower
{
    private const int Success = 0;
    private const int Uninitialized = 1;
    private const int NotSupported = 3;
    private const int DriverNotLoaded = 9;
    private const int GpuIsLost = 15;

    /// <summary>Past this, the energy since the previous read is spread over more than one tick, so the interval restarts.</summary>
    public static readonly TimeSpan LongestInterval = TimeSpan.FromSeconds(10);

    /// <summary>Below this the counter, which the driver updates a few times a second, may not have moved yet.</summary>
    public static readonly TimeSpan ShortestInterval = TimeSpan.FromMilliseconds(200);

    private readonly INvmlDevice _device;
    private readonly Func<TimeSpan> _clock;
    private bool _counter = true;
    private (ulong Millijoules, TimeSpan At)? _previous;

    public NvmlPower(INvmlDevice device, Func<TimeSpan> clock)
    {
        _device = device;
        _clock = clock;
    }

    /// <summary>The card's watts and load; either is null when the card won't say. Throws when the GPU or its driver has gone.</summary>
    public GpuReading Read()
    {
        var watts = CounterWatts();
        if (watts is null)
        {
            var powerResult = _device.PowerUsage(out var milliwatts);
            ThrowIfBroken(powerResult);
            watts = powerResult == Success ? milliwatts / 1000.0 : null;
        }

        var loadResult = _device.Utilisation(out var busy);
        ThrowIfBroken(loadResult);
        return new GpuReading(true, watts, loadResult == Success ? busy / 100.0 : null);
    }

    /// <summary>The energy counter's watts since the previous read, or null when it can't give them this time.</summary>
    private double? CounterWatts()
    {
        if (!_counter) return null;

        int result;
        ulong millijoules;
        try
        {
            result = _device.TotalEnergy(out millijoules);
        }
        catch (EntryPointNotFoundException)
        {
            _counter = false;
            return null;
        }
        ThrowIfBroken(result);
        if (result == NotSupported)
        {
            _counter = false;
            return null;
        }
        if (result != Success)
        {
            _previous = null;
            return null;
        }

        var now = _clock();
        if (_previous is not { } before || millijoules < before.Millijoules || now - before.At > LongestInterval || now <= before.At)
        {
            _previous = (millijoules, now);
            return null;
        }
        if (now - before.At < ShortestInterval) return null;

        _previous = (millijoules, now);
        return (millijoules - before.Millijoules) / 1000.0 / (now - before.At).TotalSeconds;
    }

    /// <summary>A lost GPU, an unloaded driver or a torn-down library is a broken source, not a missing reading:
    /// throwing lets the sampler back off and show it in status instead of charging a phantom load forever.</summary>
    private static void ThrowIfBroken(int result)
    {
        if (result is Uninitialized or DriverNotLoaded or GpuIsLost)
        {
            throw new InvalidOperationException($"NVML error {result}: the GPU or its driver is no longer available");
        }
    }
}
