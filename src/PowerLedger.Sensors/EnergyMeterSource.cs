namespace PowerLedger.Sensors;

/// <summary>
/// Fills the CPU package, integrated-graphics and whole-platform watts from the Windows energy meter. Owns those fields
/// and nothing else: CPU load comes from <see cref="CpuLoadSource"/>, because load is available even where
/// watts are not. The memory rail is read but not published, since the model has no field for it yet.
/// Supported only where the meter has a package rail or a whole-platform rail (Snapdragon X's <c>system</c>), so a
/// supported meter measures the processor, the platform or both; one with only core, graphics or memory rails is
/// unsupported and says why.
/// </summary>
public sealed class EnergyMeterSource : ISensorSource
{
    /// <summary>1 once this process has logged the meter's rails, which it does once, however often the sensors are rebuilt.</summary>
    private static int s_railsNoted;

    private readonly Func<EnergyMeterReading> _read;
    private readonly EnergyMeter? _meter;
    private readonly PlatformRailCheck? _check;
    private readonly string? _unavailable;

    /// <summary>Opens the machine's energy meter.</summary>
    /// <param name="check">The check of its platform rail against the battery, whose verdict the status screen shows.</param>
    /// <param name="note">Told, once in the life of the process, the meter's rails and what each is taken for, when any is
    /// not one of Intel's or AMD's usual ones.</param>
    public EnergyMeterSource(PlatformRailCheck? check = null, Action<string>? note = null)
    {
        _meter = new EnergyMeter();
        _read = _meter.Read;
        _check = check;
        Supported = _meter.Available;
        _unavailable = _meter.Unavailable;
        if (note is not null && EnergyMeter.RailsWorthNoting(_meter.Instances) is { } rails && Interlocked.Exchange(ref s_railsNoted, 1) == 0)
        {
            note($"Energy meter rails: {rails}");
        }
    }

    /// <summary>Test seam: any source of readings.</summary>
    internal EnergyMeterSource(Func<EnergyMeterReading> read, bool available, string? unavailable, PlatformRailCheck? check = null)
    {
        _read = read;
        _check = check;
        Supported = available;
        _unavailable = unavailable;
    }

    public string Name => "energy-meter";

    public bool Supported { get; }

    /// <summary>Why the meter can't be read; or, while it is read, why its platform rail is set aside.</summary>
    public string? Unavailable => _unavailable ?? _check?.Status;

    public void Contribute(SampleDraft draft)
    {
        var reading = _read();
        draft.CpuPackageW = reading.PackageW;
        draft.IGpuW = reading.IntegratedGpuW;
        draft.PlatformW = reading.PlatformW;
        draft.PlatformHoldsCharging = QualcommRails.SystemHoldsCharging;
    }

    public void Dispose() => _meter?.Dispose();
}
