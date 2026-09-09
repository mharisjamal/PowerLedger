using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>
/// Turns one <see cref="Sample"/> into a <see cref="Reading"/> (spec §5).
/// On a laptop the battery discharge rate is the truth when available; otherwise the parts are summed
/// with a learned (laptop) or default "rest of system" baseline and divided by supply efficiency.
/// Desktops are always estimated. Wall-powered external monitors are added after the efficiency division.
/// </summary>
public sealed class PowerModel
{
    /// <summary>Default laptop "rest of system" (board, RAM, SSD, radios) before calibration. Extras are separate.</summary>
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

    /// <summary>
    /// Evaluates one tick. <c>Components.Sum</c> always equals <c>TotalW</c>; in measured mode <c>Rest</c> may go
    /// negative when the parts over-report, which is the honest sensor-disagreement signal.
    /// The Service feeds <c>Components.Cpu</c>, <c>Gpu</c> and <c>Display</c> back into the calibration learner.
    /// </summary>
    public Reading Evaluate(Sample s)
    {
        var cpu = CpuWatts(s);
        var gpu = GpuWatts(s);
        var display = DisplayModel.PanelWatts(_profile, s.Brightness, s.DisplayOn);
        var monitors = DisplayModel.MonitorWatts(_profile, s.DisplayOn);
        var userIdle = s.UserIdleSeconds >= _options.IdleThresholdSeconds;
        var isLaptop = _profile.Chassis == ChassisKind.Laptop;

        if (isLaptop && s.HasDischargeRate && s.BatteryRateW is { } measured)
        {
            var measuredParts = new Components(
                Cpu: cpu, Gpu: gpu, Display: display, Ram: 0, Storage: 0, Board: 0, Extras: 0,
                Monitors: monitors, PsuLoss: 0, Rest: measured - cpu - gpu - display);
            return Build(s, measured + monitors, Quality.Measured, measuredParts, userIdle);
        }

        Components parts;
        Quality quality;
        if (isLaptop && Finite(_baselines.GetBaseline(CalibrationBuckets.For(s.Brightness, s.DisplayOn))) is { } learned)
        {
            // The learned baseline was observed on battery, so it already contains any extras drawing from the battery.
            parts = new Components(
                Cpu: cpu, Gpu: gpu, Display: display, Ram: 0, Storage: 0, Board: 0, Extras: 0,
                Monitors: monitors, PsuLoss: 0, Rest: learned);
            quality = Quality.Calibrated;
        }
        else if (isLaptop)
        {
            parts = new Components(
                Cpu: cpu, Gpu: gpu, Display: display, Ram: 0, Storage: 0, Board: 0, Extras: _profile.ExtrasWatts,
                Monitors: monitors, PsuLoss: 0, Rest: LaptopBaselineW);
            quality = Quality.Estimated;
        }
        else
        {
            parts = new Components(
                Cpu: cpu, Gpu: gpu, Display: display, Ram: RamWatts(), Storage: StorageWatts(),
                Board: DesktopBoardW + _profile.FanCount * FanW, Extras: _profile.ExtrasWatts,
                Monitors: monitors, PsuLoss: 0, Rest: 0);
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
        if (Finite(s.CpuPackageW) is { } w) return Math.Max(0, w);
        var idle = _profile.Chassis == ChassisKind.Laptop ? CpuIdleLaptopW : CpuIdleDesktopW;
        var tdp = Math.Max(idle + 1, _profile.CpuTdpOverrideW ?? _facts.CpuTdpW);
        return idle + (tdp - idle) * Load(s.CpuLoad);
    }

    private double GpuWatts(Sample s)
    {
        if (!s.DGpuPresent) return 0;
        if (Finite(s.DGpuW) is { } w) return Math.Max(0, w);
        var tdp = Math.Max(GpuIdleW + 1, _profile.GpuTdpOverrideW ?? _facts.GpuTdpW);
        return GpuIdleW + (tdp - GpuIdleW) * Load(s.DGpuLoad ?? 0);
    }

    private double RamWatts() => _profile.RamSticks * (_profile.RamIsDdr5 ? Ddr5StickW : Ddr4StickW);

    private double StorageWatts() => _profile.SsdCount * SsdW + _profile.HddCount * HddW;

    /// <summary>NaN and infinity count as "no value", like null.</summary>
    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;

    private static double Load(double load) => double.IsFinite(load) ? Math.Clamp(load, 0, 1) : 0;
}
