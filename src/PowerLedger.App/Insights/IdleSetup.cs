using Microsoft.Data.Sqlite;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>
/// What the Habits saving needs to know of this PC (report finding 5): how long the service waits with no input before
/// it counts the PC idle, since idle time only starts there, and what the PC draws asleep, since sleep is not free. The
/// draw asleep is the one the service books each sleep at (accuracy A10, <see cref="SleepPower"/>): a laptop's as it has
/// learned it from its battery, read through its adapter, since the saving supposes it sleeps where it idled, on AC.
/// </summary>
/// <param name="IdleAfter">The service's idle threshold.</param>
/// <param name="AsleepW">What the PC draws asleep, at the wall.</param>
internal readonly record struct IdleSetup(TimeSpan IdleAfter, double AsleepW)
{
    /// <summary>The service's own default threshold, and a desktop's draw asleep, the higher figure, so a PC whose settings
    /// cannot be read is never promised more than it would save.</summary>
    public static IdleSetup Default { get; } = Of(null);

    /// <summary>Where the service keeps its settings in its database (the Service's <c>SettingsStore.SettingsKey</c>).</summary>
    private const string SettingsKey = "service.settings";

    /// <param name="drain">What the laptop's battery loses asleep, as the service learned it; null before it has.</param>
    public static IdleSetup Of(ServiceSettings? settings, SleepDrain? drain = null) => new(
        TimeSpan.FromSeconds((settings ?? ServiceSettings.Default).IdleThresholdSeconds),
        SleepPower.Watts(laptop: settings?.Profile.Chassis == ChassisKind.Laptop, onBattery: false, drain));

    /// <summary>The setup from the service's settings and what it learned of sleep, as stored, read at each call;
    /// <see cref="Default"/> while they cannot be read.</summary>
    public static Func<IdleSetup> Reader(SqliteDatabase database) => () =>
    {
        try
        {
            var stored = new SettingsRepository(database);
            var settings = stored.Get(SettingsKey) is { } json ? PipeProtocol.DeserializeSettings(json) : null;
            return Of(settings, SleepDrain.Parse(stored.Get(SleepDrain.SettingsKey)));
        }
        catch (SqliteException)
        {
            return Default;
        }
    };
}
