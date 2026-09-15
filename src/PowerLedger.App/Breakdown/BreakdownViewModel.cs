using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>
/// The Breakdown screen (spec §9): power by component over a range, as a stacked chart in watts or watt-hours and as each
/// band's energy and share. It reads when shown, when the range changes and every minute while shown, off the UI thread;
/// switching the unit redraws from the last read.
/// </summary>
internal sealed class BreakdownViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    private readonly IRangeHistory _history;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private ITimer? _timer;
    private int _reads;
    private DateRange? _range;
    private RangeReport? _report;
    private ChartUnit _unit = ChartUnit.Watts;
    private ChartModel _chart = ChartModel.Empty;
    private string _heading = "";
    private string _unitLabel = "Watts";
    private IReadOnlyList<PartRow> _parts = [];
    private bool _hasNegativeRest;
    private string? _message;

    public BreakdownViewModel(IRangeHistory history, UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture)
    {
        _history = history;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        Range = new RangePicker(RangeChoice.Today, Ranges.LocalDay(clock.GetUtcNow(), zone));
        Range.Changed += Refresh;
    }

    public RangePicker Range { get; }

    public ChartUnit Unit
    {
        get => _unit;
        set
        {
            if (SetProperty(ref _unit, value)) Rebuild();
        }
    }

    public ChartModel Chart { get => _chart; private set => SetProperty(ref _chart, value); }

    /// <summary>"Last 7 days · hourly · stacked by component".</summary>
    public string Heading { get => _heading; private set => SetProperty(ref _heading, value); }

    /// <summary>"Watts", or "Wh per hour".</summary>
    public string UnitLabel { get => _unitLabel; private set => SetProperty(ref _unitLabel, value); }

    public IReadOnlyList<PartRow> Parts { get => _parts; private set => SetProperty(ref _parts, value); }

    /// <summary>Measured mode put the rest band below zero somewhere in the range, which the footnote explains (spec §9).</summary>
    public bool HasNegativeRest { get => _hasNegativeRest; private set => SetProperty(ref _hasNegativeRest, value); }

    /// <summary>Why there is nothing to show, or null.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => Message is not null;

    /// <summary>The page is shown: read now, and every minute until it is hidden. Call on the UI thread.</summary>
    public void Show()
    {
        Refresh();
        _timer ??= _clock.CreateTimer(_ => _threads.Post(Refresh), null, RefreshEvery, RefreshEvery);
    }

    public void Hide()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Reads the chosen range off the UI thread; a read a newer one overtook is dropped. Call on the UI thread.</summary>
    internal void Refresh()
    {
        var read = ++_reads;
        var range = Range.Resolve(_clock.GetUtcNow(), _zone, _culture);
        _threads.Background(() =>
        {
            var report = _history.Read(range, _zone);
            _threads.Post(() =>
            {
                if (read != _reads) return;
                _range = range;
                _report = report;
                Rebuild();
            });
        });
    }

    public void Dispose()
    {
        Range.Changed -= Refresh;
        Hide();
    }

    private void Rebuild()
    {
        if (_range is not { } range) return;
        Heading = $"{range.Title} · {Ranges.BucketName(range.Bucket)} · stacked by component";
        UnitLabel = Unit == ChartUnit.Watts ? "Watts" : "Wh per " + Ranges.BucketLength(range.Bucket);
        if (_report is not { } report)
        {
            Chart = Charts.Build(range, [], Unit, _zone, _culture);
            Parts = [];
            HasNegativeRest = false;
            Message = "History can't be read right now. It comes back when the service is running.";
            return;
        }
        Chart = Charts.Build(range, report.Series, Unit, _zone, _culture);
        Parts = Rows(report.Totals);
        HasNegativeRest = report.Totals.RestKwh < 0 || report.Series.Any(b => b.RestWh < 0);
        Message = report.Totals.OnHours > 0 || report.Totals.AsleepHours > 0 ? null : "No readings in this range.";
    }

    private IReadOnlyList<PartRow> Rows(RangeTotals t) => Bands.Rows(t, _culture, withTotal: true);
}
