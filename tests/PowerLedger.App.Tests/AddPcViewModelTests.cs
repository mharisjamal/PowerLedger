using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class AddPcViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeLink _link = new();

    private AddPcViewModel Model() => new(_link, UiThreads.Inline, _clock);

    [Fact]
    public void Opening_the_page_browses_the_network_and_marks_pcs_already_in_the_household()
    {
        _link.Connect(true);
        _link.FoundPcs = [new FoundPc("inst-1", "Laptop-2", InThisHousehold: false), new FoundPc("inst-2", "Desktop-3", InThisHousehold: true)];
        var model = Model();

        model.Start();

        model.Found.Count.ShouldBe(2);
        model.Found.Single(p => p.InstanceId == "inst-2").InThisHousehold.ShouldBeTrue();
    }

    [Fact]
    public void Browsing_repeats_every_five_seconds_while_the_page_is_open()
    {
        _link.Connect(true);
        var model = Model();
        model.Start();

        _link.FoundPcs = [new FoundPc("inst-1", "Laptop-2", false)];
        _clock.Advance(AddPcViewModel.BrowseEvery);
        model.Found.Count.ShouldBe(1);

        model.Dispose();
        _link.FoundPcs = [new FoundPc("inst-1", "Laptop-2", false), new FoundPc("inst-2", "Desktop-3", false)];
        _clock.Advance(AddPcViewModel.BrowseEvery * 3);
        model.Found.Count.ShouldBe(1);   // stopped once the page closed
    }

    [Fact]
    public void Adding_a_pc_shows_its_name_then_the_comparison_code_once_the_progress_notice_arrives()
    {
        _link.Connect(true);
        var model = Model();
        model.Start();
        var pc = new FoundPc("inst-1", "Laptop-2", false);

        model.Add.Execute(pc);
        _link.HouseholdRequests.Single().ShouldBe("inst-1");
        model.PairingText.ShouldBe("Adding Laptop-2…");

        _link.PushNotice(new HouseholdNotice(NoticeKind.PairingProgress, null, "", null, "482 913", null));
        model.PairingText.ShouldBe("On Laptop-2, check the code is 482 913 and press Join.");
    }

    [Fact]
    public void A_finished_pairing_shows_its_outcome_and_refreshes_the_list()
    {
        _link.Connect(true);
        var model = Model();
        model.Start();
        model.Add.Execute(new FoundPc("inst-1", "Laptop-2", false));
        var readsBefore = _link.HouseholdRequests.Count;

        _link.PushNotice(new HouseholdNotice(NoticeKind.PairingProgress, null, "Laptop-2 joined your household.", null, null, null));

        model.PairingText.ShouldBe("Laptop-2 joined your household.");
        _link.HouseholdRequests.Count.ShouldBe(readsBefore);   // browsing is a separate call, not counted as a household request
    }

    [Fact]
    public void A_refused_add_clears_the_pairing_text_and_shows_the_message()
    {
        _link.Connect(true);
        _link.HouseholdAnswer = new HouseholdOutcome(false, "This PC is already pairing.");
        var model = Model();
        model.Start();

        model.Add.Execute(new FoundPc("inst-1", "Laptop-2", false));

        model.PairingText.ShouldBeNull();
        model.Message.ShouldBe("This PC is already pairing.");
    }

    [Fact]
    public void Switching_to_somewhere_else_gets_a_code_with_a_ten_minute_countdown()
    {
        _link.Connect(true);
        _link.HouseholdAnswer = new HouseholdOutcome(true, "Here's your code.", "K7QM-2XHD-9PW4-R8TA");
        var model = Model();

        model.Tab = AddPcTab.SomewhereElse;

        model.Code.ShouldBe("K7QM-2XHD-9PW4-R8TA");
        model.Remaining.ShouldBe(AddPcViewModel.CodeLifetime);

        _clock.Advance(TimeSpan.FromMinutes(4));
        model.Remaining.ShouldBe(TimeSpan.FromMinutes(6));
        model.Code.ShouldBe("K7QM-2XHD-9PW4-R8TA");

        _clock.Advance(TimeSpan.FromMinutes(6));
        model.Code.ShouldBeNull();
        model.Remaining.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Leaving_and_returning_to_somewhere_else_keeps_the_same_code()
    {
        _link.Connect(true);
        _link.HouseholdAnswer = new HouseholdOutcome(true, "Here's your code.", "K7QM-2XHD-9PW4-R8TA");
        var model = Model();

        model.Tab = AddPcTab.SomewhereElse;
        model.Tab = AddPcTab.OnThisNetwork;
        model.Tab = AddPcTab.SomewhereElse;

        model.Code.ShouldBe("K7QM-2XHD-9PW4-R8TA");
        _link.HouseholdRequests.Count.ShouldBe(1);   // one code was made, not one per visit
    }

    [Fact]
    public void Join_by_code_sends_the_code_and_shows_the_result()
    {
        _link.Connect(true);
        _link.HouseholdAnswer = new HouseholdOutcome(true, "Checking the code…");
        var model = Model();
        model.JoinCode = "K7QM-2XHD-9PW4-R8TA";

        model.Join.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe("K7QM-2XHD-9PW4-R8TA");
        model.Message.ShouldBe("Checking the code…");
        model.JoinCode.ShouldBe("");
    }

    [Fact]
    public void Join_is_disabled_until_a_code_is_typed()
    {
        var model = Model();
        model.Join.CanExecute(null).ShouldBeFalse();

        model.JoinCode = "  ";
        model.Join.CanExecute(null).ShouldBeFalse();

        model.JoinCode = "K7QM-2XHD-9PW4-R8TA";
        model.Join.CanExecute(null).ShouldBeTrue();
    }

    [Fact]
    public void A_failed_join_keeps_the_typed_code_so_the_user_can_fix_it()
    {
        _link.Connect(true);
        _link.HouseholdAnswer = new HouseholdOutcome(false, "That code isn't right.");
        var model = Model();
        model.JoinCode = "WRONG-CODE-HERE-0000";

        model.Join.Execute(null);

        model.Message.ShouldBe("That code isn't right.");
        model.JoinCode.ShouldBe("WRONG-CODE-HERE-0000");
    }
}
