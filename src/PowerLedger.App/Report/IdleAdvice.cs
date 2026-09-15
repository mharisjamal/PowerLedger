using System.Globalization;

namespace PowerLedger.App;

/// <summary>
/// The idle-waste suggestion (spec §6): what Windows' timeouts say about the energy idle time used. It quotes the
/// plugged-in timeouts, where idle time costs most and the only ones a desktop has.
/// </summary>
internal static class IdleAdvice
{
    /// <summary>A sleep timeout at or under this already catches most idle time.</summary>
    public static readonly TimeSpan Prompt = TimeSpan.FromMinutes(30);

    /// <summary>A display timeout over this leaves the display lit longer than it needs to be.</summary>
    public static readonly TimeSpan PromptDisplay = TimeSpan.FromMinutes(5);

    public static string For(double idleKwh, double idleDisplayOnKwh, double energyKwh, SleepTimeouts timeouts)
    {
        if (!(energyKwh > 0) || idleKwh < 0.005 || idleKwh / energyKwh < 0.03) return "Idle time used little energy in this range.";
        if (timeouts.SleepAc is not { } sleep) return "Letting Windows sleep sooner when the machine is idle would cut most of it.";
        if (sleep == TimeSpan.Zero) return "Windows never sleeps here when plugged in. Sleeping after 30 minutes idle would cut most of it.";
        if (sleep > Prompt) return $"Windows sleeps after {Span(sleep)} idle when plugged in. Sleeping after 30 minutes would cut much of it.";
        if (idleDisplayOnKwh > idleKwh / 2 && timeouts.DisplayAc is { } display && (display == TimeSpan.Zero || display > PromptDisplay))
        {
            var lit = display == TimeSpan.Zero ? "while idle" : "for " + Span(display);
            return $"Windows already sleeps after {Span(sleep)}, but the display stays on {lit}. Turning it off after 5 minutes would save a little more.";
        }
        return $"Windows already sleeps after {Span(sleep)} idle when plugged in, so little more can be saved.";
    }

    /// <summary>"45 min", "3 h", "1 h 30 min".</summary>
    internal static string Span(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes);
        var invariant = CultureInfo.InvariantCulture;
        if (minutes < 60) return minutes.ToString(invariant) + " min";
        var hours = (minutes / 60).ToString(invariant) + " h";
        return minutes % 60 == 0 ? hours : hours + " " + (minutes % 60).ToString(invariant) + " min";
    }
}
