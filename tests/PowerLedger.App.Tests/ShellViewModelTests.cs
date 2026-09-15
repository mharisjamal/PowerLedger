using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ShellViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeRangeHistory _history = new();
    private readonly FakeLink _link = new();

    private ShellViewModel Shell() => new(
        new NowViewModel(new FakeLink(), new FakeHistory(), UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4, () => { }),
        new BreakdownViewModel(_history, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English),
        new ReportViewModel(_history, new FakeSleep(), new FakeSaver(), _ => [], UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4),
        new SettingsViewModel(_link, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "USD"),
        "0.1.0");

    [Fact]
    public void A_history_screen_reads_only_while_it_shows()
    {
        var shell = Shell();
        shell.Page = Page.Breakdown;
        shell.Current.ShouldBe(shell.Breakdown);
        _history.Reads.Single().Title.ShouldBe("Today");

        shell.Page = Page.Report;
        shell.Current.ShouldBe(shell.Report);
        _history.Reads[^1].Title.ShouldBe("September 2026");

        shell.Page = Page.Now;
        _clock.Advance(BreakdownViewModel.RefreshEvery * 3);
        _history.Reads.Count.ShouldBe(2);
    }

    [Fact]
    public void Settings_reads_the_service_while_it_shows()
    {
        _link.Connect(true);
        var shell = Shell();
        shell.Page = Page.Settings;
        shell.Current.ShouldBe(shell.Settings);
        shell.Settings.Service.IsLoaded.ShouldBeTrue();
    }
}
