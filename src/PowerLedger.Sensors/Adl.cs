using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PowerLedger.Contracts;

namespace PowerLedger.Sensors;

/// <summary>
/// AMD's older display library, atiadlxx.dll, which every AMD driver installs and which answers on drivers too old for
/// ADLX. The ADL2 half of it is used throughout, because an ADL2 caller has its own context and cannot disturb another
/// caller in the same process. The structures and sensor numbers come from adl_structures.h and adl_defines.h in AMD's
/// display-library repository.
/// </summary>
internal sealed unsafe class Adl : IAmdGpu
{
    /// <summary>ADL_OK. ADL's other success codes are warnings this source has no use for.</summary>
    private const int Ok = 0;

    /// <summary>ADL_ERR_NOT_INIT and ADL_ERR_INVALID_ADL_IDX: the context or the adapter has gone, so the library must
    /// be opened again rather than asked once more.</summary>
    private const int NotInitialised = -2, InvalidAdapter = -5;

    /// <summary>ADL_PMLOG_ASIC_POWER, the chip's draw in whole watts, and ADL_PMLOG_BOARD_POWER, the whole card's.</summary>
    private const int AsicPower = 23, BoardPower = 73;

    /// <summary>The ASIC family bits of adl_defines.h. A card counts as discrete when the driver calls it discrete,
    /// workstation, external or FireStream, and never when it calls it integrated or Fusion, which are the processor's
    /// own graphics and are already inside the processor's package reading.</summary>
    private const int AsicDiscrete = 1 << 0, AsicIntegrated = 1 << 1, AsicWorkstation = 1 << 2,
        AsicExternal = 1 << 4, AsicFusion = 1 << 5, AsicFireStream = 1 << 6;

    private const int DiscreteFamilies = AsicDiscrete | AsicWorkstation | AsicExternal | AsicFireStream;
    private const int IntegratedFamilies = AsicIntegrated | AsicFusion;

    private readonly AdlLibrary _library;
    private readonly nint _context;
    private readonly int _adapter;
    private readonly PmLogData* _log;
    private bool _closed;

    private Adl(AdlLibrary library, nint context, int adapter, string? deviceId)
    {
        _library = library;
        _context = context;
        _adapter = adapter;
        _log = (PmLogData*)NativeMemory.AllocZeroed((nuint)sizeof(PmLogData));
        DeviceId = deviceId;
    }

    public string? DeviceId { get; }

    /// <summary>ADL on this machine, opened on its first discrete Radeon. Never throws.</summary>
    public static AmdOpening Open()
        => AdlLibrary.Installed is { } library
            ? Open(library)
            : new AmdOpening(AmdLibrary.NotInstalled, null, "no AMD driver installed");

    /// <summary>Test seam: any ADL library, real or faked.</summary>
    internal static AmdOpening Open(AdlLibrary library)
    {
        var created = library.Create(out var context);
        if (created != Ok || context == 0) return WouldNotStart($"ADL would not start (result {created})");

        var kept = false;
        try
        {
            var counted = library.NumberOfAdapters(context, out var adapters);
            if (counted != Ok) return WouldNotStart($"ADL would not count the adapters (result {counted})");

            // A service in session 0 has been known to see no adapters at all, which a logon may yet put right, so an
            // empty list is not taken as an answer about this machine.
            if (adapters <= 0) return WouldNotStart("ADL listed no graphics adapters");

            var size = (nuint)adapters * (nuint)sizeof(AdapterInfo);
            var list = (AdapterInfo*)NativeMemory.AllocZeroed(size);
            try
            {
                var listed = library.AdapterInfo(context, (nint)list, (int)size);
                if (listed != Ok) return WouldNotStart($"ADL would not list the adapters (result {listed})");

                if (FirstDiscrete(library, context, list, adapters) is not { } card)
                {
                    return new AmdOpening(AmdLibrary.NoDiscreteGpu, null, "no AMD discrete GPU");
                }
                kept = true;
                return new AmdOpening(AmdLibrary.Reading, new Adl(library, context, card.Adapter, card.DeviceId), null);
            }
            finally
            {
                NativeMemory.Free(list);
            }
        }
        finally
        {
            if (!kept) library.Destroy(context);
        }
    }

    public AmdReading Read()
    {
        if (_closed) return new AmdReading(null, GpuPowerScope.Board, Lost: true);

        var read = _library.QueryPmLog(_context, _adapter, (nint)_log);
        if (read != Ok) return new AmdReading(null, GpuPowerScope.Board, Lost: read is NotInitialised or InvalidAdapter);

        // The whole card where the driver measures that, else the chip alone, which the power model marks up.
        if (Sensor(BoardPower) is { } board) return new AmdReading(board, GpuPowerScope.Board, Lost: false);
        if (Sensor(AsicPower) is { } chip) return new AmdReading(chip, GpuPowerScope.ChipOnly, Lost: false);
        return new AmdReading(null, GpuPowerScope.Board, Lost: false);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        NativeMemory.Free(_log);
        _library.Destroy(_context);
    }

    private static AmdOpening WouldNotStart(string reason) => new(AmdLibrary.WouldNotStart, null, reason);

    /// <summary>The first discrete AMD adapter in the list, with the Plug and Play instance id Windows gave it. One card
    /// can hold several adapter indices, one for each of its outputs, and the first of them answers for the card.</summary>
    private static (int Adapter, string? DeviceId)? FirstDiscrete(AdlLibrary library, nint context, AdapterInfo* list, int adapters)
    {
        for (var index = 0; index < adapters; index++)
        {
            var adapter = &list[index];

            // A bus number below zero is an adapter Windows remembers but no longer has.
            if (adapter->BusNumber < 0 || !IsAmd(adapter)) continue;
            if (library.AsicFamily(context, adapter->AdapterIndex, out var types, out var valids) != Ok) continue;

            var family = types & valids;
            if ((family & IntegratedFamilies) != 0 || (family & DiscreteFamilies) == 0) continue;
            return (adapter->AdapterIndex, Text(adapter->PnpString, AdapterInfo.MaxPath));
        }
        return null;
    }

    /// <summary>The vendor comes from the unique device id, "PCI_VEN_1002&amp;DEV_...", because ADL's own vendor field
    /// has been seen to hold the number Windows wrote in hexadecimal read as though it were decimal.</summary>
    private static bool IsAmd(AdapterInfo* adapter)
    {
        const uint Amd = 0x1002;
        var udid = Text(adapter->Udid, AdapterInfo.MaxPath) ?? "";
        var marker = udid.IndexOf("PCI_VEN_", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0 && uint.TryParse(udid.AsSpan(marker + 8, Math.Min(4, udid.Length - marker - 8)),
                System.Globalization.NumberStyles.HexNumber, null, out var vendor))
        {
            return vendor == Amd;
        }
        return adapter->VendorId == Amd;
    }

    /// <summary>A sensor's whole watts, or null where this card does not report it or reports nonsense.</summary>
    private double? Sensor(int sensor)
    {
        if (_log->Sensors[sensor * 2] == 0) return null;
        var value = _log->Sensors[(sensor * 2) + 1];
        return value >= 0 ? value : null;
    }

    private static string? Text(byte* text, int length)
    {
        var read = Marshal.PtrToStringAnsi((nint)text, length);
        var end = read?.IndexOf('\0') ?? -1;
        return end >= 0 ? read![..end] : read;
    }

    /// <summary>AdapterInfo of adl_structures.h, as a 64-bit Windows program lays it out: 1,572 bytes of eight integers
    /// and five fixed strings.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AdapterInfo
    {
        /// <summary>ADL_MAX_PATH, the length of every string ADL puts in this structure.</summary>
        public const int MaxPath = 256;

        public int Size;
        public int AdapterIndex;
        public fixed byte Udid[MaxPath];
        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorId;
        public fixed byte AdapterName[MaxPath];
        public fixed byte DisplayName[MaxPath];
        public int Present;
        public int Exist;
        public fixed byte DriverPath[MaxPath];
        public fixed byte DriverPathExt[MaxPath];
        public fixed byte PnpString[MaxPath];
        public int OsDisplayIndex;
    }

    /// <summary>ADLPMLogDataOutput of adl_structures.h: a size, then 256 sensors of an is-supported flag and a value,
    /// which is 2,052 bytes. The sensors are held as pairs of integers because a fixed array holds only numbers.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PmLogData
    {
        /// <summary>ADL_PMLOG_MAX_SENSORS.</summary>
        public const int MaxSensors = 256;

        public int Size;
        public fixed int Sensors[MaxSensors * 2];
    }
}

/// <summary>
/// The ADL library itself: the ADL2 functions this source calls, once they have been found in atiadlxx.dll. Each reader
/// makes its own context, which is what ADL2 is for, so two of them never tread on each other.
/// </summary>
internal sealed unsafe class AdlLibrary
{
    /// <summary>ADL2_Main_Control_Create's second argument: only the adapters that are physically there.</summary>
    private const int ConnectedAdaptersOnly = 1;

    private static readonly Lazy<AdlLibrary?> Found = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly delegate* unmanaged[Cdecl]<delegate* unmanaged[Stdcall]<int, nint>, int, nint*, int> _create;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _destroy;
    private readonly delegate* unmanaged[Cdecl]<nint, int*, int> _numberOfAdapters;
    private readonly delegate* unmanaged[Cdecl]<nint, nint, int, int> _adapterInfo;
    private readonly delegate* unmanaged[Cdecl]<nint, int, int*, int*, int> _asicFamily;
    private readonly delegate* unmanaged[Cdecl]<nint, int, nint, int> _queryPmLog;

    internal AdlLibrary(
        delegate* unmanaged[Cdecl]<delegate* unmanaged[Stdcall]<int, nint>, int, nint*, int> create,
        delegate* unmanaged[Cdecl]<nint, int> destroy,
        delegate* unmanaged[Cdecl]<nint, int*, int> numberOfAdapters,
        delegate* unmanaged[Cdecl]<nint, nint, int, int> adapterInfo,
        delegate* unmanaged[Cdecl]<nint, int, int*, int*, int> asicFamily,
        delegate* unmanaged[Cdecl]<nint, int, nint, int> queryPmLog)
    {
        _create = create;
        _destroy = destroy;
        _numberOfAdapters = numberOfAdapters;
        _adapterInfo = adapterInfo;
        _asicFamily = asicFamily;
        _queryPmLog = queryPmLog;
    }

    /// <summary>The installed library, or null where this machine has no AMD driver or one without these functions.</summary>
    public static AdlLibrary? Installed => Found.Value;

    /// <summary>ADL2_Main_Control_Create, with the allocator ADL insists on being given.</summary>
    public int Create(out nint context)
    {
        nint created = 0;
        var result = _create(&Allocate, ConnectedAdaptersOnly, &created);
        context = created;
        return result;
    }

    public int Destroy(nint context) => _destroy(context);

    public int NumberOfAdapters(nint context, out int adapters)
    {
        int counted = 0;
        var result = _numberOfAdapters(context, &counted);
        adapters = counted;
        return result;
    }

    public int AdapterInfo(nint context, nint list, int size) => _adapterInfo(context, list, size);

    public int AsicFamily(nint context, int adapter, out int types, out int valids)
    {
        int gotTypes = 0, gotValids = 0;
        var result = _asicFamily(context, adapter, &gotTypes, &gotValids);
        types = gotTypes;
        valids = gotValids;
        return result;
    }

    public int QueryPmLog(nint context, int adapter, nint log) => _queryPmLog(context, adapter, log);

    /// <summary>ADL_MAIN_MALLOC_CALLBACK. ADL uses it for the buffers it hands back, of which this source asks for none,
    /// so nothing allocated here is ever seen again; it is the plain allocator AMD's own samples pass.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Allocate(int size) => size <= 0 ? 0 : (nint)NativeMemory.Alloc((nuint)size);

    /// <summary>ADL's library for this process: atiadlxx.dll in a 64-bit one, atiadlxy.dll in a 32-bit one. ADL's
    /// structures hold no pointers, so the same layouts serve both.</summary>
    internal static string FileName(bool is64BitProcess) => is64BitProcess ? "atiadlxx.dll" : "atiadlxy.dll";

    private static AdlLibrary? Load()
    {
        // System32 and nowhere else: a service running as LocalSystem must not be talked into loading someone else's DLL.
        if (!NativeLibrary.TryLoad(FileName(Environment.Is64BitProcess), typeof(AdlLibrary).Assembly, DllImportSearchPath.System32, out var library))
        {
            return null;
        }
        if (!NativeLibrary.TryGetExport(library, "ADL2_Main_Control_Create", out var create)
            || !NativeLibrary.TryGetExport(library, "ADL2_Main_Control_Destroy", out var destroy)
            || !NativeLibrary.TryGetExport(library, "ADL2_Adapter_NumberOfAdapters_Get", out var adapters)
            || !NativeLibrary.TryGetExport(library, "ADL2_Adapter_AdapterInfo_Get", out var info)
            || !NativeLibrary.TryGetExport(library, "ADL2_Adapter_ASICFamilyType_Get", out var family)
            || !NativeLibrary.TryGetExport(library, "ADL2_New_QueryPMLogData_Get", out var pmLog))
        {
            return null;
        }
        return new AdlLibrary(
            (delegate* unmanaged[Cdecl]<delegate* unmanaged[Stdcall]<int, nint>, int, nint*, int>)create,
            (delegate* unmanaged[Cdecl]<nint, int>)destroy,
            (delegate* unmanaged[Cdecl]<nint, int*, int>)adapters,
            (delegate* unmanaged[Cdecl]<nint, nint, int, int>)info,
            (delegate* unmanaged[Cdecl]<nint, int, int*, int*, int>)family,
            (delegate* unmanaged[Cdecl]<nint, int, nint, int>)pmLog);
    }
}
