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

/// <summary>How one overlapped transfer ended.</summary>
internal enum Transferred
{
    /// <summary>It finished of its own accord, in time.</summary>
    Finished,

    /// <summary>It ran out of time and the device finished the cancel, so its buffer is ours again.</summary>
    CalledOff,

    /// <summary>It ran out of time and did not even finish the cancel, so Windows may still own its buffer.</summary>
    Abandoned,
}

/// <summary>
/// A block of unmanaged memory a transfer reads and writes. It is freed when the link lets go of it, and by the
/// finalizer a <see cref="SafeHandle"/> brings when the link is dropped without being closed; a block Windows may
/// still be writing into is abandoned instead, which leaks it on purpose rather than freeing it under the kernel.
/// </summary>
internal sealed class NativeBlock : SafeHandle
{
    public NativeBlock(int bytes) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(Marshal.AllocHGlobal(Math.Max(bytes, 1)));

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>The address itself, for the calls that take a raw pointer.</summary>
    public IntPtr Address => handle;

    /// <summary>Lets go of the block without freeing it, for when a transfer into it was never finished: a few bytes
    /// leaked for the life of the process are better than a heap the kernel writes into after we have handed it back.</summary>
    public void Abandon() => SetHandleAsInvalid();

    protected override bool ReleaseHandle()
    {
        Marshal.FreeHGlobal(handle);
        return true;
    }
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
    /// <summary>How long a called-off transfer is waited for. Windows finishes a cancel in microseconds when the
    /// driver is well; one that has wedged would otherwise hold the sensor thread for the life of the process.</summary>
    public static readonly TimeSpan CancelWait = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Waits out one overlapped transfer, calling it off when it runs out of time. Windows owns the transfer's buffer
    /// until a cancelled transfer has finished, so the cancel is waited for too — but only for <see cref="CancelWait"/>,
    /// never for ever: a sensor thread must always come back, whatever a device's driver is doing.
    /// </summary>
    /// <param name="wait">Waits on the event the transfer signals; false when it was not signalled in time.</param>
    /// <param name="cancel">Tells Windows to call the transfer off.</param>
    /// <param name="timeout">How long the transfer itself may take.</param>
    public static Transferred Settle(Func<TimeSpan, bool> wait, Action cancel, TimeSpan timeout)
    {
        if (wait(timeout)) return Transferred.Finished;
        cancel();
        return wait(CancelWait) ? Transferred.CalledOff : Transferred.Abandoned;
    }

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

    /// <summary>
    /// One open device, with a buffer and an event of its own for the transfer in hand. Everything unmanaged it holds
    /// is behind a handle that frees itself, so a link dropped without being closed costs nothing more than a
    /// collection; a link whose device wedged mid-transfer keeps its buffers for good instead, since Windows may still
    /// be writing into them, and refuses every transfer after that.
    /// </summary>
    private sealed class Link : IHidLink
    {
        private readonly SafeFileHandle _handle;
        private readonly EventWaitHandle _done = new(false, EventResetMode.ManualReset);
        private readonly NativeBlock _overlapped = new(Marshal.SizeOf<NativeOverlapped>());
        private readonly NativeBlock _buffer;
        private readonly int _inputLength;
        private readonly int _bufferLength;
        private bool _wedged;

        public Link(SafeFileHandle handle, int inputLength, int outputLength)
        {
            _handle = handle;
            _inputLength = inputLength;
            _bufferLength = Math.Max(inputLength, outputLength);
            _buffer = new NativeBlock(_bufferLength);
        }

        public void Flush() => HidNative.HidD_FlushQueue(_handle);

        public bool Write(byte[] report, TimeSpan timeout)
        {
            if (_wedged || report.Length > _bufferLength) return false;
            Marshal.Copy(report, 0, _buffer.Address, report.Length);
            return Transfer(write: true, report.Length, timeout) == report.Length;
        }

        public byte[]? Read(TimeSpan timeout)
        {
            var read = Transfer(write: false, _inputLength, timeout);
            if (read <= 0) return null;
            var report = new byte[read];
            Marshal.Copy(_buffer.Address, report, 0, read);
            return report;
        }

        public void Dispose()
        {
            // Closing the handle is what tells Windows to finish anything still in flight on it.
            _handle.Dispose();
            if (_wedged) return;

            // A wedged device keeps its event and its blocks: they are what a transfer Windows never finished points at.
            _done.Dispose();
            _overlapped.Dispose();
            _buffer.Dispose();
        }

        /// <summary>One overlapped transfer, waited out or called off; the bytes moved, or -1 when it didn't finish.</summary>
        private int Transfer(bool write, int length, TimeSpan timeout)
        {
            if (_wedged) return -1;
            _done.Reset();
            Marshal.StructureToPtr(
                new NativeOverlapped { EventHandle = _done.SafeWaitHandle.DangerousGetHandle() }, _overlapped.Address, false);
            var started = write
                ? HidNative.WriteFile(_handle, _buffer.Address, length, IntPtr.Zero, _overlapped.Address)
                : HidNative.ReadFile(_handle, _buffer.Address, length, IntPtr.Zero, _overlapped.Address);
            if (!started)
            {
                if (Marshal.GetLastPInvokeError() != HidNative.IoPending) return -1;
                var ended = Settle(_done.WaitOne, () => HidNative.CancelIoEx(_handle, _overlapped.Address), timeout);
                if (ended is Transferred.CalledOff) return -1;
                if (ended is Transferred.Abandoned)
                {
                    // The device did not even finish the cancel. Windows may write into the buffer and signal the
                    // event at any time from now on, so neither is ever touched or freed again, and the link is done.
                    _wedged = true;
                    _done.SafeWaitHandle.SetHandleAsInvalid();
                    _overlapped.Abandon();
                    _buffer.Abandon();
                    return -1;
                }
            }

            return HidNative.GetOverlappedResult(_handle, _overlapped.Address, out var moved, wait: false) ? moved : -1;
        }
    }
}
