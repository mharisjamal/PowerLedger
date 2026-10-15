using System.Globalization;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Plan AE: the Apps ledger's lines from a range's apps and hour rows.</summary>
public class AppLedgerTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    internal static readonly DateTimeOffset Since = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);

    internal static AppUse Use(string key, string name, double cpu, double gpu = 0, double run = 3600, double front = 0, decimal cost = 0, string? path = null)
        => new(key, name, path, cpu, gpu, run, front, cost);

    /// <summary>A day of a PC: three apps, Windows and idle, the screen and the rest, in rupees.</summary>
    internal static AppsRange Day(IReadOnlyList<AppUse>? apps = null, string? currency = "PKR", double rest = 290, double asleep = 0, double losses = 0, double untraced = 0)
        => new(Since, Since.AddHours(14), Since,
            apps ??
            [
                Use(@"c:\program files\google\chrome\application\chrome.exe", "Google Chrome", 290, 30, run: 6 * 3600 + 600, front: 3 * 3600 + 300, cost: 11.20m,
                    path: @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
                Use(@"c:\users\a\appdata\local\programs\microsoft vs code\code.exe", "Visual Studio Code", 200, 10, run: 5 * 3600, front: 4 * 3600, cost: 7.35m),
                Use("pkg:microsoft.windowsterminal_8wekyb3d8bbwe", "Terminal", 40, 0, run: 3600, front: 600, cost: 1.40m),
                Use(AppQueries.WindowsKey, "Windows", 120, 0, run: 14 * 3600, cost: 4.20m),
                Use(AppQueries.IdleKey, "Idle", 260, 0, run: 14 * 3600, cost: 9.10m),
                Use(AppQueries.ClosedKey, "Closed", 12, 0, cost: 0.42m),
            ],
            new PricedWh(410, 14.35m), new PricedWh(rest, 10.15m), new PricedWh(asleep, 0.30m), new PricedWh(losses, 0.20m), new PricedWh(untraced, 0.05m),
            currency, false);

    [Fact]
    public void The_apps_come_most_first_then_the_lines_beside_them_then_the_fixed_lines_and_the_total()
    {
        var rows = AppLedger.Rows(Day(), English);

        rows.Select(r => (r.Name, r.Kind)).ShouldBe(
        [
            ("Google Chrome", AppRowKind.App), ("Visual Studio Code", AppRowKind.App), ("Terminal", AppRowKind.App),
            ("Programs that already closed", AppRowKind.Beside),
            ("Screen", AppRowKind.Fixed), ("Windows and idle", AppRowKind.Fixed), ("Rest of your PC", AppRowKind.Fixed),
            ("Total", AppRowKind.Total),
        ]);
    }

    /// <summary>The whole is everything the lines hold, so they add up to it, and every share is of it.</summary>
    [Fact]
    public void Every_share_is_of_the_whole_the_lines_add_up_to()
    {
        var rows = AppLedger.Rows(Day(), English);
        const double whole = 320 + 210 + 40 + 12 + 410 + 380 + 290;

        rows[^1].Energy.ShouldBe("1.66");
        rows[^1].Share.ShouldBe("100%");
        rows[0].Fraction.ShouldBe(320 / whole, 1e-12);
        rows[0].Energy.ShouldBe("0.320");
        rows[0].Share.ShouldBe("19%");
        rows.Single(r => r.Name == "Windows and idle").Energy.ShouldBe("0.380");
        rows.Where(r => r.Kind != AppRowKind.Total).Sum(r => r.Fraction).ShouldBe(1, 1e-12);
    }

    /// <summary>A line with some energy never reads as none: under half a percent is "<1%", "less than 1 percent".</summary>
    [Fact]
    public void A_line_under_half_a_percent_reads_as_less_than_one()
    {
        var day = Day();
        var rows = AppLedger.Rows(day with { Apps = [.. day.Apps, Use("c:\\tiny.exe", "Tiny", 4)] }, English);
        var tiny = rows.Single(r => r.Name == "Tiny");

        tiny.Share.ShouldBe("<1%");
        tiny.Spoken.ShouldBe("Tiny, 0.004 kWh, less than 1 percent, 0 rupees");
    }

    [Fact]
    public void Each_line_costs_its_own_hours_and_the_total_is_their_sum()
    {
        var rows = AppLedger.Rows(Day(), English);

        rows[0].Cost.ShouldBe(Money.Format(11.20m, "PKR", English));
        rows.Single(r => r.Name == "Windows and idle").Amount.ShouldBe(4.20m + 9.10m);
        rows.Single(r => r.Name == "Rest of your PC").Amount.ShouldBe(10.15m);
        rows[^1].Amount.ShouldBe(11.20m + 7.35m + 1.40m + 0.42m + 14.35m + 13.30m + 10.15m);
    }

    [Fact]
    public void A_screen_reader_hears_the_name_energy_share_and_cost_in_words()
    {
        var chrome = AppLedger.Rows(Day(), English)[0];

        chrome.Spoken.ShouldBe("Google Chrome, 0.320 kWh, 19 percent, 11.20 rupees");
        chrome.Detail.ShouldBe("Processor 0.290 kWh, graphics 0.030 kWh. Ran 6 hours 10 minutes, 3 hours 5 minutes of it in front.");
    }

    /// <summary>Opened, an app shows its processor against its graphics, how long it ran and was in front, and the
    /// background tag when it was in front for less than half that time.</summary>
    [Fact]
    public void An_app_tells_its_processor_and_graphics_its_time_and_whether_it_ran_mostly_in_the_background()
    {
        var rows = AppLedger.Rows(Day(), English, open: "pkg:microsoft.windowsterminal_8wekyb3d8bbwe");
        var (chrome, code, terminal) = (rows[0], rows[1], rows[2]);

        (chrome.Processor, chrome.Graphics, chrome.ProcessorShare).ShouldBe(("0.290 kWh", "0.030 kWh", 290 / 320.0));
        (chrome.Ran, chrome.InFront, chrome.Background).ShouldBe(("Ran 6h 10m", "In front 3h 05m", false));
        code.Background.ShouldBeFalse();
        terminal.Background.ShouldBeTrue();
        terminal.Detail.ShouldEndWith("Mostly in background.");
        terminal.IsOpen.ShouldBeTrue();
        chrome.IsOpen.ShouldBeFalse();
    }

    /// <summary>A range with no time in front recorded (an App from before 0.12.0 never says which window is in front)
    /// tags nothing as in the background and says nothing of the front.</summary>
    [Fact]
    public void Without_any_time_in_front_recorded_nothing_is_said_of_the_front()
    {
        var day = Day();
        var rows = AppLedger.Rows(day with { Apps = [.. day.Apps.Select(a => a with { FrontSeconds = 0 })] }, English);

        rows.Where(r => r.Kind == AppRowKind.App).ShouldAllBe(r => !r.Background && r.InFront == null);
        rows[0].Detail.ShouldBe("Processor 0.290 kWh, graphics 0.030 kWh. Ran 6 hours 10 minutes.");
    }

    [Fact]
    public void A_store_app_and_every_line_that_is_no_app_take_a_plain_glyph()
    {
        var rows = AppLedger.Rows(Day(), English);

        (rows[0].Glyph, rows[0].Path).ShouldBe((AppGlyph.Program, @"C:\Program Files\Google\Chrome\Application\chrome.exe"));
        (rows[2].Glyph, rows[2].Path).ShouldBe((AppGlyph.Store, null));
        rows.Skip(3).Select(r => r.Glyph).ShouldBe([AppGlyph.Closed, AppGlyph.Screen, AppGlyph.System, AppGlyph.Rest, AppGlyph.Total]);
        rows.Skip(3).ShouldAllBe(r => r.Path == null);
    }

    /// <summary>As the bands count them: a rest below zero is none, and what the PC drew asleep and what charging lost
    /// are in the rest of the PC.</summary>
    [Fact]
    public void The_rest_of_the_pc_holds_the_rest_band_the_sleep_and_the_charging_losses_a_rest_below_zero_as_none()
    {
        var rest = AppLedger.Rows(Day(rest: 200, asleep: 20, losses: 10), English).Single(r => r.Key == AppLedger.RestKey);
        rest.Energy.ShouldBe("0.230");
        rest.Note.ShouldBe("Memory, drives, the board and the rest of the PC, 0.020 kWh drawn asleep, 0.010 kWh lost charging the battery.");
        rest.Amount.ShouldBe(10.15m + 0.30m + 0.20m);

        var negative = AppLedger.Rows(Day(rest: -40), English).Single(r => r.Key == AppLedger.RestKey);
        (negative.Energy, negative.Amount).ShouldBe(("0.000", 0m));
    }

    [Fact]
    public void Lines_beside_the_apps_show_only_when_they_hold_something_and_the_fixed_lines_always()
    {
        var day = Day();
        var rows = AppLedger.Rows(day with
        {
            Apps = [.. day.Apps.Where(a => a.Key != AppQueries.ClosedKey), Use(AppQueries.OtherUsersKey, "Users", 5), Use(AppQueries.OtherKey, "Other", 0)],
            Screen = PricedWh.None,
        }, English);

        rows.Where(r => r.Kind == AppRowKind.Beside).Select(r => r.Name).ShouldBe(["Other users"]);
        rows.Where(r => r.Kind == AppRowKind.Fixed).Select(r => r.Name).ShouldBe(["Screen", "Windows and idle", "Rest of your PC"]);
        rows.Where(r => r.Apart).Select(r => r.Name).ShouldBe(["Screen"], "a rule sets the fixed lines apart");
    }

    /// <summary>Energy no app was given (the hour PowerLedger began counting apps in) has a line of its own, so the
    /// lines still add up to the PC's total; a residue too small to show is left out of the whole too.</summary>
    [Fact]
    public void Energy_traced_to_no_app_has_a_line_of_its_own_when_it_shows()
    {
        var rows = AppLedger.Rows(Day(untraced: 40), English);
        var line = rows.Single(r => r.Key == AppLedger.UntracedKey);
        (line.Name, line.Kind, line.Energy, line.HasSplit).ShouldBe(("Not traced to an app", AppRowKind.Beside, "0.040", false));
        rows[^1].Energy.ShouldBe("1.70");

        AppLedger.Rows(Day(untraced: 0.3), English).ShouldNotContain(r => r.Key == AppLedger.UntracedKey);
        AppLedger.Rows(Day(untraced: 0.3), English)[^1].Energy.ShouldBe("1.66");
    }

    [Fact]
    public void Past_the_most_apps_listed_the_smallest_join_other_apps()
    {
        var many = Enumerable.Range(1, AppLedger.MostApps + 5).Select(i => Use($"c:\\app{i}.exe", $"App {i}", 100 - i, cost: 1m)).ToList();
        var rows = AppLedger.Rows(Day([.. many, Use(AppQueries.OtherKey, "Other", 10, cost: 2m)]), English);

        rows.Count(r => r.Kind == AppRowKind.App).ShouldBe(AppLedger.MostApps);
        rows[AppLedger.MostApps - 1].Name.ShouldBe($"App {AppLedger.MostApps}");
        var other = rows.Single(r => r.Name == "Other apps");
        other.Energy.ShouldBe(Format.Kwh((10 + Enumerable.Range(AppLedger.MostApps + 1, 5).Sum(i => 100.0 - i)) / 1000, English));
        other.Amount.ShouldBe(7m);
    }

    [Fact]
    public void Without_a_tariff_no_line_has_a_cost_and_none_is_spoken()
    {
        var rows = AppLedger.Rows(Day(currency: null), English);

        rows.ShouldAllBe(r => r.Cost == "N/A" && r.Amount == 0);
        rows[0].Spoken.ShouldBe("Google Chrome, 0.320 kWh, 19 percent");
    }

    /// <summary>Plan AE: "Apps show here once PowerLedger has watched them for a few minutes"; until the apps have a
    /// watt-hour between them, there is no ledger and no top app.</summary>
    [Fact]
    public void Too_little_watched_yet_is_no_ledger_and_no_top_app()
    {
        var first = Day([Use("c:\\a.exe", "A", 0.4), Use(AppQueries.IdleKey, "Idle", 0.5)]);
        AppLedger.HasApps(first).ShouldBeFalse();
        AppLedger.Rows(first, English).ShouldBeEmpty();
        AppLedger.Top(first).ShouldBeNull();
        AppLedger.Rows(AppsRange.None(Since, Since.AddHours(1), null, "PKR"), English).ShouldBeEmpty();
    }

    [Fact]
    public void The_top_app_is_the_one_that_used_the_most_never_a_line_that_is_no_app()
    {
        AppLedger.Top(Day())!.Name.ShouldBe("Google Chrome");
        AppLedger.Top(Day([Use(AppQueries.IdleKey, "Idle", 500), Use("c:\\b.exe", "B", 3)]))!.Name.ShouldBe("B");
    }

    /// <summary>A program's own name is shown as the service read it, with an em or en dash in it made a plain hyphen (the
    /// owner's rule for every word on screen), and a program without one named by its file.</summary>
    [Fact]
    public void A_programs_name_never_shows_a_dash_and_a_nameless_one_is_named_by_its_file()
    {
        var rows = AppLedger.Rows(Day([Use(@"c:\tools\spotify.exe", "Spotify \u2013 Music \u2014 Player", 30), Use(@"c:\tools\my-tool.exe", "  ", 20, path: @"C:\Tools\My-Tool.exe"),
            Use("pkg:contoso.notes_8wekyb3d8bbwe", "", 10)]), English);

        rows.Take(3).Select(r => r.Name).ShouldBe(["Spotify - Music - Player", "My-Tool", "contoso.notes"]);
        AppLedger.Top(Day([Use(@"c:\a.exe", "A \u2014 B", 30)]))!.Name.ShouldBe("A \u2014 B", "the use as read; the line names it");
        AppLedger.NameOf(Use(@"c:\a.exe", "A \u2014 B", 30)).ShouldBe("A - B");
    }

    [Fact]
    public void No_line_reads_with_a_dash()
        => AppLedger.Rows(Day(rest: 200, asleep: 20, losses: 10, untraced: 40), English)
            .SelectMany(r => new[] { r.Name, r.Energy, r.Share, r.Cost, r.Spoken, r.Detail, r.Note ?? "", r.Ran ?? "", r.InFront ?? "" })
            .ShouldAllBe(text => !text.Contains('\u2014') && !text.Contains('\u2013'));
}
