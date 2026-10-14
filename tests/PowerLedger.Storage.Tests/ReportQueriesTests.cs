using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class ReportQueriesTests
{
    private static void SeedThreeHours(TestDatabase t)
    {
        var agg = new AggregateRepository(t.Db);
        for (var i = 0; i < 120; i++)
            agg.UpsertMinute(Downsampler.ToMinute(Fixtures.T0.AddMinutes(i), Fixtures.Minute(i * 60, 30, Quality.Measured)));
        for (var i = 120; i < 180; i++)
        {
            var readings = Enumerable.Range(i * 60, 60).Select(s => Fixtures.Reading(s, 60, Quality.Estimated, idle: true)).ToList();
            agg.UpsertMinute(Downsampler.ToMinute(Fixtures.T0.AddMinutes(i), readings));
        }
        new TariffRepository(t.Db).Add(new Tariff(Fixtures.T0, 0.20m, "USD"));
    }

    [Fact]
    public void Totals_over_a_short_range_come_from_minute_rows()
    {
        using var t = new TestDatabase();
        SeedThreeHours(t);
        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(4));

        totals.EnergyKwh.ShouldBe(0.12, 1e-9);            // 120 × 0.5 Wh + 60 × 1 Wh
        totals.Cost.ShouldBe(0.024m);
        totals.Currency.ShouldBe("USD");
        totals.CostIsPartial.ShouldBeFalse();
        totals.AvgW.ShouldBe(40, 1e-9);                    // 120 Wh over 3 h on
        totals.PeakW.ShouldBe(60);
        totals.PeakAt.ShouldBe(Fixtures.T0.AddHours(2));
        totals.OnHours.ShouldBe(3, 1e-9);
        totals.IdleOnHours.ShouldBe(1, 1e-9);
        totals.IdleOffHours.ShouldBe(0);
        totals.AsleepHours.ShouldBe(0);                     // no gaps in the seeded ticks
        totals.UnmonitoredHours.ShouldBe(1, 1e-9);          // 4 h range − 3 h on
        totals.IdleOnKwh.ShouldBe(0.06, 1e-9);
        totals.CpuKwh.ShouldBe(0.12 * 0.4, 1e-9);
        totals.DisplayKwh.ShouldBe(4.0 * 3 / 1000, 1e-9);
        totals.MeasuredShare.ShouldBe(2.0 / 3, 1e-9);
        totals.EstimatedShare.ShouldBe(1.0 / 3, 1e-9);
        totals.CalibratedShare.ShouldBe(0);
    }

    [Fact]
    public void Totals_over_a_long_range_come_from_hour_rows()
    {
        using var t = new TestDatabase();
        var agg = new AggregateRepository(t.Db);
        for (var d = 0; d < 5; d++)
            agg.UpsertHour(Downsampler.ToHour(Fixtures.T0.AddDays(d), [Downsampler.ToMinute(Fixtures.T0.AddDays(d), Fixtures.Minute(0, 100))]));
        agg.UpsertMinute(Downsampler.ToMinute(Fixtures.T0, Fixtures.Minute(0, 999)));   // must be ignored

        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddDays(5));
        totals.EnergyKwh.ShouldBe(5 * 100.0 / 60 / 1000, 1e-9);
        totals.Cost.ShouldBe(0m);
        totals.Currency.ShouldBeNull();
    }

    [Fact]
    public void Cost_uses_the_tariff_in_force_when_the_energy_was_used()
    {
        using var t = new TestDatabase();
        SeedThreeHours(t);
        new TariffRepository(t.Db).Add(new Tariff(Fixtures.T0.AddHours(2), 0.50m, "USD"));
        new ReportQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(3)).Cost.ShouldBe(0.06m * 0.20m + 0.06m * 0.50m);
    }

    [Fact]
    public void Daily_buckets_split_by_local_day()
    {
        using var t = new TestDatabase();
        SeedThreeHours(t);                                                       // 12:00–15:00 UTC on 8 Sep
        var plusTen = TimeZoneInfo.CreateCustomTimeZone("plus10", TimeSpan.FromHours(10), "plus10", "plus10");
        var days = new ReportQueries(t.Db).DailyBuckets(Fixtures.T0, Fixtures.T0.AddHours(4), plusTen);
        days.Count.ShouldBe(2);                                                  // 22:00–24:00 on 8 Sep, 00:00–01:00 on 9 Sep local
        days[0].Day.ShouldBe(new DateOnly(2026, 9, 8));
        days[0].EnergyKwh.ShouldBe(0.06, 1e-9);
        days[1].Day.ShouldBe(new DateOnly(2026, 9, 9));
        days[1].EnergyKwh.ShouldBe(0.06, 1e-9);
        days[1].Cost.ShouldBe(0.012m);
        days[1].PeakW.ShouldBe(60);
    }

    [Fact]
    public void An_empty_range_is_all_zeros()
    {
        using var t = new TestDatabase();
        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(1));
        totals.EnergyKwh.ShouldBe(0);
        totals.Cost.ShouldBe(0m);
        totals.PeakAt.ShouldBeNull();
        totals.AvgW.ShouldBe(0);
        totals.MeasuredShare.ShouldBe(0);
        totals.AsleepHours.ShouldBe(0);
        totals.UnmonitoredHours.ShouldBe(1, 1e-9);
    }

    [Fact]
    public void Sleep_shows_up_as_asleep_hours_not_unmonitored()
    {
        using var t = new TestDatabase();
        var readings = Fixtures.Minute(0).Take(30).Append(Fixtures.Reading(30, delta: 1800)).ToList();
        new AggregateRepository(t.Db).UpsertMinute(Downsampler.ToMinute(Fixtures.T0, readings, maxDeltaSeconds: 5));
        // The half hour asleep ended 30 seconds into 12:00, so it began before 12:00: a range from 11:00 holds all of it.
        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0.AddHours(-1), Fixtures.T0.AddHours(1));
        totals.AsleepHours.ShouldBe(0.5, 1e-9);
        totals.OnHours.ShouldBe(30 / 3600.0, 1e-9);
        totals.UnmonitoredHours.ShouldBe(2 - 0.5 - 30 / 3600.0, 1e-9);
    }

    [Fact]
    public void A_short_range_over_purged_minutes_falls_back_to_hour_rows()
    {
        using var t = new TestDatabase();
        new AggregateRepository(t.Db).UpsertHour(Downsampler.ToHour(Fixtures.T0, [Downsampler.ToMinute(Fixtures.T0, Fixtures.Minute(0, 100))]));
        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(1));
        totals.EnergyKwh.ShouldBe(100.0 / 60 / 1000, 1e-9);
    }

    /// <summary>Report finding 7: a range that starts mid hour, 12:17 here, whose minutes of that hour are missing (the PC
    /// slept) must not take that hour's row, which holds 12:00 to 12:16, before the range. In a zone half an hour off UTC
    /// the same put 20 minutes of yesterday into Today. An hour row only stands in for minutes retention has purged.</summary>
    [Fact]
    public void An_hour_row_stands_in_only_for_an_hour_older_than_the_oldest_minute_kept()
    {
        using var t = new TestDatabase();
        var agg = new AggregateRepository(t.Db);
        var early = Enumerable.Range(0, 17).Select(i => Downsampler.ToMinute(Fixtures.T0.AddMinutes(i), Fixtures.Minute(i * 60, 60))).ToList();
        agg.UpsertHour(Downsampler.ToHour(Fixtures.T0, early));
        foreach (var m in early) agg.UpsertMinute(m);
        agg.UpsertMinute(Downsampler.ToMinute(Fixtures.T0.AddHours(1), Fixtures.Minute(3600, 120)));

        // 12:17 to 13:17 holds only 13:00's minute.
        new ReportQueries(t.Db).Totals(Fixtures.T0.AddMinutes(17), Fixtures.T0.AddMinutes(77)).EnergyKwh.ShouldBe(2.0 / 1000, 1e-9);
    }

    /// <summary>Report finding 7: a sleep from 22:00 to 07:00 is 9 hours, but only 7 of them are in a day that starts at
    /// midnight. It is laid back from the wake, as a chart lays it, and clipped to the range.</summary>
    [Fact]
    public void Asleep_hours_are_laid_back_from_the_wake_and_clipped_to_the_range()
    {
        using var t = new TestDatabase();
        var woke = Fixtures.T0.AddHours(-5);
        new AggregateRepository(t.Db).UpsertMinute(Aggregate.Empty(woke) with
        {
            EnergyWh = 0.5, AvgW = 30, MaxW = 30, OnSeconds = 60, MeasuredSeconds = 60, SampleCount = 60, GapSeconds = 9 * 3600,
        });

        var totals = new ReportQueries(t.Db).Totals(woke.AddHours(-7), woke.AddMinutes(30));

        totals.AsleepHours.ShouldBe(7, 1e-9);
        totals.UnmonitoredHours.ShouldBe(7.5 - 7 - 1 / 60.0, 1e-9);
    }

    /// <summary>Accuracy A10: what the PC drew asleep is in the total and its cost, apart from what it drew awake, and only
    /// its part inside the range, laid back from the wake as the asleep hours are.</summary>
    [Fact]
    public void Asleep_energy_counts_in_the_total_and_the_cost_laid_back_and_clipped_like_the_asleep_hours()
    {
        using var t = new TestDatabase();
        var woke = Fixtures.T0.AddHours(-5);                                   // 07:00, asleep since 22:00 at 0.85 W
        new AggregateRepository(t.Db).UpsertMinute(Aggregate.Empty(woke) with
        {
            EnergyWh = 0.5, AvgW = 30, MaxW = 30, OnSeconds = 60, MeasuredSeconds = 60, SampleCount = 60, GapSeconds = 9 * 3600,
            AsleepWh = 9 * 0.85,
        });
        new TariffRepository(t.Db).Add(new Tariff(woke.AddDays(-30), 0.20m, "USD"));

        var totals = new ReportQueries(t.Db).Totals(woke.AddHours(-7), woke.AddMinutes(30));

        totals.AsleepKwh.ShouldBe(7 * 0.85 / 1000, 1e-12);
        totals.EnergyKwh.ShouldBe((0.5 + 7 * 0.85) / 1000, 1e-12);
        totals.Cost.ShouldBe(decimal.Round((decimal)((0.5 + 7 * 0.85) / 1000) * 0.20m, 6));
        totals.AvgW.ShouldBe(30, 1e-9);                                        // awake, as before
        totals.CpuKwh.ShouldBe(0);
    }

    [Fact]
    public void A_night_asleep_is_split_between_the_days_it_spanned_and_the_days_add_up_to_the_total()
    {
        using var t = new TestDatabase();
        var woke = Fixtures.T0.AddHours(-5);                                   // 8 Sep 07:00 UTC, asleep since 7 Sep 22:00
        new AggregateRepository(t.Db).UpsertMinute(Aggregate.Empty(woke) with
        {
            EnergyWh = 0.5, AvgW = 30, MaxW = 30, OnSeconds = 60, MeasuredSeconds = 60, SampleCount = 60, GapSeconds = 9 * 3600,
            AsleepWh = 9 * 0.85,
        });
        new TariffRepository(t.Db).Add(new Tariff(woke.AddDays(-30), 0.20m, "USD"));

        var (totals, days) = new ReportQueries(t.Db).Report(woke.AddHours(-31), woke.AddMinutes(30), TimeZoneInfo.Utc);

        days.Select(d => d.Day).ShouldBe([new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 8)]);
        days[0].AsleepKwh.ShouldBe(2 * 0.85 / 1000, 1e-12);
        days[0].EnergyKwh.ShouldBe(2 * 0.85 / 1000, 1e-12);
        days[0].OnHours.ShouldBe(0);
        days[1].AsleepKwh.ShouldBe(7 * 0.85 / 1000, 1e-12);
        days[1].EnergyKwh.ShouldBe((0.5 + 7 * 0.85) / 1000, 1e-12);
        days.Sum(d => d.EnergyKwh).ShouldBe(totals.EnergyKwh, 1e-12);
        days.Sum(d => d.Cost).ShouldBe(totals.Cost, 0.000002m);

        var series = new ReportQueries(t.Db).Series(woke.AddHours(-31), woke.AddMinutes(30), TimeSpan.FromHours(1));
        series.Sum(b => b.AsleepWh).ShouldBe(9 * 0.85, 1e-9);
        series.Where(b => b.AsleepWh > 0).ShouldAllBe(b => Math.Abs(b.AsleepWh - 0.85) < 1e-9);
    }

    /// <summary>Report finding 19: a long range reads hour rows, whose start is no time for a peak; while the hour's minutes
    /// are kept, the peak is the minute it was in.</summary>
    [Fact]
    public void A_peak_in_an_hour_row_is_timed_by_its_minute()
    {
        using var t = new TestDatabase();
        var agg = new AggregateRepository(t.Db);
        var hour = Enumerable.Range(0, 60).Select(i => Downsampler.ToMinute(Fixtures.T0.AddMinutes(i), Fixtures.Minute(i * 60, i == 37 ? 90 : 30))).ToList();
        agg.UpsertHour(Downsampler.ToHour(Fixtures.T0, hour));
        foreach (var m in hour) agg.UpsertMinute(m);

        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0.AddDays(-4), Fixtures.T0.AddHours(1));

        (totals.PeakW, totals.PeakAt).ShouldBe((90.0, (DateTimeOffset?)Fixtures.T0.AddMinutes(37)));
    }

    /// <summary>Once retention has purged the hour's minutes, nothing says when in the hour the peak was, so no time is given.</summary>
    [Fact]
    public void A_peak_in_an_hour_whose_minutes_are_purged_has_no_time()
    {
        using var t = new TestDatabase();
        var hour = Enumerable.Range(0, 60).Select(i => Downsampler.ToMinute(Fixtures.T0.AddMinutes(i), Fixtures.Minute(i * 60, i == 37 ? 90 : 30))).ToList();
        new AggregateRepository(t.Db).UpsertHour(Downsampler.ToHour(Fixtures.T0, hour));

        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0.AddDays(-4), Fixtures.T0.AddHours(1));

        (totals.PeakW, totals.PeakAt).ShouldBe((90.0, (DateTimeOffset?)null));
    }

    /// <summary>Report finding 7: a long range reads whole hour rows, so its window runs on to the next whole hour; the time
    /// past the range's end has not happened, and is not unmonitored.</summary>
    [Fact]
    public void Unmonitored_time_ends_at_the_range_s_end_not_the_next_whole_hour()
    {
        using var t = new TestDatabase();
        var start = Fixtures.T0.AddDays(-4);
        new AggregateRepository(t.Db).UpsertHour(Downsampler.ToHour(start, [Downsampler.ToMinute(start, Fixtures.Minute(0, 60))]));

        var totals = new ReportQueries(t.Db).Totals(start, Fixtures.T0.AddMinutes(10));

        totals.OnHours.ShouldBe(1 / 60.0, 1e-9);
        totals.UnmonitoredHours.ShouldBe(4 * 24 + 10 / 60.0 - 1 / 60.0, 1e-9);
    }

    [Fact]
    public void Hour_rows_cover_the_bulk_and_minute_rows_fill_the_unfolded_tail()
    {
        using var t = new TestDatabase();
        var agg = new AggregateRepository(t.Db);
        var firstHour = Enumerable.Range(0, 60).Select(i => Downsampler.ToMinute(Fixtures.T0.AddMinutes(i), Fixtures.Minute(i * 60, 60))).ToList();
        agg.UpsertHour(Downsampler.ToHour(Fixtures.T0, firstHour));
        foreach (var m in firstHour) agg.UpsertMinute(m);                                              // same data, must not be counted twice
        agg.UpsertMinute(Downsampler.ToMinute(Fixtures.T0.AddHours(1), Fixtures.Minute(3600, 120)));   // the hour the job has not folded yet

        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(2));
        totals.EnergyKwh.ShouldBe((60.0 + 2.0) / 1000, 1e-9);
        totals.PeakW.ShouldBe(120);
    }

    [Fact]
    public void A_currency_change_flags_partial_cost_in_totals_and_days_and_days_conserve_energy()
    {
        using var t = new TestDatabase();
        SeedThreeHours(t);
        new TariffRepository(t.Db).Add(new Tariff(Fixtures.T0.AddHours(2), 0.50m, "EUR"));
        var (totals, days) = new ReportQueries(t.Db).Report(Fixtures.T0, Fixtures.T0.AddHours(4), TimeZoneInfo.Utc);

        totals.Currency.ShouldBe("EUR");
        totals.CostIsPartial.ShouldBeTrue();
        totals.Cost.ShouldBe(0.03m);                       // only the last hour is priced in EUR
        days.Count.ShouldBe(1);
        days[0].Currency.ShouldBe("EUR");
        days[0].CostIsPartial.ShouldBeTrue();
        days.Sum(d => d.EnergyKwh).ShouldBe(totals.EnergyKwh, 1e-9);
        new ReportQueries(t.Db).DailyBuckets(Fixtures.T0.AddDays(-5), Fixtures.T0.AddDays(-4), TimeZoneInfo.Utc).ShouldBeEmpty();
    }

    [Fact]
    public void A_short_range_reads_minutes_even_inside_a_folded_hour()
    {
        using var t = new TestDatabase();
        var agg = new AggregateRepository(t.Db);
        var hour = Enumerable.Range(0, 60).Select(i => Downsampler.ToMinute(Fixtures.T0.AddMinutes(i), Fixtures.Minute(i * 60, 60))).ToList();
        agg.UpsertHour(Downsampler.ToHour(Fixtures.T0, hour));
        foreach (var m in hour) agg.UpsertMinute(m);

        // Half the hour is in the range. Its minutes say so, where the hour row would have counted all of it.
        new ReportQueries(t.Db).Totals(Fixtures.T0.AddMinutes(30), Fixtures.T0.AddHours(1)).EnergyKwh.ShouldBe(30.0 / 1000, 1e-9);
    }

    [Fact]
    public void A_series_cuts_the_range_into_buckets_that_add_up_to_its_totals()
    {
        using var t = new TestDatabase();
        SeedThreeHours(t);
        var queries = new ReportQueries(t.Db);

        var series = queries.Series(Fixtures.T0, Fixtures.T0.AddHours(4), TimeSpan.FromHours(1));

        series.Select(b => b.Start).ShouldBe(Enumerable.Range(0, 4).Select(h => Fixtures.T0.AddHours(h)));
        series[0].EnergyWh.ShouldBe(30, 1e-9);                     // sixty minutes at 30 W
        series[0].AvgW.ShouldBe(30, 1e-9);
        series[2].EnergyWh.ShouldBe(60, 1e-9);                     // sixty minutes at 60 W
        series[3].EnergyWh.ShouldBe(0);
        series.Sum(b => b.EnergyWh).ShouldBe(queries.Totals(Fixtures.T0, Fixtures.T0.AddHours(4)).EnergyKwh * 1000, 1e-9);
    }

    [Fact]
    public void A_long_series_puts_each_hour_row_in_the_bucket_where_it_starts()
    {
        using var t = new TestDatabase();
        var agg = new AggregateRepository(t.Db);
        for (var d = 0; d < 5; d++)
            agg.UpsertHour(Downsampler.ToHour(Fixtures.T0.AddDays(d), [Downsampler.ToMinute(Fixtures.T0.AddDays(d), Fixtures.Minute(0, 100))]));

        var series = new ReportQueries(t.Db).Series(Fixtures.T0, Fixtures.T0.AddDays(5), TimeSpan.FromDays(1));
        series.Count.ShouldBe(5);
        series.ShouldAllBe(b => Math.Abs(b.EnergyWh - 100.0 / 60) < 1e-9);
    }

    [Fact]
    public void A_sleep_is_laid_back_over_the_buckets_it_covered()
    {
        // Two hours asleep, stored in the minute starting at 14:00 with thirty seconds on after the wake:
        // asleep from 12:00:30 to 14:00:30.
        using var t = new TestDatabase();
        new AggregateRepository(t.Db).UpsertMinute(Aggregate.Empty(Fixtures.T0.AddHours(2)) with { OnSeconds = 30, GapSeconds = 7200, SampleCount = 30 });

        var series = new ReportQueries(t.Db).Series(Fixtures.T0, Fixtures.T0.AddMinutes(125), TimeSpan.FromMinutes(5));
        series.Count.ShouldBe(25);
        series[0].GapSeconds.ShouldBe(270, 1e-9);
        series.Skip(1).Take(23).ShouldAllBe(b => Math.Abs(b.GapSeconds - 300) < 1e-9);
        series[24].GapSeconds.ShouldBe(30, 1e-9);
        series[24].OnSeconds.ShouldBe(30);
    }

    [Fact]
    public void A_sleep_that_began_before_the_range_is_cut_at_its_start()
    {
        using var t = new TestDatabase();
        new AggregateRepository(t.Db).UpsertMinute(Aggregate.Empty(Fixtures.T0.AddMinutes(10)) with { OnSeconds = 60, GapSeconds = 3600, SampleCount = 60 });

        var series = new ReportQueries(t.Db).Series(Fixtures.T0, Fixtures.T0.AddMinutes(15), TimeSpan.FromMinutes(5));
        series.Sum(b => b.GapSeconds).ShouldBe(600, 1e-9);        // 12:00 to 12:10
    }

    /// <summary>Review round: day buckets given a zone start at each local midnight, so across a clock change each
    /// calendar day is still one bucket (London's Sunday 25 October 2026 is 25 hours long), a row late on a day stays in
    /// it, and a sleep over midnight is laid back either side of that midnight. Without a zone, buckets stay a fixed
    /// length from the start, as every shorter bucket does.</summary>
    [Fact]
    public void Day_buckets_in_a_zone_start_at_each_local_midnight_across_a_clock_change()
    {
        var london = TimeZoneInfo.TryFindSystemTimeZoneById("GMT Standard Time", out var windows) ? windows : TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var from = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.FromHours(1));   // Friday's midnight, summer time
        var to = new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);             // Wednesday's, winter time
        using var t = new TestDatabase();
        var hours = new AggregateRepository(t.Db);
        hours.UpsertHour(Aggregate.Empty(new DateTimeOffset(2026, 10, 23, 11, 0, 0, TimeSpan.Zero)) with { EnergyWh = 10, OnSeconds = 3600 });
        hours.UpsertHour(Aggregate.Empty(new DateTimeOffset(2026, 10, 26, 23, 0, 0, TimeSpan.Zero)) with { EnergyWh = 20, OnSeconds = 3600 });   // Monday, 23:00
        hours.UpsertHour(Aggregate.Empty(new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero)) with { EnergyWh = 40, OnSeconds = 3600 });    // Tuesday, 00:00
        // Asleep Monday 22:00 to Tuesday 02:00, stored in the hour it woke, on for that whole hour.
        hours.UpsertHour(Aggregate.Empty(new DateTimeOffset(2026, 10, 27, 2, 0, 0, TimeSpan.Zero)) with { OnSeconds = 3600, GapSeconds = 4 * 3600 });
        var queries = new ReportQueries(t.Db);

        var series = queries.Series(from, to, TimeSpan.FromDays(1), london);

        series.Select(b => TimeZoneInfo.ConvertTime(b.Start, london).DateTime).ShouldBe(
            [new DateTime(2026, 10, 23), new DateTime(2026, 10, 24), new DateTime(2026, 10, 25), new DateTime(2026, 10, 26), new DateTime(2026, 10, 27)]);
        (series[3].Start - series[2].Start).ShouldBe(TimeSpan.FromHours(25));
        series.Select(b => b.EnergyWh).ShouldBe([10, 0, 0, 20, 40]);
        series.Select(b => b.GapSeconds).ShouldBe([0, 0, 0, 7200, 7200]);

        queries.Series(from, to, TimeSpan.FromDays(1)).Select(b => b.Start).ShouldBe(Enumerable.Range(0, 6).Select(i => from.AddDays(i)));
        queries.Series(from, to, TimeSpan.FromHours(6), london).Select(b => b.Start)
            .ShouldBe(Enumerable.Range(0, 21).Select(i => from.AddHours(6 * i)));
    }

    [Fact]
    public void The_first_minute_row_says_where_history_begins()
    {
        using var t = new TestDatabase();
        var repository = new AggregateRepository(t.Db);
        repository.FirstMinuteStart().ShouldBeNull();
        SeedThreeHours(t);
        repository.FirstMinuteStart().ShouldBe(Fixtures.T0);
    }
}
