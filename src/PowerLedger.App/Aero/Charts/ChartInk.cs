using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MediaGeometry = System.Windows.Media.Geometry;

namespace PowerLedger.App.Aero;

/// <summary>
/// What Aero's drawn charts share (Aero look design §1, from the prototype's Charts.cs): the HTML's smooth curve, text in
/// the look's face, the pills that hold a value or a time, and colours read from the palette's A.* tokens on the element,
/// so a chart takes the window's palette as any styled element does.
/// </summary>
internal static class ChartInk
{
    private static readonly FontFamily Fallback = new("Segoe UI Variable Display, Segoe UI Variable Text, Segoe UI");

    /// <summary>The palette's colour (or a solid brush's) <paramref name="key"/> as <paramref name="element"/> finds it, or <paramref name="fallback"/>.</summary>
    public static Color Colour(FrameworkElement element, string key, Color fallback) => element.TryFindResource(key) switch
    {
        Color colour => colour,
        SolidColorBrush brush => brush.Color,
        _ => fallback,
    };

    public static SolidColorBrush Brush(Color colour) => Frozen(new SolidColorBrush(colour));

    public static SolidColorBrush Brush(FrameworkElement element, string key, Color fallback) => Brush(Colour(element, key, fallback));

    public static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    public static Pen Pen(Brush brush, double width, double[]? dash = null, bool round = false)
    {
        var pen = new Pen(brush, width);
        if (dash != null) pen.DashStyle = new DashStyle(dash, 0);
        if (round)
        {
            pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
            pen.LineJoin = PenLineJoin.Round;
        }
        return Frozen(pen);
    }

    /// <summary>Text in the look's face (A.F.Ui), for a chart's labels and pills.</summary>
    public static FormattedText Text(FrameworkElement element, string text, double size, Brush brush, bool semi = false)
    {
        var family = element.TryFindResource("A.F.Ui") as FontFamily ?? Fallback;
        var face = new Typeface(family, FontStyles.Normal, semi ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush,
            VisualTreeHelper.GetDpi(element).PixelsPerDip);
    }

    /// <summary>Draws <paramref name="text"/> with its baseline at <paramref name="baseline"/>, anchored at its start (0),
    /// middle (1) or end (2).</summary>
    public static void At(DrawingContext dc, FormattedText text, double x, double baseline, int anchor = 0)
    {
        var dx = anchor == 0 ? 0 : anchor == 1 ? -text.Width / 2 : -text.Width;
        dc.DrawText(text, new Point(x + dx, baseline - text.Baseline));
    }

    /// <summary>A capsule: a rectangle rounded by half its height.</summary>
    public static void Pill(DrawingContext dc, Rect rect, Brush fill, Pen? edge = null)
        => dc.DrawRoundedRectangle(fill, edge, rect, rect.Height / 2, rect.Height / 2);

    /// <summary>The HTML's smooth(): a Catmull-Rom style curve through the points, at a tension of 0.17; closed down to
    /// <paramref name="baseY"/> for an area.</summary>
    public static MediaGeometry Smooth(IReadOnlyList<Point> points, bool close = false, double baseY = 0)
    {
        var geometry = new StreamGeometry();
        if (points.Count == 0) return Frozen(geometry);
        using (var c = geometry.Open())
        {
            c.BeginFigure(points[0], close, close);
            const double t = .17;
            for (var i = 0; i < points.Count - 1; i++)
            {
                var p0 = i > 0 ? points[i - 1] : points[i];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = i + 2 < points.Count ? points[i + 2] : p2;
                c.BezierTo(new Point(p1.X + (p2.X - p0.X) * t, p1.Y + (p2.Y - p0.Y) * t),
                    new Point(p2.X - (p3.X - p1.X) * t, p2.Y - (p3.Y - p1.Y) * t), p2, true, true);
            }
            if (close)
            {
                c.LineTo(new Point(points[^1].X, baseY), true, false);
                c.LineTo(new Point(points[0].X, baseY), true, false);
            }
        }
        return Frozen(geometry);
    }

    /// <summary>A round step for a scale that spans <paramref name="span"/> in about <paramref name="lines"/> steps: 1, 2,
    /// 2.5 or 5 times a power of ten.</summary>
    public static double NiceStep(double span, int lines)
    {
        if (!(span > 0) || !double.IsFinite(span)) return 1;
        var raw = span / Math.Max(1, lines);
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
        {
            if (m * power >= raw - 1e-12) return m * power;
        }
        return 10 * power;
    }
}
