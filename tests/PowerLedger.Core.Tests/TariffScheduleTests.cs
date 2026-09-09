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
        empty.Cost([(Jun, 1000)]).ShouldBe(0m);
        empty.Currency.ShouldBeNull();
    }

    [Fact]
    public void Cost_applies_the_rate_in_force_for_each_slice()
    {
        var cost = Schedule.Cost([(Jan.AddDays(3), 500), (Jun.AddDays(3), 500)]);
        cost.ShouldBe(0.5m * 0.17m + 0.5m * 0.20m);
        Schedule.Currency.ShouldBe("USD");
    }

    [Fact]
    public void Cost_is_exact_decimal_arithmetic()
        => Schedule.Cost([(Jan, 1234)]).ShouldBe(0.20978m);

    [Fact]
    public void Co2_and_comparisons()
    {
        Co2.Kg(0.284, 0.38).ShouldBe(0.10792, 1e-9);
        Co2.DefaultKgPerKwh.ShouldBe(0.40);
        Comparisons.LedBulbHours(1.0).ShouldBe(100, 1e-9);
        Comparisons.PhoneCharges(1.0).ShouldBe(1000.0 / 15, 1e-9);
        Comparisons.EvKm(1.0).ShouldBe(1 / 0.18, 1e-9);
    }
}
