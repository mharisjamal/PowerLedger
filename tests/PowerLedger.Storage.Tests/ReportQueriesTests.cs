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
        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(1));
        totals.AsleepHours.ShouldBe(0.5, 1e-9);
        totals.OnHours.ShouldBe(30 / 3600.0, 1e-9);
        totals.UnmonitoredHours.ShouldBe(1 - 0.5 - 30 / 3600.0, 1e-9);
    }

    [Fact]
    public void A_short_range_over_purged_minutes_falls_back_to_hour_rows()
    {
        using var t = new TestDatabase();
        new AggregateRepository(t.Db).UpsertHour(Downsampler.ToHour(Fixtures.T0, [Downsampler.ToMinute(Fixtures.T0, Fixtures.Minute(0, 100))]));
        var totals = new ReportQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(1));
        totals.EnergyKwh.ShouldBe(100.0 / 60 / 1000, 1e-9);
    }

    [Fact]
    public void Crossing_the_resolution_limit_switches_source_tables()
    {
        using var t = new TestDatabase();
        var agg = new AggregateRepository(t.Db);
        agg.UpsertMinute(Downsampler.ToMinute(Fixtures.T0, Fixtures.Minute(0, 30)));
        agg.UpsertHour(Downsampler.ToHour(Fixtures.T0, [Downsampler.ToMinute(Fixtures.T0, Fixtures.Minute(0, 90))]));
        var queries = new ReportQueries(t.Db);
        queries.Totals(Fixtures.T0, Fixtures.T0 + ReportQueries.MinuteResolutionLimit).EnergyKwh.ShouldBe(30.0 / 60 / 1000, 1e-9);
        queries.Totals(Fixtures.T0, Fixtures.T0 + ReportQueries.MinuteResolutionLimit + TimeSpan.FromMinutes(1)).EnergyKwh.ShouldBe(90.0 / 60 / 1000, 1e-9);
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
}
