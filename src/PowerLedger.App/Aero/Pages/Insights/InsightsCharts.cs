using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The Bill forecast's band (Aero look design §4): a track from nothing to <see cref="Maximum"/>, the likely range lit in
/// the accent, and a pill at the forecast itself, so the range reads as a span around one figure rather than two figures.
/// </summary>
internal sealed class ForecastBand : Instrument
{
    public static readonly DependencyProperty LowProperty = Register(nameof(Low), 0.0, typeof(ForecastBand));
    public static readonly DependencyProperty HighProperty = Register(nameof(High), 0.0, typeof(ForecastBand));
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0.0, typeof(ForecastBand));
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), 1.0, typeof(ForecastBand));

    public const double Track = 8;
    public const double Marker = 18;

    public double Low { get => (double)GetValue(LowProperty); set => SetValue(LowProperty, value); }

    public double High { get => (double)GetValue(HighProperty); set => SetValue(HighProperty, value); }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>Where <paramref name="value"/> falls across <paramref name="width"/>, clamped to it.</summary>
    internal static double At(double value, double maximum, double width)
        => maximum > 0 && double.IsFinite(value) ? Math.Clamp(value / maximum, 0, 1) * width : 0;

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, Marker);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var middle = Marker / 2;
        dc.DrawRoundedRectangle(LineBrush, null, new Rect(0, middle - Track / 2, width, Track), Track / 2, Track / 2);

        var low = At(Low, Maximum, width);
        var high = At(High, Maximum, width);
        dc.PushOpacity(0.45);
        dc.DrawRoundedRectangle(AccentBrush, null, new Rect(low, middle - Track / 2, Math.Max(Track, high - low), Track), Track / 2, Track / 2);
        dc.Pop();

        var at = Math.Clamp(At(Value, Maximum, width), 2, Math.Max(2, width - 2));
        dc.DrawRoundedRectangle(AccentBrush, null, new Rect(at - 2, 0, 4, Marker), 2, 2);
    }
}

/// <summary>
/// An unusual hour against its normal (Aero look design §4): the hour's energy as a bar in the accent, over a track to
/// the page's largest, with a tick where that hour and weekday normally stand, so "3 times normal" is seen, not read.
/// </summary>
internal sealed class NormalChart : Instrument
{
    public static readonly DependencyProperty KwhProperty = Register(nameof(Kwh), 0.0, typeof(NormalChart));
    public static readonly DependencyProperty NormalKwhProperty = Register(nameof(NormalKwh), 0.0, typeof(NormalChart));
    public static readonly DependencyProperty MaximumProperty = Register(nameof(Maximum), 1.0, typeof(NormalChart));

    public const double Bar = 8;
    public const double Tick = 16;

    public double Kwh { get => (double)GetValue(KwhProperty); set => SetValue(KwhProperty, value); }

    public double NormalKwh { get => (double)GetValue(NormalKwhProperty); set => SetValue(NormalKwhProperty, value); }

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, Tick);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var middle = Tick / 2;
        var track = new Rect(0, middle - Bar / 2, width, Bar);
        dc.DrawRoundedRectangle(LineBrush, null, track, Bar / 2, Bar / 2);
        dc.DrawRoundedRectangle(AccentBrush, null, track with { Width = Math.Max(Bar, ForecastBand.At(Kwh, Maximum, width)) }, Bar / 2, Bar / 2);
        var normal = Math.Clamp(ForecastBand.At(NormalKwh, Maximum, width), 1, Math.Max(1, width - 1));
        dc.DrawRoundedRectangle(StrongLineBrush, null, new Rect(normal - 1, 0, 2, Tick), 1, 1);
    }
}
