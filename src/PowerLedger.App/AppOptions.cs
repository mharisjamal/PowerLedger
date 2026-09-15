using System.IO;
using PowerLedger.Contracts;

namespace PowerLedger.App;

/// <param name="PipeName">The service's pipe; --pipe names a development service's.</param>
/// <param name="DataFolder">Where the service keeps power.db; --data names a development run's folder.</param>
/// <param name="StartInTray">--tray: start with only the tray icon, as the Run entry does.</param>
/// <param name="UpdateFeed">--update-feed: a stand-in for GitHub's releases on this machine, to test updates against; null means GitHub.</param>
internal sealed record AppOptions(string PipeName, string DataFolder, bool StartInTray, Uri? UpdateFeed = null)
{
    public static string DefaultDataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PowerLedger");

    public string DatabasePath => Path.Combine(DataFolder, "power.db");

    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        var pipe = PipeProtocol.PipeName;
        var data = DefaultDataFolder;
        var tray = false;
        Uri? feed = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--pipe" when i + 1 < args.Count:
                    pipe = args[++i];
                    break;
                case "--data" when i + 1 < args.Count:
                    data = args[++i];
                    break;
                case "--tray":
                    tray = true;
                    break;
                case "--update-feed" when i + 1 < args.Count:
                    feed = TestFeed(args[++i]);
                    break;
            }
        }
        return new AppOptions(pipe, data, tray, feed);
    }

    /// <summary>A feed to test updates against, served on this machine over HTTP or HTTPS; anything else is ignored, since
    /// whatever can change the App's command line could otherwise offer an installer GitHub doesn't list. The address ends
    /// in a slash, so the feed's paths go under it.</summary>
    internal static Uri? TestFeed(string text)
    {
        if (!Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var feed)) return null;
        return feed.IsLoopback && (feed.Scheme == Uri.UriSchemeHttp || feed.Scheme == Uri.UriSchemeHttps) ? feed : null;
    }
}
