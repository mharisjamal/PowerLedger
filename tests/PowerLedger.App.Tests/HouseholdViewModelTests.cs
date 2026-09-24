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

    private HouseholdViewModel Model() => new(_link, _history, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, FakeAccount.Model(_link));

    private static ServiceStatus InHousehold(string deviceId = "aaaa", string name = "Desktop-1", string? problem = null)
        => Statuses.Running() with { Household = new HouseholdStatus("hh1", deviceId, name, ChassisKind.Desktop, true, [], problem) };

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
    public void A_syncing_problem_shows_alongside_whatever_totals_there_are()
    {
        _link.Status = InHousehold(problem: "Couldn't reach the server. Will try again.");
        _link.Connect(true);
        var model = Model();

        model.Show();

        model.HasProblem.ShouldBeTrue();
        model.Problem.ShouldBe("Couldn't reach the server. Will try again.");
        model.Message.ShouldBeNull();   // the problem does not stop the totals from showing
    }

    [Fact]
    public void No_problem_is_the_normal_case()
    {
        _link.Status = InHousehold();
        _link.Connect(true);
        var model = Model();

        model.Show();

        model.HasProblem.ShouldBeFalse();
        model.Problem.ShouldBeNull();
    }

    [Fact]
    public void Each_pc_shows_its_name_kind_whether_its_this_pc_and_its_share_of_this_months_energy()
    {
        _link.Status = InHousehold("aaaa");
        _link.Connect(true);
        _history.Answer = _ => SnapshotWith(
            [
                // This PC has no "last synced" of its own to report (households design §2, review finding A9).
                new HouseholdMemberRow("aaaa", "Desktop-1", ChassisKind.Desktop, Now.AddDays(-30), null, null),
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
        mine.Status.ShouldBe("");

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

    [Fact]
    public void The_name_box_follows_this_pcs_name_until_the_user_types_something_else()
    {
        _link.Status = InHousehold();
        _link.Connect(true);
        var model = Model();

        model.Show();
        model.NameInput.ShouldBe("Desktop-1");

        model.NameInput = "My desktop";                    // an unsaved edit
        _clock.Advance(HouseholdViewModel.RefreshEvery);    // a routine refresh must not clobber it
        model.NameInput.ShouldBe("My desktop");
    }

    [Fact]
    public void A_rename_from_elsewhere_still_updates_an_untouched_box()
    {
        _link.Status = InHousehold();
        _link.Connect(true);
        var model = Model();
        model.Show();

        _link.Status = InHousehold(name: "Renamed-elsewhere");
        _clock.Advance(HouseholdViewModel.RefreshEvery);

        model.NameInput.ShouldBe("Renamed-elsewhere");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Save_name_is_disabled_for_an_empty_name(string input)
    {
        var model = Model();
        model.NameInput = input;
        model.SaveName.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void Save_name_is_disabled_past_forty_characters()
    {
        var model = Model();
        model.NameInput = new string('x', 41);
        model.SaveName.CanExecute(null).ShouldBeFalse();

        model.NameInput = new string('x', 40);
        model.SaveName.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void Saving_a_name_sends_it_trimmed_and_reports_the_result()
    {
        _link.Connect(true);
        var model = Model();
        model.NameInput = "  Study PC  ";

        model.SaveName.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe(("rename", "Study PC"));
        model.NameMessage.ShouldBeNull();
    }

    [Fact]
    public void A_refused_rename_shows_why()
    {
        _link.Connect(true);
        _link.HouseholdAnswer = new HouseholdOutcome(false, "Names are 1 to 40 characters.");
        var model = Model();
        model.NameInput = "Study PC";

        model.SaveName.Execute(null);

        model.NameMessage.ShouldBe("Names are 1 to 40 characters.");
    }

    [Fact]
    public void Removing_a_pc_asks_first_and_names_it()
    {
        _link.Status = InHousehold("aaaa");
        _link.Connect(true);
        _history.Answer = _ => SnapshotWith([
            new HouseholdMemberRow("aaaa", "Desktop-1", ChassisKind.Desktop, Now.AddDays(-30), null, Now),
            new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now.AddDays(-10), null, Now),
        ]);
        var model = Model();
        model.Show();
        var other = model.Members.Single(m => m.DeviceId == "bbbb");

        model.AskRemove.Execute(other);

        model.IsConfirming.ShouldBeTrue();
        model.ConfirmText.ShouldNotBeNull().ShouldContain("Laptop-2");
        _link.HouseholdRequests.ShouldBeEmpty();            // asked, not yet sent
    }

    [Fact]
    public void This_pc_cannot_be_asked_to_remove_itself()
    {
        _link.Status = InHousehold("aaaa");
        _link.Connect(true);
        _history.Answer = _ => SnapshotWith([new HouseholdMemberRow("aaaa", "Desktop-1", ChassisKind.Desktop, Now.AddDays(-30), null, Now)]);
        var model = Model();
        model.Show();
        var self = model.Members.Single();

        model.AskRemove.Execute(self);

        model.IsConfirming.ShouldBeFalse();
    }

    [Fact]
    public void Confirming_a_removal_sends_it_and_reads_the_list_again()
    {
        _link.Status = InHousehold("aaaa");
        _link.Connect(true);
        _history.Answer = _ => SnapshotWith([
            new HouseholdMemberRow("aaaa", "Desktop-1", ChassisKind.Desktop, Now.AddDays(-30), null, Now),
            new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now.AddDays(-10), null, Now),
        ]);
        var model = Model();
        model.Show();
        var readsBefore = _history.Reads.Count;
        model.AskRemove.Execute(model.Members.Single(m => m.DeviceId == "bbbb"));

        model.ConfirmPending.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe(("remove", "bbbb"));
        model.IsConfirming.ShouldBeFalse();
        _history.Reads.Count.ShouldBeGreaterThan(readsBefore);
    }

    [Fact]
    public void Cancelling_a_removal_sends_nothing()
    {
        _link.Status = InHousehold("aaaa");
        _link.Connect(true);
        _history.Answer = _ => SnapshotWith([
            new HouseholdMemberRow("aaaa", "Desktop-1", ChassisKind.Desktop, Now.AddDays(-30), null, Now),
            new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now.AddDays(-10), null, Now),
        ]);
        var model = Model();
        model.Show();
        model.AskRemove.Execute(model.Members.Single(m => m.DeviceId == "bbbb"));

        model.CancelPending.Execute(null);

        model.IsConfirming.ShouldBeFalse();
        _link.HouseholdRequests.ShouldBeEmpty();
    }

    [Fact]
    public void Leaving_asks_first_then_sends_it_once_confirmed()
    {
        _link.Status = InHousehold();
        _link.Connect(true);
        var model = Model();
        model.Show();

        model.AskLeave.Execute(null);
        model.IsConfirming.ShouldBeTrue();
        model.ConfirmText.ShouldNotBeNull().ShouldContain("Leave");
        _link.HouseholdRequests.ShouldBeEmpty();

        model.ConfirmPending.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe("leave");
        model.IsConfirming.ShouldBeFalse();
    }

    [Fact]
    public void A_refused_leave_shows_why_and_stops_confirming()
    {
        _link.Status = InHousehold();
        _link.Connect(true);
        _link.HouseholdAnswer = new HouseholdOutcome(false, "The service didn't answer, so the change may not have been made.");
        var model = Model();
        model.Show();

        model.AskLeave.Execute(null);
        model.ConfirmPending.Execute(null);

        model.IsConfirming.ShouldBeFalse();
        model.ActionMessage.ShouldBe("The service didn't answer, so the change may not have been made.");
    }
}
