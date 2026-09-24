using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// A pushed <see cref="NoticeKind.ApprovePrompt"/> (households design §7, Plan N task A7): a PC signed in as this
/// account asks to join the household. The question is <see cref="HouseholdNotice.Text"/> exactly as the service words
/// it, such as "A PC signed in as you asks to join your household. Approve it?"; it carries no name. Approve and Don't
/// approve both send the answer and close — a refusal is sent just as deliberately as an approval — and not answered by
/// the notice's expiry, it closes itself, as the Join prompt does (households design §3).
/// </summary>
internal sealed class ApprovePromptViewModel : ObservableObject
{
    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly string _promptId;
    private ITimer? _timer;

    public ApprovePromptViewModel(IServiceLink link, UiThreads threads, TimeProvider clock, HouseholdNotice notice)
    {
        _link = link;
        _threads = threads;
        _promptId = notice.PromptId ?? "";
        Heading = notice.Text;
        ComparisonCode = notice.ComparisonCode;
        ComparisonCaption = notice.ComparisonCode is not null ? "Check the other PC shows this code" : null;
        Approve = new RelayCommand(() => _ = AnswerAsync(true));
        DontApprove = new RelayCommand(() => _ = AnswerAsync(false));
        if (notice.ExpiresAt is { } expires)
        {
            var delay = expires - clock.GetUtcNow();
            _timer = clock.CreateTimer(_ => threads.Post(TimedOut), null, delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    public string Heading { get; }

    /// <summary>The approver's own check (households design §7, review finding A2): "482 913", shown large, from the
    /// approved PC's key; null should the service ever send an ApprovePrompt with none.</summary>
    public string? ComparisonCode { get; }

    public string? ComparisonCaption { get; }

    public bool HasComparisonCode => ComparisonCode is not null;

    public IRelayCommand Approve { get; }

    public IRelayCommand DontApprove { get; }

    /// <summary>The prompt was answered, or its time ran out: the window closes.</summary>
    public event Action? Closed;

    private async Task AnswerAsync(bool accept)
    {
        _timer?.Dispose();
        await _link.AnswerPromptAsync(_promptId, accept).ConfigureAwait(false);
        _threads.Post(() => Closed?.Invoke());
    }

    private void TimedOut() => Closed?.Invoke();
}
