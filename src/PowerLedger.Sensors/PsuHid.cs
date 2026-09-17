using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerLedger.Sensors;

/// <summary>
/// A HID collection Windows lists: where it is, which device it belongs to, and how long its reports are. The lengths
/// count the report ID byte Windows puts first, so a device with 64-byte reports and no report IDs shows 65.
/// </summary>
internal sealed record HidCollection(string Path, ushort VendorId, ushort ProductId, int InputReportLength, int OutputReportLength);

/// <summary>The HID calls the power supply source makes, behind a seam so a test can stand in for Windows and the device.</summary>
internal interface IHidPort
{
    /// <summary>The collections present now whose ids <paramref name="wanted"/> accepts. Reading a collection's ids asks
    /// Windows for no access to the device and sends nothing to it, so it is safe whatever else has the device open.</summary>
    IReadOnlyList<HidCollection> Find(Func<ushort, ushort, bool> wanted);

    /// <summary>Opens a collection to read and write, shared with whatever else has it open; null when Windows refuses.</summary>
    IHidLink? Open(HidCollection collection);
}

/// <summary>An open HID collection. One thread uses it at a time: the sensor thread that opened it.</summary>
internal interface IHidLink : IDisposable
{
    /// <summary>Drops the input reports already waiting, so the next report read answers what is written next.</summary>
    void Flush();

    /// <summary>Sends one output report, report ID first. False when Windows refused it or it wasn't taken in time.</summary>
    bool Write(byte[] report, TimeSpan timeout);

    /// <summary>The next input report, report ID first; null when none came in time.</summary>
    byte[]? Read(TimeSpan timeout);
}

/// <summary>
/// Windows' own HID: the device interfaces SetupAPI lists, opened through hid.dll. Reports are read and written with
/// overlapped I/O and a timeout of the caller's choosing, because a power supply that has stopped answering must cost
/// half a second and not a tick. No driver is installed and nothing is written to any device by this class itself.
/// </summary>
internal sealed class WindowsHid : IHidPort
{
    private const uint DigcfPresent = 0x02;
    private const uint DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x01 | 0x02;
    private const uint OpenExisting = 3;
    private const uint Overlapped = 0x40000000;
    private const int HidpStatusSuccess = 0x00110000;
    private const int ErrorIoPending = 997;

    public IReadOnlyList<HidCollection> Find(Func<ushort, ushort, bool> wanted)
    {
        var found = new List<HidCollection>();
        HidD_GetHidGuid(out var hid);
        var set = SetupDiGetClassDevsW(ref hid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) return found;
        try
        {
            var entry = new DeviceInterfaceData { Size = (uint)Marshal.SizeOf<DeviceInterfaceData>() };
            for (uint index = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, index, ref entry); index++)
            {
                if (PathOf(set, ref entry) is { } path && Describe(path, wanted) is { } collection) found.Add(collection);
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return found;
    }

    public IHidLink? Open(HidCollection collection)
    {
        var handle = CreateFileW(
            collection.Path, GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, Overlapped, IntPtr.Zero);
        if (!handle.IsInvalid) return new Link(handle, collection.InputReportLength, collection.OutputReportLength);
        handle.Dispose();
        return null;
    }

    /// <summary>The device path of one interface. SetupAPI is asked how long it is and then for the path itself.</summary>
    private static string? PathOf(IntPtr set, ref DeviceInterfaceData entry)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref entry, IntPtr.Zero, 0, out var size, IntPtr.Zero);
        if (size == 0) return null;
        var detail = Marshal.AllocHGlobal((int)size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W is a size and then the path. The size is the structure's own, which is
            // eight bytes where a pointer is eight and six where it is four, and not the length of the buffer.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            return SetupDiGetDeviceInterfaceDetailW(set, ref entry, detail, size, out _, IntPtr.Zero)
                ? Marshal.PtrToStringUni(detail + 4)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    /// <summary>The ids and report lengths of one collection, from a handle that asks for no access at all.</summary>
    private static HidCollection? Describe(string path, Func<ushort, ushort, bool> wanted)
    {
        using var handle = CreateFileW(path, 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var attributes = new HidAttributes { Size = (uint)Marshal.SizeOf<HidAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes)) return null;
        if (!wanted(attributes.VendorId, attributes.ProductId)) return null;
        if (!HidD_GetPreparsedData(handle, out var parsed)) return null;
        try
        {
            if (HidP_GetCaps(parsed, out var caps) != HidpStatusSuccess) return null;
            return new HidCollection(
                path, attributes.VendorId, attributes.ProductId, caps.InputReportByteLength, caps.OutputReportByteLength);
        }
        finally
        {
            HidD_FreePreparsedData(parsed);
        }
    }

    /// <summary>One open collection, with a buffer and an event of its own for the transfer in hand.</summary>
    private sealed class Link : IHidLink
    {
        private readonly SafeFileHandle _handle;
        private readonly EventWaitHandle _done = new(false, EventResetMode.ManualReset);
        private readonly IntPtr _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        private readonly IntPtr _buffer;
        private readonly int _inputLength;
        private readonly int _bufferLength;

        public Link(SafeFileHandle handle, int inputLength, int outputLength)
        {
            _handle = handle;
            _inputLength = inputLength;
            _bufferLength = Math.Max(inputLength, outputLength);
            _buffer = Marshal.AllocHGlobal(Math.Max(_bufferLength, 1));
        }

        public void Flush() => HidD_FlushQueue(_handle);

        public bool Write(byte[] report, TimeSpan timeout)
        {
            if (report.Length > _bufferLength) return false;
            Marshal.Copy(report, 0, _buffer, report.Length);
            return Transfer(write: true, report.Length, timeout) == report.Length;
        }

        public byte[]? Read(TimeSpan timeout)
        {
            var read = Transfer(write: false, _inputLength, timeout);
            if (read <= 0) return null;
            var report = new byte[read];
            Marshal.Copy(_buffer, report, 0, read);
            return report;
        }

        public void Dispose()
        {
            // Nothing is ever left in flight: every transfer has been waited out, cancelled and all, before this.
            _handle.Dispose();
            _done.Dispose();
            Marshal.FreeHGlobal(_overlapped);
            Marshal.FreeHGlobal(_buffer);
        }

        /// <summary>One overlapped transfer, waited out or cancelled; the bytes moved, or -1 when it didn't finish.</summary>
        private int Transfer(bool write, int length, TimeSpan timeout)
        {
            _done.Reset();
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _done.SafeWaitHandle.DangerousGetHandle() }, _overlapped, false);
            var started = write
                ? WriteFile(_handle, _buffer, length, IntPtr.Zero, _overlapped)
                : ReadFile(_handle, _buffer, length, IntPtr.Zero, _overlapped);
            if (!started)
            {
                if (Marshal.GetLastPInvokeError() != ErrorIoPending) return -1;
                if (!_done.WaitOne(timeout))
                {
                    // Windows owns the buffer until the cancelled transfer has finished, so it is waited for here.
                    CancelIoEx(_handle, _overlapped);
                    GetOverlappedResult(_handle, _overlapped, out _, wait: true);
                    return -1;
                }
            }

            return GetOverlappedResult(_handle, _overlapped, out var moved, wait: false) ? moved : -1;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClass;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public uint Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort Version;
    }

    /// <summary>HIDP_CAPS is 64 bytes; only the report lengths are read from it.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct HidCaps
    {
        [FieldOffset(4)] public ushort InputReportByteLength;
        [FieldOffset(6)] public ushort OutputReportByteLength;
    }

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(SafeFileHandle device, ref HidAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidCaps capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FlushQueue(SafeFileHandle device);

    [DllImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr set, IntPtr deviceInfo, ref Guid interfaceClass, uint index, ref DeviceInterfaceData entry);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr set, ref DeviceInterfaceData entry, IntPtr detail, uint detailSize, out uint requiredSize, IntPtr deviceInfo);

    [DllImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(
        string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle file, IntPtr buffer, int count, IntPtr read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle file, IntPtr buffer, int count, IntPtr written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(
        SafeFileHandle file, IntPtr overlapped, out int moved, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
}
