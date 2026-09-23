using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// The consent dialog (data-sharing design §2): the four switches, starting from the service's consent or all off when
/// unanswered, "Share" greyed until "Hardware and power" is on. Each button sends the choice at once; success closes the
/// dialog, a refusal shows the message and keeps it open. Closing another way sends nothing, since nothing here reacts to
/// it. "See what would be sent" opens the file the service just wrote; "Privacy policy" opens the page in the browser.
/// </summary>
internal sealed class ConsentViewModel : ObservableObject
{
    /// <summary>Where "Privacy policy" and "See what would be sent" (data-sharing design §2) lead.</summary>
    internal static readonly Uri PrivacyPolicyUri = new("https://github.com/mharisjamal/PowerLedger/blob/main/PRIVACY.md");

    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly Action<Uri> _openBrowser;
    private readonly Action<string> _openPayload;
    private bool _diagnostics;
    private bool _usage;
    private bool _power;
    private bool _share;
    private bool _busy;
    private string? _message;

    public ConsentViewModel(IServiceLink link, UiThreads threads, Consent current, Action<Uri> openBrowser, Action<string> openPayload)
    {
        _link = link;
        _threads = threads;
        _openBrowser = openBrowser;
        _openPayload = openPayload;
        // an answer to an older wording of the choices counts for nothing (data-sharing design §1): pre-ticking it would
        // let one Save re-consent everything it held under the new text
        var starting = current.Answered ? current : Consent.Unanswered;
        _diagnostics = starting.Diagnostics;
        _usage = starting.Usage;
        _power = starting.Power;
        _share = starting.Share;
        AllowAll = new RelayCommand(() => _ = SendAsync(new Consent(ConsentText.Version, true, true, true, true)), () => !Busy);
        AllowNone = new RelayCommand(() => _ = SendAsync(new Consent(ConsentText.Version, false, false, false, false)), () => !Busy);
        Save = new RelayCommand(() => _ = SendAsync(new Consent(ConsentText.Version, Diagnostics, Usage, Power, Share)), () => !Busy);
        SeeWhatWouldBeSent = new RelayCommand(() => _ = PreviewAsync(), () => !Busy);
        OpenPrivacyPolicy = new RelayCommand(() => _openBrowser(PrivacyPolicyUri));
    }

    /// <summary>The choice was sent and taken: the dialog closes.</summary>
    public event Action? Closed;

    /// <summary>The choice was sent and taken, carrying what it was (data-sharing design §3): lets the App's usage
    /// counter know the consent it just sent at once, rather than only at its next flush.</summary>
    public event Action<Consent>? Applied;

    public bool Diagnostics { get => _diagnostics; set => SetProperty(ref _diagnostics, value); }

    public bool Usage { get => _usage; set => SetProperty(ref _usage, value); }

    /// <summary>Turning this off also turns <see cref="Share"/> off (data-sharing design §1).</summary>
    public bool Power
    {
        get => _power;
        set
        {
            if (!SetProperty(ref _power, value)) return;
            if (!value) Share = false;
            OnPropertyChanged(nameof(CanShare));
        }
    }

    /// <summary>Whether "Share my detailed data" can be ticked: it needs <see cref="Power"/> on.</summary>
    public bool CanShare => Power;

    public bool Share { get => _share; set => SetProperty(ref _share, value); }

    /// <summary>Why the last choice wasn't taken, or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>A choice or a preview is still on its way to the service (finding 6: sharing requests are serialised, so
    /// this can take a few seconds); the buttons below refuse a second press meanwhile.</summary>
    public bool Busy { get => _busy; private set => SetProperty(ref _busy, value); }

    public IRelayCommand AllowAll { get; }

    public IRelayCommand AllowNone { get; }

    public IRelayCommand Save { get; }

    public IRelayCommand SeeWhatWouldBeSent { get; }

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

    /// <summary>Asks the service to write what the next upload would carry, and opens it. Call on the UI thread.</summary>
    internal async Task PreviewAsync()
    {
        SetBusy(true);
        var result = await _link.PreviewUploadAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            SetBusy(false);
            if (result.Ok && result.Path is { } path) _openPayload(path);
            else Message = result.Message;
        });
    }

    private void SetBusy(bool value)
    {
        Busy = value;
        AllowAll.NotifyCanExecuteChanged();
        AllowNone.NotifyCanExecuteChanged();
        Save.NotifyCanExecuteChanged();
        SeeWhatWouldBeSent.NotifyCanExecuteChanged();
    }
}
