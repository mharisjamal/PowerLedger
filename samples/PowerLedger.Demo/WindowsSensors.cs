using System.Runtime.InteropServices;
using PowerLedger.Core;

namespace PowerLedger.Demo;

/// <summary>The few readings Windows exposes without a kernel driver. Plan B replaces this with the real sensor adapters.</summary>
internal sealed class WindowsSensors
{
    private const int SystemBatteryState = 5;
    private const int UnknownRate = unchecked((int)0x80000000);

    private ulong _idle, _kernel, _user;

    public bool BatteryPresent { get; }

    public WindowsSensors()
    {
        BatteryPresent = ReadBattery().BatteryPresent != 0;
        ReadCpuLoad();   // prime the counters so the first real tick has a delta
    }

    public Sample Read(DateTimeOffset now, double deltaSeconds)
    {
        var battery = ReadBattery();
        var onBattery = battery.BatteryPresent != 0 && battery.AcOnLine == 0;
        var rate = battery.Rate;
        double? rateW = onBattery && rate != 0 && rate != UnknownRate ? Math.Abs(rate) / 1000.0 : null;

        return new Sample(
            Timestamp: now, DeltaSeconds: deltaSeconds,
            CpuPackageW: null, IGpuW: null, CpuLoad: ReadCpuLoad(),
            DGpuW: null, DGpuLoad: null, DGpuPresent: false,
            BatteryRateW: rateW, OnBattery: onBattery,
            Brightness: null, DisplayOn: true, MonitorCount: 1,
            UserIdleSeconds: IdleSeconds(), SessionLocked: false, Suspect: false);
    }

    private double ReadCpuLoad()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return 0;
        ulong idle = idleTime, kernel = kernelTime, user = userTime;
        var deltaIdle = idle - _idle;
        var deltaBusy = kernel - _kernel + (user - _user);
        _idle = idle;
        _kernel = kernel;
        _user = user;
        return deltaBusy == 0 ? 0 : Math.Clamp(1.0 - (double)deltaIdle / deltaBusy, 0, 1);
    }

    private static double IdleSeconds()
    {
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
    }

    private static SystemBatteryStateInfo ReadBattery()
    {
        var state = default(SystemBatteryStateInfo);
        _ = CallNtPowerInformation(SystemBatteryState, IntPtr.Zero, 0, ref state, (uint)Marshal.SizeOf<SystemBatteryStateInfo>());
        return state;
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

    // LibraryImport's source generator needs <AllowUnsafeBlocks> for these ref-struct signatures, which is
    // outside the demo's minimal csproj, so these three use the classic runtime-marshalled DllImport instead.
    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferSize, ref SystemBatteryStateInfo outputBuffer, uint outputBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
