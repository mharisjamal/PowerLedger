using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <summary>
/// The Windows calls the driver-free sources need. Every method answers with null rather than throwing when
/// Windows declines, so a source can decide what a missing answer means.
/// </summary>
internal static class Win32
{
    private const int SystemPowerCapabilities = 4;
    private const int SystemBatteryState = 5;

    /// <summary>Windows reports this when it cannot tell how fast the battery is moving.</summary>
    public const int UnknownRate = unchecked((int)0x80000000);

    /// <param name="AcOnLine">True when the machine is on mains power.</param>
    /// <param name="Present">True when Windows sees a battery.</param>
    /// <param name="RateMilliwatts">Negative while discharging, positive while charging, <see cref="UnknownRate"/> when unknown.</param>
    /// <param name="ShortTerm">True when Windows marks the batteries short-term, as it does for a UPS on USB.</param>
    public readonly record struct BatteryState(bool AcOnLine, bool Present, bool Charging, bool Discharging, int RateMilliwatts, bool ShortTerm = false)
    {
        /// <summary>A battery that powers this machine alone. A UPS also powers whatever else is plugged into it, so
        /// neither its presence nor its drain says anything about this machine. Windows marks some UPS units short-term,
        /// but not all, so one it doesn't mark still passes as the machine's own.</summary>
        public bool OwnBattery => Present && !ShortTerm;
    }

    /// <summary>Cumulative 100 ns counters since boot. Kernel time already includes idle time.</summary>
    public readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);

    public static BatteryState? ReadBatteryState()
    {
        var state = default(SystemBatteryStateInfo);
        var status = CallNtPowerInformation(SystemBatteryState, IntPtr.Zero, 0, ref state, (uint)Marshal.SizeOf<SystemBatteryStateInfo>());
        if (status != 0) return null;
        return new BatteryState(state.AcOnLine != 0, state.BatteryPresent != 0, state.Charging != 0, state.Discharging != 0, state.Rate, BatteriesAreShortTerm());
    }

    /// <summary>False when Windows will not say, which keeps a real battery counted.</summary>
    private static bool BatteriesAreShortTerm()
    {
        var capabilities = default(SystemPowerCapabilitiesInfo);
        var status = CallNtPowerInformation(SystemPowerCapabilities, IntPtr.Zero, 0, ref capabilities, (uint)Marshal.SizeOf<SystemPowerCapabilitiesInfo>());
        return status == 0 && capabilities.SystemBatteriesPresent != 0 && capabilities.BatteriesAreShortTerm != 0;
    }

    public static SystemTimes? ReadSystemTimes()
        => GetSystemTimes(out var idle, out var kernel, out var user) ? new SystemTimes(idle, kernel, user) : null;

    /// <summary>Seconds since the last keyboard or mouse input in the calling session. From the service (session 0)
    /// this reports the service's own session, so the service must get idle time from the user's session instead.</summary>
    public static double ReadIdleSeconds()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemBatteryStateInfo
    {
        public byte AcOnLine;
        public byte BatteryPresent;
        public byte Charging;
        public byte Discharging;
        public byte Spare0;
        public byte Spare1;
        public byte Spare2;
        public byte Spare3;
        public uint MaxCapacity;
        public uint RemainingCapacity;
        public int Rate;
        public uint EstimatedTime;
        public uint DefaultAlert1;
        public uint DefaultAlert2;
    }

    /// <summary>SYSTEM_POWER_CAPABILITIES is 76 bytes; only its two battery flags are read. The offsets were checked
    /// on real hardware against the sleep states powercfg reports.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 76)]
    private struct SystemPowerCapabilitiesInfo
    {
        [FieldOffset(30)] public byte SystemBatteriesPresent;
        [FieldOffset(31)] public byte BatteriesAreShortTerm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferSize, ref SystemBatteryStateInfo outputBuffer, uint outputBufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferSize, ref SystemPowerCapabilitiesInfo outputBuffer, uint outputBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
