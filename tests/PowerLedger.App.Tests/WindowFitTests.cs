using Shouldly;

namespace PowerLedger.App.Tests;

public class WindowFitTests
{
    private static readonly Extent Size = new(1180, 900);
    private static readonly Extent Minimum = new(880, 600);

    private static WindowFit Fit(double left, double top, double width, double height)
        => WindowFit.Within(new Bounds(left, top, width, height), Size, Minimum) ?? throw new InvalidOperationException("The work area had no room.");

    [Theory]
    [InlineData(1536, 816)]   // 1920 × 1080 at 125%, where the window ran off the bottom of the screen
    [InlineData(1280, 672)]   // 1920 × 1080 at 150%
    [InlineData(1366, 720)]   // 1366 × 768 at 100%
    [InlineData(1440, 852)]   // 2880 × 1800 at 200%
    [InlineData(1280, 752)]   // 1280 × 800 at 100%
    public void A_screen_shorter_than_the_window_gets_a_window_as_tall_as_its_work_area_less_the_margins(double width, double height)
    {
        var fit = Fit(0, 0, width, height);
        fit.Bounds.ShouldBe(new Bounds((width - 1180) / 2, 8, 1180, height - 16));
        fit.Minimum.ShouldBe(Minimum);
    }

    [Fact]
    public void A_screen_shorter_than_the_minimum_lowers_the_minimum_so_the_whole_window_fits()
    {
        var fit = Fit(0, 0, 1092.8, 566);   // 1366 × 768 at 125%
        fit.Bounds.Left.ShouldBe(8, 1e-9);
        fit.Bounds.Top.ShouldBe(8);
        fit.Bounds.Width.ShouldBe(1076.8, 1e-9);
        fit.Bounds.Height.ShouldBe(550);
        fit.Minimum.ShouldBe(new Extent(880, 550));
    }

    [Fact]
    public void A_screen_narrower_than_the_minimum_lowers_the_minimum_width_too()
    {
        var fit = Fit(0, 0, 800, 552);   // 800 × 600 at 100%
        fit.Bounds.ShouldBe(new Bounds(8, 8, 784, 536));
        fit.Minimum.ShouldBe(new Extent(784, 536));
    }

    [Fact]
    public void A_big_screen_keeps_the_window_at_its_size_centred_in_the_work_area()
    {
        Fit(0, 0, 3840, 2112).ShouldBe(new WindowFit(new Bounds(1330, 606, 1180, 900), Minimum));   // 3840 × 2160 at 100%
        Fit(0, 0, 2560, 1392).ShouldBe(new WindowFit(new Bounds(690, 246, 1180, 900), Minimum));    // 3840 × 2160 at 150%
        Fit(0, 0, 1196, 916).ShouldBe(new WindowFit(new Bounds(8, 8, 1180, 900), Minimum));        // just room for it
    }

    [Fact]
    public void The_window_is_centred_in_a_work_area_away_from_the_origin()
    {
        Fit(48, 0, 1488, 864).Bounds.ShouldBe(new Bounds(202, 8, 1180, 848));            // the taskbar on the left of 1920 × 1080 at 125%
        Fit(1280, -160, 1280, 672).Bounds.ShouldBe(new Bounds(1330, -152, 1180, 656));   // a second screen, right of the first and higher up
    }

    [Fact]
    public void A_work_area_with_no_room_for_a_window_is_not_used()
    {
        WindowFit.Within(new Bounds(0, 0, 0, 0), Size, Minimum).ShouldBeNull();
        WindowFit.Within(new Bounds(0, 0, 16, 700), Size, Minimum).ShouldBeNull();
        WindowFit.Within(new Bounds(0, 0, double.NaN, 700), Size, Minimum).ShouldBeNull();
    }
}
