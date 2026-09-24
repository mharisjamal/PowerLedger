using System.Collections.Concurrent;
using System.Security.Cryptography;
using PowerLedger.Contracts;
using PowerLedger.Service.Households.Lan;

namespace PowerLedger.Service.Households;

/// <summary>
/// Asks the user at the screen (households design §3, §7): a prompt goes as a pushed <see cref="HouseholdNotice"/> to the App
/// in the console session, and waits for its <see cref="AnswerPromptRequest"/>. For pairing, nobody there means no at once,
/// and no answer in two minutes means no. N2's prompts have no answer instead (plan 0.9): one that closes unanswered comes
/// back at a later turn, and never counts as a no. A prompt whose token is cancelled, as when its connection has gone or
/// the other side cancelled, is withdrawn: a <see cref="NoticeKind.Withdraw"/> notice names it, so the App closes it.
/// </summary>
internal sealed class HouseholdPrompts(NoticeHub notices, TimeProvider clock) : IPromptBroker
{
    public static readonly TimeSpan Timeout = PairingTimeouts.Prompt;

    /// <summary>What a <see cref="NoticeKind.Withdraw"/> notice says.</summary>
    internal const string Withdrawn = "That question has closed.";

    /// <summary>Said to a PC already in a household: entering another takes the old one's rows off it (households design §6).</summary>
    internal const string LeavesWarning = "Joining leaves the household this PC is in now and removes that household's rows from this PC.";

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _open = new(StringComparer.Ordinal);

    /// <summary>How many prompts wait for an answer now.</summary>
    public int Open => _open.Count;

    public Task<bool> AskToJoinAsync(JoinQuestion question, CancellationToken cancel)
    {
        var text = question.FromName is null ? "Join the household of the PC that made this code?" : $"Join {question.FromName}'s household?";
        if (question.LeavesHousehold) text += " " + LeavesWarning;
        return AskAsync(NoticeKind.JoinPrompt, text, question.FromName, question.ComparisonCode, cancel);
    }

    /// <summary>The adding PC's own check (plan 0.8), answered with Codes match or Cancel.</summary>
    public Task<bool> ConfirmCodeAsync(string otherName, string code, CancellationToken cancel) =>
        AskAsync(NoticeKind.ConfirmCode, $"Does {otherName} show {code}?", otherName, code, cancel);

    /// <summary>N2: a PC asks to join, signed in as this PC's own account when <paramref name="asYou"/>, else as another linked
    /// to the household. The server knows it only by its keys, so it has no name yet; <paramref name="code"/> is the approval
    /// code (plan 0.9), which the PC asking shows at the same time.</summary>
    /// <returns>Null when it wasn't answered: it comes back at a later turn.</returns>
    public Task<bool?> AskToApproveAsync(bool asYou, string code, CancellationToken cancel) => AskOrNotAsync(
        NoticeKind.ApprovePrompt,
        asYou ? "A PC signed in as you asks to join your household. Approve it?" : "A PC asks to join your household. Approve it?",
        null, code, cancel);

    /// <summary>N2: a member is about to approve this PC (plan 0.9). Before anything is sealed, its user checks that PC
    /// shows the same code: a server that put in keys of its own would make the two differ.</summary>
    /// <returns>Null when it wasn't answered: it comes back at a later turn.</returns>
    public Task<bool?> ConfirmJoinAsync(string code, CancellationToken cancel) =>
        AskOrNotAsync(NoticeKind.ConfirmJoin, $"Does your other PC show {code}? Approve it there too.", null, code, cancel);

    /// <summary>The user's answer to an open prompt.</summary>
    /// <returns>False when no prompt of that ID waits: it was answered, ran out, or never was.</returns>
    public bool Answer(string promptId, bool accept) => _open.TryRemove(promptId, out var waiting) && waiting.TrySetResult(accept);

    private async Task<bool> AskAsync(NoticeKind kind, string text, string? fromName, string? code, CancellationToken cancel) =>
        await AskOrNotAsync(kind, text, fromName, code, cancel).ConfigureAwait(false) ?? false;

    /// <summary>The user's answer; null when nobody is at the screen, no answer came in time, or the prompt was withdrawn.</summary>
    private async Task<bool?> AskOrNotAsync(NoticeKind kind, string text, string? fromName, string? code, CancellationToken cancel)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _open[id] = answer;
        try
        {
            if (!notices.Publish(new HouseholdNotice(kind, id, text, fromName, code, clock.GetUtcNow() + Timeout))) return null;
            return await answer.Task.WaitAsync(Timeout, clock, cancel).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (_open.TryRemove(id, out _)) notices.Publish(new HouseholdNotice(NoticeKind.Withdraw, id, Withdrawn, fromName, null, null));
            return null;
        }
        finally
        {
            _open.TryRemove(id, out _);
        }
    }
}
