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

/// <summary>A screen Plan D2 builds; until then it says so.</summary>
internal sealed record PlaceholderViewModel(string Title, string Text);

/// <summary>The window: which page shows, the Now screen, and the version in the title bar.</summary>
internal sealed class ShellViewModel(NowViewModel now, string version) : ObservableObject
{
    private static readonly Dictionary<Page, PlaceholderViewModel> Placeholders = new()
    {
        [Page.Breakdown] = new("Breakdown", "Power by component over any range arrives in the next build."),
        [Page.Report] = new("Report", "The energy bill, comparisons and exports arrive in the next build."),
        [Page.Settings] = new("Settings", "Tariff, machine profile and preferences arrive in the next build."),
    };

    private Page _page = Page.Now;

    public NowViewModel Now { get; } = now;

    public string Version { get; } = version;

    public Page Page
    {
        get => _page;
        set
        {
            if (SetProperty(ref _page, value)) OnPropertyChanged(nameof(Current));
        }
    }

    public object Current => Page == Page.Now ? Now : Placeholders[Page];
}
