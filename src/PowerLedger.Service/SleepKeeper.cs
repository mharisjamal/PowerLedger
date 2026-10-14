using Microsoft.Extensions.Logging;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// What the machine draws asleep (accuracy A10). It reads the battery's charge as the machine goes to sleep and again a
/// minute after it wakes, with what that minute awake drew added back, learns from the two what the battery loses asleep
/// (<see cref="SleepDrain"/>, which leaves out whatever says nothing of the sleep), and keeps that in the settings table,
/// where the App reads it too. The loop books each sleep at
/// <see cref="Watts"/>. A suspend that is no sleep (Plan AC review, <see cref="SleepKinds"/>) books nothing and teaches
/// nothing: one with nobody signed in, as a Fast Startup shutdown is, and a hibernate, from when it hibernated. Runs on the
/// loop's thread alone.
/// </summary>
/// <param name="signedIn">Whether anyone is signed in, asked as the machine goes to sleep; yes, when left out.</param>
/// <param name="hibernated">When the machine hibernated since the time given, asked as it wakes; null when it slept, and
/// always, when left out.</param>
internal sealed class SleepKeeper(
    SettingsRepository settings, Func<BatteryCharge?>? battery, ILogger log, Func<bool>? signedIn = null, Func<DateTimeOffset, DateTimeOffset?>? hibernated = null)
{
    /// <summary>A hibernate that came this soon after the suspend was the suspend: the sleep before it is no more than
    /// the clocks' disagreement.</summary>
    private static readonly TimeSpan HibernatedAtOnce = TimeSpan.FromMinutes(1);

    /// <summary>How long after a wake the battery is read again (Plan AC review): read the moment the machine wakes, it can
    /// still give its charge from before the sleep, a drain of nothing.</summary>
    public static readonly TimeSpan ReadAgainAfter = TimeSpan.FromMinutes(1);

    private (BatteryCharge Charge, DateTimeOffset At)? _before;
    private AwakeSince? _awake;
    private DateTimeOffset? _suspendedAt;
    private bool _signedIn = true;
    private double _share = 1;

    /// <summary>What the battery loses asleep, as learned so far.</summary>
    public SleepDrain Drain { get; private set; } = SleepDrain.None;

    /// <summary>Reads what earlier runs learned; nothing learned when it cannot be read.</summary>
    public void Load()
    {
        try
        {
            Drain = SleepDrain.Parse(settings.Get(SleepDrain.SettingsKey));
        }
        catch (Exception error)
        {
            log.LogWarning(error, "What the battery loses asleep could not be read; it is learned afresh");
        }
    }

    /// <summary>The machine is going to sleep: the battery's charge now, unless nobody is signed in. A sleep before it whose
    /// minute awake is not over yet is learned from the charge now, as fresh as a charge gets.</summary>
    public void Suspending(DateTimeOffset now)
    {
        if (_awake is not null) Learn(Charge());
        _suspendedAt = now;
        _signedIn = Ask(signedIn, true, "Whether anyone is signed in");
        _before = _signedIn && Charge() is { } charge ? (charge, now) : null;
        if (!_signedIn) log.LogInformation("Suspended with nobody signed in, as a Fast Startup shutdown is: the time off is no sleep");
    }

    /// <summary>The machine woke: how much of the time off was a sleep is worked out, and a sleep throughout is learned from
    /// once the battery is read again, <see cref="ReadAgainAfter"/> on (see <see cref="Awake"/>).</summary>
    public void Resumed(DateTimeOffset now)
    {
        var before = _before;
        _before = null;
        _awake = null;
        _share = Share(now);
        if (_share < 1 || before is not { } slept) return;
        _awake = new AwakeSince(slept.Charge, slept.At, now, 0);
    }

    /// <summary>
    /// A tick done: while a sleep waits to be learned from, what the tick drew from the battery is added up, and once
    /// <see cref="ReadAgainAfter"/> has passed since the wake the battery is read again and the sleep learned from, with
    /// what the minute awake drew added back (Plan AC review). A laptop plugged in within the minute says nothing more of
    /// its sleep.
    /// </summary>
    /// <param name="fromBatteryWh">What the tick drew from the battery, at the pack; nothing for the tick that reaches back
    /// across the sleep, which is no time awake.</param>
    public void Awake(DateTimeOffset now, bool onBattery, double fromBatteryWh)
    {
        if (_awake is not { } awake) return;
        if (!onBattery)
        {
            _awake = null;
            return;
        }
        _awake = awake = awake with { DrawnWh = awake.DrawnWh + (double.IsFinite(fromBatteryWh) ? Math.Max(0, fromBatteryWh) : 0) };
        if (now - awake.Woke >= ReadAgainAfter) Learn(Charge());
    }

    /// <summary>Learns the sleep waiting since its wake from the battery's charge now, less what the time awake since drew.</summary>
    private void Learn(BatteryCharge? now)
    {
        var awake = _awake!.Value;
        _awake = null;
        if (now is not { } read) return;
        var after = read with { RemainingWh = read.RemainingWh + awake.DrawnWh };
        var slept = awake.Woke - awake.Slept;
        var learned = Drain.Learn(awake.Before, after, slept);
        if (learned == Drain) return;
        Drain = learned;
        log.LogInformation(
            "Asleep on battery for {Hours:0.00} h, the battery lost {Lost:0.000} Wh; asleep it loses {Watts} W as learned",
            slept.TotalHours, awake.Before.RemainingWh - after.RemainingWh, learned.Watts?.ToString("0.000") ?? "nothing yet");
        try
        {
            settings.Set(SleepDrain.SettingsKey, learned.Save());
        }
        catch (Exception error)
        {
            log.LogWarning(error, "What the battery loses asleep could not be saved");
        }
    }

    /// <summary>What the machine drew asleep over the time off just ended, for a sleep the service was told of: what it draws
    /// asleep, over only the part of the time off that was a sleep. Asked once, by the tick that reaches back across it.</summary>
    public double Watts(bool laptop, bool onBattery)
    {
        var watts = SleepPower.Watts(laptop, onBattery, Drain) * _share;
        _share = 1;
        return watts;
    }

    /// <summary>The share of the time off since the suspend that was a sleep: none with nobody signed in, none from the
    /// moment it hibernated, all of it otherwise.</summary>
    private double Share(DateTimeOffset now)
    {
        if (_suspendedAt is not { } suspended) return 1;
        _suspendedAt = null;
        if (!_signedIn) return 0;
        if (Ask(() => hibernated?.Invoke(suspended), null, "Whether the machine hibernated") is not { } at) return 1;
        var share = at - suspended < HibernatedAtOnce || now <= suspended ? 0 : Math.Clamp((at - suspended) / (now - suspended), 0, 1);
        log.LogInformation("The machine hibernated {After:0.00} h into {Hours:0.00} h off; asleep {Share:P0} of it", (at - suspended).TotalHours, (now - suspended).TotalHours, share);
        return share;
    }

    private T Ask<T>(Func<T>? question, T otherwise, string what)
    {
        try
        {
            return question is null ? otherwise : question();
        }
        catch (Exception error)
        {
            log.LogWarning(error, "{What} could not be told", what);
            return otherwise;
        }
    }

    private BatteryCharge? Charge()
    {
        try
        {
            return battery?.Invoke();
        }
        catch (Exception error)
        {
            log.LogWarning(error, "The battery's charge could not be read");
            return null;
        }
    }

    /// <summary>A sleep waiting to be learned from: the charge as it began, when it began and ended, and what the battery has
    /// given the machine awake since.</summary>
    private readonly record struct AwakeSince(BatteryCharge Before, DateTimeOffset Slept, DateTimeOffset Woke, double DrawnWh);
}
