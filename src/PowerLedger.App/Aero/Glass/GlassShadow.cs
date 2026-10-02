using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PowerLedger.App.Aero;

/// <summary>
/// The shadows of a liquid glass piece (0.10.9), worked out rather than blurred on every frame:
/// <list type="bullet">
/// <item>the recipe's <c>filter: drop-shadow(-8px -10px 46px #0000005f)</c>. A CSS filter shadows what the piece paints,
/// and the backdrop it bends is not part of that (measured in Edge: a piece with only a backdrop filter casts none). So
/// the shadow is cast by the piece's tint and its glowing edge: black at 37 % under a Gaussian of deviation 46 (the third
/// length of drop-shadow() is the deviation itself, measured), moved 8 left and 10 up, over the backdrop and outside the
/// piece alike. An untinted pane's edge alone casts about 0.7 % at the most, as the browser draws it; a tinted piece (the
/// lime Open report, the Dark and Colour styles, a dialog) casts a real one.</item>
/// <item>a floating bubble's own <c>box-shadow: 0 8px 18px -8px rgba(0,8,40,.5)</c> (the nav, a segmented control's and
/// a figure's bubble, <see cref="Bubble"/>), or another (<see cref="Lift"/>: a glass pill's, a bar's), outside the piece
/// only, its shape the piece's rounded box.</item>
/// </list>
/// Each is a Gaussian, so it is separable: the drop shadow a sum of blurred rectangles, exact; the box-shadow its rounded
/// shape's coverage blurred across, then down. Each is computed once into a small bitmap (a pixel for every
/// <see cref="DropStep"/> units for the wide shadow, every unit for the box-shadow) and drawn stretched, smooth, again only
/// when the piece's size, corners, tint or edge light change. Nothing here runs per frame.
/// </summary>
public sealed class GlassShadow : FrameworkElement
{
    /// <summary>The drop shadow's black at 0x5F.</summary>
    public const double DropAlpha = LiquidGlassRecipe.ShadowAlpha / 255.0;

    /// <summary>drop-shadow()'s third length is the Gaussian's deviation (measured in Edge: 46.0).</summary>
    public const double DropDeviation = LiquidGlassRecipe.ShadowBlur;

    /// <summary>How far the drop shadow is drawn past the piece: three deviations, where it has fallen under 1/255 of its peak.</summary>
    public const double DropReach = 3 * DropDeviation;

    /// <summary>Units per pixel of the drop shadow's bitmap: a Gaussian this wide is smooth at a pixel every 4 units.</summary>
    public const double DropStep = 4;

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(GlassShadow),
        new FrameworkPropertyMetadata(new CornerRadius(LiquidGlassRecipe.CornerRadius), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint), typeof(Brush), typeof(GlassShadow),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowProperty = DependencyProperty.Register(nameof(Glow), typeof(double), typeof(GlassShadow),
        new FrameworkPropertyMetadata(LiquidGlassRecipe.HighlightOpacity, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CastsProperty = DependencyProperty.Register(nameof(Casts), typeof(bool), typeof(GlassShadow),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BubbleProperty = DependencyProperty.Register(nameof(Bubble), typeof(bool), typeof(GlassShadow),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LiftProperty = DependencyProperty.Register(nameof(Lift), typeof(GlassLift?), typeof(GlassShadow),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private (Size, double, double, double)? _dropFor;
    private BitmapSource? _drop;
    private (Size, double, GlassLift)? _bubbleFor;
    private BitmapSource? _bubble;

    public GlassShadow()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Loaded += (_, _) => Follow(TemplatedParent as GlassPanel);
        Unloaded += (_, _) => Follow(null);
    }

    // ---------------------------------------------------------------- what the piece's content casts

    /// <summary>How long after the last change of the piece's size its content's shadow is worked out again.</summary>
    internal static readonly TimeSpan ContentSettle = TimeSpan.FromMilliseconds(120);

    private GlassPanel? _panel;
    private DispatcherTimer? _contentTimer;
    private BitmapSource? _contentDrop;
    private Size _contentFor;

    /// <summary>The content's drop shadow last worked out, for a test.</summary>
    internal BitmapSource? ContentDropMap => _contentDrop;

    private void Follow(GlassPanel? panel)
    {
        if (_panel is not null) _panel.SizeChanged -= OnPanelSized;
        _panel = panel;
        if (panel is null)
        {
            _contentTimer?.Stop();
            return;
        }
        panel.SizeChanged += OnPanelSized;
        ScheduleContent();
    }

    private void OnPanelSized(object sender, SizeChangedEventArgs e) => ScheduleContent();

    /// <summary>Works the content's shadow out once the piece has settled: after it loads, and after a resize ends.</summary>
    internal void ScheduleContent()
    {
        if (!Casts || _panel is null) return;
        if (_contentTimer is null)
        {
            _contentTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = ContentSettle };
            _contentTimer.Tick += (_, _) => RefreshContent();
        }
        _contentTimer.Stop();
        _contentTimer.Start();
    }

    /// <summary>The content's shadow now, for the timer and a test.</summary>
    internal void RefreshContent()
    {
        _contentTimer?.Stop();
        if (_panel is not { IsLoaded: true } panel || !Casts) return;
        _contentDrop = ContentDropFor(panel, Alpha(Tint));
        _contentFor = new Size(panel.ActualWidth, panel.ActualHeight);
        InvalidateVisual();
    }

    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>The piece's tint, whose alpha casts the shadow with the edge.</summary>
    public Brush? Tint { get => (Brush?)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    /// <summary>The edge's strength (GlassGlow.Strength), whose light casts it too.</summary>
    public double Glow { get => (double)GetValue(GlowProperty); set => SetValue(GlowProperty, value); }

    /// <summary>Whether the recipe's drop shadow is drawn.</summary>
    public bool Casts { get => (bool)GetValue(CastsProperty); set => SetValue(CastsProperty, value); }

    /// <summary>Whether the bubble's own soft shadow under it is drawn.</summary>
    public bool Bubble { get => (bool)GetValue(BubbleProperty); set => SetValue(BubbleProperty, value); }

    /// <summary>The piece's own box-shadow, when it isn't a bubble's (a glass pill's, a bar's); null with none.</summary>
    public GlassLift? Lift { get => (GlassLift?)GetValue(LiftProperty); set => SetValue(LiftProperty, value); }

    /// <summary>The box-shadow drawn: <see cref="Lift"/>, else a bubble's, else none.</summary>
    private GlassLift? Raised => Lift ?? (Bubble ? GlassLift.Bubble : null);

    /// <summary>The bitmap last drawn for the drop shadow, for a test.</summary>
    internal BitmapSource? DropMap => _drop;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var radius = Math.Min(CornerRadius.TopLeft, Math.Min(w, h) / 2);
        if (Raised is { } lift)
        {
            var key = (new Size(w, h), radius, lift);
            if (_bubbleFor != key || _bubble == null)
            {
                _bubble = BubbleMap(w, h, radius, lift);
                _bubbleFor = key;
            }
            var reach = lift.Reach;
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(-reach, -reach, w + 2 * reach, h + 2 * reach + Math.Max(0, lift.Y))), new RectangleGeometry(new Rect(0, 0, w, h), radius, radius));
            outside.Freeze();
            dc.PushClip(outside);
            dc.DrawImage(_bubble, new Rect(-reach, -reach + lift.Y, w + 2 * reach, h + 2 * reach));
            dc.Pop();
        }
        if (Casts)
        {
            var tint = Alpha(Tint);
            var glow = Math.Clamp(Glow, 0, 1);
            var key = (new Size(w, h), radius, tint, glow);
            if (_dropFor != key || _drop == null)
            {
                _drop = DropMapFor(w, h, tint, glow);
                _dropFor = key;
            }
            var at = new Rect(-DropReach + LiquidGlassRecipe.ShadowX, -DropReach + LiquidGlassRecipe.ShadowY, w + 2 * DropReach, h + 2 * DropReach);
            if (_drop != null) dc.DrawImage(_drop, at);
            if (_contentDrop != null && _contentFor == new Size(w, h)) dc.DrawImage(_contentDrop, at);
        }
    }

    /// <summary>
    /// The drop shadow of what the piece's content paints (0.10.9's audit): CSS's filter shadows the whole element, its
    /// text, wells, discs, rings and bars too, not only its tint and edge, and Edge's board is 2 to 5 levels darker for it
    /// between and round the panes. The content is drawn at a pixel every <see cref="DropStep"/> units over the same grid
    /// as <see cref="DropMapFor"/> (its alpha under the tint's), blurred by the recipe's Gaussian, black at 37 %. Worked
    /// out when the piece loads and when a resize ends; a reading changing a figure's digits doesn't (it moves this
    /// 46-unit blur by less than a level). Null with nothing in the piece.
    /// </summary>
    internal static BitmapSource? ContentDropFor(GlassPanel panel, double tint)
    {
        if (panel.Template?.FindName("PART_Content", panel) is not FrameworkElement content || content.ActualWidth <= 0 || panel.ActualWidth <= 0) return null;
        var bounds = VisualTreeHelper.GetDescendantBounds(content);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;
        double w = panel.ActualWidth, h = panel.ActualHeight;
        var cols = (int)Math.Ceiling((w + 2 * DropReach) / DropStep);
        var rows = (int)Math.Ceiling((h + 2 * DropReach) / DropStep);
        double cellX = (w + 2 * DropReach) / cols, cellY = (h + 2 * DropReach) / rows;
        var at = content.TranslatePoint(new Point(0, 0), panel);
        var brush = new VisualBrush(content)
        {
            Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds,
            ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1),
        };
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(1 / cellX, 1 / cellY));
            var target = new Rect(DropReach + at.X + bounds.X, DropReach + at.Y + bounds.Y, bounds.Width, bounds.Height);
            brush.Viewport = target;
            dc.DrawRectangle(brush, null, target);
        }
        var raster = new RenderTargetBitmap(cols, rows, 96, 96, PixelFormats.Pbgra32);
        raster.Render(drawing);
        var pixels = new byte[cols * rows * 4];
        raster.CopyPixels(pixels, cols * 4, 0);
        var cover = new double[cols * rows];
        var any = false;
        for (var i = 0; i < cover.Length; i++)
        {
            cover[i] = pixels[(i * 4) + 3] / 255.0 * (1 - tint);
            any |= cover[i] > 0;
        }
        if (!any) return null;
        var blurred = Blurred(Blurred(cover, cols, rows, DropDeviation / cellX, horizontal: true), cols, rows, DropDeviation / cellY, horizontal: false);
        var shadow = new byte[cols * rows * 4];
        any = false;
        for (var i = 0; i < blurred.Length; i++)
        {
            var a = (byte)Math.Round(Math.Clamp(blurred[i] * DropAlpha, 0, 1) * 255);
            if (a == 0) continue;
            any = true;
            shadow[(i * 4) + 3] = a;   // black, premultiplied
        }
        if (!any) return null;
        var bitmap = BitmapSource.Create(cols, rows, 96, 96, PixelFormats.Pbgra32, null, shadow, cols * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>How opaque a tint is on average: a solid colour's alpha, a gradient's stops' mean; none for no brush.</summary>
    internal static double Alpha(Brush? brush) => brush switch
    {
        SolidColorBrush solid => solid.Color.A / 255.0 * solid.Opacity,
        GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops.Average(s => s.Color.A / 255.0) * gradient.Opacity,
        null => 0,
        _ => 0,
    };

    /// <summary>
    /// The drop shadow's alpha at every <see cref="DropStep"/> units over the piece and <see cref="DropReach"/> round it,
    /// before its offset: the piece's painted alpha (the tint, and the edge's glow over it), blurred. The glow is the sum
    /// of nested rectangles a unit apart, each lit by what the profile loses there, so the whole is a sum of blurred
    /// rectangles, each the product of two error functions. Null when it would draw nothing.
    /// </summary>
    internal static BitmapSource? DropMapFor(double w, double h, double tint, double glow)
    {
        // Each rectangle: inset by d, weighted.
        var rects = new List<(double Inset, double Weight)>();
        if (tint > 0) rects.Add((0, tint));
        if (glow > 0)
        {
            for (var d = 0; d < GlassGlow.GlowDepth; d++)
            {
                var weight = (1 - tint) * (GlassGlow.Glow(d + 0.5, glow) - GlassGlow.Glow(d + 1.5, glow));
                if (weight <= 0) continue;
                rects.Add((0, weight));
                if (w > 2 * (d + 1) && h > 2 * (d + 1)) rects.Add((d + 1, -weight));
            }
        }
        if (rects.Count == 0) return null;
        var cols = (int)Math.Ceiling((w + 2 * DropReach) / DropStep);
        var rows = (int)Math.Ceiling((h + 2 * DropReach) / DropStep);
        double StepX(int i) => (i + 0.5) * (w + 2 * DropReach) / cols - DropReach;
        double StepY(int i) => (i + 0.5) * (h + 2 * DropReach) / rows - DropReach;
        var gx = new double[rects.Count][];
        var gy = new double[rects.Count][];
        for (var k = 0; k < rects.Count; k++)
        {
            var inset = rects[k].Inset;
            gx[k] = new double[cols];
            gy[k] = new double[rows];
            for (var i = 0; i < cols; i++) gx[k][i] = Band(StepX(i), inset, w - inset, DropDeviation);
            for (var i = 0; i < rows; i++) gy[k][i] = Band(StepY(i), inset, h - inset, DropDeviation);
        }
        var pixels = new byte[cols * rows * 4];
        var any = false;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var sum = 0.0;
                for (var k = 0; k < rects.Count; k++) sum += rects[k].Weight * gx[k][x] * gy[k][y];
                var a = (byte)Math.Round(Math.Clamp(sum * DropAlpha, 0, 1) * 255);
                if (a == 0) continue;
                any = true;
                pixels[(y * cols + x) * 4 + 3] = a;   // black, premultiplied
            }
        }
        if (!any) return null;
        var bitmap = BitmapSource.Create(cols, rows, 96, 96, PixelFormats.Pbgra32, null, pixels, cols * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The bubble's shadow at every unit round the piece, before its drop.</summary>
    internal static BitmapSource BubbleMap(double w, double h) => BubbleMap(w, h, Math.Min(w, h) / 2, GlassLift.Bubble);

    /// <summary>
    /// A box-shadow (<paramref name="lift"/>) at every unit round the piece, before its drop, as the browser draws one: the
    /// piece's rounded box shrunk by the spread, its corners by the spread too (0.10.9's audit: a rectangle had square
    /// corners under a capsule), blurred by a Gaussian of half the blur. A Gaussian is separable whatever the shape, so the
    /// shape's coverage is blurred across, then down.
    /// </summary>
    internal static BitmapSource BubbleMap(double w, double h, double radius, GlassLift lift)
    {
        var reach = lift.Reach;
        var sigma = lift.Blur / 2;
        var cols = (int)Math.Ceiling(w + 2 * reach);
        var rows = (int)Math.Ceiling(h + 2 * reach);
        double ux = (w + 2 * reach) / cols, uy = (h + 2 * reach) / rows;
        // The shape: the box shrunk (or grown) by the spread, its corner radius with it, never under nothing.
        var inset = -lift.Spread;
        double left = inset, top = inset, right = w - inset, bottom = h - inset;
        var r = Math.Max(0, Math.Min(radius - inset, Math.Min(right - left, bottom - top) / 2));
        var cover = new double[cols * rows];
        if (right > left && bottom > top)
        {
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < cols; x++)
                {
                    // Four by four samples in the unit cell.
                    var hits = 0;
                    for (var sy = 0; sy < 4; sy++)
                    {
                        var py = (y + (sy + 0.5) / 4) * uy - reach;
                        for (var sx = 0; sx < 4; sx++)
                        {
                            var px = (x + (sx + 0.5) / 4) * ux - reach;
                            if (px < left || px > right || py < top || py > bottom) continue;
                            var cx = Math.Clamp(px, left + r, right - r);
                            var cy = Math.Clamp(py, top + r, bottom - r);
                            if ((px - cx) * (px - cx) + (py - cy) * (py - cy) <= r * r) hits++;
                        }
                    }
                    cover[(y * cols) + x] = hits / 16.0;
                }
            }
        }
        var across = Blurred(cover, cols, rows, sigma / ux, horizontal: true);
        var both = Blurred(across, cols, rows, sigma / uy, horizontal: false);
        var pixels = new byte[cols * rows * 4];
        var colour = lift.Colour;
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var a = Math.Clamp(both[(y * cols) + x] * colour.A / 255.0, 0, 1);
                var at = (y * cols + x) * 4;
                pixels[at] = (byte)Math.Round(colour.B * a);
                pixels[at + 1] = (byte)Math.Round(colour.G * a);
                pixels[at + 2] = (byte)Math.Round(colour.R * a);
                pixels[at + 3] = (byte)Math.Round(a * 255);
            }
        }
        var bitmap = BitmapSource.Create(cols, rows, 96, 96, PixelFormats.Pbgra32, null, pixels, cols * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A Gaussian of <paramref name="sigma"/> cells along rows or columns, the edges reading nothing past them.</summary>
    private static double[] Blurred(double[] from, int cols, int rows, double sigma, bool horizontal)
    {
        if (sigma <= 0) return from;
        var radius = (int)Math.Ceiling(3 * sigma);
        var kernel = new double[(2 * radius) + 1];
        for (var k = -radius; k <= radius; k++) kernel[k + radius] = Math.Exp(-(k * k) / (2 * sigma * sigma));
        var sum = kernel.Sum();
        for (var k = 0; k < kernel.Length; k++) kernel[k] /= sum;
        var to = new double[from.Length];
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var total = 0.0;
                for (var k = -radius; k <= radius; k++)
                {
                    int sx = horizontal ? x + k : x, sy = horizontal ? y : y + k;
                    if (sx < 0 || sy < 0 || sx >= cols || sy >= rows) continue;
                    total += from[(sy * cols) + sx] * kernel[k + radius];
                }
                to[(y * cols) + x] = total;
            }
        }
        return to;
    }

    /// <summary>A unit strip from <paramref name="from"/> to <paramref name="to"/> blurred by a Gaussian of
    /// <paramref name="deviation"/>, at <paramref name="at"/>.</summary>
    internal static double Band(double at, double from, double to, double deviation)
        => to <= from ? 0 : GlassGlow.Phi((at - from) / deviation) - GlassGlow.Phi((at - to) / deviation);
}
