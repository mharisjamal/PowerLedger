using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PowerLedger.App;

/// <summary>
/// The big live number (spec §9): light numerals with a smaller "W". It settles from zero over 900 ms the first time it
/// shows a value, unless Windows' client-area animation is off, and after that follows each reading without animating.
/// </summary>
internal sealed class LiveReadout : Instrument
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LiveReadout), new FrameworkPropertyMetadata(0.0, OnValueChanged));
    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(double), typeof(LiveReadout),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty NumberSizeProperty = Register(nameof(NumberSize), 84.0, typeof(LiveReadout));

    private bool _settled;

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Shown { get => (double)GetValue(ShownProperty); set => SetValue(ShownProperty, value); }

    public double NumberSize { get => (double)GetValue(NumberSizeProperty); set => SetValue(NumberSizeProperty, value); }

    internal override string Describe()
        => double.IsFinite(Value) ? Format.Watts(Value, CultureInfo.CurrentCulture) + " watts" : "Waiting for a reading";

    protected override Size MeasureOverride(Size availableSize)
    {
        var number = Number();
        var unit = Unit();
        return new Size(number.WidthIncludingTrailingWhitespace + 8 + unit.Width, number.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var number = Number();
        var unit = Unit();
        dc.DrawText(number, new Point(0, 0));
        dc.DrawText(unit, new Point(number.WidthIncludingTrailingWhitespace + 8, number.Baseline - unit.Baseline));
    }

    private FormattedText Number() => Text(Format.Watts(Shown, CultureInfo.CurrentCulture), NumberSize, InkBrush, FontWeights.Light);

    private FormattedText Unit() => Text("W", NumberSize * 0.31, LabelBrush);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var readout = (LiveReadout)d;
        var value = (double)e.NewValue;
        if (!readout._settled && value > 0 && SystemParameters.ClientAreaAnimation)
        {
            readout._settled = true;
            readout.BeginAnimation(ShownProperty, new DoubleAnimation(0, value, TimeSpan.FromMilliseconds(900))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            return;
        }
        if (value > 0) readout._settled = true;
        readout.BeginAnimation(ShownProperty, null);
        readout.Shown = value;
    }
}
