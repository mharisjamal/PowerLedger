namespace PowerLedger.App.Tests;

/// <summary>The App's preferences held in memory, recording each change.</summary>
internal sealed class FakeUiSettings : IUiSettings
{
    public UiPreferences Current { get; private set; } = UiPreferences.Default;

    public bool StartsWithWindows { get; private set; } = true;

    public List<string> Changes { get; } = [];

    public string? Choose(ThemeChoice theme)
    {
        Current = Current with { Theme = theme };
        Changes.Add($"theme {theme}");
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

    public string? FinishFirstRun()
    {
        Current = Current with { FirstRunDone = true };
        Changes.Add("first run done");
        return null;
    }
}
