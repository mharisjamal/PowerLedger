using System.Globalization;
using System.Text;
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ReportDocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static ReportData Data(DateRange range, CultureInfo culture)
        => ReportData.From(Reports.Typical(range), SleepTimeouts.Unknown, 0.38, TimeZoneInfo.Utc, culture);

    [Fact]
    public void A_report_becomes_a_pdf()
    {
        var pdf = ReportDocument.Generate(Data(Ranges.ThisMonth(Now, TimeZoneInfo.Utc, English), English), "0.1.0", Now, English);

        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
        Encoding.ASCII.GetString(pdf, pdf.Length - 8, 8).ShouldContain("%%EOF");
        pdf.Length.ShouldBeGreaterThan(5_000);
    }

    [Fact]
    public void A_report_in_hindi_and_rupees_with_no_readings_still_draws()
    {
        var hindi = CultureInfo.GetCultureInfo("hi-IN");
        var empty = Reports.Empty(Ranges.LastMonth(Now, TimeZoneInfo.Utc, hindi));
        var data = ReportData.From(empty with { Totals = empty.Totals with { Currency = "INR" } }, SleepTimeouts.Unknown, 0.71, TimeZoneInfo.Utc, hindi);

        ReportDocument.Generate(data, "0.1.0", Now, hindi).Length.ShouldBeGreaterThan(1_000);
    }

    [Fact]
    public void A_year_of_bars_fits()
    {
        var range = Ranges.Days(new DateOnly(2025, 9, 1), new DateOnly(2026, 8, 31), Now, TimeZoneInfo.Utc, English);
        ReportDocument.Generate(Data(range, English), "0.1.0", Now, English).Length.ShouldBeGreaterThan(5_000);
    }

    [Fact]
    public void Ninety_days_of_bars_fit()
    {
        var range = Ranges.Days(new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 29), Now, TimeZoneInfo.Utc, English);
        ReportDocument.Generate(Data(range, English), "0.1.0", Now, English).Length.ShouldBeGreaterThan(5_000);
    }

    [Fact]
    public void Including_the_household_adds_a_page_per_member_and_still_draws()
    {
        var range = Ranges.ThisMonth(Now, TimeZoneInfo.Utc, English);
        var household = HouseholdReportData.From(
            new HouseholdRangeTotals(46.8, [new CurrencyCost("USD", 7.96m)], []),
            [
                new DeviceReport("aaaa", 34.2, 15.4, 3.8, 1.9, 13.1, [new CurrencyCost("USD", 5.81m)]),
                new DeviceReport("bbbb", 12.6, 5.1, 1.3, 0.8, 5.4, [new CurrencyCost("USD", 2.15m)]),
            ],
            [
                new HouseholdMemberRow("aaaa", "Desktop-1", ChassisKind.Desktop, Now.AddDays(-40), null, Now),
                new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now.AddDays(-20), null, Now),
            ],
            English);
        var data = ReportData.From(Reports.Typical(range), SleepTimeouts.Unknown, 0.38, TimeZoneInfo.Utc, English, household);
        var without = ReportDocument.Generate(Data(range, English), "0.1.0", Now, English);

        var withHousehold = ReportDocument.Generate(data, "0.1.0", Now, English);

        Encoding.ASCII.GetString(withHousehold, 0, 5).ShouldBe("%PDF-");
        withHousehold.Length.ShouldBeGreaterThan(without.Length);   // two extra member pages
    }
}
