using System.Runtime.InteropServices;
using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// AMD's ADLX library, which the display driver installs as amdadlx64.dll. It has no package reference and no driver
/// of ours: where there is no AMD driver, loading it simply fails and the source says so. ADLX hands out C interfaces,
/// each a pointer to a table of function pointers, so a method is called by its slot in that table. The slots below are
/// taken from the SDK headers ISystem.h and IPerformanceMonitoring.h in the order they declare the methods, which is
/// the order the C++ classes declare them, and which has not changed since the first published SDK.
/// </summary>
internal sealed unsafe class Adlx : IAmdGpu
{
    /// <summary>ADLX_OK, and the two other results ADLX_SUCCEEDED counts as success.</summary>
    private const int Ok = 0, AlreadyEnabled = 1, AlreadyInitialized = 2;

    /// <summary>ADLX_TERMINATED, ADLX_INVALID_OBJECT and ADLX_ORPHAN_OBJECTS: the library has gone, which happens in a
    /// service when the user logs off, and every interface taken from it must be let go.</summary>
    private const int Terminated = 7, InvalidObject = 10, OrphanObjects = 11;

    /// <summary>ADLX_GPU_TYPE: GPUTYPE_UNDEFINED is 0, GPUTYPE_INTEGRATED 1, GPUTYPE_DISCRETE 2.</summary>
    private const int Discrete = 2;

    // IADLXInterface: Acquire, Release, QueryInterface. Every interface but IADLXSystem starts with these three.
    private const int Release = 1;

    // IADLXSystem: GetHybridGraphicsType, GetGPUs, QueryInterface, GetDisplaysServices, GetDesktopsServices,
    // GetGPUsChangedHandling, EnableLog, Get3DSettingsServices, GetGPUTuningServices, GetPerformanceMonitoringServices,
    // TotalSystemRAM, GetI2C. It is a singleton and is never released.
    private const int GetGpus = 1, GetPerformanceMonitoringServices = 9;

    // IADLXGPUList: the three of IADLXInterface, then IADLXList's Size, Empty, Begin, End, At, Clear, Remove_Back,
    // Add_Back, then its own At_GPUList and Add_Back_GPUList.
    private const int Begin = 5, End = 6, AtGpuList = 11;

    // IADLXGPU: the three, then VendorId, ASICFamilyType, Type, IsExternal, Name, DriverPath, PNPString, HasDesktops,
    // TotalVRAM, VRAMType, BIOSInfo, DeviceId, RevisionId, SubSystemId, SubSystemVendorId, UniqueId.
    private const int GpuType = 5, PnpString = 9;

    // IADLXPerformanceMonitoringServices: the three, then GetSamplingIntervalRange, SetSamplingInterval,
    // GetSamplingInterval, GetMaxPerformanceMetricsHistorySizeRange, SetMaxPerformanceMetricsHistorySize,
    // GetMaxPerformanceMetricsHistorySize, ClearPerformanceMetricsHistory, GetCurrentPerformanceMetricsHistorySize,
    // StartPerformanceMetricsTracking, StopPerformanceMetricsTracking, GetAllMetricsHistory, GetGPUMetricsHistory,
    // GetSystemMetricsHistory, GetFPSHistory, GetCurrentAllMetrics, GetCurrentGPUMetrics, GetCurrentSystemMetrics,
    // GetCurrentFPS, GetSupportedGPUMetrics, GetSupportedSystemMetrics.
    private const int GetCurrentGpuMetrics = 18, GetSupportedGpuMetrics = 21;

    // IADLXGPUMetricsSupport: the three, then IsSupportedGPUUsage, IsSupportedGPUClockSpeed,
    // IsSupportedGPUVRAMClockSpeed, IsSupportedGPUTemperature, IsSupportedGPUHotspotTemperature, IsSupportedGPUPower,
    // IsSupportedGPUTotalBoardPower, and the rest of the checks and ranges.
    private const int IsSupportedGpuPower = 8, IsSupportedGpuTotalBoardPower = 9;

    // IADLXGPUMetrics: the three, then TimeStamp, GPUUsage, GPUClockSpeed, GPUVRAMClockSpeed, GPUTemperature,
    // GPUHotspotTemperature, GPUPower, GPUTotalBoardPower, GPUFanSpeed, GPUVRAM, GPUVoltage, GPUIntakeTemperature.
    private const int GpuPower = 9, GpuTotalBoardPower = 10;

    /// <summary>No metric to read: this card reports neither its board power nor its chip power.</summary>
    private const int NoMetric = -1;

    private readonly AdlxLibrary _library;
    private readonly nint _gpu;
    private readonly nint _services;
    private readonly int _metric;
    private readonly GpuPowerScope _scope;
    private bool _closed;
    private bool _lost;

    private Adlx(AdlxLibrary library, nint gpu, nint services, int metric, GpuPowerScope scope, string? deviceId)
    {
        _library = library;
        _gpu = gpu;
        _services = services;
        _metric = metric;
        _scope = scope;
        DeviceId = deviceId;
    }

    public string? DeviceId { get; }

    /// <summary>ADLX on this machine, opened on its first discrete Radeon. Never throws.</summary>
    public static AmdOpening Open()
        => AdlxLibrary.Installed is { } library
            ? Open(library)
            : new AmdOpening(AmdLibrary.NotInstalled, null, "AMD's ADLX library is not installed");

    /// <summary>Test seam: any ADLX library, real or faked.</summary>
    internal static AmdOpening Open(AdlxLibrary library)
    {
        var started = library.Join(out var system);
        if (!Succeeded(started) || system == 0) return WouldNotStart($"ADLX would not start (result {started})");

        nint list = 0, gpu = 0, services = 0, support = 0;
        var kept = false;
        try
        {
            // A library that answers yes and hands back nothing is a library that said no: nothing here is dereferenced
            // before it has been seen to be there.
            var listed = Ask(system, GetGpus, out list);
            if (!Succeeded(listed) || list == 0) return WouldNotStart($"ADLX would not list the graphics cards (result {listed})");

            gpu = FirstDiscrete(list, out var deviceId);
            if (gpu == 0) return new AmdOpening(AmdLibrary.NoDiscreteGpu, null, "no AMD discrete GPU");

            var opened = Ask(system, GetPerformanceMonitoringServices, out services);
            if (!Succeeded(opened) || services == 0) return WouldNotStart($"ADLX would not open performance monitoring (result {opened})");

            var supported = Ask(services, GetSupportedGpuMetrics, gpu, out support);
            if (!Succeeded(supported) || support == 0) return WouldNotStart($"ADLX would not say what the card measures (result {supported})");

            // The whole board where the card measures that, else the chip alone, which the power model marks up.
            var (metric, scope) = Chosen(support);
            kept = true;
            return new AmdOpening(AmdLibrary.Reading, new Adlx(library, gpu, services, metric, scope, deviceId), null);
        }
        finally
        {
            if (support != 0) Let(support);
            if (list != 0) Let(list);
            if (!kept)
            {
                if (services != 0) Let(services);
                if (gpu != 0) Let(gpu);
                library.Leave(lost: false);
            }
        }
    }

    public AmdReading Read()
    {
        if (_closed || _metric == NoMetric) return new AmdReading(null, _scope, Lost: _closed);

        var asked = Ask(_services, GetCurrentGpuMetrics, _gpu, out var metrics);
        if (!Succeeded(asked) || metrics == 0) return Missing(asked);
        try
        {
            var read = AskDouble(metrics, _metric, out var watts);
            if (!Succeeded(read)) return Missing(read);

            // A card that answers with nonsense is a card that did not answer: the power model then estimates instead.
            return new AmdReading(double.IsFinite(watts) && watts >= 0 ? watts : null, _scope, Lost: false);
        }
        finally
        {
            Let(metrics);
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Let(_services);
        Let(_gpu);
        _library.Leave(_lost);
    }

    /// <summary>ADLX_SUCCEEDED: only these three results mean the call worked.</summary>
    private static bool Succeeded(int result) => result is Ok or AlreadyEnabled or AlreadyInitialized;

    private static AmdOpening WouldNotStart(string reason) => new(AmdLibrary.WouldNotStart, null, reason);

    /// <summary>The first card ADLX calls discrete. Graphics inside the processor are released and passed over, because
    /// the processor's own package reading already covers what they draw.</summary>
    private static nint FirstDiscrete(nint list, out string? deviceId)
    {
        deviceId = null;
        for (var index = Count(list, Begin); index < Count(list, End); index++)
        {
            if (!Succeeded(AskAt(list, AtGpuList, index, out var gpu)) || gpu == 0) continue;
            if (Succeeded(AskInt(gpu, GpuType, out var type)) && type == Discrete)
            {
                if (Succeeded(AskText(gpu, PnpString, out var pnp))) deviceId = pnp;
                return gpu;
            }
            Let(gpu);
        }
        return 0;
    }

    /// <summary>The board reading where the card supports it, else the chip reading, else nothing to read.</summary>
    private static (int Metric, GpuPowerScope Scope) Chosen(nint support)
    {
        if (Succeeded(AskBool(support, IsSupportedGpuTotalBoardPower, out var board)) && board)
        {
            return (GpuTotalBoardPower, GpuPowerScope.Board);
        }
        if (Succeeded(AskBool(support, IsSupportedGpuPower, out var chip)) && chip)
        {
            return (GpuPower, GpuPowerScope.ChipOnly);
        }
        return (NoMetric, GpuPowerScope.Board);
    }

    /// <summary>A reading that did not arrive, and whether the library itself has gone with it.</summary>
    private AmdReading Missing(int result)
    {
        var lost = result is Terminated or InvalidObject or OrphanObjects;
        _lost |= lost;
        return new AmdReading(null, _scope, lost);
    }

    private static void Let(nint instance) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(instance, Release))(instance);

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];

    private static uint Count(nint instance, int slot)
        => ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, slot))(instance);

    private static int Ask(nint instance, int slot, out nint value)
    {
        nint returned = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, slot))(instance, &returned);
        value = returned;
        return result;
    }

    private static int Ask(nint instance, int slot, nint argument, out nint value)
    {
        nint returned = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(instance, slot))(instance, argument, &returned);
        value = returned;
        return result;
    }

    private static int AskAt(nint instance, int slot, uint index, out nint value)
    {
        nint returned = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(instance, slot))(instance, index, &returned);
        value = returned;
        return result;
    }

    private static int AskInt(nint instance, int slot, out int value)
    {
        int returned = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(instance, slot))(instance, &returned);
        value = returned;
        return result;
    }

    private static int AskDouble(nint instance, int slot, out double value)
    {
        double returned = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, double*, int>)Slot(instance, slot))(instance, &returned);
        value = returned;
        return result;
    }

    /// <summary>adlx_bool is one byte, not four: a wider read would take whatever follows it for the answer.</summary>
    private static int AskBool(nint instance, int slot, out bool value)
    {
        byte returned = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(instance, slot))(instance, &returned);
        value = returned != 0;
        return result;
    }

    /// <summary>ADLX lends the text for as long as the interface lives, so it is copied here and then owned by us.</summary>
    private static int AskText(nint instance, int slot, out string? value)
    {
        nint returned = 0;
        var result = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(instance, slot))(instance, &returned);
        value = returned == 0 ? null : Marshal.PtrToStringAnsi(returned);
        return result;
    }
}

/// <summary>
/// The ADLX library itself: the two functions that start and stop it, and the one session a process may have open.
/// ADLX is started and stopped for the whole process, not for each caller, so the readers share one session and the
/// last one out stops it. A session the library has lost is never stopped while another reader still holds interfaces
/// from it, because stopping it would leave those pointing at memory that is no longer theirs.
/// </summary>
internal sealed unsafe class AdlxLibrary
{
    /// <summary>ADLX_FULL_VERSION for SDK 2.0.0.125: the major, minor, release and build numbers packed into 16 bits
    /// each. ADLX keeps older callers working, and answers a caller that asks for more than the installed driver has
    /// with the part it does have.</summary>
    private const ulong Version = (2UL << 48) | (0UL << 32) | (0UL << 16) | 125UL;

    /// <summary>ADLX_OK, ADLX_ALREADY_ENABLED and ADLX_ALREADY_INITIALIZED, which ADLX_SUCCEEDED counts as success, and
    /// the two results this class gives of its own: ADLX_FAIL for a library that said yes but handed back nothing, and
    /// ADLX_ORPHAN_OBJECTS while a lost session's readers still hold its interfaces.</summary>
    private const int Ok = 0, AlreadyEnabled = 1, AlreadyInitialized = 2, Fail = 3, OrphanObjects = 11;

    private static readonly Lazy<AdlxLibrary?> Found = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly delegate* unmanaged[Cdecl]<ulong, nint*, int> _initialize;
    private readonly delegate* unmanaged[Cdecl]<int> _terminate;
    private readonly Lock _gate = new();
    private nint _system;
    private int _readers;
    private bool _lost;

    internal AdlxLibrary(delegate* unmanaged[Cdecl]<ulong, nint*, int> initialize, delegate* unmanaged[Cdecl]<int> terminate)
    {
        _initialize = initialize;
        _terminate = terminate;
    }

    /// <summary>The installed library, or null when this machine has no AMD driver or too old a one.</summary>
    public static AdlxLibrary? Installed => Found.Value;

    /// <summary>Starts ADLX, or joins the session this process already has. Returns the ADLX result.</summary>
    public int Join(out nint system)
    {
        lock (_gate)
        {
            if (_readers > 0 && !_lost)
            {
                system = _system;
                _readers++;
                return Ok;
            }

            // A lost session still has readers holding its interfaces, so a new one has to wait for them to finish.
            if (_readers > 0)
            {
                system = 0;
                return OrphanObjects;
            }

            nint started = 0;
            var result = _initialize(Version, &started);
            system = started;
            if (result is not (Ok or AlreadyEnabled or AlreadyInitialized) || started == 0) return result == Ok ? Fail : result;
            _system = started;
            _readers = 1;
            _lost = false;
            return Ok;
        }
    }

    /// <summary>A reader that has let go of every interface it took. The last one out stops ADLX.</summary>
    public void Leave(bool lost)
    {
        lock (_gate)
        {
            if (_readers == 0) return;
            _lost |= lost;
            if (--_readers > 0) return;
            _terminate();
            _system = 0;
            _lost = false;
        }
    }

    private static AdlxLibrary? Load()
    {
        // System32 and nowhere else: a service running as LocalSystem must not be talked into loading someone else's DLL.
        if (!NativeLibrary.TryLoad("amdadlx64.dll", typeof(AdlxLibrary).Assembly, DllImportSearchPath.System32, out var library))
        {
            return null;
        }
        if (!NativeLibrary.TryGetExport(library, "ADLXInitialize", out var initialize)
            || !NativeLibrary.TryGetExport(library, "ADLXTerminate", out var terminate))
        {
            return null;
        }
        return new AdlxLibrary(
            (delegate* unmanaged[Cdecl]<ulong, nint*, int>)initialize, (delegate* unmanaged[Cdecl]<int>)terminate);
    }
}
