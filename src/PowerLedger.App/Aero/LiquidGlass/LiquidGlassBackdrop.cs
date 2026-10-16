using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

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
/// and the glass is drawn on the GPU from the duplicated desktop. Composed (the usual way), it lies beneath the window's
/// WPF content and the piece draws nothing: whatever the look draws over the piece must leave it visible. Imaged (a
/// layered window, a piece over another), the piece shows its own rectangle of the window's shared picture. Nothing of
/// the recipe runs in WPF.</item>
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
    private readonly Image _finished = new() { Stretch = Stretch.Fill, Visibility = Visibility.Hidden };
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
        RenderOptions.SetBitmapScalingMode(_finished, BitmapScalingMode.NearestNeighbor);
        AddVisualChild(_finished);
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => PlaceOnGpu();
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

    /// <summary>
    /// Whether the piece lies inside another piece. The element a backdrop is the bottom layer of is the glass piece (CSS's
    /// element with the backdrop-filter), and in Chromium that element is a backdrop root: a backdrop-filter inside it
    /// reads only what the outer element painted before it, never the page or the screen behind, nor the outer glass.
    /// (Edge draws the mockup's Energy pane with its fourteen glass bars over bare glass pixel for pixel as the pane
    /// alone.) So a backdrop whose element sits inside another piece's element (an ancestor above its own element has a
    /// backdrop as a child) reads that content (<see cref="InsideGlassSource"/>), through the same recipe on the WPF
    /// effects, and takes no capture. Pieces side by side, or over one another in one element (a dialog over a page), are
    /// not nested.
    /// </summary>
    internal bool IsNested => Outer(this) != null;

    /// <summary>Leaves an element (and what it holds) out of what a piece inside another reads: the look's drop shadow
    /// and highlights of a piece, which CSS paints apart from the piece's content (a filter's drop shadow after the
    /// element, <c>::after</c> over its content). True by default.</summary>
    public static readonly DependencyProperty InBackdropProperty = DependencyProperty.RegisterAttached("InBackdrop", typeof(bool),
        typeof(LiquidGlassBackdrop), new PropertyMetadata(true));

    public static bool GetInBackdrop(DependencyObject element) => (bool)element.GetValue(InBackdropProperty);

    public static void SetInBackdrop(DependencyObject element, bool value) => element.SetValue(InBackdropProperty, value);

    /// <summary>The element of the piece <paramref name="piece"/> lies inside, or null.</summary>
    private static Visual? Outer(LiquidGlassBackdrop piece)
    {
        if (VisualTreeHelper.GetParent(piece) is not { } element) return null;
        for (var node = VisualTreeHelper.GetParent(element); node != null; node = VisualTreeHelper.GetParent(node))
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                if (VisualTreeHelper.GetChild(node, i) is LiquidGlassBackdrop other && !ReferenceEquals(other, piece)) return node as Visual;
            }
        }
        return null;
    }

    /// <summary>A nested piece reads its outer element's content beneath it.</summary>
    private void UseInside(Visual outer)
    {
        var inside = new InsideGlassSource(this, outer);
        _source = inside;
        _source.Changed += OnSourceChanged;
        _source.Repainted += ToneSoon;
        // Its tone is the outer content over the outer piece's own backdrop.
        _outerPiece = null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(outer); i++)
        {
            if (VisualTreeHelper.GetChild(outer, i) is LiquidGlassBackdrop other && !ReferenceEquals(other, this)) _outerPiece = other;
        }
        if (_outerPiece != null) _outerPiece.ToneChanged += ToneSoon;
        inside.Refresh();
        OnSourceChanged();
    }

    private LiquidGlassBackdrop? _outerPiece;

    /// <summary>
    /// The tone of what the piece shows (Plan AD): the mean light of its backdrop after the recipe's brightness, as an
    /// sRGB colour, or null while there is none (no picture yet, the glass off); <see cref="Tones"/> holds it in cells
    /// about a line of text high across the piece. The look's ink follows them (GlassInk). The GPU path reads them on the GPU from the desktop
    /// copy as it draws; the CPU path, the wallpaper and a nested piece read them from the picture they already hold, at
    /// most ten times a second.
    /// </summary>
    internal Color? Tone { get; private set; }

    /// <summary>The tone in cells about a line of text high across the piece, in its device pixels; current while
    /// <see cref="Tone"/> is.</summary>
    internal ToneGrid Tones { get; } = new();

    /// <summary>Raised on the UI thread when <see cref="Tone"/> or <see cref="Tones"/> changed.</summary>
    internal event Action? ToneChanged;

    /// <summary>No tone: no picture, or the glass off.</summary>
    internal void ClearTone()
    {
        if (Tone == null) return;
        Tone = null;
        Tones.Clear();
        ToneChanged?.Invoke();
    }

    /// <summary>New cells (<see cref="BackdropTone.Pack"/>), <paramref name="cols"/> by <paramref name="rows"/> over
    /// <paramref name="region"/>, in the piece's device pixels: the GPU path's read (GpuGlassWindow), or the picture's.</summary>
    internal void SetTones(ReadOnlySpan<uint> cells, int cols, int rows, Rect region)
    {
        var changed = Tones.Set(cells, cols, rows, region);
        var tone = BackdropTone.MeanOf(Tones.Cells, Tones.Count);
        if (!changed && Tone == tone) return;
        Tone = tone;
        ToneChanged?.Invoke();
    }

    /// <summary>How often a piece off the GPU path reads its tone, at most.</summary>
    internal static readonly TimeSpan ToneEvery = TimeSpan.FromMilliseconds(100);

    private DispatcherTimer? _toneTimer;
    private Rect? _toneBox;
    private RectangleGeometry? _pictureClip;
    private Size _arranged;
    private uint[]? _toneCells;

    /// <summary>Reads the tone off the GPU path: at once when there is none yet, else once the moment has passed.</summary>
    private void ToneSoon()
    {
        if (_gpu != null) return;
        if (Tone == null && _source != null)
        {
            MeasureTone();
            return;
        }
        if (_toneTimer == null)
        {
            _toneTimer = new DispatcherTimer(ToneEvery, DispatcherPriority.Background, (_, _) =>
            {
                _toneTimer!.Stop();
                MeasureTone();
            }, Dispatcher);
            _toneTimer.Stop();
        }
        if (!_toneTimer.IsEnabled) _toneTimer.Start();
    }

    /// <summary>How many times the piece read its tone off the GPU path, for the tests.</summary>
    internal int ToneReads { get; private set; }

    /// <summary>Whether no read waits for its moment, for the tests.</summary>
    internal bool ToneIdle => _toneTimer is not { IsEnabled: true };

    /// <summary>The tone from the picture in hand: the CPU path's (its live pixels), the wallpaper's (a small copy made once
    /// per picture), or, for a nested piece, its finished glass over the outer piece's tone where it lies.</summary>
    private void MeasureTone()
    {
        if (_gpu != null) return;
        if (ScreenBox() is not { } box)
        {
            ClearTone();
            return;
        }
        ToneReads++;
        var (cols, rows) = BackdropTone.GridFor(box.Width, box.Height);
        if (_toneCells == null || _toneCells.Length < cols * rows) _toneCells = new uint[cols * rows];
        var cells = _toneCells;
        if (_source is InsideGlassSource inside)
        {
            if (_outerPiece is not { Tone: { } outerTone } outer || outer.ScreenBox() is not { } outerBox)
            {
                ClearTone();
                return;
            }
            // Its own finished glass (the outer content it bends, a navy blotch where there is one) over the outer piece's
            // glass, cell by cell.
            var (pixels, pw, ph) = (inside.Pixels, inside.Width, inside.Height);
            double w = box.Width / cols, h = box.Height / rows;
            for (var j = 0; j < rows; j++)
            {
                for (var i = 0; i < cols; i++)
                {
                    var area = new Rect(box.X - outerBox.X + i * w, box.Y - outerBox.Y + j * h, w, h);
                    var under = outer.Tones.Mean(area, out var r, out var g, out var b) ? BackdropTone.FromLinear(r, g, b) : outerTone;
                    var own = pixels.Length == pw * ph * 4 && pw > 0
                        ? BackdropTone.MeanPremultiplied(pixels, pw, ph, pw * 4, new Int32Rect((int)(i * w), (int)(j * h), Math.Max(1, (int)Math.Ceiling(w)), Math.Max(1, (int)Math.Ceiling(h))))
                        : inside.Mean ?? Colors.Transparent;
                    cells[j * cols + i] = BackdropTone.Pack(Contrast.Over(own, under));
                }
            }
            SetTones(cells, cols, rows, new Rect(0, 0, box.Width, box.Height));
            return;
        }
        if (_source?.Image is not BitmapSource image || _source.ScreenBounds is not { Width: > 0, Height: > 0 } bounds)
        {
            ClearTone();
            return;
        }
        if (ToneCells(image, bounds, box, Brightness, Scale, _dpi, cols, rows, cells) is { } region) SetTones(cells, cols, rows, region);
    }

    /// <summary>The tone of what the glass shows of <paramref name="image"/> (which lies at <paramref name="bounds"/> on the
    /// screen) over <paramref name="box"/> (screen pixels), in cells, bent as the glass bends it once its field is made
    /// (<see cref="BackdropTone.MeasureBent"/>); the region they cover in the box's own pixels, or null where the box
    /// misses the picture.</summary>
    internal static unsafe Rect? ToneCells(BitmapSource image, Rect bounds, Rect box, double brightness, double scale, double dpi, int cols, int rows, Span<uint> cells)
    {
        var field = DisplacementField.Made((int)Math.Round(box.Width), (int)Math.Round(box.Height), dpi);
        if (image is WriteableBitmap { Format.BitsPerPixel: 32, IsFrozen: false } live)
        {
            live.Lock();
            try
            {
                var pixels = new ReadOnlySpan<byte>((void*)live.BackBuffer, live.BackBufferStride * live.PixelHeight);
                return BackdropTone.MeasureBent(pixels, live.PixelWidth, live.PixelHeight, live.BackBufferStride, bounds, box, brightness, field, scale, cols, rows,
                    BackdropTone.CpuSamples, cells);
            }
            finally
            {
                live.Unlock();
            }
        }
        var small = SmallCopy.Of(image);
        return BackdropTone.MeasureBent(small.Pixels, small.Width, small.Height, small.Width * 4, bounds, box, brightness, field, scale, cols, rows,
            BackdropTone.CpuSamples, cells);
    }

    /// <summary>A still picture (the wallpaper) at most 256 pixels wide, in BGRA, made once per picture.</summary>
    private sealed class SmallCopy
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, SmallCopy> Made = [];

        public byte[] Pixels = [];
        public int Width, Height;

        public static SmallCopy Of(BitmapSource image) => Made.GetValue(image, Make);

        private static SmallCopy Make(BitmapSource image)
        {
            var k = Math.Min(1, 256.0 / Math.Max(1, image.PixelWidth));
            BitmapSource scaled = k < 1 ? new TransformedBitmap(image, new ScaleTransform(k, k)) : image;
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            var copy = new SmallCopy { Width = bgra.PixelWidth, Height = bgra.PixelHeight };
            copy.Pixels = new byte[copy.Width * copy.Height * 4];
            bgra.CopyPixels(copy.Pixels, copy.Width * 4, 0);
            return copy;
        }
    }

    /// <summary>The source map in use is ready, for a test.</summary>
    internal bool IsReady => _source is InsideGlassSource
        ? _finished.Visibility == Visibility.Visible && _finished.Source != null
        : _mapFor != null && _mapFor == _mapWanted && _displaceHost.Visibility == Visibility.Visible;

    /// <summary>Why the last source map couldn't be made, for a test; null when it was.</summary>
    internal Exception? MapError { get; private set; }

    /// <summary>Raised when a new source map is on the displacement pass, for a test.</summary>
    internal event Action? MapReady;

    /// <summary>Whether the piece draws through its window's GPU path now.</summary>
    internal bool OnGpu => _gpu != null;

    /// <summary>Whether the piece shows its rectangle of the GPU path's shared picture now (imaged).</summary>
    internal bool ShowsGpu => _gpu != null && _gpuHost.Visibility == Visibility.Visible;

    /// <summary>Whether the piece's glass is composed beneath its window's content (DirectComposition).</summary>
    internal bool Composed => _gpu?.Composes(this) ?? false;

    protected override int VisualChildrenCount => 3;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _displaceHost,
        1 => _gpuHost,
        2 => _finished,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        _displaceHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _gpuHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _finished.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _arranged = finalSize;
        var sigma = BlurDeviation * _dpi;
        // The blur reaches ceil(3 sigma) texels, each bilinear pair one further; two more keep the edge texels whole.
        _margin = Math.Min(LiquidGlassEffects.MaxBlurRadius, (int)Math.Ceiling(3 * sigma)) + 2;
        var m = _margin / _dpi;
        _displaceHost.Arrange(new Rect(-m, -m, finalSize.Width + 2 * m, finalSize.Height + 2 * m));
        _gpuHost.Arrange(new Rect(finalSize));
        _finished.Arrange(new Rect(0, 0, Math.Round(finalSize.Width * _dpi) / _dpi, Math.Round(finalSize.Height * _dpi) / _dpi));
        _across.Sigma = sigma;
        _down.Sigma = sigma;
        var texel = new Point4D(1 / Math.Round(finalSize.Width * _dpi + 2 * _margin), 1 / Math.Round(finalSize.Height * _dpi + 2 * _margin), 0, 0);
        _across.Texel = texel;
        _down.Texel = texel;
        _displace.Texel = texel;
        double w = Math.Round(finalSize.Width * _dpi), h = Math.Round(finalSize.Height * _dpi);
        _bright.Box = new Point4D(_margin * texel.X, _margin * texel.Y, (_margin + w) * texel.X, (_margin + h) * texel.Y);
        UpdateClip(finalSize);
        if (_source is not InsideGlassSource) UpdateMap(finalSize);
        Place();
        return finalSize;
    }

    /// <summary>Uses <paramref name="source"/> directly, without a window's (a test that draws the piece to a bitmap).</summary>
    internal void Use(ILiquidGlassSource source)
    {
        Detach();
        if (Outer(this) is { } outer)
        {
            UseInside(outer);
            return;
        }
        _source = source;
        _source.Changed += OnSourceChanged;
        _source.Repainted += ToneSoon;
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
        if (Outer(this) is { } outer)
        {
            if (_source is InsideGlassSource) return;
            Detach();
            UseInside(outer);
            return;
        }
        var window = PresentationSource.FromVisual(this) as HwndSource;
        if (window == null || (ReferenceEquals(window, _window) && _source != null)) return;
        Detach();
        _window = window;
        _source = LiquidGlassSources.Acquire(window);
        _source.Changed += OnSourceChanged;
        _source.Repainted += ToneSoon;
        OnSourceChanged();
    }

    private void Detach()
    {
        UseGpu(null);
        if (_source != null)
        {
            _source.Changed -= OnSourceChanged;
            _source.Repainted -= ToneSoon;
        }
        if (_window != null && _source != null) LiquidGlassSources.Release(_window);
        (_source as InsideGlassSource)?.Dispose();
        _source = null;
        if (_outerPiece != null) _outerPiece.ToneChanged -= ToneSoon;
        _outerPiece = null;
        _toneTimer?.Stop();
        _toneBox = null;
        ClearTone();
        _window = null;
        _picture.Source = null;
        _displaceHost.Visibility = Visibility.Hidden;
        _finished.Source = null;
        _finished.Visibility = Visibility.Hidden;
        SetValue(KindKey, LiquidGlassSourceKind.None);
    }

    private void OnSourceChanged()
    {
        if (_source == null) return;
        UseGpu((_source as WindowGlassSource)?.Gpu);
        SetValue(KindKey, _gpu != null ? _source.Kind : _source.Image == null ? LiquidGlassSourceKind.None : _source.Kind);
        if (_source is not InsideGlassSource && !ReferenceEquals(_picture.Source, _source.Image))
        {
            _picture.Source = _source.Image;
            // The live picture and the content inside a piece lie pixel on pixel; the wallpaper is scaled to the screen.
            RenderOptions.SetBitmapScalingMode(_picture, _source.Kind == LiquidGlassSourceKind.Wallpaper ? BitmapScalingMode.Linear : BitmapScalingMode.NearestNeighbor);
        }
        Place();
        // New pixels behind (or a new kind): the ink may follow. The GPU path reads its own as it draws.
        ToneSoon();
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
        if (_source is InsideGlassSource inside)
        {
            // Inside another piece the glass comes finished, for the piece's own box: one bitmap, no effects.
            if (!ReferenceEquals(_finished.Source, inside.Image)) _finished.Source = inside.Image;
            var shown = inside.Image != null ? Visibility.Visible : Visibility.Hidden;
            if (_finished.Visibility != shown) _finished.Visibility = shown;
            if (_displaceHost.Visibility == Visibility.Visible) _displaceHost.Visibility = Visibility.Hidden;
            return;
        }
        if (_source?.Image is not { } image || ScreenBox() is not { } box)
        {
            if (_displaceHost.Visibility == Visibility.Visible) _displaceHost.Visibility = Visibility.Hidden;
            return;
        }
        var bounds = _source.ScreenBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (_toneBox != box)
        {
            // Moved or resized over the picture: what it shows is elsewhere.
            _toneBox = box;
            ToneSoon();
        }
        var m = _margin / _dpi;
        PlacePicture(m + (bounds.X - box.X) / _dpi, m + (bounds.Y - box.Y) / _dpi, bounds.Width / _dpi, bounds.Height / _dpi);
    }

    private void PlacePicture(double left, double top, double width, double height)
    {
        if (Canvas.GetLeft(_picture) != left) Canvas.SetLeft(_picture, left);
        if (Canvas.GetTop(_picture) != top) Canvas.SetTop(_picture, top);
        if (_picture.Width != width) _picture.Width = width;
        if (_picture.Height != height) _picture.Height = height;
        // The picture cut to the box and its margin on the picture itself: the canvas's own clip leaves the effects' input
        // the whole picture's size where the picture is much larger than the piece (the window's, the wallpaper), and the
        // passes then read the picture far outside the box (a white window under a bar showed in the bar).
        var m = _margin / _dpi;
        var seen = new Rect(-left, -top, _arranged.Width + 2 * m, _arranged.Height + 2 * m);
        if (_pictureClip?.Rect != seen)
        {
            _pictureClip = new RectangleGeometry(seen);
            _pictureClip.Freeze();
            _picture.Clip = _pictureClip;
        }
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
            if (gpu == null) CacheMode = Hardware() && _source is not InsideGlassSource ? _cache : null;
            return;
        }
        if (_gpu != null)
        {
            _gpu.Changed -= PlaceOnGpu;
            _gpu.Unfollow(this);
            _gpu.Remove(this);
        }
        _gpu = gpu;
        if (gpu != null)
        {
            gpu.Changed += PlaceOnGpu;
            gpu.Follow(this);
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
            CacheMode = Hardware() && _source is not InsideGlassSource ? _cache : null;
        }
    }

    /// <summary>Whether WPF draws the piece's window in hardware (not a remote session, not software, not a bitmap).</summary>
    private bool Hardware()
        => PresentationSource.FromVisual(this) is HwndSource { CompositionTarget.RenderMode: not RenderMode.SoftwareOnly } && (RenderCapability.Tier >> 16) >= 2
           && !SystemParameters.IsRemoteSession && RenderOptions.ProcessRenderMode != RenderMode.SoftwareOnly;

    /// <summary>While something animates, each frame: places the piece again only if where WPF shows it or its opacity
    /// moved since the last look (a quick look: its transform to the root and its ancestors' opacity).</summary>
    internal void FollowFrame()
    {
        if (_gpu == null || !IsVisible || PresentationSource.FromVisual(this)?.RootVisual is not UIElement root) return;
        Matrix where;
        try
        {
            where = Chain(this, root) ?? throw new InvalidOperationException();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        var opacity = Opacity;
        for (var node = VisualTreeHelper.GetParent(this) as UIElement; node != null; node = VisualTreeHelper.GetParent(node) as UIElement) opacity *= node.Opacity;
        var look = (where, opacity, RenderSize);
        if (_lastLook == look) return;
        _lastLook = look;
        PlaceOnGpu();
    }

    private (Matrix, double, Size)? _lastLook;

    /// <summary>Tells the GPU path where the piece is and how it shows. A composed piece draws nothing (its glass lies
    /// beneath the window's WPF content); an imaged one shows its rectangle of the window's shared picture.</summary>
    internal void PlaceOnGpu()
    {
        if (_gpu == null) return;
        if (!IsVisible || Placement() is not { } placed)
        {
            _gpu.Remove(this);
            ShowImaged(null);
            return;
        }
        _gpu.Update(this, placed.Box, placed.Shape, _dpi, Brightness, BlurDeviation * _dpi, Scale);
        ShowImaged(_gpu.Composes(this) || _gpu.Image.PixelWidth <= 0 ? null : _gpu.TargetOf(this));
    }

    /// <summary>Shows the imaged piece's rectangle of the window's shared picture, or nothing (null). A piece that shows
    /// nothing lets go of the picture too: WPF marks every user of a D3DImage dirty when it changes, hidden or not, and
    /// each frame of glass would draw them again.</summary>
    private void ShowImaged(Int32Rect? target)
    {
        if (target is not { } t)
        {
            if (_gpuPicture.ImageSource != null) _gpuPicture.ImageSource = null;
            if (_gpuHost.Visibility == Visibility.Visible) _gpuHost.Visibility = Visibility.Hidden;
            return;
        }
        if (!ReferenceEquals(_gpuPicture.ImageSource, _gpu?.Image)) _gpuPicture.ImageSource = _gpu?.Image;
        // A D3DImage's units are its pixels.
        var viewbox = new Rect(t.X, t.Y, t.Width, t.Height);
        if (_gpuPicture.Viewbox != viewbox) _gpuPicture.Viewbox = viewbox;
        if (_gpuHost.Visibility != Visibility.Visible) _gpuHost.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Where the piece is drawn on the GPU path and how it shows, in physical pixels: the box is the piece's own size,
    /// centred where WPF shows it now (a spring's scale or slide moves it; a scale doesn't resize what it reads); the shape
    /// is the piece as WPF shows it, scaled, its corners with it; what can be seen of it is what its ancestors' clips
    /// leave (a scrolled page's viewport, a pane's edge); the opacity is theirs times its own (a fade).
    /// </summary>
    private (Int32Rect Box, GpuGlassShape Shape)? Placement()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || PresentationSource.FromVisual(this)?.RootVisual is not UIElement root) return null;
        int w = (int)Math.Round(ActualWidth * _dpi), h = (int)Math.Round(ActualHeight * _dpi);
        var origin = root.PointToScreen(new Point(0, 0));
        Rect ToScreen(Rect r) => new(origin.X + r.X * _dpi, origin.Y + r.Y * _dpi, r.Width * _dpi, r.Height * _dpi);
        Rect shown;
        try
        {
            shown = ToScreen(Bounds(new Rect(RenderSize), Chain(this, root) ?? throw new InvalidOperationException()));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        var box = new Int32Rect((int)Math.Round(shown.X + shown.Width / 2 - w / 2.0), (int)Math.Round(shown.Y + shown.Height / 2 - h / 2.0), w, h);
        // What the ancestors' clips (their layout clips too: VisualTreeHelper.GetClip) and opacity leave.
        var visible = shown;
        var opacity = Opacity;
        for (var node = VisualTreeHelper.GetParent(this) as Visual; node != null && !ReferenceEquals(node, root); node = VisualTreeHelper.GetParent(node) as Visual)
        {
            if (node is UIElement element) opacity *= element.Opacity;
            if (VisualTreeHelper.GetClip(node) is { } clip)
            {
                if (Chain(node, root) is { } toRoot) visible.Intersect(ToScreen(Bounds(clip.Bounds, toRoot)));
            }
            if (visible.IsEmpty) break;
        }
        opacity *= root.Opacity;
        double scaleX = shown.Width / Math.Max(1, ActualWidth * _dpi), scaleY = shown.Height / Math.Max(1, ActualHeight * _dpi);
        var r = CornerRadius;
        double k = _dpi * Math.Min(scaleX, scaleY), most = Math.Min(shown.Width, shown.Height) / 2;
        var radii = new CornerRadius(Math.Min(most, r.TopLeft * k), Math.Min(most, r.TopRight * k), Math.Min(most, r.BottomRight * k), Math.Min(most, r.BottomLeft * k));
        Rect Local(Rect s) => s.IsEmpty ? Rect.Empty : new Rect(Math.Round(s.X - box.X, 3), Math.Round(s.Y - box.Y, 3), Math.Round(s.Width, 3), Math.Round(s.Height, 3));
        return (box, new GpuGlassShape(Local(shown), radii, Local(visible), Math.Round(opacity, 3)));
    }

    /// <summary>
    /// The transform from <paramref name="from"/> to its ancestor <paramref name="ancestor"/>, from each visual's offset
    /// and transform, or null when it isn't an ancestor. WPF's TransformToAncestor works out every ancestor's drawing
    /// bounds when one of them has an Effect (the look's text shadow sits over most pieces): half the UI thread's time with
    /// glass on. An effect here moves nothing, so it is left out.
    /// </summary>
    internal static Matrix? Chain(Visual from, Visual ancestor)
    {
        var total = Matrix.Identity;
        for (Visual? node = from; node != null; node = VisualTreeHelper.GetParent(node) as Visual)
        {
            if (ReferenceEquals(node, ancestor)) return total;
            var local = Matrix.Identity;
            if (VisualTreeHelper.GetTransform(node) is { } transform) local = transform.Value;
            var offset = VisualTreeHelper.GetOffset(node);
            local.Translate(offset.X, offset.Y);
            total *= local;
        }
        return null;
    }

    private static Rect Bounds(Rect r, Matrix m)
    {
        r.Transform(m);
        return r;
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
            // The field is made: the tone again, bent as the glass now bends it.
            ToneSoon();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
