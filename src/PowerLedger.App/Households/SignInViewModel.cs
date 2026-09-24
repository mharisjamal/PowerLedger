using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// N2's sign-in section of the Household page (households design §7, Plan N task A6): Sign in with Microsoft or Google
/// through the system browser, the e-mail from the ID token once signed in (kept in ui.json only, never sent anywhere),
/// and Sign out and Delete account, the last asking first.
/// </summary>
internal sealed class SignInViewModel : ObservableObject
{
    private readonly IServiceLink _link;
    private readonly IUiSettings _ui;
    private readonly SignIn _signIn;
    private readonly UiThreads _threads;
    private readonly string _microsoftClientId;
    private readonly string _googleClientId;
    private string _deviceId = "";
    private bool _signedIn;
    private bool _confirmingDelete;
    private bool _busy;
    private string? _message;

    /// <summary><paramref name="microsoftClientId"/> and <paramref name="googleClientId"/> are <see cref="SignInClients"/>'s,
    /// passed in so a test can use one that isn't empty; while either is empty its button says sign-in isn't set up yet.</summary>
    public SignInViewModel(
        IServiceLink link, IUiSettings ui, SignIn signIn, UiThreads threads, string microsoftClientId, string googleClientId)
    {
        _link = link;
        _ui = ui;
        _signIn = signIn;
        _threads = threads;
        _microsoftClientId = microsoftClientId;
        _googleClientId = googleClientId;
        SignInWithMicrosoft = new RelayCommand(() => _ = RunAsync(SignInProvider.Microsoft), () => !Busy && MicrosoftAvailable);
        SignInWithGoogle = new RelayCommand(() => _ = RunAsync(SignInProvider.Google), () => !Busy && GoogleAvailable);
        SignOut = new RelayCommand(() => _ = SignOutAsync(), () => !Busy);
        DeleteAccount = new RelayCommand(() => ConfirmingDelete = true, () => !Busy);
        ConfirmDelete = new RelayCommand(() => _ = DeleteAsync(), () => !Busy);
        CancelDelete = new RelayCommand(() => ConfirmingDelete = false);
    }

    public bool MicrosoftAvailable => _microsoftClientId.Length > 0;

    public bool GoogleAvailable => _googleClientId.Length > 0;

    public string MicrosoftButtonText => MicrosoftAvailable ? "Sign in with Microsoft" : "Sign-in isn't set up yet";

    public string GoogleButtonText => GoogleAvailable ? "Sign in with Google" : "Sign-in isn't set up yet";

    /// <summary>The e-mail from the ID token, kept in ui.json only; null while signed out.</summary>
    public string? Email => _ui.Current.SignedInEmail;

    public bool SignedIn { get => _signedIn; private set => SetProperty(ref _signedIn, value); }

    /// <summary>"Delete account" was pressed once; it waits for Confirm or Cancel.</summary>
    public bool ConfirmingDelete { get => _confirmingDelete; private set => SetProperty(ref _confirmingDelete, value); }

    /// <summary>Why the last action didn't go through, or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>A sign-in, sign-out or delete is under way; every button here refuses a second press meanwhile.</summary>
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value)) UpdateCommands();
        }
    }

    public IRelayCommand SignInWithMicrosoft { get; }

    public IRelayCommand SignInWithGoogle { get; }

    public IRelayCommand SignOut { get; }

    public IRelayCommand DeleteAccount { get; }

    public IRelayCommand ConfirmDelete { get; }

    public IRelayCommand CancelDelete { get; }

    /// <summary>A first sign-in linked a household and made a recovery code (households design §7, Plan N task A7):
    /// shown once.</summary>
    public event Action<string>? RecoveryCodeReceived;

    /// <summary>Follows the service's status: whether this PC holds a session, and its device ID for the next sign-in's
    /// nonce. Call on the UI thread.</summary>
    public void Apply(HouseholdStatus? household)
    {
        SignedIn = household?.SignedIn ?? false;
        if (household is not null) _deviceId = household.DeviceId;
    }

    private async Task RunAsync(SignInProvider provider)
    {
        Busy = true;
        Message = null;
        var clientId = provider == SignInProvider.Microsoft ? _microsoftClientId : _googleClientId;
        var outcome = await _signIn.RunAsync(provider, clientId, _deviceId).ConfigureAwait(false);
        if (!outcome.Ok || outcome.IdToken is null || outcome.Salt is null)
        {
            _threads.Post(() =>
            {
                Busy = false;
                Message = outcome.Message;
            });
            return;
        }
        var providerName = provider == SignInProvider.Microsoft ? "microsoft" : "google";
        var result = await _link.SignInAsync(providerName, outcome.IdToken, outcome.Salt, null).ConfigureAwait(false);
        _threads.Post(() =>
        {
            Busy = false;
            Message = result.Message;
            if (result.Ok && outcome.Email is not null)
            {
                _ui.SetSignedInEmail(outcome.Email);
                OnPropertyChanged(nameof(Email));
            }
            if (result.Ok && result.Code is { } code) RecoveryCodeReceived?.Invoke(code);
        });
    }

    private async Task SignOutAsync()
    {
        Busy = true;
        var result = await _link.SignOutAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            Busy = false;
            Message = result.Message;
            if (result.Ok)
            {
                _ui.SetSignedInEmail(null);
                OnPropertyChanged(nameof(Email));
            }
        });
    }

    private async Task DeleteAsync()
    {
        Busy = true;
        var result = await _link.DeleteAccountAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            Busy = false;
            ConfirmingDelete = false;
            Message = result.Message;
            if (result.Ok)
            {
                _ui.SetSignedInEmail(null);
                OnPropertyChanged(nameof(Email));
            }
        });
    }

    private void UpdateCommands()
    {
        SignInWithMicrosoft.NotifyCanExecuteChanged();
        SignInWithGoogle.NotifyCanExecuteChanged();
        SignOut.NotifyCanExecuteChanged();
        DeleteAccount.NotifyCanExecuteChanged();
        ConfirmDelete.NotifyCanExecuteChanged();
    }
}
