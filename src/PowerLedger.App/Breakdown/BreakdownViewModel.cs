using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PowerLedger.Contracts;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>
/// The Breakdown screen (spec §9): power by component over a range, as a stacked chart in watts or watt-hours and as each
/// band's energy and share, with a footnote that says what the display band holds. It reads when shown, when the range
/// changes and every minute while shown, off the UI thread, the service's settings included, which say whether the machine
/// has a built-in panel; switching the unit redraws from the last read.
/// </summary>
internal sealed class BreakdownViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    private const string Units = " W is the average while the machine was on; Wh is the energy in each bucket.";

    private readonly IServiceLink _link;
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
    private string _footnote;

    public BreakdownViewModel(IServiceLink link, IRangeHistory history, UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture)
    {
        _link = link;
        _history = history;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        _footnote = FootnoteFor(null);
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

    /// <summary>What the display band holds, over any range and on this machine, and what the units mean.</summary>
    public string Footnote { get => _footnote; private set => SetProperty(ref _footnote, value); }

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
        _threads.Background(() => _ = ReadAsync(read, range));
    }

    public void Dispose()
    {
        Range.Changed -= Refresh;
        Hide();
    }

    /// <summary>What the display band holds over any range: the built-in panel, on a machine with one, and the external
    /// monitors counted at the time, which need not be those counted now. A laptop always has a panel, of a size it may not
    /// know; a desktop has one only when it gives the size, as an all-in-one does, which is how the model counts a panel.
    /// Without the service's settings the footnote doesn't guess whether there is one.</summary>
    private static string FootnoteFor(ServiceSettings? settings)
    {
        var display = settings?.Profile switch
        {
            null => "Display is the built-in panel, if there is one, and any external monitors counted at the time.",
            { Chassis: ChassisKind.Laptop } or { DisplayDiagonalInches: > 0 } => "Display is the built-in panel and any external monitors counted at the time.",
            _ => "Display is the external monitors counted at the time.",
        };
        return display + Units;
    }

    private async Task ReadAsync(int read, DateRange range)
    {
        var report = _history.Read(range, _zone);
        var settings = await _link.GetSettingsAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            if (read != _reads) return;
            _range = range;
            _report = report;
            Footnote = FootnoteFor(settings);
            Rebuild();
        });
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
