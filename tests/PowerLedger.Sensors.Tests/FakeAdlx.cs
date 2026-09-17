using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PowerLedger.Sensors;

namespace PowerLedger.Sensors.Tests;

/// <summary>One graphics card as the fake ADLX describes it.</summary>
internal sealed class FakeAdlxGpu
{
    /// <summary>ADLX_GPU_TYPE: 1 is integrated, 2 discrete.</summary>
    public int Type { get; init; } = 2;

    public string Pnp { get; init; } = @"PCI\VEN_1002&DEV_747E&SUBSYS_00000000&REV_C8\4&1A2B3C4D&0&0008A";

    public bool MeasuresBoard { get; init; }

    public bool MeasuresChip { get; init; }

    public double BoardWatts { get; set; } = 142.5;

    public double ChipWatts { get; set; } = 118;
}

/// <summary>
/// ADLX as a set of objects in unmanaged memory, each one a pointer to a table of function pointers, exactly as the
/// real library hands them out. The tables are laid out by the method names below, taken in the order the ADLX SDK
/// headers declare them, so a source that reached for the wrong slot lands on <see cref="Unexpected"/> instead of on
/// the method it wanted. Every interface handed out is counted, so a test can insist they are all given back.
/// </summary>
internal sealed unsafe class FakeAdlx : IDisposable
{
    /// <summary>ISystem.h, IADLXSystem. It is a singleton, so it has no Acquire, Release or reference count.</summary>
    private static readonly string[] SystemMethods =
    [
        "GetHybridGraphicsType", "GetGPUs", "QueryInterface", "GetDisplaysServices", "GetDesktopsServices",
        "GetGPUsChangedHandling", "EnableLog", "Get3DSettingsServices", "GetGPUTuningServices",
        "GetPerformanceMonitoringServices", "TotalSystemRAM", "GetI2C",
    ];

    /// <summary>ISystem.h, IADLXGPUList: IADLXInterface, then ICollections.h's IADLXList, then its own two.</summary>
    private static readonly string[] GpuListMethods =
    [
        "Acquire", "Release", "QueryInterface",
        "Size", "Empty", "Begin", "End", "At", "Clear", "Remove_Back", "Add_Back",
        "At_GPUList", "Add_Back_GPUList",
    ];

    /// <summary>ISystem.h, IADLXGPU.</summary>
    private static readonly string[] GpuMethods =
    [
        "Acquire", "Release", "QueryInterface",
        "VendorId", "ASICFamilyType", "Type", "IsExternal", "Name", "DriverPath", "PNPString", "HasDesktops",
        "TotalVRAM", "VRAMType", "BIOSInfo", "DeviceId", "RevisionId", "SubSystemId", "SubSystemVendorId", "UniqueId",
    ];

    /// <summary>IPerformanceMonitoring.h, IADLXPerformanceMonitoringServices.</summary>
    private static readonly string[] ServicesMethods =
    [
        "Acquire", "Release", "QueryInterface",
        "GetSamplingIntervalRange", "SetSamplingInterval", "GetSamplingInterval",
        "GetMaxPerformanceMetricsHistorySizeRange", "SetMaxPerformanceMetricsHistorySize",
        "GetMaxPerformanceMetricsHistorySize", "ClearPerformanceMetricsHistory",
        "GetCurrentPerformanceMetricsHistorySize", "StartPerformanceMetricsTracking",
        "StopPerformanceMetricsTracking", "GetAllMetricsHistory", "GetGPUMetricsHistory", "GetSystemMetricsHistory",
        "GetFPSHistory", "GetCurrentAllMetrics", "GetCurrentGPUMetrics", "GetCurrentSystemMetrics", "GetCurrentFPS",
        "GetSupportedGPUMetrics", "GetSupportedSystemMetrics",
    ];

    /// <summary>IPerformanceMonitoring.h, IADLXGPUMetricsSupport.</summary>
    private static readonly string[] SupportMethods =
    [
        "Acquire", "Release", "QueryInterface",
        "IsSupportedGPUUsage", "IsSupportedGPUClockSpeed", "IsSupportedGPUVRAMClockSpeed", "IsSupportedGPUTemperature",
        "IsSupportedGPUHotspotTemperature", "IsSupportedGPUPower", "IsSupportedGPUTotalBoardPower",
        "IsSupportedGPUFanSpeed", "IsSupportedGPUVRAM", "IsSupportedGPUVoltage",
        "GetGPUUsageRange", "GetGPUClockSpeedRange", "GetGPUVRAMClockSpeedRange", "GetGPUTemperatureRange",
        "GetGPUHotspotTemperatureRange", "GetGPUPowerRange", "GetGPUFanSpeedRange", "GetGPUVRAMRange",
        "GetGPUVoltageRange", "GetGPUTotalBoardPowerRange", "GetGPUIntakeTemperatureRange",
        "IsSupportedGPUIntakeTemperature",
    ];

    /// <summary>IPerformanceMonitoring.h, IADLXGPUMetrics.</summary>
    private static readonly string[] MetricsMethods =
    [
        "Acquire", "Release", "QueryInterface",
        "TimeStamp", "GPUUsage", "GPUClockSpeed", "GPUVRAMClockSpeed", "GPUTemperature", "GPUHotspotTemperature",
        "GPUPower", "GPUTotalBoardPower", "GPUFanSpeed", "GPUVRAM", "GPUVoltage", "GPUIntakeTemperature",
    ];

    [ThreadStatic]
    private static FakeAdlx? _live;

    private readonly List<Handle> _handles = [];
    private readonly List<nint> _memory = [];
    private readonly List<nint> _text = [];
    private readonly IReadOnlyList<FakeAdlxGpu> _gpus;
    private nint _system;

    public FakeAdlx(params FakeAdlxGpu[] gpus)
    {
        _gpus = gpus;
        _live = this;
    }

    /// <summary>The result ADLXInitialize gives.</summary>
    public int StartResult { get; set; }

    /// <summary>The result IADLXSystem::GetGPUs gives.</summary>
    public int ListResult { get; set; }

    /// <summary>The result IADLXSystem::GetPerformanceMonitoringServices gives.</summary>
    public int ServicesResult { get; set; }

    /// <summary>The result IADLXPerformanceMonitoringServices::GetCurrentGPUMetrics gives.</summary>
    public int ReadResult { get; set; }

    /// <summary>The result the card's own power method gives.</summary>
    public int PowerResult { get; set; }

    /// <summary>How many times ADLX was started and stopped.</summary>
    public int Starts { get; private set; }

    public int Stops { get; private set; }

    /// <summary>How many times the current metrics were asked for.</summary>
    public int Reads { get; private set; }

    /// <summary>The version the caller asked ADLX for.</summary>
    public ulong AskedVersion { get; private set; }

    /// <summary>True once a call landed on a slot this fake does not implement, which means a wrong slot number.</summary>
    public bool CalledAnUnknownMethod { get; private set; }

    /// <summary>Interfaces handed out and not yet released.</summary>
    public int OutstandingInterfaces => _handles.Count(h => h.Kind != Kind.System && h.References > 0);

    public AdlxLibrary Library => new(&Start, &Stop);

    public void Dispose()
    {
        foreach (var handle in _handles) handle.Free();
        _handles.Clear();
        foreach (var block in _memory) NativeMemory.Free((void*)block);
        _memory.Clear();
        foreach (var block in _text) Marshal.FreeHGlobal(block);
        _text.Clear();
        if (ReferenceEquals(_live, this)) _live = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Start(ulong version, nint* system)
    {
        var fake = Live();
        fake.AskedVersion = version;
        if (fake.StartResult != 0)
        {
            *system = 0;
            return fake.StartResult;
        }
        fake.Starts++;
        fake._system = fake.Make(Kind.System, SystemMethods, null, counted: false);
        *system = fake._system;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Stop()
    {
        Live().Stops++;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetGpus(nint self, nint* list)
    {
        var fake = Of(self).Fake;
        if (fake.ListResult != 0) return fake.ListResult;
        *list = fake.Make(Kind.List, GpuListMethods, null);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetServices(nint self, nint* services)
    {
        var fake = Of(self).Fake;
        if (fake.ServicesResult != 0) return fake.ServicesResult;
        *services = fake.Make(Kind.Services, ServicesMethods, null);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Begin(nint self) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint End(nint self) => (uint)Of(self).Fake._gpus.Count;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AtGpuList(nint self, uint index, nint* gpu)
    {
        var fake = Of(self).Fake;
        if (index >= (uint)fake._gpus.Count) return 9;      // ADLX_NOT_FOUND
        *gpu = fake.Make(Kind.Gpu, GpuMethods, fake._gpus[(int)index]);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GpuType(nint self, int* type)
    {
        *type = Card(self).Type;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int PnpString(nint self, nint* text)
    {
        var handle = Of(self);
        *text = handle.Fake.Ansi(((FakeAdlxGpu)handle.State!).Pnp);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetSupport(nint self, nint gpu, nint* support)
    {
        var fake = Of(self).Fake;
        *support = fake.Make(Kind.Support, SupportMethods, Of(gpu).State);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetMetrics(nint self, nint gpu, nint* metrics)
    {
        var fake = Of(self).Fake;
        fake.Reads++;
        if (fake.ReadResult != 0) return fake.ReadResult;
        *metrics = fake.Make(Kind.Metrics, MetricsMethods, Of(gpu).State);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SupportsChip(nint self, byte* supported)
    {
        *supported = (byte)(Card(self).MeasuresChip ? 1 : 0);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SupportsBoard(nint self, byte* supported)
    {
        *supported = (byte)(Card(self).MeasuresBoard ? 1 : 0);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ChipWatts(nint self, double* watts)
    {
        var fake = Of(self).Fake;
        *watts = Card(self).ChipWatts;
        return fake.PowerResult;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int BoardWatts(nint self, double* watts)
    {
        var fake = Of(self).Fake;
        *watts = Card(self).BoardWatts;
        return fake.PowerResult;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Release(nint self)
    {
        var handle = Of(self);
        return --handle.References;
    }

    /// <summary>Every slot the source has no business calling lands here.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Unexpected(nint self)
    {
        Of(self).Fake.CalledAnUnknownMethod = true;
        return 3;                                           // ADLX_FAIL
    }

    private static FakeAdlx Live() => _live ?? throw new InvalidOperationException("no fake ADLX on this thread");

    private static Handle Of(nint instance) => (Handle)GCHandle.FromIntPtr(((nint*)instance)[1]).Target!;

    private static FakeAdlxGpu Card(nint instance) => (FakeAdlxGpu)Of(instance).State!;

    /// <summary>An interface: a pointer to its table of function pointers, then the handle that finds this fake again.</summary>
    private nint Make(Kind kind, string[] methods, object? state, bool counted = true)
    {
        var handle = new Handle(this, kind, state) { References = counted ? 1 : 0 };
        _handles.Add(handle);

        var instance = (nint*)NativeMemory.Alloc((nuint)(2 * sizeof(nint)));
        _memory.Add((nint)instance);
        instance[0] = Table(kind, methods);
        instance[1] = GCHandle.ToIntPtr(handle.Self);
        return (nint)instance;
    }

    /// <summary>A table with every slot filled by the method's place in the header's list.</summary>
    private nint Table(Kind kind, string[] methods)
    {
        var table = (nint*)NativeMemory.Alloc((nuint)(methods.Length * sizeof(nint)));
        _memory.Add((nint)table);
        for (var slot = 0; slot < methods.Length; slot++) table[slot] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Unexpected;

        if (kind is not Kind.System) Fill(table, methods, "Release", (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Release);
        switch (kind)
        {
            case Kind.System:
                Fill(table, methods, "GetGPUs", (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetGpus);
                Fill(table, methods, "GetPerformanceMonitoringServices", (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetServices);
                break;
            case Kind.List:
                Fill(table, methods, "Begin", (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Begin);
                Fill(table, methods, "End", (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&End);
                Fill(table, methods, "At_GPUList", (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&AtGpuList);
                break;
            case Kind.Gpu:
                Fill(table, methods, "Type", (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&GpuType);
                Fill(table, methods, "PNPString", (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&PnpString);
                break;
            case Kind.Services:
                Fill(table, methods, "GetSupportedGPUMetrics", (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetSupport);
                Fill(table, methods, "GetCurrentGPUMetrics", (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)&GetMetrics);
                break;
            case Kind.Support:
                Fill(table, methods, "IsSupportedGPUPower", (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&SupportsChip);
                Fill(table, methods, "IsSupportedGPUTotalBoardPower", (nint)(delegate* unmanaged[Stdcall]<nint, byte*, int>)&SupportsBoard);
                break;
            case Kind.Metrics:
                Fill(table, methods, "GPUPower", (nint)(delegate* unmanaged[Stdcall]<nint, double*, int>)&ChipWatts);
                Fill(table, methods, "GPUTotalBoardPower", (nint)(delegate* unmanaged[Stdcall]<nint, double*, int>)&BoardWatts);
                break;
        }
        return (nint)table;
    }

    private static void Fill(nint* table, string[] methods, string method, nint function)
        => table[Slot(methods, method)] = function;

    private static int Slot(string[] methods, string method)
    {
        var slot = Array.IndexOf(methods, method);
        if (slot < 0) throw new InvalidOperationException($"{method} is not one of the header's methods");
        return slot;
    }

    private nint Ansi(string text)
    {
        var block = Marshal.StringToHGlobalAnsi(text);
        _text.Add(block);
        return block;
    }

    private enum Kind
    {
        System,
        List,
        Gpu,
        Services,
        Support,
        Metrics,
    }

    private sealed class Handle
    {
        public Handle(FakeAdlx fake, Kind kind, object? state)
        {
            Fake = fake;
            Kind = kind;
            State = state;
            Self = GCHandle.Alloc(this);
        }

        public FakeAdlx Fake { get; }

        public Kind Kind { get; }

        public object? State { get; }

        public GCHandle Self { get; }

        public int References { get; set; }

        public void Free() => Self.Free();
    }
}
