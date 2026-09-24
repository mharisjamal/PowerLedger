namespace PowerLedger.App.Tests;

/// <summary>The App's preferences held in memory, recording each change.</summary>
internal sealed class FakeUiSettings : IUiSettings
{
    public UiPreferences Current { get; set; } = UiPreferences.Default;

    public bool StartsWithWindows { get; private set; } = true;

    public List<string> Changes { get; } = [];

    public string? Choose(ThemeChoice theme)
    {
        Current = Current with { Theme = theme };
        Changes.Add($"theme {theme}");
        return null;
    }

    /// <summary>What <see cref="SetLook"/> answers instead of switching, for a test of a window that won't open.</summary>
    public string? LookProblem { get; set; }

    public string? SetLook(Look look)
    {
        if (LookProblem is not null) return LookProblem;
        Current = Current with { Look = look, LookIntroduced = true };
        Changes.Add($"look {look}");
        return null;
    }

    public string? IntroduceLook()
    {
        Current = Current with { LookIntroduced = true };
        Changes.Add("look introduced");
        return null;
    }

    public string? UseCo2(double kgPerKwh)
    {
        if (!(kgPerKwh >= 0 && kgPerKwh < UiPreferences.MaxCo2KgPerKwh)) return "refused";
        Current = Current with { Co2KgPerKwh = kgPerKwh };
        Changes.Add($"co2 {kgPerKwh}");
        return null;
    }

    public string? StartWithWindows(bool enabled)
    {
        StartsWithWindows = enabled;
        Changes.Add($"autostart {enabled}");
        return null;
    }

    public string? CheckForUpdates(bool enabled)
    {
        Current = Current with { CheckForUpdates = enabled };
        Changes.Add($"updates {enabled}");
        return null;
    }

    public string? ReadMonitorBrightness(bool enabled)
    {
        Current = Current with { ReadMonitorBrightness = enabled };
        Changes.Add($"monitor brightness {enabled}");
        return null;
    }

    public string? Announced(string version)
    {
        Current = Current with { AnnouncedVersion = version };
        Changes.Add($"announced {version}");
        return null;
    }

    public string? Ran(string version)
    {
        Current = Current with { LastVersion = version };
        Changes.Add($"ran {version}");
        return null;
    }

    public string? FinishFirstRun()
    {
        Current = Current with { FirstRunDone = true, FirstRunAt = Current.FirstRunAt ?? Now() };
        Changes.Add("first run done");
        return null;
    }

    public string? EnsureFirstRunAt()
    {
        if (!Current.FirstRunDone || Current.FirstRunAt is not null) return null;
        Current = Current with { FirstRunAt = Now() };
        Changes.Add("first run backfilled");
        return null;
    }

    public string? SetSignedInEmail(string? email)
    {
        Current = Current with { SignedInEmail = email };
        Changes.Add($"signed in email {email}");
        return null;
    }

    /// <summary>Tests can set this to control what a first run is stamped with; UtcNow otherwise.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;
}
