using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <summary>What Windows announces when it changes, as counters a source compares with the ones it last saw.</summary>
internal interface ISystemSignals
{
    /// <summary>Moves when the machine goes onto or off its mains, and when the battery's charge moves a percent.</summary>
    int BatteryChanges { get; }

    /// <summary>Moves when a monitor arrives or leaves, and when the lid opens or shuts.</summary>
    int MonitorChanges { get; }

    /// <summary>Moves when the internal panel's brightness changes.</summary>
    int BrightnessChanges { get; }

    /// <summary>The internal panel's brightness as Windows last announced it, in percent; null until it has.</summary>
    int? Brightness { get; }
}

/// <summary>
/// What Windows announces when it changes, gathered once for the life of the process: the power source and the battery's
/// charge (GUID_ACDC_POWER_SOURCE, GUID_BATTERY_PERCENTAGE_REMAINING), the internal panel's brightness
/// (GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS), the lid (GUID_LIDSWITCH_STATE_CHANGE), and monitors arriving or leaving
/// (GUID_DEVINTERFACE_MONITOR). Each is a callback registration, which needs neither a window nor the service's control
/// handler, so a console run gets them too. They are made the first time a source asks and never undone: sensor sets are
/// built afresh after every resume and after a read that hung, and one registration for the process outlives them all, so
/// no callback can ever reach a set that is gone. Windows calls back on threads of its own; the counters are written with
/// interlocked increments and read with volatile reads.
/// </summary>
internal sealed class SystemSignals : ISystemSignals
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtPowerSettingChange = 0x8013;
    private const int CmNotifyFilterTypeDeviceInterface = 0;
    private const int CmNotifyActionDeviceInterfaceArrival = 0;
    private const int CmNotifyActionDeviceInterfaceRemoval = 1;

    private static readonly Guid AcDcPowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    private static readonly Guid BatteryPercentageRemaining = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");
    private static readonly Guid CurrentMonitorBrightness = new("8FFEE2C6-2D01-46BE-ADB9-398ADDC5B4FF");
    private static readonly Guid LidSwitchStateChange = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
    private static readonly Guid MonitorInterface = new("E6F07B5F-EE97-4A90-B076-33F57BF4EAA7");

    private static readonly Lazy<SystemSignals> Shared = new(static () =>
    {
        var signals = new SystemSignals();
        signals.Register();
        return signals;
    });

    // Held for as long as Windows may call them, which is the life of the process.
    private readonly PowerSettingCallback _onSetting;
    private readonly DeviceCallback _onDevice;
    private int _battery;
    private int _monitors;
    private int _brightnessChanges;
    private int _brightness = -1;

    private SystemSignals()
    {
        _onSetting = OnSetting;
        _onDevice = OnDevice;
    }

    /// <summary>The machine's signals, registered for on first use.</summary>
    public static ISystemSignals Windows => Shared.Value;

    public int BatteryChanges => Volatile.Read(ref _battery);

    public int MonitorChanges => Volatile.Read(ref _monitors);

    public int BrightnessChanges => Volatile.Read(ref _brightnessChanges);

    public int? Brightness
    {
        get
        {
            var percent = Volatile.Read(ref _brightness);
            return percent >= 0 ? percent : null;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PowerSettingCallback(IntPtr context, uint type, IntPtr setting);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint DeviceCallback(IntPtr notification, IntPtr context, int action, IntPtr data, uint size);

    /// <summary>Registers for every signal. One Windows refuses is simply never announced, and the sources' own slow
    /// re-reads cover it.</summary>
    private void Register()
    {
        foreach (var setting in new[] { AcDcPowerSource, BatteryPercentageRemaining, CurrentMonitorBrightness, LidSwitchStateChange })
        {
            var guid = setting;
            var recipient = new DeviceNotifySubscribeParameters { Callback = _onSetting };
            _ = PowerSettingRegisterNotification(ref guid, DeviceNotifyCallback, ref recipient, out _);
        }

        var filter = new CmNotifyFilter
        {
            Size = (uint)Marshal.SizeOf<CmNotifyFilter>(),
            FilterType = CmNotifyFilterTypeDeviceInterface,
            ClassGuid = MonitorInterface,
        };
        _ = CM_Register_Notification(ref filter, IntPtr.Zero, _onDevice, out _);
    }

    /// <summary>POWERBROADCAST_SETTING is the setting's GUID, a DWORD length, then the data.</summary>
    private uint OnSetting(IntPtr context, uint type, IntPtr setting)
    {
        if (type != PbtPowerSettingChange || setting == IntPtr.Zero) return 0;
        var guid = Marshal.PtrToStructure<Guid>(setting);
        if (guid == AcDcPowerSource || guid == BatteryPercentageRemaining)
        {
            Interlocked.Increment(ref _battery);
        }
        else if (guid == LidSwitchStateChange)
        {
            Interlocked.Increment(ref _monitors);
        }
        else if (guid == CurrentMonitorBrightness && Marshal.ReadInt32(setting, 16) >= 4)
        {
            Volatile.Write(ref _brightness, Math.Clamp(Marshal.ReadInt32(setting, 20), 0, 100));
            Interlocked.Increment(ref _brightnessChanges);
        }
        return 0;
    }

    private uint OnDevice(IntPtr notification, IntPtr context, int action, IntPtr data, uint size)
    {
        if (action is CmNotifyActionDeviceInterfaceArrival or CmNotifyActionDeviceInterfaceRemoval) Interlocked.Increment(ref _monitors);
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public PowerSettingCallback Callback;
        public IntPtr Context;
    }

    /// <summary>CM_NOTIFY_FILTER: four DWORDs, then a union whose largest member is a 200-character instance id.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    private struct CmNotifyFilter
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Flags;
        [FieldOffset(8)] public int FilterType;
        [FieldOffset(12)] public uint Reserved;
        [FieldOffset(16)] public Guid ClassGuid;
    }

    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr registration);

    [DllImport("cfgmgr32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Register_Notification(ref CmNotifyFilter filter, IntPtr context, DeviceCallback callback, out IntPtr notification);
}
