using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;

namespace PowerLedger.App.Aero;

/// <summary>How lit the grab bar is: at rest, with the pointer near, or with the pointer on it (or the keyboard's focus).</summary>
public enum GrabGlow
{
    Idle,
    Near,
    Over,
}

/// <summary>
/// The grab bar (visionOS's, at the top middle of the Aero stage): a small glass capsule that moves the whole window. It
/// rests at a quarter so it can be found, lights to half with the pointer near and fully, a touch brighter, with the pointer
/// on it; pressed and held it moves the window through Windows' own move loop (DragMove), so snapping works; a double
/// click toggles the spread and centred layouts; focused, the arrow keys move the window 10 px at a time. The visible bar
/// is 56 by 6; the piece of the window's shape it stands in, and takes the pointer in, is 120 by 22. It is a
/// <see cref="GlassPanel"/> so it is a piece of the free-form window's shape; its style (A.GrabBar) draws only the bar, as
/// the demo's .gbtn. Nothing here runs at rest: the window hands it the pointer from its own mouse moves, and each change
/// of glow starts one short fade that ends.
/// </summary>
public sealed class GrabBar : GlassPanel
{
    public const double BarWidth = 56;
    public const double BarHeight = 6;
    public const double HitWidth = 120;
    public const double HitHeight = 22;

    public const double IdleOpacity = .25;
    public const double NearOpacity = .5;
    public const double OverOpacity = 1;

    /// <summary>How near the bar the pointer lights it to half, in DIP from the bar's edge.</summary>
    public const double NearWithin = 80;

    /// <summary>How far an arrow key moves the window.</summary>
    public const double Step = 10;

    private FrameworkElement? _bar;
    private FrameworkElement? _bright;
    private Point? _pointer;

    public GrabBar()
    {
        Focusable = true;   // GlassPanel's are not; this one is a control
        AutomationProperties.SetName(this, "Move window");
        IsKeyboardFocusedChanged += (_, _) => Show(animate: true);
    }

    /// <summary>A double click on the bar: the window toggles between spread and the smaller centred layout.</summary>
    public event EventHandler? LayoutToggled;

    /// <summary>How lit the bar is now.</summary>
    public GrabGlow Glow { get; private set; }

    /// <summary>How a press moves the window: Windows' own move loop. A test stands in for it.</summary>
    internal Action<Window> StartMove { get; set; } = window => window.DragMove();

    /// <summary>The opacity the bar rests at for <paramref name="glow"/>.</summary>
    public static double OpacityOf(GrabGlow glow) => glow switch
    {
        GrabGlow.Over => OverOpacity,
        GrabGlow.Near => NearOpacity,
        _ => IdleOpacity,
    };

    /// <summary>The glow for a pointer at <paramref name="pointer"/> in the bar's own units (null: not over the window):
    /// on it anywhere in its <paramref name="hit"/> area; near within <see cref="NearWithin"/> of the visible bar,
    /// centred in it; idle otherwise.</summary>
    public static GrabGlow GlowAt(Size hit, Point? pointer)
    {
        if (pointer is not { } p) return GrabGlow.Idle;
        if (new Rect(hit).Contains(p)) return GrabGlow.Over;
        var bar = new Rect((hit.Width - BarWidth) / 2, (hit.Height - BarHeight) / 2, BarWidth, BarHeight);
        var dx = Math.Max(0, Math.Max(bar.Left - p.X, p.X - bar.Right));
        var dy = Math.Max(0, Math.Max(bar.Top - p.Y, p.Y - bar.Bottom));
        return dx * dx + dy * dy <= NearWithin * NearWithin ? GrabGlow.Near : GrabGlow.Idle;
    }

    /// <summary>The pointer, in the bar's own units, from the window's mouse moves; null when it has left the window.</summary>
    public void Track(Point? pointer)
    {
        _pointer = pointer;
        Show(animate: true);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _bar = GetTemplateChild("PART_Bar") as FrameworkElement;
        _bright = GetTemplateChild("PART_Bright") as FrameworkElement;
        Show(animate: false, force: true);
    }

    /// <summary>Eases the bar to its glow's opacity (instant under reduced motion); only when the glow changes, so a
    /// stream of mouse moves starts nothing.</summary>
    private void Show(bool animate, bool force = false)
    {
        var glow = IsKeyboardFocused ? GrabGlow.Over : GlowAt(new Size(ActualWidth > 0 ? ActualWidth : HitWidth, ActualHeight > 0 ? ActualHeight : HitHeight), _pointer);
        if (glow == Glow && !force) return;
        Glow = glow;
        var ms = animate ? AeroMotion.Grab : 0;
        if (_bar is not null) AeroMotion.Move(_bar, OpacityProperty, OpacityOf(glow), ms, AeroMotion.Ease);
        if (_bright is not null) AeroMotion.Move(_bright, OpacityProperty, glow == GrabGlow.Over ? 1 : 0, ms, AeroMotion.Ease);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        e.Handled = true;   // the window's own glass drag stays out of it
        if (e.ClickCount == 2)
        {
            LayoutToggled?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (Window.GetWindow(this) is not { } window) return;
        try
        {
            StartMove(window);
        }
        catch (InvalidOperationException)
        {
            // The button was already up.
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None || Window.GetWindow(this) is not { } window) return;
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-Step, 0d),
            Key.Right => (Step, 0d),
            Key.Up => (0d, -Step),
            Key.Down => (0d, Step),
            _ => (0d, 0d),
        };
        if (dx == 0 && dy == 0) return;
        e.Handled = true;
        if (window.WindowState != WindowState.Normal) return;   // spread over the work area: nowhere to move
        window.Left += dx;
        window.Top += dy;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new GrabBarPeer(this);

    private sealed class GrabBarPeer(GrabBar owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Thumb;

        protected override string GetClassNameCore() => nameof(GrabBar);

        protected override bool IsControlElementCore() => true;

        protected override bool IsKeyboardFocusableCore() => true;
    }
}
