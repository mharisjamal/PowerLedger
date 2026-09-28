using System.ComponentModel;

namespace PowerLedger.App.Aero;

/// <summary>What <see cref="OverlayHost"/> needs of the overlay: <see cref="OverlayWindow"/>, or a fake in a test.</summary>
internal interface IOverlay
{
    /// <summary>The overlay's settings changed while it shows: its place, opacity or sparkline.</summary>
    void Apply(OverlaySettings settings);

    void ShowOverlay();

    /// <summary>Closes it for good; the host makes a new one when it is wanted again.</summary>
    void CloseOverlay();
}

/// <summary>
/// Keeps the watts overlay to its rule (Aero look design §5): it is Aero's only, open while the look is Aero and it is on,
/// closed when it is turned off or the look leaves Aero, and back with Aero if it is still on. It follows the one channel
/// every choice goes through, <see cref="SettingsViewModel"/>'s Look and Overlay, so Settings, the tray, the top bar and
/// the overlay's own menu all move it the same way. It also tells the tray whether to offer the overlay (only in Aero)
/// and whether it is on. It opens the overlay as it starts when the saved look is Aero, whether or not the window shows:
/// the overlay's point is to float while the window is in the tray. Call on the UI thread.
/// </summary>
internal sealed class OverlayHost : IDisposable
{
    private readonly SettingsViewModel _settings;
    private readonly Func<IOverlay> _create;
    private readonly Action<bool, bool> _offer;
    private IOverlay? _overlay;

    /// <param name="create">A new overlay, not yet shown.</param>
    /// <param name="offer">The tray's item: whether it is offered, and whether the overlay is on.</param>
    public OverlayHost(SettingsViewModel settings, Func<IOverlay> create, Action<bool, bool> offer)
    {
        _settings = settings;
        _create = create;
        _offer = offer;
        settings.PropertyChanged += OnSettingsChanged;
        Follow();
    }

    /// <summary>The overlay shows in <paramref name="look"/> with <paramref name="overlay"/>: only in Aero, only when on.</summary>
    public static bool Shows(Look look, OverlaySettings overlay) => look == Look.Aero && overlay.Enabled;

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        Close();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.Look) or nameof(SettingsViewModel.Overlay)) Follow();
    }

    private void Follow()
    {
        var look = _settings.Look;
        var overlay = _settings.Overlay;
        _offer(look == Look.Aero, overlay.Enabled);
        if (!Shows(look, overlay))
        {
            Close();
            return;
        }
        if (_overlay is { } shown)
        {
            shown.Apply(overlay);
            return;
        }
        _overlay = _create();
        _overlay.Apply(overlay);
        _overlay.ShowOverlay();
    }

    private void Close()
    {
        _overlay?.CloseOverlay();
        _overlay = null;
    }
}
