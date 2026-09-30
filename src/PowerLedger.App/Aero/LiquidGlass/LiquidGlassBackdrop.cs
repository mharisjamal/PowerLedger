using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace PowerLedger.App.Aero;

/// <summary>
/// The liquid glass engine's one element (see docs/superpowers/specs/2026-10-01-aero-liquid-glass-engine.md): put it
/// behind a glass piece's content, filling the piece, and it draws the live backdrop under the piece through the owner's
/// recipe (<see cref="LiquidGlassRecipe"/>), <c>brightness(1.1) blur(2px) url(#displacement)</c> in that order, clipped
/// to the piece's rounded rectangle (<see cref="CornerRadius"/>). It works in any top-level window (the Aero window, the
/// watts overlay, a popup): the window is left out of screen capture (WDA_EXCLUDEFROMCAPTURE) so its glass never shows
/// itself, and the picture follows the window as it moves. It draws no tint, shadow or highlight: the look does.
/// <para>As Chromium's backdrop-filter, the piece reads only the backdrop inside its own box: the blur and the
/// displacement mirror at its edges (DisplacementField). Inside, four nested elements carry one pass each
/// (LiquidGlassEffects), over the box plus a margin the blur reaches into, where the backdrop brush tiles FlipXY, the
/// box mirrored. Nothing runs at rest: the passes draw again only when the backdrop's pixels, the piece's place or its
/// size change.</para>
/// </summary>
internal sealed class LiquidGlassBackdrop : FrameworkElement
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius),
        typeof(LiquidGlassBackdrop), new FrameworkPropertyMetadata(new CornerRadius(LiquidGlassRecipe.CornerRadius), (d, _) => ((LiquidGlassBackdrop)d).UpdateClip(((LiquidGlassBackdrop)d).RenderSize)));

    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(nameof(Brightness), typeof(double),
        typeof(LiquidGlassBackdrop), new FrameworkPropertyMetadata(LiquidGlassRecipe.Brightness, (d, e) => ((LiquidGlassBackdrop)d)._bright.Brightness = (double)e.NewValue));

    public static readonly DependencyProperty BlurDeviationProperty = DependencyProperty.Register(nameof(BlurDeviation), typeof(double),
        typeof(LiquidGlassBackdrop), new FrameworkPropertyMetadata(LiquidGlassRecipe.BlurDeviation, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(nameof(Scale), typeof(double),
        typeof(LiquidGlassBackdrop), new FrameworkPropertyMetadata(LiquidGlassRecipe.Scale, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(nameof(IsLive), typeof(bool),
        typeof(LiquidGlassBackdrop), new FrameworkPropertyMetadata(true, (d, _) => ((LiquidGlassBackdrop)d).Attach()));

    private static readonly DependencyPropertyKey KindKey = DependencyProperty.RegisterReadOnly(nameof(Kind), typeof(LiquidGlassSourceKind),
        typeof(LiquidGlassBackdrop), new PropertyMetadata(LiquidGlassSourceKind.None));

    public static readonly DependencyProperty KindProperty = KindKey.DependencyProperty;

    private readonly Border _displaceHost = new();
    private readonly Border _downHost = new();
    private readonly Border _acrossHost = new();
    private readonly Border _brightHost = new();
    private readonly GlassDisplaceEffect _displace = new();
    private readonly GlassBlurEffect _down = new(down: true);
    private readonly GlassBlurEffect _across = new(down: false);
    private readonly GlassBrightnessEffect _bright = new();
    private readonly ImageBrush _backdrop = new() { TileMode = TileMode.FlipXY, ViewportUnits = BrushMappingMode.Absolute, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
    private readonly ImageBrush _map = new() { Stretch = Stretch.Fill };
    private ILiquidGlassSource? _source;
    private HwndSource? _window;
    private (int W, int H, int Margin, double Scale, double Dpi)? _mapFor;
    private (int W, int H, int Margin, double Scale, double Dpi)? _mapWanted;
    private int _mapGeneration;
    private int _margin;
    private double _dpi = 1;

    public LiquidGlassBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        RenderOptions.SetBitmapScalingMode(_map, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(_backdrop, BitmapScalingMode.NearestNeighbor);
        _brightHost.Background = _backdrop;
        _brightHost.Effect = _bright;
        _acrossHost.Child = _brightHost;
        _acrossHost.Effect = _across;
        _downHost.Child = _acrossHost;
        _downHost.Effect = _down;
        _displaceHost.Child = _downHost;
        _displace.Map = _map;
        _displaceHost.Effect = _displace;
        _displaceHost.Visibility = Visibility.Hidden;
        AddVisualChild(_displaceHost);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        LayoutUpdated += (_, _) => Place();
    }

    /// <summary>The piece's corners; the recipe's 28 by default.</summary>
    public CornerRadius CornerRadius { get => (CornerRadius)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    /// <summary>The first filter, brightness; 1.1 by default.</summary>
    public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }

    /// <summary>The second, the blur's standard deviation in units; 2 by default.</summary>
    public double BlurDeviation { get => (double)GetValue(BlurDeviationProperty); set => SetValue(BlurDeviationProperty, value); }

    /// <summary>The third, the displacement's scale in units; 200 by default, 0 for none.</summary>
    public double Scale { get => (double)GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }

    /// <summary>False draws nothing and lets go of the capture (reduce transparency, a hidden piece).</summary>
    public bool IsLive { get => (bool)GetValue(IsLiveProperty); set => SetValue(IsLiveProperty, value); }

    /// <summary>What the piece shows now: the live screen, the wallpaper, or nothing yet.</summary>
    public LiquidGlassSourceKind Kind => (LiquidGlassSourceKind)GetValue(KindProperty);

    /// <summary>The source map in use is ready, for a test.</summary>
    internal bool IsReady => _mapFor != null && _mapFor == _mapWanted && _displaceHost.Visibility == Visibility.Visible;

    /// <summary>Why the last source map couldn't be made, for a test; null when it was.</summary>
    internal Exception? MapError { get; private set; }

    /// <summary>Raised when a new source map is on the displacement pass, for a test.</summary>
    internal event Action? MapReady;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => index == 0 ? _displaceHost : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size availableSize)
    {
        _displaceHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var sigma = BlurDeviation * _dpi;
        // The blur reaches ceil(3 sigma) texels, each bilinear pair one further; two more keep the edge texels whole.
        _margin = Math.Min(LiquidGlassEffects.MaxBlurRadius, (int)Math.Ceiling(3 * sigma)) + 2;
        var m = _margin / _dpi;
        _displaceHost.Arrange(new Rect(-m, -m, finalSize.Width + 2 * m, finalSize.Height + 2 * m));
        _across.Sigma = sigma;
        _down.Sigma = sigma;
        var texel = new Point4D(1 / Math.Round(finalSize.Width * _dpi + 2 * _margin), 1 / Math.Round(finalSize.Height * _dpi + 2 * _margin), 0, 0);
        _across.Texel = texel;
        _down.Texel = texel;
        _displace.Texel = texel;
        _backdrop.Viewport = new Rect(m, m, finalSize.Width, finalSize.Height);
        UpdateClip(finalSize);
        UpdateMap(finalSize);
        Place();
        return finalSize;
    }

    /// <summary>Uses <paramref name="source"/> directly, without a window's (a test that draws the piece to a bitmap).</summary>
    internal void Use(ILiquidGlassSource source)
    {
        Detach();
        _source = source;
        _source.Changed += OnSourceChanged;
        OnSourceChanged();
    }

    /// <summary>The piece's box in device pixels on the screen: where its top left lands and its size, rounded.</summary>
    internal Rect? ScreenBox()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return null;
        int w = (int)Math.Round(ActualWidth * _dpi), h = (int)Math.Round(ActualHeight * _dpi);
        if (PresentationSource.FromVisual(this) != null)
        {
            var at = PointToScreen(new Point(0, 0));
            return new Rect(Math.Round(at.X), Math.Round(at.Y), w, h);
        }
        // Not on a screen (drawn to a bitmap): the root's top left is the screen's.
        var root = this as Visual;
        while (VisualTreeHelper.GetParent(root) is Visual parent) root = parent;
        var local = TransformToAncestor(root).Transform(new Point(0, 0));
        return new Rect(Math.Round(local.X * _dpi), Math.Round(local.Y * _dpi), w, h);
    }

    private void Attach()
    {
        if (!IsLoaded || !IsLive)
        {
            Detach();
            return;
        }
        var window = PresentationSource.FromVisual(this) as HwndSource;
        if (window == null || (ReferenceEquals(window, _window) && _source != null)) return;
        Detach();
        _window = window;
        _source = LiquidGlassSources.Acquire(window);
        _source.Changed += OnSourceChanged;
        OnSourceChanged();
    }

    private void Detach()
    {
        if (_source != null) _source.Changed -= OnSourceChanged;
        if (_window != null && _source != null) LiquidGlassSources.Release(_window);
        _source = null;
        _window = null;
        _backdrop.ImageSource = null;
        _displaceHost.Visibility = Visibility.Hidden;
        SetValue(KindKey, LiquidGlassSourceKind.None);
    }

    private void OnSourceChanged()
    {
        if (_source == null) return;
        SetValue(KindKey, _source.Image == null ? LiquidGlassSourceKind.None : _source.Kind);
        if (!ReferenceEquals(_backdrop.ImageSource, _source.Image)) _backdrop.ImageSource = _source.Image;
        Place();
    }

    /// <summary>Points the backdrop brush at the piece's part of the source's picture; a brush already there is left
    /// alone, so nothing draws again.</summary>
    private void Place()
    {
        if (_source?.Image is not { } image || ScreenBox() is not { } box)
        {
            if (_displaceHost.Visibility == Visibility.Visible) _displaceHost.Visibility = Visibility.Hidden;
            return;
        }
        var bounds = _source.ScreenBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        double kx = image.Width / bounds.Width, ky = image.Height / bounds.Height;
        var viewbox = new Rect((box.X - bounds.X) * kx, (box.Y - bounds.Y) * ky, box.Width * kx, box.Height * ky);
        if (_backdrop.Viewbox != viewbox) _backdrop.Viewbox = viewbox;
        // A map made for another size would move the wrong pixels: the piece waits for its own.
        var ready = _mapFor != null && _mapFor == _mapWanted;
        var visibility = ready ? Visibility.Visible : Visibility.Hidden;
        if (_displaceHost.Visibility != visibility) _displaceHost.Visibility = visibility;
    }

    private void UpdateClip(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        var r = CornerRadius;
        var clip = new StreamGeometry();
        using (var g = clip.Open())
        {
            double w = size.Width, h = size.Height;
            double tl = Math.Min(r.TopLeft, Math.Min(w, h) / 2), tr = Math.Min(r.TopRight, Math.Min(w, h) / 2);
            double br = Math.Min(r.BottomRight, Math.Min(w, h) / 2), bl = Math.Min(r.BottomLeft, Math.Min(w, h) / 2);
            g.BeginFigure(new Point(tl, 0), true, true);
            g.LineTo(new Point(w - tr, 0), false, false);
            if (tr > 0) g.ArcTo(new Point(w, tr), new Size(tr, tr), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(w, h - br), false, false);
            if (br > 0) g.ArcTo(new Point(w - br, h), new Size(br, br), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(bl, h), false, false);
            if (bl > 0) g.ArcTo(new Point(0, h - bl), new Size(bl, bl), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(0, tl), false, false);
            if (tl > 0) g.ArcTo(new Point(tl, 0), new Size(tl, tl), 0, false, SweepDirection.Clockwise, false, false);
        }
        clip.Freeze();
        Clip = clip;
    }

    /// <summary>Makes the source map for the piece's size off the UI thread (the turbulence is shared, so after the first
    /// piece at a display scale this is a mirror and a pack), and puts it on the displacement pass.</summary>
    private void UpdateMap(Size size)
    {
        int w = (int)Math.Round(size.Width * _dpi), h = (int)Math.Round(size.Height * _dpi);
        if (w <= 0 || h <= 0) return;
        var wanted = (w, h, _margin, Scale, _dpi);
        if (_mapWanted == wanted) return;
        _mapWanted = wanted;
        var generation = ++_mapGeneration;
        var (margin, scale, dpi) = (_margin, Scale, _dpi);
        Task.Run(() =>
        {
            var pixels = DisplacementField.Shared(w, h, dpi).SourceMap(w, h, margin, scale);
            var bitmap = BitmapSource.Create(w + 2 * margin, h + 2 * margin, 96, 96, PixelFormats.Bgra32, null, pixels, (w + 2 * margin) * 4);
            bitmap.Freeze();
            return bitmap;
        }).ContinueWith(done =>
        {
            if (generation != _mapGeneration) return;
            MapError = done.Exception?.InnerException;
            if (done.Status != TaskStatus.RanToCompletion) return;
            _map.ImageSource = done.Result;
            _mapFor = wanted;
            Place();
            MapReady?.Invoke();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
