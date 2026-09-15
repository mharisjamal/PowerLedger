using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>The power budget (spec §9 BudgetBar): one segment per band in proportion to its watts, 2 px apart, over a watt ruler that ends at the total.</summary>
internal sealed class BudgetBar : Instrument
{
    public static readonly DependencyProperty RowsProperty = Register<IReadOnlyList<BudgetRow>>(nameof(Rows), [], typeof(BudgetBar));
    public static readonly DependencyProperty TotalProperty = Register(nameof(Total), 0.0, typeof(BudgetBar));

    private const double BarHeight = 14;
    private const double Gap = 2;
    private const double RulerTop = BarHeight + 8;

    public IReadOnlyList<BudgetRow> Rows { get => (IReadOnlyList<BudgetRow>)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }

    public double Total { get => (double)GetValue(TotalProperty); set => SetValue(TotalProperty, value); }

    internal override string Describe()
        => Rows.Count == 0 ? "Power budget: no reading." : "Power budget: " + string.Join(", ", Rows.Select(r => $"{r.Name} {r.Watts} ({r.Percent})")) + ".";

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, RulerTop + 22);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var shown = Rows.Where(r => r.Share > 0).ToList();
        var room = width - Gap * Math.Max(0, shown.Count - 1);
        var x = 0.0;
        foreach (var row in shown)
        {
            var segment = room * row.Share;
            dc.DrawRectangle(BrushFor(row.Part), null, new Rect(x, 0, Math.Max(0, segment), BarHeight));
            x += segment + Gap;
        }

        var tick = Line(StrongLineBrush);
        dc.DrawLine(tick, new Point(0, RulerTop), new Point(width, RulerTop));
        if (!(Total > 0)) return;
        var totalLabel = Text($"{Format.Watts(Total, CultureInfo.CurrentCulture)} W", 10, LabelBrush);
        dc.DrawText(totalLabel, new Point(width - totalLabel.Width, RulerTop + 8));
        var step = Geometry.NiceStep(Total, 8);
        var index = 0;
        for (var value = 0.0; value <= Total + 1e-9; value += step, index++)
        {
            var at = Geometry.ScaleX(value, Total, 0, width);
            var labelled = index % 2 == 0;
            dc.DrawLine(tick, new Point(at, RulerTop), new Point(at, RulerTop + (labelled ? 8 : 4)));
            if (labelled && at < width - totalLabel.Width - 12)
            {
                DrawText(dc, Format.WholeWatts(value, CultureInfo.CurrentCulture), at, RulerTop + 8, LabelBrush, index == 0 ? TextAlignment.Left : TextAlignment.Center);
            }
        }
    }

    private Brush BrushFor(Part part) => part switch
    {
        Part.Cpu => CpuBrush,
        Part.Gpu => GpuBrush,
        Part.Display => DisplayBrush,
        _ => RestBrush,
    };
}
