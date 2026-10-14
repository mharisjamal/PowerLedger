using Microsoft.Data.Sqlite;
using PowerLedger.Contracts;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>
/// What the Habits saving needs to know of this PC (report finding 5): how long the service waits with no input before
/// it counts the PC idle, since idle time only starts there, and what the PC draws asleep, since sleep is not free.
/// </summary>
/// <param name="IdleAfter">The service's idle threshold.</param>
/// <param name="AsleepW">What the PC draws asleep, at the wall.</param>
internal readonly record struct IdleSetup(TimeSpan IdleAfter, double AsleepW)
{
    /// <summary>A laptop in Modern Standby: 0.3 to 0.7 W measured on the owner's laptop (accuracy review, finding 10).</summary>
    public const double LaptopAsleepW = 0.5;

    /// <summary>A desktop asleep draws 1 to 4 W at the wall (accuracy review, finding 10).</summary>
    public const double DesktopAsleepW = 2;

    /// <summary>The service's own default threshold, and a desktop's draw asleep, the higher figure, so a PC whose settings
    /// cannot be read is never promised more than it would save.</summary>
    public static IdleSetup Default { get; } = Of(null);

    /// <summary>Where the service keeps its settings in its database (the Service's <c>SettingsStore.SettingsKey</c>).</summary>
    private const string SettingsKey = "service.settings";

    public static IdleSetup Of(ServiceSettings? settings) => new(
        TimeSpan.FromSeconds((settings ?? ServiceSettings.Default).IdleThresholdSeconds),
        settings?.Profile.Chassis == ChassisKind.Laptop ? LaptopAsleepW : DesktopAsleepW);

    /// <summary>The setup from the service's settings as stored, read at each call; <see cref="Default"/> while they cannot be read.</summary>
    public static Func<IdleSetup> Reader(SqliteDatabase database) => () =>
    {
        try
        {
            return Of(new SettingsRepository(database).Get(SettingsKey) is { } json ? PipeProtocol.DeserializeSettings(json) : null);
        }
        catch (SqliteException)
        {
            return Default;
        }
    };
}
