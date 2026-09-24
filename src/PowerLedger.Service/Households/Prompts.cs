using System.Collections.Concurrent;
using System.Security.Cryptography;
using PowerLedger.Contracts;
using PowerLedger.Service.Households.Lan;

namespace PowerLedger.Service.Households;

/// <summary>
/// Asks the user at the screen (households design §3, §7): a prompt goes as a pushed <see cref="HouseholdNotice"/> to the App
/// in the console session, and waits for its <see cref="AnswerPromptRequest"/>. Nobody there means no at once; no answer
/// in two minutes means no. A prompt whose token is cancelled, as when its connection has gone or the other side
/// cancelled, is withdrawn: a <see cref="NoticeKind.Withdraw"/> notice names it, so the App closes it, and the answer is no.
/// </summary>
internal sealed class HouseholdPrompts(NoticeHub notices, TimeProvider clock) : IPromptBroker
{
    public static readonly TimeSpan Timeout = PairingTimeouts.Prompt;

    /// <summary>What a <see cref="NoticeKind.Withdraw"/> notice says.</summary>
    internal const string Withdrawn = "That question has closed.";

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _open = new(StringComparer.Ordinal);

    /// <summary>How many prompts wait for an answer now.</summary>
    public int Open => _open.Count;

    public Task<bool> AskToJoinAsync(JoinQuestion question, CancellationToken cancel)
    {
        var text = question.ComparisonCode is { } code
            ? $"Join {question.FromName}'s household? Its code is {code}. Check it matches the code on {question.FromName}."
            : $"Join {question.FromName}'s household?";
        if (question.LeavesHousehold) text += " Joining leaves the household this PC is in now.";
        return AskAsync(NoticeKind.JoinPrompt, text, question.FromName, question.ComparisonCode, cancel);
    }

    /// <summary>The adding PC's own check (plan 0.8), answered with Codes match or Cancel.</summary>
    public Task<bool> ConfirmCodeAsync(string otherName, string code, CancellationToken cancel) =>
        AskAsync(NoticeKind.ConfirmCode, $"Does {otherName} show {code}?", otherName, code, cancel);

    /// <summary>N2: a PC asks to join, signed in as this PC's own account when <paramref name="asYou"/>, else as another linked
    /// to the household. The server knows it only by its keys, so it has no name yet.</summary>
    public Task<bool> AskToApproveAsync(bool asYou, CancellationToken cancel) => AskAsync(
        NoticeKind.ApprovePrompt,
        asYou ? "A PC signed in as you asks to join your household. Approve it?" : "A PC asks to join your household. Approve it?",
        null, null, cancel);

    /// <summary>The user's answer to an open prompt.</summary>
    /// <returns>False when no prompt of that ID waits: it was answered, ran out, or never was.</returns>
    public bool Answer(string promptId, bool accept) => _open.TryRemove(promptId, out var waiting) && waiting.TrySetResult(accept);

    private async Task<bool> AskAsync(NoticeKind kind, string text, string? fromName, string? code, CancellationToken cancel)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _open[id] = answer;
        try
        {
            if (!notices.Publish(new HouseholdNotice(kind, id, text, fromName, code, clock.GetUtcNow() + Timeout))) return false;
            return await answer.Task.WaitAsync(Timeout, clock, cancel).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (_open.TryRemove(id, out _)) notices.Publish(new HouseholdNotice(NoticeKind.Withdraw, id, Withdrawn, fromName, null, null));
            return false;
        }
        finally
        {
            _open.TryRemove(id, out _);
        }
    }
}
