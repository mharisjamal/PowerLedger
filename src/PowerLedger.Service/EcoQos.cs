using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PowerLedger.Service;

/// <summary>
/// Windows' efficiency quality of service, EcoQoS (perf review §3, research §4). A process that asks for it has its
/// threads run at the most efficient clock speed, and on efficiency cores where the processor has them. The service's
/// steady work, a small read once a tick, needs no more; work done once, as at start, just takes a little longer. Windows
/// gives a service in session 0 none of the throttling it gives background apps, so the service asks for it itself.
/// Readings are unaffected: energy is integrated from counters and timestamps, not from how fast the code runs.
/// </summary>
internal static class EcoQos
{
    private const int ProcessPowerThrottling = 4;                    // PROCESS_INFORMATION_CLASS
    private const uint CurrentVersion = 1;                           // PROCESS_POWER_THROTTLING_CURRENT_VERSION
    private const uint ExecutionSpeed = 0x1;                         // PROCESS_POWER_THROTTLING_EXECUTION_SPEED

    /// <summary>Asks Windows to run <paramref name="process"/> at EcoQoS; false where Windows refused or doesn't know the setting.</summary>
    public static bool Enable(SafeProcessHandle process)
    {
        var state = new PowerThrottlingState { Version = CurrentVersion, ControlMask = ExecutionSpeed, StateMask = ExecutionSpeed };
        return SetProcessInformation(process, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
    }

    /// <summary>Whether <paramref name="process"/> asked for EcoQoS; null where Windows won't say.</summary>
    public static bool? IsOn(SafeProcessHandle process)
    {
        var state = new PowerThrottlingState { Version = CurrentVersion };
        return GetProcessInformation(process, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>())
            ? (state.ControlMask & state.StateMask & ExecutionSpeed) != 0
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(SafeProcessHandle process, int informationClass, ref PowerThrottlingState information, int size);

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(SafeProcessHandle process, int informationClass, ref PowerThrottlingState information, int size);
}
