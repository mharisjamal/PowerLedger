using System.Text.RegularExpressions;
using PowerLedger.Contracts;
using PowerLedger.Service.Sharing;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Where sharing keeps its consent, identity and progress: the service's settings table (data-sharing design §4).</summary>
public sealed class SharingStoreTests : IDisposable
{
    private readonly TestDatabase _database = new();

    private SharingStore Store(Func<int>? pickMinute = null) => new(new SettingsRepository(_database.Db), pickMinute);

    [Fact]
    public void Nothing_is_held_until_the_user_answers()
    {
        var store = Store();

        store.Consent.ShouldBe(Consent.Unanswered);
        store.StoredConsent.ShouldBeNull();
        (store.InstallId, store.Key, store.CollectedTo, store.LastSent, store.Problem, store.Backoff).ShouldBe((null, null, null, null, null, null));
        (store.HardwareHash, store.ConsentPending, store.LastRun, store.SentThrough).ShouldBe((null, false, null, null));
    }

    [Fact]
    public void Each_piece_of_state_comes_back_as_it_was_kept()
    {
        var store = Store();
        var consent = new StoredConsent(new Consent(ConsentText.Version, true, false, true, true), 1_000, DiagnosticsSinceMs: 900);
        store.SaveConsent(consent);
        store.CollectedTo = 2_000;
        store.LastSent = new LastSent(3_000, 41_234);
        store.Problem = new SendProblem("The server said no.", Rejected: true);
        store.Backoff = new Backoff(3, 4_000);
        store.HardwareHash = "abc123";
        store.ConsentPending = true;
        store.LastRun = 5_000;
        store.SentThrough = "2026-09-23";

        var again = Store();
        again.StoredConsent.ShouldBe(consent);
        again.Consent.ShouldBe(consent.Consent);
        again.CollectedTo.ShouldBe(2_000);
        again.LastSent.ShouldBe(new LastSent(3_000, 41_234));
        again.Problem.ShouldBe(new SendProblem("The server said no.", true));
        again.Backoff.ShouldBe(new Backoff(3, 4_000));
        (again.HardwareHash, again.ConsentPending, again.LastRun, again.SentThrough).ShouldBe(("abc123", true, 5_000L, "2026-09-23"));

        again.Problem = null;
        again.Backoff = null;
        again.ConsentPending = false;
        (Store().Problem, Store().Backoff, Store().ConsentPending).ShouldBe((null, null, false));
    }

    [Fact]
    public void The_send_minute_is_chosen_once_and_kept()
    {
        var picks = 0;
        var first = Store(() => ++picks == 1 ? 200 : 300);

        first.SendMinute.ShouldBe(200);
        first.SendMinute.ShouldBe(200);
        Store(() => 300).SendMinute.ShouldBe(200);
        picks.ShouldBe(1);
    }

    [Fact]
    public void Left_to_chance_the_send_minute_falls_between_ten_past_midnight_and_one_minute_to_six()
    {
        using var other = new TestDatabase();
        foreach (var _ in Enumerable.Range(0, 50))
        {
            var settings = new SettingsRepository(other.Db);
            settings.Remove(SharingStore.MinuteKey);
            new SharingStore(settings).SendMinute.ShouldBeInRange(10, 359);
        }
    }

    [Fact]
    public void The_install_id_is_a_random_guid_and_the_key_32_random_bytes_in_base64url_both_made_once()
    {
        var store = Store();

        var (id, key) = store.Identity();

        Guid.TryParseExact(id, "D", out _).ShouldBeTrue();
        id.ShouldBe(id.ToLowerInvariant());
        Regex.IsMatch(key, "^[A-Za-z0-9_-]{43}$").ShouldBeTrue(key);
        Store().Identity().ShouldBe((id, key));
        (Store().InstallId, Store().Key).ShouldBe((id, key));
        using var another = new TestDatabase();
        new SharingStore(new SettingsRepository(another.Db)).Identity().Id.ShouldNotBe(id);
    }

    [Fact]
    public void Forgetting_drops_the_id_the_key_and_every_state_and_turns_every_switch_off_but_keeps_the_send_minute()
    {
        var store = Store(() => 42);
        store.SaveConsent(new StoredConsent(new Consent(ConsentText.Version, true, true, true, true), 1, 1));
        store.Identity();
        _ = store.SendMinute;
        store.CollectedTo = 2;
        store.LastSent = new LastSent(3, 4);
        store.Problem = new SendProblem("x", false);
        store.Backoff = new Backoff(1, 5);
        store.HardwareHash = "h";
        store.ConsentPending = true;
        store.LastRun = 6;
        store.SentThrough = "2026-09-20";

        store.Forget(nowMs: 99);

        var after = Store(() => 7);
        after.StoredConsent.ShouldBe(new StoredConsent(new Consent(ConsentText.Version, false, false, false, false), 99, null));
        after.Consent.Answered.ShouldBeTrue();
        (after.InstallId, after.Key, after.CollectedTo, after.LastSent, after.Problem, after.Backoff).ShouldBe((null, null, null, null, null, null));
        (after.HardwareHash, after.ConsentPending, after.LastRun, after.SentThrough).ShouldBe((null, false, null, null));
        after.SendMinute.ShouldBe(42);
    }

    [Fact]
    public void Usage_merged_into_a_day_adds_the_counts_and_replaces_the_states()
    {
        var outbox = new OutboxRepository(_database.Db);
        var morning = new UsageCounts("2026-09-24", 2, new Dictionary<string, int> { ["now"] = 3, ["history"] = 1 },
            new Dictionary<string, int> { ["theme"] = 1 }, 1, 0, 11, "light", "en-GB");
        var evening = new UsageCounts("2026-09-24", 1, new Dictionary<string, int> { ["now"] = 2, ["settings"] = 4 },
            new Dictionary<string, int>(), 0, 1, 12, "dark", "en-US");

        outbox.MergeEvent("2026-09-24", OutboxEvents.Usage, json => OutboxEvents.MergeUsage(json, morning));
        outbox.MergeEvent("2026-09-24", OutboxEvents.Usage, json => OutboxEvents.MergeUsage(json, evening));

        var merged = OutboxEvents.Read(outbox, "2026-09-24").Usage.ShouldNotBeNull();
        (merged.Day, merged.AppOpens, merged.ReportsExported, merged.UpdatesInstalled).ShouldBe(("2026-09-24", 3, 1, 1));
        merged.Pages.ShouldBe(new Dictionary<string, int> { ["now"] = 5, ["history"] = 1, ["settings"] = 4 });
        merged.Settings.ShouldBe(new Dictionary<string, int> { ["theme"] = 1 });
        (merged.DaysSinceFirstRun, merged.Theme, merged.Language).ShouldBe((12, "dark", "en-US"));
        merged.Validate().ShouldBeNull();
    }

    [Fact]
    public void Merged_usage_never_grows_past_what_the_counts_allow()
    {
        var many = Enumerable.Range(0, 40).ToDictionary(i => $"page{i}", _ => UsageCounts.MaxCount);
        var more = Enumerable.Range(30, 40).ToDictionary(i => $"page{i}", _ => 1);
        var first = new UsageCounts("2026-09-24", UsageCounts.MaxCount, many, new Dictionary<string, int>(), 0, 0, 1, "dark", "en");

        var json = OutboxEvents.MergeUsage(OutboxEvents.MergeUsage(null, first), first with { Pages = more, AppOpens = 5 });

        var merged = OutboxEvents.ParseUsage(json).ShouldNotBeNull();
        merged.Pages.Count.ShouldBe(UsageCounts.MaxNames);
        merged.AppOpens.ShouldBe(UsageCounts.MaxCount);
        merged.Pages["page0"].ShouldBe(UsageCounts.MaxCount);
        merged.Validate().ShouldBeNull();
    }

    [Fact]
    public void Source_failures_merged_into_a_day_add_up_and_keep_the_last_error()
    {
        var outbox = new OutboxRepository(_database.Db);

        outbox.MergeEvent("2026-09-24", OutboxEvents.Sources, json => OutboxEvents.MergeSources(json,
            new Dictionary<string, SourceDay> { ["battery"] = new(2, "First."), ["ups"] = new(0, null) }));
        outbox.MergeEvent("2026-09-24", OutboxEvents.Sources, json => OutboxEvents.MergeSources(json,
            new Dictionary<string, SourceDay> { ["battery"] = new(1, "Second."), ["ups"] = new(0, null), ["display"] = new(1, "Third.") }));

        OutboxEvents.Read(outbox, "2026-09-24").Sources.ShouldBe(new Dictionary<string, SourceDay>
        {
            ["battery"] = new(3, "Second."),
            ["ups"] = new(0, null),
            ["display"] = new(1, "Third."),
        });
    }

    [Fact]
    public void A_days_crashes_come_back_oldest_first_and_a_day_without_events_has_none()
    {
        var outbox = new OutboxRepository(_database.Db);
        var crash = new CrashReport(SharingFakes.At, "service", "0.6.0", ["System.Exception"], "Boom.", "   at X()");
        outbox.AddEvent("2026-09-24", OutboxEvents.Crash, OutboxEvents.Write(crash));
        outbox.AddEvent("2026-09-24", OutboxEvents.Crash, OutboxEvents.Write(crash with { Message = "Again." }));
        outbox.AddEvent("2026-09-24", OutboxEvents.Crash, "not json");

        OutboxEvents.Read(outbox, "2026-09-24").Crashes.Select(c => c.Message).ShouldBe(new[] { "Boom.", "Again." });
        var none = OutboxEvents.Read(outbox, "2026-09-23");
        (none.Sources.Count, none.Crashes.Count, none.Usage).ShouldBe((0, 0, null));
    }

    public void Dispose() => _database.Dispose();
}
