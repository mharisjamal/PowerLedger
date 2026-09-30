using Microsoft.Extensions.Time.Testing;

namespace PowerLedger.App.Tests;

/// <summary>What the Aero shell tests draw (Plan S D4): Midnight's Tuesday afternoon (MidnightFixtures) in the Aero look,
/// with a Dashboard whose Save CSV goes to a fake dialog, Insights over a fixed report, and preferences just moved to Aero
/// from <paramref name="before"/> with the banner still to show.</summary>
internal static class AeroFixtures
{
    public static FakeUiSettings Moved(Look? before = Look.Midnight, bool introduced = false)
        => new() { Current = UiPreferences.Default with { Look = Look.Aero, AeroApproved = true, LookIntroduced = introduced, LookBeforeAero = before } };

    public static ShellViewModel Shell(FakeSaver saver, FakeUiSettings? ui = null, FakeInsights? insights = null, int pendingApprovals = 0, Updater? updates = null)
        => Shell(saver, out _, ui, insights, pendingApprovals, updates);

    /// <summary>The shell, and the <paramref name="link"/> its Now screen reads, for a test that pushes another reading.</summary>
    public static ShellViewModel Shell(FakeSaver saver, out FakeLink link, FakeUiSettings? ui = null, FakeInsights? insights = null, int pendingApprovals = 0, Updater? updates = null)
    {
        var now = MidnightFixtures.NowScreen(out link);
        var dashboard = Dashboard(now, saver);
        var insight = new InsightsViewModel(insights ?? new FakeInsights(), UiThreads.Inline, new FakeTimeProvider(MidnightFixtures.Now), TimeZoneInfo.Utc);
        var shell = new ShellViewModel(now, MidnightFixtures.BreakdownScreen(), MidnightFixtures.ReportScreen(saver), MidnightFixtures.HouseholdScreen(pendingApprovals),
            MidnightFixtures.SettingsScreen(ui ?? Moved(introduced: true)), MidnightFixtures.WizardScreen(), "0.10.0", updates, dashboard, insight)
        {
            Page = Page.Dashboard,
        };
        return shell;
    }

    /// <summary>MidnightFixtures' Dashboard, saving through <paramref name="saver"/> rather than Windows' dialog.</summary>
    public static DashboardViewModel Dashboard(NowViewModel now, FakeSaver saver)
    {
        var history = new FakeRangeHistory
        {
            Answer = range => Reports.Typical(range) with
            {
                Series = range.Bucket == TimeSpan.FromMinutes(5) ? MidnightFixtures.DaySeries(new DateTimeOffset(MidnightFixtures.Now.Date, TimeSpan.Zero)) : MidnightFixtures.WeekSeries(range),
            },
        };
        var summary = new FakeHistory { Snapshot = Snapshots.Typical(MidnightFixtures.Now, MidnightFixtures.DaySeries(new DateTimeOffset(MidnightFixtures.Now.Date, TimeSpan.Zero))), First = MidnightFixtures.Now.AddDays(-40) };
        return new DashboardViewModel(now, history, summary, new FakeTimeProvider(MidnightFixtures.Now), TimeZoneInfo.Utc, MidnightFixtures.English, UiThreads.Inline,
            new FakeUiSettings(), new FakeHardware(), saver);
    }
}
