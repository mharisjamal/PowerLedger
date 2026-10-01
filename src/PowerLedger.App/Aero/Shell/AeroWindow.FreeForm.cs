using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PowerLedger.App.Aero;

namespace PowerLedger.App;

/// <summary>
/// Aero free-form over the desktop (0.10.1, the approved demo): the window is only its glass. Its shape is a window
/// region, the union of every glass surface's rounded rectangle (<see cref="RegionMath"/>), kept in step on each layout
/// pass and, while something moves on a transform (the intro, a page arriving, a dialog or toast springing in, the
/// camera), each frame for that time only; between the surfaces is no window at all, so the desktop and the windows
/// behind show there as they are and take the clicks. The window stays hardware-drawn: a layered window this size would
/// be far slower. Any glass that isn't a control drags the window (Windows' own move, so snapping works); a double
/// click on the top toggles between spread and the smaller centred layout; the shape's outer edges and the grip resize it.
/// <para>Glass moves the window from a press rather than by answering WM_NCHITTEST with HTCAPTION: a caption takes no
/// mouse moves, which would stop the pointer sheen, the charts' hover and every tooltip on the glass.</para>
/// </summary>
internal partial class AeroWindow
{
    /// <summary>How deep the resize band is along the shape's outer edges, and how far along an edge a corner reaches.</summary>
    private const double ResizeBand = 6;
    private const double ResizeCorner = 18;

    private HwndSource? _hwnd;
    private IReadOnlyList<RegionPiece> _shape = [];
    private DateTime _followUntil;
    private bool _following;
    private bool _spread;
    private bool _checking;

    /// <summary>How many frame hooks Aero's windows hold to follow their shape: none at rest.</summary>
    internal static int ShapeHooks { get; private set; }

    /// <summary>The window's shape now, in device pixels, for a test.</summary>
    internal IReadOnlyList<RegionPiece> Shape => _shape;

    /// <summary>Spread over the work area (maximised), or the smaller centred layout.</summary>
    internal bool Spread => WindowState == WindowState.Maximized;

    /// <summary>Called once the handle exists: the shape follows layout from here.</summary>
    private void StartShaping()
    {
        _hwnd = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        LayoutUpdated += OnShapeLayout;
        MouseLeftButtonDown += OnGlassDown;
        Loaded += (_, _) =>
        {
            // Added after WindowChrome's, so it hears WM_NCHITTEST first.
            _hwnd?.AddHook(ShapeHook);
            Reshape();
        };
        StateChanged += (_, _) => Placed();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) Remember();
        };
        Closed += (_, _) =>
        {
            Remember();
            StopFollowing();
            LayoutUpdated -= OnShapeLayout;   // a dispatcher-wide event: held, it would keep the closed window
            _hwnd?.RemoveHook(ShapeHook);
        };
    }

    private void OnShapeLayout(object? sender, EventArgs e) => Reshape();

    /// <summary>The window's shape again, from where the surfaces are now; Windows is told only when it has changed.</summary>
    internal void Reshape()
    {
        if (_hwnd is not { IsDisposed: false, CompositionTarget: { } target } || WindowState == WindowState.Minimized || ActualWidth <= 0) return;
        var pieces = RegionMath.Pieces(Surfaces(), target.TransformToDevice.M11, new Size(ActualWidth, ActualHeight));
        if (pieces.Count == 0) return;   // nothing laid out yet: stay whole rather than vanish
        // The same shape still on the window: nothing to tell. WindowChrome takes a window's region off as it extends the
        // glass frame (0.10.4: the window is drawn with its alpha), so one gone is put back.
        if (pieces.SequenceEqual(_shape) && RegionNative.HasRegion(_hwnd.Handle)) return;
        _shape = pieces;
        RegionNative.Apply(_hwnd.Handle, pieces);
    }

    /// <summary>Follows the surfaces every frame for <paramref name="ms"/> while they move on transforms, which no
    /// layout pass reports, then lets go of the frame clock.</summary>
    internal void FollowShapeFor(double ms)
    {
        if (ms <= 0 || !IsLoaded) return;
        var until = DateTime.UtcNow + TimeSpan.FromMilliseconds(ms);
        if (until > _followUntil) _followUntil = until;
        if (_following) return;
        _following = true;
        ShapeHooks++;
        CompositionTarget.Rendering += OnShapeFrame;
    }

    private void OnShapeFrame(object? sender, EventArgs e)
    {
        Reshape();
        if (DateTime.UtcNow >= _followUntil) StopFollowing();
    }

    private void StopFollowing()
    {
        if (!_following) return;
        _following = false;
        ShapeHooks--;
        CompositionTarget.Rendering -= OnShapeFrame;
    }

    /// <summary>Every glass surface on show in this window, where it is drawn now (transforms and all) and what its
    /// ancestors' clips (a page's scroll viewport) leave of it. A pane inside another is inside its shape already.</summary>
    private List<Surface> Surfaces()
    {
        var surfaces = new List<Surface>();
        foreach (var pane in GlassPanel.Live.ToList())
        {
            if (!pane.IsVisible || pane.ActualWidth <= 0) continue;
            // A menu's or a popup's glass (0.10.9: they are glass pieces too) is in a window of its own, not this one's shape.
            if (!ReferenceEquals(PresentationSource.FromVisual(pane), _hwnd)) continue;
            var clip = new Rect(0, 0, ActualWidth, ActualHeight);
            var nested = false;
            var reached = false;
            for (var node = VisualTreeHelper.GetParent(pane); node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node == this)
                {
                    reached = true;
                    break;
                }
                if (node is GlassPanel)
                {
                    nested = true;
                    break;
                }
                if (node is Visual visual && VisualTreeHelper.GetClip(visual) is { } geometry && visual is UIElement element)
                    clip.Intersect(element.TransformToAncestor(this).TransformBounds(geometry.Bounds));
            }
            if (nested || !reached) continue;
            var bounds = pane.TransformToAncestor(this).TransformBounds(new Rect(pane.RenderSize));
            var scale = bounds.Width / pane.ActualWidth;
            surfaces.Add(new Surface(bounds, pane.CornerRadius.TopLeft * scale, clip.IsEmpty ? Rect.Empty : clip));
        }
        return surfaces;
    }

    // ---------------------------------------------------------------- moving and resizing

    /// <summary>A press on glass that isn't a control: a double click on the top toggles the layout; otherwise Windows
    /// moves the window, snapping and all, and a spread window drawn away comes back to its smaller size.</summary>
    private void OnGlassDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || IsControl(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2)
        {
            if (!OnTop(e.GetPosition(this))) return;
            e.Handled = true;
            ToggleLayout();
            return;
        }
        if (e.ClickCount != 1 || e.ButtonState != MouseButtonState.Pressed) return;
        e.Handled = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was already up.
        }
    }

    /// <summary>Spread to the smaller centred layout, or back.</summary>
    internal void ToggleLayout() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>The top: the caption strip, the top bar and the sidebar beside it.</summary>
    private bool OnTop(Point p)
    {
        var bar = TopBar.IsVisible ? TopBar : (FrameworkElement)Stage;
        var bottom = bar.TransformToAncestor(this).TransformBounds(new Rect(bar.RenderSize)).Top + (TopBar.IsVisible ? bar.ActualHeight : 100);
        return p.Y < bottom;
    }

    /// <summary>Whether <paramref name="node"/> is, or is in, something that takes the pointer for itself.</summary>
    internal static bool IsControl(DependencyObject? node)
    {
        for (; node is not null and not System.Windows.Window; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or TextBoxBase or PasswordBox or Thumb or ScrollBar or RangeBase or Selector or ListBoxItem or ComboBoxItem
                or MenuItem or TreeViewItem or Hyperlink or System.Windows.Controls.ContextMenu) return true;
            if (node is FrameworkElement { Cursor: { } cursor } && cursor == Cursors.Hand) return true;
        }
        return false;
    }

    /// <summary>WM_NCHITTEST: the grip and the shape's outer edges resize a window in its smaller layout; the rest is the
    /// window's own to hit-test.</summary>
    private IntPtr ShapeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is RegionNative.WM_WINDOWPOSCHANGED or RegionNative.WM_DWMCOMPOSITIONCHANGED && !_checking)
        {
            // WindowChrome hears these after this hook and may take the shape off: see that it is still there after.
            _checking = true;
            Dispatcher.BeginInvoke(() =>
            {
                _checking = false;
                Reshape();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        if (msg != RegionNative.WM_NCHITTEST || WindowState != WindowState.Normal || !IsLoaded) return IntPtr.Zero;
        var at = lParam.ToInt64();
        var p = PointFromScreen(new Point((short)(at & 0xFFFF), (short)((at >> 16) & 0xFFFF)));
        var code = HitResize(p);
        if (code == 0) return IntPtr.Zero;
        handled = true;
        return new IntPtr(code);
    }

    /// <summary>The resize a point in the window's units asks for, or 0: the grip, then the shape's outer edges where
    /// no control is under the pointer.</summary>
    internal int HitResize(Point p)
    {
        if (WindowState != WindowState.Normal) return 0;
        if (Grip.IsVisible && Grip.TransformToAncestor(this).TransformBounds(new Rect(Grip.RenderSize)).Contains(p)) return RegionMath.HtBottomRight;
        var frame = Stage.TransformToAncestor(this).TransformBounds(new Rect(Stage.RenderSize));
        var code = RegionMath.Edge(p, frame, ResizeBand, ResizeCorner);
        if (code == 0 || IsControl(InputHitTest(p) as DependencyObject)) return 0;
        return code;
    }

    // ---------------------------------------------------------------- the layout, remembered

    /// <summary>Before the first show, when nothing placed the window by hand (a look switch carries its own bounds):
    /// the layout last left, spread over the work area the first time, with the smaller layout's bounds ready.</summary>
    private void OpenLayout(Bounds fitted, Func<System.Drawing.Rectangle, Bounds> toUnits)
    {
        var workAreas = System.Windows.Forms.Screen.AllScreens.Select(screen => toUnits(screen.WorkingArea)).ToList();
        var (spread, normal) = AeroLayout.Open(_shell.Settings.AeroPlacement, workAreas, fitted, new Extent(MinWidth, MinHeight));
        if (normal != fitted) WindowStartupLocation = WindowStartupLocation.Manual;
        Left = normal.Left;
        Top = normal.Top;
        Width = normal.Width;
        Height = normal.Height;
        if (spread) WindowState = WindowState.Maximized;
        _spread = spread;
    }

    /// <summary>The layout changed: the maximise button, the grip and the shape follow, and it is remembered.</summary>
    private void Placed()
    {
        if (WindowState == WindowState.Minimized) return;
        _spread = WindowState == WindowState.Maximized;
        Remember();
    }

    /// <summary>Saves the layout and the smaller layout's bounds, once the window has shown.</summary>
    private void Remember()
    {
        var restore = RestoreBounds;
        if (restore.IsEmpty || restore.Width <= 0 || !_shown) return;
        _shell.Settings.PlaceAero(new AeroPlacement { Spread = _spread, Left = restore.Left, Top = restore.Top, Width = restore.Width, Height = restore.Height });
    }
}
