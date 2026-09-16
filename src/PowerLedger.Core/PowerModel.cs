using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>
/// Turns one <see cref="Sample"/> into a <see cref="Reading"/> (spec §5).
/// On a laptop the battery discharge rate is the truth when available; otherwise the parts are summed
/// with a learned (laptop) or default "rest of system" baseline and divided by supply efficiency.
/// Desktops are always estimated. The external monitors draw what the <see cref="IMonitorDraw"/> says (nothing, when the
/// model is given none). A monitor with a plug of its own is added after the efficiency division, and on top of a measured
/// rate; one running off the PC, as a portable monitor on a laptop's USB-C port does, draws through the PC's supply or
/// battery, so it goes inside the division and is already in a measured rate.
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
    private readonly IMonitorDraw _monitors;

    public PowerModel(MachineProfile profile, HardwareFacts facts, PowerModelOptions options, IBaselineProvider baselines, IMonitorDraw? monitors = null)
    {
        _profile = profile;
        _facts = facts;
        _options = options;
        _baselines = baselines;
        _monitors = monitors ?? NoMonitors.Instance;
    }

    /// <summary>
    /// Evaluates one tick. <c>Components.Sum</c> always equals <c>TotalW</c>; in measured mode <c>Unattributed</c> may go
    /// negative when the parts over-report, which is the honest sensor-disagreement signal.
    /// The Service feeds <c>Components.Cpu</c>, <c>Gpu</c> and <c>Display</c> back into the calibration learner.
    /// </summary>
    public Reading Evaluate(Sample s)
    {
        var cpu = CpuWatts(s);
        var gpu = GpuWatts(s);
        var display = DisplayModel.PanelWatts(_profile, s.Brightness, s.DisplayOn);
        var monitors = _monitors.Watts(s.DisplayOn);
        var userIdle = s.UserIdleSeconds >= _options.IdleThresholdSeconds;
        var isLaptop = _profile.Chassis == ChassisKind.Laptop;

        if (isLaptop && s.HasDischargeRate && s.BatteryRateW is { } measured)
        {
            // The battery delivers what the monitors running off the laptop draw, so the measured rate already holds them:
            // they come out of the remainder, and only the monitors with a plug of their own are added.
            var measuredParts = new Components(
                Cpu: cpu, Gpu: gpu, Display: display, Ram: 0, Storage: 0, Board: 0, Extras: 0,
                Monitors: monitors.Total, PsuLoss: 0, Unattributed: measured - cpu - gpu - display - monitors.FromPc);
            return Build(s, measured + monitors.OwnPlug, Quality.Measured, measuredParts, userIdle);
        }

        Components parts;
        Quality quality;
        if (isLaptop && Finite(_baselines.GetBaseline(CalibrationBuckets.For(s.Brightness, s.DisplayOn))) is { } learned)
        {
            // The learned baseline was observed on battery, so it already contains any extras drawing from the battery.
            parts = new Components(
                Cpu: cpu, Gpu: gpu, Display: display, Ram: 0, Storage: 0, Board: 0, Extras: 0,
                Monitors: monitors.Total, PsuLoss: 0, Unattributed: learned);
            quality = Quality.Calibrated;
        }
        else if (isLaptop)
        {
            parts = new Components(
                Cpu: cpu, Gpu: gpu, Display: display, Ram: 0, Storage: 0, Board: 0, Extras: _profile.ExtrasWatts,
                Monitors: monitors.Total, PsuLoss: 0, Unattributed: LaptopBaselineW);
            quality = Quality.Estimated;
        }
        else
        {
            parts = new Components(
                Cpu: cpu, Gpu: gpu, Display: display, Ram: RamWatts(), Storage: StorageWatts(),
                Board: DesktopBoardW + _profile.FanCount * FanW, Extras: _profile.ExtrasWatts,
                Monitors: monitors.Total, PsuLoss: 0, Unattributed: 0);
            quality = Quality.Estimated;
        }

        // A monitor with a plug of its own sits outside the PC's supply, so it is added after the efficiency division. One
        // running off the PC draws through the supply like any other part, so it is divided with them.
        var beforePsu = parts.Sum - monitors.OwnPlug;
        var efficiency = isLaptop
            ? (s.OnBattery ? 1.0 : _options.LaptopAdapterEfficiency)
            : PsuEfficiency.For(_profile.PsuTier);
        var psuLoss = beforePsu / efficiency - beforePsu;
        var total = beforePsu + psuLoss + monitors.OwnPlug;
        return Build(s, total, quality, parts with { PsuLoss = psuLoss }, userIdle);
    }

    /// <summary>A reading is never non-finite: a bad profile or option value yields a zeroed, suspect reading rather than poisoning storage.</summary>
    private static Reading Build(Sample s, double total, Quality quality, Components parts, bool userIdle)
        => double.IsFinite(total) && double.IsFinite(parts.Sum)
            ? new Reading(s.Timestamp, s.DeltaSeconds, total, quality, parts, s.OnBattery, s.DisplayOn, userIdle,
                          s.SessionLocked, s.CpuLoad, s.DGpuLoad, s.Brightness, s.Suspect)
            : new Reading(s.Timestamp, s.DeltaSeconds, 0, quality, Components.Zero, s.OnBattery, s.DisplayOn, userIdle,
                          s.SessionLocked, s.CpuLoad, s.DGpuLoad, s.Brightness, Suspect: true);

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
