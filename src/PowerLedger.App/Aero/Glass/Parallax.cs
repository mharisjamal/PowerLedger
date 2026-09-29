using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PowerLedger.App.Aero;

/// <summary>
/// The demo's tilt and parallax (Aero look design §3, Plan S G5): the wallpaper drifts a little against the pointer (22
/// by 14 px at the edges) and the panes tilt toward it. WPF has no CSS 3D, so the tilt is a 2D skew. The HTML eased 6 %
/// of the way each frame; here a timer steps at 30 Hz by the same amount per unit of time, runs only while the pointer
/// has somewhere to go, and stops once settled, so a still pointer costs nothing. Off under reduced motion, when the
/// Glass settings turn it off, and whenever the backdrop isn't the wallpaper (see-through glass has nothing of its own to
/// shift). Since 0.10.1 Aero's own window keeps it off (Backdrop's onScreen): its panes float over the real desktop,
/// which doesn't drift, and its window region can't follow a skewed pane.
/// </summary>
internal sealed class Parallax : IDisposable
{
    public const double Hz = 30;
    public const double ShiftX = 22;
    public const double ShiftY = 14;

    /// <summary>The scene is drawn a little larger, so its edges never show as it drifts.</summary>
    public const double SceneScale = 1.06;

    /// <summary>The tilt at the window's edge, in degrees of skew: the HTML's rotateY(2.4deg) and rotateX(1.8deg) as
    /// a 2D skew reads at about a third of the angle.</summary>
    public const double TiltY = 0.8;
    public const double TiltX = 0.6;

    /// <summary>The HTML's 6 % a frame at 60 Hz, as a step at <see cref="Hz"/>.</summary>
    public static readonly double Step = 1 - Math.Pow(1 - 0.06, 60 / Hz);

    private readonly Window _window;
    private readonly Border _scene;
    private readonly FrameworkElement? _tilt;
    private readonly Action _moved;
    private readonly DispatcherTimer _timer;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _shift = new();
    private readonly SkewTransform _skew = new();
    private double _tx, _ty, _cx, _cy;
    private bool _enabled;

    /// <param name="tilt">An element whose RenderTransform the parallax may own (a wrapper round the panes), or null.</param>
    /// <param name="moved">Called after each step, to line the frost up with the scene's new place.</param>
    public Parallax(Window window, Border scene, FrameworkElement? tilt, Action moved)
    {
        _window = window;
        _scene = scene;
        _tilt = tilt;
        _moved = moved;
        _timer = new DispatcherTimer(DispatcherPriority.Render, window.Dispatcher) { Interval = TimeSpan.FromSeconds(1 / Hz) };
        _timer.Tick += (_, _) => Tick();
        scene.RenderTransformOrigin = new Point(.5, .5);
        scene.RenderTransform = new TransformGroup { Children = { _scale, _shift } };
        if (tilt != null)
        {
            tilt.RenderTransformOrigin = new Point(.5, .5);
            tilt.RenderTransform = _skew;
        }
        window.MouseMove += OnMove;
        window.MouseLeave += OnLeave;
        AeroMotion.Changed += OnMotionChanged;
    }

    /// <summary>On while the backdrop is the wallpaper and the Glass settings want it; reduced motion overrides.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Settle();
        }
    }

    /// <summary>Whether the timer is stepping: only while the scene is on its way somewhere.</summary>
    internal bool Running => _timer.IsEnabled;

    internal (double X, double Y) Offset => (_shift.X, _shift.Y);

    internal (double X, double Y) Skew => (_skew.AngleX, _skew.AngleY);

    /// <summary>One step of <paramref name="value"/> toward <paramref name="target"/>; there once within a hundredth of a
    /// pixel at the widest shift.</summary>
    internal static double Approach(double value, double target)
    {
        var next = value + (target - value) * Step;
        return Math.Abs(target - next) * ShiftX < .01 ? target : next;
    }

    /// <summary>Points the parallax at a place in the window, as the pointer would: -0.5 to 0.5 each way.</summary>
    internal void PointAt(double x, double y)
    {
        _tx = Math.Clamp(x, -.5, .5);
        _ty = Math.Clamp(y, -.5, .5);
        Start();
    }

    /// <summary>Runs a step now, for a test.</summary>
    internal void Tick()
    {
        var on = Active;
        _cx = Approach(_cx, on ? _tx : 0);
        _cy = Approach(_cy, on ? _ty : 0);
        Show();
        if (_cx == (on ? _tx : 0) && _cy == (on ? _ty : 0)) _timer.Stop();
    }

    public void Dispose()
    {
        _timer.Stop();
        _window.MouseMove -= OnMove;
        _window.MouseLeave -= OnLeave;
        AeroMotion.Changed -= OnMotionChanged;
    }

    private bool Active => _enabled && !AeroMotion.Reduced;

    private void Show()
    {
        var x = Math.Round(-_cx * ShiftX, 2);
        var y = Math.Round(-_cy * ShiftY, 2);
        if (_shift.X != x) _shift.X = x;
        if (_shift.Y != y) _shift.Y = y;
        var scale = Active || _cx != 0 || _cy != 0 ? SceneScale : 1;
        if (_scale.ScaleX != scale) _scale.ScaleX = _scale.ScaleY = scale;
        if (_tilt != null)
        {
            var ay = Math.Round(_cx * TiltY, 3);
            var ax = Math.Round(-_cy * TiltX, 3);
            if (_skew.AngleY != ay) _skew.AngleY = ay;
            if (_skew.AngleX != ax) _skew.AngleX = ax;
        }
        _moved();
    }

    private void Start()
    {
        if (!Active || _timer.IsEnabled) return;
        _timer.Start();
    }

    /// <summary>Off, or reduced: straight back to rest without travel, and the timer stopped.</summary>
    private void Settle()
    {
        if (Active)
        {
            Show();
            return;
        }
        _timer.Stop();
        _cx = _cy = 0;
        Show();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!Active || _window.ActualWidth <= 0) return;
        var p = e.GetPosition(_window);
        PointAt(p.X / _window.ActualWidth - .5, p.Y / Math.Max(1, _window.ActualHeight) - .5);
    }

    private void OnLeave(object sender, MouseEventArgs e)
    {
        if (!Active) return;
        PointAt(0, 0);
    }

    private void OnMotionChanged() => _window.Dispatcher.InvokeAsync(Settle);
}
