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

/// <summary>A screen a later build brings; until then it says so.</summary>
internal sealed record PlaceholderViewModel(string Title, string Text);

/// <summary>The window: which page shows, the screens, and the version in the title bar.</summary>
internal sealed class ShellViewModel(NowViewModel now, BreakdownViewModel breakdown, string version) : ObservableObject
{
    private static readonly Dictionary<Page, PlaceholderViewModel> Placeholders = new()
    {
        [Page.Report] = new("Report", "The energy bill, comparisons and exports arrive in the next build."),
        [Page.Settings] = new("Settings", "Tariff, machine profile and preferences arrive in the next build."),
    };

    private Page _page = Page.Now;

    public NowViewModel Now { get; } = now;

    public BreakdownViewModel Breakdown { get; } = breakdown;

    public string Version { get; } = version;

    /// <summary>The page shown. A history screen reads while it shows and stops when it does not.</summary>
    public Page Page
    {
        get => _page;
        set
        {
            if (!SetProperty(ref _page, value)) return;
            if (value == Page.Breakdown) Breakdown.Show();
            else Breakdown.Hide();
            OnPropertyChanged(nameof(Current));
        }
    }

    public object Current => Page switch
    {
        Page.Now => Now,
        Page.Breakdown => Breakdown,
        _ => Placeholders[Page],
    };
}
