using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// What every drawn control shares (spec §9): hairline, label, accent and band brushes, the numeral font, and text
/// drawing. Styles set the brushes from the palette with DynamicResource, so a theme switch repaints.
/// </summary>
internal abstract class Instrument : FrameworkElement
{
    public static readonly DependencyProperty LineBrushProperty = BrushProperty(nameof(LineBrush));
    public static readonly DependencyProperty StrongLineBrushProperty = BrushProperty(nameof(StrongLineBrush));
    public static readonly DependencyProperty LabelBrushProperty = BrushProperty(nameof(LabelBrush));
    public static readonly DependencyProperty InkBrushProperty = BrushProperty(nameof(InkBrush));
    public static readonly DependencyProperty AccentBrushProperty = BrushProperty(nameof(AccentBrush));
    public static readonly DependencyProperty AccentSoftBrushProperty = BrushProperty(nameof(AccentSoftBrush));
    public static readonly DependencyProperty CpuBrushProperty = BrushProperty(nameof(CpuBrush));
    public static readonly DependencyProperty GpuBrushProperty = BrushProperty(nameof(GpuBrush));
    public static readonly DependencyProperty DisplayBrushProperty = BrushProperty(nameof(DisplayBrush));
    public static readonly DependencyProperty RestBrushProperty = BrushProperty(nameof(RestBrush));
    public static readonly DependencyProperty NumberFontProperty = DependencyProperty.Register(
        nameof(NumberFont), typeof(FontFamily), typeof(Instrument),
        new FrameworkPropertyMetadata(new FontFamily("Cascadia Mono, Consolas"), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    public Brush StrongLineBrush { get => (Brush)GetValue(StrongLineBrushProperty); set => SetValue(StrongLineBrushProperty, value); }

    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    public Brush InkBrush { get => (Brush)GetValue(InkBrushProperty); set => SetValue(InkBrushProperty, value); }

    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    public Brush AccentSoftBrush { get => (Brush)GetValue(AccentSoftBrushProperty); set => SetValue(AccentSoftBrushProperty, value); }

    public Brush CpuBrush { get => (Brush)GetValue(CpuBrushProperty); set => SetValue(CpuBrushProperty, value); }

    public Brush GpuBrush { get => (Brush)GetValue(GpuBrushProperty); set => SetValue(GpuBrushProperty, value); }

    public Brush DisplayBrush { get => (Brush)GetValue(DisplayBrushProperty); set => SetValue(DisplayBrushProperty, value); }

    public Brush RestBrush { get => (Brush)GetValue(RestBrushProperty); set => SetValue(RestBrushProperty, value); }

    public FontFamily NumberFont { get => (FontFamily)GetValue(NumberFontProperty); set => SetValue(NumberFontProperty, value); }

    /// <summary>What the control shows, in a sentence, for a screen reader.</summary>
    internal virtual string Describe() => string.Empty;

    /// <summary>To UI Automation a drawn control is a picture, named by the view or else by <see cref="Describe"/>.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new InstrumentPeer(this);

    /// <summary>A dependency property that repaints the control when it changes.</summary>
    protected static DependencyProperty Register<T>(string name, T defaultValue, Type owner)
        => DependencyProperty.Register(name, typeof(T), owner, new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    protected FormattedText Text(string text, double size, Brush brush, FontWeight? weight = null) => new(
        text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        new Typeface(NumberFont, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
        size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Draws text with its top at <paramref name="y"/>, placed by <paramref name="align"/> around <paramref name="x"/>.</summary>
    protected void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, TextAlignment align = TextAlignment.Left, double size = 10)
    {
        var formatted = Text(text, size, brush);
        var left = align switch
        {
            TextAlignment.Center => x - formatted.Width / 2,
            TextAlignment.Right => x - formatted.Width,
            _ => x,
        };
        dc.DrawText(formatted, new Point(left, y));
    }

    protected static Pen Line(Brush brush, double thickness = 1, DashStyle? dash = null) => new(brush, thickness) { DashStyle = dash ?? DashStyles.Solid };

    protected Size Fixed(Size available, double height)
        => new(double.IsInfinity(available.Width) ? 600 : available.Width, height);

    private sealed class InstrumentPeer(Instrument owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => Owner.GetType().Name;

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((Instrument)Owner).Describe() : name;
        }
    }

    private static DependencyProperty BrushProperty(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(Instrument), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
}
