using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace PowerLedger.App.Aero;

/// <summary>How lit a scroll bar is: hidden at rest, half with the pointer near or the content scrolling, whole on it or
/// while its thumb is dragged.</summary>
public enum ScrollShine
{
    Hidden,
    Near,
    Over,
}

/// <summary>
/// Aero's side scroll bar (0.10.8): a thin glass capsule over the content, inset from the pane's edge, hidden at rest.
/// It lights to half with the pointer within <see cref="NearWithin"/> of the bar or while the content scrolls (fading
/// out <see cref="Linger"/> after the last scroll), and whole on it or while its thumb is dragged, where the capsule
/// widens from <see cref="Thin"/> to <see cref="Wide"/>. Set on every ScrollViewer by Aero's implicit style, whose
/// template overlays the bars (no layout shift) inside small glass hosts, so a shown bar is a piece of the free-form
/// window's shape; a hidden host is not. Nothing runs at rest: the viewer's own mouse moves and scrolls drive it, each
/// change of shine starts one short fade that ends, and the linger timer stops once it fires.
/// </summary>
public sealed class ScrollGlow
{
    /// <summary>The strip the bar takes the pointer in, across it.</summary>
    public const double Strip = 16;
    public const double Thin = 6;
    public const double Wide = 10;

    /// <summary>How far the capsule sits in from the pane's edge.</summary>
    public const double Inset = 4;

    public const double HiddenOpacity = 0;
    public const double NearOpacity = .55;
    public const double OverOpacity = 1;

    /// <summary>How near the bar the pointer lights it to half, in DIP from the bar's edge.</summary>
    public const double NearWithin = 40;

    /// <summary>How long the bar stays lit after the last scroll.</summary>
    public const double Linger = 1000;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(ScrollGlow),
        new PropertyMetadata(false, OnEnabled));

    private static readonly DependencyProperty GlowProperty = DependencyProperty.RegisterAttached("Glow", typeof(ScrollGlow), typeof(ScrollGlow));

    private readonly ScrollViewer _viewer;
    private readonly Side _vertical;
    private readonly Side _horizontal;
    private Point? _pointer;
    private bool _scrolling;
    private DispatcherTimer? _linger;

    private ScrollGlow(ScrollViewer viewer)
    {
        _viewer = viewer;
        _vertical = new Side(this, "PART_VerticalHost", "PART_VerticalScrollBar", vertical: true);
        _horizontal = new Side(this, "PART_HorizontalHost", "PART_HorizontalScrollBar", vertical: false);
        viewer.PreviewMouseMove += (_, e) => Track(e.GetPosition(viewer));
        viewer.MouseLeave += (_, _) => Track(null);
        viewer.ScrollChanged += OnScrolled;
        viewer.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => Update(animate: true)), true);
        viewer.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => Update(animate: true)), true);
        viewer.Unloaded += (_, _) =>
        {
            _linger?.Stop();
            _pointer = null;
            _scrolling = false;
            Update(animate: false);
        };
    }

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    /// <summary>The glow on <paramref name="viewer"/>, when Aero's style gave it one.</summary>
    public static ScrollGlow? Of(ScrollViewer viewer) => (ScrollGlow?)viewer.GetValue(GlowProperty);

    /// <summary>How lit the vertical bar is now.</summary>
    public ScrollShine Vertical => _vertical.Shine;

    /// <summary>How lit the horizontal bar is now.</summary>
    public ScrollShine Horizontal => _horizontal.Shine;

    /// <summary>Whether the after-scroll timer is running: never at rest.</summary>
    public bool Lingering => _linger?.IsEnabled == true;

    /// <summary>The opacity the bar rests at for <paramref name="shine"/>.</summary>
    public static double OpacityOf(ScrollShine shine) => shine switch
    {
        ScrollShine.Over => OverOpacity,
        ScrollShine.Near => NearOpacity,
        _ => HiddenOpacity,
    };

    /// <summary>The shine for a bar taking the pointer in <paramref name="bar"/> (the viewer's units): whole while its
    /// thumb is dragged or with the pointer on it; half with the pointer within <see cref="NearWithin"/> of it or while
    /// the content scrolls; hidden otherwise.</summary>
    public static ScrollShine ShineAt(Rect bar, Point? pointer, bool scrolling, bool dragging)
    {
        if (dragging) return ScrollShine.Over;
        if (pointer is { } p)
        {
            if (bar.Contains(p)) return ScrollShine.Over;
            var dx = Math.Max(0, Math.Max(bar.Left - p.X, p.X - bar.Right));
            var dy = Math.Max(0, Math.Max(bar.Top - p.Y, p.Y - bar.Bottom));
            if (dx * dx + dy * dy <= NearWithin * NearWithin) return ScrollShine.Near;
        }
        return scrolling ? ScrollShine.Near : ScrollShine.Hidden;
    }

    /// <summary>The pointer, in the viewer's units, from its mouse moves; null when it has left the viewer.</summary>
    public void Track(Point? pointer)
    {
        _pointer = pointer;
        Update(animate: true);
    }

    /// <summary>The content scrolled (wheel, keys or the bar): lit to half until <see cref="Linger"/> after the last.</summary>
    public void Scrolled()
    {
        _scrolling = true;
        if (_linger is null)
        {
            _linger = new DispatcherTimer(DispatcherPriority.Background, _viewer.Dispatcher) { Interval = TimeSpan.FromMilliseconds(Linger) };
            _linger.Tick += (_, _) =>
            {
                _linger.Stop();
                _scrolling = false;
                Update(animate: true);
            };
        }
        _linger.Stop();
        _linger.Start();
        Update(animate: true);
    }

    private static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer viewer && (bool)e.NewValue && Of(viewer) is null) viewer.SetValue(GlowProperty, new ScrollGlow(viewer));
    }

    private void OnScrolled(object sender, ScrollChangedEventArgs e)
    {
        // A text box's own viewer inside this one reports here too: only this viewer's offsets count.
        if (e.OriginalSource != _viewer || (e.VerticalChange == 0 && e.HorizontalChange == 0)) return;
        Scrolled();
    }

    private void Update(bool animate)
    {
        _vertical.Update(animate);
        _horizontal.Update(animate);
    }

    /// <summary>The free-form window's shape again, once a host has shown or hidden.</summary>
    private void Reshape()
    {
        if (Window.GetWindow(_viewer) is AeroWindow window) window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(window.Reshape));
    }

    private sealed class Side(ScrollGlow owner, string hostName, string barName, bool vertical)
    {
        private FrameworkElement? _host;
        private ScrollBar? _bar;

        public ScrollShine Shine { get; private set; }

        public void Update(bool animate)
        {
            var viewer = owner._viewer;
            if (_host is null || _bar is null || _host.TemplatedParent != viewer)
            {
                _host = viewer.Template?.FindName(hostName, viewer) as FrameworkElement;
                _bar = viewer.Template?.FindName(barName, viewer) as ScrollBar;
                if (_host is null || _bar is null) return;
            }
            var shine = ScrollShine.Hidden;
            if (_bar.Visibility == Visibility.Visible && _host.ActualWidth > 0 && _host.ActualHeight > 0 && viewer.IsLoaded)
            {
                var bounds = new Rect(_host.TranslatePoint(new Point(0, 0), viewer), _host.RenderSize);
                shine = ShineAt(bounds, owner._pointer, owner._scrolling, _bar.Track?.Thumb?.IsDragging == true);
            }
            if (shine == Shine) return;
            Shine = shine;
            var ms = animate ? AeroMotion.ScrollFade : 0;
            if (shine != ScrollShine.Hidden && _host.Visibility != Visibility.Visible)
            {
                _host.Visibility = Visibility.Visible;
                owner.Reshape();
            }
            var host = _host;
            AeroMotion.Move(_bar, UIElement.OpacityProperty, OpacityOf(shine), ms, AeroMotion.Ease, done: shine != ScrollShine.Hidden ? null : () =>
            {
                if (Shine != ScrollShine.Hidden || host.Visibility == Visibility.Hidden) return;
                host.Visibility = Visibility.Hidden;
                owner.Reshape();
            });
            if (_bar.Track?.Thumb is { } thumb)
            {
                thumb.ApplyTemplate();
                if (thumb.Template?.FindName("PART_Capsule", thumb) is FrameworkElement capsule)
                    AeroMotion.Move(capsule, vertical ? FrameworkElement.WidthProperty : FrameworkElement.HeightProperty, shine == ScrollShine.Over ? Wide : Thin, ms, AeroMotion.Ease);
                if (thumb.Template?.FindName("PART_Bright", thumb) is FrameworkElement bright)
                    AeroMotion.Move(bright, UIElement.OpacityProperty, shine == ScrollShine.Over ? 1 : 0, ms, AeroMotion.Ease);
            }
        }
    }
}
