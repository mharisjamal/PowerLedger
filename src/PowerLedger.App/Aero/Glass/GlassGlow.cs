using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App.Aero;

/// <summary>
/// The glowing edge of a liquid glass piece (0.10.9): the owner's recipe's two inset highlights, drawn as CSS draws
/// <c>box-shadow: inset 6px 6px 0 -6px, inset 0 0 8px 1px</c> in white at <see cref="Strength"/> (the recipe's 70 % at
/// the default Edge light). WPF has no inset shadow, so each is worked out here, pixel by pixel at the display's scale:
/// <list type="bullet">
/// <item>the inner glow, the second shadow: the piece's rounded rectangle shrunk by the spread (1, its corners by 1 too)
/// is the shadow's hole; the hole is blurred by a Gaussian of deviation 4 (half the 8 px blur, as CSS says), and whatever
/// it leaves uncovered is lit. The blur is exact: across each row the hole's span blurs to two error functions, and down
/// the rows the Gaussian is summed, the rounded corners row by row, so a thin piece (the grab bar, a small bubble) is lit
/// right across as the browser lights it, not only near its sides;</item>
/// <item>the corner light, the first: the piece less its hole moved 6 right and down and grown by 6 (its corners by 6
/// too), a sharp crescent in the top-left corner only, about 2.5 px thick at 45 degrees; it lies over the glow, as the
/// first shadow in CSS's list lies over the second.</item>
/// </list>
/// Both are clipped to the piece, its edge anti-aliased. Away from its corners the edge is the same all along, so it is
/// worked out once as a small image, the piece with its middle taken out, and drawn as nine slices, the sides stretched:
/// a piece of any size costs its corners only, and a resize that keeps the corners draws the same image again. It
/// paints nothing else and takes no pointer; nothing runs at rest.
/// </summary>
public sealed class GlassGlow : FrameworkElement
{
    /// <summary>The corner light's offset and spread: <c>6px 6px 0 -6px</c>.</summary>
    public const double CornerOffset = 6;
    public const double CornerSpread = -6;

    /// <summary>The inner glow's blur and spread: <c>0 0 8px 1px</c>; its deviation is half the blur.</summary>
    public const double GlowBlur = 8;
    public const double GlowSpread = 1;
    public const double GlowDeviation = GlowBlur / 2;

    /// <summary>How deep the glow reaches: past this it adds less than a thousandth.</summary>
    public const double GlowDepth = GlowSpread + 3.5 * GlowDeviation;

    /// <summary>The images made, by piece shape and scale, shared by every piece of that shape.</summary>
    private static readonly Dictionary<(double W, double H, double R, double S, double Scale, bool Corner, bool Inner), Slices> Made = [];

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(GlassGlow),
        new FrameworkPropertyMetadata(new CornerRadius(LiquidGlassRecipe.CornerRadius), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(nameof(Strength), typeof(double), typeof(GlassGlow),
        new FrameworkPropertyMetadata(LiquidGlassRecipe.HighlightOpacity, FrameworkPropertyMetadataOptions.AffectsRender));

    public GlassGlow()
    {
        IsHitTestVisible = false;
        Focusable = false;
        // The slices are drawn pixel for pixel, the sides' one pixel stretched along: never filtered, which would blend a
        // stretched side's last row into the transparent edge of the texture.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    /// <summary>The piece's corners; a capsule's is half its height (anything larger is taken as that).</summary>
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>Both highlights' opacity: the recipe's 0.7 at the default Edge light; 0 draws nothing.</summary>
    public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }

    /// <summary>The display's scale to draw for instead of the one the piece is on, for a render at another scale.</summary>
    internal static double? ScaleOverride { get; set; }

    /// <summary>Whether the corner light and the inner glow are drawn: both always, one alone for a measurement.</summary>
    internal bool DrawsCorner { get; init; } = true;

    internal bool DrawsInner { get; init; } = true;

    /// <summary>How lit the inner glow is <paramref name="depth"/> units in from a long straight edge, at
    /// <paramref name="strength"/>: everything outside the shrunk rectangle, blurred.</summary>
    public static double Glow(double depth, double strength) => strength * (1 - Phi((depth - GlowSpread) / GlowDeviation));

    /// <summary>A corner's radius with <paramref name="spread"/> added, as CSS spreads a shadow's corners: by the spread
    /// where the corner is at least as large, and less for a small corner, so a square stays square.</summary>
    public static double Spread(double radius, double spread)
    {
        if (radius <= 0) return 0;
        if (spread < 0) return Math.Max(0, radius + spread);
        if (radius >= spread) return radius + spread;
        var ratio = radius / spread;
        return radius + spread * (1 + Math.Pow(ratio - 1, 3));
    }

    /// <summary>The corner light's shape in a piece of <paramref name="size"/> with <paramref name="radius"/> corners: the
    /// piece less its hole.</summary>
    public static System.Windows.Media.Geometry CornerLight(Size size, double radius)
    {
        var grow = -CornerSpread;
        var box = new RectangleGeometry(new Rect(size), radius, radius);
        var holeRadius = Spread(radius, grow);
        var hole = new RectangleGeometry(new Rect(CornerOffset - grow, CornerOffset - grow, size.Width + 2 * grow, size.Height + 2 * grow), holeRadius, holeRadius);
        var light = new CombinedGeometry(GeometryCombineMode.Exclude, box, hole);
        light.Freeze();
        return light;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, strength = Math.Clamp(Strength, 0, 1);
        if (w <= 0 || h <= 0 || strength <= 0) return;
        var radius = Math.Max(0, Math.Min(CornerRadius.TopLeft, Math.Min(w, h) / 2));
        var scale = ScaleOverride ?? VisualTreeHelper.GetDpi(this).DpiScaleX;
        var slices = For(w, h, radius, strength, scale, DrawsCorner, DrawsInner);
        foreach (var (source, target) in slices.Parts(w, h, scale)) dc.DrawImage(source, target);
    }

    /// <summary>The slices for a piece, made once for its shape and shared.</summary>
    internal static Slices For(double w, double h, double radius, double strength, double scale, bool corner = true, bool inner = true)
    {
        var reach = Reach(radius);
        // A side long enough to have a middle is keyed without its length: its image doesn't change with it.
        double Key(double length) => length * scale >= 2 * Math.Ceiling(reach * scale) + 1 ? -1 : Math.Round(length * scale);
        var key = (Key(w), Key(h), radius, Math.Round(strength, 4), scale, corner, inner);
        if (Made.TryGetValue(key, out var made)) return made;
        if (Made.Count > 256) Made.Clear();
        return Made[key] = Make(w, h, radius, strength, scale, corner, inner);
    }

    /// <summary>How far in from a side its edge's light can differ from the middle's: the corner, the glow's reach and the
    /// corner light's hole.</summary>
    private static double Reach(double radius) => Math.Max(radius + GlowDepth, Spread(radius, -CornerSpread) + 1) + 1;

    /// <summary>
    /// The piece's light at every device pixel of a piece <paramref name="w"/> by <paramref name="h"/> with its middle taken
    /// out: each side's first <see cref="Reach"/> units as they are, and between them a single pixel standing for the
    /// whole middle, where the light no longer changes along the side.
    /// </summary>
    internal static Slices Make(double w, double h, double radius, double strength, double scale, bool corner = true, bool inner = true)
    {
        var reach = (int)Math.Ceiling(Reach(radius) * scale);
        var (xs, cols, xEnd) = Axis(w, scale, reach);
        var (ys, rows, yEnd) = Axis(h, scale, reach);
        var hole = new Hole(GlowSpread, GlowSpread, w - GlowSpread, h - GlowSpread, Spread(radius, -GlowSpread));
        var grow = -CornerSpread;
        var lightHole = new Rect(CornerOffset - grow, CornerOffset - grow, w + 2 * grow, h + 2 * grow);
        var lightRadius = Spread(radius, grow);
        var pixels = new byte[cols * rows * 4];
        var coverX = new double[cols][];
        for (var j = 0; j < rows; j++)
        {
            var y = ys[j];
            for (var i = 0; i < cols; i++)
            {
                var x = xs[i];
                var box = Coverage(x, y, new Rect(0, 0, w, h), radius, scale);
                if (box <= 0) continue;
                var glow = inner ? strength * (1 - hole.Blurred(x, y)) : 0;
                var light = corner ? strength * (1 - Coverage(x, y, lightHole, lightRadius, scale)) : 0;
                var a = Math.Clamp((light + glow * (1 - light)) * box, 0, 1);
                var v = (byte)Math.Round(a * 255);
                if (v == 0) continue;
                var at = (j * cols + i) * 4;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = pixels[at + 3] = v;   // white, premultiplied
            }
        }
        var image = BitmapSource.Create(cols, rows, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null, pixels, cols * 4);
        image.Freeze();
        return new Slices(image, xEnd, yEnd);
    }

    /// <summary>Where each pixel of the image lies along one side, in units: the first <paramref name="reach"/> pixels,
    /// then one for the middle (when the side has one), then the last; and how many pixels each end holds.</summary>
    private static (double[] At, int Count, int End) Axis(double length, double scale, int reach)
    {
        var pixels = Math.Max(1, (int)Math.Ceiling(length * scale - 0.001));
        if (pixels < 2 * reach + 1)
        {
            var all = new double[pixels];
            for (var i = 0; i < pixels; i++) all[i] = (i + 0.5) / scale;
            return (all, pixels, pixels);
        }
        var at = new double[2 * reach + 1];
        for (var i = 0; i < reach; i++)
        {
            at[i] = (i + 0.5) / scale;
            at[2 * reach - i] = length - (i + 0.5) / scale;
        }
        at[reach] = length / 2;
        return (at, 2 * reach + 1, reach);
    }

    /// <summary>How much of the pixel centred at (<paramref name="x"/>, <paramref name="y"/>) a rounded rectangle covers,
    /// by its signed distance: the anti-aliased edge a browser draws.</summary>
    internal static double Coverage(double x, double y, Rect r, double radius, double scale)
    {
        if (r.Width <= 0 || r.Height <= 0) return 0;
        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        // The signed distance to a rounded rectangle: from its middle, less its half size and corner.
        var qx = Math.Abs(x - (r.Left + r.Width / 2)) - (r.Width / 2 - radius);
        var qy = Math.Abs(y - (r.Top + r.Height / 2)) - (r.Height / 2 - radius);
        var outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - radius;
        return Math.Clamp(0.5 - outside * scale, 0, 1);
    }

    /// <summary>The inner glow's hole, a rounded rectangle, and how much of it a Gaussian of the glow's deviation spreads
    /// onto a point.</summary>
    private readonly record struct Hole(double Left, double Top, double Right, double Bottom, double Radius)
    {
        public double Blurred(double x, double y)
        {
            if (Right <= Left || Bottom <= Top) return 0;
            var s = GlowDeviation;
            var r = Math.Min(Radius, Math.Min(Right - Left, Bottom - Top) / 2);
            var far = 4 * s;
            // The straight rows, all at once: their span blurs across to two error functions.
            double from = Math.Max(Top + r, y - far), to = Math.Min(Bottom - r, y + far);
            var sum = 0.0;
            var across = Phi((x - Left) / s) - Phi((x - Right) / s);
            if (to > from) sum += (Phi((to - y) / s) - Phi((from - y) / s)) * across;
            if (r <= 0) return sum;
            // The rounded rows, top and bottom, each narrower by its corners: row by row near a corner; away from both
            // corners' columns the narrowing is out of the blur's reach, and the rows sum at once as the straight ones do.
            const double step = 0.5;
            var clear = x - Left - r > far && Right - r - x > far;
            foreach (var (start, end, centre) in new[] { (Top, Top + r, Top + r), (Bottom - r, Bottom, Bottom - r) })
            {
                double a = Math.Max(start, y - far), b = Math.Min(end, y + far);
                if (b <= a) continue;
                if (clear)
                {
                    sum += (Phi((b - y) / s) - Phi((a - y) / s)) * across;
                    continue;
                }
                var n = (int)Math.Ceiling((b - a) / step);
                var dv = (b - a) / n;
                for (var k = 0; k < n; k++)
                {
                    double v0 = a + k * dv, v1 = v0 + dv, v = v0 + dv / 2;
                    var off = Math.Abs(v - centre);
                    var inset = r - Math.Sqrt(Math.Max(0, r * r - off * off));
                    var weight = Phi((v1 - y) / s) - Phi((v0 - y) / s);
                    sum += weight * (Phi((x - Left - inset) / s) - Phi((x - Right + inset) / s));
                }
            }
            return sum;
        }
    }

    /// <summary>The image of a piece's light with its middle taken out, and how many pixels each end of each side holds.</summary>
    internal sealed class Slices(BitmapSource image, int xEnd, int yEnd)
    {
        private readonly Dictionary<Int32Rect, CroppedBitmap> _crops = [];

        public BitmapSource Image => image;

        /// <summary>The nine slices (fewer for a short side) and where each is drawn on a piece <paramref name="w"/> by
        /// <paramref name="h"/>: the corners as they are, the sides stretched along, the middle (unlit) left out.</summary>
        public IEnumerable<(ImageSource Source, Rect Target)> Parts(double w, double h, double scale)
        {
            var xs = Spans(image.PixelWidth, xEnd, w, scale);
            var ys = Spans(image.PixelHeight, yEnd, h, scale);
            foreach (var (sy, sh, ty, th) in ys)
            {
                foreach (var (sx, sw, tx, tw) in xs)
                {
                    var middle = sw == 1 && sh == 1 && xs.Count == 3 && ys.Count == 3;
                    if (middle) continue;
                    var crop = new Int32Rect(sx, sy, sw, sh);
                    if (!_crops.TryGetValue(crop, out var source))
                    {
                        source = new CroppedBitmap(image, crop);
                        source.Freeze();
                        _crops[crop] = source;
                    }
                    yield return (source, new Rect(tx, ty, tw, th));
                }
            }
        }

        /// <summary>One side's spans: the source pixels and where they land, in units.</summary>
        private static List<(int Source, int SourceLength, double Target, double TargetLength)> Spans(int pixels, int end, double length, double scale)
        {
            if (end == pixels) return [(0, pixels, 0, pixels / scale)];
            var edge = end / scale;
            return [(0, end, 0, edge), (end, 1, edge, Math.Max(0, length - 2 * edge)), (end + 1, end, length - edge, edge)];
        }
    }

    /// <summary>The standard normal distribution (Abramowitz and Stegun 7.1.26 for erf, good to 1.5e-7).</summary>
    internal static double Phi(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * x);
        var erf = 1 - t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429)))) * Math.Exp(-x * x);
        return z >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
    }
}
