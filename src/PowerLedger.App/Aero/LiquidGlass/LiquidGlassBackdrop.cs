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
/// displacement mirror at its edges (DisplacementField). Two ways to draw it, the window's source choosing:</para>
/// <list type="bullet">
/// <item>The GPU path (<see cref="WindowGlassSource.Gpu"/>): the piece tells its window's GpuGlassWindow where its box is,
/// and shows its own rectangle of the window's picture, drawn on the GPU from the duplicated desktop. Nothing of the
/// recipe runs in WPF.</item>
/// <item>The CPU path and the wallpaper: four nested elements carry one WPF effect each (LiquidGlassEffects), over the
/// box plus a margin the blur reaches into, where the first pass reads the box mirrored. The picture is the window's one
/// source image, laid where it lies on the screen and clipped to the box and margin: no brush viewbox, which WPF would
/// cut out of the picture on the CPU for every piece on every frame. The finished glass is cached, so content drawn over
/// it (a reading) doesn't run the passes again.</item>
/// </list>
/// <para>Nothing runs at rest: the glass draws again only when the backdrop's pixels, the piece's place or its size
/// change.</para>
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
    private readonly Canvas _brightHost = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Image _picture = new() { Stretch = Stretch.Fill };
    private readonly GlassDisplaceEffect _displace = new();
    private readonly GlassBlurEffect _down = new(down: true);
    private readonly GlassBlurEffect _across = new(down: false);
    private readonly GlassBrightnessEffect _bright = new();
    private readonly ImageBrush _map = new() { Stretch = Stretch.Fill };
    private readonly Border _gpuHost = new() { Visibility = Visibility.Hidden };
    private readonly ImageBrush _gpuPicture = new() { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute };
    private readonly BitmapCache _cache = new() { SnapsToDevicePixels = true };
    private GpuGlassWindow? _gpu;
    private ILiquidGlassSource? _source;
    private HwndSource? _window;
    private (int W, int H, int Margin, double Scale, double Dpi)? _mapFor;
    private (int W, int H, int Margin, double Scale, double Dpi)? _mapWanted;
    private int _mapGeneration;
    private (Size Size, CornerRadius Radius)? _clipFor;
    private int _margin;
    private double _dpi = 1;

    public LiquidGlassBackdrop()
    {
        IsHitTestVisible = false;
        Focusable = false;
        RenderOptions.SetBitmapScalingMode(_map, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.NearestNeighbor);
        _brightHost.Children.Add(_picture);
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
        RenderOptions.SetBitmapScalingMode(_gpuPicture, BitmapScalingMode.NearestNeighbor);
        // The piece's own rectangle of the window's picture, through a brush's viewbox: the piece's bounds are all that
        // WPF marks dirty when the picture changes (an Image of the whole picture, clipped, would mark the whole of it,
        // and WPF would draw everything over every piece again on each frame).
        _gpuHost.Background = _gpuPicture;
        AddVisualChild(_gpuHost);
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

    /// <summary>Whether the piece draws through its window's GPU path now.</summary>
    internal bool OnGpu => _gpu != null;

    /// <summary>Whether the piece shows its rectangle of the GPU path's picture now.</summary>
    internal bool ShowsGpu => _gpu != null && _gpuHost.Visibility == Visibility.Visible;

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _displaceHost,
        1 => _gpuHost,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        _displaceHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _gpuHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
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
        _gpuHost.Arrange(new Rect(finalSize));
        _across.Sigma = sigma;
        _down.Sigma = sigma;
        var texel = new Point4D(1 / Math.Round(finalSize.Width * _dpi + 2 * _margin), 1 / Math.Round(finalSize.Height * _dpi + 2 * _margin), 0, 0);
        _across.Texel = texel;
        _down.Texel = texel;
        _displace.Texel = texel;
        double w = Math.Round(finalSize.Width * _dpi), h = Math.Round(finalSize.Height * _dpi);
        _bright.Box = new Point4D(_margin * texel.X, _margin * texel.Y, (_margin + w) * texel.X, (_margin + h) * texel.Y);
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
        UseGpu(null);
        if (_source != null) _source.Changed -= OnSourceChanged;
        if (_window != null && _source != null) LiquidGlassSources.Release(_window);
        _source = null;
        _window = null;
        _picture.Source = null;
        _displaceHost.Visibility = Visibility.Hidden;
        SetValue(KindKey, LiquidGlassSourceKind.None);
    }

    private void OnSourceChanged()
    {
        if (_source == null) return;
        UseGpu((_source as WindowGlassSource)?.Gpu);
        SetValue(KindKey, _gpu != null ? _source.Kind : _source.Image == null ? LiquidGlassSourceKind.None : _source.Kind);
        if (!ReferenceEquals(_picture.Source, _source.Image))
        {
            _picture.Source = _source.Image;
            // The live picture lies pixel on pixel; the wallpaper is scaled to the screen.
            RenderOptions.SetBitmapScalingMode(_picture, _source.Kind == LiquidGlassSourceKind.Live ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
        }
        Place();
    }

    /// <summary>Lays the source's picture where it lies on the screen, under the piece: one picture, uploaded once, shared
    /// by every piece in the window, each showing its own part through its clip. A picture already there is left alone,
    /// so nothing draws again.</summary>
    private void Place()
    {
        if (_gpu != null)
        {
            PlaceOnGpu();
            return;
        }
        if (_source?.Image is not { } image || ScreenBox() is not { } box)
        {
            if (_displaceHost.Visibility == Visibility.Visible) _displaceHost.Visibility = Visibility.Hidden;
            return;
        }
        var bounds = _source.ScreenBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var m = _margin / _dpi;
        double left = m + (bounds.X - box.X) / _dpi, top = m + (bounds.Y - box.Y) / _dpi;
        if (Canvas.GetLeft(_picture) != left) Canvas.SetLeft(_picture, left);
        if (Canvas.GetTop(_picture) != top) Canvas.SetTop(_picture, top);
        if (_picture.Width != bounds.Width / _dpi) _picture.Width = bounds.Width / _dpi;
        if (_picture.Height != bounds.Height / _dpi) _picture.Height = bounds.Height / _dpi;
        // A map made for another size would move the wrong pixels: the piece waits for its own.
        var ready = _mapFor != null && _mapFor == _mapWanted;
        var visibility = ready ? Visibility.Visible : Visibility.Hidden;
        if (_displaceHost.Visibility != visibility) _displaceHost.Visibility = visibility;
    }

    /// <summary>Takes the GPU path of <paramref name="gpu"/>'s window, or leaves it (null): the effects and their cache
    /// rest, and the piece shows its rectangle of the window's picture.</summary>
    private void UseGpu(GpuGlassWindow? gpu)
    {
        if (ReferenceEquals(gpu, _gpu))
        {
            if (gpu == null) CacheMode = Hardware() ? _cache : null;
            return;
        }
        if (_gpu != null)
        {
            _gpu.Changed -= PlaceOnGpu;
            _gpu.Remove(this);
        }
        _gpu = gpu;
        if (gpu != null)
        {
            gpu.Changed += PlaceOnGpu;
            _gpuPicture.ImageSource = gpu.Image;
            _displaceHost.Visibility = Visibility.Hidden;
            CacheMode = null;
            PlaceOnGpu();
        }
        else
        {
            _gpuPicture.ImageSource = null;
            _gpuHost.Visibility = Visibility.Hidden;
            // The passes run again only when the picture under the piece changes: content drawn over the piece (a
            // reading, a hover) is composed over this cache rather than making the four passes run again. Only where WPF
            // draws in hardware: its software renderer draws a cached chain of effects out of place.
            CacheMode = Hardware() ? _cache : null;
        }
    }

    /// <summary>Whether WPF draws the piece's window in hardware (not a remote session, not software, not a bitmap).</summary>
    private bool Hardware()
        => PresentationSource.FromVisual(this) is HwndSource { CompositionTarget.RenderMode: not RenderMode.SoftwareOnly } && (RenderCapability.Tier >> 16) >= 2
           && !SystemParameters.IsRemoteSession && RenderOptions.ProcessRenderMode != RenderMode.SoftwareOnly;

    /// <summary>Tells the GPU path where the piece is, and shows its rectangle of the window's picture.</summary>
    private void PlaceOnGpu()
    {
        if (_gpu == null || ScreenBox() is not { } box) return;
        _gpu.Update(this, new Int32Rect((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height), _dpi, Brightness, BlurDeviation * _dpi, Scale);
        if (_gpu.TargetOf(this) is not { } target || _gpu.Image.PixelWidth <= 0)
        {
            if (_gpuHost.Visibility == Visibility.Visible) _gpuHost.Visibility = Visibility.Hidden;
            return;
        }
        // A D3DImage's units are its pixels.
        var viewbox = new Rect(target.X, target.Y, target.Width, target.Height);
        if (_gpuPicture.Viewbox != viewbox) _gpuPicture.Viewbox = viewbox;
        if (_gpuHost.Visibility != Visibility.Visible) _gpuHost.Visibility = Visibility.Visible;
    }

    private void UpdateClip(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        var r = CornerRadius;
        // A layout pass that changes nothing leaves the clip alone: a new one would draw the four passes again.
        if (_clipFor == (size, r)) return;
        _clipFor = (size, r);
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
