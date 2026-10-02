using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// One line of text with CSS's letter-spacing, which a WPF TextBlock can't draw (0.10.9's audit): the mockup tracks its big
/// figures in (.num, -.035 em: This month's $5.73 is 7 px narrower than untracked). Each character is set at its own
/// advance plus <see cref="Tracking"/> ems, the space after every character as the browser leaves it, the last's too. The
/// text colour, face and size come from round it as a TextBlock's do; a screen reader hears the text.
/// </summary>
public sealed class TrackedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(TrackedText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(nameof(Tracking), typeof(double), typeof(TrackedText),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(TrackedText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>The space after each character, in ems (CSS's letter-spacing).</summary>
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }

    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }

    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>Each character's formatted run and where it starts, and the line's size.</summary>
    private (List<(FormattedText Run, double X)> Runs, Size Size) Lay()
    {
        var runs = new List<(FormattedText, double)>();
        var face = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double x = 0, height = new FormattedText(" ", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, FontSize, Foreground, dpi).Height;
        var text = Text ?? "";
        for (var i = 0; i < text.Length; i++)
        {
            // A surrogate pair is one character.
            var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            var run = new FormattedText(text.Substring(i, length), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, FontSize, Foreground, dpi);
            runs.Add((run, x));
            x += run.WidthIncludingTrailingWhitespace + Tracking * FontSize;
            height = Math.Max(height, run.Height);
            i += length - 1;
        }
        return (runs, new Size(Math.Max(0, x), height));
    }

    protected override Size MeasureOverride(Size availableSize) => Lay().Size;

    protected override void OnRender(DrawingContext drawingContext)
    {
        foreach (var (run, x) in Lay().Runs) drawingContext.DrawText(run, new Point(x, 0));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(TrackedText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(TrackedText);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((TrackedText)Owner).Text : name;
        }
    }
}
