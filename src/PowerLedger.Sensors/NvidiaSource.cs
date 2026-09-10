namespace PowerLedger.Sensors;

/// <summary>
/// Discrete GPU power, load and presence. Power is null on the many laptop GPUs with no measurement hardware,
/// which the power model turns into the load fallback rather than a zero.
/// </summary>
public sealed class NvidiaSource : ISensorSource
{
    private readonly Func<GpuReading> _read;
    private readonly Nvml? _nvml;

    public NvidiaSource()
    {
        _nvml = new Nvml();
        _read = _nvml.Read;
        Supported = _nvml.Available;
        Unavailable = _nvml.Unavailable;
    }

    /// <summary>Test seam: any source of GPU readings.</summary>
    internal NvidiaSource(Func<GpuReading> read, bool present, string? unavailable)
    {
        _read = read;
        Supported = present;
        Unavailable = unavailable;
    }

    public string Name => "nvidia-gpu";

    public bool Supported { get; }

    public string? Unavailable { get; }

    public void Contribute(SampleDraft draft)
    {
        var reading = _read();
        draft.DGpuPresent = reading.Present;
        draft.DGpuW = reading.PowerWatts;
        draft.DGpuLoad = reading.LoadFraction;
    }

    public void Dispose() => _nvml?.Dispose();
}
