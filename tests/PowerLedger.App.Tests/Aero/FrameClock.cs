using System.Collections;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PowerLedger.App.Tests;

/// <summary>
/// What keeps WPF drawing frames on this thread, read from its own bookkeeping (Plan U): the animation clocks its timing
/// tree is still ticking, and the handlers on CompositionTarget.Rendering. An App at rest, or one reading after it has
/// been drawn, should have neither. WPF keeps both internal, so they are read by reflection; a test that can no longer
/// find them fails naming what moved, rather than passing on nothing.
/// </summary>
internal static class FrameClock
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private static readonly Type MediaContext = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext", throwOnError: true)!;

    /// <summary>The root clocks now active (not filling, not stopped), each a clock WPF ticks every frame it asks for.</summary>
    public static IReadOnlyList<Clock> Active()
    {
        var timeManager = MediaContext.GetProperty("TimeManager", Any)?.GetValue(Context())
            ?? throw new InvalidOperationException("WPF's MediaContext has no TimeManager any more.");
        var root = timeManager.GetType().GetField("_timeManagerClock", Any)?.GetValue(timeManager)
            ?? throw new InvalidOperationException("WPF's TimeManager has no _timeManagerClock any more.");
        var field = root.GetType().GetField("_rootChildren", Any)
            ?? throw new InvalidOperationException("WPF's root clock has no _rootChildren any more.");
        var clocks = new List<Clock>();
        foreach (var child in (IEnumerable?)field.GetValue(root) ?? Array.Empty<object>())
        {
            var clock = child is WeakReference weak ? weak.Target as Clock : child as Clock;
            if (clock is { CurrentState: ClockState.Active }) clocks.Add(clock);
        }
        return clocks;
    }

    /// <summary>The active clocks that <paramref name="before"/> didn't hold: a test's own, not those an earlier test left.</summary>
    public static IReadOnlyList<Clock> ActiveSince(IReadOnlyList<Clock> before) => [.. Active().Where(c => !before.Contains(c))];

    /// <summary>How many handlers CompositionTarget.Rendering has on this thread: each asks for every frame.</summary>
    public static int RenderingHandlers
    {
        get
        {
            var rendering = MediaContext.GetField("Rendering", Any)
                ?? throw new InvalidOperationException("WPF's MediaContext has no Rendering field any more.");
            return ((Delegate?)rendering.GetValue(Context()))?.GetInvocationList().Length ?? 0;
        }
    }

    /// <summary>A clock as a failure names it: its timeline, length and repeat.</summary>
    public static string Describe(Clock clock)
        => $"{clock.Timeline.GetType().Name} {clock.Timeline.Duration} {clock.Timeline.RepeatBehavior}";

    private static object Context()
        => MediaContext.GetMethod("From", Any)?.Invoke(null, [Dispatcher.CurrentDispatcher])
           ?? throw new InvalidOperationException("WPF's MediaContext.From is gone.");
}
