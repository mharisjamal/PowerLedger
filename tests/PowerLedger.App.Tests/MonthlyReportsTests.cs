using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class MonthlyReportsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-monthly-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeRangeHistory _history = new() { First = new DateOnly(2026, 6, 15) };
    private readonly List<IReadOnlyList<MonthlyReport>> _toasts = [];

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private MonthlyReports Job() => new(
        _history, new FakeSleep(), data => Encoding.UTF8.GetBytes(data.Title), _folder, _clock, TimeZoneInfo.Utc, English, 0.38, _toasts.Add);

    [Fact]
    public void Finished_months_since_history_began_are_due_newest_first()
        => MonthlyReports.Due(new DateOnly(2026, 6, 15), new DateOnly(2026, 9, 8), _ => false)
            .ShouldBe(new[] { new DateOnly(2026, 8, 1), new DateOnly(2026, 7, 1), new DateOnly(2026, 6, 1) });

    [Fact]
    public void A_month_with_its_pdf_is_not_due_and_nothing_is_due_without_history()
    {
        MonthlyReports.Due(new DateOnly(2026, 6, 15), new DateOnly(2026, 9, 8), month => month.Month == 7)
            .ShouldBe(new[] { new DateOnly(2026, 8, 1), new DateOnly(2026, 6, 1) });
        MonthlyReports.Due(null, new DateOnly(2026, 9, 8), _ => false).ShouldBeEmpty();
        MonthlyReports.Due(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 8), _ => false).ShouldBeEmpty();
    }

    [Fact]
    public void Only_the_last_twelve_months_are_looked_at()
    {
        var due = MonthlyReports.Due(new DateOnly(2024, 1, 10), new DateOnly(2026, 9, 8), _ => false);
        due.Count.ShouldBe(12);
        due[^1].ShouldBe(new DateOnly(2025, 9, 1));
    }

    [Fact]
    public void A_check_writes_each_due_month_and_the_tray_hears_of_them_once()
    {
        var written = Job().Check();

        written.Select(w => Path.GetFileName(w.Path)).ShouldBe(new[] { "PowerLedger-2026-08.pdf", "PowerLedger-2026-07.pdf", "PowerLedger-2026-06.pdf" });
        File.ReadAllText(Path.Combine(_folder, "PowerLedger-2026-08.pdf")).ShouldBe("August 2026");
        _toasts.Single().Count.ShouldBe(3);
        MonthlyReports.Toast(_toasts.Single()).Title.ShouldBe("3 monthly reports saved");
        Job().Check().ShouldBeEmpty();                                        // a second check finds them written
        _toasts.Count.ShouldBe(1);
    }

    [Fact]
    public void A_month_with_no_readings_is_skipped_and_unreadable_history_waits()
    {
        _history.Answer = range => range.Title == "July 2026" ? Reports.Empty(range) : Reports.Typical(range);
        Job().Check().Select(w => w.Data.Title).ShouldBe(new[] { "August 2026", "June 2026" });

        Directory.Delete(_folder, recursive: true);
        _history.Answer = _ => null;
        Job().Check().ShouldBeEmpty();
        Directory.Exists(_folder).ShouldBeFalse();
    }

    [Fact]
    public void The_toast_gives_the_newest_months_headline()
    {
        var data = ReportData.Empty with { Title = "August 2026", Energy = "27.4", Cost = "$4.66" };
        MonthlyReports.Toast([new MonthlyReport("a.pdf", data)])
            .ShouldBe(("August 2026 report saved", "August 2026: 27.4 kWh · $4.66. Saved to Documents\\PowerLedger; click to open it."));
        MonthlyReports.Toast([new MonthlyReport("a.pdf", data with { Cost = "–" })]).Text
            .ShouldBe("August 2026: 27.4 kWh. Saved to Documents\\PowerLedger; click to open it.");
    }

    [Fact]
    public void The_first_check_comes_soon_after_start_then_hourly()
    {
        using var job = Job();
        job.Start();
        _clock.Advance(MonthlyReports.FirstCheck);
        _toasts.Count.ShouldBe(1);

        File.Delete(Path.Combine(_folder, "PowerLedger-2026-08.pdf"));
        _clock.Advance(MonthlyReports.CheckEvery);
        _toasts.Count.ShouldBe(2);
    }

    [Fact]
    public void The_co2_factor_can_change_between_checks()
    {
        using var job = Job();
        job.Co2KgPerKwh = 0.9;
        job.Check().First().Data.Co2Note.ShouldBe("at 0.90 kg / kWh");
    }
}
