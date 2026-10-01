using System.Globalization;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Plan S D1, D2: the choices Aero's shell makes, pure: the looks Switch look offers and where the banner's Switch
/// back goes, what the bell holds, the sidebar's "Your PCs", the household button's line, and which view each page shows.</summary>
public class AeroShellLogicTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);

    [Theory]
    [InlineData(null, "Midnight")]
    [InlineData("Classic", "Classic")]
    [InlineData("Midnight", "Midnight")]
    [InlineData("Aero", "Midnight")]
    public void Switch_back_returns_to_the_look_left_midnight_when_there_was_none(string? before, string back)
        => AeroLooks.SwitchBackTo(before is null ? null : Enum.Parse<Look>(before)).ShouldBe(Enum.Parse<Look>(back));

    [Fact]
    public void The_bell_holds_todays_unusual_hours_newest_first_three_at_the_most()
    {
        UsageAnomaly At(int day, int hour, double times) => new(new DateTimeOffset(2026, 9, day, hour, 0, 0, TimeSpan.Zero), 0.3, 0.1, times);
        var anomalies = new[] { At(8, 2, 3), At(8, 9, 4), At(7, 22, 5), At(8, 11, 3.5), At(8, 13, 3.1), At(8, 6, 6) };

        var alerts = UsageAnomalies.Today(anomalies, Now, TimeZoneInfo.Utc);

        alerts.Select(a => a.Hour.Hour).ShouldBe([13, 11, 9], "today's only, the newest three");
        UsageAnomalies.Today([], Now, TimeZoneInfo.Utc).ShouldBeEmpty();
    }

    [Fact]
    public void An_alert_says_when_and_how_many_times_the_usual()
    {
        var alert = new UsageAnomaly(new DateTimeOffset(2026, 9, 8, 14, 0, 0, TimeSpan.Zero), 0.42, 0.12, 3.5);

        Bell.Line(alert, TimeZoneInfo.Utc, English).ShouldBe("2:00 PM, 3.5 times the usual");
        Bell.Figure(alert, English).ShouldBe("0.420 kWh");
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, true)]
    [InlineData(0, 2, true)]
    public void The_bell_shows_its_dot_while_something_waits(int approvals, int alerts, bool dot)
        => Bell.HasNews(approvals, alerts).ShouldBe(dot);

    [Theory]
    [InlineData(0, "Nothing is waiting for you.")]
    [InlineData(1, "1 PC is waiting to join")]
    [InlineData(3, "3 PCs are waiting to join")]
    public void The_bell_words_the_approvals(int approvals, string line) => Bell.Approvals(approvals).ShouldBe(line);

    [Fact]
    public void Your_pcs_shows_this_pc_with_its_watts_now_and_the_others_with_this_months_energy()
    {
        HouseholdMemberDisplay[] members =
        [
            new("bbbb", "Laptop-2", "Laptop", false, "12.6", 0.27, "last seen 3 days ago", false),
            new("aaaa", "Desktop-1", "Desktop", true, "34.2", 0.73, "synced", false),
            new("cccc", "Old PC", "Desktop", false, "0", 0, "left", true),
        ];

        var rows = YourPcs.Rows(members, 34.4, English);

        rows.Select(r => r.Name).ShouldBe(["This PC", "Laptop-2"], "this PC first, and a PC that left is left out");
        rows[0].Figure.ShouldBe("34 W");
        rows[0].IsThisPc.ShouldBeTrue();
        rows[0].Initials.ShouldBe(Initials.Letters("Desktop-1"));
        rows[1].Figure.ShouldBe("12.6 kWh");
        rows[1].Share.ShouldBe(0.27);
    }

    [Fact]
    public void Without_a_household_your_pcs_is_this_pc_alone()
    {
        var rows = YourPcs.Rows([], double.NaN, English);

        rows.Count.ShouldBe(1);
        rows[0].Name.ShouldBe("This PC");
        rows[0].Figure.ShouldBe(Format.NoReading);
        rows[0].Share.ShouldBe(1);
        rows[0].Initials.ShouldBe(Initials.Letters(Environment.MachineName));
    }

    [Theory]
    [InlineData(0, "1 PC")]
    [InlineData(1, "1 PC")]
    [InlineData(2, "2 PCs")]
    [InlineData(4, "4 PCs")]
    public void The_household_button_counts_the_pcs(int pcs, string line) => YourPcs.Summary(pcs).ShouldBe(line);

    [Theory]
    [InlineData("Dashboard", "PowerLedger.App.Aero.DashboardView")]
    [InlineData("Now", "PowerLedger.App.Aero.DashboardView")]
    [InlineData("Parts", "PowerLedger.App.Aero.PartsView")]
    [InlineData("Breakdown", "PowerLedger.App.Aero.HistoryView")]
    [InlineData("Report", "PowerLedger.App.Aero.ReportsView")]
    [InlineData("Household", "PowerLedger.App.Aero.HouseholdView")]
    [InlineData("Settings", "PowerLedger.App.Aero.SettingsView")]
    [InlineData("Insights", "PowerLedger.App.Aero.InsightsView")]
    public void Each_page_has_its_view_by_page_not_only_by_type(string page, string view)
        => AeroPageHost.ViewName(Enum.Parse<Page>(page)).ShouldBe(view);
}
