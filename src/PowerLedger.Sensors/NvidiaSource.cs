namespace PowerLedger.Sensors;

/// <summary>
/// Discrete GPU power, load and presence. When Windows has switched the GPU off, as a switchable-graphics laptop
/// does almost all the time, it draws next to nothing and NVML is not asked at all. When it is on, power is null on
/// the many laptop GPUs with no measurement hardware, which the power model turns into its load fallback.
/// </summary>
public sealed class NvidiaSource : ISensorSource
{
    private readonly Func<GpuReading> _read;
    private readonly Func<bool> _poweredOff;
    private readonly Nvml? _nvml;

    public NvidiaSource()
    {
        var device = DevicePowerState.FindNvidiaGpu();
        _poweredOff = device is null ? static () => false : () => DevicePowerState.IsPoweredOff(device);
        _nvml = new Nvml();
        _read = _nvml.Read;
        Supported = _nvml.Available;
        Unavailable = _nvml.Unavailable;
    }

    /// <summary>Test seam: any source of GPU readings and power state.</summary>
    internal NvidiaSource(Func<GpuReading> read, bool present, string? unavailable, Func<bool>? poweredOff = null)
    {
        _read = read;
        _poweredOff = poweredOff ?? (static () => false);
        Supported = present;
        Unavailable = unavailable;
    }

    public string Name => "nvidia-gpu";

    public bool Supported { get; }

    public string? Unavailable { get; }

    public void Contribute(SampleDraft draft)
    {
        if (_poweredOff())
        {
            draft.DGpuPresent = true;
            draft.DGpuW = 0;
            draft.DGpuLoad = 0;
            return;
        }

        var reading = _read();
        draft.DGpuPresent = reading.Present;
        draft.DGpuW = reading.PowerWatts;
        draft.DGpuLoad = reading.LoadFraction;
    }

    public void Dispose() => _nvml?.Dispose();
}
