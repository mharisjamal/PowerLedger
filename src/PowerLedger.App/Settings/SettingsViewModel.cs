using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>A sensor source as About lists it: what it is, whether it works here, and why not.</summary>
internal sealed record SourceLine(string Name, string State, string Detail);

/// <summary>
/// The Settings screen (spec §9): the tariff and its history, the machine profile with what was detected, sampling and
/// retention, calibration with a reset that asks first, the App's own preferences, and About. It reads when shown, and the
/// status again every ten seconds while shown, off the UI thread. The service form is filled when the screen shows and
/// after a save; when the service comes up while the screen shows, only an empty form is filled, so nothing typed is lost.
/// </summary>
internal sealed class SettingsViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(10);

    private readonly IServiceLink _link;
    private readonly IMachineHistory _history;
    private readonly IUiSettings _ui;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private ITimer? _timer;
    private int _sampleSeconds = 1;
    private ChassisKind? _chassis;
    private IReadOnlyList<string> _tariffHistory = [];
    private string _detected = "";
    private string _co2;
    private string? _co2Message;
    private string? _appMessage;
    private string _calibration = "";
    private bool _confirmingReset;
    private string? _calibrationMessage;
    private string _serviceState = "";
    private IReadOnlyList<SourceLine> _sources = [];
    private string _database = "";
    private string? _notice;

    public SettingsViewModel(
        IServiceLink link, IMachineHistory history, IUiSettings ui, UiThreads threads, TimeProvider clock, TimeZoneInfo zone,
        CultureInfo culture, string regionCurrency, Updater? updates = null)
    {
        _link = link;
        _history = history;
        _ui = ui;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        _co2 = ui.Current.Co2KgPerKwh.ToString("0.00", culture);
        Tariff = new TariffForm(link, threads, clock, zone, culture, regionCurrency);
        Service = new ServiceForm(link, threads, culture);
        Tariff.Saved += ReadTariffs;
        Service.Saved += ReadSaved;
        SaveCo2 = new RelayCommand(ApplyCo2);
        ResetCalibration = new RelayCommand(() => ConfirmingReset = true);
        CancelReset = new RelayCommand(() => ConfirmingReset = false);
        ConfirmReset = new RelayCommand(() => _ = ConfirmResetAsync());
        RunSetup = new RelayCommand(() => SetupRequested?.Invoke());
        _link.ConnectionChanged += OnConnectionChanged;
        Updates = updates;
    }

    /// <summary>"Run setup again": the shell shows the wizard.</summary>
    public event Action? SetupRequested;

    public TariffForm Tariff { get; }

    /// <summary>Every tariff, newest first: "From 1 Aug 2026 · $0.17 / kWh".</summary>
    public IReadOnlyList<string> TariffHistory { get => _tariffHistory; private set => SetProperty(ref _tariffHistory, value); }

    public ServiceForm Service { get; }

    /// <summary>What the service detected, in one line.</summary>
    public string Detected { get => _detected; private set => SetProperty(ref _detected, value); }

    /// <summary>Kilograms of CO₂ per kWh, as typed.</summary>
    public string Co2 { get => _co2; set => SetProperty(ref _co2, value); }

    public string? Co2Message { get => _co2Message; private set => SetProperty(ref _co2Message, value); }

    public ICommand SaveCo2 { get; }

    /// <summary>Applies when chosen.</summary>
    public ThemeChoice Theme
    {
        get => _ui.Current.Theme;
        set
        {
            if (value == _ui.Current.Theme) return;
            AppMessage = _ui.Choose(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Applies when ticked.</summary>
    public bool StartWithWindows
    {
        get => _ui.StartsWithWindows;
        set
        {
            if (value == _ui.StartsWithWindows) return;
            AppMessage = _ui.StartWithWindows(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Applies at the next read of the monitors, within five minutes.</summary>
    public bool ReadMonitorBrightness
    {
        get => _ui.Current.ReadMonitorBrightness;
        set
        {
            if (value == _ui.Current.ReadMonitorBrightness) return;
            AppMessage = _ui.ReadMonitorBrightness(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Why a preference didn't stick, or null.</summary>
    public string? AppMessage { get => _appMessage; private set => SetProperty(ref _appMessage, value); }

    /// <summary>The Updates row (spec §13).</summary>
    public Updater? Updates { get; }

    /// <summary>How far calibration has got (spec §5).</summary>
    public string Calibration { get => _calibration; private set => SetProperty(ref _calibration, value); }

    /// <summary>The reset was pressed once; it waits for Reset or Keep.</summary>
    public bool ConfirmingReset { get => _confirmingReset; private set => SetProperty(ref _confirmingReset, value); }

    public string? CalibrationMessage { get => _calibrationMessage; private set => SetProperty(ref _calibrationMessage, value); }

    public ICommand ResetCalibration { get; }

    public ICommand ConfirmReset { get; }

    public ICommand CancelReset { get; }

    /// <summary>"Service 0.1.0 · 12,345 readings since 15 Sep 2026 07:02".</summary>
    public string ServiceState { get => _serviceState; private set => SetProperty(ref _serviceState, value); }

    public IReadOnlyList<SourceLine> Sources { get => _sources; private set => SetProperty(ref _sources, value); }

    /// <summary>The database's size on disk.</summary>
    public string Database { get => _database; private set => SetProperty(ref _database, value); }

    /// <summary>Why the service's part is empty, or null.</summary>
    public string? Notice { get => _notice; private set => SetProperty(ref _notice, value); }

    public ICommand RunSetup { get; }

    /// <summary>The page is shown: read everything now, and the status every ten seconds until hidden. Call on the UI thread.</summary>
    public void Show()
    {
        _threads.Background(() => _ = ReadAllAsync(refill: true));
        _timer ??= _clock.CreateTimer(_ => _ = ReadStatusAsync(), null, StatusEvery, StatusEvery);
    }

    public void Hide()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        Tariff.Saved -= ReadTariffs;
        Service.Saved -= ReadSaved;
        _link.ConnectionChanged -= OnConnectionChanged;
        Hide();
    }

    /// <summary>The second press of the reset: sends it, and says what came of it.</summary>
    internal async Task ConfirmResetAsync()
    {
        var result = await _link.ResetCalibrationAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            ConfirmingReset = false;
            CalibrationMessage = result.Succeeded
                ? "Calibration reset. The model learns again the next time the machine runs on battery."
                : result.Problem;
        });
        await ReadStatusAsync().ConfigureAwait(false);
    }

    /// <param name="refill">Fill the service form even when it holds values; a reconnect only fills an empty one.</param>
    private async Task ReadAllAsync(bool refill)
    {
        var settings = await _link.GetSettingsAsync().ConfigureAwait(false);
        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        var tariffs = _history.Tariffs();
        var detected = _history.Detected();
        _threads.Post(() =>
        {
            if (settings is not null)
            {
                _sampleSeconds = settings.SampleIntervalSeconds;
                _chassis = settings.Profile.Chassis;
                if (refill || !Service.IsLoaded) Service.Load(settings);
            }
            Notice = settings is null
                ? "The service isn't running. Its settings appear when it starts; the App's own preferences below work now."
                : null;
            ShowTariffs(tariffs);
            Detected = detected?.Summary(_culture) ?? "Nothing detected yet: the service detects the hardware when it starts.";
            ShowStatus(status);
        });
    }

    /// <summary>The service came up while the screen shows: read it. Raised on the link's thread.</summary>
    private void OnConnectionChanged(bool connected)
    {
        if (connected && _timer is not null) _threads.Background(() => _ = ReadAllAsync(refill: false));
    }

    private async Task ReadStatusAsync()
    {
        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        _threads.Post(() => ShowStatus(status));
    }

    /// <summary>The service took the settings: fill the form with what it now holds, while the screen shows.</summary>
    private void ReadSaved()
    {
        if (_timer is not null) _threads.Background(() => _ = ReadAllAsync(refill: true));
    }

    private void ReadTariffs()
    {
        var tariffs = _history.Tariffs();
        _threads.Post(() => ShowTariffs(tariffs));
    }

    private void ApplyCo2()
    {
        const NumberStyles plain = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowDecimalPoint;
        if (!double.TryParse(Co2, plain, _culture, out var factor) && !double.TryParse(Co2, plain, CultureInfo.InvariantCulture, out factor))
        {
            Co2Message = "Type the kilograms of CO₂ per kWh as a number, like 0.40.";
            return;
        }
        Co2Message = _ui.UseCo2(factor) ?? "Saved.";
    }

    private void ShowTariffs(IReadOnlyList<Tariff>? tariffs)
        => TariffHistory = tariffs is null
            ? []
            : [.. tariffs.OrderByDescending(t => t.EffectiveFrom).Select(t =>
                $"From {TimeZoneInfo.ConvertTime(t.EffectiveFrom, _zone).ToString("d MMM yyyy", _culture)} · {Money.Rate(t.PricePerKwh, t.Currency, _culture)} / kWh")];

    private void ShowStatus(ServiceStatus? status)
    {
        if (status is null)
        {
            ServiceState = "The service isn't running.";
            Calibration = "Unknown while the service isn't running.";
            Sources = [];
            Database = Format.Missing;
            return;
        }
        var c = status.Calibration;
        var learned = Format.Duration(c.BatterySamples * _sampleSeconds / 3600.0);
        // The battery on a desktop is a UPS, which powers more than the machine, so a desktop's readings never use it.
        Calibration = _chassis == ChassisKind.Desktop
            ? "Not used on a desktop: its readings are always estimated."
            : c.TrustedBuckets > 0
                ? $"Learned from {learned} on battery; {c.TrustedBuckets.ToString(_culture)} of {c.Buckets.ToString(_culture)} brightness levels trusted."
                : $"Learning on battery: {learned} of {Format.Duration(c.SamplesNeeded * _sampleSeconds / 3600.0)} needed.";
        ServiceState = $"Service {status.Version.Split('+')[0]} · {status.Ticks.ToString("N0", _culture)} readings since "
                       + TimeZoneInfo.ConvertTime(status.StartedAt, _zone).ToString("d MMM yyyy HH:mm", _culture);
        Sources = [.. status.Sources.Select(Line)];
        var megabytes = status.DatabaseBytes / (1024.0 * 1024.0);
        Database = (megabytes < 10 ? megabytes.ToString("0.0", _culture) : megabytes.ToString("0", _culture)) + " MB";
    }

    private static SourceLine Line(SourceStatus source)
    {
        var name = source.Name switch
        {
            "energy-meter" => "Processor energy meter",
            "battery" => "Battery",
            "cpu-load" => "Processor load",
            "nvidia-gpu" => "NVIDIA graphics",
            "gpu-load" => "Graphics load",
            "display" => "Display brightness",
            "activity" => "Display and lock state",
            _ => source.Name,
        };
        if (!source.Supported) return new SourceLine(name, "not on this machine", source.Unavailable ?? "");
        return source.Failures > 0
            ? new SourceLine(name, $"failing ({source.Failures})", source.LastError ?? "")
            : new SourceLine(name, "working", "");
    }
}
