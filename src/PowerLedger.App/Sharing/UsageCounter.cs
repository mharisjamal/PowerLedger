using System.Globalization;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// The App's usage counts (data-sharing design §3): opens, pages, settings changed by name, reports exported and updates
/// installed, flushed every 15 minutes, at local midnight and on exit while Usage is on. Counting itself is free; a flush
/// sends what is held for the day it was counted on and clears only what it sent, so a count made while a flush is on its
/// way to the service is never lost. While Usage is off, nothing is sent and whatever was held is dropped.
/// </summary>
internal sealed class UsageCounter : IDisposable
{
    public static readonly TimeSpan FlushEvery = TimeSpan.FromMinutes(15);

    private readonly IServiceLink _link;
    private readonly IUiSettings _ui;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _uiCulture;
    private readonly Lock _gate = new();
    private ITimer? _periodic;
    private ITimer? _midnight;

    private string _day;
    private int _appOpens;
    private readonly Dictionary<string, int> _pages = [];
    private readonly Dictionary<string, int> _settings = [];
    private int _reportsExported;
    private int _updatesInstalled;

    public UsageCounter(IServiceLink link, IUiSettings ui, UiThreads threads, TimeProvider clock, TimeZoneInfo zone, CultureInfo uiCulture)
    {
        _link = link;
        _ui = ui;
        _threads = threads;
        _clock = clock;
        _zone = zone;
        _uiCulture = uiCulture;
        _day = Today();
    }

    /// <summary>Starts the 15-minute and local-midnight timers. Call once, on the UI thread.</summary>
    public void Start()
    {
        _periodic ??= _clock.CreateTimer(_ => _ = FlushAsync(), null, FlushEvery, FlushEvery);
        ScheduleMidnight();
    }

    public void CountAppOpen() => Change(() => _appOpens++);

    public void CountPage(string name) => Change(() => Bump(_pages, name));

    public void CountSetting(string name) => Change(() => Bump(_settings, name));

    public void CountReportExported() => Change(() => _reportsExported++);

    public void CountUpdateInstalled() => Change(() => _updatesInstalled++);

    /// <summary>Sends what is held, whether or not a flush is due; for the App to call once as it exits.</summary>
    public Task FlushOnExitAsync() => FlushAsync();

    public void Dispose()
    {
        _periodic?.Dispose();
        _midnight?.Dispose();
    }

    /// <summary>Reads the service's status, then sends, drops or keeps what is held, depending what it says. Safe to call
    /// from anywhere, including while a Count* call runs on another thread.</summary>
    internal async Task FlushAsync()
    {
        UsageCounts snapshot;
        lock (_gate) snapshot = Snapshot();

        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        if (status?.Sharing is not { } sharing) return;   // the service is unreachable, or older and sends no Sharing status: try again later
        if (!sharing.Consent.Usage)
        {
            lock (_gate) Reset();   // Usage is off: nothing is sent, and nothing already held is kept either
            return;
        }

        var result = await _link.ReportUsageAsync(snapshot).ConfigureAwait(false);
        if (result.Succeeded) lock (_gate) Subtract(snapshot);
    }

    private static void Bump(Dictionary<string, int> counts, string name) => counts[name] = counts.GetValueOrDefault(name) + 1;

    private void Change(Action mutate)
    {
        lock (_gate) mutate();
    }

    private UsageCounts Snapshot() => new(
        _day, _appOpens, new Dictionary<string, int>(_pages), new Dictionary<string, int>(_settings), _reportsExported, _updatesInstalled,
        DaysSinceFirstRun(), ThemeName(), Language());

    /// <summary>Takes only what <paramref name="sent"/> held out of the running counts, so a count made while that send
    /// was on its way stays for the next one. Starts a fresh day once the day just sent has nothing left held against it.</summary>
    private void Subtract(UsageCounts sent)
    {
        _appOpens = Math.Max(0, _appOpens - sent.AppOpens);
        _reportsExported = Math.Max(0, _reportsExported - sent.ReportsExported);
        _updatesInstalled = Math.Max(0, _updatesInstalled - sent.UpdatesInstalled);
        Subtract(_pages, sent.Pages);
        Subtract(_settings, sent.Settings);
        if (sent.Day == _day) _day = Today();
    }

    private static void Subtract(Dictionary<string, int> counts, IReadOnlyDictionary<string, int> sent)
    {
        foreach (var (name, amount) in sent)
        {
            if (!counts.TryGetValue(name, out var have)) continue;
            var left = have - amount;
            if (left <= 0) counts.Remove(name); else counts[name] = left;
        }
    }

    private void Reset()
    {
        _day = Today();
        _appOpens = 0;
        _pages.Clear();
        _settings.Clear();
        _reportsExported = 0;
        _updatesInstalled = 0;
    }

    private string Today() => TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private int DaysSinceFirstRun()
    {
        var start = _ui.Current.FirstRunAt ?? _clock.GetUtcNow();
        var days = (TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone).Date - TimeZoneInfo.ConvertTime(start, _zone).Date).Days;
        return Math.Max(0, days);
    }

    private string ThemeName() => _ui.Current.Theme switch { ThemeChoice.Dark => "dark", ThemeChoice.Light => "light", _ => "system" };

    private string Language() => _uiCulture.Name is { Length: > 0 } name ? name : "en";

    /// <summary>(Re)schedules a one-shot timer for the next local midnight; reschedules itself again once it fires.</summary>
    private void ScheduleMidnight()
    {
        var now = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _zone);
        var midnight = new DateTimeOffset(now.Date, now.Offset).AddDays(1);
        _midnight?.Dispose();
        _midnight = _clock.CreateTimer(
            _ =>
            {
                _ = FlushAsync();
                _threads.Post(ScheduleMidnight);
            },
            null, midnight - now, Timeout.InfiniteTimeSpan);
    }
}
