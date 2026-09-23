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
    private string? _message;

    public ConsentViewModel(IServiceLink link, UiThreads threads, Consent current, Action<Uri> openBrowser, Action<string> openPayload)
    {
        _link = link;
        _threads = threads;
        _openBrowser = openBrowser;
        _openPayload = openPayload;
        _diagnostics = current.Diagnostics;
        _usage = current.Usage;
        _power = current.Power;
        _share = current.Share;
        AllowAll = new RelayCommand(() => _ = SendAsync(new Consent(ConsentText.Version, true, true, true, true)));
        AllowNone = new RelayCommand(() => _ = SendAsync(new Consent(ConsentText.Version, false, false, false, false)));
        Save = new RelayCommand(() => _ = SendAsync(new Consent(ConsentText.Version, Diagnostics, Usage, Power, Share)));
        SeeWhatWouldBeSent = new RelayCommand(() => _ = PreviewAsync());
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

    public ICommand AllowAll { get; }

    public ICommand AllowNone { get; }

    public ICommand Save { get; }

    public ICommand SeeWhatWouldBeSent { get; }

    public ICommand OpenPrivacyPolicy { get; }

    /// <summary>Sends a choice, and closes the dialog once the service has taken it. Call on the UI thread.</summary>
    internal async Task SendAsync(Consent consent)
    {
        var result = await _link.SetConsentAsync(consent).ConfigureAwait(false);
        _threads.Post(() =>
        {
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
        var result = await _link.PreviewUploadAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            if (result.Ok && result.Path is { } path) _openPayload(path);
            else Message = result.Message;
        });
    }
}
