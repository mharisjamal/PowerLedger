namespace PowerLedger.Contracts;

/// <summary>Where a monitor's watts came from (spec §5). Piped as an integer: append new members, never renumber.</summary>
public enum MonitorSource
{
    /// <summary>Energy Star's measured figure for this model.</summary>
    Model = 0,

    /// <summary>Worked out from the monitor's size and resolution.</summary>
    Estimate = 1,

    /// <summary>Typed by the user, which always wins.</summary>
    Typed = 2,
}

/// <summary>What the user said about one monitor: whether it counts, a figure of their own, and whether it has a plug of its
/// own. A monitor with no choice takes PowerLedger's defaults (see <see cref="MonitorStatus.CountedByDefault"/> and
/// <see cref="MonitorStatus.OwnPlugByDefault"/>), with the figure PowerLedger works out.</summary>
public sealed record MonitorChoice
{
    public string Key { get; init; } = "";

    public bool Counted { get; init; } = true;

    /// <summary>On-mode watts typed by the user, or null for PowerLedger's own figure.</summary>
    public double? Watts { get; init; }

    /// <summary>True for a monitor with a plug of its own, false for one that runs off this PC, or null for PowerLedger's
    /// guess.</summary>
    public bool? OwnPlug { get; init; }
}

/// <summary>One external monitor as the service sees it, for the App (spec §9).</summary>
public sealed record MonitorStatus
{
    public string Key { get; init; } = "";

    /// <summary>Windows' device instance, which the App's brightness reports name (see <see cref="MonitorKeys"/>).</summary>
    public string Instance { get; init; } = "";

    /// <summary>"DELL U2723QE", or the maker and product code when the monitor gives no name.</summary>
    public string Name { get; init; } = "";

    public double Inches { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>On-mode watts: PowerLedger's own figure at the list's test brightness, before this monitor's own brightness,
    /// or the figure the user typed, which is taken as it is.</summary>
    public double OnWatts { get; init; }

    public double SleepWatts { get; init; }

    public MonitorSource Source { get; init; }

    public bool Counted { get; init; }

    /// <summary>Whether the monitor counts when the user hasn't said: false only where the settings from before monitors
    /// were detected left monitors out (<see cref="MachineProfile.CountMonitorsByDefault"/>).</summary>
    public bool CountedByDefault { get; init; } = true;

    /// <summary>Whether the monitor is taken to have a plug of its own, drawing outside the PC, rather than to run off the
    /// PC, whose own reading already holds what it draws.</summary>
    public bool OwnPlug { get; init; } = true;

    /// <summary>What <see cref="OwnPlug"/> is when the user hasn't said: false for a monitor of 17.3 inches or less on a
    /// laptop, which is taken for a portable one running off the laptop.</summary>
    public bool OwnPlugByDefault { get; init; } = true;

    /// <summary>The brightness the App last reported, 0–1, or null when the monitor didn't answer.</summary>
    public double? Brightness { get; init; }

    /// <summary>What it draws now, as the model counts it (0 when not counted).</summary>
    public double WattsNow { get; init; }
}

/// <summary>A brightness the App read from one monitor.</summary>
public sealed record MonitorBrightness
{
    /// <summary>The monitor's device instance, as <see cref="MonitorKeys"/> gives it.</summary>
    public string Instance { get; init; } = "";

    /// <summary>0–1.</summary>
    public double Brightness { get; init; }
}

/// <summary>
/// One monitor's identity across the two ways Windows names it. WMI's <c>InstanceName</c> is
/// <c>DISPLAY\DELA0B1\5&amp;2f5a1b&amp;0&amp;UID4353_0</c>; a device interface path is
/// <c>\\?\DISPLAY#DELA0B1#5&amp;2f5a1b&amp;0&amp;UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}</c>. Both become
/// <c>DISPLAY\DELA0B1\5&amp;2F5A1B&amp;0&amp;UID4353</c>.
/// </summary>
public static class MonitorKeys
{
    /// <summary>WMI's instance name without the number WMI appends to it, in upper case.</summary>
    public static string FromInstanceName(string instanceName)
    {
        var text = instanceName.Trim();
        var underscore = text.LastIndexOf('_');
        if (underscore > 0 && underscore < text.Length - 1 && !text.AsSpan(underscore + 1).ContainsAnyExceptInRange('0', '9'))
            text = text[..underscore];
        return text.ToUpperInvariant();
    }

    /// <summary>The device instance a display's device interface path names, in upper case, or null when the path is
    /// not a display's.</summary>
    public static string? FromDevicePath(string devicePath)
    {
        var text = devicePath.Trim();
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal)) text = text[4..];
        var brace = text.IndexOf("#{", StringComparison.Ordinal);
        if (brace >= 0) text = text[..brace];
        var parts = text.Split('#');
        return parts is [var bus, { Length: > 0 }, { Length: > 0 }] && bus.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)
            ? string.Join('\\', parts).ToUpperInvariant()
            : null;
    }
}
