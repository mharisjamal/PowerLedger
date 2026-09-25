namespace PowerLedger.App;

/// <summary>The update card's "What's new" (owner's round): a small bundled table of each release's own plain points,
/// read by <see cref="WhatsNewWindow"/> instead of sending the user to the browser for them. Updated by hand alongside
/// each release.</summary>
internal static class WhatsNew
{
    /// <summary>Newest first: <see cref="Since"/> relies on this order.</summary>
    public static IReadOnlyList<(string Version, IReadOnlyList<string> Points)> Releases { get; } =
    [
        ("0.9.1",
        [
            "The Energy used card starts on Since start, and its button switches it to Today, This week or This month.",
        ]),
        ("0.9.0",
        [
            "PowerLedger now keeps itself up to date: new versions install on their own, and it reopens afterwards.",
            "If you share data, today's figures go every hour instead of once a day.",
            "Approving a PC into your household always shows the right list straight away.",
        ]),
        ("0.8.1",
        [
            "Every graphics card counts now, older NVIDIA cards such as the Quadro 6000 included, and a PC with several cards adds them all up.",
            "The new look is closer to its design: one bordered row of figures, indigo charts, and a Start service button when the service is off.",
            "What's new opens here in the app, and storage warnings show in the new look too.",
        ]),
        ("0.8.0",
        [
            "A new look: a dashboard with your power, today's energy and idle waste at a glance. Prefer the classic look? Switch back any time in Settings → Preferences.",
            "The chart shows the last hour to all your history, with a tooltip for any moment.",
        ]),
        ("0.7.1",
        [
            "Sign in with Google to add a PC to your household from anywhere: your other PC approves it after both show the same code.",
            "A recovery code gets your household back if you ever lose every PC.",
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
