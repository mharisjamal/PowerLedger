using System.Globalization;
using System.IO;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Storage;

namespace PowerLedger.App.Tests;

/// <summary>
/// What the Apps renders show (Plan AE): a working day of a PC in rupees, its programs at the paths they install to, so
/// a render shows each one's own icon where this PC has it and the plain glyph where it has not.
/// </summary>
internal static class AppsFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 16, 14, 30, 0, TimeSpan.Zero);   // a Friday afternoon
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static string ProgramFiles(params string[] parts) => Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), .. parts]);

    private static string ProgramFilesX86(params string[] parts) => Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), .. parts]);

    private static string Local(params string[] parts) => Path.Combine([Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), .. parts]);

    private static AppUse App(string path, string name, double cpu, double gpu, double runMinutes, double frontMinutes, decimal cost)
        => new(path.ToLowerInvariant(), name, path, cpu, gpu, runMinutes * 60, frontMinutes * 60, cost);

    /// <summary>The day so far: six programs (a Store app among them), programs that closed, smaller apps, Windows and
    /// idle, the screen, the rest and a little sleep.</summary>
    public static AppsRange Day(string? currency = "PKR") => new(
        new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero), Now.AddMinutes(30), new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero),
        [
            App(ProgramFiles("Google", "Chrome", "Application", "chrome.exe"), "Google Chrome", 286, 58, 400, 190, 12.04m),
            App(Local("Programs", "Microsoft VS Code", "Code.exe"), "Visual Studio Code", 212, 9, 425, 320, 7.74m),
            App(ProgramFilesX86("Microsoft", "Edge", "Application", "msedge.exe"), "Microsoft Edge", 96, 12, 420, 25, 3.78m),
            new AppUse("pkg:spotifyab.spotifymusic_zpdnekdrzrea0", "Spotify", null, 41, 3, 180 * 60, 10 * 60, 1.54m),
            App(ProgramFilesX86("Windows Media Player", "wmplayer.exe"), "Windows Media Player", 18, 6, 50, 45, 0.84m),
            App(ProgramFiles("Git", "git-bash.exe"), "Git Bash", 9, 0, 40, 30, 0.32m),
            new AppUse(AppQueries.ClosedKey, "Closed", null, 13, 2, 0, 0, 0.52m),
            new AppUse(AppQueries.OtherKey, "Other", null, 10, 1, 0, 0, 0.39m),
            new AppUse(AppQueries.WindowsKey, "Windows", null, 112, 6, 14 * 3600, 0, 4.13m),
            new AppUse(AppQueries.IdleKey, "Idle", null, 231, 16, 14 * 3600, 0, 8.65m),
        ],
        new PricedWh(402, 14.07m), new PricedWh(268, 9.38m), new PricedWh(14, 0.49m), PricedWh.None, PricedWh.None,
        currency, false);

    /// <summary>The Apps page's ViewModel over <paramref name="answer"/> (the day, unless given), on Friday afternoon.</summary>
    public static AppsViewModel Screen(Func<DateRange, AppsRange?>? answer = null, FakeAppHistory? history = null)
    {
        history ??= new FakeAppHistory();
        history.Answer = answer ?? (_ => Day());
        return new AppsViewModel(history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English);
    }
}
