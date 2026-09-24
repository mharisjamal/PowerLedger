using System.Globalization;
using System.IO;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class ReportViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeRangeHistory _history = new();
    private readonly FakeHouseholdHistory _household = new();
    private readonly FakeSaver _saver = new();
    private readonly FakeSleep _sleep = new();

    public void Dispose() => _saver.Dispose();

    private ReportViewModel Model() => new(_history, _household, _sleep, _saver, _ => [1, 2, 3], UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.38);

    [Fact]
    public void It_opens_on_this_month_and_reads_when_shown()
    {
        var model = Model();
        model.Data.ShouldBe(ReportData.Empty);
        model.Show();

        model.Data.Title.ShouldBe("September 2026");
        model.Data.Energy.ShouldBe("2.74");
        model.Data.Advice.ShouldStartWith("Windows sleeps after 3 h");
        model.Message.ShouldBeNull();
        _sleep.Reads.ShouldBe(1);
    }

    [Fact]
    public void Choosing_last_month_reads_it()
    {
        var model = Model();
        model.Show();
        model.Range.Choice = RangeChoice.LastMonth;
        model.Data.Title.ShouldBe("August 2026");
    }

    [Fact]
    public void The_pdf_is_saved_where_the_user_says()
    {
        var model = Model();
        model.Show();
        model.Range.Choice = RangeChoice.LastMonth;
        model.ExportPdf.Execute(null);

        _saver.Suggested.ShouldBe("PowerLedger-2026-08.pdf");
        File.ReadAllBytes(_saver.Chosen).ShouldBe(new byte[] { 1, 2, 3 });
        model.Saved.ShouldBe("Saved PowerLedger-2026-08.pdf");
    }

    [Fact]
    public void Csv_is_the_shown_range_at_the_chosen_grain()
    {
        var model = Model();
        model.Show();
        model.ExportCsv.Execute("Minute");

        _saver.Suggested.ShouldBe("PowerLedger-2026-09-01-to-2026-09-08-1min.csv");
        _history.Exports.Single().Grain.ShouldBe(ExportGrain.Minute);
        _history.Exports.Single().Range.Title.ShouldBe("September 2026");
        File.ReadAllLines(_saver.Chosen).ShouldBe(new[] { "header", "row" });
    }

    [Fact]
    public void A_cancelled_save_writes_nothing()
    {
        _saver.Cancel = true;
        var model = Model();
        model.Show();
        model.ExportPdf.Execute(null);

        File.Exists(_saver.Chosen).ShouldBeFalse();
        model.Saved.ShouldBeNull();
    }

    [Fact]
    public void A_save_that_fails_says_why_and_leaves_nothing_behind()
    {
        _history.Lines = null;                                     // history can't be read mid-export
        var model = Model();
        model.Show();
        model.ExportCsv.Execute("Hour");

        model.Saved.ShouldBe("Couldn't save: history can't be read right now.");
        File.Exists(_saver.Chosen).ShouldBeFalse();
        File.Exists(_saver.Chosen + ".partial").ShouldBeFalse();
    }

    [Fact]
    public void The_picture_is_drawn_by_the_view_into_the_chosen_file()
    {
        var model = Model();
        model.Show();
        model.SaveImage(stream => stream.WriteByte(7));

        _saver.Suggested.ShouldBe("PowerLedger-2026-09-01-to-2026-09-08.png");
        File.ReadAllBytes(_saver.Chosen).ShouldBe(new byte[] { 7 });
    }

    [Fact]
    public void Nothing_to_show_says_why()
    {
        _history.Answer = _ => null;
        var model = Model();
        model.Show();
        model.Message.ShouldBe("History can't be read right now. It comes back when the service is running.");
        model.Data.Title.ShouldBe("September 2026");

        _history.Answer = Reports.Empty;
        model.Refresh();
        model.Message.ShouldBe("No readings in this range.");
    }

    [Fact]
    public void A_day_is_named_by_its_date()
        => ReportViewModel.FileName(Ranges.Today(Now, TimeZoneInfo.Utc, English), TimeZoneInfo.Utc).ShouldBe("PowerLedger-2026-09-08");

    [Fact]
    public void A_new_co2_factor_reads_the_report_again()
    {
        var model = Model();
        model.Show();
        model.Data.Co2.ShouldBe("1.04 kg");

        model.Co2KgPerKwh = 0.2;
        model.Data.Co2.ShouldBe("0.55 kg");
    }

    [Fact]
    public void The_household_is_left_out_until_the_tick_is_on()
    {
        var model = Model();
        model.Show();

        model.IncludeHousehold.ShouldBeFalse();
        model.Data.Household.ShouldBeNull();
        _household.ReportReads.ShouldBeEmpty();
    }

    [Fact]
    public void Ticking_include_household_reads_it_for_the_shown_range_and_adds_it()
    {
        _household.ReportAnswer = _ => new HouseholdReportSnapshot(
            new HouseholdRangeTotals(12.5, [new CurrencyCost("USD", 2.1m)], []),
            [new DeviceReport("bbbb", 4.2, 1.6, 0.4, 0.2, 2.0, [new CurrencyCost("USD", 0.7m)])],
            [new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now, null, Now)]);
        var model = Model();
        model.Show();

        model.IncludeHousehold = true;

        _household.ReportReads.Single().Title.ShouldBe("September 2026");
        model.Data.Household.ShouldNotBeNull();
        model.Data.Household.Energy.ShouldBe("12.5");
        model.Data.Household.Costs.ShouldBe([new HouseholdCostLine("USD", Money.Format(2.1m, "USD", English))]);
        var member = model.Data.Household.Members.Single();
        member.Name.ShouldBe("Laptop-2");
        member.Kind.ShouldBe("Laptop");
        member.Energy.ShouldBe("4.20");
        member.Parts.Select(p => p.Name).ShouldBe(["CPU package", "GPU", "Display", "Rest of system"]);
    }

    [Fact]
    public void Turning_it_back_off_drops_it_and_reads_again()
    {
        var model = Model();
        model.Show();
        model.IncludeHousehold = true;
        model.Data.Household.ShouldNotBeNull();

        model.IncludeHousehold = false;

        model.Data.Household.ShouldBeNull();
    }

    [Fact]
    public void A_household_that_cannot_be_read_is_left_out_without_failing_the_rest_of_the_report()
    {
        _household.ReportAnswer = _ => null;
        var model = Model();
        model.Show();

        model.IncludeHousehold = true;

        model.Data.Household.ShouldBeNull();
        model.Message.ShouldBeNull();          // the report itself still has this PC's own data
    }
}
