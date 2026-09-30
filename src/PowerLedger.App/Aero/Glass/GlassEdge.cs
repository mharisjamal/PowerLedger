using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The edge of a glass pane, drawn as the approved demo's <c>.glass</c> draws it (0.10.6, measured against the demo at
/// 125 %), in whole device pixels so the rim stays a hairline at any scale:
/// <list type="bullet">
/// <item>the rim, 1 px round the outside: the demo's 140 degree gradient (white at 62, 30, 22 and 46 % by the edge
/// light), laid across the pane in its own pixels as CSS lays it, whatever the pane's shape;</item>
/// <item>inside it a light line 1.5 px wide (white at 34 %) over a faint dark one reaching 3 px (black at 7 %);</item>
/// <item>a sheen along the top: the demo's <c>inset 0 14px 22px -16px</c>, a soft light that is strongest at the top
/// edge and gone 30 px down;</item>
/// <item>a soft shade toward the edges: the demo's <c>inset 0 0 26px</c> black at 10 %.</item>
/// </list>
/// It paints no surface: the tint is the pane's, and what is behind the glass shows through everything here. Nothing
/// moves, so it is drawn once and again only when the pane's size or the glass changes.
/// </summary>
public sealed class GlassEdge : FrameworkElement
{
    /// <summary>The demo's gradient angle, in CSS's degrees: toward the bottom right.</summary>
    public const double RimAngle = 140;

    /// <summary>The demo's stops along that gradient for the rim's four colours.</summary>
    public static readonly double[] RimStops = [0, 0.3, 0.65, 1];

    /// <summary>The rim's width, the light line's and how far the dark line reaches from the rim, in CSS pixels.</summary>
    public const double RimWidth = 1;
    public const double LightWidth = 1.5;
    public const double DarkReach = 3;

    /// <summary>The top sheen: a shadow blurred by 22 px (a deviation of 11) whose edge stands 2 px above the pane's
    /// top (14 down less the 16 it is drawn in by), and how far down it is drawn.</summary>
    public const double SheenDeviation = 11;
    public const double SheenLift = 2;
    public const double SheenDepth = 30;

    /// <summary>The inner shade: blurred by 26 px (a deviation of 13), drawn in bands this wide to this depth.</summary>
    public const double ShadeDeviation = 13;
    public const double ShadeBand = 2;
    public const double ShadeDepth = 32;

    public static readonly DependencyProperty CornerRadiusProperty = Register(nameof(CornerRadius), new CornerRadius(26));
    public static readonly DependencyProperty RimAProperty = Register(nameof(RimA), Colors.Transparent);
    public static readonly DependencyProperty RimBProperty = Register(nameof(RimB), Colors.Transparent);
    public static readonly DependencyProperty RimCProperty = Register(nameof(RimC), Colors.Transparent);
    public static readonly DependencyProperty RimDProperty = Register(nameof(RimD), Colors.Transparent);
    public static readonly DependencyProperty LightProperty = Register(nameof(Light), Colors.Transparent);
    public static readonly DependencyProperty DarkProperty = Register(nameof(Dark), Colors.Transparent);
    public static readonly DependencyProperty SheenProperty = Register(nameof(Sheen), Colors.Transparent);
    public static readonly DependencyProperty ShadeProperty = Register(nameof(Shade), Colors.Transparent);

    public GlassEdge()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>The rim's four colours along its gradient (the palette's A.C.RimA to A.C.RimD).</summary>
    public Color RimA { get => (Color)GetValue(RimAProperty); set => SetValue(RimAProperty, value); }

    public Color RimB { get => (Color)GetValue(RimBProperty); set => SetValue(RimBProperty, value); }

    public Color RimC { get => (Color)GetValue(RimCProperty); set => SetValue(RimCProperty, value); }

    public Color RimD { get => (Color)GetValue(RimDProperty); set => SetValue(RimDProperty, value); }

    /// <summary>The light line inside the rim (A.C.RimInner).</summary>
    public Color Light { get => (Color)GetValue(LightProperty); set => SetValue(LightProperty, value); }

    /// <summary>The faint dark line under and inside it (A.C.RimDark).</summary>
    public Color Dark { get => (Color)GetValue(DarkProperty); set => SetValue(DarkProperty, value); }

    /// <summary>The top sheen's colour at full strength (A.C.TopSheen); the most that shows is 43 % of it, at the top.</summary>
    public Color Sheen { get => (Color)GetValue(SheenProperty); set => SetValue(SheenProperty, value); }

    /// <summary>The inner shade's colour at full strength (A.C.InnerShade); half of it shows at the edge.</summary>
    public Color Shade { get => (Color)GetValue(ShadeProperty); set => SetValue(ShadeProperty, value); }

    /// <summary>Where CSS's <c>linear-gradient(140deg, …)</c> starts and ends across a box of <paramref name="size"/>:
    /// through its middle at the angle, long enough that the first and last stops reach the corners.</summary>
    internal static (Point Start, Point End) RimLine(Size size)
    {
        var angle = RimAngle * Math.PI / 180;
        double dx = Math.Sin(angle), dy = -Math.Cos(angle);
        var half = (Math.Abs(size.Width * dx) + Math.Abs(size.Height * dy)) / 2;
        var middle = new Point(size.Width / 2, size.Height / 2);
        return (new Point(middle.X - dx * half, middle.Y - dy * half), new Point(middle.X + dx * half, middle.Y + dy * half));
    }

    /// <summary>How much of a blurred inset shadow shows <paramref name="distance"/> inside its edge: a Gaussian's tail.</summary>
    internal static double Falloff(double distance, double deviation) => 0.5 * Erfc(distance / (deviation * Math.Sqrt(2)));

    /// <summary><paramref name="units"/> as whole device pixels, one at the least, at <paramref name="scale"/> pixels to the unit.</summary>
    internal static double Snapped(double units, double scale) => Math.Max(1, Math.Round(units * scale)) / scale;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var radius = Math.Min(CornerRadius.TopLeft, Math.Min(w, h) / 2);
        var rim = Snapped(RimWidth, scale);
        var light = Snapped(LightWidth, scale);
        var dark = Math.Max(light, Snapped(DarkReach, scale));

        // Bottom to top, as the demo stacks its shadows: the shade, the sheen, the dark line, the light line, the rim.
        if (Shade.A > 0)
        {
            var reach = Math.Min(ShadeDepth, Math.Min(w, h) / 2 - rim);
            for (var at = 0.0; at < reach; at += ShadeBand)
            {
                var strength = Falloff(at + ShadeBand / 2, ShadeDeviation);
                Ring(dc, Solid(Faded(Shade, strength)), rim + at, Math.Min(ShadeBand, reach - at), radius, w, h);
            }
        }
        if (Sheen.A > 0 && h > 2 * rim && w > 2 * rim)
        {
            var stops = new GradientStopCollection();
            const int steps = 10;
            for (var i = 0; i <= steps; i++)
            {
                var y = SheenDepth * i / steps;
                stops.Add(new GradientStop(Faded(Sheen, i == steps ? 0 : Falloff(y + SheenLift, SheenDeviation)), (double)i / steps));
            }
            var brush = new LinearGradientBrush(stops, new Point(0, rim), new Point(0, rim + SheenDepth)) { MappingMode = BrushMappingMode.Absolute };
            brush.Freeze();
            var inner = Math.Max(0, radius - rim);
            dc.DrawRoundedRectangle(brush, null, new Rect(rim, rim, w - 2 * rim, h - 2 * rim), inner, inner);
        }
        if (Dark.A > 0) Ring(dc, Solid(Dark), rim, dark, radius, w, h);
        if (Light.A > 0) Ring(dc, Solid(Light), rim, light, radius, w, h);
        if (RimA.A > 0 || RimB.A > 0 || RimC.A > 0 || RimD.A > 0)
        {
            var (start, end) = RimLine(new Size(w, h));
            var brush = new LinearGradientBrush(
                new GradientStopCollection { new(RimA, RimStops[0]), new(RimB, RimStops[1]), new(RimC, RimStops[2]), new(RimD, RimStops[3]) }, start, end)
            {
                MappingMode = BrushMappingMode.Absolute,
            };
            brush.Freeze();
            Ring(dc, brush, 0, rim, radius, w, h);
        }
    }

    /// <summary>A ring <paramref name="width"/> wide whose outer edge is <paramref name="inset"/> inside the pane's.</summary>
    private static void Ring(DrawingContext dc, Brush brush, double inset, double width, double radius, double w, double h)
    {
        var middle = inset + width / 2;
        if (w <= 2 * middle || h <= 2 * middle || width <= 0) return;
        var pen = new Pen(brush, width);
        pen.Freeze();
        var r = Math.Max(0, radius - middle);
        dc.DrawRoundedRectangle(null, pen, new Rect(middle, middle, w - 2 * middle, h - 2 * middle), r, r);
    }

    private static SolidColorBrush Solid(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static Color Faded(Color colour, double share) => Color.FromArgb((byte)Math.Round(colour.A * Math.Clamp(share, 0, 1)), colour.R, colour.G, colour.B);

    /// <summary>The complementary error function (Abramowitz and Stegun 7.1.26, good to 1.5e-7).</summary>
    private static double Erfc(double x)
    {
        var z = Math.Abs(x);
        var t = 1 / (1 + 0.3275911 * z);
        var series = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429))));
        var value = series * Math.Exp(-z * z);
        return x >= 0 ? value : 2 - value;
    }

    private static DependencyProperty Register<T>(string name, T initial)
        => DependencyProperty.Register(name, typeof(T), typeof(GlassEdge), new FrameworkPropertyMetadata(initial, FrameworkPropertyMetadataOptions.AffectsRender));
}
