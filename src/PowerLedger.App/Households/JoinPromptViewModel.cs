using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// A pushed <see cref="NoticeKind.JoinPrompt"/> (households design §2, §3, Plan N task A3): "Join {name}'s household?",
/// the comparison code line for a pairing found on the network, and a warning when this PC already belongs to one, since
/// joining leaves it. Join and Don't join send the answer and close; not answered by the notice's expiry, it closes
/// itself, since the service already treats an unanswered prompt as declined (households design §3).
/// </summary>
internal sealed class JoinPromptViewModel : ObservableObject
{
    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly string _promptId;
    private ITimer? _timer;
    private bool _showLeaveWarning;

    public JoinPromptViewModel(IServiceLink link, UiThreads threads, TimeProvider clock, HouseholdNotice notice)
    {
        _link = link;
        _threads = threads;
        _promptId = notice.PromptId ?? "";
        FromName = notice.FromName ?? "the other PC";
        Heading = $"Join {FromName}'s household?";
        ComparisonLine = notice.ComparisonCode is { } code ? $"Its code is {code}. Check it matches the code on {FromName}." : null;
        Join = new RelayCommand(() => _ = AnswerAsync(true));
        DontJoin = new RelayCommand(() => _ = AnswerAsync(false));
        if (notice.ExpiresAt is { } expires)
        {
            var delay = expires - clock.GetUtcNow();
            _timer = clock.CreateTimer(_ => threads.Post(TimedOut), null, delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        _ = LoadHouseholdAsync();
    }

    public string FromName { get; }

    public string Heading { get; }

    /// <summary>"Its code is 482 913. Check it matches the code on {name}."; null for a pairing by code, which has none.</summary>
    public string? ComparisonLine { get; }

    public bool HasComparisonLine => ComparisonLine is not null;

    /// <summary>True while this PC already belongs to a household, so joining will leave it (households design §2).</summary>
    public bool ShowLeaveWarning { get => _showLeaveWarning; private set => SetProperty(ref _showLeaveWarning, value); }

    public IRelayCommand Join { get; }

    public IRelayCommand DontJoin { get; }

    /// <summary>The prompt was answered, or its time ran out: the window closes.</summary>
    public event Action? Closed;

    private async Task LoadHouseholdAsync()
    {
        var status = await _link.GetStatusAsync().ConfigureAwait(false);
        _threads.Post(() => ShowLeaveWarning = status?.Household?.HouseholdId is not null);
    }

    private async Task AnswerAsync(bool accept)
    {
        _timer?.Dispose();
        await _link.AnswerPromptAsync(_promptId, accept).ConfigureAwait(false);
        _threads.Post(() => Closed?.Invoke());
    }

    private void TimedOut() => Closed?.Invoke();
}
