using Microsoft.Extensions.Logging;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service;

/// <summary>
/// What the machine draws asleep (accuracy A10). It reads the battery's charge as the machine goes to sleep and again as it
/// wakes, learns from the two what the battery loses asleep (<see cref="SleepDrain"/>, which leaves out whatever says
/// nothing of the sleep), and keeps that in the settings table, where the App reads it too. The loop books each sleep at
/// <see cref="Watts"/>. Runs on the loop's thread alone.
/// </summary>
internal sealed class SleepKeeper(SettingsRepository settings, Func<BatteryCharge?>? battery, ILogger log)
{
    private (BatteryCharge Charge, DateTimeOffset At)? _before;

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

    /// <summary>The machine is going to sleep: the battery's charge now.</summary>
    public void Suspending(DateTimeOffset now) => _before = Charge() is { } charge ? (charge, now) : null;

    /// <summary>The machine woke: what the sleep cost the battery is learned, and kept.</summary>
    public void Resumed(DateTimeOffset now)
    {
        var before = _before;
        _before = null;
        if (before is not { } slept || Charge() is not { } after) return;
        var learned = Drain.Learn(slept.Charge, after, now - slept.At);
        if (learned == Drain) return;
        Drain = learned;
        log.LogInformation(
            "Asleep on battery for {Hours:0.00} h, the battery lost {Lost:0.000} Wh; asleep it loses {Watts} W as learned",
            (now - slept.At).TotalHours, slept.Charge.RemainingWh - after.RemainingWh, learned.Watts?.ToString("0.000") ?? "nothing yet");
        try
        {
            settings.Set(SleepDrain.SettingsKey, learned.Save());
        }
        catch (Exception error)
        {
            log.LogWarning(error, "What the battery loses asleep could not be saved");
        }
    }

    /// <summary>What the machine draws asleep, for a sleep the service was told of.</summary>
    public double Watts(bool laptop, bool onBattery) => SleepPower.Watts(laptop, onBattery, Drain);

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
