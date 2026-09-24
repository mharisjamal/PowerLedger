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

    /// <summary>Review finding A10: the code is shown separately and large, not folded into a sentence, since the
    /// service no longer puts it in the text.</summary>
    [Fact]
    public void The_heading_is_the_services_own_wording_and_the_code_is_shown_separately()
    {
        var model = Model(Notice(text: "Join Desktop-7's household?", comparisonCode: "482 913", fromName: "Desktop-7"));

        model.Heading.ShouldBe("Join Desktop-7's household?");
        model.HasComparisonCode.ShouldBeTrue();
        model.ComparisonCode.ShouldBe("482 913");
        model.ComparisonCaption.ShouldBe("Check Desktop-7 shows this code");
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
    public void A_pairing_by_code_has_no_comparison_code_to_show()
    {
        var model = Model(Notice(comparisonCode: null));

        model.HasComparisonCode.ShouldBeFalse();
        model.ComparisonCode.ShouldBeNull();
        model.ComparisonCaption.ShouldBeNull();
    }

    [Fact]
    public void With_no_name_the_caption_still_reads_sensibly()
    {
        var model = Model(Notice(fromName: null));
        model.ComparisonCode.ShouldBe("482 913");
        model.ComparisonCaption.ShouldBe("Check the other PC shows this code");
    }

    /// <summary>Service gap C reported: a code pairing's JoinPrompt has no FromName at all (Prompts.cs words Text for
    /// this itself); the heading must show the service's own no-name sentence with no "null" or empty quotes anywhere,
    /// and there is no comparison code to show either.</summary>
    [Fact]
    public void A_code_pairings_join_prompt_has_no_name_and_shows_no_null_or_empty_quotes()
    {
        var model = Model(Notice(text: "Join the household of the PC that made this code?", comparisonCode: null, fromName: null));

        model.Heading.ShouldBe("Join the household of the PC that made this code?");
        model.Heading.ShouldNotContain("null");
        model.Heading.ShouldNotContain("\"\"");
        model.HasComparisonCode.ShouldBeFalse();
        model.ComparisonCode.ShouldBeNull();
        model.ComparisonCaption.ShouldBeNull();
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
