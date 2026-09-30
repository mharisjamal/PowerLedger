using System.Windows;
using System.Windows.Interop;

namespace PowerLedger.App.Aero;

/// <summary>
/// The backdrop source for each top-level window (an HwndSource: a Window, the overlay, a popup), made on the first
/// liquid glass piece that loads in it and let go with the last one. Tests and the render harness set
/// <see cref="Override"/> to hand every window a fake source, so nothing captures the screen.
/// </summary>
internal static class LiquidGlassSources
{
    /// <summary>When set, every window's source comes from here instead of the screen.</summary>
    public static Func<HwndSource, ILiquidGlassSource>? Override { get; set; }
}
