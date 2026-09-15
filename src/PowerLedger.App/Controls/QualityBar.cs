using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>The quality mix (spec §9): one bar split into the measured, calibrated and estimated shares of the on-time, in
/// the quality colours, 2 px apart. With no readings it is an empty outline.</summary>
internal sealed class QualityBar : Instrument
{
    public static readonly DependencyProperty MixProperty = Register(nameof(Mix), new QualityMix(0, 0, 0), typeof(QualityBar));
    public static readonly DependencyProperty MeasuredBrushProperty = Register<Brush>(nameof(MeasuredBrush), Brushes.Green, typeof(QualityBar));
    public static readonly DependencyProperty CalibratedBrushProperty = Register<Brush>(nameof(CalibratedBrush), Brushes.SteelBlue, typeof(QualityBar));
    public static readonly DependencyProperty EstimatedBrushProperty = Register<Brush>(nameof(EstimatedBrush), Brushes.Tan, typeof(QualityBar));

    private const double Gap = 2;

    public QualityMix Mix { get => (QualityMix)GetValue(MixProperty); set => SetValue(MixProperty, value); }

    public Brush MeasuredBrush { get => (Brush)GetValue(MeasuredBrushProperty); set => SetValue(MeasuredBrushProperty, value); }

    public Brush CalibratedBrush { get => (Brush)GetValue(CalibratedBrushProperty); set => SetValue(CalibratedBrushProperty, value); }

    public Brush EstimatedBrush { get => (Brush)GetValue(EstimatedBrushProperty); set => SetValue(EstimatedBrushProperty, value); }

    internal override string Describe()
    {
        var culture = CultureInfo.CurrentCulture;
        var mix = Mix;
        return $"Quality: {Format.Percent(mix.Measured, culture)} measured, {Format.Percent(mix.Calibrated, culture)} calibrated, {Format.Percent(mix.Estimated, culture)} estimated.";
    }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, 12);

    protected override void OnRender(DrawingContext dc)
    {
        (double Share, Brush Brush)[] parts = [(Mix.Measured, MeasuredBrush), (Mix.Calibrated, CalibratedBrush), (Mix.Estimated, EstimatedBrush)];
        var shown = parts.Where(p => p.Share > 0).ToList();
        if (shown.Count == 0)
        {
            dc.DrawRectangle(null, Line(LineBrush), new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1)));
            return;
        }
        var total = shown.Sum(p => p.Share);
        var room = ActualWidth - Gap * (shown.Count - 1);
        var x = 0.0;
        foreach (var (share, brush) in shown)
        {
            var width = room * share / total;
            dc.DrawRectangle(brush, null, new Rect(x, 0, Math.Max(0, width), ActualHeight));
            x += width + Gap;
        }
    }
}
