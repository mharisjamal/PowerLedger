using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App.Aero;

/// <summary>
/// The shadows of a liquid glass piece (0.10.9), worked out rather than blurred on every frame:
/// <list type="bullet">
/// <item>the recipe's <c>filter: drop-shadow(-8px -10px 46px #0000005f)</c>. A CSS filter shadows what the piece paints,
/// and the backdrop it bends is not part of that (measured in Edge: a piece with only a backdrop filter casts none). So
/// the shadow is cast by the piece's tint and its glowing edge: black at 37 % under a Gaussian of deviation 46 (the third
/// length of drop-shadow() is the deviation itself, measured), moved 8 left and 10 up, over the backdrop and outside the
/// piece alike. An untinted pane's edge alone casts about 0.7 % at the most, as the browser draws it; a tinted piece (the
/// lime Open report, the Dark and Colour styles, a dialog) casts a real one.</item>
/// <item>a floating bubble's own <c>box-shadow: 0 8px 18px -8px rgba(0,8,40,.5)</c> (the nav, a segmented control's and
/// a figure's bubble, <see cref="Bubble"/>), outside the piece only.</item>
/// </list>
/// Each is a Gaussian of a rectangle, so it is separable and exact: it is computed once into a small bitmap (a pixel for
/// every <see cref="DropStep"/> units for the wide shadow, every unit for the bubble's) and drawn stretched, smooth, again
/// only when the piece's size, corners, tint or edge light change. Nothing here runs per frame.
/// </summary>
public sealed class GlassShadow : FrameworkElement
{
    /// <summary>The drop shadow's black at 0x5F.</summary>
    public const double DropAlpha = LiquidGlassRecipe.ShadowAlpha / 255.0;

    /// <summary>drop-shadow()'s third length is the Gaussian's deviation (measured in Edge: 46.0).</summary>
    public const double DropDeviation = LiquidGlassRecipe.ShadowBlur;

    /// <summary>How far the drop shadow is drawn past the piece: three deviations, where it has fallen under 1/255 of its peak.</summary>
    public const double DropReach = 3 * DropDeviation;

    /// <summary>Units per pixel of the drop shadow's bitmap: a Gaussian this wide is smooth at a pixel every 4 units.</summary>
    public const double DropStep = 4;

    /// <summary>The bubble's shadow: <c>0 8px 18px -8px rgba(0,8,40,.5)</c>, its deviation half the blur.</summary>
    public const double BubbleY = 8;
    public const double BubbleDeviation = 9;
    public const double BubbleSpread = -8;
    public static readonly Color BubbleColour = Color.FromArgb(0x80, 0, 8, 40);

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(GlassShadow),
        new FrameworkPropertyMetadata(new CornerRadius(LiquidGlassRecipe.CornerRadius), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint), typeof(Brush), typeof(GlassShadow),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowProperty = DependencyProperty.Register(nameof(Glow), typeof(double), typeof(GlassShadow),
        new FrameworkPropertyMetadata(LiquidGlassRecipe.HighlightOpacity, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CastsProperty = DependencyProperty.Register(nameof(Casts), typeof(bool), typeof(GlassShadow),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BubbleProperty = DependencyProperty.Register(nameof(Bubble), typeof(bool), typeof(GlassShadow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private (Size, double, double, double)? _dropFor;
    private BitmapSource? _drop;
    private (Size, double)? _bubbleFor;
    private BitmapSource? _bubble;

    public GlassShadow()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>The piece's tint, whose alpha casts the shadow with the edge.</summary>
    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    /// <summary>The edge's strength (GlassGlow.Strength), whose light casts it too.</summary>
    public double Glow { get => (double)GetValue(GlowProperty); set => SetValue(GlowProperty, value); }

    /// <summary>Whether the recipe's drop shadow is drawn.</summary>
    public bool Casts { get => (bool)GetValue(CastsProperty); set => SetValue(CastsProperty, value); }

    /// <summary>Whether the bubble's own soft shadow under it is drawn.</summary>
    public bool Bubble { get => (bool)GetValue(BubbleProperty); set => SetValue(BubbleProperty, value); }

    /// <summary>The bitmap last drawn for the drop shadow, for a test.</summary>
    internal BitmapSource? DropMap => _drop;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var radius = Math.Min(CornerRadius.TopLeft, Math.Min(w, h) / 2);
        if (Bubble)
        {
            var key = (new Size(w, h), radius);
            if (_bubbleFor != key || _bubble == null)
            {
                _bubble = BubbleMap(w, h);
                _bubbleFor = key;
            }
            var reach = 3 * BubbleDeviation;
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(-reach, -reach, w + 2 * reach, h + 2 * reach + BubbleY)), new RectangleGeometry(new Rect(0, 0, w, h), radius, radius));
            outside.Freeze();
            dc.PushClip(outside);
            dc.DrawImage(_bubble, new Rect(-reach, -reach + BubbleY, w + 2 * reach, h + 2 * reach));
            dc.Pop();
        }
        if (Casts)
        {
            var tint = Alpha(Tint);
            var glow = Math.Clamp(Glow, 0, 1);
            var key = (new Size(w, h), radius, tint, glow);
            if (_dropFor != key || _drop == null)
            {
                _drop = DropMapFor(w, h, tint, glow);
                _dropFor = key;
            }
            if (_drop != null)
                dc.DrawImage(_drop, new Rect(-DropReach + LiquidGlassRecipe.ShadowX, -DropReach + LiquidGlassRecipe.ShadowY, w + 2 * DropReach, h + 2 * DropReach));
        }
    }

    /// <summary>How opaque a tint is on average: a solid colour's alpha, a gradient's stops' mean; none for no brush.</summary>
    internal static double Alpha(Brush? brush) => brush switch
    {
        SolidColorBrush solid => solid.Color.A / 255.0 * solid.Opacity,
        GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops.Average(s => s.Color.A / 255.0) * gradient.Opacity,
        null => 0,
        _ => 0,
    };

    /// <summary>
    /// The drop shadow's alpha at every <see cref="DropStep"/> units over the piece and <see cref="DropReach"/> round it,
    /// before its offset: the piece's painted alpha (the tint, and the edge's glow over it), blurred. The glow is the sum
    /// of nested rectangles a unit apart, each lit by what the profile loses there, so the whole is a sum of blurred
    /// rectangles, each the product of two error functions. Null when it would draw nothing.
    /// </summary>
    internal static BitmapSource? DropMapFor(double w, double h, double tint, double glow)
    {
        // Each rectangle: inset by d, weighted.
        var rects = new List<(double Inset, double Weight)>();
        if (tint > 0) rects.Add((0, tint));
        if (glow > 0)
        {
            for (var d = 0; d < GlassGlow.GlowDepth; d++)
            {
                var weight = (1 - tint) * (GlassGlow.Glow(d + 0.5, glow) - GlassGlow.Glow(d + 1.5, glow));
                if (weight <= 0) continue;
                rects.Add((0, weight));
                if (w > 2 * (d + 1) && h > 2 * (d + 1)) rects.Add((d + 1, -weight));
            }
        }
        if (rects.Count == 0) return null;
        var cols = (int)Math.Ceiling((w + 2 * DropReach) / DropStep);
        var rows = (int)Math.Ceiling((h + 2 * DropReach) / DropStep);
        double StepX(int i) => (i + 0.5) * (w + 2 * DropReach) / cols - DropReach;
        double StepY(int i) => (i + 0.5) * (h + 2 * DropReach) / rows - DropReach;
        var gx = new double[rects.Count][];
        var gy = new double[rects.Count][];
        for (var k = 0; k < rects.Count; k++)
        {
            var inset = rects[k].Inset;
            gx[k] = new double[cols];
            gy[k] = new double[rows];
            for (var i = 0; i < cols; i++) gx[k][i] = Band(StepX(i), inset, w - inset, DropDeviation);
            for (var i = 0; i < rows; i++) gy[k][i] = Band(StepY(i), inset, h - inset, DropDeviation);
        }
        var pixels = new byte[cols * rows * 4];
        var any = false;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var sum = 0.0;
                for (var k = 0; k < rects.Count; k++) sum += rects[k].Weight * gx[k][x] * gy[k][y];
                var a = (byte)Math.Round(Math.Clamp(sum * DropAlpha, 0, 1) * 255);
                if (a == 0) continue;
                any = true;
                pixels[(y * cols + x) * 4 + 3] = a;   // black, premultiplied
            }
        }
        if (!any) return null;
        var bitmap = BitmapSource.Create(cols, rows, 96, 96, PixelFormats.Pbgra32, null, pixels, cols * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The bubble's shadow at every unit round the piece, before its drop: the piece shrunk by 8, blurred.</summary>
    internal static BitmapSource BubbleMap(double w, double h)
    {
        var reach = 3 * BubbleDeviation;
        var cols = (int)Math.Ceiling(w + 2 * reach);
        var rows = (int)Math.Ceiling(h + 2 * reach);
        var inset = -BubbleSpread;
        var gx = new double[cols];
        var gy = new double[rows];
        for (var i = 0; i < cols; i++) gx[i] = Band((i + 0.5) * (w + 2 * reach) / cols - reach, inset, w - inset, BubbleDeviation);
        for (var i = 0; i < rows; i++) gy[i] = Band((i + 0.5) * (h + 2 * reach) / rows - reach, inset, h - inset, BubbleDeviation);
        var pixels = new byte[cols * rows * 4];
        var colour = BubbleColour;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var a = Math.Clamp(gx[x] * gy[y] * colour.A / 255.0, 0, 1);
                var at = (y * cols + x) * 4;
                pixels[at] = (byte)Math.Round(colour.B * a);
                pixels[at + 1] = (byte)Math.Round(colour.G * a);
                pixels[at + 2] = (byte)Math.Round(colour.R * a);
                pixels[at + 3] = (byte)Math.Round(a * 255);
            }
        }
        var bitmap = BitmapSource.Create(cols, rows, 96, 96, PixelFormats.Pbgra32, null, pixels, cols * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A unit strip from <paramref name="from"/> to <paramref name="to"/> blurred by a Gaussian of
    /// <paramref name="deviation"/>, at <paramref name="at"/>.</summary>
    internal static double Band(double at, double from, double to, double deviation)
        => to <= from ? 0 : GlassGlow.Phi((at - from) / deviation) - GlassGlow.Phi((at - to) / deviation);
}
