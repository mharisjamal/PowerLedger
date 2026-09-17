using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PowerLedger.Sensors;

/// <param name="Path">The device path <c>CreateFile</c> opens.</param>
/// <param name="Device">Which device the collection belongs to. A device that declares more than one top-level
/// collection gets one interface each, and they all carry the same device here, so one UPS is never counted as two.</param>
internal readonly record struct HidPath(string Path, string Device);

/// <param name="UsagePage">The collection's usage page; 0x84 is the power device page.</param>
/// <param name="Usage">The collection's usage, such as 0x1C for Output.</param>
/// <param name="Parent">The collection this one sits in; index 0 is the top-level collection.</param>
/// <param name="Type">The collection type byte: 0 is Physical, 1 Application, 2 Logical. A maker may use 0x80 and
/// above, which is how MGE numbers its Flow collections: NUT writes the output flow "UPS.Flow.[4]" for type 0x84.</param>
internal readonly record struct HidCollection(ushort UsagePage, ushort Usage, ushort Parent, byte Type);

/// <summary>
/// One value field of a feature report, as Windows' HID parser describes it. The raw bits a report carries mean
/// nothing without this: the field's sign, the range the device maps onto, and the power of ten its unit carries.
/// </summary>
internal sealed record HidValue
{
    /// <summary>The HID unit of a watt or a volt-ampere. HID's SI units are grams, centimetres and seconds, in which a
    /// watt is 10^7 g·cm²/s³, so a device reporting whole watts declares an exponent of 7.</summary>
    public const uint WattUnit = 0x0000D121;

    /// <summary>The HID unit of a volt, which carries the same 10^7.</summary>
    public const uint VoltUnit = 0x00F0D121;

    public required ushort UsagePage { get; init; }

    public required ushort Usage { get; init; }

    /// <summary>The report this field is in; 0 on a device that uses no report ids.</summary>
    public byte ReportId { get; init; }

    /// <summary>The link collection holding the field, as an index into the collection's tree.</summary>
    public ushort Collection { get; init; }

    public int LogicalMin { get; init; }

    public int LogicalMax { get; init; }

    public int PhysicalMin { get; init; }

    public int PhysicalMax { get; init; }

    /// <summary>The HID unit, e.g. <see cref="WattUnit"/>; 0 when the device declares none.</summary>
    public uint Units { get; init; }

    /// <summary>The unit's exponent as the descriptor encodes it, which <see cref="Physical"/> reads as a number.</summary>
    public uint UnitsExp { get; init; }

    public ushort BitSize { get; init; } = 16;

    /// <summary>How many fields the usage covers. Windows only reads single values, so anything else is left alone.</summary>
    public ushort ReportCount { get; init; } = 1;

    /// <summary>Whether the field has a null value, which is any value outside the logical range and means "no reading".</summary>
    public bool HasNull { get; init; }

    /// <summary>
    /// What the raw bits mean: the sign the logical range implies, the physical range where the device maps onto one,
    /// and the unit's exponent. Null when the field carries its null value.
    /// </summary>
    /// <remarks>
    /// A value outside the logical range is taken as sent unless the field has a null value, because the range is
    /// often the part a firmware gets wrong: NUT clamps to it and had to patch the clamp away for CyberPower units
    /// that report their rating above their own maximum (a 510 W EC850LCD declaring 450, NUT issue 2917).
    /// </remarks>
    public double? Physical(uint raw)
    {
        // A maximum below the minimum is a descriptor that ran a positive number into the sign bit, so the field is
        // read as the unsigned number it was meant to be and its range is not used at all.
        var ranged = LogicalMin <= LogicalMax;
        long value = raw;
        if (ranged && LogicalMin < 0 && BitSize is > 0 and <= 32 && (raw & (1u << (BitSize - 1))) != 0)
        {
            value = raw - (1L << BitSize);
        }

        if (HasNull && ranged && (value < LogicalMin || value > LogicalMax)) return null;

        // A physical range of nothing to nothing is the spec's way of saying there is none.
        double physical = value;
        if (ranged && LogicalMin < LogicalMax && PhysicalMin < PhysicalMax)
        {
            physical = ((value - (double)LogicalMin) * ((double)PhysicalMax - PhysicalMin) / ((double)LogicalMax - LogicalMin)) + PhysicalMin;
        }

        var result = physical * Math.Pow(10, Exponent);
        return double.IsFinite(result) ? result : null;
    }

    /// <summary>The power of ten the raw value carries.</summary>
    private int Exponent
    {
        get
        {
            // The exponent is a nibble in two's complement, which some devices widen to a whole byte; both read alike.
            var exponent = (int)(sbyte)(UnitsExp & 0xFF);
            if (exponent > 7) exponent -= 16;
            return Units is WattUnit or VoltUnit ? exponent - 7 : exponent;
        }
    }
}

/// <summary>The HID devices Windows knows, behind a seam so that tests can stand in for them.</summary>
internal interface IHid
{
    /// <summary>Every HID collection attached, whatever it is.</summary>
    IReadOnlyList<HidPath> Interfaces();

    /// <summary>Opens one collection for reading feature reports, or null when Windows will not open it.</summary>
    IHidCollection? Open(HidPath path);
}

/// <summary>One open HID collection: what its descriptor says, and the feature reports it answers with.</summary>
internal interface IHidCollection : IDisposable
{
    /// <summary>The device this collection belongs to; see <see cref="HidPath.Device"/>.</summary>
    string Device { get; }

    /// <summary>The top-level collection's usage page; 0x84 for a power device.</summary>
    ushort UsagePage { get; }

    /// <summary>The top-level collection's usage; 0x04 is a UPS and 0x24 a power summary.</summary>
    ushort Usage { get; }

    /// <summary>The collection tree, index 0 being the top-level collection itself.</summary>
    IReadOnlyList<HidCollection> Collections { get; }

    /// <summary>Every value field of the feature reports.</summary>
    IReadOnlyList<HidValue> Values { get; }

    /// <summary>What the device calls its maker, or null when it says nothing.</summary>
    string? Manufacturer { get; }

    /// <summary>What the device calls itself, or null when it says nothing.</summary>
    string? Product { get; }

    /// <summary>Reads one feature report by its id, or null when the device did not answer.</summary>
    byte[]? Feature(byte reportId);

    /// <summary>The raw bits of one field in a report just read, or null when that report does not carry it.</summary>
    uint? Raw(HidValue value, byte[] report);
}

/// <summary>
/// Reading HID feature reports, over the shared calls in <see cref="HidNative"/>: SetupAPI lists the collections and
/// the HID parser reads them. Nothing here writes to a device. The only call that reaches a device at all is
/// <c>HidD_GetFeature</c>, which asks it for a report, so PowerLedger can never change how a UPS behaves. A collection
/// is opened with no access at all, which cannot write whatever this class asked. Every method answers with null or
/// nothing rather than throwing when Windows declines, so the source above can decide what a missing answer means.
/// </summary>
internal sealed class WindowsHid : IHid
{
    public IReadOnlyList<HidPath> Interfaces() => HidNative.Interfaces();

    public IHidCollection? Open(HidPath path)
    {
        // Zero access asks only to read reports, which Windows allows beside its own UPS battery driver; a handle
        // that asked to read or write would be refused on the collections Windows keeps for itself.
        var handle = HidNative.Open(path.Path, access: 0, flags: 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var collection = Collection.From(handle, path.Device);
        if (collection is null) handle.Dispose();
        return collection;
    }

    /// <summary>One collection Windows has opened, with its descriptor read once and kept.</summary>
    private sealed class Collection : IHidCollection
    {
        private const int FeatureReports = 2;           // HidP_Feature
        private const int Success = HidNative.Success;  // HIDP_STATUS_SUCCESS

        private readonly SafeFileHandle _handle;
        private readonly HidNative.Caps _caps;
        private IntPtr _preparsed;
        private IReadOnlyList<HidCollection>? _collections;
        private IReadOnlyList<HidValue>? _values;
        private string? _manufacturer;
        private string? _product;
        private bool _named;

        private Collection(SafeFileHandle handle, IntPtr preparsed, HidNative.Caps caps, string device)
        {
            _handle = handle;
            _preparsed = preparsed;
            _caps = caps;
            Device = device;
        }

        public string Device { get; }

        public ushort UsagePage => _caps.UsagePage;

        public ushort Usage => _caps.Usage;

        public IReadOnlyList<HidCollection> Collections => _collections ??= ReadCollections();

        public IReadOnlyList<HidValue> Values => _values ??= ReadValues();

        public string? Manufacturer
        {
            get
            {
                ReadNames();
                return _manufacturer;
            }
        }

        public string? Product
        {
            get
            {
                ReadNames();
                return _product;
            }
        }

        public static Collection? From(SafeFileHandle handle, string device)
        {
            if (!HidNative.HidD_GetPreparsedData(handle, out var preparsed) || preparsed == IntPtr.Zero) return null;
            if (HidNative.HidP_GetCaps(preparsed, out var caps) != Success)
            {
                HidNative.HidD_FreePreparsedData(preparsed);
                return null;
            }
            return new Collection(handle, preparsed, caps, device);
        }

        public byte[]? Feature(byte reportId)
        {
            var length = _caps.FeatureReportByteLength;
            if (length < 2) return null;                // a report of one byte carries nothing but its id

            var report = new byte[length];
            report[0] = reportId;
            return HidNative.HidD_GetFeature(_handle, report, length) ? report : null;
        }

        public uint? Raw(HidValue value, byte[] report)
            => HidNative.HidP_GetUsageValue(FeatureReports, value.UsagePage, value.Collection, value.Usage, out var raw,
                _preparsed, report, (uint)report.Length) == Success ? raw : null;

        public void Dispose()
        {
            if (_preparsed != IntPtr.Zero)
            {
                HidNative.HidD_FreePreparsedData(_preparsed);
                _preparsed = IntPtr.Zero;
            }
            _handle.Dispose();
        }

        private IReadOnlyList<HidCollection> ReadCollections()
        {
            var count = (uint)_caps.NumberLinkCollectionNodes;
            if (count == 0) return [];

            var nodes = new HidNative.LinkCollectionNode[count];
            if (HidNative.HidP_GetLinkCollectionNodes(nodes, ref count, _preparsed) != Success) return [];

            var collections = new List<HidCollection>((int)count);
            for (var i = 0; i < count; i++)
            {
                collections.Add(new HidCollection(nodes[i].LinkUsagePage, nodes[i].LinkUsage, nodes[i].Parent, (byte)(nodes[i].Bits & 0xFF)));
            }
            return collections;
        }

        private IReadOnlyList<HidValue> ReadValues()
        {
            var count = _caps.NumberFeatureValueCaps;
            if (count == 0) return [];

            var caps = new HidNative.ValueCaps[count];
            if (HidNative.HidP_GetValueCaps(FeatureReports, caps, ref count, _preparsed) != Success) return [];

            var values = new List<HidValue>(count);
            for (var i = 0; i < count; i++)
            {
                // A cap over a range of usages describes one field per usage, so each becomes a field of its own.
                var cap = caps[i];
                var first = cap.Usage;
                var last = cap.IsRange != 0 ? cap.UsageMax : cap.Usage;
                if (last < first || last - first > 64) last = first;
                for (var usage = first; usage <= last; usage++)
                {
                    values.Add(new HidValue
                    {
                        UsagePage = cap.UsagePage,
                        Usage = usage,
                        ReportId = cap.ReportId,
                        Collection = cap.LinkCollection,
                        LogicalMin = cap.LogicalMin,
                        LogicalMax = cap.LogicalMax,
                        PhysicalMin = cap.PhysicalMin,
                        PhysicalMax = cap.PhysicalMax,
                        Units = cap.Units,
                        UnitsExp = cap.UnitsExp,
                        BitSize = cap.BitSize,
                        ReportCount = cap.IsRange != 0 ? (ushort)1 : cap.ReportCount,
                        HasNull = cap.HasNull != 0,
                    });
                }
            }
            return values;
        }

        /// <summary>Asks the device for its two names, once. A device that answers neither is left nameless.</summary>
        private void ReadNames()
        {
            if (_named) return;
            _named = true;
            _manufacturer = Text(HidNative.HidD_GetManufacturerString);
            _product = Text(HidNative.HidD_GetProductString);
        }

        private string? Text(Func<SafeFileHandle, byte[], uint, bool> read)
        {
            var buffer = new byte[256];                 // a USB string is at most 126 characters
            if (!read(_handle, buffer, (uint)buffer.Length)) return null;
            var text = Encoding.Unicode.GetString(buffer);
            var end = text.IndexOf('\0', StringComparison.Ordinal);
            return end < 0 ? text : text[..end];
        }
    }
}

/// <summary>
/// The Windows HID calls both power-device sources need, declared once so the two cannot drift apart over what Windows
/// was asked. SetupAPI lists the collections, hid.dll says what each one is and how long its reports are, and the file
/// calls carry the reports themselves. Nothing here says anything to a device of its own accord: the caller decides
/// what, if anything, goes out, and each source's own rules decide what it is allowed to send. Every call answers with
/// null, nothing or an invalid handle rather than throwing when Windows declines.
/// </summary>
internal static class HidNative
{
    /// <summary>HIDP_STATUS_SUCCESS, which a HidP call answers with when it worked.</summary>
    public const int Success = 0x00110000;

    public const uint GenericRead = 0x80000000;

    public const uint GenericWrite = 0x40000000;

    /// <summary>FILE_FLAG_OVERLAPPED: reads and writes that can be waited on for a while and then called off.</summary>
    public const uint Overlapped = 0x40000000;

    /// <summary>ERROR_IO_PENDING, which says an overlapped transfer has started rather than failed.</summary>
    public const int IoPending = 997;

    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint FileShareRead = 0x01;
    private const uint FileShareWrite = 0x02;
    private const uint OpenExisting = 3;

    /// <summary>GUID_DEVINTERFACE_HID, the interface every HID collection publishes.</summary>
    private static readonly Guid HidInterface = new(0x4d1e55b2, 0xf16f, 0x11cf, 0x88, 0xcb, 0x00, 0x11, 0x11, 0x00, 0x00, 0x30);

    /// <summary>Every HID collection attached, with the device each one hangs off.</summary>
    public static IReadOnlyList<HidPath> Interfaces()
    {
        var guid = HidInterface;
        var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return [];

        var found = new List<HidPath>();
        try
        {
            var element = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
            for (uint index = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref element); index++)
            {
                if (PathOf(set, ref element) is { } path) found.Add(path);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return found;
    }

    /// <summary>Opens a collection, shared with whatever else has it open. Zero access asks only to read reports, which
    /// Windows allows on collections it keeps for itself; read and write access is for a device that answers commands.
    /// The handle is invalid when Windows refuses, and is the caller's to dispose either way.</summary>
    public static SafeFileHandle Open(string path, uint access, uint flags)
        => CreateFileW(path, access, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);

    /// <summary>The vendor and product ids of the device an open collection belongs to; null when Windows won't say.
    /// Windows answers from what it already knows, so asking costs the device nothing.</summary>
    public static (ushort VendorId, ushort ProductId)? Ids(SafeFileHandle handle)
    {
        var attributes = new DeviceAttributes { Size = (uint)Marshal.SizeOf<DeviceAttributes>() };
        return HidD_GetAttributes(handle, ref attributes) ? (attributes.VendorId, attributes.ProductId) : null;
    }

    /// <summary>What an open collection's descriptor says, the parsed descriptor being freed again; null when Windows
    /// will not parse it. A caller that needs the descriptor itself keeps its own, as the UPS's collection does.</summary>
    public static Caps? Capabilities(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var preparsed) || preparsed == IntPtr.Zero) return null;
        try
        {
            return HidP_GetCaps(preparsed, out var caps) == Success ? caps : null;
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    private static HidPath? PathOf(IntPtr set, ref DeviceInterfaceData element)
    {
        SetupDiGetDeviceInterfaceDetailSize(set, ref element, IntPtr.Zero, 0, out var size, IntPtr.Zero);
        if (size is 0 or > 4096) return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W is a size followed by the path; the size is 8 where a pointer is 8.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            var info = new DevInfoData { Size = (uint)Marshal.SizeOf<DevInfoData>() };
            if (!SetupDiGetDeviceInterfaceDetailW(set, ref element, buffer, size, out _, ref info)) return null;
            return Marshal.PtrToStringUni(buffer + 4) is { Length: > 0 } path
                ? new HidPath(path, DeviceOf(info.DevInst) ?? path)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The device the collection hangs off, which its sibling collections share; null when Windows won't say.</summary>
    private static string? DeviceOf(uint node)
    {
        if (CM_Get_Parent(out var parent, node, 0) != 0) return null;
        var buffer = new char[200];                     // MAX_DEVICE_ID_LEN
        if (CM_Get_Device_IDW(parent, buffer, (uint)buffer.Length, 0) != 0) return null;
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClass;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevInfoData
    {
        public uint Size;
        public Guid Class;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    /// <summary>HIDD_ATTRIBUTES: which device the collection belongs to.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceAttributes
    {
        public uint Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort Version;
    }

    /// <summary>HIDP_CAPS; only the top-level usage, the report lengths and the counts are read. A report length counts
    /// the report ID byte Windows puts first, so a device with 64-byte reports and no report ids shows 65.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct Caps
    {
        [FieldOffset(0)] public ushort Usage;
        [FieldOffset(2)] public ushort UsagePage;
        [FieldOffset(4)] public ushort InputReportByteLength;
        [FieldOffset(6)] public ushort OutputReportByteLength;
        [FieldOffset(8)] public ushort FeatureReportByteLength;
        [FieldOffset(44)] public ushort NumberLinkCollectionNodes;
        [FieldOffset(60)] public ushort NumberFeatureValueCaps;
    }

    /// <summary>HIDP_VALUE_CAPS, whose last sixteen bytes are a union of the range and the single-usage forms.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct ValueCaps
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportId;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(16)] public byte HasNull;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(32)] public uint UnitsExp;
        [FieldOffset(36)] public uint Units;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(48)] public int PhysicalMin;
        [FieldOffset(52)] public int PhysicalMax;
        [FieldOffset(56)] public ushort Usage;          // Range.UsageMin where IsRange is set
        [FieldOffset(58)] public ushort UsageMax;
    }

    /// <summary>HIDP_LINK_COLLECTION_NODE; <see cref="Bits"/> holds the collection type in its lowest byte.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LinkCollectionNode
    {
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public ushort Parent;
        public ushort NumberOfChildren;
        public ushort NextSibling;
        public ushort FirstChild;
        public uint Bits;
        public IntPtr UserContext;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid interfaceClass, uint index, ref DeviceInterfaceData element);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailSize(IntPtr set, ref DeviceInterfaceData element, IntPtr detail, uint detailSize, out uint required, IntPtr deviceInfo);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref DeviceInterfaceData element, IntPtr detail, uint detailSize, out uint required, ref DevInfoData deviceInfo);

    [DllImport("setupapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_Device_IDW(uint node, char[] buffer, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int count, IntPtr read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, int count, IntPtr written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlapped, out int moved, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);

    // The HidD routines answer with a BOOLEAN, one byte, not the four-byte BOOL the marshaller assumes by default.
    [DllImport("hid.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref DeviceAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    /// <summary>Drops the input reports already waiting on a handle, so the next report read answers what is sent next.</summary>
    [DllImport("hid.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_FlushQueue(SafeFileHandle handle);

    [DllImport("hid.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] report, uint length);

    [DllImport("hid.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetManufacturerString(SafeFileHandle handle, byte[] buffer, uint length);

    [DllImport("hid.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool HidD_GetProductString(SafeFileHandle handle, byte[] buffer, uint length);

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int HidP_GetCaps(IntPtr preparsed, out Caps caps);

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int HidP_GetValueCaps(int reportType, [Out] ValueCaps[] caps, ref ushort length, IntPtr preparsed);

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int HidP_GetLinkCollectionNodes([Out] LinkCollectionNode[] nodes, ref uint length, IntPtr preparsed);

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort collection, ushort usage, out uint value, IntPtr preparsed, byte[] report, uint reportLength);
}
