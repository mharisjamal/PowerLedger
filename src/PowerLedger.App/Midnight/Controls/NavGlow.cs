using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// The sidebar's current page (0.8.1, after the reference): a full-width row washed with the accent at its left, running
/// into the violet and fading out to the right, with a brighter edge along its left. One element that the sidebar moves
/// from item to item (MidnightWindow.MovePill). Drawn rather than layered: the palette holds solid brushes, and the
/// gradient between two of them is made here, so it follows a palette swap.
/// </summary>
internal sealed class NavGlow : FrameworkElement
{
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(NavGlow), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondBrushProperty = DependencyProperty.Register(
        nameof(SecondBrush), typeof(Brush), typeof(NavGlow), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How much of the accent the wash has at its left edge, where the item's words start.</summary>
    internal const double Strength = 0.30;

    /// <summary>How much of the violet it has where the accent has faded into it.</summary>
    internal const double SecondStrength = 0.12;

    /// <summary>The bright edge's width.</summary>
    internal const double Edge = 3;

    /// <summary>The accent (M.Accent): the wash's start and the edge.</summary>
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    /// <summary>The violet (M.Accent2) the wash runs into.</summary>
    public Brush SecondBrush { get => (Brush)GetValue(SecondBrushProperty); set => SetValue(SecondBrushProperty, value); }

    /// <summary>The wash's stops, left to right: the accent, the violet part way along, nothing at the right.</summary>
    internal static GradientStopCollection Stops(Color accent, Color second) =>
    [
        new GradientStop(WithAlpha(accent, Strength), 0),
        new GradientStop(WithAlpha(second, SecondStrength), 0.6),
        new GradientStop(WithAlpha(second, 0), 1),
    ];

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        if (!(size.Width > 0) || !(size.Height > 0)) return;
        var wash = new LinearGradientBrush(Stops(ColourOf(AccentBrush), ColourOf(SecondBrush)), new Point(0, 0), new Point(1, 0));
        wash.Freeze();
        dc.DrawRectangle(wash, null, new Rect(size));
        dc.DrawRectangle(AccentBrush, null, new Rect(0, 0, Math.Min(Edge, size.Width), size.Height));
    }

    private static Color WithAlpha(Color colour, double alpha) => Color.FromArgb((byte)Math.Round(255 * alpha), colour.R, colour.G, colour.B);

    private static Color ColourOf(Brush brush) => (brush as SolidColorBrush)?.Color ?? Colors.Transparent;
}
