using System.IO;

namespace PowerLedger.App;

/// <summary>What only the App cares about, as Settings and the wizard change it. Each change applies at once and is saved
/// to ui.json; a method returns null when all went well, and otherwise says what went wrong.</summary>
internal interface IUiSettings
{
    UiPreferences Current { get; }

    bool StartsWithWindows { get; }

    string? Choose(ThemeChoice theme);

    string? UseCo2(double kgPerKwh);

    string? StartWithWindows(bool enabled);

    string? CheckForUpdates(bool enabled);

    /// <summary>Whether the App reads the monitors' brightness; the next scheduled read follows it.</summary>
    string? ReadMonitorBrightness(bool enabled);

    /// <summary>Remembers that the tray announced <paramref name="version"/>.</summary>
    string? Announced(string version);

    /// <summary>Remembers that <paramref name="version"/> ran.</summary>
    string? Ran(string version);

    string? FinishFirstRun();

    /// <summary>N2's signed-in e-mail, kept in ui.json only (households design §7); null once signed out.</summary>
    string? SetSignedInEmail(string? email);

    /// <summary>Stamps <see cref="UiPreferences.FirstRunAt"/> with now when the first run is done but nothing stamped it
    /// yet: an install from before this field existed. Does nothing before the first run finishes, or once stamped.</summary>
    string? EnsureFirstRunAt();
}

/// <summary>
/// The App's live preferences. A theme goes to <paramref name="applyTheme"/>, a CO₂ factor to <paramref name="applyCo2"/>
/// (the screens that show CO₂), and starting with Windows to the Run entry; each is then saved. A change applies even
/// when ui.json cannot be written, and the answer says so.
/// </summary>
internal sealed class AppPreferences(
    UiPreferencesStore store, UiPreferences initial, Action<ThemeChoice> applyTheme, Action<double> applyCo2, StartWithWindows autostart) : IUiSettings
{
    public UiPreferences Current { get; private set; } = initial;

    public bool StartsWithWindows => autostart.IsEnabled;

    public string? Choose(ThemeChoice theme)
    {
        applyTheme(theme);
        return Save(Current with { Theme = theme });
    }

    public string? UseCo2(double kgPerKwh)
    {
        if (!(double.IsFinite(kgPerKwh) && kgPerKwh >= 0 && kgPerKwh < UiPreferences.MaxCo2KgPerKwh))
            return $"A grid's intensity is between 0 and {UiPreferences.MaxCo2KgPerKwh:0} kg of CO₂ per kWh.";
        applyCo2(kgPerKwh);
        return Save(Current with { Co2KgPerKwh = kgPerKwh });
    }

    public string? StartWithWindows(bool enabled)
    {
        try
        {
            autostart.Set(enabled);
            return null;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return "Couldn't change starting with Windows: " + error.Message;
        }
    }

    public string? CheckForUpdates(bool enabled) => Save(Current with { CheckForUpdates = enabled });

    public string? ReadMonitorBrightness(bool enabled) => Save(Current with { ReadMonitorBrightness = enabled });

    public string? Announced(string version) => Save(Current with { AnnouncedVersion = version });

    public string? Ran(string version) => Save(Current with { LastVersion = version });

    public string? FinishFirstRun() => Save(Current with { FirstRunDone = true, FirstRunAt = Current.FirstRunAt ?? DateTimeOffset.UtcNow });

    public string? SetSignedInEmail(string? email) => Save(Current with { SignedInEmail = email });

    public string? EnsureFirstRunAt()
        => Current.FirstRunDone && Current.FirstRunAt is null ? Save(Current with { FirstRunAt = DateTimeOffset.UtcNow }) : null;

    /// <summary>Spec §9: starting with Windows is on by default. Until the first run is done the App turns it on as it
    /// starts, as the user who runs it (the installer can't: it runs as the elevating account); after that it is left
    /// as the user set it.</summary>
    public void ApplyFirstRunDefaults()
    {
        if (Current.FirstRunDone || autostart.IsEnabled) return;
        StartWithWindows(true);
    }

    private string? Save(UiPreferences next)
    {
        Current = next;
        try
        {
            store.Save(next);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "Couldn't save your preferences: " + error.Message;
        }
    }
}
