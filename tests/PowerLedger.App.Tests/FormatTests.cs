using System.Globalization;
using Shouldly;

namespace PowerLedger.App.Tests;

public class FormatTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    [Fact]
    public void Live_watts_have_one_decimal_and_never_go_negative()
    {
        Format.Watts(34.23, Invariant).ShouldBe("34.2");
        Format.Watts(-0.4, Invariant).ShouldBe("0.0");
        Format.Watts(double.NaN, Invariant).ShouldBe("N/A");
        Format.Watts(34.23, German).ShouldBe("34,2");
    }

    [Fact]
    public void A_missing_value_reads_as_a_word_never_a_dash()
    {
        Format.Missing.ShouldBe("N/A");                          // a cell in a ledger, a table or a legend
        Format.NoReading.ShouldBe("No reading");                 // a figure standing alone, set large
        TodayLedger.Empty.Energy.ShouldBe(Format.Missing);
        MonthLedger.Empty.Cost.ShouldBe(Format.Missing);
        ChartLegend.Empty.Cpu.ShouldBe(Format.Missing);
        LiveReadout.Figure(double.NaN, Invariant).ShouldBe(Format.NoReading);
        LiveReadout.Figure(34.23, Invariant).ShouldBe("34.2");
    }

    [Fact]
    public void Energy_keeps_three_significant_figures()
    {
        Format.Kwh(0.2841, Invariant).ShouldBe("0.284");
        Format.Kwh(2.744, Invariant).ShouldBe("2.74");
        Format.Kwh(10.32, Invariant).ShouldBe("10.3");
        Format.Kwh(274.4, Invariant).ShouldBe("274");
    }

    [Fact]
    public void Durations_read_as_hours_and_minutes()
    {
        Format.Duration(7 + 5 / 60.0).ShouldBe("7h 05m");
        Format.Duration(0.75).ShouldBe("45m");
        Format.Duration(0).ShouldBe("0m");
        Format.Duration(double.NaN).ShouldBe("0m");
    }

    [Fact]
    public void Kilograms_percents_and_whole_watts()
    {
        Format.Kg(0.1079, Invariant).ShouldBe("0.11");
        Format.Percent(0.427, Invariant).ShouldBe("43%");
        Format.Percent(1.2, Invariant).ShouldBe("100%");
        Format.WholeWatts(41.4, Invariant).ShouldBe("41");
    }

    [Fact]
    public void Money_uses_the_currency_symbol_and_the_reader_s_number_style()
    {
        Money.Format(0.0483m, "USD", English).ShouldBe("$0.05");
        Money.Format(0.0483m, "EUR", English).ShouldBe("€0.05");
        Money.Format(1234.5m, "USD", German).Replace(' ', ' ').ShouldBe("1.234,50 $");   // ICU puts a no-break space there
    }

    [Fact]
    public void A_currency_without_a_known_symbol_is_written_with_its_code()
        => Money.Format(5m, "XXX", English).ShouldBe("5.00 XXX");

    [Fact]
    public void A_price_keeps_the_decimals_it_needs_up_to_four()
    {
        Money.Rate(0.17m, "USD", English).ShouldBe("$0.17");
        Money.Rate(0.125m, "USD", English).ShouldBe("$0.125");
        Money.Rate(0.12345m, "USD", English).ShouldBe("$0.1235");
    }
}
