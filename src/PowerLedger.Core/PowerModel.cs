using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>
/// Turns one <see cref="Sample"/> into a <see cref="Reading"/> (spec §5).
/// Battery discharge is the truth when available; otherwise the parts are summed
/// with a learned or default "rest of system" baseline and divided by supply efficiency.
/// </summary>
public sealed class PowerModel
{
    public const double LaptopBaselineW = 5;
    public const double DesktopBoardW = 12;
    public const double FanW = 1;
    public const double Ddr4StickW = 2.5;
    public const double Ddr5StickW = 1.5;
    public const double SsdW = 2;
    public const double HddW = 6;
    public const double CpuIdleLaptopW = 2;
    public const double CpuIdleDesktopW = 8;
    public const double GpuIdleW = 3;

    private readonly MachineProfile _profile;
    private readonly HardwareFacts _facts;
    private readonly PowerModelOptions _options;
    private readonly IBaselineProvider _baselines;

    public PowerModel(MachineProfile profile, HardwareFacts facts, PowerModelOptions options, IBaselineProvider baselines)
    {
        _profile = profile;
        _facts = facts;
        _options = options;
        _baselines = baselines;
    }

    public Reading Evaluate(Sample s)
    {
        var cpu = CpuWatts(s);
        var gpu = GpuWatts(s);
        var display = DisplayModel.PanelWatts(_profile, s.Brightness, s.DisplayOn);
        var monitors = DisplayModel.MonitorWatts(_profile, s.DisplayOn);
        var userIdle = s.UserIdleSeconds >= _options.IdleThresholdSeconds;

        if (s.OnBattery && s.BatteryRateW is { } measured && measured >= 0)
        {
            var rest = Math.Max(0, measured - cpu - gpu - display);
            var measuredParts = new Components(cpu, gpu, display, 0, 0, 0, 0, 0, 0, rest);
            return Build(s, measured, Quality.Measured, measuredParts, userIdle);
        }

        var isLaptop = _profile.Chassis == ChassisKind.Laptop;
        Components parts;
        Quality quality;
        if (_baselines.GetBaseline(CalibrationBuckets.For(s.Brightness, s.DisplayOn)) is { } learned)
        {
            parts = new Components(cpu, gpu, display, 0, 0, 0, 0, monitors, 0, learned);
            quality = Quality.Calibrated;
        }
        else if (isLaptop)
        {
            parts = new Components(cpu, gpu, display, 0, 0, 0, _profile.ExtrasWatts, monitors, 0, Rest: LaptopBaselineW);
            quality = Quality.Estimated;
        }
        else
        {
            var board = DesktopBoardW + _profile.FanCount * FanW;
            parts = new Components(cpu, gpu, display, RamWatts(), StorageWatts(), board, _profile.ExtrasWatts, monitors, 0, 0);
            quality = Quality.Estimated;
        }

        // External monitors are wall-powered: they sit outside the PC's supply, so they are added after the efficiency division.
        var beforePsu = parts.Sum - parts.Monitors;
        var efficiency = isLaptop
            ? (s.OnBattery ? 1.0 : _options.LaptopAdapterEfficiency)
            : PsuEfficiency.For(_profile.PsuTier);
        var psuLoss = beforePsu / efficiency - beforePsu;
        var total = beforePsu + psuLoss + parts.Monitors;
        return Build(s, total, quality, parts with { PsuLoss = psuLoss }, userIdle);
    }

    private static Reading Build(Sample s, double total, Quality quality, Components parts, bool userIdle)
        => new(s.Timestamp, s.DeltaSeconds, total, quality, parts, s.OnBattery, s.DisplayOn, userIdle,
               s.SessionLocked, s.CpuLoad, s.DGpuLoad, s.Brightness, s.Suspect);

    private double CpuWatts(Sample s)
    {
        if (s.CpuPackageW is { } w) return Math.Max(0, w);
        var idle = _profile.Chassis == ChassisKind.Laptop ? CpuIdleLaptopW : CpuIdleDesktopW;
        var tdp = Math.Max(idle + 1, _profile.CpuTdpOverrideW ?? _facts.CpuTdpW);
        return idle + (tdp - idle) * Math.Clamp(s.CpuLoad, 0, 1);
    }

    private double GpuWatts(Sample s)
    {
        if (!s.DGpuPresent) return 0;
        if (s.DGpuW is { } w) return Math.Max(0, w);
        var tdp = Math.Max(GpuIdleW + 1, _profile.GpuTdpOverrideW ?? _facts.GpuTdpW);
        return GpuIdleW + (tdp - GpuIdleW) * Math.Clamp(s.DGpuLoad ?? 0, 0, 1);
    }

    private double RamWatts() => _profile.RamSticks * (_profile.RamIsDdr5 ? Ddr5StickW : Ddr4StickW);

    private double StorageWatts() => _profile.SsdCount * SsdW + _profile.HddCount * HddW;
}
