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

    private static HouseholdNotice Notice(
        string text = "Join Desktop-7's household?", string? comparisonCode = "482 913", DateTimeOffset? expiresAt = null, string? fromName = "Desktop-7")
        => new(NoticeKind.JoinPrompt, "prompt-1", text, fromName, comparisonCode, expiresAt ?? Now.AddMinutes(2));

    [Fact]
    public void The_heading_is_the_services_own_wording_and_shows_the_comparison_code_from_pairing_on_the_network()
    {
        var model = Model(Notice(text: "Join Desktop-7's household?", comparisonCode: "482 913", fromName: "Desktop-7"));

        model.Heading.ShouldBe("Join Desktop-7's household?");
        model.HasComparisonLine.ShouldBeTrue();
        model.ComparisonLine.ShouldBe("Its code is 482 913. Check it matches the code on Desktop-7.");
    }

    /// <summary>The service already appends the leave warning to Text when this PC belongs to another household; the
    /// App shows it as given rather than rebuilding it from its own read of the status.</summary>
    [Fact]
    public void The_leave_warning_is_shown_exactly_as_the_service_words_it()
    {
        var model = Model(Notice(text: "Join Desktop-7's household? Joining leaves the household this PC is in now."));

        model.Heading.ShouldBe("Join Desktop-7's household? Joining leaves the household this PC is in now.");
    }

    [Fact]
    public void A_pairing_by_code_has_no_comparison_line()
    {
        var model = Model(Notice(comparisonCode: null));

        model.HasComparisonLine.ShouldBeFalse();
        model.ComparisonLine.ShouldBeNull();
    }

    [Fact]
    public void With_no_name_the_comparison_line_still_reads_sensibly()
    {
        var model = Model(Notice(fromName: null));
        model.ComparisonLine.ShouldBe("Its code is 482 913. Check it matches the code on the other PC.");
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
        var model = Model(new HouseholdNotice(NoticeKind.JoinPrompt, "prompt-1", "Join Desktop-7's household?", "Desktop-7", "482 913", ExpiresAt: null));
        var closed = 0;
        model.Closed += () => closed++;

        _clock.Advance(TimeSpan.FromHours(1));

        closed.ShouldBe(0);
    }
}
