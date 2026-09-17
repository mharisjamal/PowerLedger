using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
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
/// which no longer reaches the model, always goes back as it came. When the settings loaded are behind the service's, as
/// they are once it has carried the old monitor settings over, a save reads them again and takes from them what the form
/// doesn't show, so it never puts back what was carried over; what the user typed and ticked stays. The UPSes and power
/// supplies the service reads are listed as it says, with what the user says a UPS powers and whether a power supply is read.
/// A form that saves itself,
/// as Settings' does, sends the settings whenever the user changes a value, one save at a time, while what the form fills in
/// itself, from the service's settings or its status, sends nothing. The wizard's form saves when asked.
/// </summary>
internal sealed class ServiceForm : ObservableObject
{
    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly CultureInfo _culture;
    private readonly bool _savesItself;
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
    private UpsLoad _upsLoad = MachineProfile.DefaultLaptop.UpsLoad;
    private bool _readPowerSupply = MachineProfile.DefaultLaptop.ReadPowerSupply;
    private IReadOnlyList<string> _upses = [];
    private IReadOnlyList<string> _powerSupplies = [];

    /// <summary>The form is changing what it shows itself, as it loads the settings, follows the service's word for the
    /// monitors or takes the settings a save read again or sent, so nothing it changes is taken for the user's change.</summary>
    private bool _quiet;

    /// <summary>A save the form started by itself is on its way.</summary>
    private bool _saving;

    /// <summary>The user changed something while that save was on its way, so the settings are sent again once it is done.</summary>
    private bool _changedSince;

    /// <param name="savesItself">Send the settings whenever the user changes something, as Settings does; the wizard sends
    /// them when Next is pressed.</param>
    public ServiceForm(IServiceLink link, UiThreads threads, CultureInfo culture, bool savesItself = false)
    {
        _link = link;
        _threads = threads;
        _culture = culture;
        _savesItself = savesItself;
    }

    /// <summary>Raised on the UI thread once the service has taken the settings, with the settings it took.</summary>
    public event Action<ServiceSettings>? Saved;

    /// <summary>The service has sent its settings, so the form holds real values.</summary>
    public bool IsLoaded { get => _isLoaded; private set => SetProperty(ref _isLoaded, value); }

    public ChassisKind Chassis
    {
        get => _chassis;
        set
        {
            if (!SetProperty(ref _chassis, value)) return;
            OnPropertyChanged(nameof(IsDesktop));
            OnChanged();
        }
    }

    /// <summary>A desktop's power supply matters; a laptop's adapter is modelled instead.</summary>
    public bool IsDesktop => Chassis == ChassisKind.Desktop;

    public PsuTier PsuTier { get => _psuTier; set => Change(ref _psuTier, value); }

    public string RamSticks { get => _ramSticks; set => Change(ref _ramSticks, value); }

    public bool RamIsDdr5 { get => _ramIsDdr5; set => Change(ref _ramIsDdr5, value); }

    public string SsdCount { get => _ssdCount; set => Change(ref _ssdCount, value); }

    public string HddCount { get => _hddCount; set => Change(ref _hddCount, value); }

    public string FanCount { get => _fanCount; set => Change(ref _fanCount, value); }

    /// <summary>The built-in panel's diagonal in inches; 0 for none.</summary>
    public string PanelInches { get => _panelInches; set => Change(ref _panelInches, value); }

    /// <summary>The external monitors the service detected, each with the user's choice for it (Plan J).</summary>
    public ObservableCollection<MonitorRow> Monitors { get; } = [];

    /// <summary>The monitor rows show only when there are monitors to list.</summary>
    public bool HasMonitors => Monitors.Count > 0;

    /// <summary>Each UPS the service reads, in a line: "UPS · APC Back-UPS ES 850G2 · 142 W (load of its rated watts)".</summary>
    public IReadOnlyList<string> Upses
    {
        get => _upses;
        private set
        {
            if (SetProperty(ref _upses, value)) OnPropertyChanged(nameof(HasUps));
        }
    }

    /// <summary>Each power supply the service reads, in a line: "Power supply · Corsair HX1000i · 312 W (DC output, all
    /// rails)".</summary>
    public IReadOnlyList<string> PowerSupplies
    {
        get => _powerSupplies;
        private set
        {
            if (!SetProperty(ref _powerSupplies, value)) return;
            OnPropertyChanged(nameof(HasPowerSupply));
            OnPropertyChanged(nameof(AsksToReadPowerSupply));
        }
    }

    /// <summary>What a UPS powers is asked only where there is one.</summary>
    public bool HasUps => Upses.Count > 0;

    public bool HasPowerSupply => PowerSupplies.Count > 0;

    /// <summary>Whether the "Read this power supply" tick shows: while a power supply is listed, and while reading one is off,
    /// since the service then leaves it to its maker's program and may stop naming it, and the tick could never be ticked
    /// again.</summary>
    public bool AsksToReadPowerSupply => HasPowerSupply || !ReadPowerSupply;

    /// <summary>What the user says a UPS on USB powers. Its reading stands for this PC only once they say this PC, alone or
    /// with its monitors.</summary>
    public UpsLoad UpsLoad { get => _upsLoad; set => Change(ref _upsLoad, value); }

    /// <summary>Whether a power supply that reports over USB is read.</summary>
    public bool ReadPowerSupply
    {
        get => _readPowerSupply;
        set
        {
            if (!SetProperty(ref _readPowerSupply, value)) return;
            OnPropertyChanged(nameof(AsksToReadPowerSupply));
            OnChanged();
        }
    }

    public string ExtrasWatts { get => _extrasWatts; set => Change(ref _extrasWatts, value); }

    /// <summary>The processor's rated watts; blank uses the bundled table.</summary>
    public string CpuTdp { get => _cpuTdp; set => Change(ref _cpuTdp, value); }

    /// <summary>The graphics card's rated watts; blank uses the bundled table.</summary>
    public string GpuTdp { get => _gpuTdp; set => Change(ref _gpuTdp, value); }

    public string IdleMinutes { get => _idleMinutes; set => Change(ref _idleMinutes, value); }

    /// <summary>"1" to "5" seconds, chosen with segmented buttons.</summary>
    public string SampleInterval { get => _sampleInterval; set => Change(ref _sampleInterval, value); }

    public string RawHours { get => _rawHours; set => Change(ref _rawHours, value); }

    public string HistoryYears { get => _historyYears; set => Change(ref _historyYears, value); }

    /// <summary>"Saving…", "Saved." or why the settings weren't sent or taken.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>Fills the form from the service's settings, and clears what was said of the last save. The monitors are listed
    /// afresh by <see cref="ShowMonitors"/>, with the choices these settings hold.</summary>
    public void Load(ServiceSettings settings) => Quietly(() =>
    {
        var p = settings.Profile;
        _profile = p;
        _choices = p.Monitors ?? [];   // settings from a service before monitors hold no list
        foreach (var row in Monitors) row.Changed -= OnChanged;
        Monitors.Clear();
        OnPropertyChanged(nameof(HasMonitors));
        Message = null;
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
        UpsLoad = p.UpsLoad;
        ReadPowerSupply = p.ReadPowerSupply;
        IsLoaded = true;
    });

    /// <summary>Lists the UPSes and power supplies in the service's status, each in a line of its own, in its order. Call on
    /// the UI thread.</summary>
    public void ShowPowerDevices(IReadOnlyList<PowerDeviceStatus> devices)
    {
        var listed = devices.OfType<PowerDeviceStatus>().ToList();
        Upses = [.. listed.Where(device => device.Kind == PowerDeviceKind.Ups).Select(device => Line("UPS", device))];
        PowerSupplies = [.. listed.Where(device => device.Kind == PowerDeviceKind.PowerSupply).Select(device => Line("Power supply", device))];
    }

    /// <summary>Lists the external monitors in the service's status, once each and in its order, with the user's choice for
    /// each from the settings last loaded, or read again or sent by a save. A monitor already listed keeps its row, and with
    /// it what the user typed and ticked, so a status read while a figure is being typed takes nothing away; a monitor
    /// unplugged goes.</summary>
    public void ShowMonitors(IReadOnlyList<MonitorStatus> monitors)
    {
        var listed = monitors.OfType<MonitorStatus>().DistinctBy(monitor => monitor.Key).ToList();
        for (var index = Monitors.Count - 1; index >= 0; index--)
        {
            if (listed.Exists(monitor => monitor.Key == Monitors[index].Key)) continue;
            Monitors[index].Changed -= OnChanged;
            Monitors.RemoveAt(index);
        }
        for (var index = 0; index < listed.Count; index++)
        {
            var monitor = listed[index];
            var row = Monitors.FirstOrDefault(candidate => candidate.Key == monitor.Key);
            if (row is null)
            {
                row = new MonitorRow(monitor, ChoiceFor(monitor.Key), _culture);
                row.Changed += OnChanged;
                Monitors.Insert(index, row);
                continue;
            }
            var at = Monitors.IndexOf(row);
            if (at != index) Monitors.Move(at, index);
            Quietly(() => row.Refresh(monitor));
        }
        OnPropertyChanged(nameof(HasMonitors));
    }

    /// <summary>Sends the settings typed, and says whether the service took them. When the settings loaded are behind the
    /// service's, its settings are read again first, and nothing is sent without them. Settings the service took are the
    /// ones the form holds from then on.</summary>
    public async Task<bool> SaveAsync()
    {
        if (Read(out var problem) is not { } settings)
        {
            Message = problem;
            return false;
        }
        Message = "Saving…";
        if (IsBehind)
        {
            var current = await _link.GetSettingsAsync().ConfigureAwait(false);
            var caughtUp = new TaskCompletionSource<ServiceSettings?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _threads.Post(() => caughtUp.SetResult(CatchUp(current)));   // the rows are the UI thread's
            if (await caughtUp.Task.ConfigureAwait(false) is not { } read) return false;
            settings = read;
        }
        var result = await _link.SetSettingsAsync(settings).ConfigureAwait(false);
        _threads.Post(() =>
        {
            Message = result.Succeeded ? "Saved." : result.Problem;
            if (!result.Succeeded) return;
            TakeSaved(settings);
            Saved?.Invoke(settings);
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
                UpsLoad = UpsLoad, ReadPowerSupply = ReadPowerSupply,
            },
            IdleThresholdSeconds = idle * 60,
            SampleIntervalSeconds = interval,
            RawRetentionHours = rawHours,
            HistoryRetentionYears = years,
        };
        problem = settings.Validate();
        return problem is null ? settings : null;
    }

    /// <summary>Whether the settings loaded may be behind the service's: they still hold monitor settings from before monitors
    /// were detected, which the service carries over at the first reading that finds a monitor, or the monitors listed say a
    /// monitor without a choice counts otherwise than they do.</summary>
    private bool IsBehind => _profile.ExternalMonitors > 0 || _profile.IncludeMonitors
        || Monitors.Any(row => row.CountedByDefault != _profile.CountMonitorsByDefault);

    /// <summary>
    /// The settings the form describes, once it has taken from the settings the service sent again what it doesn't show: whether
    /// a monitor without a choice counts, the monitor settings from before monitors were detected, and the choices for monitors
    /// not listed. Each monitor listed takes its choice from them where the user hasn't typed or ticked. Null, with the problem
    /// said, when the service sent none or the settings can't be sent. Call on the UI thread.
    /// </summary>
    private ServiceSettings? CatchUp(ServiceSettings? current)
    {
        if (current is null)
        {
            Message = _link.IsConnected ? "The service didn't answer, so nothing was changed." : WriteResult.NotConnected.Problem;
            return null;
        }
        _profile = current.Profile;
        _choices = current.Profile.Monitors ?? [];   // settings from a service before monitors hold no list
        Quietly(() =>
        {
            foreach (var row in Monitors) row.Take(ChoiceFor(row.Key), _profile.CountMonitorsByDefault);
        });
        if (Read(out var problem) is { } settings) return settings;
        Message = problem;
        return null;
    }

    /// <summary>
    /// The service took <paramref name="settings"/>, so the form holds them from now on as it would once loaded, without
    /// filling anything shown again: a monitor's choice is weighed against them, and a save reads the service's settings again
    /// only while they are behind. Each monitor listed takes its choice sent, which says what its boxes showed when the
    /// settings were read, so a box the user hasn't touched keeps what it shows. A figure the user has typed since stays in
    /// its box, as does a box they have ticked, for the save that follows. Call on the UI thread.
    /// </summary>
    private void TakeSaved(ServiceSettings settings)
    {
        _profile = settings.Profile;
        _choices = settings.Profile.Monitors;
        Quietly(() =>
        {
            foreach (var row in Monitors)
            {
                // A row fills a box it takes as untouched, one that shows what it last filled, but the user may have typed that
                // figure back since the settings were read.
                var watts = row.Watts;
                row.Take(ChoiceFor(row.Key), _profile.CountMonitorsByDefault);
                row.Watts = watts;
            }
        });
    }

    /// <summary>The choice the settings last taken hold for the monitor, or null.</summary>
    private MonitorChoice? ChoiceFor(string key) => _choices.FirstOrDefault(choice => choice?.Key == key);

    /// <summary>Takes a value the user set, and says so.</summary>
    private void Change<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (SetProperty(ref field, value, property)) OnChanged();
    }

    /// <summary>Makes a change the user didn't: nothing it changes is sent. Call on the UI thread.</summary>
    private void Quietly(Action change)
    {
        var quiet = _quiet;
        _quiet = true;
        try
        {
            change();
        }
        finally
        {
            _quiet = quiet;
        }
    }

    /// <summary>The user changed something. A form that saves itself sends the settings now, or, while a save it started is
    /// on its way, once that save is done. Call on the UI thread.</summary>
    private void OnChanged()
    {
        if (_quiet || !_savesItself) return;
        if (_saving)
        {
            _changedSince = true;
            return;
        }
        _saving = true;
        _ = SaveInTurnAsync();
    }

    /// <summary>Sends the settings, and once that save is done, on the UI thread, sends them again if the user changed anything
    /// while it was on its way. So one save is on its way at a time, and however many changes were made meanwhile, one more
    /// save carries the last of them.</summary>
    private async Task SaveInTurnAsync()
    {
        try
        {
            await SaveAsync().ConfigureAwait(false);
        }
        finally
        {
            _threads.Post(() =>
            {
                _saving = false;
                if (!_changedSince) return;
                _changedSince = false;
                OnChanged();
            });
        }
    }

    /// <summary>
    /// The choices that say what PowerLedger wouldn't assume: first those for the monitors listed, in their order, then those
    /// loaded for monitors not attached now, in the order loaded, as many as fit within <see cref="ServiceSettings.MaxMonitors"/>.
    /// A monitor taken as the service would take it, at PowerLedger's own figure, needs none. A listed monitor's choice is
    /// weighed against what the service said of that monitor, which took its plug for the chassis in the settings the form
    /// took: saved with another chassis, the service would take the plug afresh, so each listed monitor's plug is said as it
    /// shows. One loaded for a monitor not attached now is weighed against the profile the settings came with, and, unless it
    /// says the monitor runs off the PC, as for a monitor with a plug of its own, since only the service can guess the plug of
    /// a monitor it doesn't see. Each save puts the monitors attached first, so the choices loaded run from the monitor seen
    /// most recently, and those dropped are for the monitors unseen longest.
    /// </summary>
    private bool Choices(out IReadOnlyList<MonitorChoice> choices, ref string? problem)
    {
        choices = [];
        var listed = new List<MonitorChoice>();
        var sayPlugs = Chassis != _profile.Chassis;
        foreach (var row in Monitors)
        {
            if (!Optional(row.Watts, $"the watts for {row.Name}", out var watts, ref problem)) return false;
            var choice = row.Choice(watts, sayPlugs);
            if (SaysSomething(choice, row.CountedByDefault, row.OwnPlug)) listed.Add(choice);
        }
        var unplugged = _choices.Where(choice =>
            choice is not null && SaysSomething(choice, _profile.CountMonitorsByDefault, choice.OwnPlug ?? true)
            && !Monitors.Any(row => row.Key == choice.Key));
        choices = [.. listed, .. unplugged.Take(ServiceSettings.MaxMonitors - listed.Count)];
        return true;
    }

    /// <summary>Whether a choice says what PowerLedger wouldn't assume of a monitor without one: its plug, which a choice holds
    /// only where the service might take another, a figure typed for it, or, for a monitor with a plug of its own, that it
    /// counts where monitors don't by default or doesn't where they do. A monitor that runs off the PC always counts, so
    /// whether it counts says nothing.</summary>
    private static bool SaysSomething(MonitorChoice choice, bool countedByDefault, bool ownPlug)
        => choice.OwnPlug is not null || choice.Watts is not null || (ownPlug && choice.Counted != countedByDefault);

    /// <summary>A power device in a line: "UPS · APC Back-UPS ES 850G2 · 142 W (load of its rated watts)", with whatever the
    /// service hasn't said left out, and "not read yet" for a device it has found but not read.</summary>
    private string Line(string kind, PowerDeviceStatus device)
    {
        var name = device.Name?.Trim();
        var how = device.How?.Trim();
        var watts = device.Watts is { } value && double.IsFinite(value)
            ? Format.WholeWatts(value, _culture) + " W" + (string.IsNullOrEmpty(how) ? "" : $" ({how})")
            : "not read yet";
        return string.Join(" · ", new[] { kind, string.IsNullOrEmpty(name) ? null : name, watts }.OfType<string>());
    }

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
