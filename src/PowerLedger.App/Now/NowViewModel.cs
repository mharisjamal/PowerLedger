using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>
/// The Now screen (spec §9): the live reading and its quality, the last minute, the meter, the power budget, today's and
/// the month's ledgers, and today's chart. Readings arrive on the link's thread and are posted to the UI thread; history
/// is read every minute and the status and settings every ten seconds, both off the UI thread.
/// </summary>
internal sealed partial class NowViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan HistoryEvery = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ConnectGrace = TimeSpan.FromSeconds(3);

    private readonly IServiceLink _link;
    private readonly IHistory _history;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private double _co2KgPerKwh;
    private readonly LiveWindow _window = new(TimeSpan.FromSeconds(60));
    private ITimer? _historyTimer;
    private ITimer? _statusTimer;
    private ITimer? _graceTimer;
    private HistorySnapshot? _snapshot;
    private ServiceSettings? _settings;
    private int _monitors;

    /// <summary>How the figures of the monitors counted were got, by the least sure of them: "estimated", "brightness
    /// assumed", or null when each was measured for its model at a brightness read from it, or typed.</summary>
    private string? _monitorFigures;

    private ReadingFrame? _last;
    private double _livePeak;
    private DateOnly _liveDay;

    private LivePanel _live = LivePanel.Waiting;
    private TodayLedger _today = TodayLedger.Empty;
    private MonthLedger _month = MonthLedger.Empty;
    private ChartModel _chart = ChartModel.Empty;
    private ChartLegend _legend = ChartLegend.Empty;
    private StatusLine _status = StatusLine.Down;
    private Connection _connection = Connection.Connecting;
    private bool _isSensorless;
    private bool _isCollecting = true;

    public NowViewModel(
        IServiceLink link, IHistory history, UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture,
        double co2KgPerKwh, Action startService)
    {
        _link = link;
        _history = history;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        _co2KgPerKwh = co2KgPerKwh;
        StartService = new RelayCommand(startService);
        _link.FrameReceived += OnFrame;
        _link.ConnectionChanged += OnConnectionChanged;
    }

    public LivePanel Live { get => _live; private set => SetProperty(ref _live, value); }

    public TodayLedger Today { get => _today; private set => SetProperty(ref _today, value); }

    public MonthLedger Month { get => _month; private set => SetProperty(ref _month, value); }

    /// <summary>Today's stacked chart (spec §9).</summary>
    public ChartModel Chart { get => _chart; private set => SetProperty(ref _chart, value); }

    public ChartLegend Legend { get => _legend; private set => SetProperty(ref _legend, value); }

    /// <summary>Kilograms of CO₂ per kWh for today's ledger; a new factor redraws it (Settings, spec §9).</summary>
    public double Co2KgPerKwh
    {
        get => _co2KgPerKwh;
        set
        {
            _co2KgPerKwh = value;
            if (_snapshot is not null) Today = TodayOf(_snapshot, TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone));
        }
    }

    public StatusLine Status { get => _status; private set => SetProperty(ref _status, value); }

    public Connection Connection
    {
        get => _connection;
        private set
        {
            if (SetProperty(ref _connection, value)) OnPropertyChanged(nameof(IsServiceDown));
        }
    }

    public bool IsServiceDown => Connection == Connection.Down;

    public bool IsSensorless { get => _isSensorless; private set => SetProperty(ref _isSensorless, value); }

    public bool IsCollecting { get => _isCollecting; private set => SetProperty(ref _isCollecting, value); }

    /// <summary>"Start service" on the banner (spec §9: a Start button, through UAC).</summary>
    public ICommand StartService { get; }

    /// <summary>The newest reading, for the tray icon.</summary>
    public ReadingFrame? Last => _last;

    /// <summary>The tray's tooltip: now and today (spec §9).</summary>
    public string TrayTooltip => _last is { } frame
        ? $"{Format.Watts(frame.TotalW, _culture)} W · {frame.Quality}\nToday {Today.Energy} kWh · {Today.Cost}"
        : IsServiceDown ? "PowerLedger · service not running" : "PowerLedger · waiting for the service";

    /// <summary>Starts the timers; the first history read happens at once. Call once, on the UI thread.</summary>
    public void Start()
    {
        _historyTimer = _clock.CreateTimer(_ => RefreshHistory(), null, TimeSpan.Zero, HistoryEvery);
        _statusTimer = _clock.CreateTimer(_ => _ = PollAsync(), null, StatusEvery, StatusEvery);
        _graceTimer = _clock.CreateTimer(_ => _threads.Post(EndGrace), null, ConnectGrace, Timeout.InfiniteTimeSpan);
        if (_link.IsConnected) OnConnectionChanged(true);
    }

    /// <summary>Reads history and hands it to the UI thread. Called off the UI thread.</summary>
    internal void RefreshHistory()
    {
        var snapshot = _history.Read(_clock.GetUtcNow(), _zone);
        _threads.Post(() => ApplyHistory(snapshot));
    }

    /// <summary>Asks the service for its status and its settings, which the wizard or Settings may have changed since the
    /// last poll. Nothing waits on it, so it never throws: a poll that fails is simply retried by the next.</summary>
    internal async Task PollAsync()
    {
        try
        {
            if (!_link.IsConnected) return;
            var status = await _link.GetStatusAsync().ConfigureAwait(false);
            var settings = await _link.GetSettingsAsync().ConfigureAwait(false);
            _threads.Post(() => ApplyStatus(status, settings));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The next poll tries again.
        }
    }

    public void Dispose()
    {
        _link.FrameReceived -= OnFrame;
        _link.ConnectionChanged -= OnConnectionChanged;
        _historyTimer?.Dispose();
        _statusTimer?.Dispose();
        _graceTimer?.Dispose();
    }

    private void OnFrame(ReadingFrame frame) => _threads.Post(() => ApplyFrame(frame));

    private void OnConnectionChanged(bool connected) => _threads.Post(() =>
    {
        Connection = connected ? Connection.Connected : Connection.Down;
        if (connected)
        {
            _settings = null;
            _threads.Background(() =>
            {
                RefreshHistory();
                _ = PollAsync();
            });
            return;
        }
        _last = null;
        _window.Clear();
        Live = LivePanel.Waiting;
        Status = StatusLine.Down;
        OnPropertyChanged(nameof(Last));
        OnPropertyChanged(nameof(TrayTooltip));
    });

    private void EndGrace()
    {
        if (Connection != Connection.Connecting) return;
        Connection = Connection.Down;
        OnPropertyChanged(nameof(TrayTooltip));
    }

    private void ApplyFrame(ReadingFrame frame)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(frame.Timestamp, _zone).DateTime);
        if (day != _liveDay)
        {
            _liveDay = day;
            _livePeak = 0;
        }
        _livePeak = Math.Max(_livePeak, double.IsFinite(frame.TotalW) ? frame.TotalW : 0);
        _window.Add(frame.Timestamp, frame.TotalW);
        _last = frame;
        RebuildLive();
        OnPropertyChanged(nameof(Last));
    }

    private void RebuildLive()
    {
        if (_last is not { } frame)
        {
            Live = LivePanel.Waiting;
            return;
        }
        var local = TimeZoneInfo.ConvertTime(frame.Timestamp, _zone);
        var today = _snapshot?.Today;
        var peak = Math.Max(today?.PeakW ?? 0, _livePeak);
        var average = today is { OnHours: > 0 } ? today.AvgW : frame.TotalW;
        var spark = _window.Samples;
        var shares = Budget.Of(frame);
        Live = new LivePanel(
            frame.TotalW,
            $"Live · {Source(frame.Quality)} · {local.ToString("HH:mm:ss", _culture)}",
            frame.Quality, Note(frame.Quality), spark,
            MeterRange.For(Math.Max(peak, spark.Count > 0 ? spark.Max(s => s.Watts) : 0)),
            average, peak,
            [.. shares.Select(share => Row(share, frame))],
            shares.Sum(share => share.Watts));
        OnPropertyChanged(nameof(TrayTooltip));
    }

    private void ApplyHistory(HistorySnapshot? snapshot)
    {
        _snapshot = snapshot;
        IsCollecting = snapshot is null || snapshot.Today.OnHours <= 0;
        if (snapshot is null)
        {
            Today = TodayLedger.Empty;
            Month = MonthLedger.Empty;
            Chart = ChartModel.Empty;
            Legend = ChartLegend.Empty;
        }
        else
        {
            var local = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone);
            Today = TodayOf(snapshot, local);
            Month = MonthOf(snapshot, local);
            Chart = Charts.Build(snapshot.TodayRange, snapshot.TodaySeries, ChartUnit.Watts, _zone, _culture);
            Legend = new ChartLegend(Wh(snapshot.Today.CpuKwh), Wh(snapshot.Today.GpuKwh), Wh(snapshot.Today.DisplayKwh), Wh(snapshot.Today.RestKwh));
        }
        RebuildLive();
    }

    /// <summary>A desktop's readings never come from a battery: the one Windows shows there is a UPS, which powers more
    /// than the machine. So on a desktop the battery is no power sensor, and there is no calibration to speak of.</summary>
    private void ApplyStatus(ServiceStatus? status, ServiceSettings? settings)
    {
        if (settings is not null) _settings = settings;
        if (status is null) return;
        var counted = status.Monitors?.Where(monitor => monitor is { Counted: true }).ToList() ?? [];
        _monitors = counted.Count;
        _monitorFigures = counted.Exists(monitor => monitor.Source == MonitorSource.Estimate) ? "estimated"
            : counted.Exists(monitor => monitor.Source == MonitorSource.Model && !(monitor.Brightness is { } brightness && double.IsFinite(brightness)))
                ? "brightness assumed"
                : null;
        var desktop = _settings?.Profile.Chassis == ChassisKind.Desktop;
        IsSensorless = !Supported(status, "energy-meter") && (desktop || !Supported(status, "battery"));
        var interval = _settings?.SampleIntervalSeconds ?? 1;
        var learned = Format.Duration(status.Calibration.BatterySamples * interval / 3600.0);
        var needed = Format.Duration(status.Calibration.SamplesNeeded * interval / 3600.0);
        var calibration = desktop ? "Desktop · always estimated"
            : status.Calibration.TrustedBuckets > 0 ? $"Calibration {learned} on battery"
            : $"Calibrating · {learned} of {needed} on battery";
        Status = new StatusLine(
            true,
            $"Service {status.Version.Split('+')[0]}",
            $"Sampling {interval.ToString(_culture)} s",
            calibration,
            $"Database {Megabytes(status.DatabaseBytes)}",
            status.WriteProblem ?? status.DatabaseNotice);
        if (_last is not null) RebuildLive();
    }

    private TodayLedger TodayOf(HistorySnapshot snapshot, DateTimeOffset local)
    {
        var t = snapshot.Today;
        var tariff = snapshot.Tariff;
        return new TodayLedger(
            local.ToString("ddd d MMM", _culture),
            Format.Kwh(t.EnergyKwh, _culture),
            t.Currency is { } currency ? Money.Format(t.Cost, currency, _culture) : Format.Missing,
            tariff is null ? "no tariff set" : $"at {Money.Rate(tariff.PricePerKwh, tariff.Currency, _culture)} / kWh" + (t.CostIsPartial ? " · partial" : ""),
            Format.WholeWatts(t.AvgW, _culture),
            Format.WholeWatts(t.PeakW, _culture),
            t.PeakAt is { } at ? "at " + TimeZoneInfo.ConvertTime(at, _zone).ToString("HH:mm", _culture) : "",
            Format.Duration(t.OnHours),
            Format.Duration(t.IdleOnHours),
            $"{Format.WholeWatts(t.IdleOnKwh * 1000, _culture)} Wh wasted",
            Format.Duration(t.AsleepHours),
            Format.Kg(t.EnergyKwh * _co2KgPerKwh, _culture),
            $"{_co2KgPerKwh.ToString("0.00", _culture)} kg / kWh grid");
    }

    private MonthLedger MonthOf(HistorySnapshot snapshot, DateTimeOffset local)
    {
        var o = MonthOutlook.From(snapshot.Month, snapshot.MonthDays, local);
        string Cash(decimal? amount) => amount is { } value && o.Currency is { } currency ? Money.Format(value, currency, _culture) : Format.Missing;
        string DayEnergy(DayTotals? day) => day is null ? Format.Missing : Format.Kwh(day.EnergyKwh, _culture) + " kWh";
        string DayDate(DayTotals? day) => day is null ? "" : day.Day.ToString("ddd d MMM", _culture);
        return new MonthLedger(
            local.ToString("MMMM", _culture),
            $"{o.DaysRecorded.ToString(_culture)} {(o.DaysRecorded == 1 ? "day" : "days")} · {Format.Percent(o.MeasuredShare, _culture)} measured",
            Format.Kwh(o.EnergyKwh, _culture),
            Cash(o.Cost),
            Cash(o.ProjectedCost),
            o.ProjectedKwh is { } projected ? Format.Kwh(projected, _culture) + " kWh" : "after a full day",
            o.DailyAverageKwh is { } average ? Format.Kwh(average, _culture) + " kWh" : Format.Missing,
            Format.Kwh(o.IdleWasteKwh, _culture) + " kWh",
            o.Currency is null ? "" : Cash(o.IdleWasteCost),
            DayEnergy(o.Lowest), DayDate(o.Lowest), DayEnergy(o.Highest), DayDate(o.Highest),
            o.NextReport.ToString("d MMM", _culture));
    }

    private BudgetRow Row(BudgetShare share, ReadingFrame frame)
    {
        var machine = _snapshot?.Machine;
        var (name, detail) = share.Part switch
        {
            Part.Cpu => ("CPU package", Join(ShortName(machine?.Cpu), Load(frame.CpuLoad), frame.CpuMeasured ? "measured" : "modelled")),
            Part.Gpu => ("GPU", GpuDetail(machine?.Gpu, frame)),
            Part.Display => ("Display", DisplayDetail(machine, frame)),
            _ => ("Rest of system", Join("RAM, SSD, board, radios", Remainder(frame.Quality))),
        };
        return new BudgetRow(share.Part, name, detail, Format.Watts(share.Watts, _culture) + " W", Format.Percent(share.Share, _culture), share.Share);
    }

    private string GpuDetail(string? gpu, ReadingFrame frame)
    {
        var name = ShortName(gpu);
        if (frame.GpuMeasured && frame.Components.Gpu <= 0 && (frame.GpuLoad ?? 0) <= 0) return Join(name, "switched off");
        if (!frame.GpuMeasured && frame.GpuLoad is null && frame.Components.Gpu <= 0) return "no discrete GPU the service can read";
        return Join(name, Load(frame.GpuLoad), frame.GpuMeasured ? "measured" : "modelled");
    }

    /// <summary>The display band: the built-in panel, as big and bright as it is, plus the external monitors the service
    /// counts (Plan J), and how the least sure of their figures was got: "plus 2 monitors, estimated".</summary>
    private string DisplayDetail(MachineNames? machine, ReadingFrame frame)
    {
        if (!frame.DisplayOn) return "display off";
        var size = machine is { DisplayDiagonalInches: > 0 } ? machine.DisplayDiagonalInches.ToString("0.#", _culture) + " in" : null;
        var brightness = frame.Brightness is { } b ? "brightness " + Format.Percent(b, _culture) : null;
        var panel = Join(size, brightness);
        var monitors = (_monitors == 1 ? "1 monitor" : $"{_monitors.ToString(_culture)} monitors") + (_monitorFigures is { } figures ? ", " + figures : "");
        return (panel.Length > 0, _monitors > 0) switch
        {
            (true, true) => $"{panel} · plus {monitors}",
            (true, false) => panel,
            (false, true) => monitors,
            _ => frame.Components.Display > 0 ? "built-in panel" : "nothing counted",
        };
    }

    private string? Load(double? load) => load is { } value ? Format.Percent(value, _culture) + " load" : null;

    private string Wh(double kwh) => Format.WholeWatts(kwh * 1000, _culture) + " Wh";

    private string Megabytes(long bytes)
    {
        var mb = bytes / (1024.0 * 1024.0);
        return (mb < 10 ? mb.ToString("0.0", _culture) : mb.ToString("0", _culture)) + " MB";
    }

    private string Note(Quality quality) => quality switch
    {
        Quality.Measured => $"Windows battery report · {(_settings?.SampleIntervalSeconds ?? 1).ToString(_culture)} s samples",
        Quality.Calibrated => "Model with a baseline learned on battery · ±10%",
        _ => "Model from the sensors and the machine profile · ±20%",
    };

    private static string Source(Quality quality) => quality switch
    {
        Quality.Measured => "battery discharge",
        Quality.Calibrated => "calibrated model",
        _ => "estimate",
    };

    private static string Remainder(Quality quality) => quality switch
    {
        Quality.Measured => "measured remainder",
        Quality.Calibrated => "learned baseline",
        _ => "estimated",
    };

    private static bool Supported(ServiceStatus status, string source) => status.Sources.Any(s => s.Name == source && s.Supported);

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>"11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz" becomes "Core i7-1165G7".</summary>
    internal static string? ShortName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Spaces().Replace(Noise().Replace(name, " "), " ").Trim();
    }

    [GeneratedRegex(@"\((R|TM|C)\)|@.*$|\b\d+(st|nd|rd|th) Gen\b|\bIntel\b|\bAMD\b|\bNVIDIA\b|\bProcessor\b|\bCPU\b|\bwith Radeon Graphics\b|\b\d+-Core\b", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
