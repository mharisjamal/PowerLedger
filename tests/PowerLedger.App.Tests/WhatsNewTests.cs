using Shouldly;

namespace PowerLedger.App.Tests;

public class WhatsNewTests
{
    private static IReadOnlyList<string> PointsOf(string version) => WhatsNew.Releases.Single(r => r.Version == version).Points;

    [Fact]
    public void With_no_last_version_only_the_currents_own_points_come_back()
        => WhatsNew.Since(null, "0.7.0").ShouldBe(PointsOf("0.7.0"));

    [Fact]
    public void A_last_version_newer_than_current_falls_back_to_the_currents_own_points()
        => WhatsNew.Since("0.8.0", "0.7.0").ShouldBe(PointsOf("0.7.0"));

    [Fact]
    public void A_last_version_equal_to_current_falls_back_to_the_currents_own_points()
        => WhatsNew.Since("0.7.0", "0.7.0").ShouldBe(PointsOf("0.7.0"));

    [Fact]
    public void An_unparsable_last_version_falls_back_to_the_currents_own_points()
        => WhatsNew.Since("not-a-version", "0.7.0").ShouldBe(PointsOf("0.7.0"));

    [Fact]
    public void A_version_nothing_is_bundled_for_falls_back_to_an_empty_list()
        => WhatsNew.Since(null, "0.1.0").ShouldBeEmpty();

    /// <summary>Exactly one release qualifies: its points come back on their own, with no version heading.</summary>
    [Fact]
    public void One_release_between_last_and_current_has_no_heading()
        => WhatsNew.Since("0.6.0", "0.7.0").ShouldBe(PointsOf("0.7.0"));

    /// <summary>More than one release qualifies: newest first, each headed by its own version number.</summary>
    [Fact]
    public void Several_releases_between_last_and_current_are_headed_by_their_own_version_newest_first()
    {
        var points = WhatsNew.Since("0.5.0", "0.7.0");

        var expected = new List<string> { "0.7.0" };
        expected.AddRange(PointsOf("0.7.0"));
        expected.Add("0.6.0");
        expected.AddRange(PointsOf("0.6.0"));
        points.ShouldBe(expected);
    }

    [Fact]
    public void The_0_7_1_points_match_exactly()
        => PointsOf("0.7.1").ShouldBe(
        [
            "Sign in with Google to add a PC to your household from anywhere: your other PC approves it after both show the same code.",
            "A recovery code gets your household back if you ever lose every PC.",
        ]);

    [Fact]
    public void Updating_from_0_7_0_shows_only_what_0_7_1_added()
        => WhatsNew.Since("0.7.0", "0.7.1").ShouldBe(PointsOf("0.7.1"));

    [Fact]
    public void The_0_7_0_points_match_exactly()
        => PointsOf("0.7.0").ShouldBe(
        [
            "See all your PCs together: add a PC on your network or with a code, and the Household page shows their total.",
            "Your data is encrypted end to end; the server can't read it.",
            "The installer works again on Windows 11 PCs that showed \"does not support the version of Windows\".",
            "32-bit Windows is supported.",
            "Send feedback from the bug button at the foot of the window.",
            "The data-sharing question is now one screen: Allow all or Decline; change any choice later in Settings → Privacy.",
        ]);

    [Fact]
    public void The_0_6_0_points_match_exactly()
        => PointsOf("0.6.0").ShouldBe(
        [
            "Optional data sharing: help improve the estimates by sharing anonymous readings; ask in Settings → Privacy.",
        ]);

    /// <summary>0.10.8: Aero's side scroll bar, lit as the pointer comes near.</summary>
    [Fact]
    public void The_0_10_8_points_match_exactly_and_come_first()
    {
        WhatsNew.Releases[0].Version.ShouldBe("0.10.8", "newest first");
        PointsOf("0.10.8").ShouldBe([
            "Aero has a side scrollbar that lights up when your pointer comes near.",
        ]);
        WhatsNew.Since("0.10.7", "0.10.8").ShouldBe(PointsOf("0.10.8"));
    }

    /// <summary>0.10.7: Aero is by request, approved by the owner, and the old invite codes stop working.</summary>
    [Fact]
    public void The_0_10_7_points_match_exactly()
    {
        WhatsNew.Releases[1].Version.ShouldBe("0.10.7");
        PointsOf("0.10.7").ShouldBe([
            "Aero is now by request: press Request Aero in Settings, Look, and send your request ID to the PowerLedger owner. Earlier invite codes no longer work.",
        ]);
        WhatsNew.Since("0.10.6", "0.10.7").ShouldBe(PointsOf("0.10.7"));
    }

    /// <summary>0.10.6 is Aero's thin glass edges, every style over what is really behind the window, and the reveal
    /// each time it opens.</summary>
    [Fact]
    public void The_0_10_6_points_match_exactly()
    {
        WhatsNew.Releases[2].Version.ShouldBe("0.10.6");
        PointsOf("0.10.6").ShouldBe([
            "Aero's pills, buttons and panels have the demo's thin glass edges.",
            "Every glass style now shows what is really behind PowerLedger.",
            "Aero opens with its reveal every time, as in the video.",
        ]);
        WhatsNew.Since("0.10.5", "0.10.6").ShouldBe(PointsOf("0.10.6"));
    }

    /// <summary>0.10.5 measures workstations and servers, and Aero Settings has the network UPS section.</summary>
    [Fact]
    public void The_0_10_5_points_match_exactly()
    {
        WhatsNew.Releases[3].Version.ShouldBe("0.10.5");
        PointsOf("0.10.5").ShouldBe([
            "More is measured on workstations and servers: Windows power meters, a UPS on another computer (Network UPS Tools), Intel Arc card power and server power readings.",
            "Aero Settings has the network UPS section too.",
        ]);
        WhatsNew.Since("0.10.4", "0.10.5").ShouldBe(PointsOf("0.10.5"));
    }

    /// <summary>0.10.4 is Aero without its frame, live Clear, and more measured sources.</summary>
    [Fact]
    public void The_0_10_4_points_match_exactly()
    {
        WhatsNew.Releases[4].Version.ShouldBe("0.10.4");
        PointsOf("0.10.4").ShouldBe([
            "Aero has no frame around it any more, and its glass has thin, clean edges.",
            "Clear shows whatever is behind PowerLedger, live: your desktop and your other windows.",
            "More is measured: Snapdragon PCs, NVIDIA cards' own energy counter, and better battery readings.",
        ]);
        WhatsNew.Since("0.10.3", "0.10.4").ShouldBe(PointsOf("0.10.4"));
    }

    /// <summary>0.10.3 is Aero as its film: Aero bloom, the count-ups, Replay intro and Play tour, the panels closer to the design.</summary>
    [Fact]
    public void The_0_10_3_points_match_exactly()
    {
        WhatsNew.Releases[5].Version.ShouldBe("0.10.3");
        PointsOf("0.10.3").ShouldBe([
            "Aero is now invite only. Enter your invite code in Settings, Look, to turn it on; everyone else uses Midnight.",
            "Aero looks like its film: the glass glows with the Windows 11 bloom. Settings, Glass, Behind the glass has Aero bloom, My desktop or Plain.",
            "The opening counts your figures up, and Replay intro and Play tour sit beside the window's buttons.",
            "Aero's panels, charts and spacing follow the approved design more closely.",
        ]);
        WhatsNew.Since("0.10.2", "0.10.3").ShouldBe(PointsOf("0.10.3"));
    }

    /// <summary>0.10.2 lifts the dark-theme frost over dark wallpaper.</summary>
    [Fact]
    public void The_0_10_2_points_match_exactly()
    {
        WhatsNew.Releases[6].Version.ShouldBe("0.10.2");
        PointsOf("0.10.2").ShouldBe([
            "Aero's sidebar and panels stay clear glass over dark parts of your wallpaper too.",
        ]);
        WhatsNew.Since("0.10.1", "0.10.2").ShouldBe(PointsOf("0.10.2"));
    }

    /// <summary>0.10.1 is Aero free-form over the desktop, brighter glass, and its CPU and intro fixes.</summary>
    [Fact]
    public void The_0_10_1_points_match_exactly()
    {
        WhatsNew.Releases[7].Version.ShouldBe("0.10.1");
        PointsOf("0.10.1").ShouldBe([
            "Aero floats on your desktop: only the glass is the window, and your desktop shows clearly between the panels.",
            "Brighter glass, closer to the demo. Increase contrast in Settings keeps the darker, easier to read glass.",
            "Aero uses far less of your PC while it is open, and its opening is smoother.",
        ]);
        WhatsNew.Since("0.10.0", "0.10.1").ShouldBe(PointsOf("0.10.1"));
    }

    /// <summary>0.10.0 is Plan S: Aero, its glass settings, Insights, the overlay, and pairing on Public Wi-Fi.</summary>
    [Fact]
    public void The_0_10_0_points_match_exactly()
    {
        WhatsNew.Releases[8].Version.ShouldBe("0.10.0");
        PointsOf("0.10.0").ShouldBe([
            "Aero, a new Liquid Glass look, is now the default, with a short tour the first time it opens. Switch look brings back Midnight or Classic.",
            "Settings has a Glass section: Clear, Tinted, Dark or your own colour, plus Reduce transparency and Increase contrast.",
            "Insights: your likely bill this month, hours of unusual use, when your PC idles most, and its carbon.",
            "A small glass overlay can show your watts over other windows.",
            "PCs on the same Wi-Fi find each other even when Windows calls the network Public: keep the Household page open on both.",
        ]);
        WhatsNew.Since("0.9.4", "0.10.0").ShouldBe(PointsOf("0.10.0"));
    }

    /// <summary>0.9.4 names the parts.</summary>
    [Fact]
    public void The_0_9_4_points_match_exactly()
    {
        WhatsNew.Releases[9].Version.ShouldBe("0.9.4");
        PointsOf("0.9.4").ShouldBe(["Where the power went names each part: your processor, graphics card, monitors, and memory and drives."]);
        WhatsNew.Since("0.9.3", "0.9.4").ShouldBe(PointsOf("0.9.4"));
    }

    /// <summary>0.9.3 brings back Restart to update and shows the version.</summary>
    [Fact]
    public void The_0_9_3_points_match_exactly()
    {
        WhatsNew.Releases[10].Version.ShouldBe("0.9.3");
        PointsOf("0.9.3").ShouldBe(["Check now downloads a new version again and offers Restart to update, and the Midnight sidebar shows the version you have."]);
        WhatsNew.Since("0.9.2", "0.9.3").ShouldBe(PointsOf("0.9.3"));
    }

    /// <summary>0.9.2 is updates that install at once.</summary>
    [Fact]
    public void The_0_9_2_points_match_exactly()
    {
        WhatsNew.Releases[11].Version.ShouldBe("0.9.2");
        PointsOf("0.9.2").ShouldBe(["Check now installs a new version straight away, and PowerLedger looks for new versions every 15 minutes."]);
        WhatsNew.Since("0.9.1", "0.9.2").ShouldBe(PointsOf("0.9.2"));
    }

    /// <summary>0.9.1 is the Energy used period button.</summary>
    [Fact]
    public void The_0_9_1_points_match_exactly()
    {
        WhatsNew.Releases[12].Version.ShouldBe("0.9.1");
        PointsOf("0.9.1").ShouldBe(["The Energy used card starts on Since start, and its button switches it to Today, This week or This month."]);
        WhatsNew.Since("0.9.0", "0.9.1").ShouldBe(PointsOf("0.9.1"));
    }

    /// <summary>0.9.0 is Plan Q: updates that install themselves, hourly sharing, and the household approval fix.</summary>
    [Fact]
    public void The_0_9_0_points_match_exactly()
    {
        WhatsNew.Releases[13].Version.ShouldBe("0.9.0");
        PointsOf("0.9.0").ShouldBe(
        [
            "PowerLedger now keeps itself up to date: new versions install on their own, and it reopens afterwards.",
            "If you share data, today's figures go every hour instead of once a day.",
            "Approving a PC into your household always shows the right list straight away.",
        ]);
        WhatsNew.Since("0.8.1", "0.9.0").ShouldBe(PointsOf("0.9.0"));
    }

    [Fact]
    public void The_0_8_1_points_match_exactly()
    {
        WhatsNew.Releases[14].Version.ShouldBe("0.8.1");
        PointsOf("0.8.1").ShouldBe(
        [
            "Every graphics card counts now, older NVIDIA cards such as the Quadro 6000 included, and a PC with several cards adds them all up.",
            "The new look is closer to its design: one bordered row of figures, indigo charts, and a Start service button when the service is off.",
            "What's new opens here in the app, and storage warnings show in the new look too.",
        ]);
        WhatsNew.Since("0.8.0", "0.8.1").ShouldBe(PointsOf("0.8.1"));
    }

    /// <summary>0.8.0 is the Midnight look, and says the classic one is a switch away (Midnight look design §1).</summary>
    [Fact]
    public void The_0_8_0_points_match_exactly()
    {
        WhatsNew.Releases[15].Version.ShouldBe("0.8.0");
        PointsOf("0.8.0").ShouldBe(
        [
            "A new look: a dashboard with your power, today's energy and idle waste at a glance. Prefer the classic look? Switch back any time in Settings → Preferences.",
            "The chart shows the last hour to all your history, with a tooltip for any moment.",
        ]);
    }

    [Fact]
    public void From_0_7_1_to_0_8_0_the_new_looks_points_come_alone()
        => WhatsNew.Since("0.7.1", "0.8.0").ShouldBe(PointsOf("0.8.0"));

    [Fact]
    public void From_0_7_0_to_0_8_0_both_releases_come_headed_newest_first()
    {
        var expected = new List<string> { "0.8.0" };
        expected.AddRange(PointsOf("0.8.0"));
        expected.Add("0.7.1");
        expected.AddRange(PointsOf("0.7.1"));
        WhatsNew.Since("0.7.0", "0.8.0").ShouldBe(expected);
    }
}
