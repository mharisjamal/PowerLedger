using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

/// <summary>Every graphics card in the machine counts: each measured one as it measured, each other one from its own
/// rating and load, the way a card with no power sensor has always been worked out.</summary>
public class GpuCardTests
{
    private static PowerModel Desktop(MachineProfile? profile = null)
        => new(profile ?? MachineProfile.DefaultDesktop, HardwareFacts.DesktopDefaults, new PowerModelOptions(), new FixedBaseline(null));

    /// <summary>A desktop card's idle when nothing measures it: 4% of its rating, never under 3 W.</summary>
    private static double Idle(double rated) => Math.Max(PowerModel.GpuIdleW, PowerModel.DesktopGpuIdleShare * rated);

    [Fact]
    public void A_big_desktop_card_nothing_measures_idles_at_a_share_of_its_rating_and_a_laptops_at_3_W()
    {
        // TechPowerUp's card-only idle readings: an RTX 4090 about 21 W of its 450 W, a GTX 1080 about 7 W of 180 W.
        PowerModel.DesktopGpuIdleShare.ShouldBe(0.04);
        Desktop().Evaluate(With(new GpuCard("NVIDIA GeForce RTX 4090", null, 0, RatedW: 450))).Components.Gpu.ShouldBe(18, 1e-9);
        Desktop().Evaluate(With(new GpuCard("NVIDIA GeForce GT 1030", null, 0, RatedW: 30))).Components.Gpu.ShouldBe(3, 1e-9);
        Desktop().Evaluate(With(new GpuCard("NVIDIA GeForce RTX 4090", null, 1, RatedW: 450))).Components.Gpu.ShouldBe(450, 1e-9);

        var laptop = new PowerModel(MachineProfile.DefaultLaptop, HardwareFacts.LaptopDefaults, new PowerModelOptions(), new FixedBaseline(null));
        laptop.Evaluate(With(new GpuCard("NVIDIA GeForce RTX 4090 Laptop GPU", null, 0, RatedW: 140))).Components.Gpu.ShouldBe(3, 1e-9);
    }

    [Theory]
    [InlineData("AMD Radeon RX 580 2048SP")]
    [InlineData("Radeon RX 470 Series")]
    [InlineData("AMD Radeon RX Vega 64")]
    [InlineData("AMD Radeon VII")]
    [InlineData("AMD Radeon R9 290X")]
    [InlineData("AMD Radeon R9 Fury X")]
    [InlineData("AMD Radeon HD 7970")]
    [InlineData("AMD Radeon (TM) R9 200 Series")]
    [InlineData("AMD Radeon Pro WX 7100")]
    [InlineData("AMD FirePro W9100")]
    public void A_gcn_cards_chip_reading_counts_the_rest_of_the_card_at_forty_percent_more(string name)
    {
        PowerModel.GcnRestOfCardFactor.ShouldBe(1.4);
        Desktop().Evaluate(With(new GpuCard(name, 100, 0.5, GpuPowerScope.ChipOnly))).Components.Gpu.ShouldBe(140, 1e-9);
        Desktop().Evaluate(With(new GpuCard(name, 100, 0.5, GpuPowerScope.Board))).Components.Gpu.ShouldBe(100, 1e-9);
    }

    [Theory]
    [InlineData("AMD Radeon RX 5700 XT")]
    [InlineData("AMD Radeon RX 6800 XT")]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("AMD Radeon RX 5500M")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics")]
    public void Any_other_cards_chip_reading_counts_fifteen_percent_more(string name)
        => Desktop().Evaluate(With(new GpuCard(name, 100, 0.5, GpuPowerScope.ChipOnly))).Components.Gpu.ShouldBe(115, 1e-9);

    private static Sample With(params GpuCard[] cards)
    {
        var totals = GpuCard.Totals(cards);
        return TestData.Laptop(cpu: 50, brightness: null) with
        {
            DGpuPresent = totals.Present, DGpuW = totals.Watts, DGpuLoad = totals.Load, DGpuScope = totals.Scope, Gpus = cards,
        };
    }

    [Fact]
    public void Two_measured_cards_are_summed_each_with_its_own_scope()
    {
        var r = Desktop().Evaluate(With(
            new GpuCard("NVIDIA GeForce RTX 4070", 150, 0.6),
            new GpuCard("AMD Radeon RX 7800 XT", 80, 0.2, GpuPowerScope.ChipOnly)));

        r.Components.Gpu.ShouldBe(150 + 80 * PowerModel.RestOfCardFactor, 1e-9);
        r.Components.Sum.ShouldBe(r.TotalW, 1e-9);
    }

    [Fact]
    public void A_card_that_measures_nothing_is_worked_out_from_its_own_rating_not_the_machines()
    {
        // Feedback issue #4: a Quadro 6000 whose NVML gives utilisation but no power. The desktop default is 75 W.
        var r = Desktop().Evaluate(With(new GpuCard("Quadro 6000", Watts: null, Load: 0.5, RatedW: 204)));

        r.Components.Gpu.ShouldBe(Idle(204) + (204 - Idle(204)) * 0.5, 1e-9);
    }

    [Fact]
    public void A_measured_card_and_an_estimated_one_are_both_counted()
    {
        var r = Desktop().Evaluate(With(
            new GpuCard("Quadro RTX 4000", 60, 0.9),
            new GpuCard("Quadro 6000", null, 0.25, RatedW: 204)));

        r.Components.Gpu.ShouldBe(60 + Idle(204) + (204 - Idle(204)) * 0.25, 1e-9);
    }

    [Fact]
    public void A_card_with_no_load_yet_counts_as_idle_and_one_with_no_rating_takes_the_machines()
    {
        Desktop().Evaluate(With(new GpuCard("Quadro 6000", null, null, RatedW: 204))).Components.Gpu.ShouldBe(Idle(204), 1e-9);
        Desktop().Evaluate(With(new GpuCard("", null, 1.0))).Components.Gpu.ShouldBe(HardwareFacts.DesktopDefaults.GpuTdpW, 1e-9);
    }

    [Fact]
    public void The_users_figure_rates_the_only_card_and_with_several_only_those_without_a_rating()
    {
        var profile = MachineProfile.DefaultDesktop with { GpuTdpOverrideW = 150 };

        Desktop(profile).Evaluate(With(new GpuCard("Quadro 6000", null, 1.0, RatedW: 204))).Components.Gpu.ShouldBe(150, 1e-9);

        // One figure cannot be the rating of two different cards.
        Desktop(profile).Evaluate(With(
            new GpuCard("Quadro 6000", null, 1.0, RatedW: 204),
            new GpuCard("", null, 1.0))).Components.Gpu.ShouldBe(204 + 150, 1e-9);
    }

    [Fact]
    public void A_card_windows_has_switched_off_draws_nothing()
        => Desktop().Evaluate(With(new GpuCard("NVIDIA GeForce MX330", 0, 0, RatedW: 10))).Components.Gpu.ShouldBe(0);

    [Fact]
    public void A_sample_without_cards_is_worked_out_as_before()
    {
        Desktop().Evaluate(TestData.Laptop(cpu: 50, gpu: 120, brightness: null)).Components.Gpu.ShouldBe(120, 1e-9);
        Desktop().Evaluate(TestData.Laptop(gpu: null, gpuPresent: false) with { Gpus = [] }).Components.Gpu.ShouldBe(0);
    }

    [Fact]
    public void The_totals_have_watts_only_when_every_card_measured_its_own()
    {
        GpuCard.Totals([]).ShouldBe(new GpuTotals(false, null, null, GpuPowerScope.Board));

        var measured = GpuCard.Totals([new GpuCard("a", 150, 0.6), new GpuCard("b", 80, 0.2, GpuPowerScope.Package)]);
        measured.ShouldBe(new GpuTotals(true, 230, 0.6, GpuPowerScope.Package));

        var chip = GpuCard.Totals([new GpuCard("a", 150, null, GpuPowerScope.Package), new GpuCard("b", 80, null, GpuPowerScope.ChipOnly)]);
        chip.Scope.ShouldBe(GpuPowerScope.ChipOnly);
        chip.Load.ShouldBeNull();

        var mixed = GpuCard.Totals([new GpuCard("a", 150, 0.1), new GpuCard("b", null, 0.7, RatedW: 204)]);
        mixed.ShouldBe(new GpuTotals(true, null, 0.7, GpuPowerScope.Board));

        GpuCard.Totals([new GpuCard("a", double.NaN, null)]).Watts.ShouldBeNull();
    }
}
