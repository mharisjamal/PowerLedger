using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class TariffScheduleTests
{
    private static readonly DateTimeOffset Jan = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jun = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TariffSchedule Schedule = new([
        new Tariff(Jun, 0.20m, "USD"),
        new Tariff(Jan, 0.17m, "USD"),
    ]);

    [Fact]
    public void At_picks_the_latest_tariff_effective_on_or_before_the_instant()
    {
        Schedule.At(Jun.AddDays(10))!.PricePerKwh.ShouldBe(0.20m);
        Schedule.At(Jun)!.PricePerKwh.ShouldBe(0.20m);
        Schedule.At(Jun.AddSeconds(-1))!.PricePerKwh.ShouldBe(0.17m);
    }

    [Fact]
    public void Energy_before_the_first_tariff_uses_the_first_tariff()
        => Schedule.At(Jan.AddYears(-1))!.PricePerKwh.ShouldBe(0.17m);

    [Fact]
    public void An_empty_schedule_has_no_tariff_and_zero_cost()
    {
        var empty = new TariffSchedule([]);
        empty.At(Jun).ShouldBeNull();
        empty.Cost([(Jun, 1000)]).ShouldBe(new CostResult(0m, null, false));
        empty.Currency.ShouldBeNull();
        empty.HasMixedCurrencies.ShouldBeFalse();
    }

    [Fact]
    public void Cost_applies_the_rate_in_force_for_each_slice()
    {
        var cost = Schedule.Cost([(Jan.AddDays(3), 500), (Jun.AddDays(3), 500)]);
        cost.Amount.ShouldBe(0.5m * 0.17m + 0.5m * 0.20m);
        cost.Currency.ShouldBe("USD");
        cost.Partial.ShouldBeFalse();
        Schedule.Currency.ShouldBe("USD");
    }

    [Fact]
    public void Cost_is_exact_decimal_arithmetic()
        => Schedule.Cost([(Jan, 1234)]).Amount.ShouldBe(0.20978m);

    [Fact]
    public void A_currency_change_starts_a_new_cost_history()
    {
        var moved = new TariffSchedule([new Tariff(Jan, 0.30m, "GBP"), new Tariff(Jun, 0.20m, "EUR")]);
        var cost = moved.Cost([(Jan.AddDays(3), 500), (Jun.AddDays(3), 500)]);
        cost.ShouldBe(new CostResult(0.5m * 0.20m, "EUR", Partial: true));
        moved.HasMixedCurrencies.ShouldBeTrue();
        moved.Cost([(Jun.AddDays(3), 500)]).Partial.ShouldBeFalse();
    }

    [Fact]
    public void Same_instant_tariffs_keep_input_order_so_the_later_one_wins()
    {
        var schedule = new TariffSchedule([new Tariff(Jan, 0.10m, "USD"), new Tariff(Jan, 0.11m, "USD")]);
        schedule.At(Jan)!.PricePerKwh.ShouldBe(0.11m);
    }

    [Fact]
    public void Non_finite_energy_is_ignored()
        => Schedule.Cost([(Jan, double.NaN), (Jan, double.PositiveInfinity), (Jan, 1000)]).Amount.ShouldBe(0.17m);

    [Fact]
    public void Co2_and_comparisons()
    {
        Co2.Kg(0.284, 0.38).ShouldBe(0.10792, 1e-9);
        Co2.DefaultKgPerKwh.ShouldBe(0.40);
        Comparisons.LedBulbHours(1.0).ShouldBe(100, 1e-9);
        Comparisons.PhoneCharges(1.0).ShouldBe(1000.0 / 15, 1e-9);
        Comparisons.EvKm(1.0).ShouldBe(1 / 0.18, 1e-9);
    }

    [Fact]
    public void Energy_carrying_floating_point_residue_still_costs_an_exact_amount()
    {
        // 60 ticks of 30 W integrated one second at a time land just off 0.5 Wh; money must not inherit that.
        var minuteWh = Enumerable.Range(0, 60).Aggregate(0.0, (wh, _) => wh + 30 * (1.0 / 3600));
        var noisy = Enumerable.Range(0, 120).Select(_ => (Jan.AddDays(3), minuteWh)).ToList();
        minuteWh.ShouldNotBe(0.5);
        Schedule.Cost(noisy).Amount.ShouldBe(0.0102m);
    }
}
