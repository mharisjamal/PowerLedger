using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App.Aero;

/// <summary>
/// The Glass settings' colour picker (Aero look design §1): a disc of hue round the edge and saturation out from the
/// white centre, with a ringed knob at the colour chosen. A pick changes the hue and how strong it is, not how dark: the
/// colour's brightness is kept (a black or near-black colour picks at the demo violet's brightness instead, or it could
/// pick nothing but black), as agent S's TintWheel did, which this replaces. Drag or click to choose; with the keyboard,
/// Left and Right turn the hue (5 degrees, 1 with Shift), Up and Down change the saturation (5 %, 1 % with Shift). The
/// disc is drawn once for each size and kept; nothing runs at rest. A screen reader hears it as a slider named "Colour"
/// whose value is the colour, its hue and its saturation, and can set a #RRGGBB.
/// </summary>
public sealed class ColourWheel : FrameworkElement
{
    public static readonly DependencyProperty HueProperty = DependencyProperty.Register(nameof(Hue), typeof(double), typeof(ColourWheel),
        new FrameworkPropertyMetadata(248.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHsChanged, CoerceHue));

    public static readonly DependencyProperty SaturationProperty = DependencyProperty.Register(nameof(Saturation), typeof(double), typeof(ColourWheel),
        new FrameworkPropertyMetadata(0.53, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHsChanged, CoerceFraction));

    /// <summary>The colour chosen; set, it moves the knob.</summary>
    public static readonly DependencyProperty ColourProperty = DependencyProperty.Register(nameof(Colour), typeof(Color), typeof(ColourWheel),
        new FrameworkPropertyMetadata(Color.FromRgb(0x74, 0x66, 0xD8), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColourChanged));

    /// <summary>The colour as #RRGGBB, upper-case, as <c>GlassSettings.TintColor</c> keeps it; set, it moves the knob.</summary>
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(nameof(Hex), typeof(string), typeof(ColourWheel),
        new FrameworkPropertyMetadata("#7466D8", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHexChanged));

    public static readonly RoutedEvent ColourChangedEvent = EventManager.RegisterRoutedEvent(nameof(ColourChanged), RoutingStrategy.Bubble,
        typeof(RoutedEventHandler), typeof(ColourWheel));

    public const double HueStep = 5;
    public const double SaturationStep = 0.05;

    /// <summary>The demo violet's brightness, which a pick takes when the colour it starts from is too dark to show a hue.</summary>
    public const double DemoBrightness = 0xD8 / 255.0;

    /// <summary>Below this brightness a colour is too dark for a pick to keep it.</summary>
    public const double DarkestKept = 0.15;

    [ThreadStatic]
    private static Dictionary<int, BitmapSource>? _discs;

    private bool _syncing;
    private double _brightness = DemoBrightness;

    static ColourWheel()
    {
        FocusableProperty.OverrideMetadata(typeof(ColourWheel), new FrameworkPropertyMetadata(true));
        AutomationProperties.NameProperty.OverrideMetadata(typeof(ColourWheel), new FrameworkPropertyMetadata("Colour"));
    }

    public ColourWheel()
    {
        Cursor = Cursors.Cross;
        SetResourceReference(FocusVisualStyleProperty, "A.Focus.Pill");
        Apply(Colour);
    }

    public double Hue { get => (double)GetValue(HueProperty); set => SetValue(HueProperty, value); }

    public double Saturation { get => (double)GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }

    public string Hex { get => (string)GetValue(HexProperty); set => SetValue(HexProperty, value); }

    public Color Colour { get => (Color)GetValue(ColourProperty); set => SetValue(ColourProperty, value); }

    /// <summary>The colour's brightness, 0 to 1, which a pick keeps.</summary>
    public double Brightness => _brightness;

    public event RoutedEventHandler ColourChanged
    {
        add => AddHandler(ColourChangedEvent, value);
        remove => RemoveHandler(ColourChangedEvent, value);
    }

    /// <summary>What a screen reader says the value is.</summary>
    internal string Description => string.Create(CultureInfo.InvariantCulture, $"{Hex}, hue {Math.Round(Hue)} degrees, saturation {Math.Round(Saturation * 100)} percent");

    /// <summary>The hue and saturation at <paramref name="point"/> in a disc of <paramref name="size"/>: the angle from the
    /// top, clockwise, and the distance from the centre, held to the edge.</summary>
    internal static (double Hue, double Saturation) At(Point point, Size size)
    {
        var radius = Math.Min(size.Width, size.Height) / 2;
        if (radius <= 0) return (0, 0);
        var (dx, dy) = (point.X - size.Width / 2, point.Y - size.Height / 2);
        var hue = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        return (hue, Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / radius, 0, 1));
    }

    /// <summary>Where a hue and saturation sit in a disc of <paramref name="size"/>.</summary>
    internal static Point PointOf(double hue, double saturation, Size size)
    {
        var radius = Math.Min(size.Width, size.Height) / 2;
        var angle = hue * Math.PI / 180;
        return new Point(size.Width / 2 + Math.Sin(angle) * saturation * radius, size.Height / 2 - Math.Cos(angle) * saturation * radius);
    }

    internal static Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = (hue % 360) switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));

        static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }

    internal static (double Hue, double Saturation, double Value) ToHsv(Color colour)
    {
        double r = colour.R / 255.0, g = colour.G / 255.0, b = colour.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), span = max - min;
        var hue = span == 0 ? 0 : max == r ? 60 * ((g - b) / span % 6) : max == g ? 60 * ((b - r) / span + 2) : 60 * ((r - g) / span + 4);
        return ((hue + 360) % 360, max == 0 ? 0 : span / max, max);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(double.IsInfinity(availableSize.Width) ? 180 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 180 : availableSize.Height);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;
        var size = new Size(side, side);
        var pixels = (int)Math.Ceiling(side * VisualTreeHelper.GetDpi(this).DpiScaleX);
        drawingContext.DrawImage(Disc(pixels), new Rect(size));
        var rim = TryFindResource("A.B.Line") as Brush ?? Brushes.Gray;
        drawingContext.DrawEllipse(null, new Pen(rim, 1), new Point(side / 2, side / 2), side / 2 - 0.5, side / 2 - 0.5);
        var knob = PointOf(Hue, Saturation, size);
        // The knob: the colour in a white ring (the switch's knob white), set off by a thin shadow line.
        var white = TryFindResource("A.B.White") as Brush ?? Brushes.White;
        var shade = TryFindResource("A.C.Shadow") is Color shadow ? Color.FromArgb(0x59, shadow.R, shadow.G, shadow.B) : Color.FromArgb(0x59, 0, 0, 0);
        drawingContext.DrawEllipse(new SolidColorBrush(Colour), new Pen(white, 3), knob, 9, 9);
        drawingContext.DrawEllipse(null, new Pen(new SolidColorBrush(shade), 1), knob, 10.5, 10.5);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured) Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var fine = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Left: Hue = (Hue - (fine ? 1 : HueStep) + 360) % 360; break;
            case Key.Right: Hue = (Hue + (fine ? 1 : HueStep)) % 360; break;
            case Key.Up: Saturation += fine ? 0.01 : SaturationStep; break;
            case Key.Down: Saturation -= fine ? 0.01 : SaturationStep; break;
            default: return;
        }
        e.Handled = true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private void Pick(Point point)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        var (hue, saturation) = At(point, new Size(side, side));
        Hue = hue;
        Saturation = saturation;
    }

    private static object CoerceHue(DependencyObject d, object value) => value is double h && double.IsFinite(h) ? (h % 360 + 360) % 360 : 0.0;

    private static object CoerceFraction(DependencyObject d, object value) => value is double f && double.IsFinite(f) ? Math.Clamp(f, 0, 1) : 0.0;

    /// <summary>A pick or a key moved the hue or saturation: the colour follows at the kept brightness.</summary>
    private static void OnHsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var wheel = (ColourWheel)d;
        if (wheel._syncing) return;
        if (wheel._brightness < DarkestKept) wheel._brightness = DemoBrightness;
        wheel.Show(FromHsv(wheel.Hue, wheel.Saturation, wheel._brightness));
        wheel.RaiseEvent(new RoutedEventArgs(ColourChangedEvent, wheel));
    }

    private static void OnColourChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var wheel = (ColourWheel)d;
        if (wheel._syncing) return;
        wheel.Apply((Color)e.NewValue);
        wheel.RaiseEvent(new RoutedEventArgs(ColourChangedEvent, wheel));
    }

    private static void OnHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var wheel = (ColourWheel)d;
        if (wheel._syncing || e.NewValue is not string hex) return;
        Color colour;
        try
        {
            colour = (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (FormatException)
        {
            return;
        }
        wheel.Apply(colour);
        wheel.RaiseEvent(new RoutedEventArgs(ColourChangedEvent, wheel));
    }

    /// <summary>Takes <paramref name="colour"/> as the wheel's: its hue, saturation and brightness, and the hex.</summary>
    private void Apply(Color colour)
    {
        var (hue, saturation, value) = ToHsv(colour);
        _brightness = value;
        _syncing = true;
        try
        {
            SetCurrentValue(HueProperty, hue);
            SetCurrentValue(SaturationProperty, saturation);
        }
        finally
        {
            _syncing = false;
        }
        Show(Color.FromRgb(colour.R, colour.G, colour.B));
    }

    /// <summary>Puts <paramref name="colour"/> in Colour and Hex without starting the round again.</summary>
    private void Show(Color colour)
    {
        _syncing = true;
        try
        {
            SetCurrentValue(ColourProperty, colour);
            SetCurrentValue(HexProperty, string.Create(CultureInfo.InvariantCulture, $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}"));
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The disc at <paramref name="pixels"/> across, drawn once and kept.</summary>
    private static BitmapSource Disc(int pixels)
    {
        _discs ??= [];
        if (_discs.TryGetValue(pixels, out var disc)) return disc;
        var data = new byte[pixels * pixels * 4];
        var size = new Size(pixels, pixels);
        var radius = pixels / 2.0;
        for (var y = 0; y < pixels; y++)
        {
            for (var x = 0; x < pixels; x++)
            {
                var centre = new Point(x + 0.5, y + 0.5);
                var distance = Math.Sqrt(Math.Pow(centre.X - radius, 2) + Math.Pow(centre.Y - radius, 2));
                var alpha = Math.Clamp(radius - distance + 0.5, 0, 1);
                if (alpha <= 0) continue;
                var (hue, saturation) = At(centre, size);
                var colour = FromHsv(hue, saturation, 1);
                var i = (y * pixels + x) * 4;
                data[i] = (byte)Math.Round(colour.B * alpha);
                data[i + 1] = (byte)Math.Round(colour.G * alpha);
                data[i + 2] = (byte)Math.Round(colour.R * alpha);
                data[i + 3] = (byte)Math.Round(255 * alpha);
            }
        }
        disc = BitmapSource.Create(pixels, pixels, 96, 96, PixelFormats.Pbgra32, null, data, pixels * 4);
        disc.Freeze();
        _discs[pixels] = disc;
        return disc;
    }

    /// <summary>A slider named "Colour" whose value is the colour in words, settable as #RRGGBB.</summary>
    private sealed class Peer(ColourWheel owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        public bool IsReadOnly => !Owner.IsEnabled;

        public string Value => ((ColourWheel)Owner).Description;

        public void SetValue(string value)
        {
            var hex = value.Trim();
            if (hex.Length != 7 || hex[0] != '#' || !hex.Skip(1).All(Uri.IsHexDigit)) throw new ArgumentException("A colour as #RRGGBB.", nameof(value));
            ((ColourWheel)Owner).Hex = hex.ToUpperInvariant();
        }

        public override object? GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

        protected override string GetClassNameCore() => nameof(ColourWheel);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

        protected override bool IsKeyboardFocusableCore() => true;
    }
}
