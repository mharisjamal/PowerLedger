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
