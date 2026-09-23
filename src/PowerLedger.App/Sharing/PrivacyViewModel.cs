using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// Settings → Privacy (data-sharing design §2): the same four switches as the consent dialog, each sent to the service
/// the moment it changes, with the install id and a status line that follows how sharing is going. It reads with the rest
/// of Settings and follows its ten-second status refresh, without a refresh undoing a tick still on its way to the
/// service. "Delete my data" asks first.
/// </summary>
internal sealed class PrivacyViewModel : ObservableObject
{
    private readonly IServiceLink _link;
    private readonly UiThreads _threads;
    private readonly TimeZoneInfo _zone;
    private readonly CultureInfo _culture;
    private readonly Action _openSent;
    private readonly Action<Uri> _openBrowser;
    private readonly Action<string> _copyToClipboard;
    private int _inFlight;
    private bool _diagnostics;
    private bool _usage;
    private bool _power;
    private bool _share;
    private bool _busy;
    private string _installId = "None yet";
    private string _status = "You haven't chosen yet.";
    private string? _message;
    private bool _confirmingDelete;

    public PrivacyViewModel(
        IServiceLink link, UiThreads threads, TimeZoneInfo zone, CultureInfo culture, Action openSent, Action<Uri> openBrowser,
        Action<string> copyToClipboard)
    {
        _link = link;
        _threads = threads;
        _zone = zone;
        _culture = culture;
        _openSent = openSent;
        _openBrowser = openBrowser;
        _copyToClipboard = copyToClipboard;
        OpenSent = new RelayCommand(() => _openSent());
        OpenPrivacyPolicy = new RelayCommand(() => _openBrowser(ConsentViewModel.PrivacyPolicyUri));
        CopyInstallId = new RelayCommand(() => _copyToClipboard(InstallId));
        DeleteMyData = new RelayCommand(() => ConfirmingDelete = true);
        CancelDelete = new RelayCommand(() => ConfirmingDelete = false);
        ConfirmDelete = new RelayCommand(() => _ = ConfirmDeleteAsync(), () => !Busy);
    }

    public bool Diagnostics
    {
        get => _diagnostics;
        set { if (value != _diagnostics) Change(value, _usage, _power, _share); }
    }

    public bool Usage
    {
        get => _usage;
        set { if (value != _usage) Change(_diagnostics, value, _power, _share); }
    }

    /// <summary>Turning this off also turns <see cref="Share"/> off, in the same request (data-sharing design §1).</summary>
    public bool Power
    {
        get => _power;
        set { if (value != _power) Change(_diagnostics, _usage, value, value && _share); }
    }

    public bool Share
    {
        get => _share;
        set { if (value != _share) Change(_diagnostics, _usage, _power, value); }
    }

    /// <summary>The random id uploads go under, for questions to the owner, or "None yet" before one exists.</summary>
    public string InstallId { get => _installId; private set => SetProperty(ref _installId, value); }

    /// <summary>How sharing stands, following <see cref="StatusLine"/>.</summary>
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <summary>Why the last tick wasn't taken, or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>"Delete my data" was pressed once; it waits for Delete or Cancel.</summary>
    public bool ConfirmingDelete { get => _confirmingDelete; private set => SetProperty(ref _confirmingDelete, value); }

    /// <summary>A tick or a delete is still on its way to the service (finding 6: sharing requests are serialised, so
    /// this can take a few seconds); Delete refuses a second press meanwhile.</summary>
    public bool Busy { get => _busy; private set => SetProperty(ref _busy, value); }

    /// <summary>A tick was sent and taken, carrying what it was (data-sharing design §3): lets the App's usage counter
    /// know the consent it just sent at once, rather than only at its next flush.</summary>
    public event Action<Consent>? Applied;

    public ICommand OpenSent { get; }

    public ICommand OpenPrivacyPolicy { get; }

    public ICommand CopyInstallId { get; }

    public ICommand DeleteMyData { get; }

    public ICommand CancelDelete { get; }

    public IRelayCommand ConfirmDelete { get; }

    /// <summary>The ten-second status refresh: the switches, the id and the status line, unless a tick is still on its way
    /// to the service. Call on the UI thread.</summary>
    public void Apply(SharingStatus? sharing)
    {
        InstallId = sharing?.InstallId ?? "None yet";
        Status = sharing is null ? "The service isn't running." : StatusLine(sharing, _zone, _culture);
        if (_inFlight > 0 || sharing is null) return;
        // an answer to an older wording of the choices counts for nothing (data-sharing design §1): showing it ticked
        // would resend those old switches, under the new text, the moment any one of them is touched
        var consent = sharing.Consent.Answered ? sharing.Consent : Consent.Unanswered;
        _diagnostics = consent.Diagnostics;
        _usage = consent.Usage;
        _power = consent.Power;
        _share = consent.Share;
        Refreshed();
    }

    /// <summary>Sets the switches at once, optimistically, and sends the whole consent; a refusal puts them back. Call on
    /// the UI thread.</summary>
    private void Change(bool diagnostics, bool usage, bool power, bool share)
    {
        var previous = new Consent(ConsentText.Version, _diagnostics, _usage, _power, _share);
        _diagnostics = diagnostics;
        _usage = usage;
        _power = power;
        _share = share;
        Refreshed();
        _ = ApplyAsync(new Consent(ConsentText.Version, diagnostics, usage, power, share), previous);
    }

    private async Task ApplyAsync(Consent next, Consent previous)
    {
        _inFlight++;
        UpdateBusy();
        var result = await _link.SetConsentAsync(next).ConfigureAwait(false);
        _threads.Post(() =>
        {
            _inFlight--;
            UpdateBusy();
            if (!result.Ok)
            {
                _diagnostics = previous.Diagnostics;
                _usage = previous.Usage;
                _power = previous.Power;
                _share = previous.Share;
            }
            Message = result.Ok ? null : result.Message;
            Refreshed();
            if (result.Ok) Applied?.Invoke(next);
        });
    }

    /// <summary>The second press of delete: sends it, and forgets nothing on a refusal. Call on the UI thread.</summary>
    private async Task ConfirmDeleteAsync()
    {
        _inFlight++;
        UpdateBusy();
        var result = await _link.DeleteMyDataAsync().ConfigureAwait(false);
        _threads.Post(() =>
        {
            _inFlight--;
            UpdateBusy();
            ConfirmingDelete = false;
            Message = result.Message;
        });
    }

    /// <summary>Refreshes <see cref="Busy"/> from <see cref="_inFlight"/> and lets Delete know. Call on the UI thread.</summary>
    private void UpdateBusy()
    {
        Busy = _inFlight > 0;
        ConfirmDelete.NotifyCanExecuteChanged();
    }

    private void Refreshed()
    {
        OnPropertyChanged(nameof(Diagnostics));
        OnPropertyChanged(nameof(Usage));
        OnPropertyChanged(nameof(Power));
        OnPropertyChanged(nameof(Share));
    }

    /// <summary>The Privacy section's status line (data-sharing design §2): unanswered, off, waiting for the first upload,
    /// the last upload's time and size, or the last problem, with how many complete days still wait.</summary>
    internal static string StatusLine(SharingStatus sharing, TimeZoneInfo zone, CultureInfo culture)
    {
        // A problem that trying again won't clear is shown as the service words it, whatever the switches.
        if (sharing.ProblemLasts && sharing.Problem is { Length: > 0 } lasting)
            return char.ToUpper(lasting[0], culture) + lasting[1..] + (lasting.EndsWith('.') ? "" : ".");
        var consent = sharing.Consent;
        var basis = !consent.Answered ? "You haven't chosen yet."
            : !consent.AllowsAny ? "Nothing is sent."
            : sharing.Problem is { } problem ? sharing.Rejected ? $"Rejected by the server: {problem}" : $"Couldn't send: {problem}. Will try again."
            : sharing.LastSentAt is { } at ? $"Last sent {TimeZoneInfo.ConvertTime(at, zone).ToString("d MMM yyyy", culture)} · {Format.Kb(sharing.LastSentBytes ?? 0, culture)}"
            : "Nothing sent yet.";
        return sharing.DaysWaiting > 0 ? $"{basis} · {sharing.DaysWaiting.ToString(culture)} days waiting" : basis;
    }
}
