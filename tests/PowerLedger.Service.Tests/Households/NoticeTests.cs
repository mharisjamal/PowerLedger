using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Service.Households;
using PowerLedger.Service.Households.Lan;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>Notices go only to the App at the screen (households design §9), and prompts wait for its answer.</summary>
public sealed class NoticeTests
{
    private static readonly HouseholdNotice Hello = new(NoticeKind.Info, null, "Laptop-2 is no longer in the household.", null, null, null);

    [Fact]
    public void A_notice_goes_to_clients_in_the_console_session_only()
    {
        var console = 2u;
        var hub = new NoticeHub(() => console);
        var atTheScreen = hub.Subscribe(2);
        var elsewhere = hub.Subscribe(3);

        hub.Publish(Hello).ShouldBeTrue();

        atTheScreen.TryRead(out var got).ShouldBeTrue();
        got.ShouldBe(Hello);
        elsewhere.TryRead(out _).ShouldBeFalse();
        hub.AnyoneAtTheScreen.ShouldBeTrue();

        console = 3;
        hub.Unsubscribe(elsewhere);
        hub.AnyoneAtTheScreen.ShouldBeFalse();
        hub.Publish(Hello).ShouldBeFalse();
        console = NoticeHub.NoSession;
        hub.Publish(Hello).ShouldBeFalse();
    }

    [Fact]
    public async Task With_nobody_at_the_screen_a_join_prompt_is_refused_at_once()
    {
        var prompts = new HouseholdPrompts(new NoticeHub(() => 1), new FakeTimeProvider());

        (await prompts.AskToJoinAsync(new JoinQuestion("Desktop-7", "482 913", false), CancellationToken.None)).ShouldBeFalse();
        prompts.Open.ShouldBe(0);
    }

    [Fact]
    public async Task A_join_prompt_shows_the_code_and_the_warning_and_waits_for_the_answer()
    {
        var clock = new FakeTimeProvider();
        var hub = new NoticeHub(() => 1);
        var app = hub.Subscribe(1);
        var prompts = new HouseholdPrompts(hub, clock);

        var asking = prompts.AskToJoinAsync(new JoinQuestion("Desktop-7", "482 913", LeavesHousehold: true), CancellationToken.None);

        var notice = await app.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        notice.Kind.ShouldBe(NoticeKind.JoinPrompt);
        notice.Text.ShouldBe(
            "Join Desktop-7's household? Its code is 482 913. Check it matches the code on Desktop-7. Joining leaves the household this PC is in now.");
        (notice.FromName, notice.ComparisonCode, notice.ExpiresAt).ShouldBe(("Desktop-7", "482 913", (DateTimeOffset?)clock.GetUtcNow().AddMinutes(2)));
        prompts.Answer("not-the-id", true).ShouldBeFalse();
        prompts.Answer(notice.PromptId!, true).ShouldBeTrue();
        (await asking).ShouldBeTrue();
        prompts.Answer(notice.PromptId!, false).ShouldBeFalse();                 // answered already
    }

    [Fact]
    public async Task A_prompt_nobody_answers_in_two_minutes_is_dont_join()
    {
        var clock = new FakeTimeProvider();
        var hub = new NoticeHub(() => 1);
        var app = hub.Subscribe(1);
        var prompts = new HouseholdPrompts(hub, clock);

        var asking = prompts.AskToJoinAsync(new JoinQuestion("Desktop-7", null, false), CancellationToken.None);
        var notice = await app.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        notice.Text.ShouldBe("Join Desktop-7's household?");
        clock.Advance(HouseholdPrompts.Timeout);

        (await asking.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeFalse();
        prompts.Answer(notice.PromptId!, true).ShouldBeFalse();
    }
}

/// <summary>One change to the household at a time, the worker's own work giving way to requests.</summary>
public sealed class HouseholdGateTests
{
    [Fact]
    public async Task A_request_cancels_the_background_work_holding_the_gate_and_goes_next()
    {
        var gate = new HouseholdGate();
        var background = await gate.EnterBackgroundAsync(CancellationToken.None);

        var request = gate.EnterAsync(CancellationToken.None);

        background.Attention.IsCancellationRequested.ShouldBeTrue();
        request.IsCompleted.ShouldBeFalse();
        background.Dispose();
        using (await request.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            var waiting = gate.EnterBackgroundAsync(CancellationToken.None);
            waiting.IsCompleted.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Background_work_that_starts_while_a_request_waits_gives_way_at_once()
    {
        var gate = new HouseholdGate();
        var first = await gate.EnterAsync(CancellationToken.None);
        var request = gate.EnterAsync(CancellationToken.None);
        var background = gate.EnterBackgroundAsync(CancellationToken.None);

        first.Dispose();
        var winner = await Task.WhenAny(request, background.ContinueWith(task => (IDisposable)task.Result));
        if (winner == request)
        {
            (await request).Dispose();
            (await background).Dispose();
        }
        else
        {
            var lease = await background;
            lease.Attention.IsCancellationRequested.ShouldBeTrue();
            lease.Dispose();
            (await request).Dispose();
        }
    }
}
