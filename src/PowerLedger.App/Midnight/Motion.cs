using System.Windows;
using System.Windows.Media.Animation;

namespace PowerLedger.App;

/// <summary>
/// How long Midnight's motion takes (plan O 0.5): 150 ms for a hover or a press, 220 ms for a page or the sidebar's pill,
/// 320 ms for the slow ones. When Windows asks for reduced motion (SystemParameters.ClientAreaAnimation off) movement takes
/// no time and only a 120 ms opacity fade is left, so nothing is lost but the travel. Every animation asks here: a
/// style's storyboard through <see cref="MotionAnimation"/> each time it starts, code directly.
/// </summary>
internal static class Motion
{
    public static readonly Duration Fast = new(TimeSpan.FromMilliseconds(150));
    public static readonly Duration Base = new(TimeSpan.FromMilliseconds(220));
    public static readonly Duration Slow = new(TimeSpan.FromMilliseconds(320));

    private static readonly Duration ReducedFade = new(TimeSpan.FromMilliseconds(120));
    private static readonly Duration None = new(TimeSpan.Zero);
    private static bool? _forced;

    public static bool Reduced => _forced ?? !SystemParameters.ClientAreaAnimation;

    /// <summary>The length of a movement: <paramref name="duration"/>, or nothing under reduced motion.</summary>
    public static Duration Of(Duration duration) => Reduced ? None : duration;

    /// <summary>The length of an opacity fade: <paramref name="duration"/>, or 120 ms under reduced motion.</summary>
    public static Duration Fade(Duration duration) => Reduced ? ReducedFade : duration;

    /// <summary>Stands in for Windows' setting until the scope ends, for a test.</summary>
    internal static IDisposable Force(bool reduced)
    {
        var before = _forced;
        _forced = reduced;
        return new Scope(() => _forced = before);
    }

    private sealed class Scope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}

internal enum MotionSpeed
{
    Fast,
    Base,
    Slow,
}

/// <summary>
/// A style's animation whose length is a token (<see cref="Speed"/>, and <see cref="Fade"/> for an opacity fade) asked of
/// <see cref="Motion"/> each time it starts, not once when the style loads: a storyboard in a style is frozen with it, so
/// a length fixed then would ignore Windows' animation setting changed later in the session. Leave Duration unset; the
/// length is the animation's natural one.
/// </summary>
internal sealed class MotionAnimation : DoubleAnimation
{
    public static readonly DependencyProperty SpeedProperty = DependencyProperty.Register(
        nameof(Speed), typeof(MotionSpeed), typeof(MotionAnimation), new PropertyMetadata(MotionSpeed.Base));

    public static readonly DependencyProperty FadeProperty = DependencyProperty.Register(
        nameof(Fade), typeof(bool), typeof(MotionAnimation), new PropertyMetadata(false));

    public MotionSpeed Speed { get => (MotionSpeed)GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }

    /// <summary>An opacity fade, which keeps 120 ms under reduced motion where a movement takes none.</summary>
    public bool Fade { get => (bool)GetValue(FadeProperty); set => SetValue(FadeProperty, value); }

    protected override Duration GetNaturalDurationCore(Clock clock)
    {
        var duration = Speed switch
        {
            MotionSpeed.Fast => Motion.Fast,
            MotionSpeed.Slow => Motion.Slow,
            _ => Motion.Base,
        };
        return Fade ? Motion.Fade(duration) : Motion.Of(duration);
    }

    protected override Freezable CreateInstanceCore() => new MotionAnimation();
}
