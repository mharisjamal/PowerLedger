namespace PowerLedger.App;

/// <summary>The update card's "What's new" (owner's round): a small bundled table of each release's own plain points,
/// read by <see cref="WhatsNewWindow"/> instead of sending the user to the browser for them. Updated by hand alongside
/// each release.</summary>
internal static class WhatsNew
{
    /// <summary>Newest first: <see cref="Since"/> relies on this order.</summary>
    public static IReadOnlyList<(string Version, IReadOnlyList<string> Points)> Releases { get; } =
    [
        ("0.8.0",
        [
            "A new look: a dashboard with your power, today's energy and idle waste at a glance. Prefer the classic look? Switch back any time in Settings → Preferences.",
            "The chart shows the last hour to all your history, with a tooltip for any moment.",
        ]),
        ("0.7.0",
        [
            "See all your PCs together: add a PC on your network or with a code, and the Household page shows their total.",
            "Your data is encrypted end to end; the server can't read it.",
            "The installer works again on Windows 11 PCs that showed \"does not support the version of Windows\".",
            "32-bit Windows is supported.",
            "Send feedback from the bug button at the foot of the window.",
            "The data-sharing question is now one screen: Allow all or Decline; change any choice later in Settings → Privacy.",
        ]),
        ("0.6.0",
        [
            "Optional data sharing: help improve the estimates by sharing anonymous readings; ask in Settings → Privacy.",
        ]),
    ];

    /// <summary>The points of every bundled release after <paramref name="lastVersion"/> up to <paramref name="current"/>,
    /// newest first; each release's points are headed by its own version number when more than one release is included.
    /// Falls back to <paramref name="current"/>'s own points alone when <paramref name="lastVersion"/> is unknown (null),
    /// not older than <paramref name="current"/> (nothing is "since" a version that isn't older), or when nothing bundled
    /// falls in between — so the window always has something to show rather than an empty list.</summary>
    public static IReadOnlyList<string> Since(string? lastVersion, string current)
    {
        if (lastVersion is null || !Version.TryParse(lastVersion, out var last) || !Version.TryParse(current, out var upTo) || last >= upTo)
            return PointsOf(current);

        var included = Releases.Where(release => Version.TryParse(release.Version, out var version) && version > last && version <= upTo).ToList();
        if (included.Count == 0) return PointsOf(current);
        if (included.Count == 1) return included[0].Points;

        var points = new List<string>();
        foreach (var release in included)
        {
            points.Add(release.Version);
            points.AddRange(release.Points);
        }
        return points;
    }

    private static IReadOnlyList<string> PointsOf(string version)
        => Releases.FirstOrDefault(release => release.Version == version).Points ?? [];
}
