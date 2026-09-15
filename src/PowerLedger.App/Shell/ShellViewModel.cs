using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerLedger.App;

/// <summary>The four screens of spec §9's rail.</summary>
internal enum Page
{
    Now,
    Breakdown,
    Report,
    Settings,
}

/// <summary>The window: which page shows, the screens, and the version in the title bar.</summary>
internal sealed class ShellViewModel(
    NowViewModel now, BreakdownViewModel breakdown, ReportViewModel report, SettingsViewModel settings, string version) : ObservableObject
{
    private Page _page = Page.Now;

    public NowViewModel Now { get; } = now;

    public BreakdownViewModel Breakdown { get; } = breakdown;

    public ReportViewModel Report { get; } = report;

    public SettingsViewModel Settings { get; } = settings;

    public string Version { get; } = version;

    /// <summary>The page shown. A screen that reads history or the service reads while it shows and stops when it does not.</summary>
    public Page Page
    {
        get => _page;
        set
        {
            if (!SetProperty(ref _page, value)) return;
            if (value == Page.Breakdown) Breakdown.Show();
            else Breakdown.Hide();
            if (value == Page.Report) Report.Show();
            else Report.Hide();
            if (value == Page.Settings) Settings.Show();
            else Settings.Hide();
            OnPropertyChanged(nameof(Current));
        }
    }

    public object Current => Page switch
    {
        Page.Now => Now,
        Page.Breakdown => Breakdown,
        Page.Report => Report,
        _ => Settings,
    };
}
