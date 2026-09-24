using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// A pushed <see cref="NoticeKind.JoinPrompt"/> (households design §2, §3, Plan N task A3): the question is
/// <see cref="HouseholdNotice.Text"/> exactly as the service words it — it already ends with the leave warning when this
/// PC belongs to another household, so the App shows it as given rather than rebuilding it. The comparison code line is
/// separate, for a pairing found on the network. Join and Don't join send the answer and close; not answered by the
/// notice's expiry, it closes itself, since the service already treats an unanswered prompt as declined (households
/// design §3).
/// </summary>
internal sealed class JoinPromptViewModel : ObservableObject
{
    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly string _promptId;
    private ITimer? _timer;

    public JoinPromptViewModel(IServiceLink link, UiThreads threads, TimeProvider clock, HouseholdNotice notice)
    {
        _link = link;
        _threads = threads;
        _promptId = notice.PromptId ?? "";
        Heading = notice.Text;
        ComparisonCode = notice.ComparisonCode;
        ComparisonCaption = notice.ComparisonCode is not null ? $"Check {notice.FromName ?? "the other PC"} shows this code" : null;
        Join = new RelayCommand(() => _ = AnswerAsync(true));
        DontJoin = new RelayCommand(() => _ = AnswerAsync(false));
        if (notice.ExpiresAt is { } expires)
        {
            var delay = expires - clock.GetUtcNow();
            _timer = clock.CreateTimer(_ => threads.Post(TimedOut), null, delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The service's own wording, such as "Join Desktop-7's household?" or, when this PC already belongs to
    /// one, "Join Desktop-7's household? Joining leaves the household this PC is in now."</summary>
    public string Heading { get; }

    /// <summary>"482 913", shown large and on its own (review finding A10: the service no longer folds it into a
    /// sentence); null for a pairing by code, which has none.</summary>
    public string? ComparisonCode { get; }

    /// <summary>"Check {name} shows this code."; null exactly when <see cref="ComparisonCode"/> is.</summary>
    public string? ComparisonCaption { get; }

    public bool HasComparisonCode => ComparisonCode is not null;

    public IRelayCommand Join { get; }

    public IRelayCommand DontJoin { get; }

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
