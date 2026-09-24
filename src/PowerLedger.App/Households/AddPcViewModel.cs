using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>Add a PC's three tabs (households design §2, review finding A3).</summary>
internal enum AddPcTab
{
    OnThisNetwork,
    SomewhereElse,
    JoinWithCode,
}

/// <summary>
/// Add a PC (households design §3, §4, Plan N task A2, review findings A1/A3/A4/A5). On this network browses every 5 s
/// while the page is open, never starting a second browse while one is out and never leaving "Looking…" showing when the
/// link can't answer; each PC marked when it is already in this household. Add starts a pairing; once the key exchange
/// is done the adder itself must check a <see cref="NoticeKind.ConfirmCode"/> before the pairing can finish. Somewhere
/// else makes a one-time code only when Make a code is pressed, with a 10-minute countdown, kept across a visit to
/// another tab and back. Join with a code sends the code another PC showed. Cancel, on any pairing under way, and
/// closing the window both give up the pairing gate the service is holding.
/// </summary>
internal sealed class AddPcViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan BrowseEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CountdownTick = TimeSpan.FromSeconds(1);

    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly TimeProvider _clock;
    private ITimer? _browseTimer;
    private ITimer? _countdownTimer;
    private int _browsing;
    private int _browseAgain;
    private DateTimeOffset? _codeExpires;
    private bool _pairingActive;
    private string? _confirmPromptId;
    private string? _confirmQuestion;
    private string? _confirmCode;

    private AddPcTab _tab = AddPcTab.OnThisNetwork;
    private IReadOnlyList<FoundPc> _found = [];
    private string? _pairingText;
    private string? _code;
    private TimeSpan _remaining;
    private string _joinCode = "";
    private string? _message;
    private bool _busy;

    public AddPcViewModel(IServiceLink link, UiThreads threads, TimeProvider clock)
    {
        _link = link;
        _threads = threads;
        _clock = clock;
        _link.HouseholdNoticeReceived += OnNotice;
        Add = new RelayCommand<FoundPc>(pc =>
        {
            if (pc is not null) _ = AddAsync(pc);
        });
        Join = new RelayCommand(() => _ = JoinAsync(), () => JoinCode.Trim().Length > 0 && !Busy);
        MakeCode = new RelayCommand(() => _ = MakeCodeAsync(), () => _code is null);
        CodesMatch = new RelayCommand(() => _ = AnswerConfirmAsync(true));
        ConfirmCancel = new RelayCommand(() => _ = AnswerConfirmAsync(false));
        CancelPairing = new RelayCommand(() => _ = CancelPairingAsync(), () => CanCancelPairing);
    }

    public AddPcTab Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value)) return;
            if (value == AddPcTab.OnThisNetwork) StartBrowsing();
            else StopBrowsing();
        }
    }

    /// <summary>PCs found on this network, each marked when it is already in this household.</summary>
    public IReadOnlyList<FoundPc> Found
    {
        get => _found;
        private set
        {
            if (SetProperty(ref _found, value)) OnPropertyChanged(nameof(NoneFound));
        }
    }

    /// <summary>Nothing found yet: shows "Looking…" instead of an empty list.</summary>
    public bool NoneFound => Found.Count == 0;

    /// <summary>A pairing under way or just finished: "Adding Laptop-2…", then its outcome once both sides have agreed.
    /// Null while none is under way.</summary>
    public string? PairingText
    {
        get => _pairingText;
        private set
        {
            if (SetProperty(ref _pairingText, value)) OnPropertyChanged(nameof(HasPairingText));
        }
    }

    public bool HasPairingText => PairingText is not null;

    /// <summary>Review finding A1: the adder's own check, once the key exchange with a PC on this network is done.
    /// Null until a <see cref="NoticeKind.ConfirmCode"/> notice arrives; the pairing is not done until this is answered.</summary>
    public bool IsConfirming => _confirmPromptId is not null;

    /// <summary>The service's own wording, e.g. "Does Laptop-2 show 482 913?".</summary>
    public string? ConfirmQuestion => _confirmQuestion;

    /// <summary>The same code, on its own and large, so the two PCs are easy to compare side by side.</summary>
    public string? ConfirmCode => _confirmCode;

    /// <summary>This PC's one-time code for Somewhere else, large and copyable; null before Make a code is pressed or
    /// once it expires.</summary>
    public string? Code
    {
        get => _code;
        private set
        {
            if (!SetProperty(ref _code, value)) return;
            OnPropertyChanged(nameof(HasCode));
            MakeCode.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Whether Somewhere else currently has a made code showing, so Make a code can hide while it does.</summary>
    public bool HasCode => _code is not null;

    public TimeSpan Remaining { get => _remaining; private set => SetProperty(ref _remaining, value); }

    /// <summary>The code typed under Join with a code.</summary>
    public string JoinCode
    {
        get => _joinCode;
        set
        {
            if (SetProperty(ref _joinCode, value)) Join.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Why the last action didn't go through, or why a browse couldn't be answered, or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value)) Join.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Review finding A3: an Add or a made code is out there, and can be called off — but not while
    /// <see cref="IsConfirming"/>, whose own Codes match / Cancel already settles it.</summary>
    public bool CanCancelPairing => _pairingActive && !IsConfirming;

    public IRelayCommand<FoundPc> Add { get; }

    public IRelayCommand Join { get; }

    public IRelayCommand MakeCode { get; }

    public IRelayCommand CodesMatch { get; }

    public IRelayCommand ConfirmCancel { get; }

    public IRelayCommand CancelPairing { get; }

    /// <summary>The window opened: browse if that is the tab showing. Call on the UI thread.</summary>
    public void Start()
    {
        if (Tab == AddPcTab.OnThisNetwork) StartBrowsing();
    }

    /// <summary>The window closed: stop browsing and, review finding A3, give up a pairing gate still held.</summary>
    public void Dispose()
    {
        _link.HouseholdNoticeReceived -= OnNotice;
        StopBrowsing();
        _countdownTimer?.Dispose();
        _countdownTimer = null;
        if (_pairingActive) _ = _link.CancelPairingAsync();
    }

    private void StartBrowsing()
    {
        Refresh();
        _browseTimer ??= _clock.CreateTimer(_ => _threads.Post(Refresh), null, BrowseEvery, BrowseEvery);
    }

    private void StopBrowsing()
    {
        _browseTimer?.Dispose();
        _browseTimer = null;
    }

    private void Refresh() => _threads.Background(() => _ = BrowseAsync());

    /// <summary>Review finding A5: a browse still out is not joined by a second one — a tick or a tab reopen that lands
    /// while one is in flight just asks for one more right behind it, so nothing is lost and nothing overlaps. A link
    /// that can't answer (not connected) reads as a message, never as "Looking…" forever.</summary>
    private async Task BrowseAsync()
    {
        if (Interlocked.CompareExchange(ref _browsing, 1, 0) != 0)
        {
            Volatile.Write(ref _browseAgain, 1);
            return;
        }
        var found = await _link.BrowsePcsAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            if (found is null) Message = "Couldn't look for PCs right now.";
            else
            {
                Found = found;
                Message = null;
            }
        });
        Volatile.Write(ref _browsing, 0);
        if (Interlocked.Exchange(ref _browseAgain, 0) == 1) _ = BrowseAsync();
    }

    private async Task AddAsync(FoundPc pc)
    {
        SetPairingActive(true);
        PairingText = $"Adding {pc.Name}…";
        var result = await _link.AddPcAsync(pc.InstanceId).ConfigureAwait(false);
        _threads.Post(() =>
        {
            if (result.Ok) return;
            SetPairingActive(false);
            PairingText = null;
            Message = result.Message;
        });
    }

    private async Task MakeCodeAsync()
    {
        if (_code is not null) return;
        var result = await _link.StartCodePairingAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            if (!result.Ok)
            {
                Message = result.Message;
                return;
            }
            SetPairingActive(true);
            Code = result.Code;
            _codeExpires = _clock.GetUtcNow() + CodeLifetime;
            Remaining = CodeLifetime;
            _countdownTimer ??= _clock.CreateTimer(_ => _threads.Post(Tick), null, CountdownTick, CountdownTick);
        });
    }

    private void Tick()
    {
        if (_codeExpires is not { } expires) return;
        var left = expires - _clock.GetUtcNow();
        if (left > TimeSpan.Zero)
        {
            Remaining = left;
            return;
        }
        Remaining = TimeSpan.Zero;
        Code = null;
        _codeExpires = null;
        _countdownTimer?.Dispose();
        _countdownTimer = null;
        SetPairingActive(false);
    }

    private async Task JoinAsync()
    {
        Busy = true;
        var result = await _link.JoinByCodeAsync(JoinCode).ConfigureAwait(false);
        _threads.Post(() =>
        {
            Busy = false;
            Message = result.Message;
            if (result.Ok) JoinCode = "";
        });
    }

    /// <summary>Review finding A1: answers the adder's own check, which the pairing needs before it can finish either
    /// way. Declining it (the codes don't match) gives up the pairing from this side.</summary>
    private async Task AnswerConfirmAsync(bool matches)
    {
        if (_confirmPromptId is not { } promptId) return;
        var result = await _link.AnswerPromptAsync(promptId, matches).ConfigureAwait(false);
        _threads.Post(() =>
        {
            SetConfirm(null, null, null);
            if (!matches) SetPairingActive(false);
            if (!result.Ok) Message = result.Message;
        });
    }

    private async Task CancelPairingAsync()
    {
        var result = await _link.CancelPairingAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            ResetPairing();
            Message = result.Message;
        });
    }

    /// <summary>Back to no pairing under way: clears the confirm check, any made code and its countdown, and the
    /// progress text. Used by Cancel, by a withdrawn confirm (review finding A4), and once an outcome notice arrives.</summary>
    private void ResetPairing()
    {
        SetPairingActive(false);
        SetConfirm(null, null, null);
        PairingText = null;
        Code = null;
        _codeExpires = null;
        Remaining = TimeSpan.Zero;
        _countdownTimer?.Dispose();
        _countdownTimer = null;
    }

    private void SetPairingActive(bool active)
    {
        _pairingActive = active;
        OnPropertyChanged(nameof(CanCancelPairing));
        CancelPairing.NotifyCanExecuteChanged();
    }

    private void SetConfirm(string? promptId, string? question, string? code)
    {
        _confirmPromptId = promptId;
        _confirmQuestion = question;
        _confirmCode = code;
        OnPropertyChanged(nameof(IsConfirming));
        OnPropertyChanged(nameof(ConfirmQuestion));
        OnPropertyChanged(nameof(ConfirmCode));
        OnPropertyChanged(nameof(CanCancelPairing));
        CancelPairing.NotifyCanExecuteChanged();
    }

    /// <summary>A pairing this PC started moved on (households design §9). Raised off the UI thread.</summary>
    private void OnNotice(HouseholdNotice notice)
    {
        switch (notice.Kind)
        {
            case NoticeKind.ConfirmCode:
                _threads.Post(() => SetConfirm(notice.PromptId, notice.Text, notice.ComparisonCode));
                break;
            case NoticeKind.Withdraw:
                if (notice.PromptId != _confirmPromptId) return;
                _threads.Post(ResetPairing);
                break;
            case NoticeKind.PairingProgress:
            case NoticeKind.Info:
                _threads.Post(() =>
                {
                    ResetPairing();
                    PairingText = notice.Text;
                    Refresh();
                });
                break;
        }
    }
}
