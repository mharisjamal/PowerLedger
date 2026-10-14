using Microsoft.Extensions.Logging;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// What the machine draws asleep (accuracy A10). It reads the battery's charge as the machine goes to sleep and again as it
/// wakes, learns from the two what the battery loses asleep (<see cref="SleepDrain"/>, which leaves out whatever says
/// nothing of the sleep), and keeps that in the settings table, where the App reads it too. The loop books each sleep at
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

    private (BatteryCharge Charge, DateTimeOffset At)? _before;
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

    /// <summary>The machine is going to sleep: the battery's charge now, unless nobody is signed in.</summary>
    public void Suspending(DateTimeOffset now)
    {
        _suspendedAt = now;
        _signedIn = Ask(signedIn, true, "Whether anyone is signed in");
        _before = _signedIn && Charge() is { } charge ? (charge, now) : null;
        if (!_signedIn) log.LogInformation("Suspended with nobody signed in, as a Fast Startup shutdown is: the time off is no sleep");
    }

    /// <summary>The machine woke: how much of the time off was a sleep is worked out, and what a sleep throughout cost the
    /// battery is learned, and kept.</summary>
    public void Resumed(DateTimeOffset now)
    {
        var before = _before;
        _before = null;
        var after = before is null ? null : Charge();         // first, as close to the wake as can be
        _share = Share(now);
        if (_share < 1) return;
        if (before is not { } slept || after is null) return;
        var learned = Drain.Learn(slept.Charge, after.Value, now - slept.At);
        if (learned == Drain) return;
        Drain = learned;
        log.LogInformation(
            "Asleep on battery for {Hours:0.00} h, the battery lost {Lost:0.000} Wh; asleep it loses {Watts} W as learned",
            (now - slept.At).TotalHours, slept.Charge.RemainingWh - after.Value.RemainingWh, learned.Watts?.ToString("0.000") ?? "nothing yet");
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
}
