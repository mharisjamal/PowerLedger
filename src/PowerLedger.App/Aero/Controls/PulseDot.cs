using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PowerLedger.App.Aero;

/// <summary>
/// The accent dot with a ring that breathes out every 1.8 s (the HTML's <c>.pulse</c>): "the service is measuring". The
/// ring runs at 20 frames a second from a cached bitmap, and only while someone is using the window: it is shown, not
/// minimised, in the foreground, and has had the pointer or the keyboard in the last <see cref="AwakeFor"/> (Aero look
/// design §3: animation runs only on interaction or the live tick). Left alone, in the background, hidden to the tray,
/// under reduced motion, or with <see cref="Breathes"/> off (the overlay's) the dot is still, so an idle App asks for no
/// frames.
/// </summary>
public sealed class PulseDot : Grid
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double),
        typeof(PulseDot), new PropertyMetadata(7.0, (d, _) => ((PulseDot)d).Build()));

    public static readonly DependencyProperty BreathesProperty = DependencyProperty.Register(nameof(Breathes), typeof(bool),
        typeof(PulseDot), new PropertyMetadata(true, (d, _) => ((PulseDot)d).Build()));

    /// <summary>The ring's frame rate: a third of the screen's, which a soft ring reads the same at; measured on screen,
    /// 30 cost about a point of a core more than 20 with nothing else moving.</summary>
    public const int FrameRate = 20;

    /// <summary>How long the ring keeps breathing after the last pointer move, key or activation.</summary>
    public static readonly TimeSpan AwakeFor = TimeSpan.FromSeconds(15);

    private readonly Ellipse _ring = new() { IsHitTestVisible = false };
    private readonly Ellipse _dot = new() { IsHitTestVisible = false };
    private readonly ScaleTransform _scale = new();
    private Window? _window;
    private DispatcherTimer? _sleep;
    private bool _awake;
    private DateTime _woke;
    private Point? _pointer;
    private AnimationClock? _grow, _fade;

    public PulseDot()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Children.Add(_ring);
        Children.Add(_dot);
        _ring.RenderTransform = _scale;
        // The ring is drawn once and only scaled and faded after: the compositor does the breathing.
        _ring.CacheMode = new BitmapCache();
        _ring.RenderTransformOrigin = new Point(.5, .5);
        _ring.SetResourceReference(Shape.FillProperty, "A.B.Accent");
        _dot.SetResourceReference(Shape.FillProperty, "A.B.Accent");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => Build();
    }

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <summary>False for a still dot.</summary>
    public bool Breathes { get => (bool)GetValue(BreathesProperty); set => SetValue(BreathesProperty, value); }

    /// <summary>Whether the ring is breathing now, for a test.</summary>
    internal bool Breathing => _ring.HasAnimatedProperties;

    /// <summary>Whether someone has used the window lately, for a test.</summary>
    internal bool Awake => _awake;

    /// <summary>Whether the ring should breathe: someone is using the window and motion is full.</summary>
    internal static bool ShouldBreathe(bool breathes, bool reduced, bool visible, bool windowActive, bool minimised, bool awake)
        => breathes && !reduced && visible && windowActive && !minimised && awake;

    /// <summary>The window was used: the ring breathes for <see cref="AwakeFor"/> from now.</summary>
    internal void Wake()
    {
        _woke = DateTime.UtcNow;
        if (_awake) return;
        _awake = true;
        _sleep ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _sleep.Tick -= OnSleep;
        _sleep.Tick += OnSleep;
        _sleep.Interval = AwakeFor;
        _sleep.Start();
        Build();
    }

    /// <summary>Stops breathing now, as if <see cref="AwakeFor"/> had passed, for a test.</summary>
    internal void Sleep()
    {
        _sleep?.Stop();
        _awake = false;
        Build();
    }

    private void OnSleep(object? sender, EventArgs e)
    {
        var left = AwakeFor - (DateTime.UtcNow - _woke);
        if (left > TimeSpan.FromMilliseconds(50))
        {
            _sleep!.Interval = left;
            return;
        }
        Sleep();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AeroMotion.Changed += Build;
        _window = Window.GetWindow(this);
        if (_window != null)
        {
            _window.Activated += OnWindowChanged;
            _window.Deactivated += OnWindowChanged;
            _window.StateChanged += OnWindowChanged;
            _window.PreviewMouseMove += OnPointer;
            _window.PreviewMouseDown += OnUsed;
            _window.PreviewKeyDown += OnUsed;
            if (_window.IsActive) Wake();
        }
        Build();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AeroMotion.Changed -= Build;
        if (_window != null)
        {
            _window.Activated -= OnWindowChanged;
            _window.Deactivated -= OnWindowChanged;
            _window.StateChanged -= OnWindowChanged;
            _window.PreviewMouseMove -= OnPointer;
            _window.PreviewMouseDown -= OnUsed;
            _window.PreviewKeyDown -= OnUsed;
            _window = null;
        }
        _sleep?.Stop();
        _awake = false;
        Build();
    }

    private void OnWindowChanged(object? sender, EventArgs e)
    {
        if (_window?.IsActive == true) Wake();
        Build();
    }

    private void OnUsed(object sender, EventArgs e) => Wake();

    /// <summary>The pointer moved, if it did: WPF also raises a mouse move under a still pointer whenever the layout
    /// under it changes, which a live reading does every second, and those kept the ring breathing for good (Plan U).</summary>
    private void OnPointer(object sender, MouseEventArgs e)
    {
        if (_window is not null) PointerAt(e.GetPosition(_window));
    }

    /// <summary>The pointer is at <paramref name="at"/> in the window: a wake if it has moved since the last.</summary>
    internal void PointerAt(Point at)
    {
        if (_pointer == at) return;
        _pointer = at;
        Wake();
    }

    private void Build()
    {
        Width = Height = Size;
        _ring.Width = _ring.Height = _dot.Width = _dot.Height = Size;
        Breathe(IsLoaded && _window != null
            && ShouldBreathe(Breathes, AeroMotion.Reduced, IsVisible, _window.IsActive, _window.WindowState == WindowState.Minimized, _awake));
    }

    /// <summary>Starts the ring breathing, or stops it and its clocks; internal for a test, whose windows are never
    /// active.</summary>
    internal void Breathe(bool breathe)
    {
        if (breathe == Breathing) return;
        StopClocks();
        if (!breathe)
        {
            _ring.Opacity = 0;
            return;
        }
        // The HTML's box-shadow ring: spreads 8 px each side and fades out by 70 % of the cycle.
        var grow = (Size + 16) / Size;
        var cycle = TimeSpan.FromMilliseconds(AeroMotion.Pulse);
        var outAt = TimeSpan.FromMilliseconds(AeroMotion.Pulse * .7);
        var scale = new DoubleAnimationUsingKeyFrames { Duration = cycle, RepeatBehavior = RepeatBehavior.Forever };
        scale.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.Zero));
        scale.KeyFrames.Add(new EasingDoubleKeyFrame(grow, outAt, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        scale.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, cycle));
        var fade = new DoubleAnimationUsingKeyFrames { Duration = cycle, RepeatBehavior = RepeatBehavior.Forever };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(.6, TimeSpan.Zero));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, outAt));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, cycle));
        Timeline.SetDesiredFrameRate(scale, FrameRate);
        Timeline.SetDesiredFrameRate(fade, FrameRate);
        _grow = scale.CreateClock();
        _fade = fade.CreateClock();
        _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, _grow);
        _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, _grow);
        _ring.ApplyAnimationClock(OpacityProperty, _fade);
    }

    /// <summary>Stops the ring's clocks, not only takes them off the ring: a clock that repeats for ever and is merely
    /// detached (BeginAnimation with null) stays active in WPF's timing tree until a garbage collection, and kept the
    /// frame clock ticking at the ring's rate long after the dot went still (Plan U, measured on the real App).</summary>
    private void StopClocks()
    {
        _grow?.Controller?.Remove();
        _fade?.Controller?.Remove();
        _grow = _fade = null;
        _scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, null);
        _scale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, null);
        _ring.ApplyAnimationClock(OpacityProperty, null);
    }
}
