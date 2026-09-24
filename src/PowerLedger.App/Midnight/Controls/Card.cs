using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// A card that lifts off the page on a shadow it paints itself (plan O 0.3 M.Card): a few rounded rectangles behind it,
/// each larger and fainter than the one inside, set down by a third of <see cref="Depth"/>. No Effect goes on the card,
/// so its text keeps ClearType and a redraw inside it (the chart's crosshair) touches that control alone, where a
/// DropShadowEffect would take the whole card through an effect pass. The style sets <see cref="Shadow"/> and
/// <see cref="Depth"/>, and a hover raises the depth; on a plain Border they do nothing, so M.Card there is flat.
/// </summary>
internal sealed class Card : Border
{
    public static readonly DependencyProperty ShadowProperty = DependencyProperty.RegisterAttached(
        "Shadow", typeof(Brush), typeof(Card), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DepthProperty = DependencyProperty.RegisterAttached(
        "Depth", typeof(double), typeof(Card), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How much of the shadow colour's own alpha the layers add up to at the card's edge.</summary>
    internal const double Peak = 0.6;

    /// <summary>The palette's shadow colour (M.Shadow).</summary>
    public Brush? Shadow { get => GetShadow(this); set => SetShadow(this, value); }

    /// <summary>How far the shadow reaches (M.Elevation.Card), animated up on hover.</summary>
    public double Depth { get => GetDepth(this); set => SetDepth(this, value); }

    public static Brush? GetShadow(DependencyObject element) => (Brush?)element.GetValue(ShadowProperty);

    public static void SetShadow(DependencyObject element, Brush? value) => element.SetValue(ShadowProperty, value);

    public static double GetDepth(DependencyObject element) => (double)element.GetValue(DepthProperty);

    public static void SetDepth(DependencyObject element, double value) => element.SetValue(DepthProperty, value);

    /// <summary>
    /// The shadow's layers, outermost first: a layer every 2 px of <paramref name="depth"/> (3 to 12 of them), the
    /// outermost reaching the depth on every side and the corner growing with it, all set down by a third of the depth,
    /// each adding an equal part of <see cref="Peak"/>, so the shade deepens evenly toward the card's edge.
    /// </summary>
    internal static IReadOnlyList<(Rect Rect, double Radius, double Alpha)> ShadowLayers(Size size, double corner, double depth)
    {
        if (!(depth > 0) || !(size.Width > 0) || !(size.Height > 0)) return [];
        var count = (int)Math.Clamp(Math.Round(depth / 2), 3, 12);
        var drop = depth / 3;
        var layers = new List<(Rect, double, double)>(count);
        for (var i = count; i >= 1; i--)
        {
            var grow = depth * i / count;
            layers.Add((new Rect(-grow, drop - grow, size.Width + 2 * grow, size.Height + 2 * grow), corner + grow, Peak / count));
        }
        return layers;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Shadow is SolidColorBrush { Color: var colour })
        {
            foreach (var (rect, radius, alpha) in ShadowLayers(RenderSize, CornerRadius.TopLeft, Depth))
            {
                var layer = new SolidColorBrush(Color.FromArgb((byte)Math.Round(colour.A * alpha), colour.R, colour.G, colour.B));
                layer.Freeze();
                dc.DrawRoundedRectangle(layer, null, rect, radius, radius);
            }
        }
        base.OnRender(dc);   // the card's own fill and edge over the shadow
    }

    /// <summary>The shadow is paint, not card: only the card's own box takes the pointer, so hovering near a card doesn't raise it.</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
        => new Rect(RenderSize).Contains(hitTestParameters.HitPoint) ? base.HitTestCore(hitTestParameters) : null;
}
