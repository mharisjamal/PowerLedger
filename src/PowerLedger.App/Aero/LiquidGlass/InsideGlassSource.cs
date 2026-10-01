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
/// changed: after a layout pass, or each frame while AeroMotion animates, the drawings are walked and hashed, and only a
/// new hash or a new box rasterises them. Nothing runs at rest, and the window's own WPF drawing is never touched.</para>
/// <para>A visual's Effect (a text shadow) is not drawn: a DrawingGroup has no effects.</para>
/// </summary>
internal sealed class InsideGlassSource : ILiquidGlassSource, IDisposable
{
    private readonly LiquidGlassBackdrop _piece;
    private readonly Visual _outer;
    private int? _hash;
    private Rect _bounds;
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

    public LiquidGlassSourceKind Kind => LiquidGlassSourceKind.Inside;

    public ImageSource? Image { get; private set; }

    public Rect ScreenBounds => _bounds;

    public event Action? Changed;

    /// <summary>Draws the picture now if what it shows changed; true when it did.</summary>
    public bool Refresh()
    {
        if (_disposed || _piece.ScreenBox() is not { } box || box.Width <= 0 || box.Height <= 0) return false;
        Rect inOuter;
        try
        {
            inOuter = _piece.TransformToAncestor(_outer).TransformBounds(new Rect(_piece.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        Interlocked.Increment(ref Checked);
        var drawing = new DrawingGroup();
        var hash = new HashCode();
        hash.Add(box.Size);
        hash.Add(inOuter);
        var done = false;
        using (var context = drawing.Open())
        {
            Walk(_outer, Matrix.Identity, context, ref hash, inOuter, ref done, top: true);
        }
        var value = hash.ToHashCode();
        if (value == _hash && Image != null)
        {
            // The same picture, moved with its window: only where it lies changes.
            if (_bounds == box) return false;
            _bounds = box;
            Changed?.Invoke();
            return true;
        }
        _hash = value;
        _bounds = box;
        var dpi = VisualTreeHelper.GetDpi(_piece);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new TranslateTransform(-inOuter.X, -inOuter.Y));
            context.DrawDrawing(drawing);
        }
        var bitmap = new RenderTargetBitmap((int)box.Width, (int)box.Height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
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

    /// <summary>Adds <paramref name="node"/> and what it holds, in WPF's painting order, until the inner piece.</summary>
    private void Walk(DependencyObject node, Matrix above, DrawingContext context, ref HashCode hash, Rect box, ref bool done, bool top)
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
        // Nothing of it reaches the box: left out (unless the inner piece is inside it).
        var bounds = VisualTreeHelper.GetDescendantBounds(visual);
        bounds.Union(VisualTreeHelper.GetContentBounds(visual));
        var reaches = !bounds.IsEmpty && Transform(bounds, here).IntersectsWith(box);
        if (!reaches && !_piece.IsDescendantOf(visual)) return;
        var opacity = VisualTreeHelper.GetOpacity(visual);
        var clip = VisualTreeHelper.GetClip(visual);
        var mask = VisualTreeHelper.GetOpacityMask(visual);
        context.PushTransform(new MatrixTransform(local));
        if (clip != null) context.PushClip(clip);
        if (opacity < 1) context.PushOpacity(opacity);
        if (mask != null) context.PushOpacityMask(mask);
        hash.Add(local);
        hash.Add(opacity);
        if (clip != null) hash.Add(clip.ToString(CultureInfo.InvariantCulture));
        if (mask != null) hash.Add(Token(mask));
        if (reaches && VisualTreeHelper.GetDrawing(visual) is { } own)
        {
            context.DrawDrawing(own);
            Add(own, ref hash);
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual) && !done; i++) Walk(VisualTreeHelper.GetChild(visual, i), here, context, ref hash, box, ref done, top: false);
        if (mask != null) context.Pop();
        if (opacity < 1) context.Pop();
        if (clip != null) context.Pop();
        context.Pop();
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
                if (group.ClipGeometry != null) hash.Add(group.ClipGeometry.ToString(CultureInfo.InvariantCulture));
                if (group.OpacityMask != null) hash.Add(Token(group.OpacityMask));
                foreach (var child in group.Children) Add(child, ref hash);
                break;
            case GeometryDrawing shape:
                hash.Add(shape.Geometry?.ToString(CultureInfo.InvariantCulture));
                hash.Add(shape.Geometry?.Transform?.Value ?? Matrix.Identity);
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

    private void OnLayoutUpdated(object? sender, EventArgs e) => Refresh();

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

    private void OnFrame(object? sender, EventArgs e)
    {
        Refresh();
        if (DateTime.UtcNow > _followUntil) Hook(false);
    }
}
