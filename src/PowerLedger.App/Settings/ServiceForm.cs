using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// What the service is told (spec §8): the machine profile as detected and corrected, with the user's choices for the
/// external monitors, the idle threshold in minutes, the sample interval, and how long history is kept. Numbers are typed
/// in the user's culture. Each one that can't be read is named; then the service's own checks run before the settings are
/// sent, whole. A form the service never filled can't be saved, so a stopped service is never sent defaults. A monitor's
/// choice is sent only when it says what PowerLedger wouldn't assume, and the choices for monitors not attached now follow
/// those for the monitors listed, as many as the service takes. The monitor count and the choice to count monitors from
/// before monitors were detected go back as the service sent them while the form lists no monitor, so the service still
/// carries them over to the monitors it finds; once the form lists one, the choices it shows replace them. The old figure,
/// which no longer reaches the model, always goes back as it came.
/// </summary>
internal sealed class ServiceForm : ObservableObject
{
    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly CultureInfo _culture;
    private readonly RelayCommand _save;
    private MachineProfile _profile = MachineProfile.DefaultLaptop;
    private IReadOnlyList<MonitorChoice> _choices = [];
    private bool _isLoaded;
    private ChassisKind _chassis = MachineProfile.DefaultLaptop.Chassis;
    private PsuTier _psuTier = MachineProfile.DefaultLaptop.PsuTier;
    private string _ramSticks = "";
    private bool _ramIsDdr5;
    private string _ssdCount = "";
    private string _hddCount = "";
    private string _fanCount = "";
    private string _panelInches = "";
    private string _extrasWatts = "";
    private string _cpuTdp = "";
    private string _gpuTdp = "";
    private string _idleMinutes = "";
    private string _sampleInterval = "1";
    private string _rawHours = "";
    private string _historyYears = "";
    private string? _message;

    public ServiceForm(IServiceLink link, UiThreads threads, CultureInfo culture)
    {
        _link = link;
        _threads = threads;
        _culture = culture;
        _save = new RelayCommand(() => _ = SaveAsync(), () => IsLoaded);
    }

    /// <summary>Raised on the UI thread once the service has taken the settings.</summary>
    public event Action? Saved;

    /// <summary>The service has sent its settings, so the form holds real values.</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        private set
        {
            if (SetProperty(ref _isLoaded, value)) _save.NotifyCanExecuteChanged();
        }
    }

    public ChassisKind Chassis
    {
        get => _chassis;
        set
        {
            if (SetProperty(ref _chassis, value)) OnPropertyChanged(nameof(IsDesktop));
        }
    }

    /// <summary>A desktop's power supply matters; a laptop's adapter is modelled instead.</summary>
    public bool IsDesktop => Chassis == ChassisKind.Desktop;

    public PsuTier PsuTier { get => _psuTier; set => SetProperty(ref _psuTier, value); }

    public string RamSticks { get => _ramSticks; set => SetProperty(ref _ramSticks, value); }

    public bool RamIsDdr5 { get => _ramIsDdr5; set => SetProperty(ref _ramIsDdr5, value); }

    public string SsdCount { get => _ssdCount; set => SetProperty(ref _ssdCount, value); }

    public string HddCount { get => _hddCount; set => SetProperty(ref _hddCount, value); }

    public string FanCount { get => _fanCount; set => SetProperty(ref _fanCount, value); }

    /// <summary>The built-in panel's diagonal in inches; 0 for none.</summary>
    public string PanelInches { get => _panelInches; set => SetProperty(ref _panelInches, value); }

    /// <summary>The external monitors the service detected, each with the user's choice for it (Plan J).</summary>
    public ObservableCollection<MonitorRow> Monitors { get; } = [];

    /// <summary>The monitor rows show only when there are monitors to list.</summary>
    public bool HasMonitors => Monitors.Count > 0;

    public string ExtrasWatts { get => _extrasWatts; set => SetProperty(ref _extrasWatts, value); }

    /// <summary>The processor's rated watts; blank uses the bundled table.</summary>
    public string CpuTdp { get => _cpuTdp; set => SetProperty(ref _cpuTdp, value); }

    /// <summary>The graphics card's rated watts; blank uses the bundled table.</summary>
    public string GpuTdp { get => _gpuTdp; set => SetProperty(ref _gpuTdp, value); }

    public string IdleMinutes { get => _idleMinutes; set => SetProperty(ref _idleMinutes, value); }

    /// <summary>"1" to "5" seconds, chosen with segmented buttons.</summary>
    public string SampleInterval { get => _sampleInterval; set => SetProperty(ref _sampleInterval, value); }

    public string RawHours { get => _rawHours; set => SetProperty(ref _rawHours, value); }

    public string HistoryYears { get => _historyYears; set => SetProperty(ref _historyYears, value); }

    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    public ICommand Save => _save;

    /// <summary>Fills the form from the service's settings. The monitors are listed afresh by <see cref="ShowMonitors"/>,
    /// with the choices these settings hold.</summary>
    public void Load(ServiceSettings settings)
    {
        var p = settings.Profile;
        _profile = p;
        _choices = p.Monitors ?? [];   // settings from a service before monitors hold no list
        Monitors.Clear();
        OnPropertyChanged(nameof(HasMonitors));
        Chassis = p.Chassis;
        PsuTier = p.PsuTier;
        RamSticks = Whole(p.RamSticks);
        RamIsDdr5 = p.RamIsDdr5;
        SsdCount = Whole(p.SsdCount);
        HddCount = Whole(p.HddCount);
        FanCount = Whole(p.FanCount);
        PanelInches = Number(p.DisplayDiagonalInches);
        ExtrasWatts = Number(p.ExtrasWatts);
        CpuTdp = p.CpuTdpOverrideW is { } cpu ? Number(cpu) : "";
        GpuTdp = p.GpuTdpOverrideW is { } gpu ? Number(gpu) : "";
        IdleMinutes = Whole((int)Math.Round(settings.IdleThresholdSeconds / 60.0));
        SampleInterval = Whole(settings.SampleIntervalSeconds);
        RawHours = Whole(settings.RawRetentionHours);
        HistoryYears = Whole(settings.HistoryRetentionYears);
        IsLoaded = true;
    }

    /// <summary>Lists the external monitors in the service's status, once each and in its order, with the user's choice for
    /// each from the settings last loaded. A monitor already listed keeps its row, and with it what the user typed and
    /// ticked, so a status read while a figure is being typed takes nothing away; a monitor unplugged goes.</summary>
    public void ShowMonitors(IReadOnlyList<MonitorStatus> monitors)
    {
        var listed = monitors.OfType<MonitorStatus>().DistinctBy(monitor => monitor.Key).ToList();
        for (var index = Monitors.Count - 1; index >= 0; index--)
        {
            if (!listed.Exists(monitor => monitor.Key == Monitors[index].Key)) Monitors.RemoveAt(index);
        }
        for (var index = 0; index < listed.Count; index++)
        {
            var monitor = listed[index];
            var row = Monitors.FirstOrDefault(candidate => candidate.Key == monitor.Key);
            if (row is null)
            {
                Monitors.Insert(index, new MonitorRow(monitor, _choices.FirstOrDefault(choice => choice?.Key == monitor.Key), _culture));
                continue;
            }
            var at = Monitors.IndexOf(row);
            if (at != index) Monitors.Move(at, index);
            row.Refresh(monitor);
        }
        OnPropertyChanged(nameof(HasMonitors));
    }

    /// <summary>Sends the settings typed, and says whether the service took them.</summary>
    public async Task<bool> SaveAsync()
    {
        if (Read(out var problem) is not { } settings)
        {
            Message = problem;
            return false;
        }
        Message = "Saving…";
        var result = await _link.SetSettingsAsync(settings).ConfigureAwait(false);
        _threads.Post(() =>
        {
            Message = result.Succeeded ? "Saved." : result.Problem;
            if (result.Succeeded) Saved?.Invoke();
        });
        return result.Succeeded;
    }

    /// <summary>The settings the form describes, or null and the first problem.</summary>
    internal ServiceSettings? Read(out string? problem)
    {
        problem = !IsLoaded ? "The service hasn't sent its settings yet." : null;
        int ramSticks = 0, ssds = 0, hdds = 0, fans = 0, idle = 0, interval = 0, rawHours = 0, years = 0;
        double panel = 0, extras = 0;
        double? cpu = null, gpu = null;
        IReadOnlyList<MonitorChoice> choices = [];
        _ = problem is null
            && Int(RamSticks, "the memory sticks", out ramSticks, ref problem) && Int(SsdCount, "the SSDs", out ssds, ref problem)
            && Int(HddCount, "the hard drives", out hdds, ref problem) && Int(FanCount, "the fans", out fans, ref problem)
            && Double(PanelInches, "the panel size in inches", out panel, ref problem)
            && Choices(out choices, ref problem)
            && Double(ExtrasWatts, "the extras in watts", out extras, ref problem)
            && Optional(CpuTdp, "the processor's rated watts", out cpu, ref problem)
            && Optional(GpuTdp, "the graphics card's rated watts", out gpu, ref problem)
            && Int(IdleMinutes, "the idle threshold in minutes", out idle, ref problem)
            && Int(SampleInterval, "the sample interval", out interval, ref problem)
            && Int(RawHours, "the hours of second-by-second history", out rawHours, ref problem)
            && Int(HistoryYears, "the years of minute-by-minute history", out years, ref problem);
        if (problem is null && idle is < 1 or > 30) problem = "The idle threshold is between 1 and 30 minutes.";
        if (problem is not null) return null;

        var settings = new ServiceSettings
        {
            Profile = _profile with
            {
                Chassis = Chassis, PsuTier = PsuTier, RamSticks = ramSticks, RamIsDdr5 = RamIsDdr5, SsdCount = ssds, HddCount = hdds,
                FanCount = fans, DisplayDiagonalInches = panel, ExtrasWatts = extras, CpuTdpOverrideW = cpu, GpuTdpOverrideW = gpu,
                Monitors = choices,
                ExternalMonitors = HasMonitors ? 0 : _profile.ExternalMonitors, IncludeMonitors = !HasMonitors && _profile.IncludeMonitors,
            },
            IdleThresholdSeconds = idle * 60,
            SampleIntervalSeconds = interval,
            RawRetentionHours = rawHours,
            HistoryRetentionYears = years,
        };
        problem = settings.Validate();
        return problem is null ? settings : null;
    }

    /// <summary>
    /// The choices that say what PowerLedger wouldn't assume: first those for the monitors listed, in their order, then those
    /// loaded for monitors not attached now, in the order loaded, as many as fit within <see cref="ServiceSettings.MaxMonitors"/>.
    /// A monitor taken as the service would take it, at PowerLedger's own figure, needs none. A listed monitor's choice is
    /// weighed against what the service said of that monitor; one loaded for a monitor not attached now, against the profile
    /// the settings came with. Each save puts the monitors attached first, so the choices loaded run from the monitor seen
    /// most recently, and those dropped are for the monitors unseen longest.
    /// </summary>
    private bool Choices(out IReadOnlyList<MonitorChoice> choices, ref string? problem)
    {
        choices = [];
        var listed = new List<MonitorChoice>();
        foreach (var row in Monitors)
        {
            if (!Optional(row.Watts, $"the watts for {row.Name}", out var watts, ref problem)) return false;
            var choice = row.Choice(watts);
            if (SaysSomething(choice, row.CountedByDefault)) listed.Add(choice);
        }
        var unplugged = _choices.Where(choice =>
            choice is not null && SaysSomething(choice, _profile.CountMonitorsByDefault) && !Monitors.Any(row => row.Key == choice.Key));
        choices = [.. listed, .. unplugged.Take(ServiceSettings.MaxMonitors - listed.Count)];
        return true;
    }

    /// <summary>Whether a choice says what PowerLedger wouldn't assume of a monitor without one: that it counts where
    /// monitors don't by default, or doesn't where they do, that it has a figure typed for it, or that its plug isn't the one
    /// the service takes it to have, which is the only plug a choice holds.</summary>
    private static bool SaysSomething(MonitorChoice choice, bool countedByDefault)
        => choice.Counted != countedByDefault || choice.Watts is not null || choice.OwnPlug is not null;

    private bool Int(string text, string what, out int value, ref string? problem)
    {
        if (int.TryParse(text, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, _culture, out value)) return true;
        problem = $"Type {what} as a whole number.";
        return false;
    }

    private bool Double(string text, string what, out double value, ref string? problem)
    {
        const NumberStyles plain = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowDecimalPoint;
        if (double.TryParse(text, plain, _culture, out value) || double.TryParse(text, plain, CultureInfo.InvariantCulture, out value)) return true;
        problem = $"Type {what} as a number.";
        return false;
    }

    private bool Optional(string text, string what, out double? value, ref string? problem)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!Double(text, what, out var number, ref problem)) return false;
        value = number;
        return true;
    }

    private string Whole(int value) => value.ToString(_culture);

    private string Number(double value) => value.ToString("0.##", _culture);
}
