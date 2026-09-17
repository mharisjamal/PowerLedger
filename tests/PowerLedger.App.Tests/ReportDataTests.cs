using System.Globalization;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ReportDataTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly SleepTimeouts Timeouts = new(TimeSpan.FromHours(3), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5));

    private static ReportData Month(Func<RangeReport, RangeReport>? change = null)
    {
        var report = Reports.Typical(Ranges.ThisMonth(Now, Utc, English));
        return ReportData.From(change is null ? report : change(report), Timeouts, 0.38, Utc, English);
    }

    [Fact]
    public void A_month_so_far_reads_as_a_bill()
    {
        var data = Month();
        data.Title.ShouldBe("September 2026");
        data.Period.ShouldBe("1 Sep – 8 Sep 2026");
        data.HasData.ShouldBeTrue();
        data.Energy.ShouldBe("2.74");
        data.Cost.ShouldBe("$0.47");
        data.CostNote.ShouldBe("$0.17 / kWh");
        data.Co2.ShouldBe("1.04 kg");                                   // 2.74 kWh at 0.38 kg / kWh
        data.Co2Note.ShouldBe("at 0.38 kg / kWh");
        data.Average.ShouldBe("41");
        data.Peak.ShouldBe("68");
        data.PeakAt.ShouldBe("at 14:00, Tue 1 Sep");
        data.On.ShouldBe("60h 00m");
        data.Asleep.ShouldBe("50h 00m");
        data.Unmonitored.ShouldBe("5h 00m");
    }

    [Fact]
    public void Idle_waste_is_priced_and_the_advice_quotes_windows()
    {
        var data = Month();
        data.IdleWaste.ShouldBe("0.384 kWh");                          // 14% of 2.74 kWh
        data.IdleWasteNote.ShouldBe("≈ $0.07 · 14% of the energy");
        data.Advice.ShouldBe("Windows sleeps after 3 h idle when plugged in. Sleeping after 30 minutes would cut much of it.");
        data.IdleOn.ShouldBe("6h 00m");
        data.IdleOff.ShouldBe("2h 00m");
    }

    [Fact]
    public void Parts_equivalents_and_quality_are_written_out()
    {
        var data = Month();
        data.Parts.Select(p => p.Name).ShouldBe(new[] { "CPU package", "GPU", "Display", "Rest of system" });
        data.Parts[0].Energy.ShouldBe("1.18");
        data.Parts[0].Share.ShouldBe("43%");
        data.Parts[0].Fraction.ShouldBe(0.43, 1e-9);
        data.Equivalents.Select(e => e.Value).ShouldBe(new[] { "274 hours", "183", "15 km" });
        data.QualityText.ShouldBe("62% measured · 20% calibrated · 18% estimated");
        data.Quality.ShouldBe(new QualityMix(0.62, 0.2, 0.18));
    }

    [Fact]
    public void The_quality_legend_says_what_each_quality_is_and_where_the_monitors_watts_come_from()
    {
        // The Report screen and the PDF both show it. A monitor's watts come from its own figure whatever the quality, but
        // aren't always added: one running off the laptop is already in the battery's report.
        ReportData.QualityLegend.ShouldBe(
            "Measured: Windows' battery report. Calibrated: a model with a baseline learned on battery, ±10%. Estimated: the model alone, ±20%. "
            + "UPS and power supply readings count as measured. External monitors' watts come from their own figures in every mode.");
    }

    [Fact]
    public void A_tariff_that_began_inside_the_range_says_from_when()
    {
        var range = Ranges.ThisMonth(Now, Utc, English);
        var data = Month(r => r with { Tariff = new Tariff(new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero), 0.25m, "USD") });
        data.CostNote.ShouldBe("$0.25 / kWh from 5 Sep");
    }

    [Fact]
    public void Daily_bars_cover_every_day_of_the_month()
    {
        var data = Month();
        data.Days.Count.ShouldBe(30);
        data.Days[0].Day.ShouldBe(new DateOnly(2026, 9, 1));
        data.Days.Take(8).ShouldAllBe(d => d.Kwh > 0);
        data.Days.Skip(8).ShouldAllBe(d => d.Kwh == 0);
    }

    [Fact]
    public void Without_a_tariff_or_with_a_currency_change_the_cost_says_so()
    {
        var untariffed = Month(r => r with { Totals = r.Totals with { Currency = null, Cost = 0 } });
        untariffed.Cost.ShouldBe("–");
        untariffed.CostNote.ShouldBe("no tariff set");
        untariffed.IdleWasteNote.ShouldBe("14% of the energy");
        Month(r => r with { Totals = r.Totals with { CostIsPartial = true } }).CostNote
            .ShouldBe("partial: energy priced in an earlier currency is left out");
    }

    [Fact]
    public void A_day_reads_its_date_and_an_empty_range_has_no_data()
    {
        var today = ReportData.From(Reports.Typical(Ranges.Today(Now, Utc, English)), Timeouts, 0.38, Utc, English);
        today.Period.ShouldBe("Tue 8 Sep 2026");
        today.PeakAt.ShouldBe("at 14:00");
        today.Days.Count.ShouldBe(1);

        var empty = ReportData.From(Reports.Empty(Ranges.LastMonth(Now, Utc, English)), Timeouts, 0.38, Utc, English);
        empty.HasData.ShouldBeFalse();
        empty.Period.ShouldBe("1 Aug – 31 Aug 2026");
        empty.QualityText.ShouldBe("no readings");
    }
}
