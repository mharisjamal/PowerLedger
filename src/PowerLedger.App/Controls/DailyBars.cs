using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>The report's daily bars (spec §9): one amber bar per day of the range over a round kWh scale, the highest day
/// labelled, and day numbers along the bottom as often as they fit. Midnight gives it a top and a bottom colour, and each
/// bar runs from the brighter top down to the deeper foot; Classic sets neither, and its bars stay the accent.</summary>
internal sealed class DailyBars : Instrument
{
    public static readonly DependencyProperty DaysProperty = Register<IReadOnlyList<DayBar>>(nameof(Days), [], typeof(DailyBars));
    public static readonly DependencyProperty BarTopBrushProperty = Register<Brush?>(nameof(BarTopBrush), null, typeof(DailyBars));
    public static readonly DependencyProperty BarBottomBrushProperty = Register<Brush?>(nameof(BarBottomBrush), null, typeof(DailyBars));

    private const double Left = 40;
    private const double RightInset = 14;
    private const double Top = 20;
    private const double AxisRoom = 22;

    public IReadOnlyList<DayBar> Days { get => (IReadOnlyList<DayBar>)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    /// <summary>A bar's colour at its top; with <see cref="BarBottomBrush"/>, the bars are a gradient between the two.</summary>
    public Brush? BarTopBrush { get => (Brush?)GetValue(BarTopBrushProperty); set => SetValue(BarTopBrushProperty, value); }

    public Brush? BarBottomBrush { get => (Brush?)GetValue(BarBottomBrushProperty); set => SetValue(BarBottomBrushProperty, value); }

    /// <summary>What a bar is painted with: top to bottom across each bar when both ends are solid colours, else the accent.</summary>
    internal static Brush BarBrush(Brush? top, Brush? bottom, Brush accent)
    {
        if (top is not SolidColorBrush { Color: var high } || bottom is not SolidColorBrush { Color: var low }) return accent;
        var brush = new LinearGradientBrush(high, low, new Point(0, 0), new Point(0, 1));
        brush.Freeze();
        return brush;
    }

    internal override string Describe()
    {
        var days = Days;
        if (!days.Any(d => d.Kwh > 0)) return "Daily energy: no readings.";
        var highest = days.MaxBy(d => d.Kwh)!;
        var culture = CultureInfo.CurrentCulture;
        return $"Daily energy over {days.Count.ToString(culture)} days; the highest was {Format.Kwh(highest.Kwh, culture)} kWh on {highest.Day.ToString("d MMM", culture)}.";
    }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, 170);

    protected override void OnRender(DrawingContext dc)
    {
        var days = Days;
        var culture = CultureInfo.CurrentCulture;
        var right = ActualWidth - RightInset;
        var bottom = ActualHeight - AxisRoom;
        var (max, step) = Geometry.ChartScale(days.Count > 0 ? days.Max(d => d.Kwh) : 0, 0.1);
        double Y(double kwh) => bottom - (bottom - Top) * Math.Clamp(kwh / max, 0, 1);

        var grid = Line(LineBrush);
        for (var value = 0.0; value <= max + step / 1000; value += step)
        {
            dc.DrawLine(grid, new Point(Left, Y(value)), new Point(right, Y(value)));
            DrawText(dc, Format.Scale(value, step, culture), Left - 8, Y(value) - 7, LabelBrush, TextAlignment.Right);
        }
        if (days.Count == 0) return;

        var slot = (right - Left) / days.Count;
        var gap = Math.Min(slot * 0.3, 6);
        var every = Geometry.LabelEvery(days.Count, slot, 22);
        var bar = BarBrush(BarTopBrush, BarBottomBrush, AccentBrush);
        for (var i = 0; i < days.Count; i++)
        {
            var x = Left + i * slot;
            if (days[i].Kwh > 0) dc.DrawRectangle(bar, null, new Rect(x + gap / 2, Y(days[i].Kwh), Math.Max(1, slot - gap), bottom - Y(days[i].Kwh)));
            if (i % every == 0) DrawText(dc, days[i].Day.Day.ToString(culture), x + slot / 2, bottom + 5, LabelBrush, TextAlignment.Center);
        }
        var highest = Enumerable.Range(0, days.Count).MaxBy(i => days[i].Kwh);
        if (days[highest].Kwh > 0)
        {
            DrawText(dc, Format.Kwh(days[highest].Kwh, culture), Left + (highest + 0.5) * slot, Y(days[highest].Kwh) - 15, LabelBrush, TextAlignment.Center);
        }
    }
}
