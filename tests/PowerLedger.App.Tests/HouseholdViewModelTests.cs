using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.App.Tests;

public class HouseholdViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeHouseholdHistory _history = new();
    private readonly FakeLink _link = new();

    private HouseholdViewModel Model() => new(_link, _history, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English);

    private static ServiceStatus InHousehold(string deviceId = "aaaa")
        => Statuses.Running() with { Household = new HouseholdStatus("hh1", deviceId, "Desktop-1", ChassisKind.Desktop, true, [], null) };

    private static HouseholdSnapshot SnapshotWith(IReadOnlyList<HouseholdMemberRow> members, IReadOnlyList<DeviceEnergy>? month = null) => new(
        new HouseholdRangeTotals(0, [], []), new HouseholdRangeTotals(0, [], []), new HouseholdRangeTotals(0, [], month ?? []), members);

    [Fact]
    public void With_no_household_the_page_explains_the_feature_and_reads_nothing_from_storage()
    {
        _link.Status = Statuses.Running() with { Household = new HouseholdStatus(null, "aaaa", "Desktop-1", ChassisKind.Desktop, true, [], null) };
        _link.Connect(true);
        var model = Model();

        model.Show();

        model.HasHousehold.ShouldBeFalse();
        model.Message.ShouldBe(HouseholdViewModel.Explanation);
        model.HasMessage.ShouldBeTrue();
        model.Members.ShouldBeEmpty();
        _history.Reads.ShouldBeEmpty();
    }

    [Fact]
    public void Not_connected_to_the_service_reads_as_no_household_too()
    {
        var model = Model();
        model.Show();

        model.HasHousehold.ShouldBeFalse();
        model.Message.ShouldBe(HouseholdViewModel.Explanation);
    }

    [Fact]
    public void Today_this_week_and_this_month_show_energy_and_cost_grouped_by_currency()
    {
        _link.Status = InHousehold();
        _link.Connect(true);
        _history.Answer = _ => new HouseholdSnapshot(
            new HouseholdRangeTotals(1.5, [new CurrencyCost("USD", 0.25m)], []),
            new HouseholdRangeTotals(10.0, [new CurrencyCost("USD", 1.6m)], []),
            new HouseholdRangeTotals(40.0, [new CurrencyCost("USD", 6.4m), new CurrencyCost("EUR", 1.1m)], []),
            []);
        var model = Model();

        model.Show();

        model.HasHousehold.ShouldBeTrue();
        model.Message.ShouldBeNull();
        model.HasMessage.ShouldBeFalse();
        model.Today.Title.ShouldBe("Today");
        model.Today.Energy.ShouldBe("1.50");
        model.Today.Costs.ShouldBe([new HouseholdCostLine("USD", Money.Format(0.25m, "USD", English))]);
        model.Week.Energy.ShouldBe("10.0");
        model.Month.Energy.ShouldBe("40.0");
        model.Month.Costs.Count.ShouldBe(2);   // shown side by side, never converted into one
        model.Month.Costs.Select(c => c.Currency).ShouldBe(["USD", "EUR"], ignoreOrder: true);
    }

    [Fact]
    public void Each_pc_shows_its_name_kind_whether_its_this_pc_and_its_share_of_this_months_energy()
    {
        _link.Status = InHousehold("aaaa");
        _link.Connect(true);
        _history.Answer = _ => SnapshotWith(
            [
                new HouseholdMemberRow("aaaa", "Desktop-1", ChassisKind.Desktop, Now.AddDays(-30), null, Now.AddMinutes(-2)),
                new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now.AddDays(-10), null, Now.AddDays(-3)),
            ],
            [new DeviceEnergy("aaaa", 12.0), new DeviceEnergy("bbbb", 4.0)]);
        var model = Model();

        model.Show();

        model.Members.Count.ShouldBe(2);
        var mine = model.Members.Single(m => m.DeviceId == "aaaa");
        mine.Name.ShouldBe("Desktop-1");
        mine.Kind.ShouldBe("Desktop");
        mine.IsThisPc.ShouldBeTrue();
        mine.Energy.ShouldBe("12.0");
        mine.Share.ShouldBe(1.0);              // the busiest PC's bar is full
        mine.Status.ShouldBe("synced 2 minutes ago");

        var theirs = model.Members.Single(m => m.DeviceId == "bbbb");
        theirs.IsThisPc.ShouldBeFalse();
        theirs.Energy.ShouldBe("4.00");
        theirs.Share.ShouldBe(4.0 / 12.0, 1e-9);
        theirs.Status.ShouldBe("last seen 3 days ago");
    }

    [Fact]
    public void A_pc_that_has_left_is_shown_as_left_however_long_ago_it_last_synced()
    {
        _link.Status = InHousehold();
        _link.Connect(true);
        _history.Answer = _ => SnapshotWith([new HouseholdMemberRow("cccc", "Old-PC", ChassisKind.Desktop, Now.AddDays(-90), Now.AddMinutes(-1), Now.AddMinutes(-30))]);
        var model = Model();

        model.Show();

        model.Members.Single().Status.ShouldBe("left");
    }

    [Fact]
    public void A_pc_that_has_never_synced_says_so()
    {
        _link.Status = InHousehold();
        _link.Connect(true);
        _history.Answer = _ => SnapshotWith([new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now, null, null)]);
        var model = Model();

        model.Show();

        model.Members.Single().Status.ShouldBe("not synced yet");
    }

    [Fact]
    public void A_sync_just_under_a_day_old_still_reads_as_synced_not_last_seen()
    {
        _link.Status = InHousehold();
        _link.Connect(true);
        _history.Answer = _ => SnapshotWith([new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now, null, Now.AddHours(-23))]);
        var model = Model();

        model.Show();

        model.Members.Single().Status.ShouldBe("synced 23 hours ago");
    }

    [Fact]
    public void When_the_service_cannot_be_read_the_page_says_so_instead_of_showing_stale_totals()
    {
        _link.Status = InHousehold();
        _link.Connect(true);
        _history.Answer = _ => null;
        var model = Model();

        model.Show();

        model.HasHousehold.ShouldBeTrue();
        model.Message.ShouldBe("History can't be read right now. It comes back when the service is running.");
    }

    [Fact]
    public void Add_a_pc_asks_the_shell_to_open_the_window()
    {
        var model = Model();
        var raised = 0;
        model.AddPcRequested += () => raised++;

        model.AddPc.Execute(null);

        raised.ShouldBe(1);
    }

    [Fact]
    public void It_refreshes_every_minute_while_shown_and_stops_once_hidden()
    {
        _link.Status = InHousehold();
        _link.Connect(true);
        var model = Model();

        model.Show();
        _history.Reads.Count.ShouldBe(1);

        _clock.Advance(HouseholdViewModel.RefreshEvery);
        _history.Reads.Count.ShouldBe(2);

        model.Hide();
        _clock.Advance(HouseholdViewModel.RefreshEvery * 3);
        _history.Reads.Count.ShouldBe(2);
    }
}
