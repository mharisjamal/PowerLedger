using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class DisplayModelTests
{
    [Fact]
    public void Laptop_panel_at_sixty_percent_on_a_15_inch_screen_is_4_2_watts()
        => DisplayModel.PanelWatts(MachineProfile.DefaultLaptop, 0.6, displayOn: true).ShouldBe(4.2, 0.001);

    [Fact]
    public void Small_panels_scale_down_and_large_panels_scale_up()
    {
        var small = MachineProfile.DefaultLaptop with { DisplayDiagonalInches = 13.3 };
        var large = MachineProfile.DefaultLaptop with { DisplayDiagonalInches = 17.3 };
        DisplayModel.PanelWatts(small, 1.0, true).ShouldBe(6.0 * 0.8, 0.001);
        DisplayModel.PanelWatts(large, 1.0, true).ShouldBe(6.0 * 1.3, 0.001);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(13.3, 0.8)]
    [InlineData(14.0, 0.8)]
    [InlineData(14.1, 1.0)]
    [InlineData(16.9, 1.0)]
    [InlineData(17.0, 1.3)]
    public void Size_factor_boundaries(double diagonal, double expected)
        => DisplayModel.SizeFactor(diagonal).ShouldBe(expected);

    [Fact]
    public void Panel_is_zero_when_display_is_off_or_machine_is_a_desktop()
    {
        DisplayModel.PanelWatts(MachineProfile.DefaultLaptop, 0.6, displayOn: false).ShouldBe(0);
        DisplayModel.PanelWatts(MachineProfile.DefaultDesktop, 0.6, displayOn: true).ShouldBe(0);
    }

    [Fact]
    public void Unknown_brightness_assumes_fifty_percent()
        => DisplayModel.PanelWatts(MachineProfile.DefaultLaptop, null, true).ShouldBe(1.5 + 4.5 * 0.5, 0.001);

    [Fact]
    public void NaN_brightness_counts_as_fifty_percent()
        => DisplayModel.PanelWatts(MachineProfile.DefaultLaptop, double.NaN, true).ShouldBe(3.75, 0.001);

    [Fact]
    public void External_monitors_count_only_when_opted_in()
    {
        var two = MachineProfile.DefaultDesktop with { ExternalMonitors = 2, IncludeMonitors = true, MonitorWatts = 25 };
        DisplayModel.MonitorWatts(two, displayOn: true).ShouldBe(50);
        DisplayModel.MonitorWatts(two, displayOn: false).ShouldBe(1.0, 0.001);
        DisplayModel.MonitorWatts(MachineProfile.DefaultDesktop, displayOn: true).ShouldBe(0);
    }

    [Theory]
    [InlineData(PsuTier.White, 0.82)]
    [InlineData(PsuTier.Bronze, 0.85)]
    [InlineData(PsuTier.Silver, 0.87)]
    [InlineData(PsuTier.Gold, 0.90)]
    [InlineData(PsuTier.Platinum, 0.92)]
    [InlineData(PsuTier.Titanium, 0.94)]
    public void Psu_efficiency_by_tier(PsuTier tier, double expected)
        => PsuEfficiency.For(tier).ShouldBe(expected);

    [Fact]
    public void Unknown_psu_tier_falls_back_to_bronze()
        => PsuEfficiency.For((PsuTier)99).ShouldBe(0.85);
}
