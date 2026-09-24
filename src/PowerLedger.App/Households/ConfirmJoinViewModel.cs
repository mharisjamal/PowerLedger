using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// A pushed <see cref="NoticeKind.ConfirmJoin"/> (households design §7, task 0.8, Plan N review finding A2): this PC was
/// approved by another member, signed in as the same account; its user checks the approving PC showed the same code
/// before this PC actually joins. The question is <see cref="HouseholdNotice.Text"/> as the service words it; the code
/// is <see cref="HouseholdNotice.ComparisonCode"/>, shown large and on its own. Codes match and Cancel both send the
/// answer and close; not answered by the notice's expiry, it closes itself (households design §3).
/// </summary>
internal sealed class ConfirmJoinViewModel : ObservableObject
{
    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly string _promptId;
    private ITimer? _timer;

    public ConfirmJoinViewModel(IServiceLink link, UiThreads threads, TimeProvider clock, HouseholdNotice notice)
    {
        _link = link;
        _threads = threads;
        _promptId = notice.PromptId ?? "";
        Heading = notice.Text;
        ComparisonCode = notice.ComparisonCode;
        CodesMatch = new RelayCommand(() => _ = AnswerAsync(true));
        Cancel = new RelayCommand(() => _ = AnswerAsync(false));
        if (notice.ExpiresAt is { } expires)
        {
            var delay = expires - clock.GetUtcNow();
            _timer = clock.CreateTimer(_ => threads.Post(TimedOut), null, delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    public string Heading { get; }

    /// <summary>"482 913", worked out from the sealer's key (task 0.8); null should the service ever send none.</summary>
    public string? ComparisonCode { get; }

    public bool HasComparisonCode => ComparisonCode is not null;

    public IRelayCommand CodesMatch { get; }

    public IRelayCommand Cancel { get; }

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
