using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace PowerLedger.App.Aero;

/// <summary>
/// The accent dot with a ring that breathes out every 1.8 s (the HTML's <c>.pulse</c>): "the service is measuring". The
/// ring runs at 30 frames a second, which a soft ring reads the same at, and only while someone can see it: its window
/// is shown, not minimised, and in the foreground. In the background, hidden to the tray, under reduced motion, or with
/// <see cref="Breathes"/> off (the overlay's, whose window would otherwise redraw all the time) the dot is still, so an
/// idle App asks for no frames.
/// </summary>
public sealed class PulseDot : Grid
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double),
        typeof(PulseDot), new PropertyMetadata(7.0, (d, _) => ((PulseDot)d).Build()));

    public static readonly DependencyProperty BreathesProperty = DependencyProperty.Register(nameof(Breathes), typeof(bool),
        typeof(PulseDot), new PropertyMetadata(true, (d, _) => ((PulseDot)d).Build()));

    /// <summary>The ring's frame rate: half the screen's, and the same to the eye.</summary>
    public const int FrameRate = 30;

    private readonly Ellipse _ring = new() { IsHitTestVisible = false };
    private readonly Ellipse _dot = new() { IsHitTestVisible = false };
    private readonly ScaleTransform _scale = new();
    private Window? _window;

    public PulseDot()
    {
        IsHitTestVisible = false;
        Focusable = false;
        Children.Add(_ring);
        Children.Add(_dot);
        _ring.RenderTransform = _scale;
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

    /// <summary>Whether the ring should breathe: someone can see it and motion is full.</summary>
    internal static bool ShouldBreathe(bool breathes, bool reduced, bool visible, bool windowActive, bool minimised)
        => breathes && !reduced && visible && windowActive && !minimised;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AeroMotion.Changed += Build;
        _window = Window.GetWindow(this);
        if (_window != null)
        {
            _window.Activated += OnWindowChanged;
            _window.Deactivated += OnWindowChanged;
            _window.StateChanged += OnWindowChanged;
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
            _window = null;
        }
        Build();
    }

    private void OnWindowChanged(object? sender, EventArgs e) => Build();

    private void Build()
    {
        Width = Height = Size;
        _ring.Width = _ring.Height = _dot.Width = _dot.Height = Size;
        var breathe = IsLoaded && _window != null
            && ShouldBreathe(Breathes, AeroMotion.Reduced, IsVisible, _window.IsActive, _window.WindowState == WindowState.Minimized);
        if (breathe == Breathing) return;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _ring.BeginAnimation(OpacityProperty, null);
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
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        _ring.BeginAnimation(OpacityProperty, fade);
    }
}
