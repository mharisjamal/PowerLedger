using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerLedger.Sensors;

/// <summary>
/// A HID device Windows lists, as a power supply needs to know it: where its collection is, which device it is, and how
/// long its reports are. The lengths count the report ID byte Windows puts first, so a device with 64-byte reports and
/// no report IDs shows 65.
/// </summary>
internal sealed record HidDevice(string Path, ushort VendorId, ushort ProductId, int InputReportLength, int OutputReportLength);

/// <summary>The HID calls a power supply makes, behind a seam so a test can stand in for Windows and the device.</summary>
internal interface IHidPort
{
    /// <summary>The devices present now whose ids <paramref name="wanted"/> accepts. Reading a device's ids asks Windows
    /// for no access to it and sends it nothing, so it is safe whatever else has the device open.</summary>
    IReadOnlyList<HidDevice> Find(Func<ushort, ushort, bool> wanted);

    /// <summary>Opens a device to read and write, shared with whatever else has it open; null when Windows refuses.</summary>
    IHidLink? Open(HidDevice device);
}

/// <summary>An open HID device. One thread uses it at a time: the sensor thread that opened it.</summary>
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
/// Windows' own HID, as a power supply uses it: the devices <see cref="HidNative"/> lists, matched by their ids and
/// opened for the commands such a supply answers. Reports are read and written with overlapped I/O and a timeout of the
/// caller's choosing, and a transfer that runs out of time is called off, because a supply that has stopped answering
/// must cost half a second and not a tick. No driver is installed, and this class puts nothing on the wire of its own
/// accord: what may be sent is for the caller's rules to decide.
/// </summary>
internal sealed class WindowsHidPort : IHidPort
{
    public IReadOnlyList<HidDevice> Find(Func<ushort, ushort, bool> wanted)
    {
        var found = new List<HidDevice>();
        foreach (var path in HidNative.Interfaces())
        {
            if (Describe(path.Path, wanted) is { } device) found.Add(device);
        }

        return found;
    }

    public IHidLink? Open(HidDevice device)
    {
        var handle = HidNative.Open(device.Path, HidNative.GenericRead | HidNative.GenericWrite, HidNative.Overlapped);
        if (!handle.IsInvalid) return new Link(handle, device.InputReportLength, device.OutputReportLength);
        handle.Dispose();
        return null;
    }

    /// <summary>The ids and report lengths of one device, from a handle that asks for no access at all.</summary>
    private static HidDevice? Describe(string path, Func<ushort, ushort, bool> wanted)
    {
        using var handle = HidNative.Open(path, access: 0, flags: 0);
        if (handle.IsInvalid) return null;
        if (HidNative.Ids(handle) is not { } ids || !wanted(ids.VendorId, ids.ProductId)) return null;
        if (HidNative.Capabilities(handle) is not { } caps) return null;
        return new HidDevice(path, ids.VendorId, ids.ProductId, caps.InputReportByteLength, caps.OutputReportByteLength);
    }

    /// <summary>One open device, with a buffer and an event of its own for the transfer in hand.</summary>
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

        public void Flush() => HidNative.HidD_FlushQueue(_handle);

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
            // Nothing is ever left in flight: every transfer has been waited out, called off and all, before this.
            _handle.Dispose();
            _done.Dispose();
            Marshal.FreeHGlobal(_overlapped);
            Marshal.FreeHGlobal(_buffer);
        }

        /// <summary>One overlapped transfer, waited out or called off; the bytes moved, or -1 when it didn't finish.</summary>
        private int Transfer(bool write, int length, TimeSpan timeout)
        {
            _done.Reset();
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _done.SafeWaitHandle.DangerousGetHandle() }, _overlapped, false);
            var started = write
                ? HidNative.WriteFile(_handle, _buffer, length, IntPtr.Zero, _overlapped)
                : HidNative.ReadFile(_handle, _buffer, length, IntPtr.Zero, _overlapped);
            if (!started)
            {
                if (Marshal.GetLastPInvokeError() != HidNative.IoPending) return -1;
                if (!_done.WaitOne(timeout))
                {
                    // Windows owns the buffer until the transfer it was told to call off has finished, so it is waited for.
                    HidNative.CancelIoEx(_handle, _overlapped);
                    HidNative.GetOverlappedResult(_handle, _overlapped, out _, wait: true);
                    return -1;
                }
            }

            return HidNative.GetOverlappedResult(_handle, _overlapped, out var moved, wait: false) ? moved : -1;
        }
    }
}
