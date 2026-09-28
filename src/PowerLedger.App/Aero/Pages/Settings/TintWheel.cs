using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App.Aero;

/// <summary>
/// The Glass section's hue and saturation wheel (Aero look design §3): hue round the disc from red at the top, clockwise,
/// saturation out from the white centre, the colour's brightness kept as it was, so a wheel pick changes the hue and how
/// strong it is rather than how dark. Drag or click to pick; with the keyboard, Left and Right turn the hue by 5 degrees
/// and Up and Down change the saturation by 5 %. The disc is drawn once per size into a bitmap and the knob is the only
/// thing that moves. Agent G's ColourWheel comes later (Plan S G6); this one is the Glass section's until the lead folds
/// the two together.
/// </summary>
internal sealed class TintWheel : FrameworkElement
{
    public static readonly DependencyProperty ColourProperty = DependencyProperty.Register(nameof(Colour), typeof(Color), typeof(TintWheel),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((TintWheel)d).Describe()));

    private const double HueStep = 5;
    private const double SaturationStep = 0.05;
    private static readonly Dictionary<int, BitmapSource> Discs = [];

    public TintWheel()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        Describe();
    }

    public Color Colour { get => (Color)GetValue(ColourProperty); set => SetValue(ColourProperty, value); }

    /// <summary>The colour at <paramref name="point"/> in a wheel <paramref name="size"/> across, at
    /// <paramref name="value"/> brightness (0 to 1); a point past the rim takes the rim's colour.</summary>
    public static Color At(Point point, double size, double value)
    {
        var radius = size / 2;
        var dx = point.X - radius;
        var dy = point.Y - radius;
        var saturation = Math.Min(1, Math.Sqrt((dx * dx) + (dy * dy)) / radius);
        var hue = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
        return FromHsv(hue, saturation, value);
    }

    /// <summary>Where <paramref name="colour"/> sits on a wheel <paramref name="size"/> across.</summary>
    public static Point Where(Color colour, double size)
    {
        var (hue, saturation, _) = ToHsv(colour);
        var radius = size / 2;
        var angle = hue * Math.PI / 180;
        return new Point(radius + (Math.Sin(angle) * saturation * radius), radius - (Math.Cos(angle) * saturation * radius));
    }

    public static (double Hue, double Saturation, double Value) ToHsv(Color colour)
    {
        double r = colour.R / 255.0, g = colour.G / 255.0, b = colour.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * (((b - r) / delta) + 2) : 60 * (((r - g) / delta) + 4);
        return ((hue + 360) % 360, max == 0 ? 0 : delta / max, max);
    }

    public static Color FromHsv(double hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1 - Math.Abs(((hue / 60) % 2) - 1));
        var m = value - c;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsFinite(Width) ? Width : 160, double.IsFinite(Height) ? Height : 160);

    protected override void OnRender(DrawingContext drawing)
    {
        var size = Math.Min(RenderSize.Width, RenderSize.Height);
        if (size <= 0) return;
        drawing.DrawImage(Disc((int)Math.Ceiling(size * 2)), new Rect(0, 0, size, size));
        var rim = TryFindResource("A.B.Rim") as Brush;
        drawing.DrawEllipse(null, rim is null ? null : new Pen(rim, 1), new Point(size / 2, size / 2), size / 2, size / 2);
        var knob = Where(Colour, size);
        var white = TryFindResource("A.B.White") as Brush ?? Brushes.White;
        var shadow = TryFindResource("A.C.Shadow") is Color dark ? dark : Colors.Black;
        drawing.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x60, shadow.R, shadow.G, shadow.B)), 4), knob, 9, 9);
        drawing.DrawEllipse(new SolidColorBrush(Colour), new Pen(white, 2.5), knob, 9, 9);
        if (IsKeyboardFocused && TryFindResource("A.B.Accent") is Brush accent) drawing.DrawEllipse(null, new Pen(accent, 2), knob, 13, 13);
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
        ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var (hue, saturation, value) = ToHsv(Colour);
        switch (e.Key)
        {
            case Key.Left: hue = (hue - HueStep + 360) % 360; break;
            case Key.Right: hue = (hue + HueStep) % 360; break;
            case Key.Up: saturation = Math.Min(1, saturation + SaturationStep); break;
            case Key.Down: saturation = Math.Max(0, saturation - SaturationStep); break;
            default: return;
        }
        Colour = FromHsv(hue, saturation, Brightness(value));
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    /// <summary>A black or near-black tint would pick nothing but black: the wheel then picks at the demo's brightness.</summary>
    private static double Brightness(double value) => value < 0.2 ? 0.85 : value;

    private static byte Byte(double channel) => (byte)Math.Round(Math.Clamp(channel, 0, 1) * 255);

    private void Pick(Point point)
    {
        var size = Math.Min(RenderSize.Width, RenderSize.Height);
        if (size > 0) Colour = At(point, size, Brightness(ToHsv(Colour).Value));
    }

    private void Describe()
    {
        var (hue, saturation, _) = ToHsv(Colour);
        AutomationProperties.SetHelpText(this, $"Hue {Math.Round(hue):0} degrees, saturation {Math.Round(saturation * 100):0} percent");
    }

    /// <summary>The disc at <paramref name="pixels"/> across, at full brightness, with a softened rim; drawn once per size.</summary>
    private static BitmapSource Disc(int pixels)
    {
        if (Discs.TryGetValue(pixels, out var cached)) return cached;
        var stride = pixels * 4;
        var data = new byte[stride * pixels];
        var radius = pixels / 2.0;
        for (var y = 0; y < pixels; y++)
        {
            for (var x = 0; x < pixels; x++)
            {
                var point = new Point(x + 0.5, y + 0.5);
                var distance = Math.Sqrt(Math.Pow(point.X - radius, 2) + Math.Pow(point.Y - radius, 2));
                var alpha = Math.Clamp(radius - distance, 0, 1);
                if (alpha <= 0) continue;
                var colour = At(point, pixels, 1);
                var i = (y * stride) + (x * 4);
                data[i] = (byte)(colour.B * alpha);
                data[i + 1] = (byte)(colour.G * alpha);
                data[i + 2] = (byte)(colour.R * alpha);
                data[i + 3] = (byte)(255 * alpha);
            }
        }
        var disc = BitmapSource.Create(pixels, pixels, 96, 96, PixelFormats.Pbgra32, null, data, stride);
        disc.Freeze();
        Discs[pixels] = disc;
        return disc;
    }
}
