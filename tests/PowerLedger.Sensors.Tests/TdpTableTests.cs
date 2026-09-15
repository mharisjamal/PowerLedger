using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class TdpTableTests
{
    [Fact]
    public void A_windows_cpu_name_matches_the_bare_model_in_the_table()
    {
        var table = TdpTable.Bundled;
        table.Cpu("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz").ShouldBe(28);
        table.Cpu("Intel(R) Core(TM) i7-1165G7").ShouldBe(28);
    }

    [Fact]
    public void A_gpu_name_matches_the_same_way()
        => TdpTable.Bundled.Gpu("NVIDIA GeForce MX330").ShouldBe(10);

    [Fact]
    public void An_unknown_model_has_no_answer_so_the_caller_can_choose_its_own_default()
    {
        TdpTable.Bundled.Cpu("Some Processor 9000").ShouldBeNull();
        TdpTable.Bundled.Gpu("Imaginary GPU").ShouldBeNull();
        TdpTable.Bundled.Cpu(null).ShouldBeNull();
        TdpTable.Bundled.Cpu("").ShouldBeNull();
    }

    [Fact]
    public void The_longest_matching_key_wins_so_a_suffix_beats_its_prefix()
    {
        var table = new TdpTable(
            new Dictionary<string, double> { ["i7-1165"] = 15, ["i7-1165G7"] = 28 },
            new Dictionary<string, double>());

        table.Cpu("Intel Core i7-1165G7").ShouldBe(28);
    }

    [Fact]
    public void The_longest_matching_key_wins_wherever_the_table_lists_it()
    {
        var table = new TdpTable(
            new Dictionary<string, double>(),
            new Dictionary<string, double> { ["RX 7900 XTX"] = 355, ["RX 7900 XT"] = 315 });

        table.Gpu("AMD Radeon RX 7900 XTX").ShouldBe(355);
        table.Gpu("AMD Radeon RX 7900 XT").ShouldBe(315);
    }

    [Theory]
    [InlineData("AMD Radeon RX 7900 XTX", 355)]
    [InlineData("AMD Radeon RX 7900 XT", 315)]
    [InlineData("AMD Radeon RX 7900 GRE", 260)]
    [InlineData("AMD Radeon RX 7600 XT", 190)]
    [InlineData("AMD Radeon RX 7600", 165)]
    [InlineData("AMD Radeon RX 9070 XT", 304)]
    [InlineData("AMD Radeon RX 9070", 220)]
    [InlineData("AMD Radeon RX 6800 XT", 300)]
    [InlineData("AMD Radeon RX 6800", 250)]
    [InlineData("AMD Radeon RX 6700 XT", 230)]
    [InlineData("AMD Radeon RX 6700", 175)]
    [InlineData("AMD Radeon RX 6600 XT", 160)]
    [InlineData("AMD Radeon RX 6600", 132)]
    [InlineData("AMD Radeon RX 5700 XT", 225)]
    [InlineData("AMD Radeon RX 5700", 180)]
    [InlineData("NVIDIA GeForce RTX 4060 Ti", 160)]
    [InlineData("NVIDIA GeForce RTX 4060", 115)]
    [InlineData("NVIDIA GeForce RTX 4070 Ti SUPER", 285)]
    [InlineData("NVIDIA GeForce RTX 4070 SUPER", 220)]
    [InlineData("NVIDIA GeForce RTX 4070 Ti", 285)]
    [InlineData("NVIDIA GeForce RTX 3060 Ti", 200)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", 125)]
    public void A_variant_is_rated_as_itself_not_as_the_model_its_name_begins_with(string name, double watts)
        => TdpTable.Bundled.Gpu(name).ShouldBe(watts);

    [Theory]
    [InlineData("AMD Radeon RX 6800M", 145)]
    [InlineData("AMD Radeon RX 6800S", 100)]
    [InlineData("AMD Radeon RX 6700M", 135)]
    [InlineData("AMD Radeon(TM) RX 6700S", 80)]
    [InlineData("AMD Radeon RX 6600M", 100)]
    [InlineData("AMD Radeon RX 6600S", 80)]
    [InlineData("AMD Radeon RX 7600M XT", 120)]
    [InlineData("AMD Radeon RX 7600M", 90)]
    [InlineData("AMD Radeon RX 7600S", 75)]
    [InlineData("Intel(R) Arc(TM) A770M Graphics", 150)]
    public void A_laptop_card_is_not_rated_as_the_desktop_card_its_name_begins_with(string name, double watts)
        => TdpTable.Bundled.Gpu(name).ShouldBe(watts);

    [Theory]
    [InlineData("AMD Radeon RX 9060 XT", 160)]
    [InlineData("AMD Radeon RX 7800 XT", 263)]
    [InlineData("AMD Radeon RX 7700 XT", 245)]
    [InlineData("AMD Radeon RX 6950 XT", 335)]
    [InlineData("AMD Radeon RX 6900 XT", 300)]
    [InlineData("AMD Radeon RX 6750 XT", 250)]
    [InlineData("AMD Radeon RX 6650 XT", 180)]
    [InlineData("AMD Radeon RX 6500 XT", 107)]
    [InlineData("AMD Radeon RX 6400", 53)]
    [InlineData("AMD Radeon RX 5600 XT", 150)]
    [InlineData("AMD Radeon RX 5500 XT", 130)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", 225)]
    [InlineData("Intel(R) Arc(TM) A750 Graphics", 225)]
    [InlineData("Intel(R) Arc(TM) A580 Graphics", 185)]
    [InlineData("Intel(R) Arc(TM) A380 Graphics", 75)]
    [InlineData("Intel(R) Arc(TM) A310 Graphics", 75)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", 190)]
    [InlineData("Intel(R) Arc(TM) B570 Graphics", 150)]
    [InlineData("Intel(R) Arc(TM) A730M Graphics", 120)]
    [InlineData("Intel(R) Arc(TM) A370M Graphics", 50)]
    public void Common_amd_and_intel_cards_are_rated_by_their_board_power(string name, double watts)
        => TdpTable.Bundled.Gpu(name).ShouldBe(watts);

    [Fact]
    public void Matching_ignores_case()
        => TdpTable.Bundled.Cpu("intel core i7-1165g7").ShouldBe(28);

    [Fact]
    public void The_bundled_table_is_loaded_once_and_holds_real_entries()
    {
        TdpTable.Bundled.ShouldBeSameAs(TdpTable.Bundled);
        TdpTable.Bundled.CpuCount.ShouldBeGreaterThan(5);
        TdpTable.Bundled.GpuCount.ShouldBeGreaterThan(3);
    }
}
