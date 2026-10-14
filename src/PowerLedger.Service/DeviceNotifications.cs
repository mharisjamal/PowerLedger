using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PowerLedger.Service;

/// <summary>
/// Graphics adapters and batteries coming and going (perf review §12), through the configuration manager's notifications,
/// which need neither a window nor the service control handler. They are what the hardware facts can change with while the
/// service runs, as with a dock that carries a graphics card, a card's driver being installed or a battery fitted; the loop
/// then detects the hardware again once they settle, where it used to on every resume. Drives and the rest of the facts
/// can only change with the machine off, so start-up reads them.
/// </summary>
internal sealed class DeviceNotifications : IHostedService
{
    /// <summary>GUID_DEVINTERFACE_DISPLAY_ADAPTER.</summary>
    internal static readonly Guid DisplayAdapters = new("5B45201D-F2F2-4F3B-85BB-30FF1F953599");

    /// <summary>GUID_DEVICE_BATTERY.</summary>
    internal static readonly Guid Batteries = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

    private const uint FilterTypeDeviceInterface = 0;
    private const uint ActionArrival = 0;
    private const uint ActionRemoval = 1;
    private const uint Success = 0;

    private readonly LoopCommands _commands;
    private readonly ILogger<DeviceNotifications> _log;
    private readonly NotifyCallback _onNotify;                       // held for as long as Windows may call it
    private readonly List<IntPtr> _registrations = [];

    public DeviceNotifications(LoopCommands commands, ILogger<DeviceNotifications> log)
    {
        _commands = commands;
        _log = log;
        _onNotify = OnNotify;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint NotifyCallback(IntPtr notify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize);

    /// <summary>How many classes Windows took the registration for.</summary>
    internal int Registered => _registrations.Count;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var deviceClass in new[] { DisplayAdapters, Batteries })
        {
            var filter = new NotifyFilter { Size = (uint)Marshal.SizeOf<NotifyFilter>(), FilterType = FilterTypeDeviceInterface, ClassGuid = deviceClass };
            var result = CM_Register_Notification(ref filter, IntPtr.Zero, _onNotify, out var registration);
            if (result == Success) _registrations.Add(registration);
            else _log.LogWarning("Device notifications for {Class} are unavailable (error {Result}); the hardware is detected at start only", deviceClass, result);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in _registrations) CM_Unregister_Notification(registration);
        _registrations.Clear();
        return Task.CompletedTask;
    }

    /// <summary>Called by Windows on a thread of its own when a device of a registered class comes or goes.</summary>
    internal uint OnNotify(IntPtr notify, IntPtr context, uint action, IntPtr eventData, uint eventDataSize)
    {
        if (action is ActionArrival or ActionRemoval) _commands.Post(new DevicesChangedCommand());
        return Success;
    }

    /// <summary>CM_NOTIFY_FILTER for one device interface class; the union after the type is as large as a device
    /// instance id, 200 characters, of which the class GUID is the start.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct NotifyFilter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public uint FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [DllImport("cfgmgr32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Register_Notification(ref NotifyFilter filter, IntPtr context, NotifyCallback callback, out IntPtr registration);

    [DllImport("cfgmgr32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Unregister_Notification(IntPtr registration);
}
