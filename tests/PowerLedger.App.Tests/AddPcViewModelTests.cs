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

    /// <summary>Review finding A5: a browse still in flight is not joined by a second one; the timer tick that would have
    /// started it is not lost, so a browse follows right behind once the first is done.</summary>
    [Fact]
    public async Task A_browse_still_in_flight_is_not_joined_by_a_second_one()
    {
        _link.Connect(true);
        _link.BrowseGate = new TaskCompletionSource<IReadOnlyList<FoundPc>?>();
        var model = Model();
        model.Start();
        _link.BrowseCalls.ShouldBe(1);

        _clock.Advance(AddPcViewModel.BrowseEvery);   // the tick while the first browse is still out
        _link.BrowseCalls.ShouldBe(1);                // not started again yet

        _link.BrowseGate.SetResult([new FoundPc("inst-1", "Laptop-2", false)]);
        await WaitFor.True(() => _link.BrowseCalls == 2);   // the missed tick's browse follows right behind
        model.Found.Count.ShouldBe(1);
    }

    /// <summary>Review finding A5: not connected reads as "can't look right now", never a "Looking…" that never resolves.</summary>
    [Fact]
    public void A_browse_that_cannot_be_answered_says_so_instead_of_looking_forever()
    {
        var model = Model();   // never connected

        model.Start();

        model.NoneFound.ShouldBeTrue();
        model.Message.ShouldBe("Couldn't look for PCs right now.");
    }

    [Fact]
    public void A_successful_browse_clears_any_earlier_cant_look_message()
    {
        var model = Model();
        model.Start();
        model.Message.ShouldNotBeNull();

        _link.Connect(true);
        _link.FoundPcs = [];
        _clock.Advance(AddPcViewModel.BrowseEvery);

        model.Message.ShouldBeNull();
    }

    [Fact]
    public void Adding_a_pc_shows_its_name_while_the_key_exchange_is_under_way()
    {
        _link.Connect(true);
        var model = Model();
        model.Start();
        var pc = new FoundPc("inst-1", "Laptop-2", false);

        model.Add.Execute(pc);

        _link.HouseholdRequests.Single().ShouldBe("inst-1");
        model.PairingText.ShouldBe("Adding Laptop-2…");
        model.IsConfirming.ShouldBeFalse();
    }

    /// <summary>Review finding A1: the adder's own check. The pairing is not done merely because AddPcRequest
    /// succeeded — Codes match must be pressed, which answers the ConfirmCode prompt.</summary>
    [Fact]
    public void A_confirm_code_notice_asks_the_adder_to_check_the_code_before_it_completes()
    {
        _link.Connect(true);
        var model = Model();
        model.Start();
        model.Add.Execute(new FoundPc("inst-1", "Laptop-2", false));

        _link.PushNotice(new HouseholdNotice(NoticeKind.ConfirmCode, "confirm-1", "Does Laptop-2 show 482 913?", "Laptop-2", "482 913", Now.AddMinutes(2)));

        model.IsConfirming.ShouldBeTrue();
        model.ConfirmQuestion.ShouldBe("Does Laptop-2 show 482 913?");
        model.ConfirmCode.ShouldBe("482 913");

        model.CodesMatch.Execute(null);

        var lastRequest = ((string PromptId, bool Accept))_link.HouseholdRequests.Last();
        lastRequest.ShouldBe(("confirm-1", true));
        model.IsConfirming.ShouldBeFalse();
    }

    [Fact]
    public void Cancelling_a_confirm_code_answers_it_as_not_matching()
    {
        _link.Connect(true);
        var model = Model();
        model.Start();
        model.Add.Execute(new FoundPc("inst-1", "Laptop-2", false));
        _link.PushNotice(new HouseholdNotice(NoticeKind.ConfirmCode, "confirm-1", "Does Laptop-2 show 482 913?", "Laptop-2", "482 913", Now.AddMinutes(2)));

        model.ConfirmCancel.Execute(null);

        var lastRequest = ((string PromptId, bool Accept))_link.HouseholdRequests.Last();
        lastRequest.ShouldBe(("confirm-1", false));
        model.IsConfirming.ShouldBeFalse();
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
        model.CanCancelPairing.ShouldBeFalse();
        _link.HouseholdRequests.Count.ShouldBe(readsBefore);   // browsing is a separate call, not counted as a household request
    }

    /// <summary>Review finding A4: a withdrawn confirm closes it without answering, since the connection or the pairing
    /// itself is already gone.</summary>
    [Fact]
    public void A_withdrawn_confirm_code_closes_it_without_sending_an_answer()
    {
        _link.Connect(true);
        var model = Model();
        model.Start();
        model.Add.Execute(new FoundPc("inst-1", "Laptop-2", false));
        _link.PushNotice(new HouseholdNotice(NoticeKind.ConfirmCode, "confirm-1", "Does Laptop-2 show 482 913?", "Laptop-2", "482 913", Now.AddMinutes(2)));
        var requestsBefore = _link.HouseholdRequests.Count;

        _link.PushNotice(new HouseholdNotice(NoticeKind.Withdraw, "confirm-1", "", null, null, null));

        model.IsConfirming.ShouldBeFalse();
        model.CanCancelPairing.ShouldBeFalse();
        _link.HouseholdRequests.Count.ShouldBe(requestsBefore);
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
        model.CanCancelPairing.ShouldBeFalse();
    }

    /// <summary>Review finding A3: Somewhere else no longer makes a code just by opening the tab.</summary>
    [Fact]
    public void Somewhere_else_makes_no_code_until_make_a_code_is_pressed()
    {
        _link.Connect(true);
        var model = Model();

        model.Tab = AddPcTab.SomewhereElse;

        model.Code.ShouldBeNull();
        _link.HouseholdRequests.ShouldBeEmpty();
    }

    [Fact]
    public void Make_a_code_gets_a_code_with_a_ten_minute_countdown()
    {
        _link.Connect(true);
        _link.HouseholdAnswer = new HouseholdOutcome(true, "Here's your code.", "K7QM-2XHD-9PW4-R8TA");
        var model = Model();
        model.Tab = AddPcTab.SomewhereElse;

        model.MakeCode.Execute(null);

        model.Code.ShouldBe("K7QM-2XHD-9PW4-R8TA");
        model.Remaining.ShouldBe(AddPcViewModel.CodeLifetime);
        model.CanCancelPairing.ShouldBeTrue();

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
        model.MakeCode.Execute(null);

        model.Tab = AddPcTab.OnThisNetwork;
        model.Tab = AddPcTab.SomewhereElse;

        model.Code.ShouldBe("K7QM-2XHD-9PW4-R8TA");
        _link.HouseholdRequests.Count.ShouldBe(1);   // one code was made, not one per visit
    }

    /// <summary>Review finding A3: an active pairing, here a code waiting for a joiner, can be called off.</summary>
    [Fact]
    public void Cancel_pairing_sends_cancel_pairing_request_and_clears_the_code()
    {
        _link.Connect(true);
        _link.HouseholdAnswer = new HouseholdOutcome(true, "Here's your code.", "K7QM-2XHD-9PW4-R8TA");
        var model = Model();
        model.Tab = AddPcTab.SomewhereElse;
        model.MakeCode.Execute(null);
        _link.HouseholdAnswer = new HouseholdOutcome(true, "Cancelled.");

        model.CancelPairing.Execute(null);

        _link.HouseholdRequests.Last().ShouldBe("cancelPairing");
        model.Code.ShouldBeNull();
        model.CanCancelPairing.ShouldBeFalse();
    }

    /// <summary>Review finding A3: closing the window (Dispose) cancels a pairing still under way.</summary>
    [Fact]
    public void Disposing_while_a_pairing_is_under_way_cancels_it()
    {
        _link.Connect(true);
        var model = Model();
        model.Start();
        model.Add.Execute(new FoundPc("inst-1", "Laptop-2", false));

        model.Dispose();

        _link.HouseholdRequests.ShouldContain("cancelPairing");
    }

    [Fact]
    public void Disposing_with_no_pairing_under_way_sends_nothing()
    {
        _link.Connect(true);
        var model = Model();
        model.Start();

        model.Dispose();

        _link.HouseholdRequests.ShouldBeEmpty();
    }

    [Fact]
    public void Join_by_code_sends_the_code_and_shows_the_result()
    {
        _link.Connect(true);
        _link.HouseholdAnswer = new HouseholdOutcome(true, "Checking the code…");
        var model = Model();
        model.Tab = AddPcTab.JoinWithCode;
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
        model.Tab = AddPcTab.JoinWithCode;
        model.JoinCode = "WRONG-CODE-HERE-0000";

        model.Join.Execute(null);

        model.Message.ShouldBe("That code isn't right.");
        model.JoinCode.ShouldBe("WRONG-CODE-HERE-0000");
    }
}
