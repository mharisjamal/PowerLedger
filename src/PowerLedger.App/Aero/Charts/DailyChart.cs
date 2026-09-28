using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>Energy each day's scale (Aero look design §1), pure, on the prototype's 640 × 150 drawing: the month's days
/// across, from the 1st to its last, and kWh up to a round ceiling over the busiest day of either month.</summary>
internal static class DailyScale
{
    public const double X0 = 36, X1 = 628, Y0 = 10, Y1 = 126, Width = 640, Height = 150, AxisBaseline = 146;

    private static readonly double[] Mantissas = [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8];

    /// <summary>The smallest round figure (1, 1.5, 2, 2.5, 3, 4, 5, 6 or 8 times a power of ten) at or over the busiest
    /// day, and half a kWh at the least, so a quiet month doesn't fill the chart.</summary>
    public static double Ceiling(double busiest)
    {
        if (!(busiest > 0.5) || !double.IsFinite(busiest)) return 0.5;
        var power = Math.Pow(10, Math.Floor(Math.Log10(busiest)));
        foreach (var m in Mantissas)
        {
            if (m * power >= busiest - 1e-12) return Math.Round(m * power, 6);
        }
        return 10 * power;
    }

    /// <summary>The gridlines' kWh, from nothing up to <paramref name="max"/>, two or three of them.</summary>
    public static IReadOnlyList<double> Grid(double max)
    {
        var step = ChartInk.NiceStep(max, 2);
        var lines = new List<double>();
        for (var v = 0.0; v <= max + 1e-9; v += step) lines.Add(Math.Round(v, 6));
        return lines;
    }

    /// <summary>The days the axis names: the 1st, every fifth, and the month's last, a fifth too close to the last left out.</summary>
    public static IReadOnlyList<int> Labels(int days)
        => [1, .. Enumerable.Range(1, 6).Select(i => i * 5).Where(d => d <= days - 4), days];

    public static double X(int day, int days) => X0 + (day - 1) / (double)Math.Max(1, days - 1) * (X1 - X0);

    public static double Y(double kwh, double max) => Y1 - Math.Max(0, kwh) / max * (Y1 - Y0);

    /// <summary>"1st", "2nd", "3rd", "11th", "22nd".</summary>
    public static string Ordinal(int day)
        => day.ToString(CultureInfo.InvariantCulture) + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}

/// <summary>
/// Energy each day (Aero look design §1, the prototype's DailyChart on real days): the chosen month's days as a hatched
/// area under the accent line, the month before as a dashed line, and a tip on the day under the pointer ("$0.27 on the
/// 17th"), today's when the pointer is away. Drawn on demand only; the intro wipes it in through <see cref="Reveal"/>.
/// </summary>
internal sealed class DailyChart : FrameworkElement
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(nameof(Days), typeof(IReadOnlyList<DailyDay>),
        typeof(DailyChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((DailyChart)d).OnDays()));

    public static readonly DependencyProperty PreviousProperty = DependencyProperty.Register(nameof(Previous), typeof(IReadOnlyList<double>),
        typeof(DailyChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal), typeof(double),
        typeof(DailyChart), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private int? _tip;

    public DailyChart()
    {
        Cursor = Cursors.Cross;
        AutomationProperties.SetName(this, "Energy each day");
    }

    /// <summary>The chosen month's days so far.</summary>
    public IReadOnlyList<DailyDay>? Days { get => (IReadOnlyList<DailyDay>?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    /// <summary>The month before's kWh a day, the whole of it.</summary>
    public IReadOnlyList<double>? Previous { get => (IReadOnlyList<double>?)GetValue(PreviousProperty); set => SetValue(PreviousProperty, value); }

    /// <summary>How much of the month is wiped in, 0 to 1.</summary>
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    /// <summary>The day the tip is on, 0 based: the pointer's, else the last.</summary>
    internal int TipIndex => _tip ?? Math.Max(0, (Days?.Count ?? 0) - 1);

    private void OnDays()
    {
        _tip = null;
        var days = Days ?? [];
        AutomationProperties.SetHelpText(this, days.Count == 0 ? Format.NoReading
            : $"{Format.Kwh(days.Sum(d => d.Kwh), CultureInfo.CurrentCulture)} kWh over {days.Count} days, the most {Format.Kwh(days.Max(d => d.Kwh), CultureInfo.CurrentCulture)} kWh");
    }

    private int MonthDays => Days is { Count: > 0 } days ? DateTime.DaysInMonth(days[0].Day.Year, days[0].Day.Month) : 30;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var days = Days ?? [];
        if (days.Count == 0 || ActualWidth <= 0) return;
        var x = e.GetPosition(this).X / (ActualWidth / DailyScale.Width);
        var i = Math.Clamp((int)Math.Round((x - DailyScale.X0) / (DailyScale.X1 - DailyScale.X0) * (MonthDays - 1)), 0, days.Count - 1);
        if (i == _tip) return;
        _tip = i;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _tip = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double sx = ActualWidth / DailyScale.Width, sy = ActualHeight / DailyScale.Height;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var days = Days ?? [];
        var previous = Previous ?? [];
        var monthDays = MonthDays;
        var max = DailyScale.Ceiling(days.Select(d => d.Kwh).Concat(previous).DefaultIfEmpty(0).Max());
        Point P(int day, double kwh) => new(DailyScale.X(day, monthDays) * sx, DailyScale.Y(kwh, max) * sy);

        var accentColour = ChartInk.Colour(this, "A.C.Accent", Color.FromRgb(0xD3, 0xF0, 0x3F));
        var accent = ChartInk.Brush(accentColour);
        var axis = ChartInk.Brush(this, "A.C.Text3", Color.FromArgb(0x70, 0xF3, 0xF4, 0xF6));
        var grid = ChartInk.Pen(ChartInk.Brush(this, "A.C.Grid", Color.FromArgb(0x0F, 255, 255, 255)), 1);
        var culture = CultureInfo.CurrentCulture;
        foreach (var v in DailyScale.Grid(max))
        {
            var y = DailyScale.Y(v, max) * sy;
            dc.DrawLine(grid, new Point(DailyScale.X0 * sx, y), new Point(DailyScale.X1 * sx, y));
            ChartInk.At(dc, ChartInk.Text(this, v.ToString("0.##", culture) + " kWh", 11, axis), 0, y + 4);
        }
        foreach (var d in DailyScale.Labels(monthDays))
            ChartInk.At(dc, ChartInk.Text(this, d.ToString(culture), 11, axis), DailyScale.X(d, monthDays) * sx, DailyScale.AxisBaseline * sy, 1);

        if (previous.Count > 1)
        {
            var prevPen = ChartInk.Pen(ChartInk.Brush(this, "A.C.PrevLine", Color.FromArgb(0x42, 255, 255, 255)), 1.5, [4 / 1.5, 5 / 1.5]);
            dc.DrawGeometry(null, prevPen, ChartInk.Smooth([.. previous.Take(monthDays).Select((v, i) => P(i + 1, v))]));
        }
        if (days.Count == 0)
        {
            ChartInk.At(dc, ChartInk.Text(this, "No history this month yet", 13, axis), ActualWidth / 2, ActualHeight / 2, 1);
            return;
        }

        var top = days.Select((d, i) => P(i + 1, d.Kwh)).ToList();
        dc.PushClip(new RectangleGeometry(new Rect(0, -10, Math.Clamp(Reveal, 0, 1) * ActualWidth, ActualHeight + 20)));
        // The HTML's hatch: 5 px accent stripes every 10 px at minus 45 degrees.
        var hatch = ChartInk.Frozen(new DrawingBrush(new GeometryDrawing(accent, null, new RectangleGeometry(new Rect(0, 0, 5, 10))))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 10, 10), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 10, 10), ViewboxUnits = BrushMappingMode.Absolute, Transform = new RotateTransform(-45), Opacity = .85,
        });
        if (top.Count > 1)
        {
            dc.DrawGeometry(hatch, null, ChartInk.Smooth(top, close: true, baseY: DailyScale.Y1 * sy));
            dc.DrawGeometry(null, ChartInk.Pen(accent, 2, round: true), ChartInk.Smooth(top));
        }
        for (var i = 0; i < top.Count; i++)
        {
            if (i % 3 == 0 || i == top.Count - 1) dc.DrawEllipse(accent, null, top[i], 3.5, 3.5);
        }
        dc.Pop();

        if (Reveal < .98) return;
        var t = Math.Min(TipIndex, days.Count - 1);
        var (x, yTip) = (top[t].X, top[t].Y);
        dc.DrawLine(ChartInk.Pen(ChartInk.Brush(this, "A.C.GuideStrong", Color.FromArgb(0x59, 255, 255, 255)), 1, [3, 4]),
            new Point(x, DailyScale.Y0 * sy), new Point(x, DailyScale.Y1 * sy));
        dc.DrawEllipse(ChartInk.Brush(this, "A.C.MarkerFill", Color.FromRgb(0x1B, 0x1D, 0x20)), ChartInk.Pen(accent, 2.5), new Point(x, yTip), 5, 5);
        var label = ChartInk.Text(this, days[t].Tip(culture), 11.5,
            ChartInk.Brush(this, "A.C.Ink", Color.FromRgb(0x16, 0x18, 0x1C)), semi: true);
        var tw = label.Width + 18;
        var tx = Math.Min(Math.Max(x - tw / 2, DailyScale.X0 * sx), DailyScale.X1 * sx - tw);
        ChartInk.Pill(dc, new Rect(tx, yTip - 34, tw, 22), ChartInk.Brush(this, "A.C.Pill", Color.FromRgb(0xF4, 0xF4, 0xF0)));
        ChartInk.At(dc, label, tx + tw / 2, yTip - 19, 1);
    }
}
