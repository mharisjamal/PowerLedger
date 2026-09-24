using CommunityToolkit.Mvvm.ComponentModel;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <summary>
/// Settings → Household (households design §2): the one tick, "Let my other PCs find this one on the network", on by
/// default, sent the moment it changes and following Settings' own ten-second status refresh, without a refresh undoing
/// a tick still on its way to the service (the same shape as Settings → Privacy).
/// </summary>
internal sealed class HouseholdSettingsViewModel(IServiceLink link, UiThreads threads) : ObservableObject
{
    private int _inFlight;
    private bool _discoverable = true;
    private bool _busy;
    private string? _message;
    private Task _pending = Task.CompletedTask;

    /// <summary>Whether other PCs on a Private network can find this one.</summary>
    public bool Discoverable
    {
        get => _discoverable;
        set
        {
            if (value == _discoverable) return;
            var previous = _discoverable;
            _discoverable = value;
            OnPropertyChanged();
            _pending = ChangeAsync(value, previous);
        }
    }

    /// <summary>The change still on its way to the service, for a test to await; already-completed once there is none.</summary>
    internal Task Pending => _pending;

    /// <summary>Why the last tick wasn't taken, or null.</summary>
    public string? Message { get => _message; private set => SetProperty(ref _message, value); }

    /// <summary>A tick is still on its way to the service.</summary>
    public bool Busy { get => _busy; private set => SetProperty(ref _busy, value); }

    /// <summary>Follows the service's status, unless a tick is still in flight. Call on the UI thread.</summary>
    public void Apply(HouseholdStatus? household)
    {
        if (_inFlight > 0 || household is null) return;
        _discoverable = household.Discoverable;
        OnPropertyChanged(nameof(Discoverable));
    }

    private async Task ChangeAsync(bool value, bool previous)
    {
        _inFlight++;
        Busy = true;
        var result = await link.SetDiscoverableAsync(value).ConfigureAwait(false);
        threads.Post(() =>
        {
            _inFlight--;
            Busy = _inFlight > 0;
            if (!result.Ok)
            {
                _discoverable = previous;
                OnPropertyChanged(nameof(Discoverable));
            }
            Message = result.Ok ? null : result.Message;
        });
    }
}
