using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The liquid glass engine's one element (see docs/superpowers/specs/2026-10-01-aero-liquid-glass-engine.md): put it
/// behind a glass piece's content, filling the piece, and it draws the live backdrop under the piece through the owner's
/// recipe (<see cref="LiquidGlassRecipe"/>), <c>brightness(1.1) blur(2px) url(#displacement)</c> in that order, clipped
/// to the piece's rounded rectangle (<see cref="CornerRadius"/>). It works in any top-level window (the Aero window, the
/// watts overlay, a popup): the window is left out of screen capture (WDA_EXCLUDEFROMCAPTURE) so its glass never shows
/// itself, and the picture follows the window as it moves. It draws no tint, shadow or highlight: the look does.
/// </summary>
internal sealed class LiquidGlassBackdrop : FrameworkElement
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius),
        typeof(LiquidGlassBackdrop), new FrameworkPropertyMetadata(new CornerRadius(LiquidGlassRecipe.CornerRadius), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(nameof(Brightness), typeof(double),
        typeof(LiquidGlassBackdrop), new FrameworkPropertyMetadata(LiquidGlassRecipe.Brightness, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BlurDeviationProperty = DependencyProperty.Register(nameof(BlurDeviation), typeof(double),
        typeof(LiquidGlassBackdrop), new FrameworkPropertyMetadata(LiquidGlassRecipe.BlurDeviation, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(nameof(Scale), typeof(double),
        typeof(LiquidGlassBackdrop), new FrameworkPropertyMetadata(LiquidGlassRecipe.Scale, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(nameof(IsLive), typeof(bool),
        typeof(LiquidGlassBackdrop), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyPropertyKey KindKey = DependencyProperty.RegisterReadOnly(nameof(Kind), typeof(LiquidGlassSourceKind),
        typeof(LiquidGlassBackdrop), new PropertyMetadata(LiquidGlassSourceKind.None));

    public static readonly DependencyProperty KindProperty = KindKey.DependencyProperty;

    public LiquidGlassBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>The piece's corners; the recipe's 28 by default.</summary>
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>The first filter, brightness; 1.1 by default.</summary>
    public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }

    /// <summary>The second, the blur's standard deviation in units; 2 by default.</summary>
    public double BlurDeviation { get => (double)GetValue(BlurDeviationProperty); set => SetValue(BlurDeviationProperty, value); }

    /// <summary>The third, the displacement's scale in units; 200 by default, 0 for none.</summary>
    public double Scale { get => (double)GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }

    /// <summary>False draws nothing and lets go of the capture (reduce transparency, a hidden piece).</summary>
    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }

    /// <summary>What the piece shows now: the live screen, the wallpaper, or nothing yet.</summary>
    public LiquidGlassSourceKind Kind => (LiquidGlassSourceKind)GetValue(KindProperty);
}
