using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>
/// The Dashboard (Midnight look design §4): three KPI cards, the area chart over a chosen range, and where the power went
/// by part over a chosen range, from the sources Classic's pages read. The live figures come from <see cref="NowViewModel"/>;
/// history is read off the UI thread when the page shows, when a range changes, and every minute while it shows, the chart
/// among it only for the hour and the day, which move. Nothing here knows a view.
/// </summary>
internal sealed class DashboardViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    /// <summary>The parts in the order the table lists them, with the glyph each shows (Segoe Fluent Icons).</summary>
    private static readonly (Part Part, string Glyph)[] PartGlyphs =
        [(Part.Cpu, "\uE950"), (Part.Gpu, "\uE7F4"), (Part.Display, "\uE7F8"), (Part.Rest, "\uE770")];

    private readonly NowViewModel _now;
    private readonly IRangeHistory _history;
    private readonly IHistory _summary;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private readonly UiThreads _threads;
    private ITimer? _timer;
    private int _reads;
    private Reading? _read;
    private Quality? _lastQuality;
    private RangePill _range = RangePill.Day;
    private PartsRange _partsRange = PartsRange.Today;
    private IReadOnlyList<KpiCard> _kpis = [];
    private ChartModel _chart = ChartModel.Empty;
    private string _chartTitle = "";
    private string? _chartMessage;
    private DateTimeOffset? _chartFrom;
    private IReadOnlyList<DashboardPart> _parts = [];
    private readonly IUiSettings? _ui;
    private EnergyPeriod _energyPeriod;

    /// <summary>One pass over the history, read together so every card agrees on the moment.</summary>
    /// <param name="Pill">The chart's range when it was read, so a later pass keeps its chart only for the same range.</param>
    /// <param name="Recent">The last 31 days, whose complete days make the average day.</param>
    /// <param name="LastMonth">Last month up to the same day and time as now.</param>
    /// <param name="Before">The parts' range a period back, for their trends.</param>
    /// <param name="FirstRow">When the history begins, or null with none: this month is the first only when it begins in it.</param>
    /// <param name="Energy">The energy card's period as it was read.</param>
    private sealed record Reading(
        RangePill Pill, DateTimeOffset LocalNow, HistorySnapshot? Snapshot, RangeReport? Recent, RangeReport? LastMonth,
        DateRange ChartRange, RangeReport? Chart, DateRange PartsWindow, RangeReport? Parts, RangeReport? Before, DateTimeOffset? FirstRow = null,
        EnergyReading? Energy = null);

    /// <summary>What the energy card's period needs beyond the snapshot, the average day and last month to date, which
    /// every pass reads anyway: today and this month need nothing more.</summary>
    /// <param name="SoFar">The period so far: this week, or everything since the start.</param>
    /// <param name="SamePoint">The period before up to the same point: last week to the same day and time.</param>
    /// <param name="Whole">The whole of the period before, which the bar measures the period so far against.</param>
    private sealed record EnergyReading(EnergyPeriod Period, RangeReport? SoFar = null, RangeReport? SamePoint = null, RangeReport? Whole = null);

    /// <param name="ui">Where the energy card's period is kept between runs; none keeps it for this run only.</param>
    public DashboardViewModel(
        NowViewModel now, IRangeHistory history, IHistory summary, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture, UiThreads threads,
        IUiSettings? ui = null, IHardwareNames? hardware = null)
    {
        _now = now;
        _history = history;
        _summary = summary;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        _threads = threads;
        _ui = ui;
        _energyPeriod = ui?.Current.EnergyPeriod ?? UiPreferences.Default.EnergyPeriod;
        ChooseEnergyPeriod = new RelayCommand<EnergyPeriod>(period => EnergyPeriod = period);
        _now.PropertyChanged += OnNowChanged;
        Rebuild();
        if (hardware is not null)
        {
            _threads.Background(() =>
            {
                var models = hardware.Read();
                _threads.Post(() =>
                {
                    _models = models;
                    Rebuild();
                });
            });
        }
    }

    /// <summary>Each part's model, once read off the UI thread; empty until then.</summary>
    private IReadOnlyDictionary<Part, string> _models = new Dictionary<Part, string>();

    /// <summary>The live reading, as the Now page has it.</summary>
    public LivePanel Live => _now.Live;

    public TodayLedger Today => _now.Today;

    public MonthLedger Month => _now.Month;

    /// <summary>The service's status, for the header's pill.</summary>
    public StatusLine Status => _now.Status;

    /// <summary>Power now, Energy used over <see cref="EnergyPeriod"/> and Idle waste this month, in that order.</summary>
    public IReadOnlyList<KpiCard> Kpis { get => _kpis; private set => SetProperty(ref _kpis, value); }

    /// <summary>The chart's range; a change reads it again at once.</summary>
    public RangePill Range
    {
        get => _range;
        set
        {
            if (SetProperty(ref _range, value)) Refresh();
        }
    }

    /// <summary>"Power over time" over <see cref="Range"/>, in watts, stacked by part with sleep hatched, as Classic draws it.</summary>
    public ChartModel Chart { get => _chart; private set => SetProperty(ref _chart, value); }

    /// <summary>The range's own name: "Last hour", "Today", "Since 3 Sep 2026".</summary>
    public string ChartTitle { get => _chartTitle; private set => SetProperty(ref _chartTitle, value); }

    /// <summary>Why the chart is empty, or null (design §5).</summary>
    public string? ChartMessage { get => _chartMessage; private set => SetProperty(ref _chartMessage, value); }

    /// <summary>When the chart's first bucket starts, null before the first read: the chart's tooltip counts its time from it.</summary>
    public DateTimeOffset? ChartFrom { get => _chartFrom; private set => SetProperty(ref _chartFrom, value); }

    /// <summary>The zone the ranges are cut in, so the tooltip tells the time as the axis does.</summary>
    public TimeZoneInfo Zone => _zone;

    /// <summary>The culture the figures are written in, for the chart's labels and tooltip too.</summary>
    public CultureInfo Culture => _culture;

    /// <summary>The parts table's range; a change reads it again at once.</summary>
    public PartsRange PartsRange
    {
        get => _partsRange;
        set
        {
            if (SetProperty(ref _partsRange, value)) Refresh();
        }
    }

    /// <summary>What the Energy used card covers, as its period menu chose it, Since start until then. A change is saved and
    /// reads the card again at once, leaving the chart as it was.</summary>
    public EnergyPeriod EnergyPeriod
    {
        get => _energyPeriod;
        set
        {
            if (!SetProperty(ref _energyPeriod, value)) return;
            _ui?.SetEnergyPeriod(value);
            Refresh(chart: false);
        }
    }

    /// <summary>The period menu's items: the period to choose.</summary>
    public IRelayCommand<EnergyPeriod> ChooseEnergyPeriod { get; }

    /// <summary>"Where the power went": one row a part, CPU first.</summary>
    public IReadOnlyList<DashboardPart> Parts { get => _parts; private set => SetProperty(ref _parts, value); }

    /// <summary>The page is shown: read now, and every minute until it is hidden. Call on the UI thread.</summary>
    public void Show()
    {
        Refresh();
        _timer ??= _clock.CreateTimer(_ => _threads.Post(Tick), null, RefreshEvery, RefreshEvery);
    }

    public void Hide()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        _now.PropertyChanged -= OnNowChanged;
        Hide();
    }

    /// <summary>A minute on: everything again, but the chart only while its range moves by the minute.</summary>
    private void Tick() => Refresh(chart: Range is RangePill.Hour or RangePill.Day);

    /// <summary>
    /// Reads the history off the UI thread; a read a newer one overtook is dropped. The chart is read again unless
    /// <paramref name="chart"/> is false and the last reading's chart is of the range still chosen: a minute's tick can
    /// start just after a pill is clicked and finish after that click's read, and must not bring the old range back. A
    /// read that throws would go with its task; it comes back as a reading of nothing, which the page shows as unread
    /// (design §5), and the next minute tries again. Review 7: a chart that wasn't read is never kept, whatever its
    /// range, and a kept one only while its range is still the one now would cut, so the first minute after midnight
    /// cuts the week, the month, the year and All again. Call on the UI thread.
    /// </summary>
    private void Refresh(bool chart = true)
    {
        var read = ++_reads;
        var pill = Range;
        var parts = PartsRange;
        var energy = EnergyPeriod;
        var kept = chart || _read is not { Chart: not null } last || last.Pill != pill ? null : _read;
        _threads.Background(() =>
        {
            var now = _clock.GetUtcNow();
            Reading reading;
            try
            {
                if (kept is not null && !SameCut(kept.ChartRange, RangeFor(pill, now))) kept = null;   // a new day
                reading = Read(now, pill, parts, energy, kept);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                reading = NothingRead(now, pill, parts) with { Energy = new EnergyReading(energy) };
            }
            _threads.Post(() =>
            {
                if (read != _reads) return;
                _read = reading;
                if (kept is null) ShowChart();
                Rebuild();
            });
        });
    }

    private Reading Read(DateTimeOffset now, RangePill pill, PartsRange parts, EnergyPeriod energy, Reading? kept)
    {
        var chartRange = kept?.ChartRange ?? RangeFor(pill, now);
        var partsRange = RangeFor(parts, now);
        var first = _summary.FirstRow();
        return new Reading(
            pill,
            TimeZoneInfo.ConvertTime(now, _zone),
            _summary.Read(now, _zone),
            _history.Read(Ranges.LastDays(31, now, _zone, _culture), _zone),
            _history.Read(LastMonthSoFar(now, _zone), _zone),
            chartRange,
            kept is not null ? kept.Chart : _history.Read(chartRange, _zone),
            partsRange,
            _history.Read(partsRange, _zone),
            _history.Read(Before(partsRange, DaysOf(parts), _zone), _zone),
            first,
            ReadEnergy(energy, now, first));
    }

    /// <summary>The energy card's own reads: since the start, from the first row's day to now; this week so far, the
    /// same stretch of last week and the whole of it; the whole of last month. Today and this month so far come from the
    /// snapshot, and last month to date from the pass's own read.</summary>
    private EnergyReading ReadEnergy(EnergyPeriod period, DateTimeOffset now, DateTimeOffset? first)
    {
        var day = TimeSpan.FromDays(1);   // the card needs the totals only; a bucket a day is the least to cut
        switch (period)
        {
            case EnergyPeriod.SinceStart:
                return new(period, _history.Read(Ranges.All(first, now, _zone, _culture) with { Title = "Since start" }, _zone));
            case EnergyPeriod.ThisWeek:
                var week = Ranges.ThisWeek(now, _zone) with { Bucket = day };
                var monday = Ranges.LocalDay(week.From, _zone);
                var lastWeek = Ranges.Days(monday.AddDays(-7), monday.AddDays(-1), now, _zone, _culture) with { Title = "All of last week", Bucket = day };
                return new(
                    period,
                    _history.Read(week, _zone),
                    _history.Read(Before(week, 7, _zone) with { Title = "Last week to date" }, _zone),
                    _history.Read(lastWeek, _zone));
            case EnergyPeriod.ThisMonth:
                return new(period, Whole: _history.Read(Ranges.LastMonth(now, _zone, _culture) with { Title = "All of last month", Bucket = day }, _zone));
            default:
                return new(period);
        }
    }

    /// <summary>A reading of nothing, for a pass that threw: the chart's range as well as it can be named, and no data.</summary>
    private Reading NothingRead(DateTimeOffset now, RangePill pill, PartsRange parts)
    {
        DateRange chartRange;
        try
        {
            chartRange = RangeFor(pill, now);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            chartRange = Ranges.Today(now, _zone, _culture);   // All asks the history where it starts
        }
        return new Reading(pill, TimeZoneInfo.ConvertTime(now, _zone), null, null, null, chartRange, null, RangeFor(parts, now), null, null);
    }

    /// <summary>Whether two ranges are cut the same: they start and end on the same days.</summary>
    private static bool SameCut(DateRange one, DateRange other) => one.From == other.From && one.Through == other.Through;

    private DateRange RangeFor(RangePill pill, DateTimeOffset now) => pill switch
    {
        RangePill.Hour => Ranges.LastHour(now, _zone, _culture),
        RangePill.Day => Ranges.Today(now, _zone, _culture),
        RangePill.Week => Ranges.LastDays(7, now, _zone, _culture),
        RangePill.Month => Ranges.LastDays(30, now, _zone, _culture),
        RangePill.Year => Ranges.LastYear(now, _zone, _culture),
        _ => Ranges.All(_summary.FirstRow(), now, _zone, _culture),
    };

    private DateRange RangeFor(PartsRange range, DateTimeOffset now) => range switch
    {
        PartsRange.Today => Ranges.Today(now, _zone, _culture),
        PartsRange.SevenDays => Ranges.LastDays(7, now, _zone, _culture),
        _ => Ranges.LastDays(30, now, _zone, _culture),
    };

    private static int DaysOf(PartsRange range) => range switch
    {
        PartsRange.Today => 1,
        PartsRange.SevenDays => 7,
        _ => 30,
    };

    /// <summary>
    /// <paramref name="range"/> a period of <paramref name="days"/> back on the local clock: today so far against yesterday
    /// from midnight to the same time, the last 7 days against the 7 before them to the same time. Calendar days, not
    /// 24-hour blocks, so across a clock change the period before starts at its own midnight.
    /// </summary>
    internal static DateRange Before(DateRange range, int days, TimeZoneInfo zone)
    {
        DateTimeOffset Back(DateTimeOffset instant) => Ranges.At(TimeZoneInfo.ConvertTime(instant, zone).DateTime.AddDays(-days), zone);
        return new DateRange(Back(range.From), Back(range.To), Back(range.Through), "Before", range.Bucket);
    }

    /// <summary>
    /// Last month from its first up to the same day of the month and time of day as now, for the idle waste's trend: a
    /// month so far against as much of the month before. A day last month lacked (the 31st after a 30-day month) takes
    /// all of it.
    /// </summary>
    internal static DateRange LastMonthSoFar(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var thisMonth = new DateTime(local.Year, local.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        var to = lastMonth + (local - thisMonth);
        if (to > thisMonth) to = thisMonth;
        var (from, until) = (Ranges.At(lastMonth, zone), Ranges.At(to, zone));
        return new DateRange(from, until, until, "Last month to date", Ranges.BucketFor(until - from));
    }

    private void OnNowChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NowViewModel.Live):
            case nameof(NowViewModel.Today):
            case nameof(NowViewModel.Month):
            case nameof(NowViewModel.Status):
                OnPropertyChanged(e.PropertyName);
                if (e.PropertyName is nameof(NowViewModel.Live)) Rebuild();
                break;
            case nameof(NowViewModel.IsServiceDown):
                Rebuild();
                break;
        }
    }

    /// <summary>The cards and the parts from the live panel and the last reading, on every live reading. The chart is
    /// not among them: it changes only with the history, so a reading a second leaves its crosshair be. On the UI thread.</summary>
    private void Rebuild()
    {
        var live = _now.Live;
        if (live.Quality is { } quality) _lastQuality = quality;
        Kpis = [PowerNow(live), EnergyUsed(), IdleWaste()];
        Parts = PartRows(live, _read);
    }

    /// <summary>The chart from a reading that read it. On the UI thread.</summary>
    private void ShowChart()
    {
        if (_read is not { } read) return;
        ChartTitle = read.ChartRange.Title;
        ChartFrom = read.ChartRange.From;
        Chart = Charts.Build(read.ChartRange, read.Chart?.Series ?? [], ChartUnit.Watts, _zone, _culture);
        // Review 8: the history gives every range its buckets, so an empty chart is one whose every bucket is empty.
        ChartMessage = read.Chart is not { } chart ? "Couldn't read the history"
            : chart.Series.Any(bucket => bucket.OnSeconds > 0 || bucket.GapSeconds > 0 || bucket.EnergyWh > 0) ? null : "No history yet";
    }

    /// <summary>Power now: the live watts over the meter's scale, with how the reading is got as the chip; "No reading" and
    /// the last known quality while the service is down (design §5).</summary>
    private KpiCard PowerNow(LivePanel live)
    {
        var watts = double.IsFinite(live.Watts) ? live.Watts : (double?)null;
        return new KpiCard(
            "Power now",
            watts is { } w ? Format.Watts(w, _culture) + " W" : Format.NoReading,
            _now.IsServiceDown ? "Service not running" : live.Eyebrow,
            _lastQuality?.ToString(),
            _lastQuality is null ? TrendKind.Text : TrendKind.Quality,
            DashboardMaths.Fill(watts ?? 0, live.Meter.Max));
    }

    /// <summary>Energy used over the period the card's menu chose, with its cost, from the period the last pass read,
    /// which a new choice's read replaces at once; before the first pass, the period chosen and nothing more.</summary>
    private KpiCard EnergyUsed()
    {
        var period = _read?.Energy?.Period ?? EnergyPeriod;
        var card = _read is not { } read ? null : period switch
        {
            EnergyPeriod.Today => TodayEnergy(read),
            EnergyPeriod.ThisWeek => WeekEnergy(read),
            EnergyPeriod.ThisMonth => MonthEnergy(read),
            _ => EnergySinceStart(read),
        };
        card ??= new KpiCard(EnergyLabel, Format.NoReading, _read is null ? "" : Unread, null, TrendKind.Text, 0)
        {
            HasBar = period != EnergyPeriod.SinceStart,
        };
        return card with { Period = PeriodName(period), LowerIsBetter = true };
    }

    private const string EnergyLabel = "Energy used";

    /// <summary>The period's name on the card's button, as the menu lists it.</summary>
    internal static string PeriodName(EnergyPeriod period) => period switch
    {
        EnergyPeriod.Today => "Today",
        EnergyPeriod.ThisWeek => "This week",
        EnergyPeriod.ThisMonth => "This month",
        _ => "Since start",
    };

    /// <summary>Today: kWh so far and its cost, against the average of the last 30 complete days up to the same time of
    /// day; the bar is today over that whole average day. Null without a snapshot.</summary>
    private KpiCard? TodayEnergy(Reading read)
    {
        if (read.Snapshot is not { } snapshot) return null;
        var today = snapshot.Today;
        var todayDay = DateOnly.FromDateTime(read.LocalNow.DateTime);
        var average = DashboardMaths.AverageDayWh(read.Recent?.Days.Where(day => day.Day < todayDay).ToList() ?? []);
        var change = DashboardMaths.TodayTrend(today.EnergyKwh * 1000, average, read.LocalNow.TimeOfDay.TotalDays);
        return Energy(today, change, DashboardMaths.Fill(today.EnergyKwh * 1000, average ?? 0), "vs your average day");
    }

    /// <summary>This week, Monday to now as <see cref="Ranges.ThisWeek"/> cuts it, against last week from its Monday to the
    /// same day and time; the bar is this week so far over the whole of last week. Null without this week.</summary>
    private KpiCard? WeekEnergy(Reading read)
    {
        if (read.Energy?.SoFar is not { } week) return null;
        var kwh = week.Totals.EnergyKwh;
        var change = DashboardMaths.MonthTrend(kwh * 1000, read.Energy.SamePoint?.Totals.EnergyKwh * 1000);
        return Energy(week.Totals, change, DashboardMaths.Fill(kwh, read.Energy.Whole?.Totals.EnergyKwh ?? 0), "vs last week");
    }

    /// <summary>This month so far, from the snapshot the idle waste reads, against last month to the same day and time;
    /// the bar is this month so far over the whole of last month. Null without a snapshot.</summary>
    private KpiCard? MonthEnergy(Reading read)
    {
        if (read.Snapshot is not { } snapshot) return null;
        var kwh = snapshot.Month.EnergyKwh;
        var change = DashboardMaths.MonthTrend(kwh * 1000, read.LastMonth?.Totals.EnergyKwh * 1000);
        return Energy(snapshot.Month, change, DashboardMaths.Fill(kwh, read.Energy?.Whole?.Totals.EnergyKwh ?? 0), "vs last month");
    }

    /// <summary>Everything this PC has recorded, from the first row's day, with its cost; no bar or trend, but the day's
    /// average over the time since the first row, a day at the least so an hour's history is not a day's worth. Null
    /// without the range.</summary>
    private KpiCard? EnergySinceStart(Reading read)
    {
        if (read.Energy?.SoFar is not { } all) return null;
        string? line = null;
        if (read.FirstRow is { } first)
        {
            var days = Math.Max(1, (read.LocalNow - first).TotalDays);
            var perDay = Math.Max(0, all.Totals.EnergyKwh) / days;
            line = $"since {Ranges.LocalDay(first, _zone).ToString("d MMMM", _culture)}, {perDay.ToString("0.00", _culture)} kWh a day on average";
        }
        return Energy(all.Totals, null, 0, line) with { HasBar = false };
    }

    /// <summary>The energy card over <paramref name="totals"/>: its kWh, its cost or that there is no tariff, the change
    /// and the bar.</summary>
    private KpiCard Energy(RangeTotals totals, double? change, double fill, string? against) => new(
        EnergyLabel,
        Format.Kwh(totals.EnergyKwh, _culture) + " kWh",
        totals.Currency is { } currency ? Money.Format(totals.Cost, currency, _culture) : "no tariff set",
        Trend(change), change is { } c ? DashboardMaths.Kind(c) : TrendKind.Text,
        fill) { Against = against };

    /// <summary>Idle waste this month: the month's idle energy, on and off, priced at the month's average price as the
    /// Report and the Now page price it, against last month's; the bar is idle waste over the month's total.</summary>
    private KpiCard IdleWaste()
    {
        const string label = "Idle waste this month";
        if (_read is not { Snapshot: { } snapshot } read) return new KpiCard(label, Format.NoReading, _read is null ? "" : Unread, null, TrendKind.Text, 0);
        var month = snapshot.Month;
        var idle = month.IdleOnKwh + month.IdleOffKwh;
        var price = month.EnergyKwh > 0 ? month.Cost / (decimal)month.EnergyKwh : 0m;
        var last = read.LastMonth;
        var lastIdle = last is { Days.Count: > 0 } ? (last.Totals.IdleOnKwh + last.Totals.IdleOffKwh) * 1000 : (double?)null;
        var change = DashboardMaths.MonthTrend(idle * 1000, lastIdle);
        var (trend, kind) = IsFirstMonth(read) ? ("first month", TrendKind.Text)
            : (Trend(change), change is { } c ? DashboardMaths.Kind(c) : TrendKind.Text);
        return new KpiCard(
            label,
            Format.Kwh(idle, _culture) + " kWh",
            month.Currency is { } currency ? Money.Format(decimal.Round((decimal)idle * price, 2), currency, _culture) : "no tariff set",
            trend, kind,
            DashboardMaths.Fill(idle, month.EnergyKwh)) { LowerIsBetter = true };
    }

    /// <summary>Review 9: this month is the first when the history begins in it. An empty stretch of last month isn't
    /// one: the 1st's morning on a PC that was off overnight, or a PC installed late last month, has history from before
    /// this month. With no first row to go by, an empty last month says so, as before.</summary>
    private bool IsFirstMonth(Reading read)
    {
        if (read.FirstRow is not { } first) return read.LastMonth is { Days.Count: 0 };
        var local = read.LocalNow.DateTime;
        return first >= Ranges.Midnight(new DateOnly(local.Year, local.Month, 1), _zone);
    }

    /// <summary>The parts over the chosen range: watts now from the live budget, energy and share from the range, the
    /// live figure's quality as the chip, and the change against the same length of time before.</summary>
    private IReadOnlyList<DashboardPart> PartRows(LivePanel live, Reading? read)
    {
        var totals = read?.Parts?.Totals;
        var rows = totals is not null ? Bands.Rows(totals, _culture, withTotal: false) : [];
        var before = read?.Before?.Totals;
        var frame = _now.Last;
        return [.. PartGlyphs.Select(entry =>
        {
            var row = rows.FirstOrDefault(r => r.Part == entry.Part);
            var kwh = totals is null ? 0 : Kwh(totals, entry.Part);
            var was = before is null ? (double?)null : Kwh(before, entry.Part);
            var change = was is > 0 ? (kwh - was.Value) / was.Value : (double?)null;
            return new DashboardPart(
                entry.Part,
                row?.Name ?? Name(entry.Part),
                entry.Glyph,
                live.Budget.FirstOrDefault(b => b.Part == entry.Part)?.Watts ?? Format.Missing,
                totals is null ? Format.Missing : Energy(kwh),
                row?.Fraction ?? 0,
                frame is null ? null : QualityOf(frame, entry.Part),
                Trend(change),
                change is { } c ? DashboardMaths.Kind(c) : TrendKind.Text)
            {
                LowerIsBetter = true,
                Model = _models.GetValueOrDefault(entry.Part),
            };
        })];
    }

    /// <summary>How a part's live figure is got: the CPU's and the GPU's from their own sensors when the frame says so,
    /// the display always from the model, and the rest as the reading as a whole is.</summary>
    private static Quality QualityOf(ReadingFrame frame, Part part) => part switch
    {
        Part.Cpu => frame.CpuMeasured ? Quality.Measured : Quality.Estimated,
        Part.Gpu => frame.GpuMeasured ? Quality.Measured : Quality.Estimated,
        Part.Display => Quality.Estimated,
        _ => frame.Quality,
    };

    private static double Kwh(RangeTotals totals, Part part) => Math.Max(0, part switch
    {
        Part.Cpu => totals.CpuKwh,
        Part.Gpu => totals.GpuKwh,
        Part.Display => totals.DisplayKwh,
        _ => totals.RestKwh,
    });

    private static string Name(Part part) => part switch
    {
        Part.Cpu => "CPU package",
        Part.Gpu => "GPU",
        Part.Display => "Display",
        _ => "Rest of system",
    };

    private const string Unread = "History can't be read right now";

    /// <summary>"284 Wh" under a kilowatt-hour, "2.74 kWh" from there.</summary>
    private string Energy(double kwh) => kwh < 1 ? Format.WholeWatts(kwh * 1000, _culture) + " Wh" : Format.Kwh(kwh, _culture) + " kWh";

    /// <summary>A change as a whole percentage without its sign, which the arrow carries: "12%".</summary>
    private string? Trend(double? change)
        => change is { } c && double.IsFinite(c) ? Math.Round(Math.Abs(c) * 100).ToString("0", _culture) + "%" : null;
}
