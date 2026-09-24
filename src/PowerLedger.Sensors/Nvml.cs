using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <param name="Present">True when at least one NVIDIA GPU answered.</param>
/// <param name="PowerWatts">Current draw, or null on a card with no power measurement hardware.</param>
/// <param name="LoadFraction">Utilisation 0..1, or null when the card declines to answer.</param>
public readonly record struct GpuReading(bool Present, double? PowerWatts, double? LoadFraction);

/// <summary>
/// The NVIDIA management library, which ships with the display driver. No package reference and no driver of
/// ours: if there is no NVIDIA GPU, loading the library simply fails and the source reports itself unsupported.
/// <see cref="NvmlLibrary"/> finds it in System32, NVSMI or the driver's DriverStore folder; the DllImports' own
/// search stays System32 alone.
/// </summary>
public sealed class Nvml : IDisposable
{
    private const int Success = 0;
    private const int Uninitialized = 1;
    private const int DriverNotLoaded = 9;
    private const int GpuIsLost = 15;

    private readonly IntPtr _device;
    private bool _initialised;

    /// <summary>The library is looked for where the driver put it, not only in System32 (see <see cref="NvmlLibrary"/>).</summary>
    static Nvml() => NvmlLibrary.Register();

    public Nvml() : this(Environment.Is64BitProcess)
    {
    }

    /// <summary>Test seam: a 32-bit process never loads NVML, which has no 32-bit version.</summary>
    internal Nvml(bool is64BitProcess)
    {
        if (Bitness.SixtyFourBitOnly("NVIDIA's NVML", is64BitProcess) is { } reason)
        {
            Unavailable = reason;
            return;
        }
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

        var powerResult = nvmlDeviceGetPowerUsage(_device, out var milliwatts);
        var loadResult = nvmlDeviceGetUtilizationRates(_device, out var utilisation);
        ThrowIfBroken(powerResult);
        ThrowIfBroken(loadResult);

        // "Not supported" means a card with no power sensor, and other errors a card that declined this once: both are null.
        double? watts = powerResult == Success ? milliwatts / 1000.0 : null;
        double? load = loadResult == Success ? utilisation.Gpu / 100.0 : null;
        return new GpuReading(true, watts, load);
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

    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlShutdown();
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilisation utilisation);
}
