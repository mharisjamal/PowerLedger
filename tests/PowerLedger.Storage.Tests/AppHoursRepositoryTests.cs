using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class AppHoursRepositoryTests
{
    private static readonly DateTimeOffset Hour = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static readonly AppHourRow Chrome = new(@"c:\program files\google\chrome\application\chrome.exe", "Google Chrome",
        @"C:\Program Files\Google\Chrome\Application\chrome.exe", CpuWh: 1.25, GpuWh: 0.5, RunSeconds: 3600, FrontSeconds: 1200.5);

    private static readonly AppHourRow Windows = new(AppKeys.Windows, "Windows", null, 0.75, 0, 3600, 0);

    [Fact]
    public void An_hour_round_trips_and_a_later_write_replaces_every_row_of_that_hour_only()
    {
        using var t = new TestDatabase();
        var repo = new AppHoursRepository(t.Db);
        repo.ReadHour(Hour).ShouldBeEmpty();

        repo.ReplaceHour(Hour, [Chrome, Windows]);
        repo.ReplaceHour(Hour.AddHours(1), [Windows]);
        repo.ReadHour(Hour).OrderBy(r => r.Key, StringComparer.Ordinal).ShouldBe([Windows, Chrome]);

        // The hour's busiest can change as it goes on: an app that dropped out of them must not stay behind.
        var later = Windows with { CpuWh = 2 };
        repo.ReplaceHour(Hour, [later]);
        repo.ReadHour(Hour).ShouldBe([later]);
        repo.ReadHour(Hour.AddHours(1)).ShouldBe([Windows]);
    }

    [Fact]
    public void A_row_with_a_figure_that_is_no_number_is_refused_and_the_hour_is_left_as_it_was()
    {
        using var t = new TestDatabase();
        var repo = new AppHoursRepository(t.Db);
        repo.ReplaceHour(Hour, [Chrome]);
        Should.Throw<ArgumentException>(() => repo.ReplaceHour(Hour, [Windows, Chrome with { GpuWh = double.NaN }]));
        repo.ReadHour(Hour).ShouldBe([Chrome]);
    }

    [Fact]
    public void Retention_purges_hours_older_than_400_days_and_keeps_the_rest()
    {
        using var t = new TestDatabase();
        var repo = new AppHoursRepository(t.Db);
        var now = Hour.AddDays(500);
        repo.ReplaceHour(now.AddDays(-401), [Chrome, Windows]);
        repo.ReplaceHour(now.AddDays(-399), [Chrome]);

        var result = new RetentionJob(t.Db).Run(now, new RetentionOptions());
        result.AppHoursDeleted.ShouldBe(2);
        repo.ReadHour(now.AddDays(-401)).ShouldBeEmpty();
        repo.ReadHour(now.AddDays(-399)).ShouldBe([Chrome]);
    }
}
