using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class JoinPromptViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeLink _link = new();

    private JoinPromptViewModel Model(HouseholdNotice notice) => new(_link, UiThreads.Inline, _clock, notice);

    private static HouseholdNotice Notice(string? comparisonCode = "482 913", DateTimeOffset? expiresAt = null, string? fromName = "Desktop-7")
        => new(NoticeKind.JoinPrompt, "prompt-1", "", fromName, comparisonCode, expiresAt ?? Now.AddMinutes(2));

    [Fact]
    public void The_heading_names_the_household_and_shows_the_comparison_code_from_pairing_on_the_network()
    {
        var model = Model(Notice(comparisonCode: "482 913", fromName: "Desktop-7"));

        model.Heading.ShouldBe("Join Desktop-7's household?");
        model.HasComparisonLine.ShouldBeTrue();
        model.ComparisonLine.ShouldBe("Its code is 482 913. Check it matches the code on Desktop-7.");
    }

    [Fact]
    public void A_pairing_by_code_has_no_comparison_line()
    {
        var model = Model(Notice(comparisonCode: null));

        model.HasComparisonLine.ShouldBeFalse();
        model.ComparisonLine.ShouldBeNull();
    }

    [Fact]
    public void With_no_name_the_heading_still_reads_sensibly()
    {
        var model = Model(Notice(fromName: null));
        model.Heading.ShouldBe("Join the other PC's household?");
    }

    [Fact]
    public void A_pc_already_in_a_household_is_warned_that_joining_leaves_it()
    {
        _link.Status = Statuses.Running() with { Household = new HouseholdStatus("hh1", "aaaa", "This-PC", ChassisKind.Desktop, true, [], null) };
        _link.Connect(true);

        var model = Model(Notice());

        model.ShowLeaveWarning.ShouldBeTrue();
    }

    [Fact]
    public void A_pc_in_no_household_gets_no_leave_warning()
    {
        _link.Status = Statuses.Running() with { Household = new HouseholdStatus(null, "aaaa", "This-PC", ChassisKind.Desktop, true, [], null) };
        _link.Connect(true);

        var model = Model(Notice());

        model.ShowLeaveWarning.ShouldBeFalse();
    }

    [Fact]
    public void Join_sends_accept_for_this_prompt_and_closes()
    {
        _link.Connect(true);
        var model = Model(Notice());
        var closed = 0;
        model.Closed += () => closed++;

        model.Join.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe(("prompt-1", true));
        closed.ShouldBe(1);
    }

    [Fact]
    public void Dont_join_sends_refuse_for_this_prompt_and_closes()
    {
        _link.Connect(true);
        var model = Model(Notice());
        var closed = 0;
        model.Closed += () => closed++;

        model.DontJoin.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe(("prompt-1", false));
        closed.ShouldBe(1);
    }

    [Fact]
    public void Not_answered_by_its_expiry_it_closes_itself_without_sending_anything()
    {
        var model = Model(Notice(expiresAt: Now.AddMinutes(2)));
        var closed = 0;
        model.Closed += () => closed++;

        _clock.Advance(TimeSpan.FromMinutes(2));

        closed.ShouldBe(1);
        _link.HouseholdRequests.ShouldBeEmpty();
    }

    [Fact]
    public void With_no_expiry_it_never_times_out_on_its_own()
    {
        var model = Model(new HouseholdNotice(NoticeKind.JoinPrompt, "prompt-1", "", "Desktop-7", "482 913", ExpiresAt: null));
        var closed = 0;
        model.Closed += () => closed++;

        _clock.Advance(TimeSpan.FromHours(1));

        closed.ShouldBe(0);
    }
}
