using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PowerLedger.App.Aero;

/// <summary>
/// A CSS cubic-bezier() timing curve, so Aero's springs and glides match the approved HTML exactly (Aero look design
/// §3). Frozen instances are shared by every animation; x(u) = t is solved by Newton, with bisection as the fallback.
/// </summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    public CubicBezierEase() => EasingMode = EasingMode.EaseIn;

    public CubicBezierEase(double x1, double y1, double x2, double y2)
        : this()
    {
        X1 = x1;
        Y1 = y1;
        X2 = x2;
        Y2 = y2;
    }

    public double X1 { get; set; }

    public double Y1 { get; set; }

    public double X2 { get; set; }

    public double Y2 { get; set; }

    /// <summary>The curve's progress at <paramref name="t"/>, 0 to 1.</summary>
    public double At(double t) => EaseInCore(t);

    protected override double EaseInCore(double normalizedTime)
    {
        var t = normalizedTime;
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        var u = t;
        for (var i = 0; i < 8; i++)
        {
            var x = Bezier(u, X1, X2) - t;
            if (Math.Abs(x) < 1e-6) return Bezier(u, Y1, Y2);
            var slope = Slope(u, X1, X2);
            if (Math.Abs(slope) < 1e-6) break;
            u -= x / slope;
        }
        double low = 0, high = 1;
        u = t;
        for (var i = 0; i < 30; i++)
        {
            var x = Bezier(u, X1, X2);
            if (Math.Abs(x - t) < 1e-6) break;
            if (x < t) low = u;
            else high = u;
            u = (low + high) / 2;
        }
        return Bezier(u, Y1, Y2);
    }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase(X1, Y1, X2, Y2);

    private static double Bezier(double u, double p1, double p2)
    {
        var v = 1 - u;
        return 3 * v * v * u * p1 + 3 * v * u * u * p2 + u * u * u;
    }

    private static double Slope(double u, double p1, double p2)
    {
        var v = 1 - u;
        return 3 * v * v * p1 + 6 * v * u * (p2 - p1) + 3 * u * u * (1 - p2);
    }
}

/// <summary>
/// Aero's motion tokens, from the approved HTML (Aero look design §3): a spring for things that move into place, a glide
/// for things that settle, CSS's ease for hovers, and every duration by name. Under reduced motion
/// (<c>GlassSettings.ReduceMotion ?? !SystemParameters.ClientAreaAnimation</c>) a movement takes no time and a fade keeps
/// at most <see cref="ReducedFade"/>, so nothing is lost but the travel. Nothing here runs per frame: each call starts
/// one WPF animation, which the compositor clocks and which ends.
/// </summary>
public static class AeroMotion
{
    public static readonly CubicBezierEase Spring = Frozen(new CubicBezierEase(.34, 1.36, .5, 1));
    public static readonly CubicBezierEase Glide = Frozen(new CubicBezierEase(.22, 1, .36, 1));

    /// <summary>CSS's own <c>ease</c>, which the HTML's hover transitions take.</summary>
    public static readonly CubicBezierEase Ease = Frozen(new CubicBezierEase(.25, .1, .25, 1));

    /// <summary>The demo's own count-up curve, 1 - (1 - t)^4 (its <c>ease</c> function), for the intro's figures.</summary>
    public static readonly QuarticEase Quart = Frozen(new QuarticEase { EasingMode = EasingMode.EaseOut });

    // Durations, in milliseconds.
    public const double Hover = 250;
    public const double Press = 350;
    public const double NavPill = 550;
    public const double SegPill = 500;
    public const double Switch = 450;
    public const double SwitchFill = 300;
    public const double Checked = 350;
    public const double PaneIn = 1000;
    public const double ContentIn = 700;
    public const double ContentDelay = 700;
    public const double Stagger = 75;
    public const double ChartsStart = 1050;
    public const double CountUp = 1100;
    public const double BarGrow = 1200;
    public const double LineDraw = 1400;
    public const double DailyWipe = 1300;
    public const double PieRise = 900;
    public const double PieStagger = 140;
    public const double Menu = 380;
    public const double Modal = 550;
    public const double Scrim = 350;
    public const double Toast = 500;
    public const double ToastHold = 2600;
    public const double Camera = 1000;
    public const double TourStep = 2300;
    public const double FocusFade = 700;
    public const double Sheen = 500;
    public const double TableFade = 220;
    public const double ReducedFade = 200;
    public const double Pulse = 1800;
    public const double NumberRoll = 600;

    // Distances and scales.
    public const double PaneRise = 22;
    public const double PaneScale = .95;
    public const double ContentRise = 8;
    public const double PressScale = .95;
    public const double MenuScale = .9;
    public const double MenuDrop = -6;

    private static bool? _override;

    /// <summary>Raised when <see cref="Reduced"/> may have changed: the override was set, or Windows' setting changed.</summary>
    public static event Action? Changed;

    /// <summary>True while motion is reduced: the user's Glass setting when set, otherwise Windows'.</summary>
    public static bool Reduced => _override ?? !SystemParameters.ClientAreaAnimation;

    /// <summary>The Glass settings' Reduce motion: true or false wins over Windows; null follows it again.</summary>
    public static bool? Override => _override;

    public static void SetOverride(bool? reduced)
    {
        if (_override == reduced) return;
        _override = reduced;
        Changed?.Invoke();
    }

    /// <summary>For the window that hears Windows' setting change (WM_SETTINGCHANGE): tells every listener to look again.</summary>
    public static void WindowsChanged() => Changed?.Invoke();

    /// <summary>Stands in for the setting until the scope ends, for a test.</summary>
    internal static IDisposable Force(bool? reduced)
    {
        var before = _override;
        SetOverride(reduced);
        return new Scope(() => SetOverride(before));
    }

    /// <summary>The length of a movement: <paramref name="ms"/>, or none under reduced motion.</summary>
    public static double MoveMs(double ms) => Reduced ? 0 : ms;

    /// <summary>The length of an opacity or colour fade: <paramref name="ms"/>, or at most <see cref="ReducedFade"/> under reduced motion.</summary>
    public static double FadeMs(double ms) => Reduced ? Math.Min(ms, ReducedFade) : ms;

    public static double EaseOutQuart(double t) => 1 - Math.Pow(1 - t, 4);

    /// <summary>A movement: runs <paramref name="ms"/> long, or jumps under reduced motion.</summary>
    public static void Move(IAnimatable target, DependencyProperty property, double to, double ms, IEasingFunction? ease = null,
        double delayMs = 0, Action? done = null, double? from = null)
        => Run(target, property, to, MoveMs(ms), ease, Reduced ? 0 : delayMs, done, from);

    /// <summary>An opacity fade: keeps a short fade under reduced motion where a movement takes none.</summary>
    public static void Fade(IAnimatable target, DependencyProperty property, double to, double ms, IEasingFunction? ease = null,
        double delayMs = 0, Action? done = null, double? from = null)
        => Run(target, property, to, FadeMs(ms), ease, Reduced ? 0 : delayMs, done, from);

    private static void Run(IAnimatable target, DependencyProperty property, double to, double ms, IEasingFunction? ease,
        double delayMs, Action? done, double? from)
    {
        if (from is { } start)
        {
            // Hold the start value through any delay: before its clock begins an animation shows the base value, and a
            // running one's value would otherwise be snapshotted and held instead.
            target.BeginAnimation(property, null);
            if (target is DependencyObject d && !d.IsSealed) d.SetValue(property, start);
        }
        if (ms <= 0 && delayMs <= 0)
        {
            // Nothing to animate: set the value and leave no clock running.
            target.BeginAnimation(property, null);
            if (target is DependencyObject d && !d.IsSealed) d.SetValue(property, to);
            done?.Invoke();
            return;
        }
        var animation = new DoubleAnimation
        {
            To = to,
            From = from,
            Duration = TimeSpan.FromMilliseconds(Math.Max(0, ms)),
            BeginTime = TimeSpan.FromMilliseconds(Math.Max(0, delayMs)),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (done != null) animation.Completed += (_, _) => done();
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private sealed class Scope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}

/// <summary>
/// A style's animation whose length is asked of <see cref="AeroMotion"/> each time it starts, not once when the style
/// loads: a storyboard in a style is frozen with it, so a length fixed then would ignore reduced motion changed later.
/// Leave Duration unset and give <see cref="Ms"/>; <see cref="Fade"/> for an opacity or colour fade, which keeps a short
/// fade under reduced motion where a movement takes none.
/// </summary>
public sealed class AeroAnimation : DoubleAnimation
{
    public static readonly DependencyProperty MsProperty = DependencyProperty.Register(
        nameof(Ms), typeof(double), typeof(AeroAnimation), new PropertyMetadata(AeroMotion.Hover));

    public static readonly DependencyProperty FadeProperty = DependencyProperty.Register(
        nameof(Fade), typeof(bool), typeof(AeroAnimation), new PropertyMetadata(true));

    public double Ms { get => (double)GetValue(MsProperty); set => SetValue(MsProperty, value); }

    public bool Fade { get => (bool)GetValue(FadeProperty); set => SetValue(FadeProperty, value); }

    protected override Duration GetNaturalDurationCore(Clock clock)
        => new(TimeSpan.FromMilliseconds(Fade ? AeroMotion.FadeMs(Ms) : AeroMotion.MoveMs(Ms)));

    protected override Freezable CreateInstanceCore() => new AeroAnimation();
}

/// <summary>
/// Press feedback for any button (the HTML's <c>:active{transform:scale(.95)}</c>): a slight compression on press and a
/// spring back on release, by mouse or the space bar. Under reduced motion the scale jumps, which reads as a tap.
/// </summary>
public static class Press
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Press), new PropertyMetadata(false, OnChanged));

    private static readonly DependencyProperty ScaleProperty = DependencyProperty.RegisterAttached(
        "Scale", typeof(ScaleTransform), typeof(Press), new PropertyMetadata(null));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    /// <summary>The scale a pressed button is drawn at, for a test: null when press feedback is off.</summary>
    internal static ScaleTransform? ScaleOf(DependencyObject element) => (ScaleTransform?)element.GetValue(ScaleProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        if (e.NewValue is true)
        {
            if (ScaleOf(button) is not null) return;
            var scale = new ScaleTransform(1, 1);
            button.SetValue(ScaleProperty, scale);
            button.RenderTransformOrigin = new Point(.5, .5);
            button.RenderTransform = scale;
            button.PreviewMouseLeftButtonDown += Down;
            button.PreviewMouseLeftButtonUp += Up;
            button.MouseLeave += Up;
            button.PreviewKeyDown += KeyDown;
            button.PreviewKeyUp += KeyUp;
        }
        else if (ScaleOf(button) is { } scale)
        {
            button.PreviewMouseLeftButtonDown -= Down;
            button.PreviewMouseLeftButtonUp -= Up;
            button.MouseLeave -= Up;
            button.PreviewKeyDown -= KeyDown;
            button.PreviewKeyUp -= KeyUp;
            if (ReferenceEquals(button.RenderTransform, scale)) button.RenderTransform = Transform.Identity;
            button.ClearValue(ScaleProperty);
        }
    }

    private static void Down(object sender, MouseButtonEventArgs e) => To(sender, AeroMotion.PressScale);

    private static void Up(object sender, MouseEventArgs e) => To(sender, 1);

    private static void KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && !e.IsRepeat) To(sender, AeroMotion.PressScale);
    }

    private static void KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) To(sender, 1);
    }

    private static void To(object sender, double value)
    {
        if (sender is not DependencyObject d || ScaleOf(d) is not { } scale) return;
        if (scale.ScaleX == value && !scale.HasAnimatedProperties) return;
        AeroMotion.Move(scale, ScaleTransform.ScaleXProperty, value, AeroMotion.Press, AeroMotion.Spring);
        AeroMotion.Move(scale, ScaleTransform.ScaleYProperty, value, AeroMotion.Press, AeroMotion.Spring);
    }
}
