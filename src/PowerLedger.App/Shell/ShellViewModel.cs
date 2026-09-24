using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PowerLedger.App;

/// <summary>The screens of spec §9's rail, and Midnight's Dashboard (Midnight look design §2), which stands in for Now
/// there as Now stands in for it in Classic.</summary>
internal enum Page
{
    Now,
    Breakdown,
    Report,
    Household,
    Settings,
    Dashboard,
}

/// <summary>The window: which page shows, the screens, the first-run wizard while it runs, and the version in the title bar.</summary>
internal sealed class ShellViewModel : ObservableObject
{
    private Page _page = Page.Now;
    private bool _isSetup;

    public ShellViewModel(
        NowViewModel now, BreakdownViewModel breakdown, ReportViewModel report, HouseholdViewModel household, SettingsViewModel settings,
        WizardViewModel wizard, string version, Updater? updates = null, DashboardViewModel? dashboard = null)
    {
        Now = now;
        Breakdown = breakdown;
        Report = report;
        Household = household;
        Settings = settings;
        Wizard = wizard;
        Version = version;
        Updates = updates;
        Dashboard = dashboard;
        Wizard.Finished += EndSetup;
        Settings.SetupRequested += BeginSetup;
        Feedback = new RelayCommand(() => FeedbackRequested?.Invoke());
    }

    public NowViewModel Now { get; }

    /// <summary>Midnight's landing page (Midnight look design §4); a Classic-only App has none, and shows Now for it.</summary>
    public DashboardViewModel? Dashboard { get; }

    public BreakdownViewModel Breakdown { get; }

    public ReportViewModel Report { get; }

    public HouseholdViewModel Household { get; }

    public SettingsViewModel Settings { get; }

    public WizardViewModel Wizard { get; }

    public string Version { get; }

    /// <summary>The update card in the rail (spec §13); without one the card stays hidden.</summary>
    public Updater? Updates { get; }

    /// <summary>The rail's Send feedback button.</summary>
    public IRelayCommand Feedback { get; }

    /// <summary>Raised when the rail's Send feedback button is pressed; the App opens the window.</summary>
    public event Action? FeedbackRequested;

    /// <summary>The page shown. A screen that reads history or the service reads while it shows and stops when it does not.</summary>
    public Page Page
    {
        get => _page;
        set
        {
            if (!SetProperty(ref _page, value)) return;
            ShowPage();
            OnPropertyChanged(nameof(Current));
        }
    }

    /// <summary>The wizard has the window: the rail and the screens wait until it is finished.</summary>
    public bool IsSetup
    {
        get => _isSetup;
        private set
        {
            if (!SetProperty(ref _isSetup, value)) return;
            ShowPage();
            OnPropertyChanged(nameof(Current));
        }
    }

    public object Current => IsSetup ? Wizard : Page switch
    {
        Page.Now => Now,
        Page.Dashboard => Dashboard ?? (object)Now,
        Page.Breakdown => Breakdown,
        Page.Report => Report,
        Page.Household => Household,
        _ => Settings,
    };

    /// <summary>Shows the first-run wizard from its first step.</summary>
    public void BeginSetup()
    {
        Wizard.Start();
        IsSetup = true;
    }

    /// <summary>The wizard is done: back to the landing page, Now or Midnight's Dashboard by the look in use.</summary>
    public void EndSetup()
    {
        _page = Settings.Look == Look.Midnight && Dashboard is not null ? Page.Dashboard : Page.Now;
        OnPropertyChanged(nameof(Page));
        IsSetup = false;
    }

    /// <summary>Lets only the screen on show read, and none while the wizard runs.</summary>
    private void ShowPage()
    {
        var shown = IsSetup ? (Page?)null : Page;
        if (shown == Page.Dashboard) Dashboard?.Show();
        else Dashboard?.Hide();
        if (shown == Page.Breakdown) Breakdown.Show();
        else Breakdown.Hide();
        if (shown == Page.Report) Report.Show();
        else Report.Hide();
        if (shown == Page.Household) Household.Show();
        else Household.Hide();
        if (shown == Page.Settings) Settings.Show();
        else Settings.Hide();
    }
}
