namespace PowerLedger.Service.Updates;

/// <summary>What the update worker does with a checked download on this tick.</summary>
internal enum InstallMoment
{
    /// <summary>Not yet: someone is using PowerLedger.</summary>
    Wait,

    /// <summary>Say "PowerLedger updates in a minute" in the tray, and install a minute from now.</summary>
    Warn,

    /// <summary>Install now.</summary>
    Now,
}

/// <summary>
/// When the service installs an update (Plan Q §4). At once when the update is required (the server has stopped taking
/// this version, or the App asked with Update now), when nobody is signed in at the console, or when the App's main window
/// isn't showing there. Otherwise once the user has been idle for five minutes, and at the latest an hour after the
/// download, in both cases with a tray notice a minute before. And it downloads on a metered connection only once it has
/// waited a day for another, or when the update is required.
/// </summary>
internal static class InstallTiming
{
    /// <summary>Idle this long with the window showing, and the update goes in.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);

    /// <summary>However busy the user is, the update goes in this long after the download.</summary>
    public static readonly TimeSpan Latest = TimeSpan.FromHours(1);

    /// <summary>The tray notice comes this long before an install the user might see.</summary>
    public static readonly TimeSpan Warning = TimeSpan.FromMinutes(1);

    /// <summary>A metered connection is waited out this long before the download goes ahead on it.</summary>
    public static readonly TimeSpan MeteredPatience = TimeSpan.FromHours(24);

    /// <param name="urgent">Required by the server, or asked for with Update now.</param>
    /// <param name="userAtConsole">Someone is signed in at the console.</param>
    /// <param name="windowShowing">An App there says its main window is showing.</param>
    /// <param name="idleSeconds">Seconds since the user's last input, from the App; null when no App says.</param>
    /// <param name="downloadedAt">When the download was checked.</param>
    /// <param name="warnedAt">When the tray notice went out, or null before it has.</param>
    /// <param name="now">Now.</param>
    public static InstallMoment Decide(
        bool urgent, bool userAtConsole, bool windowShowing, double? idleSeconds, DateTimeOffset downloadedAt, DateTimeOffset? warnedAt,
        DateTimeOffset now)
    {
        if (urgent || !userAtConsole || !windowShowing) return InstallMoment.Now;
        if (warnedAt is { } warned) return now - warned >= Warning ? InstallMoment.Now : InstallMoment.Wait;
        var idle = idleSeconds is { } seconds && seconds >= Idle.TotalSeconds;
        return idle || now >= downloadedAt + Latest - Warning ? InstallMoment.Warn : InstallMoment.Wait;
    }

    /// <summary>Whether a release first seen at <paramref name="firstSeen"/> may be downloaded now.</summary>
    public static bool MayDownload(bool metered, bool urgent, DateTimeOffset firstSeen, DateTimeOffset now)
        => !metered || urgent || now - firstSeen >= MeteredPatience;
}
