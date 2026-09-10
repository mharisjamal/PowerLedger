using System.Runtime.InteropServices;

namespace PowerLedger.Sensors;

/// <summary>
/// The Windows calls the driver-free sources need. Every method answers with null rather than throwing when
/// Windows declines, so a source can decide what a missing answer means.
/// </summary>
internal static class Win32
{
    private const int SystemBatteryState = 5;

    /// <summary>Windows reports this when it cannot tell how fast the battery is moving.</summary>
    public const int UnknownRate = unchecked((int)0x80000000);

    /// <param name="AcOnLine">True when the machine is on mains power.</param>
    /// <param name="Present">True when a battery is fitted.</param>
    /// <param name="RateMilliwatts">Negative while discharging, positive while charging, <see cref="UnknownRate"/> when unknown.</param>
    public readonly record struct BatteryState(bool AcOnLine, bool Present, bool Charging, bool Discharging, int RateMilliwatts);

    /// <summary>Cumulative 100 ns counters since boot. Kernel time already includes idle time.</summary>
    public readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);

    public static BatteryState? ReadBatteryState()
    {
        var state = default(SystemBatteryStateInfo);
        var status = CallNtPowerInformation(SystemBatteryState, IntPtr.Zero, 0, ref state, (uint)Marshal.SizeOf<SystemBatteryStateInfo>());
        if (status != 0) return null;
        return new BatteryState(state.AcOnLine != 0, state.BatteryPresent != 0, state.Charging != 0, state.Discharging != 0, state.Rate);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferSize, ref SystemBatteryStateInfo outputBuffer, uint outputBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

}
