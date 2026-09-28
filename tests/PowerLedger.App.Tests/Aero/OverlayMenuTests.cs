using System.Windows;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Aero look design §5: the overlay's right-click menu offers the four corners or Free, the opacity, the sparkline and
/// Close, each a new <see cref="OverlaySettings"/> saved through <see cref="SettingsViewModel.Overlay"/>. The sparkline
/// shows the last 30 seconds of <see cref="NowViewModel.Live"/>.
/// </summary>
public class OverlayMenuTests
{
    private static readonly OverlaySettings On = OverlaySettings.Default with { Enabled = true };

    private static OverlayChoice Item(OverlaySettings settings, string header)
        => OverlayMenu.Items(settings, (640, 20)).Single(i => i.Header == header);

    [Fact]
    public void It_lists_the_corners_free_the_opacities_the_sparkline_and_close()
        => OverlayMenu.Items(On, (0, 0)).Select(i => i.Header).ShouldBe(
        [
            "Position", "Top left", "Top right", "Bottom left", "Bottom right", "Free (drag it anywhere)", "",
            "Opacity", "100%", "85%", "70%", "55%", "",
            "Show the last 30 seconds", "Close overlay",
        ]);

    [Fact]
    public void The_current_corner_opacity_and_sparkline_are_ticked()
    {
        var settings = On with { Position = OverlayPosition.BottomLeft, Opacity = 0.7, Sparkline = true };

        OverlayMenu.Items(settings, (0, 0)).Where(i => i.Checked).Select(i => i.Header)
            .ShouldBe(["Bottom left", "70%", "Show the last 30 seconds"]);
    }

    /// <summary>A corner keeps the place the overlay is at, so the corner chosen is on the display it is on
    /// (<see cref="OverlayPlacement"/>).</summary>
    [Fact]
    public void A_corner_moves_it_there_on_the_display_it_is_on()
        => Item(On, "Bottom right").Apply!(On).ShouldBe(On with { Position = OverlayPosition.BottomRight, Left = 640, Top = 20 });

    [Fact]
    public void Free_keeps_it_where_it_is_until_dragged()
        => Item(On, "Free (drag it anywhere)").Apply!(On).ShouldBe(On with { Position = OverlayPosition.Free, Left = 640, Top = 20 });

    [Fact]
    public void An_opacity_is_chosen()
        => Item(On, "55%").Apply!(On).ShouldBe(On with { Opacity = 0.55 });

    [Fact]
    public void The_sparkline_is_turned_off_and_on()
    {
        var off = Item(On, "Show the last 30 seconds").Apply!(On);

        off.ShouldBe(On with { Sparkline = false });
        Item(off, "Show the last 30 seconds").Apply!(off).ShouldBe(On);
    }

    [Fact]
    public void Close_turns_it_off_keeping_the_rest()
        => Item(On with { Position = OverlayPosition.Free, Left = 5, Top = 6 }, "Close overlay").Apply!(On with { Position = OverlayPosition.Free, Left = 5, Top = 6 })
            .ShouldBe(OverlaySettings.Default with { Position = OverlayPosition.Free, Left = 5, Top = 6 });

    [Fact]
    public void Headings_and_separators_do_nothing()
        => OverlayMenu.Items(On, (0, 0)).Where(i => i.Header is "" or "Position" or "Opacity").ShouldAllBe(i => i.Apply == null);

    [Fact]
    public void The_sparkline_draws_the_last_30_seconds_across_its_width_newest_at_the_right()
    {
        SparkSample[] samples = [new(45, 999), new(30, 100), new(15, 200), new(0, 150)];

        var points = Aero.Sparkline.Points(samples, 30, new Size(60, 20));

        points.Select(p => p.X).ShouldBe([0, 30, 60]);
        points.Select(p => p.Y).ShouldBe([19, 1, 10], "the lowest at the foot, the highest at the top, a pixel in");
    }

    [Fact]
    public void A_flat_or_single_reading_sits_mid_height()
    {
        Aero.Sparkline.Points([new(2, 80), new(1, 80), new(0, 80)], 30, new Size(60, 20)).ShouldAllBe(p => p.Y == 10);
        Aero.Sparkline.Points([new(0, 80)], 30, new Size(60, 20)).ShouldBe([new Point(60, 10)]);
        Aero.Sparkline.Points([], 30, new Size(60, 20)).ShouldBeEmpty();
    }
}
