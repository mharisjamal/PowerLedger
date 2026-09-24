using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// The consent dialog (data-sharing design §2, owner's round: one screen, two choices): explains the four purposes and
/// that detailed rows may be published or sold, then offers only "Allow all" (every purpose on) or "Decline" (every
/// purpose off) — the same <see cref="Consent"/> either sends. Settings → Privacy keeps the individual switches for
/// changing one purpose at a time later. Success closes the dialog; a refusal shows the message and keeps it open.
/// Closing another way sends nothing, since nothing here reacts to it. "Privacy policy" opens the page in the browser.
/// </summary>
internal sealed class ConsentViewModel : ObservableObject
{
    /// <summary>Where "Privacy policy" (data-sharing design §2) leads.</summary>
    internal static readonly Uri PrivacyPolicyUri = new("https://github.com/mharisjamal/PowerLedger/blob/main/PRIVACY.md");

    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly Action<Uri> _openBrowser;
    private bool _busy;
    private string? _message;

    public ConsentViewModel(IServiceLink link, UiThreads threads, Action<Uri> openBrowser)
    {
        _link = link;
        _threads = threads;
        _openBrowser = openBrowser;
        AllowAll = new RelayCommand(() => _ = SendAsync(new Consent(ConsentText.Version, true, true, true, true)), () => !Busy);
        Decline = new RelayCommand(() => _ = SendAsync(new Consent(ConsentText.Version, false, false, false, false)), () => !Busy);
        OpenPrivacyPolicy = new RelayCommand(() => _openBrowser(PrivacyPolicyUri));
    }

    /// <summary>The choice was sent and taken: the dialog closes.</summary>
    public event Action? Closed;

    /// <summary>The choice was sent and taken, carrying what it was (data-sharing design §3): lets the App's usage
    /// counter know the consent it just sent at once, rather than only at its next flush.</summary>
    public event Action<Consent>? Applied;

    /// <summary>Why the last choice wasn't taken, or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>A choice is still on its way to the service (finding 6: sharing requests are serialised, so this can take
    /// a few seconds); the buttons refuse a second press meanwhile.</summary>
    public bool Busy { get => _busy; private set => SetProperty(ref _busy, value); }

    public IRelayCommand AllowAll { get; }

    public IRelayCommand Decline { get; }

    public ICommand OpenPrivacyPolicy { get; }

    /// <summary>Sends a choice, and closes the dialog once the service has taken it. Call on the UI thread.</summary>
    internal async Task SendAsync(Consent consent)
    {
        SetBusy(true);
        var result = await _link.SetConsentAsync(consent).ConfigureAwait(false);
        _threads.Post(() =>
        {
            SetBusy(false);
            if (result.Ok)
            {
                Applied?.Invoke(consent);
                Closed?.Invoke();
            }
            else Message = result.Message;
        });
    }

    private void SetBusy(bool value)
    {
        Busy = value;
        AllowAll.NotifyCanExecuteChanged();
        Decline.NotifyCanExecuteChanged();
    }
}
