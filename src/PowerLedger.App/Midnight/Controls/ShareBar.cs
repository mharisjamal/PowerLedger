using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>A table row's share (plan O M1-2): a thin rounded track, 4 px since 0.8.1 as the reference's, with the part's
/// colour along the share of it.</summary>
internal sealed class ShareBar : Instrument
{
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0.0, typeof(ShareBar));
    public static readonly DependencyProperty FillBrushProperty = Register<Brush>(nameof(FillBrush), Brushes.Gray, typeof(ShareBar));
    public static readonly DependencyProperty TrackBrushProperty = Register<Brush>(nameof(TrackBrush), Brushes.Transparent, typeof(ShareBar));

    public const double BarHeight = 4;

    /// <summary>The share, 0 to 1.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    internal override string Describe() => Format.Percent(Value, CultureInfo.CurrentCulture) + ".";

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, BarHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (!(width > 0)) return;
        var radius = BarHeight / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, BarHeight), radius, radius);
        var fill = double.IsFinite(Value) ? Math.Clamp(Value, 0, 1) * width : 0;
        if (fill > 0) dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, 0, Math.Max(fill, BarHeight), BarHeight), radius, radius);
    }
}
