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
    private readonly string _googleClientSecret;
    private string _deviceId = "";
    private bool _signedIn;
    private bool _confirmingDelete;
    private bool _busy;
    private string? _message;
    private string _recoveryCodeInput = "";
    private bool _confirmingRecoverySignIn;
    private SignInProvider? _pendingSignInProvider;

    /// <summary>Plan 0.9: shown before a sign-in that carries a typed recovery code actually runs, since it removes the
    /// household's other PCs.</summary>
    public const string RecoveryWarning =
        "Signing in with the recovery code removes your household's other PCs from it. Use it only if you've lost them all; otherwise approve this PC from one of them.";

    /// <summary><paramref name="microsoftClientId"/> and <paramref name="googleClientId"/> are <see cref="SignInClients"/>'s,
    /// passed in so a test can use one that isn't empty; while either is empty its button says sign-in isn't set up yet.
    /// <paramref name="googleClientSecret"/> is <see cref="SignInClients.GoogleSecret"/>, Google's only (review finding
    /// A7).</summary>
    public SignInViewModel(
        IServiceLink link, IUiSettings ui, SignIn signIn, UiThreads threads, string microsoftClientId, string googleClientId,
        string googleClientSecret = "")
    {
        _link = link;
        _ui = ui;
        _signIn = signIn;
        _threads = threads;
        _microsoftClientId = microsoftClientId;
        _googleClientId = googleClientId;
        _googleClientSecret = googleClientSecret;
        SignInWithMicrosoft = new RelayCommand(() => BeginSignIn(SignInProvider.Microsoft), () => !Busy && MicrosoftAvailable);
        SignInWithGoogle = new RelayCommand(() => BeginSignIn(SignInProvider.Google), () => !Busy && GoogleAvailable);
        ContinueRecoverySignIn = new RelayCommand(() =>
        {
            var provider = _pendingSignInProvider;
            ConfirmingRecoverySignIn = false;
            _pendingSignInProvider = null;
            if (provider is { } chosen) _ = RunAsync(chosen);
        });
        CancelRecoverySignIn = new RelayCommand(() =>
        {
            ConfirmingRecoverySignIn = false;
            _pendingSignInProvider = null;
        });
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

    /// <summary>Use a recovery code (households design §7): typed here, on the sign-in flow, and sent with the next
    /// sign-in instead of waiting for another member's approval.</summary>
    public string RecoveryCodeInput { get => _recoveryCodeInput; set => SetProperty(ref _recoveryCodeInput, value); }

    public bool SignedIn { get => _signedIn; private set => SetProperty(ref _signedIn, value); }

    /// <summary>"Delete account" was pressed once; it waits for Confirm or Cancel.</summary>
    public bool ConfirmingDelete { get => _confirmingDelete; private set => SetProperty(ref _confirmingDelete, value); }

    /// <summary>Plan 0.9: a sign-in with a typed recovery code was pressed once; it waits for Continue or Cancel before
    /// the browser opens at all.</summary>
    public bool ConfirmingRecoverySignIn { get => _confirmingRecoverySignIn; private set => SetProperty(ref _confirmingRecoverySignIn, value); }

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

    /// <summary>Runs the sign-in the warning was about (plan 0.9).</summary>
    public IRelayCommand ContinueRecoverySignIn { get; }

    /// <summary>Backs out of a recovery-code sign-in; nothing was sent (plan 0.9).</summary>
    public IRelayCommand CancelRecoverySignIn { get; }

    public IRelayCommand SignOut { get; }

    public IRelayCommand DeleteAccount { get; }

    public IRelayCommand ConfirmDelete { get; }

    public IRelayCommand CancelDelete { get; }

    /// <summary>Follows the service's status: whether this PC holds a session, and its device ID for the next sign-in's
    /// nonce. Call on the UI thread.</summary>
    public void Apply(HouseholdStatus? household)
    {
        SignedIn = household?.SignedIn ?? false;
        if (household is not null) _deviceId = household.DeviceId;
    }

    /// <summary>Plan 0.9: a typed recovery code warns before the browser even opens, since it removes the household's
    /// other PCs; Continue there is what actually starts <see cref="RunAsync"/>.</summary>
    private void BeginSignIn(SignInProvider provider)
    {
        if (!string.IsNullOrWhiteSpace(_recoveryCodeInput))
        {
            _pendingSignInProvider = provider;
            ConfirmingRecoverySignIn = true;
            return;
        }
        _ = RunAsync(provider);
    }

    /// <summary>Review finding A7: whatever goes wrong, including something <see cref="SignIn"/> itself didn't expect
    /// and so didn't turn into a failed <see cref="SignInResult"/>, this always leaves Busy false again.</summary>
    private async Task RunAsync(SignInProvider provider)
    {
        Busy = true;
        Message = null;
        try
        {
            var clientId = provider == SignInProvider.Microsoft ? _microsoftClientId : _googleClientId;
            var clientSecret = provider == SignInProvider.Google ? _googleClientSecret : null;
            var outcome = await _signIn.RunAsync(provider, clientId, _deviceId, clientSecret: clientSecret).ConfigureAwait(false);
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
            var recoveryCode = string.IsNullOrWhiteSpace(_recoveryCodeInput) ? null : _recoveryCodeInput.Trim();
            var result = await _link.SignInAsync(providerName, outcome.IdToken, outcome.Salt, recoveryCode).ConfigureAwait(false);
            _threads.Post(() =>
            {
                Busy = false;
                Message = result.Message;
                if (result.Ok && outcome.Email is not null)
                {
                    _ui.SetSignedInEmail(outcome.Email);
                    OnPropertyChanged(nameof(Email));
                }
                if (result.Ok) RecoveryCodeInput = "";
                // A first sign-in that links a household makes a recovery code, but it now arrives as its own pushed
                // RecoveryCode notice (task 0.8), not on this reply, so App.xaml.cs opens that window from the notice.
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _threads.Post(() =>
            {
                Busy = false;
                Message = "Something went wrong signing in.";
            });
        }
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
