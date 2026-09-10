namespace PowerLedger.Sensors;

/// <summary>
/// Fills the CPU package and integrated-graphics watts from the Windows energy meter. Owns those two fields
/// and nothing else: CPU load comes from <see cref="CpuLoadSource"/>, because load is available even where
/// watts are not. The memory rail is read but not published, since the model has no field for it yet.
/// </summary>
public sealed class EnergyMeterSource : ISensorSource
{
    private readonly Func<EnergyMeterReading> _read;
    private readonly EnergyMeter? _meter;

    /// <summary>Opens the machine's energy meter.</summary>
    public EnergyMeterSource()
    {
        _meter = new EnergyMeter();
        _read = _meter.Read;
        Supported = _meter.Available;
        Unavailable = _meter.Unavailable;
    }

    /// <summary>Test seam: any source of readings.</summary>
    internal EnergyMeterSource(Func<EnergyMeterReading> read, bool available, string? unavailable)
    {
        _read = read;
        Supported = available;
        Unavailable = unavailable;
    }

    public string Name => "energy-meter";

    public bool Supported { get; }

    public string? Unavailable { get; }

    public void Contribute(SampleDraft draft)
    {
        var reading = _read();
        draft.CpuPackageW = reading.PackageW;
        draft.IGpuW = reading.IntegratedGpuW;
    }

    public void Dispose() => _meter?.Dispose();
}
