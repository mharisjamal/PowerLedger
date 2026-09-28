using System.Windows;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Aero look design §5: the overlay sits in a corner of a work area, or where it was dragged, clamped to the work area of
/// the display it is nearest, so a display unplugged or rescaled never strands it off screen. Displays are given as
/// Windows lays them out, in pixels on the virtual screen, with each one's scale; the pill is 150 by 44 device-independent
/// pixels in every case here.
/// </summary>
public class OverlayPlacementTests
{
    private static readonly Size Pill = new(150, 44);

    /// <summary>A 1920 by 1080 display at 100 %, the taskbar's 40 pixels along the bottom.</summary>
    private static readonly OverlayDisplay Main100 = new(new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1040), 1, Primary: true);

    /// <summary>A 2880 by 1620 display at 150 %, the taskbar's 60 pixels along the bottom.</summary>
    private static readonly OverlayDisplay Main150 = new(new Rect(0, 0, 2880, 1620), new Rect(0, 0, 2880, 1560), 1.5, Primary: true);

    /// <summary>A second display at 150 %, right of <see cref="Main100"/>, with no taskbar.</summary>
    private static readonly OverlayDisplay Right150 = new(new Rect(1920, 0, 2880, 1620), new Rect(1920, 0, 2880, 1620), 1.5, Primary: false);

    /// <summary>A second display at 100 %, left of the main one, so on negative coordinates.</summary>
    private static readonly OverlayDisplay Left100 = new(new Rect(-1920, 0, 1920, 1080), new Rect(-1920, 0, 1920, 1080), 1, Primary: false);

    private static OverlaySettings At(OverlayPosition position, double? left = null, double? top = null)
        => OverlaySettings.Default with { Enabled = true, Position = position, Left = left, Top = top };

    [Theory]
    [InlineData("TopLeft", 16, 16)]
    [InlineData("TopRight", 1754, 16)]
    [InlineData("BottomLeft", 16, 980)]
    [InlineData("BottomRight", 1754, 980)]
    public void On_one_display_at_100_percent_a_corner_is_16_pixels_in_from_the_work_areas_edges(string corner, double x, double y)
    {
        var place = OverlayPlacement.Place(At(Enum.Parse<OverlayPosition>(corner)), [Main100], Pill);

        place.Pill.ShouldBe(new Point(x, y));
        place.Scale.ShouldBe(1);
    }

    /// <summary>At 150 % the inset and the pill are half as big again in pixels: 24 in, 225 by 66.</summary>
    [Theory]
    [InlineData("TopLeft", 24, 24)]
    [InlineData("TopRight", 2631, 24)]
    [InlineData("BottomLeft", 24, 1470)]
    [InlineData("BottomRight", 2631, 1470)]
    public void On_one_display_at_150_percent_the_corner_is_scaled_with_it(string corner, double x, double y)
    {
        var place = OverlayPlacement.Place(At(Enum.Parse<OverlayPosition>(corner)), [Main150], Pill);

        place.Pill.ShouldBe(new Point(x, y));
        place.Scale.ShouldBe(1.5);
    }

    [Fact]
    public void A_corner_with_no_place_remembered_is_on_the_main_display()
    {
        var place = OverlayPlacement.Place(At(OverlayPosition.TopRight), [Right150, Main100], Pill);

        place.Pill.ShouldBe(new Point(1754, 16));
        place.Scale.ShouldBe(1);
    }

    /// <summary>The corner of the display the overlay was last on: chosen from the menu after dragging it to the second
    /// display, it goes to that display's corner, at that display's scale.</summary>
    [Fact]
    public void A_corner_is_on_the_display_nearest_the_place_last_remembered()
    {
        var place = OverlayPlacement.Place(At(OverlayPosition.TopRight, left: 2500, top: 100), [Main100, Right150], Pill);

        place.Pill.ShouldBe(new Point(1920 + 2880 - 24 - 225, 24));
        place.Scale.ShouldBe(1.5);
    }

    [Fact]
    public void A_free_place_on_the_second_display_stays_where_it_was_dragged()
    {
        var place = OverlayPlacement.Place(At(OverlayPosition.Free, left: 2500, top: 100), [Main100, Right150], Pill);

        place.Pill.ShouldBe(new Point(2500, 100));
        place.Scale.ShouldBe(1.5);
    }

    [Fact]
    public void A_free_place_on_a_display_left_of_the_main_one_stays_on_it()
    {
        var place = OverlayPlacement.Place(At(OverlayPosition.Free, left: -1000, top: 300), [Main100, Left100], Pill);

        place.Pill.ShouldBe(new Point(-1000, 300));
        place.Scale.ShouldBe(1);
    }

    /// <summary>The second display unplugged: the overlay comes back to the edge of the one left, not off screen.</summary>
    [Fact]
    public void A_free_place_on_a_display_since_unplugged_is_clamped_into_the_nearest_work_area()
    {
        var place = OverlayPlacement.Place(At(OverlayPosition.Free, left: 4000, top: 500), [Main100], Pill);

        place.Pill.ShouldBe(new Point(1920 - 150, 500));
    }

    [Fact]
    public void A_free_place_partly_off_a_work_area_is_pulled_back_inside_it()
    {
        var below = OverlayPlacement.Place(At(OverlayPosition.Free, left: 2600, top: 1600), [Main100, Right150], Pill);
        var over = OverlayPlacement.Place(At(OverlayPosition.Free, left: -40, top: -30), [Main100, Right150], Pill);

        below.Pill.ShouldBe(new Point(2600, 1620 - 66), "clamped at the second display's scale");
        over.Pill.ShouldBe(new Point(0, 0));
    }

    /// <summary>The taskbar is out of a work area: a place dragged over it is lifted above it.</summary>
    [Fact]
    public void A_free_place_over_the_taskbar_is_lifted_above_it()
        => OverlayPlacement.Place(At(OverlayPosition.Free, left: 500, top: 1030), [Main100], Pill).Pill.ShouldBe(new Point(500, 1040 - 44));

    [Fact]
    public void Free_before_it_was_ever_dragged_is_the_default_corner()
        => OverlayPlacement.Place(At(OverlayPosition.Free), [Main100], Pill).Pill.ShouldBe(new Point(1754, 16));

    /// <summary>A place is remembered in device-independent pixels of the main display's scale, one linear map for the
    /// whole virtual screen, so it comes back to the same pixel whatever display it is on.</summary>
    [Fact]
    public void A_place_is_remembered_at_the_main_displays_scale_and_comes_back_to_the_same_pixel()
    {
        var mainAt150 = Main150;
        var right = new OverlayDisplay(new Rect(2880, 0, 1920, 1080), new Rect(2880, 0, 1920, 1080), 1, Primary: false);

        var (left, top) = OverlayPlacement.Remember(new Point(3000, 150), [right, mainAt150]);

        left.ShouldBe(2000);
        top.ShouldBe(100);
        OverlayPlacement.Place(At(OverlayPosition.Free, left, top), [right, mainAt150], Pill).Pill.ShouldBe(new Point(3000, 150));
    }

    [Fact]
    public void The_window_sits_its_room_around_the_pill_further_out()
    {
        var place = OverlayPlacement.Place(At(OverlayPosition.TopLeft), [Main150], Pill);

        place.Window(margin: 18).ShouldBe(new Point(24 - 27, 24 - 27));
    }

    [Fact]
    public void With_no_display_listed_it_falls_back_to_the_origin_at_100_percent()
        => OverlayPlacement.Place(At(OverlayPosition.BottomRight), [], Pill).ShouldBe(new OverlayPlace(new Point(16, 16), 1));
}
