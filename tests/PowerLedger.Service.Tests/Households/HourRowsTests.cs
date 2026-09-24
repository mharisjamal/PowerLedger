using PowerLedger.Core;
using PowerLedger.Service.Households;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>This PC's hour rows for the household, built from samples_1h with the cost at each hour's tariff (households
/// design §1).</summary>
public sealed class HourRowsTests : IDisposable
{
    private const string Me = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Midnight = new(2026, 9, 24, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();

    private AggregateRepository Aggregates => new(_database.Db);

    private TariffRepository Tariffs => new(_database.Db);

    private HouseholdRepository Household => new(_database.Db);

    private HourRows Rows => new(Aggregates, Tariffs, Household);

    [Fact]
    public void Each_hour_row_carries_the_figures_of_its_hour_in_samples_1h()
    {
        Aggregates.UpsertHour(Hour(0, energyWh: 42.5));
        Aggregates.UpsertHour(Hour(1, energyWh: 17.25));

        Rows.Build(Me, Midnight.AddDays(-1), Midnight.AddHours(3)).ShouldBe(2);

        var row = Household.Row(Me, Midnight.ToUnixTimeMilliseconds()).ShouldNotBeNull();
        row.ShouldBe(new HouseholdRow(
            Me, Midnight.ToUnixTimeMilliseconds(), EnergyWh: 42.5, CpuWh: 12, GpuWh: 8, DisplayWh: 5, RestWh: 17.5, IdleOnWh: 3, IdleOffWh: 1,
            OnS: 3500, BatteryS: 900, IdleS: 700, MeasuredS: 2000, CalibratedS: 1000, EstimatedS: 500, CostMicro: null, Currency: null,
            ChangedMs: Midnight.AddHours(3).ToUnixTimeMilliseconds()));
        Household.Row(Me, Midnight.AddHours(1).ToUnixTimeMilliseconds()).ShouldNotBeNull().EnergyWh.ShouldBe(17.25);
    }

    [Fact]
    public void The_cost_is_at_the_tariff_in_force_at_each_hour_across_a_change_of_price_and_currency()
    {
        Tariffs.Add(new Tariff(Midnight.AddDays(-30), 0.25m, "GBP"));
        Tariffs.Add(new Tariff(Midnight.AddHours(1), 0.30m, "GBP"));
        Tariffs.Add(new Tariff(Midnight.AddHours(2).AddMinutes(30), 0.40m, "EUR"));
        Aggregates.UpsertHour(Hour(0, energyWh: 1000));
        Aggregates.UpsertHour(Hour(1, energyWh: 500));
        Aggregates.UpsertHour(Hour(2, energyWh: 123.4567));
        Aggregates.UpsertHour(Hour(3, energyWh: 10));

        Rows.Build(Me, Midnight.AddDays(-1), Midnight.AddHours(5));

        Cost(0).ShouldBe((250_000L, "GBP"));                        // 1 kWh at 0.25
        Cost(1).ShouldBe((150_000L, "GBP"));                        // 0.5 kWh at the new 0.30
        Cost(2).ShouldBe((37_037L, "GBP"));                         // the change mid-hour counts from the next hour
        Cost(3).ShouldBe((4_000L, "EUR"));
    }

    [Fact]
    public void A_row_changes_only_when_its_figures_do()
    {
        Aggregates.UpsertHour(Hour(0, energyWh: 10));
        Aggregates.UpsertHour(Hour(1, energyWh: 20));
        Rows.Build(Me, Midnight.AddDays(-1), Midnight.AddHours(2)).ShouldBe(2);

        Rows.Build(Me, Midnight.AddDays(-1), Midnight.AddHours(3)).ShouldBe(0);
        Changed(0).ShouldBe(Midnight.AddHours(2));
        Changed(1).ShouldBe(Midnight.AddHours(2));

        Aggregates.UpsertHour(Hour(1, energyWh: 21));                // a restart folded the hour again
        Rows.Build(Me, Midnight.AddDays(-1), Midnight.AddHours(4)).ShouldBe(1);
        Changed(0).ShouldBe(Midnight.AddHours(2));
        Changed(1).ShouldBe(Midnight.AddHours(4));

        Tariffs.Add(new Tariff(Midnight.AddDays(-2), 0.20m, "USD"));   // a tariff entered later prices both hours
        Rows.Build(Me, Midnight.AddDays(-1), Midnight.AddHours(5)).ShouldBe(2);
        Changed(0).ShouldBe(Midnight.AddHours(5));
        Cost(0).ShouldBe((2_000L, "USD"));
    }

    [Fact]
    public void A_clock_set_back_still_marks_a_changed_row_as_newer()
    {
        Aggregates.UpsertHour(Hour(0, energyWh: 10));
        Rows.Build(Me, Midnight.AddDays(-1), Midnight.AddHours(5));
        Aggregates.UpsertHour(Hour(0, energyWh: 11));

        Rows.Build(Me, Midnight.AddDays(-1), Midnight.AddHours(2)).ShouldBe(1);

        Household.Row(Me, Midnight.ToUnixTimeMilliseconds()).ShouldNotBeNull().EnergyWh.ShouldBe(11);
        Changed(0).ShouldBe(Midnight.AddHours(5).AddMilliseconds(1));
    }

    [Fact]
    public void After_the_clock_goes_back_a_new_hour_is_still_newer_than_every_row_kept()
    {
        Aggregates.UpsertHour(Hour(0, energyWh: 10));
        Rows.Build(Me, Midnight.AddDays(-1), Midnight.AddHours(5));             // built while the clock was 3 h fast
        Aggregates.UpsertHour(Hour(1, energyWh: 20));

        Rows.Build(Me, Midnight.AddDays(-1), Midnight.AddHours(2)).ShouldBe(1);  // the clock put right

        Changed(1).ShouldBe(Midnight.AddHours(5).AddMilliseconds(1));
    }

    [Fact]
    public void Rows_changed_more_than_a_day_ahead_go_back_to_now_once_and_changes_carry_on_after_them()
    {
        var now = Midnight.AddHours(5);
        Aggregates.UpsertHour(Hour(0, energyWh: 10));
        Aggregates.UpsertHour(Hour(1, energyWh: 20));
        Rows.Build(Me, Midnight.AddDays(-1), now.AddYears(1));                  // built while the clock was a year fast

        Rows.Rebase(Me, now).ShouldBeTrue();

        (Changed(0), Changed(1)).ShouldBe((now, now));
        Rows.Rebase(Me, now.AddMinutes(1)).ShouldBeFalse();                      // once
        Aggregates.UpsertHour(Hour(0, energyWh: 11));
        Rows.Build(Me, Midnight.AddDays(-1), now).ShouldBe(1);
        Changed(0).ShouldBe(now.AddMilliseconds(1));                              // after the newest, as ever
    }

    [Fact]
    public void Rows_changed_less_than_a_day_ahead_stay_as_they_are()
    {
        var now = Midnight.AddHours(5);
        Aggregates.UpsertHour(Hour(0, energyWh: 10));
        Rows.Build(Me, Midnight.AddDays(-1), now.AddHours(23));

        Rows.Rebase(Me, now).ShouldBeFalse();

        Changed(0).ShouldBe(now.AddHours(23));
    }

    [Fact]
    public void Only_the_hours_from_the_start_are_built_and_the_backfill_reaches_back_13_months()
    {
        var now = Midnight.AddHours(5);
        Aggregates.UpsertHour(Hour(-24 * 400, energyWh: 1));
        Aggregates.UpsertHour(Hour(-24 * 390, energyWh: 2));
        Aggregates.UpsertHour(Hour(0, energyWh: 3));

        HourRows.BackfillFrom(now).ShouldBe(now.AddMonths(-13));
        Rows.Build(Me, HourRows.BackfillFrom(now), now).ShouldBe(2);

        Household.RowsBetween(Me, 0, now.ToUnixTimeMilliseconds()).Select(row => row.EnergyWh).ShouldBe([2.0, 3.0]);
    }

    public void Dispose() => _database.Dispose();

    private (long?, string?) Cost(int hour)
    {
        var row = Household.Row(Me, Midnight.AddHours(hour).ToUnixTimeMilliseconds()).ShouldNotBeNull();
        return (row.CostMicro, row.Currency);
    }

    private DateTimeOffset Changed(int hour) =>
        DateTimeOffset.FromUnixTimeMilliseconds(Household.Row(Me, Midnight.AddHours(hour).ToUnixTimeMilliseconds()).ShouldNotBeNull().ChangedMs);

    private static Aggregate Hour(int hour, double energyWh) => new(
        Midnight.AddHours(hour), AvgW: energyWh, MaxW: energyWh * 2,
        EnergyWh: energyWh, CpuWh: 12, GpuWh: 8, DisplayWh: 5, RestWh: 17.5, IdleOnWh: 3, IdleOffWh: 1,
        IdleOnSeconds: 400, IdleOffSeconds: 300, OnSeconds: 3500, BatterySeconds: 900, GapSeconds: 100, SampleCount: 3500,
        MeasuredSeconds: 2000, CalibratedSeconds: 1000, EstimatedSeconds: 500);
}
