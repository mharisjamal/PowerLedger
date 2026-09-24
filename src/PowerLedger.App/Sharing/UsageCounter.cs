using System.Globalization;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// The App's usage counts (data-sharing design §3): opens, pages, settings changed by name, reports exported and updates
/// installed, flushed every 15 minutes, at local midnight and on exit while Usage is on. Counting itself is free; a flush
/// sends what is held for the day it was counted on and clears only what it sent, so a count made while a flush is on its
/// way to the service is never lost. While Usage is off, nothing is sent and whatever was held is dropped; the same
/// happens the moment Usage is found to have turned on from a known off, since what is held by then can't be told apart
/// from what was counted before consent.
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
    private int _answers;     // the user's own answers seen, so a status read begun before one is known to be older than it
    private bool? _usageOn;   // the last consent this counter saw for Usage: null until Start, a flush or ConsentChanged first says

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

    /// <summary>Starts the 15-minute and local-midnight timers, and reads the current consent once so a flip to Usage on
    /// while nothing has counted this session yet is still recognised as a change (data-sharing design §3). Call once, on
    /// the UI thread.</summary>
    public void Start()
    {
        _periodic ??= _clock.CreateTimer(_ => _ = FlushAsync(), null, FlushEvery, FlushEvery);
        ScheduleMidnight();
        _link.ConnectionChanged += OnConnectionChanged;
        _ = SeedAsync();
    }

    /// <summary>Tells the counter the consent an App action just took: the consent dialog's or Settings → Privacy's own
    /// successful setConsent (data-sharing design §3), known at once rather than only at the next flush. Unlike a flush's
    /// or the seed's own first-ever observation, an unknown previous state here is never trusted: it can only mean the
    /// seed had not yet run when this consent changed (Start ran before the link connected), so whatever is held cannot
    /// be shown to be held only since consent, and is dropped exactly as if consent had been off. Safe to call from the
    /// UI thread.</summary>
    public void ConsentChanged(Consent consent)
    {
        var usageOn = consent.Answered && consent.Usage;
        lock (_gate)
        {
            if (!usageOn || _usageOn != true) Reset();
            _usageOn = usageOn;
            _answers++;
        }
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
        _link.ConnectionChanged -= OnConnectionChanged;
        _periodic?.Dispose();
        _midnight?.Dispose();
    }

    /// <summary>Seeds again once the link (re)connects (data-sharing design §3): Start's own seed can run before the pipe
    /// has finished connecting and find nothing, which must not leave the consent unknown for up to the next fifteen-
    /// minute flush.</summary>
    private void OnConnectionChanged(bool connected)
    {
        if (connected) _threads.Background(() => _ = SeedAsync());
    }

    /// <summary>Reads the service's status, then sends, drops or keeps what is held, depending what it says. Safe to call
    /// from anywhere, including while a Count* call runs on another thread.</summary>
    internal async Task FlushAsync()
    {
        UsageCounts snapshot;
        int answers;
        lock (_gate)
        {
            snapshot = Snapshot();
            answers = _answers;
        }

        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        if (status?.Sharing is not { } sharing) return;   // the service is unreachable, or older and sends no Sharing status: try again later

        // an answer to an older wording of the choices counts for nothing (data-sharing design §1), whatever Usage says
        var usageOn = sharing.Consent.Answered && sharing.Consent.Usage;
        bool sendable;
        lock (_gate)
        {
            if (_answers != answers) return;   // the user answered while it was read: what it says is older than their answer
            // on, and not a fresh change from a known off: what is held can't be from before consent (a first-ever
            // observation of on, with nothing known before it, is trusted, so an already-consented session sends normally)
            sendable = usageOn && _usageOn != false;
            Observe(usageOn);
        }
        if (!sendable) return;   // off, or only just found on from a known off: this snapshot might hold counts from before consent

        var result = await _link.ReportUsageAsync(snapshot).ConfigureAwait(false);
        if (result.Succeeded) lock (_gate) Subtract(snapshot);
    }

    /// <summary>Reads the current consent once, without waiting for the first flush (data-sharing design §3). Errors are
    /// swallowed the same way a flush's own status read would be: try again at the next flush.</summary>
    private async Task SeedAsync()
    {
        int answers;
        lock (_gate) answers = _answers;
        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        if (status?.Sharing is not { } sharing) return;
        lock (_gate)
        {
            if (_answers == answers) Observe(sharing.Consent.Answered && sharing.Consent.Usage);   // not older than the user's answer
        }
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

    /// <summary>Updates the last consent this counter saw for Usage. Off, or only just turned on from a known off, drops
    /// whatever is held: it cannot be told apart from what was counted before consent (data-sharing design §1). A first-
    /// ever observation of on is trusted as is, so an App session that starts already consented sends normally. Call
    /// under <see cref="_gate"/>.</summary>
    private void Observe(bool usageOn)
    {
        if (!usageOn || _usageOn == false) Reset();
        _usageOn = usageOn;
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
