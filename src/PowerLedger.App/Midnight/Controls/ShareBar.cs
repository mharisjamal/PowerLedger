using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>A table row's share (plan O M1-2): a 6 px rounded track with the part's colour along the share of it.</summary>
internal sealed class ShareBar : Instrument
{
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0.0, typeof(ShareBar));
    public static readonly DependencyProperty FillBrushProperty = Register<Brush>(nameof(FillBrush), Brushes.Gray, typeof(ShareBar));
    public static readonly DependencyProperty TrackBrushProperty = Register<Brush>(nameof(TrackBrush), Brushes.Transparent, typeof(ShareBar));

    /// <summary>The share, 0 to 1.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    internal override string Describe() => Format.Percent(Value, CultureInfo.CurrentCulture) + ".";

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, HatchBar.BarHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (!(width > 0)) return;
        var radius = HatchBar.BarHeight / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, HatchBar.BarHeight), radius, radius);
        var fill = double.IsFinite(Value) ? Math.Clamp(Value, 0, 1) * width : 0;
        if (fill > 0) dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, 0, Math.Max(fill, HatchBar.BarHeight), HatchBar.BarHeight), radius, radius);
    }
}
