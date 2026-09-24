using System.Windows;
using System.Windows.Markup;

namespace PowerLedger.App;

/// <summary>
/// How long Midnight's motion takes (plan O 0.5): 150 ms for a hover or a press, 220 ms for a page or the sidebar's pill,
/// 320 ms for the slow ones. When Windows asks for reduced motion (SystemParameters.ClientAreaAnimation off) movement takes
/// no time and only a 120 ms opacity fade is left, so nothing is lost but the travel. Every animation asks here: a
/// storyboard through <see cref="MotionExtension"/>, code directly.
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

/// <summary>A storyboard's Duration from the tokens: {local:Motion Fast}, or {local:Motion Fast, Fade=True} for an opacity fade.</summary>
[MarkupExtensionReturnType(typeof(Duration))]
internal sealed class MotionExtension(MotionSpeed speed) : MarkupExtension
{
    public MotionSpeed Speed { get; set; } = speed;

    public bool Fade { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var duration = Speed switch
        {
            MotionSpeed.Fast => Motion.Fast,
            MotionSpeed.Slow => Motion.Slow,
            _ => Motion.Base,
        };
        return Fade ? Motion.Fade(duration) : Motion.Of(duration);
    }
}
