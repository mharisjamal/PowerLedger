using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>What a liquid glass piece shows through it.</summary>
internal enum LiquidGlassSourceKind
{
    /// <summary>Nothing yet (the window has no handle, or it is hidden).</summary>
    None,

    /// <summary>The live screen under the window, from the monitor's desktop duplication, our own windows left out.</summary>
    Live,

    /// <summary>The wallpaper where Windows draws it on the screen: capture is unavailable (older Windows, policy, a
    /// remote session) or failed.</summary>
    Wallpaper,

    /// <summary>A piece inside another piece: the outer piece's own content beneath it (InsideGlassSource), as a
    /// backdrop-filter inside a backdrop root reads it.</summary>
    Inside,
}

/// <summary>
/// The picture behind one top-level window, for its liquid glass pieces: <see cref="Image"/> lies on the screen at
/// <see cref="ScreenBounds"/>, in physical pixels, and <see cref="Changed"/> says when either moved on (on the window's
/// UI thread). One source serves every piece on its window.
/// </summary>
internal interface ILiquidGlassSource
{
    LiquidGlassSourceKind Kind { get; }

    /// <summary>The backdrop picture, or null while there is none.</summary>
    ImageSource? Image { get; }

    /// <summary>Where <see cref="Image"/> lies on the virtual screen, in physical pixels.</summary>
    Rect ScreenBounds { get; }

    /// <summary>New pixels, new bounds or a new kind; raised on the UI thread.</summary>
    event Action? Changed;
}
