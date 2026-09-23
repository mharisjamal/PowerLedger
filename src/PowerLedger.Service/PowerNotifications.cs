using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PowerLedger.Service;

/// <summary>
/// Suspend, resume and the console display's state (spec §6), through the power manager's callback registrations.
/// They need neither a window nor the service control handler, so a console run gets them too. A suspend holds the
/// machine for up to <see cref="SuspendGrace"/> while the loop writes its buffer and closes the session.
/// </summary>
internal sealed class PowerNotifications : IHostedService
{
    /// <summary>How long a suspend waits for the loop; Windows allows about two seconds.</summary>
    public static readonly TimeSpan SuspendGrace = TimeSpan.FromMilliseconds(1500);

    private const uint DeviceNotifyCallback = 2;
    private const uint PbtApmSuspend = 0x4;
    private const uint PbtApmResumeAutomatic = 0x12;
    private const uint PbtPowerSettingChange = 0x8013;
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    private readonly LoopCommands _commands;
    private readonly ServiceSignals _signals;
    private readonly ILogger<PowerNotifications> _log;
    private readonly DeviceNotifyCallbackRoutine _onSuspendResume;   // held for as long as Windows may call it
    private readonly DeviceNotifyCallbackRoutine _onDisplay;
    private IntPtr _suspendRegistration;
    private IntPtr _displayRegistration;

    public PowerNotifications(LoopCommands commands, ServiceSignals signals, ILogger<PowerNotifications> log)
    {
        _commands = commands;
        _signals = signals;
        _log = log;
        _onSuspendResume = OnSuspendResume;
        _onDisplay = OnDisplay;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var suspend = new DeviceNotifySubscribeParameters { Callback = _onSuspendResume };
        var status = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref suspend, out _suspendRegistration);
        if (status != 0) _log.LogWarning("Suspend and resume notifications are unavailable (error {Status}); sleep will show only as gaps", status);

        var display = new DeviceNotifySubscribeParameters { Callback = _onDisplay };
        var setting = ConsoleDisplayState;
        status = PowerSettingRegisterNotification(ref setting, DeviceNotifyCallback, ref display, out _displayRegistration);
        if (status != 0) _log.LogWarning("Display state notifications are unavailable (error {Status}); the display is taken as on", status);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_suspendRegistration != IntPtr.Zero) PowerUnregisterSuspendResumeNotification(_suspendRegistration);
        if (_displayRegistration != IntPtr.Zero) PowerSettingUnregisterNotification(_displayRegistration);
        _suspendRegistration = _displayRegistration = IntPtr.Zero;
        return Task.CompletedTask;
    }

    /// <summary>Called by Windows on a system thread when the machine is about to sleep or has woken.</summary>
    internal uint OnSuspendResume(IntPtr context, uint type, IntPtr setting)
    {
        switch (type)
        {
            case PbtApmSuspend:
                try
                {
                    _commands.SendAsync(new SuspendCommand()).Wait(SuspendGrace);
                }
                catch (AggregateException error)
                {
                    _log.LogWarning(error.InnerException, "Preparing for sleep failed");
                }
                break;
            case PbtApmResumeAutomatic:
                // Nobody waits for the loop to wake, but a resume it couldn't carry out is looked at here: a failed task
                // left unlooked-at would be recorded as a service crash, and the service carries on.
                _ = _commands.SendAsync(new ResumeCommand()).ContinueWith(
                    failed => _log.LogWarning(failed.Exception?.InnerException, "Waking up after sleep failed"),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                break;
        }
        return 0;
    }

    /// <summary>Called by Windows when the console display turns on, off or dims. POWERBROADCAST_SETTING is a GUID, a
    /// DWORD length, then the data: 0 off, 1 on, 2 dimmed.</summary>
    internal uint OnDisplay(IntPtr context, uint type, IntPtr setting)
    {
        if (type == PbtPowerSettingChange && setting != IntPtr.Zero && Marshal.ReadInt32(setting, 16) >= 4)
        {
            _signals.DisplayOn = Marshal.ReadInt32(setting, 20) != 0;
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public DeviceNotifyCallbackRoutine Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr registration);

    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);

    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr registration);

    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerSettingUnregisterNotification(IntPtr registration);
}
