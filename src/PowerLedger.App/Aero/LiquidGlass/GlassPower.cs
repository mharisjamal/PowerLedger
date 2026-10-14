using System.Runtime.InteropServices;

namespace PowerLedger.App.Aero;

/// <summary>
/// What Windows says about power, for the liquid glass (perf review P10), and the owner's switch over it. The glass is
/// <see cref="Lighter"/> while Windows' Energy Saver (battery saver before Windows 11) is on, and on battery when Lighter
/// glass on battery is chosen (GlassSettings.LighterOnBattery, which GlassMaterial copies here). Lighter, the governor caps
/// the glass's background refresh at 15 frames a second within 2 % of a core (<see cref="LiquidGlassGovernor"/>); a window
/// moved still has its glass drawn at once (DrawNow), and the look is the same. Read once from GetSystemPowerStatus, then
/// kept by each glass window's power notifications (GUID_POWER_SAVING_STATUS, GUID_ACDC_POWER_SOURCE and
/// PBT_APMPOWERSTATUSCHANGE: <see cref="Register"/>, <see cref="Notified"/>).
/// </summary>
internal static class GlassPower
{
    private static readonly object Gate = new();
    private static bool _energySaver, _onBattery, _lighterOnBattery, _read;

    /// <summary>GUID_POWER_SAVING_STATUS: Energy Saver (battery saver) on, 1, or off, 0.</summary>
    internal static readonly Guid PowerSavingStatus = new("e00958c0-c213-4ace-ac77-fecced2eeea5");

    /// <summary>GUID_ACDC_POWER_SOURCE: 0 the mains, 1 a battery, 2 a short-term source (a UPS).</summary>
    internal static readonly Guid PowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");

    /// <summary>Windows' Energy Saver is on.</summary>
    public static bool EnergySaver
    {
        get => Volatile.Read(ref _energySaver);
        set => Set(() => _energySaver = value);
    }

    /// <summary>The PC runs on its battery (or a UPS's).</summary>
    public static bool OnBattery
    {
        get => Volatile.Read(ref _onBattery);
        set => Set(() => _onBattery = value);
    }

    /// <summary>The owner's switch: lighter glass whenever on battery, not only under Energy Saver.</summary>
    public static bool LighterOnBattery
    {
        get => Volatile.Read(ref _lighterOnBattery);
        set => Set(() => _lighterOnBattery = value);
    }

    /// <summary>Whether the glass is lighter now.</summary>
    public static bool Lighter => Lighten(EnergySaver, OnBattery, LighterOnBattery);

    /// <summary>The rule, pure: Energy Saver on, or on battery with the switch on.</summary>
    public static bool Lighten(bool energySaver, bool onBattery, bool lighterOnBattery) => energySaver || (onBattery && lighterOnBattery);

    /// <summary>Reads both from Windows now (GetSystemPowerStatus): the mains line, and SystemStatusFlag's battery saver.</summary>
    public static void Read()
    {
        if (!GetSystemPowerStatus(out var status)) return;
        _read = true;
        OnBattery = status.ACLineStatus == 0;
        EnergySaver = status.SystemStatusFlag == 1;
    }

    /// <summary>Once, before the first glass window's notifications: what Windows says now.</summary>
    public static void ReadOnce()
    {
        if (!_read) Read();
    }

    /// <summary>Changes one of the three; the governor hears when that makes the glass lighter or full again.</summary>
    private static void Set(Action change)
    {
        bool before, after;
        lock (Gate)
        {
            before = Lighter;
            change();
            Interlocked.MemoryBarrier();
            after = Lighter;
        }
        if (after != before) LiquidGlassGovernor.PowerChanged();
    }

    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;

    /// <summary>Asks Windows to tell <paramref name="hwnd"/> when Energy Saver or the power source changes; the handles to
    /// give back to <see cref="Unregister"/>.</summary>
    public static IntPtr[] Register(IntPtr hwnd)
    {
        ReadOnce();
        var saving = PowerSavingStatus;
        var source = PowerSource;
        return [RegisterPowerSettingNotification(hwnd, ref saving, 0), RegisterPowerSettingNotification(hwnd, ref source, 0)];
    }

    public static void Unregister(IntPtr[] handles)
    {
        foreach (var handle in handles)
        {
            if (handle != IntPtr.Zero) UnregisterPowerSettingNotification(handle);
        }
    }

    /// <summary>A window's message: Energy Saver or the power source changed (a setting's new value), or the power status
    /// did (read again). Returns whether it was one.</summary>
    public static bool Notified(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WM_POWERBROADCAST) return false;
        switch ((int)wParam)
        {
            case PBT_APMPOWERSTATUSCHANGE:
                Read();
                return true;
            case PBT_POWERSETTINGCHANGE when lParam != IntPtr.Zero:
                var setting = Marshal.PtrToStructure<Guid>(lParam);
                if (Marshal.ReadInt32(lParam, 16) < 4) return true;
                var value = Marshal.ReadInt32(lParam, 20);   // POWERBROADCAST_SETTING: the GUID, DataLength, then the DWORD
                if (setting == PowerSavingStatus) EnergySaver = value != 0;
                else if (setting == PowerSource) OnBattery = value != 0;
                return true;
            default:
                return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("user32.dll")]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
