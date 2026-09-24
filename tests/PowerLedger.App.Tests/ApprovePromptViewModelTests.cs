using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ApprovePromptViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeLink _link = new();

    private ApprovePromptViewModel Model(HouseholdNotice notice) => new(_link, UiThreads.Inline, _clock, notice);

    private static HouseholdNotice Notice(string text, DateTimeOffset? expiresAt = null, string? comparisonCode = "482 913")
        => new(NoticeKind.ApprovePrompt, "prompt-2", text, null, comparisonCode, expiresAt ?? Now.AddMinutes(2));

    [Fact]
    public void The_heading_is_the_services_own_wording_when_the_pc_signed_in_as_this_account()
    {
        var model = Model(Notice("A PC signed in as you asks to join your household. Approve it?"));
        model.Heading.ShouldBe("A PC signed in as you asks to join your household. Approve it?");
    }

    [Fact]
    public void The_heading_is_the_services_own_wording_without_a_signed_in_pc()
    {
        var model = Model(Notice("A PC asks to join your household. Approve it?"));
        model.Heading.ShouldBe("A PC asks to join your household. Approve it?");
    }

    /// <summary>Review finding A2: the approval code is shown prominently, so the user can compare it against what the
    /// approved PC shows, before approving.</summary>
    [Fact]
    public void The_approval_code_shows_prominently_so_it_can_be_checked_against_the_other_pc()
    {
        var model = Model(Notice("A PC asks to join your household. Approve it?", comparisonCode: "482 913"));

        model.HasComparisonCode.ShouldBeTrue();
        model.ComparisonCode.ShouldBe("482 913");
        model.ComparisonCaption.ShouldBe("Check the other PC shows this code");
    }

    [Fact]
    public void Approve_sends_accept_for_this_prompt_and_closes()
    {
        _link.Connect(true);
        var model = Model(Notice("A PC asks to join your household. Approve it?"));
        var closed = 0;
        model.Closed += () => closed++;

        model.Approve.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe(("prompt-2", true));
        closed.ShouldBe(1);
    }

    [Fact]
    public void Dont_approve_still_sends_answer_prompt_request_with_accept_false()
    {
        _link.Connect(true);
        var model = Model(Notice("A PC asks to join your household. Approve it?"));
        var closed = 0;
        model.Closed += () => closed++;

        model.DontApprove.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe(("prompt-2", false));
        closed.ShouldBe(1);
    }

    [Fact]
    public void Not_answered_by_its_expiry_it_closes_itself_without_sending_anything()
    {
        var model = Model(Notice("A PC asks to join your household. Approve it?", expiresAt: Now.AddMinutes(2)));
        var closed = 0;
        model.Closed += () => closed++;

        _clock.Advance(TimeSpan.FromMinutes(2));

        closed.ShouldBe(1);
        _link.HouseholdRequests.ShouldBeEmpty();
    }

    /// <summary>Service round, review: only the first answer counts, so a fast double-click can never send two.</summary>
    [Fact]
    public void A_second_click_after_answering_sends_nothing_more()
    {
        _link.Connect(true);
        var model = Model(Notice("A PC asks to join your household. Approve it?"));

        model.Approve.Execute(null);
        model.DontApprove.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe(("prompt-2", true));
    }

    /// <summary>Service round, review: this PC never shows two windows for one request — replacing the old one stops
    /// its timer, so it can never answer late once it is no longer showing.</summary>
    [Fact]
    public void Stop_prevents_a_late_timeout_from_answering()
    {
        var model = Model(Notice("A PC asks to join your household. Approve it?", expiresAt: Now.AddMinutes(2)));
        var closed = 0;
        model.Closed += () => closed++;

        model.Stop();
        _clock.Advance(TimeSpan.FromMinutes(2));

        closed.ShouldBe(0);
        _link.HouseholdRequests.ShouldBeEmpty();
    }
}
