using System.Runtime.InteropServices;

namespace PowerLedger.App;

/// <summary>
/// Windows' efficiency mode (EcoQoS) for the App's own process while none of its windows shows (perf review P3): what is
/// left to do then, the tray's reading a second and a poll now and then, runs at the processor's most efficient clocks,
/// and on its efficiency cores where it has them. Turned off when a window shows, which hands the choice back to Windows.
/// No figure depends on it: the readings and their times come from the service.
/// </summary>
internal static class ProcessQos
{
    private const int ProcessPowerThrottling = 4;   // PROCESS_INFORMATION_CLASS
    private const uint CurrentVersion = 1;          // PROCESS_POWER_THROTTLING_CURRENT_VERSION
    private const uint ExecutionSpeed = 0x1;        // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
    private static readonly IntPtr CurrentProcess = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    /// <summary>Asks for efficiency mode, or, with <paramref name="on"/> false, hands the choice back to Windows. False when
    /// Windows wouldn't, as one before Windows 10 1709 can't: the App runs as before.</summary>
    public static bool Efficient(bool on)
    {
        var state = new PowerThrottlingState
        {
            Version = CurrentVersion,
            ControlMask = on ? ExecutionSpeed : 0,
            StateMask = on ? ExecutionSpeed : 0,
        };
        return SetProcessInformation(CurrentProcess, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    /// <summary>Whether efficiency mode is asked for now, or null where Windows doesn't say; for a test.</summary>
    internal static bool? IsEfficient()
    {
        var state = new PowerThrottlingState { Version = CurrentVersion };
        if (!GetProcessInformation(CurrentProcess, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>())) return null;
        return (state.ControlMask & ExecutionSpeed) != 0 && (state.StateMask & ExecutionSpeed) != 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);
}
