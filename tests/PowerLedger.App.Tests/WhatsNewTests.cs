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

    [Fact]
    public void The_0_8_1_points_match_exactly_and_come_first()
    {
        WhatsNew.Releases[0].Version.ShouldBe("0.8.1", "newest first");
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
        WhatsNew.Releases[1].Version.ShouldBe("0.8.0");
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
