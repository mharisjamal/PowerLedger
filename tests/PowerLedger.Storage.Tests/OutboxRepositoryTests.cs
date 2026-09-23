using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

/// <summary>What waits to be sent (data-sharing design §4): minute rows, and events by local day.</summary>
public class OutboxRepositoryTests
{
    private static MinuteRow Minute(string day, int index, double watts = 50, double? gpuLoad = 0.25) => new(
        StartMs: DateTimeOffset.Parse(day + "T00:00:00Z").AddMinutes(index).ToUnixTimeMilliseconds(), day, index,
        watts, watts + 10, 1, 2, 3, 4, 5, 6, 7, 8, 9, -1.5, 0.125, gpuLoad, null, 60, 1, 2, 3, 4, 5, 51, 60, 3, 1, 5);

    [Fact]
    public void Minutes_round_trip_by_day_and_a_minute_written_again_replaces_the_first()
    {
        using var t = new TestDatabase();
        var outbox = new OutboxRepository(t.Db);
        outbox.InsertMinutes([Minute("2026-09-23", 1), Minute("2026-09-24", 2, gpuLoad: null), Minute("2026-09-24", 1)]);
        outbox.InsertMinutes([Minute("2026-09-24", 2, watts: 70)]);

        outbox.MinuteDays().ShouldBe(new[] { "2026-09-23", "2026-09-24" });
        outbox.Minutes("2026-09-24").ShouldBe(new[] { Minute("2026-09-24", 1), Minute("2026-09-24", 2, watts: 70) });
        outbox.Minutes("2026-09-23").ShouldBe(new[] { Minute("2026-09-23", 1) });
        outbox.Minutes("2026-09-22").ShouldBeEmpty();
    }

    [Fact]
    public void Events_are_kept_by_day_and_kind_in_the_order_they_came()
    {
        using var t = new TestDatabase();
        var outbox = new OutboxRepository(t.Db);
        outbox.AddEvent("2026-09-24", "crash", "{\"n\":1}");
        outbox.AddEvent("2026-09-23", "crash", "{\"n\":0}");
        outbox.AddEvent("2026-09-24", "crash", "{\"n\":2}");
        outbox.AddEvent("2026-09-24", "usage", "{}");

        outbox.EventDays().ShouldBe(new[] { "2026-09-23", "2026-09-24" });
        outbox.Events("2026-09-24", "crash").ShouldBe(new[] { "{\"n\":1}", "{\"n\":2}" });
        outbox.CountEvents("2026-09-24", "crash").ShouldBe(2);
        outbox.Events("2026-09-24", "sources").ShouldBeEmpty();
    }

    [Fact]
    public void A_merged_event_is_one_row_a_day_that_the_merge_sees_and_replaces()
    {
        using var t = new TestDatabase();
        var outbox = new OutboxRepository(t.Db);
        var seen = new List<string?>();
        string Append(string? json, string add)
        {
            seen.Add(json);
            return (json ?? "") + add;
        }

        outbox.MergeEvent("2026-09-24", "usage", json => Append(json, "a"));
        outbox.MergeEvent("2026-09-24", "usage", json => Append(json, "b"));
        outbox.MergeEvent("2026-09-25", "usage", json => Append(json, "c"));

        seen.ShouldBe(new[] { null, "a", null });
        outbox.Events("2026-09-24", "usage").ShouldBe(new[] { "ab" });
        outbox.Events("2026-09-25", "usage").ShouldBe(new[] { "c" });
    }

    [Fact]
    public void A_day_goes_with_its_minutes_and_events_and_the_rest_stays()
    {
        using var t = new TestDatabase();
        var outbox = new OutboxRepository(t.Db);
        outbox.InsertMinutes([Minute("2026-09-23", 1), Minute("2026-09-24", 1)]);
        outbox.AddEvent("2026-09-23", "crash", "{}");
        outbox.AddEvent("2026-09-24", "crash", "{}");

        outbox.DeleteDay("2026-09-23");

        outbox.MinuteDays().ShouldBe(new[] { "2026-09-24" });
        outbox.EventDays().ShouldBe(new[] { "2026-09-24" });
        outbox.Days().ShouldBe(new[] { "2026-09-24" });
    }

    [Fact]
    public void A_switch_turned_off_takes_its_kind_away_and_clear_takes_everything()
    {
        using var t = new TestDatabase();
        var outbox = new OutboxRepository(t.Db);
        outbox.InsertMinutes([Minute("2026-09-23", 1)]);
        outbox.AddEvent("2026-09-22", "crash", "{}");
        outbox.AddEvent("2026-09-24", "usage", "{}");
        outbox.Days().ShouldBe(new[] { "2026-09-22", "2026-09-23", "2026-09-24" });

        outbox.DeleteMinutes();
        outbox.MinuteDays().ShouldBeEmpty();
        outbox.DeleteEvents("crash");
        outbox.EventDays().ShouldBe(new[] { "2026-09-24" });

        outbox.InsertMinutes([Minute("2026-09-23", 1)]);
        outbox.Clear();
        outbox.Days().ShouldBeEmpty();
    }

    [Fact]
    public void What_waits_survives_a_restart()
    {
        using var t = new TestDatabase();
        new OutboxRepository(t.Db).InsertMinutes([Minute("2026-09-24", 7)]);
        new OutboxRepository(t.Db).AddEvent("2026-09-24", "crash", "{}");

        using var reopened = SqliteDatabase.OpenAndMigrate(t.Path);
        var outbox = new OutboxRepository(reopened);
        outbox.Minutes("2026-09-24").ShouldBe(new[] { Minute("2026-09-24", 7) });
        outbox.Events("2026-09-24", "crash").ShouldBe(new[] { "{}" });
    }
}
