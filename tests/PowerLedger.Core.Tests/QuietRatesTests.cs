using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

/// <summary>Plan AF: what each app does at rest, learned from the quiet windows, so the power above rest goes to the work
/// above rest.</summary>
public class QuietRatesTests
{
    private static readonly (int, string) Chrome = (7, @"c:\chrome.exe");
    private static readonly (int, string) Drivers = (0, "::drivers");

    private static Dictionary<(int User, string Key), double> Work(params ((int, string) Key, double Amount)[] work)
        => work.ToDictionary(w => w.Key, w => w.Amount);

    [Fact]
    public void A_rate_is_the_mean_of_its_first_quiet_windows_then_moves_slowly()
    {
        var rates = new QuietRates();
        rates.RateOf(Chrome).ShouldBe(0);
        rates.Learn(Work((Chrome, 500)), seconds: 5);                // 100 a second
        rates.RateOf(Chrome).ShouldBe(100, 1e-9);
        rates.Learn(Work((Chrome, 1500)), seconds: 5);               // 300 a second: the mean of the two
        rates.RateOf(Chrome).ShouldBe(200, 1e-9);

        for (var i = 0; i < 2000; i++) rates.Learn(Work((Chrome, 250)), seconds: 5);
        rates.RateOf(Chrome).ShouldBe(50, 1);                      // hours of quiet at 50 a second
    }

    [Fact]
    public void An_app_absent_from_a_quiet_window_did_none_of_its_work_then()
    {
        var rates = new QuietRates();
        rates.Learn(Work((Chrome, 500), (Drivers, 50)), seconds: 5);
        rates.Learn(Work((Drivers, 50)), seconds: 5);
        rates.RateOf(Chrome).ShouldBe(50, 1e-9);
        rates.RateOf(Drivers).ShouldBe(10, 1e-9);
    }

    [Fact]
    public void Work_above_a_rate_is_what_exceeds_it_over_the_window_never_below_nought()
    {
        QuietRates.Excess(amount: 1000, rate: 100, seconds: 5).ShouldBe(500);
        QuietRates.Excess(amount: 300, rate: 100, seconds: 5).ShouldBe(0);
        QuietRates.Excess(amount: 300, rate: 0, seconds: 5).ShouldBe(300);
    }

    [Fact]
    public void Past_the_most_keys_the_least_busy_are_forgotten()
    {
        var rates = new QuietRates();
        for (var i = 0; i < QuietRates.MostKeys + 10; i++) rates.Learn(Work(((0, $"app{i}"), i + 1)), seconds: 1);
        rates.Count.ShouldBeLessThanOrEqualTo(QuietRates.MostKeys);
        rates.RateOf((0, $"app{QuietRates.MostKeys + 9}")).ShouldBeGreaterThan(0);
    }
}
