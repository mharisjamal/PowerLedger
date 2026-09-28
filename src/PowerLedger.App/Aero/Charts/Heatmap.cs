using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The Habits heatmap (Aero look design §4): idle watt-hours as a 7 × 24 grid of rounded cells, a row a weekday starting
/// on the culture's first day of the week and a column an hour, lit in the accent by a square-root scale (so an evening
/// of 20 Wh still shows beside a night of 300) with no idle time left as a bare track. The worst window is ringed. Drawn
/// in one pass on the brushes the view gives from the palette; nothing animates, so it costs nothing at rest.
/// </summary>
internal sealed class Heatmap : Instrument
{
    public static readonly DependencyProperty ValuesProperty = Register<double[,]?>(nameof(Values), null, typeof(Heatmap));
    public static readonly DependencyProperty WorstStartProperty = Register(nameof(WorstStart), 0, typeof(Heatmap));
    public static readonly DependencyProperty WorstHoursProperty = Register(nameof(WorstHours), 0, typeof(Heatmap));

    public const double CellHeight = 16;
    public const double Gap = 3;
    public const double LabelWidth = 40;
    public const double AxisHeight = 20;

    /// <summary>Idle Wh, [(int)DayOfWeek, hour of day].</summary>
    public double[,]? Values { get => (double[,]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    public int WorstStart { get => (int)GetValue(WorstStartProperty); set => SetValue(WorstStartProperty, value); }

    /// <summary>How many hours the ringed window spans; 0 rings nothing.</summary>
    public int WorstHours { get => (int)GetValue(WorstHoursProperty); set => SetValue(WorstHoursProperty, value); }

    /// <summary>The weekdays top to bottom, from the culture's first.</summary>
    internal static IReadOnlyList<DayOfWeek> Rows(CultureInfo culture)
        => [.. Enumerable.Range(0, IdleHabits.Days).Select(i => (DayOfWeek)(((int)culture.DateTimeFormat.FirstDayOfWeek + i) % IdleHabits.Days))];

    /// <summary>How lit a cell of <paramref name="wh"/> is where the busiest holds <paramref name="most"/>: 0 for none,
    /// else from a faint 0.14 up to 1.</summary>
    internal static double Shade(double wh, double most) => wh > 0 && most > 0 ? 0.14 + 0.86 * Math.Sqrt(Math.Min(1, wh / most)) : 0;

    internal override string Describe()
    {
        if (Values is null) return "Idle energy by weekday and hour: none yet";
        return WorstHours > 0
            ? $"Idle energy by weekday and hour, most from {Hour(WorstStart)} to {Hour((WorstStart + WorstHours) % 24)}"
            : "Idle energy by weekday and hour";
    }

    protected override Size MeasureOverride(Size availableSize)
        => Fixed(availableSize, IdleHabits.Days * CellHeight + (IdleHabits.Days - 1) * Gap + AxisHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var culture = CultureInfo.CurrentCulture;
        var cellWidth = Math.Max(2, (ActualWidth - LabelWidth - (IdleHabits.Hours - 1) * Gap) / IdleHabits.Hours);
        var values = Values;
        var most = values?.Cast<double>().DefaultIfEmpty(0).Max() ?? 0;
        var rows = Rows(culture);
        double X(int hour) => LabelWidth + hour * (cellWidth + Gap);

        for (var row = 0; row < rows.Count; row++)
        {
            var day = rows[row];
            var y = row * (CellHeight + Gap);
            DrawText(dc, culture.DateTimeFormat.GetAbbreviatedDayName(day), 0, y + (CellHeight - 13) / 2, LabelBrush, size: 11);
            for (var hour = 0; hour < IdleHabits.Hours; hour++)
            {
                var cell = new Rect(X(hour), y, cellWidth, CellHeight);
                dc.DrawRoundedRectangle(LineBrush, null, cell, 3, 3);
                var shade = values is null ? 0 : Shade(values[(int)day, hour], most);
                if (shade <= 0) continue;
                dc.PushOpacity(shade);
                dc.DrawRoundedRectangle(AccentBrush, null, cell, 3, 3);
                dc.Pop();
            }
        }

        var bottom = IdleHabits.Days * CellHeight + (IdleHabits.Days - 1) * Gap;
        foreach (var hour in new[] { 0, 6, 12, 18 }) DrawText(dc, Hour(hour), X(hour), bottom + 5, LabelBrush, size: 11);

        if (values is null || most <= 0 || WorstHours <= 0) return;
        var ring = Line(StrongLineBrush, 1.5);
        var start = WorstStart;
        var left = WorstHours;
        while (left > 0)
        {
            var span = Math.Min(left, IdleHabits.Hours - start);
            var box = new Rect(X(start) - 1.5, -1.5, span * cellWidth + (span - 1) * Gap + 3, bottom + 3);
            dc.DrawRoundedRectangle(null, ring, box, 5, 5);
            left -= span;
            start = 0;
        }
    }

    private static string Hour(int hour) => new TimeOnly(hour, 0).ToString("HH:mm", CultureInfo.CurrentCulture);
}
