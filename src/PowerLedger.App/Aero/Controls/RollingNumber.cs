using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// A whole number whose digits roll to their new values on the spring, like an odometer (the demo's watts): each digit is
/// a strip of 0 to 9 behind a one-line window, softened at its top and foot so a digit rolls out of the glass rather
/// than a hard slot. Under reduced motion the digits change in place. A live reading (<see cref="Value"/>, once a second)
/// changes the digits in place too: a roll on every reading kept the compositor drawing most of each second (Plan U), so
/// only <see cref="Set"/> with animate rolls, for a change someone made. A screen reader hears the number, not the strips.
/// </summary>
public sealed class RollingNumber : StackPanel
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int),
        typeof(RollingNumber), new PropertyMetadata(0, (d, e) => ((RollingNumber)d).Set((int)e.NewValue, animate: false)));

    public static readonly DependencyProperty DigitSizeProperty = DependencyProperty.Register(nameof(DigitSize), typeof(double),
        typeof(RollingNumber), new PropertyMetadata(22.0, (d, _) => ((RollingNumber)d).Rebuild()));

    public static readonly DependencyProperty DigitWeightProperty = DependencyProperty.Register(nameof(DigitWeight), typeof(FontWeight),
        typeof(RollingNumber), new PropertyMetadata(FontWeights.Medium, (d, _) => ((RollingNumber)d).Rebuild()));

    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(nameof(Tracking), typeof(double),
        typeof(RollingNumber), new PropertyMetadata(0.0, (d, _) => ((RollingNumber)d).Rebuild()));

    private readonly List<(Canvas Window, StackPanel Strip, TranslateTransform Shift)> _digits = [];
    private double[] _advance = new double[10];
    private double _strip;
    private string _shown = "";
    private double _lineHeight;

    public RollingNumber()
    {
        Orientation = Orientation.Horizontal;
        Focusable = false;
        // No inheritable value of its own (0.10.9): one set here kept the text colour the number had when it was built off
        // the tree, and Power now's watts drew black.
        Set(0, animate: false);
        // In the tree its font is known: the digits' width is measured again in it.
        Loaded += (_, _) => Rebuild();
    }

    /// <summary>The number shown; set, its digits change in place (a live reading's).</summary>
    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double DigitSize { get => (double)GetValue(DigitSizeProperty); set => SetValue(DigitSizeProperty, value); }

    public FontWeight DigitWeight { get => (FontWeight)GetValue(DigitWeightProperty); set => SetValue(DigitWeightProperty, value); }

    /// <summary>The space after each digit, in ems, as CSS's letter-spacing (the mockup's .num is -.035).</summary>
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }

    /// <summary>The number as a screen reader and a test read it.</summary>
    public string Text => _shown;

    /// <summary>How far each digit's strip is rolled, in lines, for a test: the digit it shows.</summary>
    internal IReadOnlyList<double> Rolled => _digits.Select(d => -d.Shift.Y / _lineHeight).ToList();

    internal double LineHeight => _lineHeight;

    /// <summary>Shows <paramref name="value"/> (below 0 as 0), rolling each digit on the spring when
    /// <paramref name="animate"/> and motion is full.</summary>
    public void Set(int value, bool animate = true)
    {
        var text = Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
        if (text == _shown && _digits.Count == text.Length) return;
        if (text.Length != _digits.Count) Build(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var digit = text[i] - '0';
            Place(i, digit);
            AeroMotion.Move(_digits[i].Shift, TranslateTransform.YProperty, -digit * _lineHeight, animate ? AeroMotion.NumberRoll : 0, AeroMotion.Spring);
        }
        _shown = text;
    }

    /// <summary>The digit's own width, as the browser sets Geist's proportional figures: its advance and the tracking, the
    /// digit's left edge at the window's (the strip centres each digit in the widest's width).</summary>
    private void Place(int index, int digit)
    {
        var (window, _, shift) = _digits[index];
        window.Width = Math.Max(0, _advance[digit] + Tracking * DigitSize);
        shift.X = -(_strip - _advance[digit]) / 2;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private void Rebuild()
    {
        var text = _shown;
        _shown = "";
        _digits.Clear();
        Children.Clear();
        if (text.Length > 0) Set(int.Parse(text, CultureInfo.InvariantCulture), animate: false);
    }

    private void Build(int count)
    {
        Children.Clear();
        _digits.Clear();
        _lineHeight = Math.Ceiling(DigitSize * 1.25);
        var face = new Typeface(FontFamilyOf(), FontStyles.Normal, DigitWeight, FontStretches.Normal);
        for (var d = 0; d <= 9; d++)
        {
            _advance[d] = new FormattedText(d.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, DigitSize,
                Brushes.White, 1).WidthIncludingTrailingWhitespace;
        }
        _strip = Math.Ceiling(_advance.Max());
        var width = _strip;
        for (var i = 0; i < count; i++)
        {
            var strip = new StackPanel { Width = width };
            for (var d = 0; d <= 9; d++)
            {
                strip.Children.Add(new TextBlock
                {
                    Text = d.ToString(CultureInfo.InvariantCulture), FontSize = DigitSize, FontWeight = DigitWeight,
                    Height = _lineHeight, LineHeight = _lineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    TextAlignment = TextAlignment.Center,
                });
            }
            var shift = new TranslateTransform();
            strip.RenderTransform = shift;
            // Clipped above and below only: a tracked-in digit's ink may reach past its window, as text past its advance.
            var window = new Canvas { Width = width, Height = _lineHeight, Clip = new RectangleGeometry(new Rect(-width, 0, 3 * width, _lineHeight)) };
            window.Children.Add(strip);
            Children.Add(window);
            _digits.Add((window, strip, shift));
        }
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        fade.GradientStops.Add(new GradientStop(Colors.Black, .18));
        fade.GradientStops.Add(new GradientStop(Colors.Black, .82));
        fade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        fade.Freeze();
        OpacityMask = fade;
    }

    private FontFamily FontFamilyOf() => System.Windows.Documents.TextElement.GetFontFamily(this);

    /// <summary>Reads as its number, with no children: the strips' ten digits each are drawing, not content.</summary>
    private sealed class Peer(RollingNumber owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(RollingNumber);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            var number = ((RollingNumber)Owner).Text;
            return string.IsNullOrEmpty(name) ? number : name + " " + number;
        }

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
