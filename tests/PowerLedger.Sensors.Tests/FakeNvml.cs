using PowerLedger.Sensors;

namespace PowerLedger.Sensors.Tests;

/// <summary>
/// One GPU as NVML answers for it, under the test's control: an energy counter the test moves, a power figure, a load, and
/// the result code each call gives. Codes are NVML's own.
/// </summary>
internal sealed class FakeNvmlDevice : INvmlDevice
{
    public const int Success = 0;
    public const int Uninitialized = 1;
    public const int NotSupported = 3;
    public const int Timeout = 10;
    public const int GpuIsLost = 15;

    /// <summary>The counter nvmlDeviceGetTotalEnergyConsumption gives, in millijoules since the driver loaded.</summary>
    public ulong EnergyMillijoules { get; set; }

    public int EnergyResult { get; set; } = Success;

    /// <summary>Thrown by the energy call when set, as a driver too old to have it throws EntryPointNotFoundException.</summary>
    public Exception? EnergyThrows { get; set; }

    public uint PowerMilliwatts { get; set; }

    public int PowerResult { get; set; } = Success;

    public uint LoadPercent { get; set; } = 40;

    public int LoadResult { get; set; } = Success;

    public int EnergyCalls { get; private set; }

    public int PowerCalls { get; private set; }

    public int TotalEnergy(out ulong millijoules)
    {
        EnergyCalls++;
        if (EnergyThrows is { } error) throw error;
        millijoules = EnergyMillijoules;
        return EnergyResult;
    }

    public int PowerUsage(out uint milliwatts)
    {
        PowerCalls++;
        milliwatts = PowerMilliwatts;
        return PowerResult;
    }

    public int Utilisation(out uint gpuPercent)
    {
        gpuPercent = LoadPercent;
        return LoadResult;
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeClock
{
    public TimeSpan Now { get; set; } = TimeSpan.FromSeconds(100);

    public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);

    public TimeSpan Read() => Now;
}
