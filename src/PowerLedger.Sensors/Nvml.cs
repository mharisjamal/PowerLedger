using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <param name="Present">True when at least one NVIDIA GPU answered.</param>
/// <param name="PowerWatts">Current draw, or null on a card with no power measurement hardware.</param>
/// <param name="LoadFraction">Utilisation 0..1, or null when the card declines to answer.</param>
public readonly record struct GpuReading(bool Present, double? PowerWatts, double? LoadFraction);

/// <summary>
/// The NVIDIA management library, which ships with the display driver. No package reference and no driver of
/// ours: if there is no NVIDIA GPU, loading the library simply fails and the source reports itself unsupported.
/// </summary>
public sealed class Nvml : IDisposable
{
    private const int Success = 0;
    private const int NotSupported = 3;

    private readonly IntPtr _device;
    private bool _initialised;

    public Nvml()
    {
        try
        {
            if (nvmlInit_v2() != Success)
            {
                Unavailable = "NVML would not start";
                return;
            }
            _initialised = true;

            if (nvmlDeviceGetCount_v2(out var count) != Success || count == 0)
            {
                Unavailable = "no NVIDIA GPU";
                return;
            }
            if (nvmlDeviceGetHandleByIndex_v2(0, out _device) != Success)
            {
                Unavailable = "NVML would not open the GPU";
                return;
            }
            Available = true;
        }
        catch (DllNotFoundException)
        {
            Unavailable = "no NVIDIA driver installed";
        }
        catch (EntryPointNotFoundException)
        {
            Unavailable = "the NVIDIA driver is too old";
        }
    }

    /// <summary>True when a GPU is open and can be asked.</summary>
    public bool Available { get; }

    /// <summary>Why there is nothing to ask, for the status screen; null when a GPU is available.</summary>
    public string? Unavailable { get; }

    public GpuReading Read()
    {
        if (!Available) return new GpuReading(false, null, null);

        double? watts = nvmlDeviceGetPowerUsage(_device, out var milliwatts) == Success ? milliwatts / 1000.0 : null;
        double? load = nvmlDeviceGetUtilizationRates(_device, out var utilisation) == Success ? utilisation.Gpu / 100.0 : null;
        return new GpuReading(true, watts, load);
    }

    public void Dispose()
    {
        if (!_initialised) return;
        _initialised = false;
        try
        {
            nvmlShutdown();
        }
        catch (DllNotFoundException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilisation
    {
        public uint Gpu;
        public uint Memory;
    }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilisation utilisation);
}
