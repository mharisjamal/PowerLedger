using Microsoft.Data.Sqlite;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

/// <summary>
/// Plan AE: the Apps page's read of <c>app_hours</c> (Schema.V6), with the hour rows' screen, rest, sleep and charging
/// losses for the hours apps were counted in, each hour priced at the tariff then in force. Rows are put straight into
/// the tables, as the service would leave them.
/// </summary>
public class AppQueriesTests
{
    private static readonly DateTimeOffset H0 = Fixtures.T0;   // 12:00 UTC on 8 Sep 2026

    private static void App(TestDatabase t, DateTimeOffset hour, string key, string name, double cpu, double gpu = 0, double run = 3600, double front = 0, string? path = null)
    {
        using var c = t.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO app_hours (start_ms, app_key, name, path, cpu_wh, gpu_wh, run_s, front_s) VALUES ($s, $k, $n, $p, $c, $g, $r, $f)";
        cmd.Parameters.AddWithValue("$s", hour.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$p", (object?)path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", cpu);
        cmd.Parameters.AddWithValue("$g", gpu);
        cmd.Parameters.AddWithValue("$r", run);
        cmd.Parameters.AddWithValue("$f", front);
        cmd.ExecuteNonQuery();
    }

    /// <summary>An hour row as the hourly job folds one: the parts at the pack, the hour on throughout.</summary>
    private static Aggregate Hour(DateTimeOffset start, double cpu, double gpu, double display, double rest, double asleep = 0, double gap = 0, double battery = 0, double on = 3600)
        => Aggregate.Empty(start) with
        {
            EnergyWh = cpu + gpu + display + rest, CpuWh = cpu, GpuWh = gpu, DisplayWh = display, RestWh = rest,
            OnSeconds = on, MeasuredSeconds = on, SampleCount = (int)on, AvgW = (cpu + gpu + display + rest) * 3600 / Math.Max(1, on),
            AsleepWh = asleep, GapSeconds = gap, BatteryWh = battery,
        };

    [Fact]
    public void A_history_with_no_apps_yet_reads_as_none_counted()
    {
        using var t = new TestDatabase();
        new AggregateRepository(t.Db).UpsertHour(Hour(H0, 10, 2, 4, 6));

        var range = new AppQueries(t.Db).Read(H0, H0.AddHours(2));

        range.Apps.ShouldBeEmpty();
        range.CountedSince.ShouldBeNull();
        range.Screen.Wh.ShouldBe(0);
        range.Rest.Wh.ShouldBe(0);
        range.Untraced.Wh.ShouldBe(0);
    }

    /// <summary>The App opens the database read-only and may meet one the service has not upgraded to version 6 yet.</summary>
    [Fact]
    public void A_database_from_before_version_6_reads_as_none_counted()
    {
        using var t = new TestDatabase();
        using (var c = t.Db.Open())
        {
            using var drop = c.CreateCommand();
            drop.CommandText = "DROP TABLE app_hours";
            drop.ExecuteNonQuery();
        }
        using var reader = new SqliteDatabase(t.Path, readOnly: true);

        var range = new AppQueries(reader).Read(H0, H0.AddHours(2));

        range.Apps.ShouldBeEmpty();
        range.CountedSince.ShouldBeNull();
    }

    [Fact]
    public void Each_app_adds_up_over_its_hours_most_first_named_as_its_newest_hour_names_it()
    {
        using var t = new TestDatabase();
        App(t, H0, @"c:\program files\google\chrome\application\chrome.exe", "Google Chrome", 3, 1, run: 3600, front: 1200, path: @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        App(t, H0, "::windows", "Windows", 2, 0.5);
        App(t, H0.AddHours(1), @"c:\program files\google\chrome\application\chrome.exe", "Chrome", 5, 2, run: 1800, front: 600, path: @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        App(t, H0.AddHours(1), "pkg:microsoft.windowsterminal_8wekyb3d8bbwe", "Terminal", 9, 0, run: 3600);

        using var reader = new SqliteDatabase(t.Path, readOnly: true);
        var range = new AppQueries(reader).Read(H0, H0.AddHours(2));

        range.Apps.Select(a => a.Key).ShouldBe([@"c:\program files\google\chrome\application\chrome.exe", "pkg:microsoft.windowsterminal_8wekyb3d8bbwe", "::windows"]);
        var chrome = range.Apps[0];
        chrome.Name.ShouldBe("Chrome");
        chrome.Path.ShouldBe(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
        (chrome.CpuWh, chrome.GpuWh, chrome.Wh).ShouldBe((8, 3, 11));
        (chrome.RunSeconds, chrome.FrontSeconds).ShouldBe((5400, 1800));
        range.Apps[1].Path.ShouldBeNull();
        range.CountedSince.ShouldBe(H0);
    }

    /// <summary>The screen, the rest, and processor and graphics energy no app was given come from the hour rows of the
    /// hours apps were counted in; an hour from before PowerLedger counted apps is no part of the page.</summary>
    [Fact]
    public void The_hour_rows_count_only_from_the_first_hour_apps_were_counted()
    {
        using var t = new TestDatabase();
        var hours = new AggregateRepository(t.Db);
        hours.UpsertHour(Hour(H0, 10, 2, 4, 6));                                  // before apps were counted
        hours.UpsertHour(Hour(H0.AddHours(1), 10, 2, 5, 7));
        hours.UpsertHour(Hour(H0.AddHours(2), 20, 4, 6, 8));
        App(t, H0.AddHours(1), "a", "A", 7, 2);
        App(t, H0.AddHours(1), "::idle", "Idle", 3, 0);                          // the hour wholly traced
        App(t, H0.AddHours(2), "a", "A", 12, 4);                                 // 8 Wh of the hour's processor traced to no app

        var range = new AppQueries(t.Db).Read(H0, H0.AddHours(3));

        range.CountedSince.ShouldBe(H0.AddHours(1));
        range.From.ShouldBe(H0.AddHours(1));
        range.Screen.Wh.ShouldBe(11, 1e-9);
        range.Rest.Wh.ShouldBe(15, 1e-9);
        range.Untraced.Wh.ShouldBe(8, 1e-9);
        (range.Apps.Sum(a => a.Wh) + range.Untraced.Wh).ShouldBe(10 + 2 + 20 + 4, 1e-9);
    }

    /// <summary>An hour whose apps were given a little more than its row holds (the hour under way, its apps a tick ahead
    /// of its last minute) traces nothing less than nothing.</summary>
    [Fact]
    public void An_hour_traced_past_its_row_leaves_nothing_untraced()
    {
        using var t = new TestDatabase();
        new AggregateRepository(t.Db).UpsertHour(Hour(H0, 10, 0, 4, 6));
        App(t, H0, "a", "A", 10.4);

        new AppQueries(t.Db).Read(H0, H0.AddHours(1)).Untraced.Wh.ShouldBe(0);
    }

    /// <summary>The hour under way has no hour row yet: its minutes stand in for it, as the report reads them.</summary>
    [Fact]
    public void The_hour_under_way_is_read_from_its_minutes()
    {
        using var t = new TestDatabase();
        var rows = new AggregateRepository(t.Db);
        rows.UpsertHour(Hour(H0, 6, 0, 3, 3));
        for (var m = 0; m < 30; m++) rows.UpsertMinute(Hour(H0.AddHours(1).AddMinutes(m), 0.2, 0.05, 0.1, 0.15, on: 60));
        App(t, H0, "a", "A", 6);
        App(t, H0.AddHours(1), "a", "A", 5);

        var range = new AppQueries(t.Db).Read(H0, H0.AddHours(1).AddMinutes(30));

        range.Screen.Wh.ShouldBe(3 + 30 * 0.1, 1e-9);
        range.Rest.Wh.ShouldBe(3 + 30 * 0.15, 1e-9);
        range.Untraced.Wh.ShouldBe(30 * 0.25 - 5, 1e-9);
    }

    [Fact]
    public void Each_hour_is_priced_at_the_tariff_in_force_then()
    {
        using var t = new TestDatabase();
        var rows = new AggregateRepository(t.Db);
        rows.UpsertHour(Hour(H0, 1000, 0, 500, 250));
        rows.UpsertHour(Hour(H0.AddHours(1), 2000, 0, 500, 250));
        App(t, H0, "a", "A", 1000);
        App(t, H0.AddHours(1), "a", "A", 2000);
        var tariffs = new TariffRepository(t.Db);
        tariffs.Add(new Tariff(H0.AddDays(-1), 0.20m, "USD"));
        tariffs.Add(new Tariff(H0.AddHours(1), 0.50m, "USD"));

        var range = new AppQueries(t.Db).Read(H0, H0.AddHours(2));

        range.Apps.Single().Cost.ShouldBe(1m * 0.20m + 2m * 0.50m);
        range.Screen.Cost.ShouldBe(0.5m * 0.20m + 0.5m * 0.50m);
        range.Rest.Cost.ShouldBe(0.25m * 0.20m + 0.25m * 0.50m);
        range.Currency.ShouldBe("USD");
        range.CostIsPartial.ShouldBeFalse();
    }

    [Fact]
    public void Energy_priced_in_another_currency_is_left_out_and_said_so()
    {
        using var t = new TestDatabase();
        new AggregateRepository(t.Db).UpsertHour(Hour(H0, 1000, 0, 0, 0));
        new AggregateRepository(t.Db).UpsertHour(Hour(H0.AddHours(1), 1000, 0, 0, 0));
        App(t, H0, "a", "A", 1000);
        App(t, H0.AddHours(1), "a", "A", 1000);
        var tariffs = new TariffRepository(t.Db);
        tariffs.Add(new Tariff(H0.AddDays(-1), 0.20m, "EUR"));
        tariffs.Add(new Tariff(H0.AddHours(1), 30m, "PKR"));

        var range = new AppQueries(t.Db).Read(H0, H0.AddHours(2));

        range.Apps.Single().Cost.ShouldBe(30m);
        range.Currency.ShouldBe("PKR");
        range.CostIsPartial.ShouldBeTrue();
    }

    /// <summary>What the PC drew asleep, as the report counts it (accuracy A10): laid back from the wake and clipped to the
    /// hours apps were counted in; and what charging lost for the energy the battery gave (A9), as the bands' line.</summary>
    [Fact]
    public void Sleep_is_laid_back_from_the_wake_and_charging_losses_follow_the_battery()
    {
        using var t = new TestDatabase();
        var rows = new AggregateRepository(t.Db);
        // Woke 30 minutes into the second hour after 3 hours asleep at 1 W: 1.5 of those hours fall in the range.
        rows.UpsertHour(Hour(H0, 10, 0, 0, 0, battery: 10));
        rows.UpsertHour(Hour(H0.AddHours(1), 5, 0, 0, 0, asleep: 3, gap: 3 * 3600, on: 1800));
        App(t, H0, "a", "A", 10);
        App(t, H0.AddHours(1), "a", "A", 5);

        var range = new AppQueries(t.Db).Read(H0, H0.AddHours(2));

        range.Asleep.Wh.ShouldBe(1.5, 1e-9);
        range.Losses.Wh.ShouldBe(10 * (BatteryRoundTrip.Factor - 1), 1e-9);
    }

    /// <summary>In a zone half an hour off UTC a local day starts half way through an hour. App rows are by the hour, so
    /// each hour is in the day its start falls in, as a long report's hour rows are cut and its days grouped: the hour from
    /// 23:30 to 00:30 is the day before's, and never in both days. Cut by UTC whatever offset the range's ends carry.</summary>
    [Fact]
    public void An_hour_is_in_the_range_its_start_falls_in()
    {
        using var t = new TestDatabase();
        var rows = new AggregateRepository(t.Db);
        rows.UpsertHour(Hour(H0, 4, 0, 1, 1));
        rows.UpsertHour(Hour(H0.AddHours(1), 6, 0, 2, 2));
        App(t, H0, "a", "A", 4);
        App(t, H0.AddHours(1), "a", "A", 6);
        var india = TimeSpan.FromHours(5.5);

        var before = new AppQueries(t.Db).Read(H0.AddMinutes(-30).ToOffset(india), H0.AddMinutes(30).ToOffset(india));
        var after = new AppQueries(t.Db).Read(H0.AddMinutes(30).ToOffset(india), H0.AddMinutes(90).ToOffset(india));

        (before.Apps.Single().Wh, before.Screen.Wh, before.From).ShouldBe((4, 1, H0));
        (after.Apps.Single().Wh, after.Screen.Wh, after.From).ShouldBe((6, 2, H0.AddHours(1)));
    }

    [Fact]
    public void Hours_after_the_range_are_left_out()
    {
        using var t = new TestDatabase();
        App(t, H0, "a", "A", 4);
        App(t, H0.AddHours(1), "a", "A", 6);
        App(t, H0.AddHours(1), "b", "B", 1);

        var range = new AppQueries(t.Db).Read(H0, H0.AddHours(1));

        range.Apps.Single().Wh.ShouldBe(4);
        range.CountedSince.ShouldBe(H0);
    }

    [Fact]
    public void A_range_before_apps_were_counted_has_none_but_says_since_when()
    {
        using var t = new TestDatabase();
        new AggregateRepository(t.Db).UpsertHour(Hour(H0, 10, 0, 3, 3));
        App(t, H0.AddHours(5), "a", "A", 4);

        var range = new AppQueries(t.Db).Read(H0, H0.AddHours(1));

        range.Apps.ShouldBeEmpty();
        range.Screen.Wh.ShouldBe(0);
        range.CountedSince.ShouldBe(H0.AddHours(5));
    }

    [Fact]
    public void A_database_the_reader_cannot_open_throws_as_the_report_does()
        => Should.Throw<SqliteException>(() => new AppQueries(new SqliteDatabase(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.db"), readOnly: true))
            .Read(H0, H0.AddHours(1)));
}
