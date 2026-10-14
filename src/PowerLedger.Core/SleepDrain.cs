using System.Globalization;

namespace PowerLedger.Core;

/// <summary>A laptop battery's charge as Windows reports it (SYSTEM_BATTERY_STATE), in watt-hours.</summary>
/// <param name="OnAc">True when the machine is on mains power.</param>
/// <param name="RemainingWh">What is left in the battery.</param>
/// <param name="FullWh">What the battery holds when full, which changes only slowly as it wears, or when it is swapped.</param>
public readonly record struct BatteryCharge(bool OnAc, double RemainingWh, double FullWh);

/// <summary>
/// What a laptop's battery loses asleep (accuracy A10), learned from its charge as it went to sleep and as it woke, on
/// battery throughout: watt-hours lost over hours asleep, both summed over the sleeps seen, the older ones weighing less
/// as more sleep is seen. A battery reports its charge in steps (often a hundredth of its capacity), so a single short
/// sleep can read as nothing or as a step; summed over many, the steps even out. What says nothing of a sleep is left out:
/// one on AC at either end, one under <see cref="ShortestSleep"/>, a charge that rose, a battery whose full capacity changed
/// (swapped), and a drain past <see cref="MostBelievableW"/>, which no sleep draws. The service keeps it in its settings
/// table under <see cref="SettingsKey"/>, where the App reads it too.
/// </summary>
/// <param name="DrainedWh">Watt-hours lost asleep, summed over the sleeps seen, older ones weighed down.</param>
/// <param name="Hours">Hours asleep, summed and weighed the same way.</param>
public sealed record SleepDrain(double DrainedWh, double Hours)
{
    /// <summary>Where the service keeps what it learned, in its settings table.</summary>
    public const string SettingsKey = "sleep.drain";

    /// <summary>A sleep shorter than this moves the charge too little to read.</summary>
    public static readonly TimeSpan ShortestSleep = TimeSpan.FromMinutes(10);

    /// <summary>Hours of sleep after which a sleep weighs half what a new one does.</summary>
    public const double HalfLifeHours = 72;

    /// <summary>A drain past this is no sleep: the machine stayed awake, or something else drew on the battery.</summary>
    public const double MostBelievableW = 10;

    /// <summary>The hours asleep that must be seen before the drain is trusted.</summary>
    public const double LeastHours = 2;

    /// <summary>A full capacity that moved more than this share across a sleep is another battery.</summary>
    public const double SwappedShare = 0.02;

    /// <summary>Nothing learned yet.</summary>
    public static SleepDrain None { get; } = new(0, 0);

    /// <summary>What the battery loses asleep, in watts; null until <see cref="LeastHours"/> of sleep have been seen.</summary>
    public double? Watts => Hours >= LeastHours ? DrainedWh / Hours : null;

    /// <summary>This drain with one more sleep learned, the battery's charge <paramref name="before"/> it and
    /// <paramref name="after"/> it; this drain itself when the two say nothing of the sleep.</summary>
    public SleepDrain Learn(BatteryCharge before, BatteryCharge after, TimeSpan slept)
    {
        if (before.OnAc || after.OnAc || slept < ShortestSleep) return this;
        if (!Positive(before.FullWh) || !Positive(after.FullWh) || !double.IsFinite(before.RemainingWh) || !double.IsFinite(after.RemainingWh)) return this;
        if (Math.Abs(after.FullWh - before.FullWh) > SwappedShare * before.FullWh) return this;
        var drained = before.RemainingWh - after.RemainingWh;
        var hours = slept.TotalHours;
        if (drained < 0 || drained / hours > MostBelievableW) return this;
        var kept = Math.Pow(0.5, hours / HalfLifeHours);
        return new SleepDrain((DrainedWh * kept) + drained, (Hours * kept) + hours);
    }

    /// <summary>The drain as text, for the settings table.</summary>
    public string Save() => string.Create(CultureInfo.InvariantCulture, $"{DrainedWh:R} {Hours:R}");

    /// <summary>A drain saved by <see cref="Save"/>; <see cref="None"/> for anything else.</summary>
    public static SleepDrain Parse(string? text)
    {
        var parts = text?.Split(' ');
        if (parts is not { Length: 2 }) return None;
        return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var drained)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
            && double.IsFinite(drained) && drained >= 0 && Positive(hours)
                ? new SleepDrain(drained, hours)
                : None;
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}

/// <summary>
/// What a PC draws asleep (accuracy A10). A laptop draws what its battery loses asleep, as learned (<see cref="SleepDrain"/>),
/// or <see cref="LaptopDrainW"/> before it has learned: from the battery on battery, and on AC through its adapter, which
/// is at its least efficient with so little to deliver and draws a little with nothing to deliver at all. A desktop draws
/// <see cref="DesktopW"/> at the wall.
/// </summary>
public static class SleepPower
{
    /// <summary>A laptop in Modern Standby: 0.3 to 0.7 W measured on the owner's laptop (accuracy review, finding 10).</summary>
    public const double LaptopDrainW = 0.5;

    /// <summary>A desktop asleep draws 1 to 4 W at the wall (accuracy review, finding 10).</summary>
    public const double DesktopW = 2;

    /// <summary>What the PC draws asleep: a laptop on battery from the battery, on AC at the wall; a desktop at the wall.</summary>
    public static double Watts(bool laptop, bool onBattery, SleepDrain? learned)
    {
        if (!laptop) return DesktopW;
        var drain = learned?.Watts ?? LaptopDrainW;
        return onBattery ? drain : AdapterEfficiency.Wall(drain);
    }
}
