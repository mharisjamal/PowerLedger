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
    private readonly FakeHouseholdHistory _householdHistory = new();
    private readonly FakeLink _link = new();
    private readonly FakeUiSettings _ui = new();
    private readonly FakeMachineHistory _machine = new();

    private ShellViewModel Shell() => new(
        new NowViewModel(new FakeLink(), new FakeHistory(), UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4, () => { }),
        new BreakdownViewModel(_link, _history, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English),
        new ReportViewModel(_history, _householdHistory, new FakeSleep(), new FakeSaver(), _ => [], UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4),
        new HouseholdViewModel(_link, _householdHistory, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, FakeAccount.Model(_link)),
        new SettingsViewModel(_link, _machine, _ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "USD"),
        new WizardViewModel(_link, _machine, _ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "USD"),
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
    public void The_household_page_reads_only_while_it_shows()
    {
        var shell = Shell();
        shell.Page = Page.Household;
        shell.Current.ShouldBe(shell.Household);
        _householdHistory.Reads.ShouldBeEmpty();          // no household in the fake status: nothing is read from storage

        shell.Page = Page.Now;
        _clock.Advance(HouseholdViewModel.RefreshEvery * 3);
        _householdHistory.Reads.ShouldBeEmpty();           // stopped once hidden, so no read arrives late
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

    [Fact]
    public void Setup_takes_the_window_until_it_is_finished()
    {
        var shell = Shell();
        shell.Page = Page.Report;
        shell.BeginSetup();
        shell.IsSetup.ShouldBeTrue();
        shell.Current.ShouldBe(shell.Wizard);

        shell.Wizard.Finish.Execute(null);
        shell.IsSetup.ShouldBeFalse();
        shell.Page.ShouldBe(Page.Now);
        shell.Current.ShouldBe(shell.Now);
        _ui.Current.FirstRunDone.ShouldBeTrue();
    }

    [Fact]
    public void Settings_can_run_setup_again()
    {
        var shell = Shell();
        shell.Page = Page.Settings;
        shell.Settings.RunSetup.Execute(null);
        shell.IsSetup.ShouldBeTrue();
        shell.Wizard.Step.ShouldBe(SetupStep.Tariff);
    }
}
