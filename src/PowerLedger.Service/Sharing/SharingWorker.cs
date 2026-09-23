using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service.Sharing;

/// <summary>Where sharing keeps its files, and what it asks of the machine, so tests can supply their own.</summary>
/// <param name="Sent">Copies of what was sent, the newest 30, and the preview. Users can read it.</param>
/// <param name="Crashes">The service's own crash files, waiting to be recorded or deleted.</param>
/// <param name="Names">The names scrubbed out of every text kept for sending.</param>
/// <param name="Host">What the PC says of itself.</param>
/// <param name="PickMinute">Chooses the send minute, once; null for chance.</param>
internal sealed record SharingEnvironment(string Sent, string Crashes, Func<ScrubNames> Names, Func<HostFacts> Host, Func<int>? PickMinute = null)
{
    public static SharingEnvironment For(ServicePaths paths) => new(paths.Sent, paths.Crashes, ScrubNames.Here, HostFacts.Here);
}

/// <summary>
/// The collector and uploader (data-sharing design §4). Every five minutes, and at start, it builds the minutes completed
/// since the last run into the outbox while Hardware and power is on, records the sources' failures and the service's
/// crashes while Crash and sensor reports is on, drops days too old to send, posts a consent change the server hasn't
/// heard, and sends each complete day once the schedule says so. Between runs it carries out the App's requests, one at a
/// time, so nothing else touches the outbox or the sharing state and neither needs a lock. A request the App waits on
/// doesn't wait for a run: the run gives way to it where nothing is half-done, cancelling a request to the server under
/// way, and starts again once the App has its answer. So the App is answered within <see cref="AppWait"/> of asking, and a
/// switch turned off is off before the next day is built. Nothing is kept or sent before the user answers, or while every
/// switch is off.
/// </summary>
internal sealed class SharingWorker : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    /// <summary>How long after the pipe queued it a request the App waits on is answered, whatever came before it, so the
    /// pipe answers inside its own time limit.</summary>
    public static readonly TimeSpan AppWait = TimeSpan.FromSeconds(8);

    public const int MaxDaysPerRun = 7;
    public const int KeepDays = 14;
    public const int KeepSent = 30;
    public const int MaxCrashesPerDay = 20;
    internal const string PreviewFile = "preview.json";

    private const long HourMs = 3_600_000;
    private const string NotInTime = "the server didn't answer in time";
    private const string NoTimeLeft = "the service was busy with another request";
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>How far behind now the minutes are built: readings reach the database at each minute boundary.</summary>
    private static readonly TimeSpan CollectionLag = TimeSpan.FromMinutes(2);

    private static readonly Consent AllOff = new(ConsentText.Version, false, false, false, false);
    private static readonly Consent AllOn = new(ConsentText.Version, true, true, true, true);

    private readonly StatusBoard _board;
    private readonly SharingCommands _commands;
    private readonly ISharingClient _client;
    private readonly SharingEnvironment _environment;
    private readonly TimeProvider _clock;
    private readonly ILogger<SharingWorker> _log;
    private readonly SharingStore _store;
    private readonly OutboxRepository _outbox;
    private readonly RawSampleRepository _raw;
    private readonly TariffRepository _tariffs;

    /// <summary>Each source's failure count at the last look, so only new failures are added to the day.</summary>
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);

    /// <summary>True when the last run, or a consent change's post, gave way to the App: it starts again once the inbox is empty.</summary>
    private bool _resume;

    public SharingWorker(
        SqliteDatabase database, StatusBoard board, SharingCommands commands, ISharingClient client, SharingEnvironment environment,
        TimeProvider clock, ILogger<SharingWorker> log)
    {
        _board = board;
        _commands = commands;
        _client = client;
        _environment = environment;
        _clock = clock;
        _log = log;
        _store = new SharingStore(new SettingsRepository(database), environment.PickMinute);
        _outbox = new OutboxRepository(database);
        _raw = new RawSampleRepository(database);
        _tariffs = new TariffRepository(database);
    }

    private TimeZoneInfo Zone => _clock.LocalTimeZone;

    /// <summary>True once the sampling loop has published what a report is built from.</summary>
    private bool LoopHasPublished => _board.Settings is not null && _board.Status is not null;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            await TickSafelyAsync(stop).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Every, _clock);
            Task<bool>? tick = null;
            Task<bool>? inbox = null;
            while (!stop.IsCancellationRequested)
            {
                tick ??= timer.WaitForNextTickAsync(stop).AsTask();
                inbox ??= _commands.Reader.WaitToReadAsync(stop).AsTask();
                if (!_resume) await Task.WhenAny(tick, inbox).ConfigureAwait(false);   // a run that gave way goes on at once

                if (inbox.IsCompleted)
                {
                    if (!await inbox.ConfigureAwait(false)) break;
                    inbox = null;
                    while (!stop.IsCancellationRequested && _commands.TryTake(out var command))
                    {
                        await HandleSafelyAsync(command, stop).ConfigureAwait(false);
                    }
                    continue;                                                          // what came in meanwhile goes first
                }

                if (tick.IsCompleted)
                {
                    if (!await tick.ConfigureAwait(false)) break;
                    tick = null;
                }
                await TickSafelyAsync(stop).ConfigureAwait(false);                     // the tick's, or the one that gave way
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            _commands.Close();
        }
    }

    /// <summary>The five-minute run (data-sharing design §4), with how sharing stands published for the status before it
    /// starts, as its requests can take minutes, and again once it is done. A run that gives way to the App starts again
    /// once the App has its answer.</summary>
    internal async Task TickAsync(CancellationToken stop)
    {
        _resume = false;
        Publish();
        try
        {
            await RunAsync(stop).ConfigureAwait(false);
        }
        catch (GiveWay)
        {
            _resume = true;
        }
        finally
        {
            Publish();
        }
    }

    /// <summary>The run's steps. Each is whole before a later one can give way to the App, so a run that starts again loses
    /// nothing: the failures counted and the crashes read are recorded first.</summary>
    private async Task RunAsync(CancellationToken stop)
    {
        var now = _clock.GetUtcNow();
        var stored = _store.StoredConsent;
        var consent = stored?.Consent ?? Consent.Unanswered;
        var failures = FailuresSinceLastLook();
        if (stored is null || !consent.AllowsAny)
        {
            _outbox.Clear();
            DeleteCrashFiles();
            await PostPendingConsentAsync(now, atOnce: false, stop).ConfigureAwait(false);
            return;
        }

        if (consent.Diagnostics)
        {
            RecordFailures(now, failures);
            IngestCrashFiles(now, stored);
        }
        else
        {
            DeleteCrashFiles();
        }
        if (consent.Power) Collect(now, stored, giveWay: true);
        DropOldDays(now);
        if (!await PostPendingConsentAsync(now, atOnce: false, stop).ConfigureAwait(false)) return;

        if (LoopHasPublished
            && SendSchedule.Due(now, Zone, _store.SendMinute, _store.LastRun, _store.Backoff, CompleteDays(now, consent).Count > 0))
        {
            await SendAsync(now, answerBy: default, stop).ConfigureAwait(false);
        }
    }

    /// <summary>Carries out one of the App's requests and answers it, unless the pipe has given up on it: the App was then told
    /// the service didn't answer in time, and it is left undone.</summary>
    internal async Task HandleAsync(SharingCommand command, CancellationToken stop)
    {
        if (!command.TryTake()) return;
        try
        {
            switch (command)
            {
                case SetConsentCommand consent:
                    await SetConsentAsync(consent, stop).ConfigureAwait(false);
                    break;
                case ReportUsageCommand usage:
                    RecordUsage(usage);
                    break;
                case ReportCrashCommand crash:
                    RecordCrash(crash);
                    break;
                case PreviewCommand preview:
                    Preview(preview);
                    break;
                case SendNowCommand send:
                    await SendNowAsync(send, stop).ConfigureAwait(false);
                    break;
                case DeleteMyDataCommand delete:
                    await DeleteAsync(delete, stop).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            command.Answer(false, SharingCommands.Stopping);
            throw;
        }
        catch (Exception error)
        {
            _log.LogError(error, "{Command} failed", command.GetType().Name);
            command.Answer(false, $"That didn't work: {error.Message}");
        }
        finally
        {
            Publish();
        }
    }

    /// <summary>How sharing stands, for Settings → Privacy: the consent, the ID, the last upload, the last problem and the
    /// complete days waiting. It never throws: the status keeps what was last published, and a request or a run goes on.</summary>
    private void Publish()
    {
        try
        {
            var now = _clock.GetUtcNow();
            var consent = _store.Consent;
            var last = _store.LastSent;
            var problem = _store.Problem;
            _board.Publish(new SharingStatus(
                consent, _store.InstallId, last is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(last.AtMs), last?.Bytes,
                problem?.Text, problem?.Rejected ?? false, consent.AllowsAny ? CompleteDays(now, consent).Count : 0));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.LogWarning(error, "How sharing stands could not be read for the status");
        }
    }

    private async Task TickSafelyAsync(CancellationToken stop)
    {
        try
        {
            await TickAsync(stop).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            _log.LogError(error, "Data sharing's five-minute run failed");
        }
    }

    /// <summary>A request whatever happens, as the five-minute run is: anything thrown out of the worker would stop the whole
    /// service.</summary>
    private async Task HandleSafelyAsync(SharingCommand command, CancellationToken stop)
    {
        try
        {
            await HandleAsync(command, stop).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            _log.LogError(error, "{Command} failed", command.GetType().Name);
            command.Answer(false, $"That didn't work: {error.Message}");
        }
    }

    private async Task SetConsentAsync(SetConsentCommand command, CancellationToken stop)
    {
        var consent = command.Consent;
        if (consent is null)
        {
            command.Answer(false, "The choices are missing.");
            return;
        }
        if (consent.Validate() is { } problem)
        {
            command.Answer(false, problem);
            return;
        }

        var now = _clock.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var before = _store.StoredConsent;
        var was = before?.Consent is { Answered: true } answered ? answered : AllOff;
        if (consent.AllowsAny) _store.Identity();                           // the first switch turned on makes the ID and key
        if (consent.Power && !was.Power) _store.CollectedTo = nowMs;        // nothing from before the answer is collected
        if (!consent.Power)
        {
            _outbox.DeleteMinutes();
            _store.CollectedTo = null;
        }
        if (!consent.Diagnostics)
        {
            _outbox.DeleteEvents(OutboxEvents.Crash);
            _outbox.DeleteEvents(OutboxEvents.Sources);
            DeleteCrashFiles();
        }
        else if (!was.Diagnostics)
        {
            FailuresSinceLastLook();                                           // failures from before the answer aren't counted
        }
        if (!consent.Usage) _outbox.DeleteEvents(OutboxEvents.Usage);

        var diagnosticsSince = consent.Diagnostics ? (was.Diagnostics ? before?.DiagnosticsSinceMs ?? nowMs : nowMs) : (long?)null;
        _store.SaveConsent(new StoredConsent(consent, nowMs, diagnosticsSince));
        if (_store.InstallId is not null) _store.ConsentPending = true;
        _log.LogInformation(
            "Data sharing set: diagnostics {Diagnostics}, usage {Usage}, power {Power}, share {Share}",
            consent.Diagnostics, consent.Usage, consent.Power, consent.Share);
        Publish();                                                            // the status shows it before the App hears back
        command.Answer(true, "Your choices are saved.");

        try
        {
            await PostPendingConsentAsync(now, atOnce: true, stop).ConfigureAwait(false);   // a failure is tried again later
        }
        catch (GiveWay)
        {
            _resume = true;                                                   // posted after the App's next request
        }
    }

    private void RecordUsage(ReportUsageCommand command)
    {
        var consent = _store.Consent;
        if (!(consent.AllowsAny && consent.Usage))
        {
            command.Answer(true, "Usage is off, so nothing was kept.");
            return;
        }
        if (command.Counts is not { } counts || counts.Validate() is not null)
        {
            command.Answer(false, command.Counts?.Validate() ?? "The counts are missing.");
            return;
        }
        var day = FileDay(counts.Day, _clock.GetUtcNow());
        _outbox.MergeEvent(day, OutboxEvents.Usage, json => OutboxEvents.MergeUsage(json, counts with { Day = day }));
        command.Answer(true, "Counted.");
    }

    private void RecordCrash(ReportCrashCommand command)
    {
        var stored = _store.StoredConsent;
        var kept = stored is { Consent: { AllowsAny: true, Diagnostics: true } } && command.Crash is { } crash
            && Record(crash, _clock.GetUtcNow(), stored);
        command.Answer(true, kept ? "Recorded." : "Not kept.");
    }

    /// <summary>Writes what an upload would carry now, every section as if every switch were on, from the last hour of
    /// readings and the latest status, to <c>Sent\preview.json</c>, indented to be read.</summary>
    private void Preview(PreviewCommand command)
    {
        if (!LoopHasPublished)
        {
            command.Answer(false, "The service is still starting. Try again in a moment.");
            return;
        }
        var now = _clock.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var minutes = MinuteBuilder.Build(_raw.ReadRange(nowMs - HourMs, nowMs), Zone, GapThreshold);
        var day = minutes.Count > 0 ? minutes[^1].Day : Today(now);
        var events = OutboxEvents.Read(_outbox, day);
        var usage = events.Usage ?? new UsageCounts(day, 0, new Dictionary<string, int>(), new Dictionary<string, int>(), 0, 0, 0, "system", "en");
        var inputs = Inputs(
            day, AllOn, _store.InstallId ?? Guid.Empty.ToString("D"), [.. minutes.Where(minute => minute.Day == day)], events with { Usage = usage },
            withHardware: true, now);

        var json = ReportJson.Indented(ReportJson.Write(ReportBuilder.Build(inputs)));
        Directory.CreateDirectory(_environment.Sent);
        var path = Path.Combine(_environment.Sent, PreviewFile);
        File.WriteAllBytes(path, json);
        command.Answer(true, "What the next upload would carry is in the file.", path);
    }

    private async Task SendNowAsync(SendNowCommand command, CancellationToken stop)
    {
        var stored = _store.StoredConsent;
        if (stored is null || !stored.Consent.AllowsAny)
        {
            // An answer to an older wording counts for nothing until the user answers again.
            command.Answer(false, stored?.Consent.Answered == true ? "Nothing is sent while every switch is off." : "Nothing is sent until you choose what to share.");
            return;
        }
        if (!LoopHasPublished)
        {
            command.Answer(false, "The service is still starting. Try again in a moment.");
            return;
        }
        var now = _clock.GetUtcNow();
        if (stored.Consent.Power) Collect(now, stored, giveWay: false);
        if (CompleteDays(now, stored.Consent).Count == 0)
        {
            command.Answer(true, "Nothing is waiting to be sent.");
            return;
        }
        using var answerBy = AnswerBy(command);
        var (ok, message) = (await SendAsync(now, answerBy.Token, stop).ConfigureAwait(false)).Reply;
        command.Answer(ok, message);
    }

    private async Task DeleteAsync(DeleteMyDataCommand command, CancellationToken stop)
    {
        var now = _clock.GetUtcNow();
        if (_store.InstallId is not { } id || _store.Key is not { } key)
        {
            Forget(now);
            command.Answer(true, "Nothing had been sent from this PC, and every switch is now off.");
            return;
        }
        using var answerBy = AnswerBy(command);
        var outcome = await CallAsync(cancel => _client.DeleteAsync(id, key, cancel), stop, answerBy.Token).ConfigureAwait(false);
        if (outcome is SendOutcome.Accepted or SendOutcome.Gone)
        {
            _log.LogInformation("The server deleted this install's data; forgetting it");
            Forget(now);
            command.Answer(true, "Your data has been deleted from the server.");
        }
        else
        {
            command.Answer(false, $"Couldn't delete your data: {outcome.Reason}. Nothing was changed.");
        }
    }

    /// <summary>
    /// Builds the minutes from the readings since the last run, or since the user said yes, up to two minutes ago, an hour
    /// at a time. A run that finds the readings gone, because the service was off longer than they are kept, builds nothing.
    /// Nothing is built while the loop's writes are failing: it holds the readings it couldn't write and writes them later,
    /// and minutes built past them now would leave them out for good.
    /// </summary>
    /// <param name="giveWay">For the five-minute run: it gives way to a request the App waits on between hours.</param>
    private void Collect(DateTimeOffset now, StoredConsent stored, bool giveWay)
    {
        if (_board.Status?.WriteProblem is not null) return;
        var cutoff = (Rollups.Floor(now, Minute) - CollectionLag).ToUnixTimeMilliseconds();
        var from = Math.Max(_store.CollectedTo ?? stored.AtMs, (now - TimeSpan.FromDays(KeepDays + 1)).ToUnixTimeMilliseconds());
        while (from < cutoff)
        {
            if (giveWay && _commands.AppWaiting) throw new GiveWay();
            var to = Math.Min(cutoff, ((from / HourMs) + 1) * HourMs);
            var minutes = MinuteBuilder.Build(_raw.ReadRange(from, to), Zone, GapThreshold);
            if (minutes.Count > 0) _outbox.InsertMinutes(minutes);
            _store.CollectedTo = to;
            from = to;
        }
    }

    private double GapThreshold => EnergyIntegrator.GapThresholdFor((_board.Settings ?? ServiceSettings.Default).SampleIntervalSeconds);

    /// <summary>Each source's failures since the last look. A count lower than last time means the sensor set was rebuilt,
    /// and it counts from zero again.</summary>
    private Dictionary<string, SourceDay> FailuresSinceLastLook()
    {
        var names = _environment.Names();
        var added = new Dictionary<string, SourceDay>(StringComparer.Ordinal);
        foreach (var source in _board.Status?.Sources ?? [])
        {
            if (source?.Name is not { } name) continue;
            var last = _failures.GetValueOrDefault(name);
            var failed = source.Failures >= last ? source.Failures - last : source.Failures;
            _failures[name] = source.Failures;
            var error = failed > 0 ? Scrubber.Scrub(source.LastError, names) : "";
            added[name] = new SourceDay(failed, error.Length > 0 ? error : null);
        }
        return added;
    }

    /// <summary>Adds the failures to today's sources, so a day the PC was on says how every source did.</summary>
    private void RecordFailures(DateTimeOffset now, Dictionary<string, SourceDay> failures)
    {
        if (failures.Count == 0) return;
        _outbox.MergeEvent(Today(now), OutboxEvents.Sources, json => OutboxEvents.MergeSources(json, failures));
    }

    /// <summary>Records the service's crash files, scrubbed, and deletes them. One still being written waits for the next run.</summary>
    private void IngestCrashFiles(DateTimeOffset now, StoredConsent stored)
    {
        foreach (var file in CrashFiles())
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (SharingJson.Read(text, SharingJson.Default.CrashReport) is { } crash) Record(crash, now, stored);
            TryDelete(file);
        }
    }

    /// <summary>Keeps a crash under today, scrubbed and cut to size: only one from after the user turned diagnostics on, and
    /// at most <see cref="MaxCrashesPerDay"/> a day.</summary>
    private bool Record(CrashReport crash, DateTimeOffset now, StoredConsent stored)
    {
        if (crash.At.ToUnixTimeMilliseconds() < (stored.DiagnosticsSinceMs ?? stored.AtMs)) return false;
        var names = _environment.Names();
        var scrubbed = (crash with
        {
            Types = [.. (crash.Types ?? []).Select(type => Scrubber.Scrub(type, names))],
            Message = Scrubber.Scrub(crash.Message, names),
            Stack = Scrubber.Scrub(crash.Stack, names),
        }).Trimmed();
        if (scrubbed.Validate() is not null) return false;
        var day = Today(now);
        if (_outbox.CountEvents(day, OutboxEvents.Crash) >= MaxCrashesPerDay) return false;
        _outbox.AddEvent(day, OutboxEvents.Crash, OutboxEvents.Write(scrubbed));
        return true;
    }

    private void DropOldDays(DateTimeOffset now)
    {
        var oldest = LocalDays.Of(now, Zone).AddDays(-KeepDays);
        foreach (var day in _outbox.Days())
        {
            if (!TryParse(day, out var date) || date < oldest) _outbox.DeleteDay(day);
        }
    }

    /// <summary>Posts a consent change the server hasn't heard: at once when the user has just made it, otherwise once a
    /// back-off of its own has passed, since the server counts every request against the install's daily limit. The App
    /// has had its answer by then, so the post gives way to its next request (<see cref="GiveWay"/>).</summary>
    /// <returns>False when the server has deleted this install, which is then forgotten.</returns>
    private async Task<bool> PostPendingConsentAsync(DateTimeOffset now, bool atOnce, CancellationToken stop)
    {
        if (!_store.ConsentPending) return true;
        if (_store.InstallId is not { } id || _store.Key is not { } key)
        {
            Posted();
            return true;
        }
        if (!atOnce && _store.ConsentBackoff is { } backoff && now.ToUnixTimeMilliseconds() < backoff.NextMs) return true;

        var consent = _store.Consent;
        var outcome = await OwnCallAsync(cancel => _client.SendConsentAsync(id, key, consent, cancel), stop).ConfigureAwait(false);
        switch (outcome)
        {
            case SendOutcome.Accepted:
                Posted();
                return true;
            case SendOutcome.Gone:
                _log.LogInformation("The server has deleted this install; forgetting it");
                Forget(now);
                return false;
            case SendOutcome.Rejected rejected:
                _log.LogWarning("The server refused the consent change for good: {Reason}", rejected.Text);
                Posted();
                return true;
            default:
                _log.LogInformation("The consent change didn't reach the server ({Reason}); it will be tried again", outcome.Reason);
                _store.ConsentBackoff = SendSchedule.After(_store.ConsentBackoff, now);
                return true;
        }
    }

    /// <summary>The consent change needs posting no more.</summary>
    private void Posted()
    {
        _store.ConsentPending = false;
        _store.ConsentBackoff = null;
    }

    /// <summary>
    /// Sends the complete days waiting, oldest first and at most <see cref="MaxDaysPerRun"/>, each with the sections switched
    /// on as it is built, and the hardware when it changed since it last went. Before each day it makes way for a request
    /// the App waits on, so a switch the App turns off is off for every day not yet under way: the five-minute run gives way
    /// and starts again once the App has its answer, and Send now stops, saying what went.
    /// </summary>
    /// <param name="answerBy">Send now's time limit; none for the five-minute run, whose requests give way to the App instead.</param>
    private async Task<RunResult> SendAsync(DateTimeOffset now, CancellationToken answerBy, CancellationToken stop)
    {
        var forApp = answerBy.CanBeCanceled;
        var lastRun = _store.LastRun;
        _store.LastRun = now.ToUnixTimeMilliseconds();
        var (id, key) = _store.Identity();
        var result = new RunResult();
        try
        {
            for (var days = 0; days < MaxDaysPerRun; days++)
            {
                if (_commands.AppWaiting)
                {
                    if (!forApp) throw new GiveWay();
                    break;                                                              // the rest go at the next chance
                }
                if (answerBy.IsCancellationRequested) break;
                var consent = _store.Consent;
                if (!consent.AllowsAny || CompleteDays(now, consent) is not [var day, ..]) break;

                var inputs = Inputs(day, consent, id, consent.Power ? _outbox.Minutes(day) : [], OutboxEvents.Read(_outbox, day), withHardware: false, now);
                var hash = consent.Power ? ReportJson.Hash(ReportBuilder.Hardware(inputs)) : null;
                var report = ReportBuilder.Build(inputs with { WithHardware = hash is not null && hash != _store.HardwareHash });
                if (report is { Diagnostics: null, Usage: null, Power: null })
                {
                    _outbox.DeleteDay(day);                                            // nothing the switches allow is left in it
                    continue;
                }

                var body = SharingClient.Gzip(ReportJson.Write(report));
                Task<SendOutcome> Send(CancellationToken cancel) => _client.SendReportAsync(body, key, cancel);
                var outcome = forApp
                    ? await CallAsync(Send, stop, answerBy).ConfigureAwait(false)
                    : await OwnCallAsync(Send, stop).ConfigureAwait(false);
                switch (outcome)
                {
                    case SendOutcome.Accepted:
                        Accepted(day, body, report.Power?.Hardware is null ? null : hash, now);
                        result.Sent++;
                        break;
                    case SendOutcome.Rejected rejected:
                        _log.LogWarning("The server rejected {Day}: {Reason}", day, rejected.Text);
                        _outbox.DeleteDay(day);
                        Close(day);
                        _store.Problem = new SendProblem(rejected.Text, Rejected: true);
                        result.Rejected = rejected.Text;
                        break;
                    case SendOutcome.Gone:
                        _log.LogInformation("The server has deleted this install; forgetting it");
                        Forget(now);
                        result.Gone = true;
                        return result;
                    default:
                        var reason = outcome.Reason ?? "the server didn't take it";
                        _log.LogInformation("Sending {Day} failed ({Reason}); trying again later", day, reason);
                        _store.Problem = new SendProblem(reason, Rejected: false);
                        _store.Backoff = SendSchedule.After(_store.Backoff, now);
                        result.Failed = reason;
                        return result;
                }
            }
        }
        catch (GiveWay)
        {
            _store.LastRun = lastRun;                                                   // not a run: it starts again
            throw;
        }
        return result;
    }

    /// <summary>The day leaves the outbox, and a copy of what went is kept for the user to see.</summary>
    private void Accepted(string day, byte[] body, string? hardwareHash, DateTimeOffset now)
    {
        _outbox.DeleteDay(day);
        Close(day);
        WriteSent(day, body);
        _store.LastSent = new LastSent(now.ToUnixTimeMilliseconds(), body.Length);
        _store.Problem = null;
        _store.Backoff = null;
        Posted();                                                              // the report carried the consent as it is now
        if (hardwareHash is not null) _store.HardwareHash = hardwareHash;
    }

    /// <summary>Marks the day, and every one before it, as done: nothing more is filed under them.</summary>
    private void Close(string day)
    {
        if (_store.SentThrough is not { } through || string.CompareOrdinal(day, through) > 0) _store.SentThrough = day;
    }

    private void WriteSent(string day, byte[] body)
    {
        try
        {
            Directory.CreateDirectory(_environment.Sent);
            File.WriteAllBytes(Path.Combine(_environment.Sent, day + ".json.gz"), body);
            foreach (var old in Directory.GetFiles(_environment.Sent, "*.json.gz").OrderDescending(StringComparer.Ordinal).Skip(KeepSent))
            {
                TryDelete(old);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(error, "The copy of what was sent for {Day} could not be kept", day);
        }
    }

    /// <summary>As a 410 or a delete: every switch off, the ID, the key and every state forgotten, the outbox emptied and the
    /// copies of what was sent deleted.</summary>
    private void Forget(DateTimeOffset now)
    {
        _store.Forget(now.ToUnixTimeMilliseconds());
        _outbox.Clear();
        DeleteCrashFiles();
        if (Directory.Exists(_environment.Sent))
        {
            foreach (var file in Directory.GetFiles(_environment.Sent)) TryDelete(file);
        }
    }

    /// <summary>The days waiting that are over and fully collected, oldest first.</summary>
    private List<string> CompleteDays(DateTimeOffset now, Consent consent)
    {
        var limit = LocalDays.Of(now, Zone);
        if (consent.Power && _store.CollectedTo is { } collected)
        {
            var through = LocalDays.Of(DateTimeOffset.FromUnixTimeMilliseconds(collected), Zone);
            if (through < limit) limit = through;
        }
        return [.. _outbox.Days().Where(day => TryParse(day, out var date) && date < limit)];
    }

    /// <summary>The day counts are kept under: their own, unless it is already sent, still to come or too old to send,
    /// when they are kept under today.</summary>
    private string FileDay(string day, DateTimeOffset now)
    {
        var today = LocalDays.Of(now, Zone);
        if (!TryParse(day, out var date) || date > today || date < today.AddDays(-KeepDays)) return LocalDays.Text(today);
        return _store.SentThrough is { } through && string.CompareOrdinal(day, through) <= 0 ? LocalDays.Text(today) : day;
    }

    private ReportInputs Inputs(
        string day, Consent consent, string installId, IReadOnlyList<MinuteRow> minutes, DayEvents events, bool withHardware, DateTimeOffset now) =>
        new(_environment.Host(), installId, consent, day, LocalDays.UtcOffsetMinutes(LocalDays.Parse(day), Zone), minutes, events,
            _board.Status, _board.Facts, _board.Settings ?? ServiceSettings.Default, _tariffs.Schedule().At(now), _board.DiscreteGpu,
            withHardware, _environment.Names());

    /// <summary>Makes a request for one the App waits on. One that doesn't answer before <paramref name="deadline"/> counts as
    /// no answer, and so does one there is no time left to make, which isn't made.</summary>
    private static async Task<SendOutcome> CallAsync(
        Func<CancellationToken, Task<SendOutcome>> request, CancellationToken stop, CancellationToken deadline)
    {
        if (deadline.IsCancellationRequested) return new SendOutcome.Unreachable(NoTimeLeft);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, deadline);
        try
        {
            return await request(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            return new SendOutcome.Unreachable(NotInTime);
        }
    }

    /// <summary>Makes a request of the worker's own, which gives way to the App: a request the App waits on, queued before
    /// or while it is made, cancels it, and the work stops there (<see cref="GiveWay"/>).</summary>
    private async Task<SendOutcome> OwnCallAsync(Func<CancellationToken, Task<SendOutcome>> request, CancellationToken stop)
    {
        var attention = _commands.Attention;
        if (attention.IsCancellationRequested) throw new GiveWay();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, attention);
        try
        {
            return await request(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested && attention.IsCancellationRequested)
        {
            throw new GiveWay();
        }
    }

    /// <summary>When a request the App waits on must be answered: <see cref="AppWait"/> after it was queued, however long
    /// those before it took, so the pipe has the answer inside its own limit.</summary>
    private CancellationTokenSource AnswerBy(SharingCommand command)
    {
        var waited = command.QueuedAt is { } queued ? _clock.GetUtcNow() - queued : TimeSpan.Zero;
        if (waited < AppWait) return new CancellationTokenSource(waited > TimeSpan.Zero ? AppWait - waited : AppWait, _clock);
        var spent = new CancellationTokenSource();
        spent.Cancel();
        return spent;
    }

    private string Today(DateTimeOffset now) => LocalDays.Text(LocalDays.Of(now, Zone));

    private string[] CrashFiles() => Directory.Exists(_environment.Crashes) ? Directory.GetFiles(_environment.Crashes, "*.json") : [];

    private void DeleteCrashFiles()
    {
        foreach (var file in CrashFiles()) TryDelete(file);
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(error, "{File} could not be deleted yet", file);
        }
    }

    private static bool TryParse(string day, out DateOnly date) =>
        DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>Thrown out of the worker's own work, where nothing is half-done, once a request the App waits on is queued:
    /// the work stops, the App is answered, and the work starts again.</summary>
    private sealed class GiveWay() : Exception("A request the App waits on comes first.");

    /// <summary>What a run did, for the App's Send now.</summary>
    private sealed class RunResult
    {
        public int Sent { get; set; }

        public string? Rejected { get; set; }

        public string? Failed { get; set; }

        public bool Gone { get; set; }

        public (bool Ok, string Message) Reply =>
            Gone ? (false, "The server has deleted this PC's data, so every switch is now off.")
            : Failed is { } failed ? (false, $"Couldn't send: {failed}. Will try again.")
            : Rejected is { } rejected ? (false, $"Rejected by the server: {rejected}.")
            : Sent == 0 ? (true, "Nothing went in time; the rest will go at the next chance.")
            : (true, Sent == 1 ? "Sent 1 day." : $"Sent {Sent} days.");
    }
}
