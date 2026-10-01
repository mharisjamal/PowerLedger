namespace PowerLedger.App.Aero;

/// <summary>
/// The owner's chosen liquid glass (2026-10-01), as its source's parameters: a backdrop filter of
/// <c>brightness(1.1) blur(2px) url(#displacement)</c>, applied in that order, where the url filter is a turbulence
/// (type turbulence, base frequency 0.01, two octaves, seed 0, no stitching) displacing the backdrop by 200 units through
/// its red and green channels, with colour-interpolation-filters left at linearRGB; a drop shadow of -8, -10, blur 46 in
/// black at 0x5F; a 28 unit corner; no tint; and two inset highlights in white at 70 %. The engine
/// (<see cref="LiquidGlassBackdrop"/>) draws the filtered backdrop; the look draws the shadow and the highlights.
/// Every length here is in WPF units, the source's CSS pixels.
/// </summary>
internal static class LiquidGlassRecipe
{
    /// <summary>The first backdrop filter: every channel times this, clamped to 1.</summary>
    public const double Brightness = 1.1;

    /// <summary>The second: a Gaussian blur with this standard deviation.</summary>
    public const double BlurDeviation = 2;

    /// <summary>The third, feTurbulence's baseFrequency (both axes).</summary>
    public const double BaseFrequency = 0.01;

    /// <summary>feTurbulence's numOctaves.</summary>
    public const int Octaves = 2;

    /// <summary>feTurbulence's seed (SVG's default, 0, which the reference algorithm reads as 1).</summary>
    public const int Seed = 0;

    /// <summary>feDisplacementMap's scale: a channel at 1 moves the sample half this far one way, at 0 the other.</summary>
    public const double Scale = 200;

    /// <summary>The glass's corner radius.</summary>
    public const double CornerRadius = 28;

    /// <summary>drop-shadow(-8px -10px 46px #0000005f): its offset, blur and black's alpha.</summary>
    public const double ShadowX = -8;
    public const double ShadowY = -10;
    public const double ShadowBlur = 46;
    public const byte ShadowAlpha = 0x5F;

    /// <summary>The ::before highlights: <c>inset 6px 6px 0 -6px</c> and <c>inset 0 0 8px 1px</c>, both white at this alpha.</summary>
    public const double HighlightOpacity = 0.7;
}
