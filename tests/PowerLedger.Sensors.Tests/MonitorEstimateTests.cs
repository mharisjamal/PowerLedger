using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class MonitorEstimateTests
{
    private const string Header = "brand,model_number,model_name,alternatives,inches,width,height,panel,on_w,sleep_w,off_w,max_nits,hdr,certified";

    private static (double OnW, double SleepW, double OffW) For(double inches, int width, int height)
        => MonitorEstimate.For(inches, width, height, MonitorCatalogue.Shipped);

    /// <summary>The shipped table's four listings over 57 inches: two signage displays, an 85.5-inch display, and a 24-inch
    /// monitor whose size was entered in centimetres.</summary>
    private static MonitorCatalogue Televisions => MonitorCatalogue.Parse(new StringReader(string.Join("\n",
        Header,
        "Lenovo,Q24i-10,F19238FL0,F19238FX0|X24i-20,60.5,1080,1920,TFT LCD,13.51,0.2,0.19,265,,2019-07-18",
        "PHILIPS,65BDL3117P,65BDL3117P,,65,3840,2160,TFT LCD,151.86,0.79,0,700,,2022-11-07",
        "PHILIPS,65BDL4650D,65BDL4650D,,64.5,3840,2160,TFT LCD,124.21,0.73,0,500,,2023-05-18",
        "PLANAR,2E0I1,2E0I1,2E0I1yyyyy,85.5,3840,2160,IPS LCD,130.45,0.52,,500,,2026-01-09")));

    /// <summary>Three 27-inch 1080p monitors from the shipped table, which draw 0.09 W, 0.29 W and 0.14 W switched off.</summary>
    private static MonitorCatalogue TwentySevenInch => MonitorCatalogue.Parse(new StringReader(string.Join("\n",
        Header,
        "Acer,CB272,CB272,CB27******,27,1920,1080,IPS LCD,14.41,0.11,0.09,227.6,,2024-08-15",
        "Acer,CB273,CB273_v,CB273***,27,1920,1080,IPS LCD,17.07,0.35,0.29,250,,2025-04-22",
        "PHILIPS,27B2N2100,27B2N2100,,27,1920,1080,IPS LCD,13.48,0.17,0.14,250,,2024-07-17")));

    [Fact]
    public void Each_certified_monitor_estimated_from_all_the_others_lands_close_to_what_it_measured()
    {
        var monitors = MonitorCatalogue.Shipped.Monitors.Where(monitor => monitor.Inches <= MonitorEstimate.LargestMonitor).ToList();
        var errors = monitors
            .Select(held =>
            {
                var others = monitors.Where(monitor => !ReferenceEquals(monitor, held));
                return Math.Abs(MonitorEstimate.For(held.Inches, held.Width, held.Height, others).OnW - held.OnW) / held.OnW;
            })
            .Order()
            .ToList();

        MonitorCatalogue.Median(errors).ShouldBeLessThanOrEqualTo(0.12);
        errors[(int)Math.Ceiling(errors.Count * 0.9) - 1].ShouldBeLessThanOrEqualTo(0.35);
    }

    [Fact]
    public void A_24_inch_1080p_monitor_draws_what_the_hundreds_certified_like_it_draw()
    {
        var (on, sleep, off) = For(24, 1920, 1080);
        on.ShouldBeInRange(10, 16);
        sleep.ShouldBeInRange(0.1, 0.4);
        off.ShouldBeInRange(0.05, 0.3);
    }

    [Fact]
    public void An_estimated_monitor_draws_the_median_off_figure_of_the_monitors_like_it()
    {
        MonitorEstimate.For(27, 1920, 1080, TwentySevenInch).ShouldBe((14.41, 0.17, 0.14));
        MonitorEstimate.For(0, 0, 0, TwentySevenInch).ShouldBe((14.41, 0.17, 0.14));
    }

    [Fact]
    public void A_27_inch_4k_monitor_draws_more()
        => For(27, 3840, 2160).OnW.ShouldBeInRange(18, 30);

    [Fact]
    public void A_resolution_given_short_side_first_is_the_same_monitor()
    {
        For(23.8, 1080, 1920).ShouldBe(For(23.8, 1920, 1080));
        MonitorEstimate.Formula(49, 1080, 3840).ShouldBe(MonitorEstimate.Formula(49, 3840, 1080));
    }

    [Fact]
    public void A_portable_monitor_is_not_estimated_like_a_19_inch_desk_monitor()
    {
        For(15.6, 1920, 1080).OnW.ShouldBeInRange(4, 8);
        For(19.5, 1600, 900).OnW.ShouldBeInRange(8.5, 12);
    }

    [Fact]
    public void A_49_inch_super_ultrawide_with_too_few_like_it_listed_is_estimated_from_the_formula()
    {
        // The list has one 49-inch 3840 × 1080 monitor, Philips' 49B2U5300C at 33.72 W: too few for a median.
        var (on, sleep, off) = For(49, 3840, 1080);
        on.ShouldBe(MonitorEstimate.Formula(49, 3840, 1080));
        on.ShouldBe(34.2152, 0.0001);
        sleep.ShouldBe(0.2);
        off.ShouldBe(0.16);
    }

    [Theory]
    [InlineData(15.6, 1920, 1080, 7.6934)]    // under 190 square inches
    [InlineData(21.5, 1920, 1080, 12.1229)]   // 190 up to 210
    [InlineData(23.8, 1920, 1080, 13.0331)]   // 210 up to 315
    [InlineData(32, 3840, 2160, 28.9853)]     // 315 and over
    public void The_formula_follows_the_area_bands_of_energy_stars_allowance(double inches, int width, int height, double watts)
        => MonitorEstimate.Formula(inches, width, height).ShouldBe(watts, 0.0001);

    [Fact]
    public void The_formula_never_gives_less_than_3_watts()
        => MonitorEstimate.Formula(7, 800, 480).ShouldBe(3);

    [Theory]
    [InlineData(0, 1920, 1080)]
    [InlineData(-24, 1920, 1080)]
    [InlineData(double.NaN, 1920, 1080)]
    [InlineData(27, 0, 0)]
    [InlineData(27, 1920, 0)]
    public void A_monitor_that_does_not_give_its_size_or_resolution_takes_the_median_of_all(double inches, int width, int height)
    {
        var (on, sleep, off) = For(inches, width, height);
        on.ShouldBeInRange(13.5, 15);
        sleep.ShouldBeInRange(0.1, 0.4);
        off.ShouldBeInRange(0.05, 0.3);
    }

    [Fact]
    public void Televisions_listed_as_monitors_take_no_part_in_the_medians()
        // Taken into the 49-inch class, the three 4K screens would give their median, 130 W.
        => MonitorEstimate.For(49, 3840, 2160, Televisions).OnW.ShouldBe(MonitorEstimate.Formula(49, 3840, 2160));

    [Fact]
    public void A_screen_bigger_than_any_monitor_is_estimated_from_the_formula()
        // A 65-inch television used as a monitor would otherwise take the 49-inch super-ultrawides' median, 48 W.
        => For(65, 3840, 2160).ShouldBe((MonitorEstimate.Formula(65, 3840, 2160), 0.2, 0.16));

    [Fact]
    public void With_too_few_monitors_for_a_median_one_that_does_not_give_its_size_is_taken_for_the_commonest()
    {
        var (on, sleep, off) = MonitorEstimate.For(0, 0, 0, Televisions);
        on.ShouldBe(MonitorEstimate.Formula(23.8, 1920, 1080));
        sleep.ShouldBe(0.2);
        off.ShouldBe(0.16);
    }
}
