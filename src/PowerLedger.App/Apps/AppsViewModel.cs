using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Storage;

namespace PowerLedger.App;

/// <summary>The Apps page's periods (Plan AE): today, this week from Monday and this month from the 1st, each so far.</summary>
internal enum AppsPeriod
{
    Today,
    Week,
    Month,
}

/// <summary>
/// Aero's Apps page (Plan AE): energy and cost by app, a ledger. It reads the period off the UI thread when the page
/// shows, when the period changes and every minute while it shows; a read a newer one overtook is dropped. A line opens
/// to its details and stays open through later reads; opening reads nothing. Nothing here knows a view, and nothing it
/// reads leaves the PC.
/// </summary>
internal sealed class AppsViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    private const string Unreadable = "Apps can't be read right now. They come back when the service is running.";

    private readonly IAppHistory _history;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private ITimer? _timer;
    private int _reads;
    private AppsPeriod _period;
    private Reading? _read;
    private string? _open;
    private IReadOnlyList<AppRow> _rows = [];
    private string? _message;
    private string _summary = "";
    private string? _note;
    private bool _hasCost;

    /// <summary>One read: the period and its range as they were read, and what came back (null when it couldn't be read).</summary>
    private sealed record Reading(AppsPeriod Period, DateRange Range, AppsRange? Apps);

    public AppsViewModel(IAppHistory history, UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo culture)
    {
        _history = history;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _culture = culture;
        Toggle = new RelayCommand<string>(key =>
        {
            _open = key is null || key == _open ? null : key;
            Rebuild();
        });
    }

    /// <summary>The period shown; a change reads it at once.</summary>
    public AppsPeriod Period
    {
        get => _period;
        set
        {
            if (SetProperty(ref _period, value)) Refresh();
        }
    }

    /// <summary>The ledger's lines (<see cref="AppLedger.Rows"/>), empty while there is nothing to show.</summary>
    public IReadOnlyList<AppRow> Rows
    {
        get => _rows;
        private set
        {
            var had = HasRows;
            if (SetProperty(ref _rows, value) && had != HasRows) OnPropertyChanged(nameof(HasRows));
        }
    }

    /// <summary>Whether there are lines, so the column heads show.</summary>
    public bool HasRows => _rows.Count > 0;

    /// <summary>Why there are no lines: the history can't be read, or apps haven't been watched long enough yet; null
    /// while there are lines, and before the first read.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>The period's total and its cost: "1.66 kWh, Rs58.17 so far today"; empty without lines.</summary>
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    /// <summary>What the figures leave out, when they do: apps counted only from part way through the period, or costs
    /// in an earlier currency left out.</summary>
    public string? Note { get => _note; private set => SetProperty(ref _note, value); }

    /// <summary>Whether a tariff prices the lines, so the cost column has anything to show.</summary>
    public bool HasCost { get => _hasCost; private set => SetProperty(ref _hasCost, value); }

    /// <summary>Opens the line with the key given, closing any other; given the open line's key, closes it.</summary>
    public IRelayCommand<string> Toggle { get; }

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

    public void Dispose() => Hide();

    /// <summary>Reads the period off the UI thread; a read a newer one overtook is dropped, and one that throws reads as
    /// one that couldn't be read. Call on the UI thread.</summary>
    internal void Refresh()
    {
        var read = ++_reads;
        var period = Period;
        _threads.Background(() =>
        {
            var range = RangeFor(period, _clock.GetUtcNow());
            AppsRange? apps;
            try
            {
                apps = _history.Apps(range);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                apps = null;
            }
            _threads.Post(() =>
            {
                if (read != _reads) return;
                _read = new Reading(period, range, apps);
                Rebuild();
            });
        });
    }

    private DateRange RangeFor(AppsPeriod period, DateTimeOffset now) => period switch
    {
        AppsPeriod.Week => Ranges.ThisWeek(now, _zone),
        AppsPeriod.Month => Ranges.ThisMonth(now, _zone, _culture),
        _ => Ranges.Today(now, _zone, _culture),
    };

    /// <summary>The lines and the words over them from the last read, with the open line as it is now. On the UI thread.</summary>
    private void Rebuild()
    {
        if (_read is not { } read) return;
        var apps = read.Apps;
        Rows = apps is null ? [] : AppLedger.Rows(apps, _culture, _open);
        Message = apps is null ? Unreadable : Rows.Count == 0 ? AppLedger.Waiting(apps, read.Range, _zone, _culture) : null;
        HasCost = apps?.Currency is not null;
        Summary = Rows.Count == 0 || apps is null ? "" : SummaryOf(Rows[^1], apps.Currency, read.Period);
        Note = apps is null || Rows.Count == 0 ? null : AppLedger.Note(apps, read.Range, _clock.GetUtcNow(), _zone, _culture);
    }

    private static string SummaryOf(AppRow total, string? currency, AppsPeriod period)
    {
        var when = period switch
        {
            AppsPeriod.Week => "this week",
            AppsPeriod.Month => "this month",
            _ => "today",
        };
        return currency is null ? $"{total.Energy} kWh so far {when}" : $"{total.Energy} kWh, {total.Cost} so far {when}";
    }
}
