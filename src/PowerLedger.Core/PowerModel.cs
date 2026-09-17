using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>
/// Turns one <see cref="Sample"/> into a <see cref="Reading"/> (spec §5).
/// The total comes from the first of these that applies: a laptop's battery discharge rate while it runs on its battery;
/// the output of a UPS the user says powers this PC, alone or with its monitors; a power supply's DC output over its
/// efficiency; and otherwise the model, which sums the parts with a learned (laptop) or default "rest of system" baseline
/// and divides by supply efficiency. A UPS or power supply total keeps the model's parts, and the rest is what the total
/// leaves of them. The external monitors draw what the <see cref="IMonitorDraw"/> says (nothing, when the model is given
/// none). A monitor with a plug of its own is added after the efficiency division, and on top of a measured rate or a power
/// supply's reading; a UPS that powers the monitors too already holds it. One running off the PC, as a portable monitor on a
/// laptop's USB-C port does, draws through the PC's supply or battery, so it goes inside the division and is already in
/// every reading.
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

    /// <summary>
    /// What a discrete GPU's chip or package reading is multiplied by to count the whole card (spec §3). Such a reading
    /// leaves out the card's memory regulators, voltage regulator losses and fans, and measured cards drew 12–25% more than
    /// their chip figure: an RX 5700 XT reporting 180 W drew 202 W, and an RX 6800 XT reporting 255 W about 300 W.
    /// </summary>
    public const double RestOfCardFactor = 1.15;

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
    /// Evaluates one tick. <c>Components.Sum</c> always equals <c>TotalW</c>; when a battery, UPS or power supply reading
    /// gives the total, <c>Unattributed</c> may go negative when the parts over-report, which is the honest
    /// sensor-disagreement signal.
    /// </summary>
    public Reading Evaluate(Sample s) => Evaluate(s, out _);

    /// <summary>
    /// Evaluates one tick, as <see cref="Evaluate(Sample)"/> does, and gives what the monitors drew as this reading counts
    /// them. The Service feeds <c>Components.Cpu</c>, <c>Gpu</c> and <c>Display</c> back into the calibration learner, and
    /// with the display what the monitors running off the PC drew, since a battery rate holds that too. It takes that
    /// figure from here, the one the reading used, rather than asking the monitors again, which detection may have changed
    /// in between. The split is no part of the <see cref="Components"/>, which are stored and piped part for part:
    /// <c>Components.Monitors</c> holds both kinds together.
    /// </summary>
    /// <param name="monitors">What the <see cref="IMonitorDraw"/> said, asked once for this reading.</param>
    public Reading Evaluate(Sample s, out MonitorWatts monitors)
    {
        var cpu = CpuWatts(s);
        var gpu = GpuWatts(s);
        var display = DisplayModel.PanelWatts(_profile, s.Brightness, s.DisplayOn);
        monitors = _monitors.Watts(s.DisplayOn);
        var userIdle = s.UserIdleSeconds >= _options.IdleThresholdSeconds;
        var isLaptop = _profile.Chassis == ChassisKind.Laptop;

        if (isLaptop && s.HasDischargeRate && s.BatteryRateW is { } measured)
        {
            // The battery delivers what the monitors running off the laptop draw, so the measured rate already holds them:
            // they come out of the remainder, and only the monitors with a plug of their own are added.
            var measuredParts = new Components(
                Cpu: cpu, Gpu: gpu, Display: display, Ram: 0, Storage: 0, Board: 0, Extras: 0,
                Monitors: monitors.Total, PsuLoss: 0, Unattributed: measured - cpu - gpu - display - monitors.FromPc);
            return Build(s, measured + monitors.OwnPlug, Quality.Measured, measuredParts, userIdle, TotalSource.Battery);
        }

        Components parts;
        Quality quality;
        if (isLaptop && Finite(_baselines.GetBaseline(CalibrationBuckets.For(s.Brightness, s.DisplayOn))) is { } learned)
        {
            // The learned baseline was observed on battery, so it already contains any extras drawing from the battery. It
            // doesn't contain the monitors running off the laptop, which the learner is given with the display.
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
        parts = parts with { PsuLoss = psuLoss };

        // A laptop running on its battery draws through neither a UPS nor a power supply, whatever they read.
        var onItsBattery = isLaptop && s.OnBattery;
        if (!onItsBattery && UpsWatts(s) is { } ups)
        {
            // A UPS that powers this PC alone leaves out the monitors with plugs of their own; one that powers the monitors too
            // already holds them. A load of the rated volt-amperes assumes a power factor, so a total from it is an estimate.
            var total = _profile.UpsLoad == UpsLoad.ThisPc ? ups + monitors.OwnPlug : ups;
            var measuredQuality = s.UpsSource == UpsPowerSource.LoadOfRatedVoltAmps ? Quality.Estimated : Quality.Measured;
            return Build(s, total, measuredQuality, WithRest(parts, total), userIdle, TotalSource.Ups);
        }
        if (!onItsBattery && PowerSupplyWatts(s) is { } output)
        {
            // The power supply draws its DC output over its efficiency from the wall, and loses the difference.
            var wall = output / PsuEfficiency.For(_profile.PsuTier);
            var total = wall + monitors.OwnPlug;
            return Build(s, total, Quality.Measured, WithRest(parts with { PsuLoss = wall - output }, total), userIdle, TotalSource.PowerSupply);
        }
        return Build(s, beforePsu + psuLoss + monitors.OwnPlug, quality, parts, userIdle, TotalSource.Model);
    }

    /// <summary>A reading is never non-finite: a bad profile or option value yields a zeroed, suspect reading rather than poisoning storage.</summary>
    private static Reading Build(Sample s, double total, Quality quality, Components parts, bool userIdle, TotalSource source)
        => double.IsFinite(total) && double.IsFinite(parts.Sum)
            ? new Reading(s.Timestamp, s.DeltaSeconds, total, quality, parts, s.OnBattery, s.DisplayOn, userIdle,
                          s.SessionLocked, s.CpuLoad, s.DGpuLoad, s.Brightness, s.Suspect, source, s.DGpuScope)
            : new Reading(s.Timestamp, s.DeltaSeconds, 0, quality, Components.Zero, s.OnBattery, s.DisplayOn, userIdle,
                          s.SessionLocked, s.CpuLoad, s.DGpuLoad, s.Brightness, Suspect: true, source, s.DGpuScope);

    /// <summary>The parts with what <paramref name="total"/> leaves of the others as the rest, so that they sum to it.</summary>
    private static Components WithRest(Components parts, double total) => parts with { Unattributed = total - (parts.Sum - parts.Unattributed) };

    /// <summary>The watts a UPS gave, when the user has said it powers this PC, alone or with its monitors; otherwise null,
    /// since a UPS that powers more, or may, doesn't stand for this PC.</summary>
    private double? UpsWatts(Sample s)
        => (_profile.UpsLoad is UpsLoad.ThisPc or UpsLoad.ThisPcAndMonitors) && s.UpsSource != UpsPowerSource.None
           && Finite(s.UpsOutputW) is { } watts && watts >= 0
            ? watts
            : null;

    /// <summary>The DC output watts a power supply gave, unless the user has turned reading it off.</summary>
    private double? PowerSupplyWatts(Sample s)
        => _profile.ReadPowerSupply && Finite(s.PsuOutputW) is { } watts && watts >= 0 ? watts : null;

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
        if (Finite(s.DGpuW) is { } w)
        {
            return Math.Max(0, w) * (s.DGpuScope is GpuPowerScope.ChipOnly or GpuPowerScope.Package ? RestOfCardFactor : 1);
        }
        var tdp = Math.Max(GpuIdleW + 1, _profile.GpuTdpOverrideW ?? _facts.GpuTdpW);
        return GpuIdleW + (tdp - GpuIdleW) * Load(s.DGpuLoad ?? 0);
    }

    private double RamWatts() => _profile.RamSticks * (_profile.RamIsDdr5 ? Ddr5StickW : Ddr4StickW);

    private double StorageWatts() => _profile.SsdCount * SsdW + _profile.HddCount * HddW;

    /// <summary>NaN and infinity count as "no value", like null.</summary>
    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;

    private static double Load(double load) => double.IsFinite(load) ? Math.Clamp(load, 0, 1) : 0;
}
