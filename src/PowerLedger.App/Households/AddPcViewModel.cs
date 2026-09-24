using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>Add a PC's two tabs (households design §2).</summary>
internal enum AddPcTab
{
    OnThisNetwork,
    SomewhereElse,
}

/// <summary>
/// Add a PC (households design §3, §4, Plan N task A2). On this network browses every 5 s while the page is open, each
/// PC marked when it is already in this household; Add starts a pairing whose comparison code, then outcome, arrives as
/// pushed <see cref="HouseholdNotice"/>s. Somewhere else makes a one-time code with a 10-minute countdown, made once and
/// kept across a visit to the other tab and back. Join a household with a code sends the code another PC showed.
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
    private int _browseReads;
    private string? _addingName;
    private DateTimeOffset? _codeExpires;

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
    }

    public AddPcTab Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value)) return;
            if (value == AddPcTab.OnThisNetwork) StartBrowsing();
            else StopBrowsing();
            if (value == AddPcTab.SomewhereElse && _code is null) _ = StartCodeAsync();
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

    /// <summary>A pairing under way or just finished: "Adding Laptop-2…", then "On Laptop-2, check the code is 482 913
    /// and press Join.", then its outcome once the other PC answers. Null while none is under way.</summary>
    public string? PairingText
    {
        get => _pairingText;
        private set
        {
            if (SetProperty(ref _pairingText, value)) OnPropertyChanged(nameof(HasPairingText));
        }
    }

    public bool HasPairingText => PairingText is not null;

    /// <summary>This PC's one-time code for Somewhere else, large and copyable; null before one is made or once it expires.</summary>
    public string? Code { get => _code; private set => SetProperty(ref _code, value); }

    public TimeSpan Remaining { get => _remaining; private set => SetProperty(ref _remaining, value); }

    /// <summary>The code typed under Join a household with a code.</summary>
    public string JoinCode
    {
        get => _joinCode;
        set
        {
            if (SetProperty(ref _joinCode, value)) Join.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Why the last action didn't go through, or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value)) Join.NotifyCanExecuteChanged();
        }
    }

    public IRelayCommand<FoundPc> Add { get; }

    public IRelayCommand Join { get; }

    /// <summary>The window opened: browse if that is the tab showing. Call on the UI thread.</summary>
    public void Start()
    {
        if (Tab == AddPcTab.OnThisNetwork) StartBrowsing();
    }

    public void Dispose()
    {
        _link.HouseholdNoticeReceived -= OnNotice;
        StopBrowsing();
        _countdownTimer?.Dispose();
        _countdownTimer = null;
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

    private void Refresh()
    {
        var read = ++_browseReads;
        _threads.Background(() => _ = BrowseAsync(read));
    }

    private async Task BrowseAsync(int read)
    {
        var found = await _link.BrowsePcsAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            if (read != _browseReads) return;
            Found = found ?? [];
        });
    }

    private async Task AddAsync(FoundPc pc)
    {
        _addingName = pc.Name;
        PairingText = $"Adding {pc.Name}…";
        var result = await _link.AddPcAsync(pc.InstanceId).ConfigureAwait(false);
        _threads.Post(() =>
        {
            if (result.Ok) return;
            PairingText = null;
            _addingName = null;
            Message = result.Message;
        });
    }

    private async Task StartCodeAsync()
    {
        var result = await _link.StartCodePairingAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            if (result.Ok && result.Code is { } code)
            {
                Code = code;
                _codeExpires = _clock.GetUtcNow() + CodeLifetime;
                Remaining = CodeLifetime;
                _countdownTimer ??= _clock.CreateTimer(_ => _threads.Post(Tick), null, CountdownTick, CountdownTick);
            }
            else
            {
                Message = result.Message;
            }
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

    /// <summary>A pairing this PC started moved on (households design §9): the comparison code while it is under way, or,
    /// once <see cref="HouseholdNotice.ComparisonCode"/> is null, its outcome in the text — at which point the browse
    /// list is read again, since it may show a new member. Raised off the UI thread.</summary>
    private void OnNotice(HouseholdNotice notice)
    {
        if (notice.Kind != NoticeKind.PairingProgress) return;
        _threads.Post(() =>
        {
            if (notice.ComparisonCode is { } code)
            {
                PairingText = $"On {_addingName ?? notice.FromName ?? "the other PC"}, check the code is {code} and press Join.";
                return;
            }
            PairingText = notice.Text;
            _addingName = null;
            Refresh();
        });
    }
}
