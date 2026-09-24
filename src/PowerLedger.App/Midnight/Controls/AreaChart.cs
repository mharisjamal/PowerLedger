using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// "Power over time" (plan O M1-2, after the reference in 0.8.1): the total as one smooth 2 px line running from the
/// accent into the violet over a gradient that fades to nothing, sleep faintly hatched, a dashed line at now, and the axes
/// the model gives in small grey words. The parts are the History page's and the table's to show, not this chart's. The pointer or the arrow keys pick a bucket: a
/// ringed dot marks it on the line, a dotted guide runs from it to the axis, and a tooltip says when and how much, so
/// the figure is reachable without a mouse. The tooltip is a ToolTip of the window's, so it takes the window's bubble.
/// </summary>
internal sealed class AreaChart : Instrument
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
        nameof(Model), typeof(ChartModel), typeof(AreaChart), new FrameworkPropertyMetadata(ChartModel.Empty, FrameworkPropertyMetadataOptions.AffectsRender, OnModelChanged));
    public static readonly DependencyProperty FromProperty = Register<DateTimeOffset?>(nameof(From), null, typeof(AreaChart));
    public static readonly DependencyProperty ZoneProperty = Register(nameof(Zone), TimeZoneInfo.Local, typeof(AreaChart));
    public static readonly DependencyProperty SecondBrushProperty = Register<Brush?>(nameof(SecondBrush), null, typeof(AreaChart));
    public static readonly DependencyProperty RingBrushProperty = Register<Brush?>(nameof(RingBrush), null, typeof(AreaChart));
    public static readonly DependencyProperty FillTopBrushProperty = Register<Brush>(nameof(FillTopBrush), Brushes.Transparent, typeof(AreaChart));
    public static readonly DependencyProperty FillBottomBrushProperty = Register<Brush>(nameof(FillBottomBrush), Brushes.Transparent, typeof(AreaChart));
    public static readonly DependencyProperty HatchBrushProperty = Register<Brush>(nameof(HatchBrush), Brushes.Gray, typeof(AreaChart));
    public static readonly DependencyProperty EmptyTextProperty = Register(nameof(EmptyText), "No history yet", typeof(AreaChart));
    public static readonly DependencyProperty CultureProperty = Register(nameof(Culture), CultureInfo.CurrentCulture, typeof(AreaChart));

    private const double Left = 48;
    private const double RightInset = 16;
    private const double Top = 18;
    private const double AxisRoom = 28;
    private const double PlotHeight = 260;
    private const double LabelSize = 11;

    private int _hover = -1;
    private ToolTip? _tip;

    public AreaChart()
    {
        Focusable = true;
    }

    public ChartModel Model { get => (ChartModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    /// <summary>When the first bucket starts, for the tooltip's time; without it the tooltip gives the value alone.</summary>
    public DateTimeOffset? From { get => (DateTimeOffset?)GetValue(FromProperty); set => SetValue(FromProperty, value); }

    public TimeZoneInfo Zone { get => (TimeZoneInfo)GetValue(ZoneProperty); set => SetValue(ZoneProperty, value); }

    /// <summary>The colour the line runs into at the right; the accent all along when unset.</summary>
    public Brush? SecondBrush { get => (Brush?)GetValue(SecondBrushProperty); set => SetValue(SecondBrushProperty, value); }

    /// <summary>The ring round the hovered dot: the tooltip's colour, white on the dark page; the ink when unset.</summary>
    public Brush? RingBrush { get => (Brush?)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }

    public Brush FillTopBrush { get => (Brush)GetValue(FillTopBrushProperty); set => SetValue(FillTopBrushProperty, value); }

    public Brush FillBottomBrush { get => (Brush)GetValue(FillBottomBrushProperty); set => SetValue(FillBottomBrushProperty, value); }

    public Brush HatchBrush { get => (Brush)GetValue(HatchBrushProperty); set => SetValue(HatchBrushProperty, value); }

    /// <summary>What the plot says when there is nothing to draw.</summary>
    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    /// <summary>The culture the axis and the tooltip write in: the page's, which built the model's ticks, not the thread's.</summary>
    public CultureInfo Culture { get => (CultureInfo)GetValue(CultureProperty); set => SetValue(CultureProperty, value); }

    /// <summary>The bucket the crosshair is on, or -1.</summary>
    internal int Hovered => _hover;

    /// <summary>The tooltip, once a bucket has been hovered.</summary>
    internal ToolTip? Tip => _tip;

    /// <summary>What the tooltip says, its lines joined: "12:00 Power: 92 W".</summary>
    internal string TipText => _tip?.Content is StackPanel lines ? string.Join(" ", lines.Children.OfType<TextBlock>().Select(line => line.Text)) : "";

    internal override string Describe() => Model.Description + (_hover >= 0 && _hover < Model.Buckets.Count ? $" At {HoverLabel(_hover)}." : "");

    /// <summary>Puts the crosshair on <paramref name="index"/>, or takes it away with -1.</summary>
    internal void Hover(int index)
    {
        if (index >= Model.Buckets.Count) index = -1;
        if (index == _hover) return;
        _hover = index;
        InvalidateVisual();
        ShowTip();
    }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, PlotHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var model = Model;
        var buckets = model.Buckets;
        var capacity = Math.Max(1, model.Capacity);
        var right = ActualWidth - RightInset;
        var bottom = ActualHeight - AxisRoom;
        if (!(right > Left) || !(bottom > Top)) return;
        var culture = Culture;
        var (max, step) = Scale(model);
        double X(double at) => AreaGeometry.X(at, capacity, Left, right);
        double Y(double value) => AreaGeometry.Y(value, max, Top, bottom);

        var grid = Line(LineBrush);
        foreach (var (value, label) in AreaGeometry.YLabels(max, step, Charts.Symbol(model.Unit), culture))
        {
            dc.DrawLine(grid, new Point(Left, Y(value)), new Point(right, Y(value)));
            DrawText(dc, label, Left - 10, Y(value) - 8, LabelBrush, TextAlignment.Right, LabelSize);
        }
        for (var i = 0; i < model.Ticks.Count; i++)
        {
            var tick = model.Ticks[i];
            var x = X(tick.At);
            if (tick.At > 0 && tick.At < capacity) dc.DrawLine(grid, new Point(x, Top), new Point(x, bottom));
            var label = Text(tick.Label, LabelSize, LabelBrush);
            var room = (i + 1 < model.Ticks.Count ? X(model.Ticks[i + 1].At) : right) - x;
            if (label.Width + 8 <= room) dc.DrawText(label, new Point(x + 4, bottom + 8));
        }
        if (buckets.Count == 0)
        {
            DrawText(dc, EmptyText, (Left + right) / 2, (Top + bottom) / 2 - 7, LabelBrush, TextAlignment.Center, 12);
            return;
        }

        foreach (var (start, end) in AreaGeometry.AsleepRuns(buckets, model.Bucket))
        {
            dc.DrawRectangle(HatchBar.Hatch(HatchBrush), null, new Rect(new Point(X(start), Top), new Point(X(end), bottom)));
        }

        var line = AreaGeometry.Line(buckets.Select(b => b.Total).ToArray(), capacity, max, Left, right, Top, bottom);
        var fill = new LinearGradientBrush(ColourOf(FillTopBrush), ColourOf(FillBottomBrush), new Point(0, 0), new Point(0, 1));
        fill.Freeze();
        dc.DrawGeometry(fill, null, Curve(line, bottom));
        var stroke = new Pen(LineBrushFor(line), 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, stroke, Curve(line, null));

        if (model.NowAt is { } nowAt)
        {
            var nowX = X(Math.Clamp(nowAt, 0, capacity));
            dc.DrawLine(Line(AccentBrush, 1, new DashStyle([3, 3], 0)), new Point(nowX, Top), new Point(nowX, bottom));
            var now = Text("now", LabelSize, AccentTextOrAccent);
            dc.DrawText(now, new Point(nowX + 5 + now.Width <= ActualWidth ? nowX + 5 : nowX - 5 - now.Width, Top - 2));
        }

        if (_hover >= 0 && _hover < line.Count)
        {
            var at = line[_hover];
            var dotted = Line(LabelBrush, 1, new DashStyle([1, 3], 0));
            dc.DrawLine(Line(LineBrush), new Point(at.X, Top), new Point(at.X, bottom));
            dc.DrawLine(dotted, new Point(Left, at.Y), new Point(right, at.Y));
            dc.DrawEllipse(AccentBrush, new Pen(RingBrush ?? InkBrush, 2), at, 5, 5);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Hover(AreaGeometry.BucketAt(e.GetPosition(this).X, Model.Buckets.Count, Model.Capacity, Left, ActualWidth - RightInset) ?? -1);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!IsKeyboardFocused) Hover(-1);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
    }

    /// <summary>Left and Right walk the buckets from the one hovered, else from now or the last; Escape lets go.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        var count = Model.Buckets.Count;
        if (count > 0)
        {
            var start = _hover >= 0 ? _hover : Model.NowAt is { } now ? Math.Clamp((int)now, 0, count - 1) : count - 1;
            switch (e.Key)
            {
                case Key.Left:
                    Hover(_hover >= 0 ? Math.Max(0, start - 1) : start);
                    e.Handled = true;
                    break;
                case Key.Right:
                    Hover(_hover >= 0 ? Math.Min(count - 1, start + 1) : start);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Hover(-1);
                    e.Handled = true;
                    break;
            }
        }
        base.OnKeyDown(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (!IsMouseOver) Hover(-1);
    }

    private static void OnModelChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        var chart = (AreaChart)element;
        if (chart._hover < 0) return;
        // The same range read again (the minute's re-read of the hour or the day) has its buckets in the same places: the
        // crosshair stays and the tooltip says the new figure. Another range's model would point at a bucket that isn't there.
        if (e.OldValue is ChartModel old && e.NewValue is ChartModel now && SameLayout(old, now) && chart._hover < now.Buckets.Count)
        {
            chart.ShowTip();
            return;
        }
        chart.Hover(-1);
    }

    private static bool SameLayout(ChartModel a, ChartModel b) => a.Bucket == b.Bucket && a.Capacity == b.Capacity && a.Ticks.SequenceEqual(b.Ticks);

    private static (double Max, double Step) Scale(ChartModel model)
        => Geometry.ChartScale(model.Buckets.Count > 0 ? model.Buckets.Max(b => b.Total) : 0, Charts.Floor(model.Unit));

    /// <summary>The line through <paramref name="points"/> as one smooth curve (AreaGeometry.Smooth); with a
    /// <paramref name="baseline"/>, closed down to it and back, for the fill under the line.</summary>
    private static StreamGeometry Curve(IReadOnlyList<Point> points, double? baseline)
    {
        var shape = new StreamGeometry();
        if (points.Count > 0)
        {
            using var g = shape.Open();
            var filled = baseline is not null;
            g.BeginFigure(points[0], isFilled: filled, isClosed: filled);
            foreach (var (c1, c2, end) in AreaGeometry.Smooth(points)) g.BezierTo(c1, c2, end, isStroked: !filled, isSmoothJoin: true);
            if (baseline is { } y)
            {
                g.LineTo(new Point(points[^1].X, y), isStroked: false, isSmoothJoin: false);
                g.LineTo(new Point(points[0].X, y), isStroked: false, isSmoothJoin: false);
            }
        }
        shape.Freeze();
        return shape;
    }

    private static Color ColourOf(Brush brush) => (brush as SolidColorBrush)?.Color ?? Colors.Transparent;

    /// <summary>The line's brush: the accent into <see cref="SecondBrush"/> across the line's own width, or the accent.</summary>
    private Brush LineBrushFor(IReadOnlyList<Point> line)
    {
        if (SecondBrush is null || line.Count < 2) return AccentBrush;
        var brush = new LinearGradientBrush(ColourOf(AccentBrush), ColourOf(SecondBrush), new Point(line[0].X, 0), new Point(Math.Max(line[^1].X, line[0].X + 1), 0))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>The tooltip's words: the time, quieter, over the figure named ("Power: 92 W"), in the window's tooltip colours.</summary>
    private StackPanel TipLines(int index)
    {
        var lines = new StackPanel();
        if (AreaGeometry.HoverWhen(From, Model.Bucket, index, Model.Capacity, Zone, Culture) is { } when)
        {
            var time = new TextBlock { Text = when, Margin = new Thickness(0, 0, 0, 1) };
            time.SetResourceReference(StyleProperty, "M.Tip.Muted");
            lines.Children.Add(time);
        }
        lines.Children.Add(new TextBlock { Text = AreaGeometry.HoverAmount(Model.Buckets[index].Total, Model.Unit, Culture), FontWeight = FontWeights.SemiBold });
        return lines;
    }

    private string HoverLabel(int index)
        => AreaGeometry.HoverLabel(From, Model.Bucket, index, Model.Capacity, Model.Buckets[index].Total, Model.Unit, Zone, Culture);

    private void ShowTip()
    {
        if (_hover < 0 || _hover >= Model.Buckets.Count || !(ActualWidth > Left + RightInset))
        {
            if (_tip is not null) _tip.IsOpen = false;
            return;
        }
        _tip ??= new ToolTip { PlacementTarget = this, Placement = PlacementMode.Relative, StaysOpen = true, Focusable = false };
        if (TryFindResource(typeof(ToolTip)) is Style style && !ReferenceEquals(_tip.Style, style)) _tip.Style = style;
        var (max, _) = Scale(Model);
        var x = AreaGeometry.X(_hover + 0.5, Math.Max(1, Model.Capacity), Left, ActualWidth - RightInset);
        var y = AreaGeometry.Y(Model.Buckets[_hover].Total, max, Top, ActualHeight - AxisRoom);
        _tip.Content = TipLines(_hover);
        _tip.HorizontalOffset = x + 10;
        _tip.VerticalOffset = y - 64;
        _tip.IsOpen = true;
    }
}
