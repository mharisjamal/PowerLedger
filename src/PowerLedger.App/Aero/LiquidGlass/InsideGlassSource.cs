using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PowerLedger.App.Aero;

/// <summary>
/// What a piece inside another piece reads (<see cref="LiquidGlassBackdrop.IsNested"/>), as Chromium reads it: in
/// Chromium a backdrop-filter element is a backdrop root, so a backdrop-filter inside it reads only what the outer
/// element painted before it (its background and the content before the inner piece, over transparent), never the page
/// or the screen, and not the outer piece's own glass. The mockup's ring hole bends the ring beneath it; a bubble on a
/// well reads the well's tint; a bubble on bare glass reads nothing at all.
/// <para>The picture is the inner piece's own box of the outer element's content, drawn in WPF's painting order up to
/// the inner piece: each visual's own drawing (VisualTreeHelper.GetDrawing) with its offset, transform, clip and opacity;
/// backdrops and the parts the look marks out (<see cref="LiquidGlassBackdrop.InBackdropProperty"/>: a piece's drop shadow
/// and its highlights, which CSS paints apart from the content) left out. It is drawn again only when what it would draw
/// changed: once after a burst of layout passes, or each frame while AeroMotion animates, the drawings of the visuals that
/// can reach the box (an element's laid out size and a few units more) are walked and hashed, and only a new hash or a
/// new size rasterises them. The picture always lies at the piece, so a moved window draws nothing again. Nothing runs at
/// rest, and the window's own WPF drawing is never touched. A colour changed without a layout pass shows at the next
/// one.</para>
/// <para>The picture it hands over is the finished glass: the recipe run on the CPU (<see cref="GlassRecipeCpu"/>), at the
/// effects' precision, so WPF draws one bitmap for the piece and no effects.</para>
/// <para>A visual's Effect (a text shadow) is not drawn: a DrawingGroup has no effects.</para>
/// </summary>
internal sealed class InsideGlassSource : ILiquidGlassSource, IDisposable
{
    private readonly LiquidGlassBackdrop _piece;
    private readonly Visual _outer;
    private int? _hash;
    private bool _hooked;
    private DateTime _followUntil;
    private bool _disposed;

    public InsideGlassSource(LiquidGlassBackdrop piece, Visual outer)
    {
        _piece = piece;
        _outer = outer;
        // Followed only on a screen: a piece drawn to a bitmap (a test) is drawn once, and nothing holds on to it.
        _live = PresentationSource.FromVisual(piece) != null;
        if (!_live) return;
        _piece.LayoutUpdated += OnLayoutUpdated;
        LiquidGlassSources.Animating += OnAnimating;
    }

    private readonly bool _live;

    /// <summary>How many times a picture was rasterised, for the measurements.</summary>
    internal static long Drawn;

    /// <summary>How many times the content was walked and hashed, for the measurements.</summary>
    internal static long Checked;

    /// <summary>The time spent walking, hashing and drawing, in stopwatch ticks, for the measurements.</summary>
    internal static long Ticks;

    public LiquidGlassSourceKind Kind => LiquidGlassSourceKind.Inside;

    public ImageSource? Image { get; private set; }

    /// <summary>The piece's own box: the picture is always the piece's, wherever its window is.</summary>
    public Rect ScreenBounds => _piece.ScreenBox() ?? Rect.Empty;

    public event Action? Changed;

    /// <summary>Draws the picture now if what it shows changed; true when it did.</summary>
    public bool Refresh()
    {
        if (_disposed) return false;
        var dpi = VisualTreeHelper.GetDpi(_piece);
        var size = new Size(Math.Round(_piece.ActualWidth * dpi.DpiScaleX), Math.Round(_piece.ActualHeight * dpi.DpiScaleY));
        if (size.Width <= 0 || size.Height <= 0) return false;
        // The frame everything is drawn and compared in is the piece's own: what moves, scales or fades the piece and what
        // lies beneath it alike (the intro gliding a pane's content, a fade over both) changes nothing in its picture.
        Matrix toPiece;
        try
        {
            toPiece = LiquidGlassBackdrop.Chain(_piece, _outer) ?? throw new InvalidOperationException();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        if (!toPiece.HasInverse) return false;   // squashed to nothing (a bar grown from its foot): nothing shows
        toPiece.Invert();
        Interlocked.Increment(ref Checked);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return Draw(size, dpi, toPiece);
        }
        finally
        {
            Interlocked.Add(ref Ticks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }
    }

    private bool Draw(Size size, DpiScale dpi, Matrix toPiece)
    {
        var drawing = new DrawingGroup();
        var hash = new HashCode();
        hash.Add(size);
        hash.Add(_piece.Brightness);
        hash.Add(_piece.BlurDeviation);
        hash.Add(_piece.Scale);
        hash.Add(dpi.DpiScaleX);
        var done = false;
        using (var context = drawing.Open())
        {
            Walk(_outer, Matrix.Identity, 0, context, ref hash, toPiece, Shared(), ref done, top: true);
        }
        var value = hash.ToHashCode();
        if (value == _hash && Image != null) return false;
        _hash = value;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new MatrixTransform(toPiece));
            context.DrawDrawing(drawing);
        }
        int w = (int)size.Width, h = (int)size.Height;
        var content = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        content.Render(visual);
        var pixels = new byte[w * h * 4];
        content.CopyPixels(pixels, w * 4, 0);
        // The finished glass, drawn here once rather than by four WPF effects on every frame WPF draws the piece.
        var glass = GlassRecipeCpu.Draw(pixels, w, h, dpi.DpiScaleX, _piece.Brightness, _piece.BlurDeviation * dpi.DpiScaleX, _piece.Scale);
        var bitmap = BitmapSource.Create(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null, glass, w * 4);
        bitmap.Freeze();
        Image = bitmap;
        Interlocked.Increment(ref Drawn);
        Changed?.Invoke();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_live) return;
        _piece.LayoutUpdated -= OnLayoutUpdated;
        LiquidGlassSources.Animating -= OnAnimating;
        Hook(false);
    }

    /// <summary>The outer element and the piece's ancestors inside it: whatever they do to the piece they do to what lies
    /// beneath it too.</summary>
    private HashSet<DependencyObject> Shared()
    {
        var shared = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        for (var node = VisualTreeHelper.GetParent(_piece); node != null; node = VisualTreeHelper.GetParent(node))
        {
            shared.Add(node);
            if (ReferenceEquals(node, _outer)) break;
        }
        return shared;
    }

    /// <summary>Adds <paramref name="node"/> and what it holds, in WPF's painting order, until the inner piece. Only what
    /// reaches the piece goes into the hash: its drawing, where it lands in the piece's own frame (<paramref name="above"/>
    /// and its own transform, through <paramref name="toPiece"/>) and the opacity, clips and masks over it
    /// (<paramref name="path"/>), so a digit rolling elsewhere in the pane draws nothing again. An opacity or a mask on
    /// the piece's own ancestors (<paramref name="shared"/>) is the piece's to take once, over its finished glass, as an
    /// opacity group is in the browser: it is neither drawn into the picture nor compared.</summary>
    private void Walk(DependencyObject node, Matrix above, int path, DrawingContext context, ref HashCode hash, Matrix toPiece, HashSet<DependencyObject> shared, ref bool done, bool top)
    {
        if (done) return;
        if (ReferenceEquals(node, _piece))
        {
            done = true;
            return;
        }
        if (node is LiquidGlassBackdrop || !LiquidGlassBackdrop.GetInBackdrop(node) || node is not Visual visual) return;
        if (node is UIElement { Visibility: not Visibility.Visible }) return;
        // The outer element is drawn where it is (its own offset and transform are the frame the box is in).
        var local = Matrix.Identity;
        if (!top)
        {
            var offset = VisualTreeHelper.GetOffset(visual);
            local.Translate(offset.X, offset.Y);
            if (VisualTreeHelper.GetTransform(visual) is { } transform) local = transform.Value * local;
        }
        var here = local * above;
        // Its own drawing is taken only where it can reach the box: an element's laid out size and a margin for what
        // spills past it (a glyph's overhang, antialiasing). WPF's own drawing bounds would cost a walk of every drawing.
        var reach = visual is UIElement element ? new Rect(element.RenderSize) : VisualTreeHelper.GetContentBounds(visual);
        if (!reach.IsEmpty) reach.Inflate(Spill, Spill);
        var inPiece = here * toPiece;
        var reaches = !reach.IsEmpty && Transform(reach, inPiece).IntersectsWith(new Rect(_piece.RenderSize));
        var mine = shared.Contains(node);
        var opacity = mine ? 1 : VisualTreeHelper.GetOpacity(visual);
        var clip = VisualTreeHelper.GetClip(visual);
        var mask = mine ? null : VisualTreeHelper.GetOpacityMask(visual);
        context.PushTransform(new MatrixTransform(local));
        if (clip != null) context.PushClip(clip);
        if (opacity < 1) context.PushOpacity(opacity);
        if (mask != null) context.PushOpacityMask(mask);
        var over = new HashCode();
        over.Add(path);
        over.Add(opacity);
        if (clip != null) over.Add(Token(clip));
        if (mask != null) over.Add(Token(mask));
        path = over.ToHashCode();
        if (reaches && VisualTreeHelper.GetDrawing(visual) is { } own)
        {
            context.DrawDrawing(own);
            hash.Add(inPiece);
            hash.Add(path);
            Add(own, ref hash);
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual) && !done; i++) Walk(VisualTreeHelper.GetChild(visual, i), here, path, context, ref hash, toPiece, shared, ref done, top: false);
        if (mask != null) context.Pop();
        if (opacity < 1) context.Pop();
        if (clip != null) context.Pop();
        context.Pop();
    }

    /// <summary>How far past an element's laid out size its own drawing is taken to reach.</summary>
    private const double Spill = 4;

    /// <summary>A geometry's shape, as a value, cheaply: a rectangle's or an ellipse's numbers, a path's figures, a
    /// combination's parts.</summary>
    private static int Token(System.Windows.Media.Geometry? geometry)
    {
        var hash = new HashCode();
        switch (geometry)
        {
            case null:
                break;
            case RectangleGeometry r:
                hash.Add(r.Rect);
                hash.Add(r.RadiusX);
                hash.Add(r.RadiusY);
                break;
            case EllipseGeometry e:
                hash.Add(e.Center);
                hash.Add(e.RadiusX);
                hash.Add(e.RadiusY);
                break;
            case LineGeometry l:
                hash.Add(l.StartPoint);
                hash.Add(l.EndPoint);
                break;
            case CombinedGeometry c:
                hash.Add(c.GeometryCombineMode);
                hash.Add(Token(c.Geometry1));
                hash.Add(Token(c.Geometry2));
                break;
            case GeometryGroup g:
                hash.Add(g.FillRule);
                foreach (var child in g.Children) hash.Add(Token(child));
                break;
            default:
                hash.Add(geometry.ToString(CultureInfo.InvariantCulture));
                break;
        }
        if (geometry != null) hash.Add(geometry.Transform?.Value ?? Matrix.Identity);
        return hash.ToHashCode();
    }

    private static Rect Transform(Rect r, Matrix m)
    {
        r.Transform(m);
        return r;
    }

    /// <summary>Adds what a drawing draws to the hash: its shapes, glyphs, pictures and their brushes.</summary>
    private static void Add(Drawing drawing, ref HashCode hash)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                hash.Add(group.Transform?.Value ?? Matrix.Identity);
                hash.Add(group.Opacity);
                if (group.ClipGeometry != null) hash.Add(Token(group.ClipGeometry));
                if (group.OpacityMask != null) hash.Add(Token(group.OpacityMask));
                foreach (var child in group.Children) Add(child, ref hash);
                break;
            case GeometryDrawing shape:
                hash.Add(Token(shape.Geometry));
                hash.Add(Token(shape.Brush));
                if (shape.Pen != null)
                {
                    hash.Add(Token(shape.Pen.Brush));
                    hash.Add(shape.Pen.Thickness);
                }
                break;
            case GlyphRunDrawing text:
                hash.Add(Token(text.ForegroundBrush));
                if (text.GlyphRun is { } run)
                {
                    hash.Add(run.BaselineOrigin);
                    hash.Add(run.FontRenderingEmSize);
                    foreach (var glyph in run.GlyphIndices) hash.Add(glyph);
                }
                break;
            case ImageDrawing image:
                hash.Add(image.Rect);
                hash.Add(image.ImageSource);
                break;
            default:
                hash.Add(drawing);
                break;
        }
    }

    /// <summary>A brush's look, as a value: a colour's colour, a gradient's stops, anything else itself.</summary>
    private static int Token(Brush? brush)
    {
        var hash = new HashCode();
        switch (brush)
        {
            case null:
                break;
            case SolidColorBrush solid:
                hash.Add(solid.Color);
                break;
            case GradientBrush gradient:
                foreach (var stop in gradient.GradientStops)
                {
                    hash.Add(stop.Color);
                    hash.Add(stop.Offset);
                }
                if (gradient is LinearGradientBrush linear)
                {
                    hash.Add(linear.StartPoint);
                    hash.Add(linear.EndPoint);
                }
                else if (gradient is RadialGradientBrush radial)
                {
                    hash.Add(radial.Center);
                    hash.Add(radial.GradientOrigin);
                    hash.Add(radial.RadiusX);
                    hash.Add(radial.RadiusY);
                }
                hash.Add(gradient.MappingMode);
                break;
            default:
                hash.Add(brush);
                break;
        }
        if (brush != null)
        {
            hash.Add(brush.Opacity);
            hash.Add(brush.Transform?.Value ?? Matrix.Identity);
        }
        return hash.ToHashCode();
    }

    /// <summary>After a layout pass (a reading makes a few in a row), one look once the dispatcher is idle.</summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_queued) return;
        _queued = true;
        _piece.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            _queued = false;
            Refresh();
        });
    }

    private bool _queued;

    private void OnAnimating(TimeSpan length)
    {
        var until = DateTime.UtcNow + length + TimeSpan.FromMilliseconds(50);
        if (until > _followUntil) _followUntil = until;
        Hook(true);
    }

    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>Whether this piece is still to be looked at for the animation that ended: the frame's share ran out first.</summary>
    private bool _behind;

    private void OnFrame(object? sender, EventArgs e)
    {
        var frame = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        if (InsideGlassBudget.May(frame))
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Refresh();
            InsideGlassBudget.Spend(frame, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            _behind = false;
        }
        else _behind = true;
        if (DateTime.UtcNow > _followUntil && !_behind) Hook(false);
    }
}

/// <summary>
/// The share of a frame the pieces inside other pieces may take while something animates (the intro gliding the panes'
/// content in, the bars growing from their feet, the ring sweeping round under its glass hole): once the frame's pieces
/// have spent <see cref="Budget"/>, the rest wait for a later frame (their pictures meanwhile move with them), and a piece behind when the
/// animation ends is looked at once more. One piece is always looked at, so each frame moves on. Without it every nested
/// piece drew on every frame, 15 of them at once as the bars grew, and the Dashboard's intro ran at 150 to 580 ms a frame.
/// </summary>
internal static class InsideGlassBudget
{
    /// <summary>What the nested pieces may spend of a frame: a third of one at 60 a second.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(5);

    [ThreadStatic] private static TimeSpan _frame;
    [ThreadStatic] private static long _spent;
    [ThreadStatic] private static bool _any;

    /// <summary>Whether a piece may look at its picture in the frame rendered at <paramref name="frame"/>.</summary>
    public static bool May(TimeSpan frame)
    {
        if (frame != _frame)
        {
            _frame = frame;
            _spent = 0;
            _any = false;
        }
        return !_any || _spent < Budget.TotalSeconds * System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>A piece spent <paramref name="ticks"/> (stopwatch ticks) of the frame at <paramref name="frame"/>.</summary>
    public static void Spend(TimeSpan frame, long ticks)
    {
        if (frame != _frame) May(frame);
        _spent += ticks;
        _any = true;
    }
}
