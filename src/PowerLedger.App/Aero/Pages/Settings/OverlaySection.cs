using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerLedger.App.Aero;

/// <summary>
/// Settings' Overlay section in Aero (Aero look design §5): the watts overlay on or off, its corner (or Free, where it was
/// dragged), its opacity and its sparkline. Each choice sets <see cref="SettingsViewModel.Overlay"/> to a new record with
/// the one field changed, which saves it in range and raises it, and the overlay follows; the section follows the same
/// channel back, so the overlay's own menu and the tray show here too.
/// </summary>
internal sealed class OverlaySection : ObservableObject
{
    private static readonly string[] Shown = [nameof(Enabled), nameof(Position), nameof(Opacity), nameof(Sparkline)];

    private readonly SettingsViewModel _settings;

    public OverlaySection(SettingsViewModel settings)
    {
        _settings = settings;
        settings.PropertyChanged += OnSettingsChanged;
    }

    public bool Enabled { get => Overlay.Enabled; set => Change(Overlay with { Enabled = value }); }

    public OverlayPosition Position { get => Overlay.Position; set => Change(Overlay with { Position = value }); }

    /// <summary>The pill's opacity, <see cref="OverlaySettings.MinOpacity"/> to 1.</summary>
    public double Opacity { get => Overlay.Opacity; set => Change(Overlay with { Opacity = value }); }

    public bool Sparkline { get => Overlay.Sparkline; set => Change(Overlay with { Sparkline = value }); }

    private OverlaySettings Overlay => _settings.Overlay;

    private void Change(OverlaySettings overlay) => _settings.Overlay = overlay;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.Overlay)) return;
        foreach (var name in Shown) OnPropertyChanged(name);
    }
}
