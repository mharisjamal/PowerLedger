using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ConfirmJoinViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeLink _link = new();

    private ConfirmJoinViewModel Model(HouseholdNotice notice) => new(_link, UiThreads.Inline, _clock, notice);

    private static HouseholdNotice Notice(string? comparisonCode = "482 913", DateTimeOffset? expiresAt = null)
        => new(NoticeKind.ConfirmJoin, "prompt-3", "Your household approved this PC. Does the approving PC show this code?", null, comparisonCode,
            expiresAt ?? Now.AddMinutes(2));

    [Fact]
    public void The_heading_is_the_services_own_wording_and_the_code_shows_prominently()
    {
        var model = Model(Notice());

        model.Heading.ShouldBe("Your household approved this PC. Does the approving PC show this code?");
        model.HasComparisonCode.ShouldBeTrue();
        model.ComparisonCode.ShouldBe("482 913");
    }

    [Fact]
    public void Codes_match_sends_accept_for_this_prompt_and_closes()
    {
        _link.Connect(true);
        var model = Model(Notice());
        var closed = 0;
        model.Closed += () => closed++;

        model.CodesMatch.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe(("prompt-3", true));
        closed.ShouldBe(1);
    }

    [Fact]
    public void Cancel_sends_refuse_for_this_prompt_and_closes()
    {
        _link.Connect(true);
        var model = Model(Notice());
        var closed = 0;
        model.Closed += () => closed++;

        model.Cancel.Execute(null);

        _link.HouseholdRequests.Single().ShouldBe(("prompt-3", false));
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
}
