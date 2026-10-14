using System.Text.RegularExpressions;
using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>
/// Turns one <see cref="Sample"/> into a <see cref="Reading"/> (spec §5).
/// The total comes from the first of these that applies: a laptop's battery discharge rate while it runs on its battery;
/// the output of a UPS the user says powers this PC, alone or with its monitors; the machine's own input meter, a platform
/// power meter or its management controller, which is a total already; the processor's own meter of the whole platform
/// (Snapdragon X's system rail), over the efficiency of whatever feeds this machine on AC and as it is on battery; what a
/// power supply says it draws from the wall, which is a total already, or else its DC output over that efficiency, a
/// desktop's supply read at the load it is carrying or a laptop's adapter; and otherwise the model, which sums the parts with a
/// learned (laptop) or default "rest of system" baseline and divides by that same efficiency. Each of the measured totals
/// counts only from a watt and a third of what was measured inside the machine up, since a machine that runs draws at least
/// that; a battery rate counts only on a tick the validator did not flag. A UPS or power supply total keeps the model's
/// parts, and the rest is what the total leaves of them. The external monitors draw what the <see cref="IMonitorDraw"/>
/// says (nothing, when the model is given none). A monitor with a plug of its own is added after the efficiency
/// division, and on top of a measured rate or a power supply's reading; a UPS that powers the monitors too already
/// holds it. One running off the PC, as a portable monitor on a laptop's USB-C port does, draws through the PC's supply
/// or battery, so it goes inside any division and is already in every reading.
/// </summary>
public sealed partial class PowerModel
{
    /// <summary>Default laptop "rest of system" (board, RAM, SSD, radios) before calibration, at a CPU drawing nothing; it
    /// adds <see cref="LaptopRestPerCpuWatt"/> of each CPU watt. Extras are separate.</summary>
    public const double LaptopBaselineW = 1;

    /// <summary>
    /// What the rest of an uncalibrated laptop adds for each watt its CPU package draws: the platform's voltage regulators,
    /// memory and fabric work harder with the processor. On the owner's i7-1165G7 each CPU watt showed up as 1.66 W at the
    /// battery (1.2 to 2.2 run by run, minute by minute), where a flat 5 W rest read idle minutes 46 to 66% high and loaded
    /// ones 14% low; 1 W plus half of each CPU watt read 13.6 h of its battery minutes within 1% overall and 8% in every
    /// CPU band (Plan AC). The calibration learner starts from this share and learns the laptop's own.
    /// </summary>
    public const double LaptopRestPerCpuWatt = 0.5;

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
    /// What a desktop card nothing measures idles at, as a share of its rating (Plan AC): big cards idle well above 3 W.
    /// TechPowerUp's card-only idle readings put an RTX 4090 at about 21 W of its 450 W and a GTX 1080 at about 7 W of
    /// 180 W, and cards that keep their link awake (Fermi, an Arc A7xx without ASPM) idle higher still; 4% sits under
    /// them, and a small card stays at <see cref="GpuIdleW"/>. A laptop's card idles switched off, so it keeps 3 W.
    /// </summary>
    public const double DesktopGpuIdleShare = 0.04;

    /// <summary>
    /// What a discrete GPU's chip or package reading is multiplied by to count the whole card (spec §3). Such a reading
    /// leaves out the card's memory regulators, voltage regulator losses and fans, and measured cards drew 12–25% more than
    /// their chip figure: an RX 5700 XT reporting 180 W drew 202 W, and an RX 6800 XT reporting 255 W about 300 W.
    /// </summary>
    public const double RestOfCardFactor = 1.15;

    /// <summary>The same for AMD's GCN cards, whose ASIC power leaves out their HBM or GDDR5 and its regulators too: board
    /// measurements of GCN cards run about 40% over the chip figure (accuracy review, A16).</summary>
    public const double GcnRestOfCardFactor = 1.4;

    /// <summary>A measured total under this is no reading of a running machine (Plan AC).</summary>
    public const double LeastBelievableW = 1;

    /// <summary>A measured total under this share of what was measured inside the machine is no reading of it (Plan AC).</summary>
    public const double LeastBelievableShare = 0.3;

    private readonly MachineProfile _profile;
    private readonly HardwareFacts _facts;
    private readonly PowerModelOptions _options;
    private readonly IBaselineProvider _baselines;
    private readonly IMonitorDraw _monitors;

    /// <summary>The name a power supply last gave, for its rating on a tick it gave none. The model is evaluated on the
    /// sampling thread alone.</summary>
    private string? _psuName;

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

    /// <summary>The processor load under which a PC left alone counts as idle (report finding 5): above it the PC is working,
    /// a build, an agent or a download, and that is use, not idle waste.</summary>
    public const double IdleCpuLoad = 0.10;

    /// <summary>The busiest graphics card's load under which a PC left alone counts as idle.</summary>
    public const double IdleGpuLoad = 0.05;

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
        var display = DisplayModel.PanelWatts(_profile, s);
        monitors = _monitors.Watts(s.DisplayOn);
        // Idle is the user away and the PC doing nothing; a load nobody could read counts as none, as its watts do. Idle time
        // nobody reported, with the App closed, comes in as 0, so the PC counts as in use.
        var userIdle = s.UserIdleSeconds >= _options.IdleThresholdSeconds
                       && Load(s.CpuLoad) < IdleCpuLoad && Load(s.DGpuLoad ?? 0) < IdleGpuLoad;
        var isLaptop = _profile.Chassis == ChassisKind.Laptop;
        if (!string.IsNullOrWhiteSpace(s.PsuName)) _psuName = s.PsuName;

        // Plan AC: a measured total under a third of what was measured inside the machine (its CPU package and any GPU that
        // reads its own watts), or under a watt, is no reading of it but a stale rate, a dead sensor or a misread unit (a
        // HID report's exponent read wrong gives 1e-7 W): the next source, or the model, stands in. Only measured parts set
        // it, since a GPU worked out from its load can be far over what it draws.
        var least = Math.Max(LeastBelievableW, LeastBelievableShare * ((Finite(s.CpuPackageW) is { } package ? Math.Max(0, package) : 0) + MeasuredGpuWatts(s)));

        // A rate on a suspect tick is no reading either: the validator flags the seconds after an AC change, when the rate
        // Windows gives is from before it.
        if (isLaptop && s.HasDischargeRate && !s.Suspect && s.BatteryRateW is { } measured && measured + monitors.OwnPlug >= least)
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
        if (isLaptop && Learned(_baselines.GetRest(CalibrationBuckets.For(s.Brightness, s.DisplayOn))) is { } learned)
        {
            // The learned rest was observed on battery, so it already contains any extras drawing from the battery. It
            // doesn't contain the monitors running off the laptop, which the learner is given with the display.
            parts = new Components(
                Cpu: cpu, Gpu: gpu, Display: display, Ram: 0, Storage: 0, Board: 0, Extras: 0,
                Monitors: monitors.Total, PsuLoss: 0, Unattributed: learned.At(cpu));
            quality = Quality.Calibrated;
        }
        else if (isLaptop)
        {
            parts = new Components(
                Cpu: cpu, Gpu: gpu, Display: display, Ram: 0, Storage: 0, Board: 0, Extras: _profile.ExtrasWatts,
                Monitors: monitors.Total, PsuLoss: 0, Unattributed: LaptopBaselineW + (LaptopRestPerCpuWatt * cpu));
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
        var psuLoss = FromTheWall(beforePsu, isLaptop, s.OnBattery, _psuName) - beforePsu;
        parts = parts with { PsuLoss = psuLoss };

        // A laptop running on its battery draws through neither a UPS nor a power supply, whatever they read.
        var onItsBattery = isLaptop && s.OnBattery;
        // What a laptop charging on AC draws for its battery at the wall, which a UPS's reading or an input meter's holds and
        // the PC has not used: the battery rule (see AtLeastTheDischarge) takes it off them.
        var charge = isLaptop && !s.OnBattery && Finite(s.BatteryChargeW) is { } charging && charging > 0
            ? FromTheWall(beforePsu + charging, isLaptop, onBattery: false, _psuName) - FromTheWall(beforePsu, isLaptop, onBattery: false, _psuName)
            : 0;
        if (!onItsBattery && UpsWatts(s) is { } ups && (_profile.UpsLoad == UpsLoad.ThisPc ? ups + monitors.OwnPlug : ups) >= least)
        {
            // A UPS that powers this PC alone leaves out the monitors with plugs of their own; one that powers the monitors too
            // already holds them. A load of the rated volt-amperes assumes a power factor, so a total from it is an estimate.
            var total = LessTheCharge(_profile.UpsLoad == UpsLoad.ThisPc ? ups + monitors.OwnPlug : ups, charge, parts);
            var upsQuality = s.UpsSource is UpsPowerSource.LoadOfRatedVoltAmps or UpsPowerSource.ApparentPowerAssumedFactor
                ? Quality.Estimated
                : Quality.Measured;
            return Build(s, total, upsQuality, WithRest(parts, total), userIdle, TotalSource.Ups);
        }
        if (!onItsBattery && SystemMeterWatts(s) is { } meter && meter + monitors.OwnPlug >= least)
        {
            // The machine's own input meter, a platform power meter or its management controller, measures what it draws
            // at its input, its supply's losses and all, so like a supply's wall figure nothing is divided.
            var meterParts = parts with { PsuLoss = 0 };
            var meterTotal = LessTheCharge(meter + monitors.OwnPlug, charge, meterParts);
            var from = s.SystemMeter == SystemMeterKind.Bmc ? TotalSource.Bmc : TotalSource.PowerMeter;
            return Build(s, meterTotal, Quality.Measured, WithRest(meterParts, meterTotal), userIdle, from);
        }
        if (Finite(s.PlatformW) is { } platform && platform > 0 && FromTheWall(platform, isLaptop, s.OnBattery, _psuName) + monitors.OwnPlug >= least)
        {
            // It comes before a power supply's figures: it meters the whole machine, where a supply may feed only part of it.
            // The processor's own meter of the whole platform (Snapdragon X's system rail) measures the machine's DC draw,
            // the display and anything running off the PC included, behind the charger, so battery charging is not in it.
            // On AC the wall figure is what the adapter draws to deliver it, as for a power supply's DC output; on battery,
            // with no rate to read (a battery reporting relative units), it is the draw itself.
            var drawn = FromTheWall(platform, isLaptop, s.OnBattery, _psuName);
            var railTotal = AtLeastTheDischarge(s, isLaptop, drawn + monitors.OwnPlug, monitors.OwnPlug);
            return Build(s, railTotal, Quality.Measured, WithRest(parts with { PsuLoss = drawn - platform }, railTotal), userIdle, TotalSource.PlatformMeter);
        }
        if (!onItsBattery && WallWatts(s) is { } wall && wall + monitors.OwnPlug >= least)
        {
            // What the supply says it draws from the wall is the whole draw already, its own losses and all, so nothing is
            // divided and no loss is worked out: what it holds of the supply's is no more knowable than the rest of it.
            var wallTotal = wall + monitors.OwnPlug;
            return Build(s, wallTotal, Quality.Measured, WithRest(parts with { PsuLoss = 0 }, wallTotal), userIdle, TotalSource.PowerSupplyWall);
        }
        if (!onItsBattery && PowerSupplyWatts(s) is { } output && FromTheWall(output, isLaptop, onBattery: false, s.PsuName) + monitors.OwnPlug >= least)
        {
            // The power supply draws its DC output over its efficiency from the wall, and loses the difference. A desktop's
            // supply is read off its tier's curve at the load it is carrying, since one idling at a fraction of its rating
            // is far below the figure its tier certifies; a laptop is fed by an adapter, which no supply tier describes.
            var drawn = FromTheWall(output, isLaptop, onBattery: false, s.PsuName);
            var total = drawn + monitors.OwnPlug;
            return Build(s, total, Quality.Measured, WithRest(parts with { PsuLoss = drawn - output }, total), userIdle, TotalSource.PowerSupply);
        }
        var modelled = beforePsu + psuLoss + monitors.OwnPlug;
        var floored = AtLeastTheDischarge(s, isLaptop, modelled, monitors.OwnPlug);
        return Build(s, floored, quality, floored > modelled ? WithRest(parts, floored) : parts, userIdle, TotalSource.Model);
    }

    /// <summary>The total with what charging the battery drew at the wall taken off, never taking it under the parts the
    /// model knows of (all but the rest): the charge rate and the adapter's share of it are estimates beside a measured
    /// total.</summary>
    private static double LessTheCharge(double total, double charge, Components parts)
        => charge > 0 ? Math.Max(total - charge, Math.Min(total, parts.Sum - parts.Unattributed)) : total;

    /// <summary>What the cards that read their own watts measured, the rest of each card included; 0 when none did.</summary>
    private double MeasuredGpuWatts(Sample s)
    {
        if (s.Gpus is { Count: > 0 } cards)
        {
            var sum = 0.0;
            foreach (var card in cards)
            {
                if (Finite(card.Watts) is not null) sum += CardWatts(card.Name, card.Watts, card.Scope, card.Load, card.RatedW, onlyCard: false);
            }
            return sum;
        }
        return s.DGpuPresent && Finite(s.DGpuW) is not null ? CardWatts(null, s.DGpuW, s.DGpuScope, s.DGpuLoad, rated: null, onlyCard: true) : 0;
    }

    /// <summary>
    /// What the machine draws from the wall for <paramref name="dcW"/> of DC (Plan AC). A laptop on battery draws it as it
    /// is; on AC its adapter's curve is read at its load with its no-load draw (<see cref="AdapterEfficiency"/>), unless the
    /// options fix one efficiency. A desktop's supply is read on its tier's curve at the load it carries, rated as its name
    /// says or at <see cref="PsuEfficiency.DefaultRatedW"/>, and at the tier its series is when its name says (an NZXT E is
    /// Gold whatever the profile's default).
    /// </summary>
    private double FromTheWall(double dcW, bool isLaptop, bool onBattery, string? psuName)
    {
        if (isLaptop)
        {
            if (onBattery) return dcW;
            return _options.LaptopAdapterEfficiency is { } flat ? dcW / flat : AdapterEfficiency.Wall(dcW, _options.LaptopAdapterRatedW);
        }
        return dcW / PsuEfficiency.For(_profile.PsuTier, dcW, psuName);
    }

    /// <summary>
    /// A laptop on AC whose battery is discharging draws more than its adapter delivers, so it draws at least the discharge:
    /// the total is raised to it where it came out lower. It stays a floor and not a measurement, since what the adapter
    /// delivers beside it is unknown, so the quality and source are left as they were.
    /// The battery rule, the same everywhere: energy the battery gives the machine is counted when it is given, on battery
    /// as the total and on AC as this floor; energy that goes into the battery while it charges is never counted, since the
    /// PC has not used it yet and counting it as well would count the same energy twice. So a charge rate is never added to
    /// any total, and a figure that might hold one has it taken off (see the Sensors' SampleDraft).
    /// </summary>
    private static double AtLeastTheDischarge(Sample s, bool isLaptop, double total, double ownPlug)
        => isLaptop && s.DischargeOnAcW is { } discharge ? Math.Max(total, discharge + ownPlug) : total;

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
    /// since a UPS that powers more, or may, doesn't stand for this PC. Only watts above zero are a reading, as a discharge
    /// rate is (see <see cref="Sample.HasDischargeRate"/>): a machine that runs draws something, so nought watts is a load
    /// below the first percent of the rating or a sensor that has died, and taking it as the total would wipe out the draw
    /// of the whole machine.</summary>
    private double? UpsWatts(Sample s)
        => (_profile.UpsLoad is UpsLoad.ThisPc or UpsLoad.ThisPcAndMonitors) && s.UpsSource != UpsPowerSource.None
           && Finite(s.UpsOutputW) is { } watts && watts > 0
            ? watts
            : null;

    /// <summary>The input watts the machine's own meter gave. Nought watts is no reading, as a UPS's is not.</summary>
    private static double? SystemMeterWatts(Sample s)
        => s.SystemMeter != SystemMeterKind.None && Finite(s.SystemMeterW) is { } watts && watts > 0 ? watts : null;

    /// <summary>The DC output watts a power supply gave, unless the user has turned reading it off. Nought watts is no
    /// reading of a running machine's rails, for the same reason a UPS's is not.</summary>
    private double? PowerSupplyWatts(Sample s)
        => _profile.ReadPowerSupply && Finite(s.PsuOutputW) is { } watts && watts > 0 ? watts : null;

    /// <summary>The wall watts a power supply gave, for the units that report what they draw rather than what their rails
    /// put out (see <see cref="Sample.PsuWallW"/>), under the same rules as the DC figure. A supply reports one or the
    /// other and never both; this one is taken first, because it is the machine's draw without an efficiency assumed.</summary>
    private double? WallWatts(Sample s)
        => _profile.ReadPowerSupply && Finite(s.PsuWallW) is { } watts && watts > 0 ? watts : null;

    private double CpuWatts(Sample s)
    {
        if (Finite(s.CpuPackageW) is { } w) return Math.Max(0, w);
        var idle = _profile.Chassis == ChassisKind.Laptop ? CpuIdleLaptopW : CpuIdleDesktopW;
        var tdp = Math.Max(idle + 1, _profile.CpuTdpOverrideW ?? _facts.CpuTdpW);
        return idle + (tdp - idle) * Load(s.CpuLoad);
    }

    /// <summary>Every discrete card's watts added up, or the one card's when the sample does not list them.</summary>
    private double GpuWatts(Sample s)
    {
        if (s.Gpus is { Count: > 0 } cards)
        {
            var sum = 0.0;
            foreach (var card in cards) sum += CardWatts(card.Name, card.Watts, card.Scope, card.Load, card.RatedW, onlyCard: cards.Count == 1);
            return sum;
        }
        return s.DGpuPresent ? CardWatts(null, s.DGpuW, s.DGpuScope, s.DGpuLoad, rated: null, onlyCard: true) : 0;
    }

    /// <summary>A card's watts: measured ones as they are, with the rest of the card added to a chip or package figure (more
    /// of it on a GCN card); the others idle plus the load's share of the rating above idle, a desktop card idling at
    /// <see cref="DesktopGpuIdleShare"/> of its rating and never under <see cref="GpuIdleW"/>. The user's figure rates the
    /// card when there is one; with several it rates only those without a rating of their own, since one figure cannot be
    /// every card's.</summary>
    private double CardWatts(string? name, double? watts, GpuPowerScope scope, double? load, double? rated, bool onlyCard)
    {
        if (Finite(watts) is { } w)
        {
            var restOfCard = IsGcn(name) ? GcnRestOfCardFactor : RestOfCardFactor;
            return Math.Max(0, w) * (scope is GpuPowerScope.ChipOnly or GpuPowerScope.Package ? restOfCard : 1);
        }
        var rating = (onlyCard ? _profile.GpuTdpOverrideW : null) ?? Finite(rated) ?? _profile.GpuTdpOverrideW ?? _facts.GpuTdpW;
        var idle = _profile.Chassis == ChassisKind.Laptop || !double.IsFinite(rating) ? GpuIdleW : Math.Max(GpuIdleW, DesktopGpuIdleShare * rating);
        var tdp = Math.Max(idle + 1, rating);
        return idle + ((tdp - idle) * Load(load ?? 0));
    }

    /// <summary>Whether a card's name is one of AMD's GCN cards (Radeon HD 7000 and 8000, R5, R7 and R9, RX 400 and 500,
    /// Vega, Radeon VII, FirePro and Radeon Pro WX), whose chip reading leaves out more of the card.</summary>
    private static bool IsGcn(string? name) => !string.IsNullOrEmpty(name) && GcnName().IsMatch(name);

    [GeneratedRegex(@"\b(HD\s*[78]\d{3}|R[579]\s*\d{3}X?2?|R9\s*(Fury|Nano)|RX\s*[45]\d{2}X?|Vega|Radeon\s*(Pro\s*)?VII|FirePro|WX\s*\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex GcnName();

    private double RamWatts() => _profile.RamSticks * (_profile.RamIsDdr5 ? Ddr5StickW : Ddr4StickW);

    private double StorageWatts() => _profile.SsdCount * SsdW + _profile.HddCount * HddW;

    /// <summary>A learned rest whose figures are numbers; anything else counts as nothing learned.</summary>
    private static RestOfSystem? Learned(RestOfSystem? rest)
        => rest is { } r && double.IsFinite(r.InterceptW) && double.IsFinite(r.PerCpuWatt) ? r : null;

    /// <summary>NaN and infinity count as "no value", like null.</summary>
    private static double? Finite(double? value) => value is { } v && double.IsFinite(v) ? v : null;

    private static double Load(double load) => double.IsFinite(load) ? Math.Clamp(load, 0, 1) : 0;
}
