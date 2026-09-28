using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The overlay's watts, whose digits roll to their new values on a spring like an odometer, as the prototype's
/// RollingNumber: each digit a strip of 0 to 9 behind a one-line window with soft top and bottom edges. Under reduced
/// motion the digits change in place. Agent G's RollingNumber comes later (Plan S G6); this one is the overlay's until the
/// lead folds the two together.
/// </summary>
internal sealed class OverlayDigits : StackPanel
{
    private static readonly Brush SoftEdges = Edges();
    private readonly List<TranslateTransform> _shifts = [];
    private int _value = -1;
    private double _line;

    public OverlayDigits()
    {
        Orientation = Orientation.Horizontal;
        System.Windows.Documents.Typography.SetNumeralAlignment(this, FontNumeralAlignment.Tabular);
        OpacityMask = SoftEdges;
    }

    /// <summary>The digits' size, in device-independent pixels.</summary>
    public double DigitSize { get; set; } = 21;

    /// <summary>The number shown, or -1 before any.</summary>
    public int Value => _value;

    /// <summary>Shows <paramref name="value"/> (0 or more), rolling each digit that changed unless
    /// <paramref name="animate"/> is false or motion is reduced.</summary>
    public void Set(int value, bool animate = true)
    {
        value = Math.Max(0, value);
        if (value == _value) return;
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Length != _shifts.Count)
        {
            Rebuild(text.Length);
            animate = false;
        }
        for (var i = 0; i < text.Length; i++)
        {
            AeroMotion.Move(_shifts[i], TranslateTransform.YProperty, -(text[i] - '0') * _line, animate ? AeroMotion.NumberRoll : 0, AeroMotion.Spring);
        }
        _value = value;
    }

    private void Rebuild(int count)
    {
        Children.Clear();
        _shifts.Clear();
        _line = Math.Ceiling(DigitSize * 1.25);
        var probe = new TextBlock { Text = "0", FontSize = DigitSize, FontWeight = FontWeights.SemiBold };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Ceiling(probe.DesiredSize.Width);
        for (var i = 0; i < count; i++)
        {
            var strip = new StackPanel { Width = width };
            for (var digit = 0; digit <= 9; digit++)
            {
                strip.Children.Add(new TextBlock
                {
                    Text = digit.ToString(CultureInfo.InvariantCulture), FontSize = DigitSize, FontWeight = FontWeights.SemiBold,
                    Height = _line, LineHeight = _line, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, TextAlignment = TextAlignment.Center,
                });
            }
            var shift = new TranslateTransform();
            strip.RenderTransform = shift;
            var window = new Canvas { Width = width, Height = _line, ClipToBounds = true };
            window.Children.Add(strip);
            Children.Add(window);
            _shifts.Add(shift);
        }
    }

    /// <summary>A digit rolls in out of the glass rather than out of a hard slot.</summary>
    private static Brush Edges()
    {
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        fade.GradientStops.Add(new GradientStop(Colors.Black, .18));
        fade.GradientStops.Add(new GradientStop(Colors.Black, .82));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        fade.Freeze();
        return fade;
    }
}
