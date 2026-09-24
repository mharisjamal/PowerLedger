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
    public void Quadro_6000_and_quadro_rtx_6000_are_told_apart()
    {
        // Feedback issue #4: a Fermi Quadro 6000 (2010, 204 W) and the Turing Quadro RTX 6000 (2018, 295 W).
        var table = TdpTable.Bundled;
        table.Gpu("NVIDIA Quadro 6000").ShouldBe(204);
        table.Gpu("Quadro 6000").ShouldBe(204);                  // as an R390 driver's NVML names it
        table.Gpu("NVIDIA Quadro RTX 6000").ShouldBe(295);
        table.Gpu("Quadro RTX 6000").ShouldBe(295);
        table.Gpu("NVIDIA RTX 6000 Ada Generation").ShouldBe(300);
        table.Gpu("NVIDIA RTX A6000").ShouldBe(300);
        table.Gpu("NVIDIA Quadro 600").ShouldBe(40);

        // Neither key is inside the other name, so a table holding only one of them rates only that one.
        var onlyFermi = new TdpTable(new Dictionary<string, double>(), new Dictionary<string, double> { ["Quadro 6000"] = 204 });
        onlyFermi.Gpu("NVIDIA Quadro RTX 6000").ShouldBeNull();
        var onlyTuring = new TdpTable(new Dictionary<string, double>(), new Dictionary<string, double> { ["Quadro RTX 6000"] = 295 });
        onlyTuring.Gpu("NVIDIA Quadro 6000").ShouldBeNull();
    }

    [Fact]
    public void A_key_that_ends_in_a_number_does_not_match_a_longer_number()
    {
        var table = new TdpTable(
            new Dictionary<string, double> { ["E5-1650"] = 130 },
            new Dictionary<string, double> { ["Quadro 600"] = 40, ["RX 570"] = 150, ["GTX 680"] = 195 });

        table.Gpu("NVIDIA Quadro 6000").ShouldBeNull();          // a 204 W card is not a 40 W one
        table.Gpu("AMD Radeon RX 5700").ShouldBeNull();
        table.Gpu("NVIDIA Quadro 600").ShouldBe(40);
        table.Gpu("Radeon RX 570 Series").ShouldBe(150);
        table.Gpu("NVIDIA GeForce GTX 680M").ShouldBe(195);      // a letter after it is still the model it begins with
        table.Cpu("Intel(R) Xeon(R) CPU E5-1650 0 @ 3.20GHz").ShouldBe(130);
        table.Cpu("Intel(R) Xeon(R) CPU E5-16500").ShouldBeNull();
    }

    [Theory]
    [InlineData("Intel(R) Xeon(R) CPU E5-1650 0 @ 3.20GHz", 130)]
    [InlineData("Intel(R) Xeon(R) CPU E5-1650 v2 @ 3.50GHz", 130)]
    [InlineData("Intel(R) Xeon(R) CPU E5-1650 v3 @ 3.50GHz", 140)]
    [InlineData("Intel(R) Xeon(R) CPU E5-1650 v4 @ 3.60GHz", 140)]
    [InlineData("Intel(R) Xeon(R) CPU E5-2670 0 @ 2.60GHz", 115)]
    [InlineData("Intel(R) Xeon(R) CPU E5-2690 v4 @ 2.60GHz", 135)]
    [InlineData("Intel(R) Xeon(R) CPU E3-1230 V2 @ 3.30GHz", 69)]
    [InlineData("Intel(R) Xeon(R) CPU E3-1231 v3 @ 3.40GHz", 80)]
    [InlineData("Intel(R) Xeon(R) CPU E3-1230L v3 @ 1.80GHz", 25)]
    [InlineData("Intel(R) Xeon(R) CPU E7-8890 v4 @ 2.20GHz", 165)]
    [InlineData("Intel(R) Xeon(R) CPU X5670 @ 2.93GHz", 95)]
    [InlineData("Intel(R) Xeon(R) CPU W3690 @ 3.47GHz", 130)]
    [InlineData("Intel(R) Xeon(R) W-2135 CPU @ 3.70GHz", 140)]
    [InlineData("Intel(R) Xeon(R) W-2295 CPU @ 3.00GHz", 165)]
    [InlineData("Intel(R) Xeon(R) w9-3495X", 350)]
    [InlineData("Intel(R) Core(TM) i7 CPU         920  @ 2.67GHz", 130)]   // the first Core i7s, padded as their CPUID has it
    [InlineData("Intel(R) Core(TM) i5 CPU 750 @ 2.67GHz", 95)]
    [InlineData("Intel(R) Core(TM) i7-2600K CPU @ 3.40GHz", 95)]
    [InlineData("Intel(R) Core(TM) i5-3570 CPU @ 3.40GHz", 77)]
    [InlineData("Intel(R) Core(TM) i7-4790K CPU @ 4.00GHz", 88)]
    [InlineData("Intel(R) Core(TM) i7-4790 CPU @ 3.60GHz", 84)]
    [InlineData("Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz", 91)]
    [InlineData("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", 95)]
    [InlineData("Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz", 95)]
    [InlineData("Intel(R) Core(TM) i5-9400F CPU @ 2.90GHz", 65)]
    [InlineData("Intel(R) Core(TM) i9-10900K CPU @ 3.70GHz", 125)]
    [InlineData("Intel(R) Core(TM) i7-4710HQ CPU @ 2.50GHz", 47)]
    public void Workstation_and_older_processors_are_rated(string name, double watts)
        => TdpTable.Bundled.Cpu(name).ShouldBe(watts);

    [Theory]
    [InlineData("NVIDIA GeForce GTX 480", 250)]
    [InlineData("GeForce GTX 580", 244)]
    [InlineData("NVIDIA GeForce GTX 560 Ti", 170)]
    [InlineData("NVIDIA GeForce GTX 680", 195)]
    [InlineData("NVIDIA GeForce GTX 780 Ti", 250)]
    [InlineData("NVIDIA GeForce GTX TITAN Z", 375)]
    [InlineData("NVIDIA GeForce GTX 980", 165)]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", 250)]
    [InlineData("NVIDIA GeForce GTX 1080", 180)]
    [InlineData("NVIDIA GeForce GTX 1050 Ti", 75)]
    [InlineData("NVIDIA GeForce RTX 2080 Ti", 250)]
    [InlineData("NVIDIA GeForce RTX 3090 Ti", 450)]
    [InlineData("NVIDIA GeForce RTX 5090", 575)]
    [InlineData("NVIDIA GeForce RTX 5060 Ti", 180)]
    [InlineData("NVIDIA Quadro FX 5800", 189)]
    [InlineData("Quadro FX 3800", 108)]
    [InlineData("NVIDIA Quadro 4000", 142)]
    [InlineData("NVIDIA Quadro 7000", 204)]
    [InlineData("NVIDIA Quadro K2000", 51)]
    [InlineData("NVIDIA Quadro K6000", 225)]
    [InlineData("NVIDIA Quadro M6000", 250)]
    [InlineData("NVIDIA Quadro P400", 30)]
    [InlineData("NVIDIA Quadro P4000", 105)]
    [InlineData("NVIDIA Quadro RTX 8000", 295)]
    [InlineData("NVIDIA RTX A2000", 70)]
    [InlineData("NVIDIA RTX A4000", 140)]
    [InlineData("NVIDIA RTX 4000 SFF Ada Generation", 70)]
    [InlineData("NVIDIA RTX 4000 Ada Generation", 130)]
    [InlineData("Tesla K80", 300)]
    [InlineData("Tesla P4", 75)]
    [InlineData("Tesla P40", 250)]
    [InlineData("Tesla V100-PCIE-16GB", 250)]
    [InlineData("NVIDIA Tesla T4", 70)]
    [InlineData("NVIDIA A10", 150)]
    [InlineData("NVIDIA A100-PCIE-40GB", 250)]
    [InlineData("NVIDIA L4", 72)]
    [InlineData("NVIDIA L40S", 350)]
    [InlineData("ATI Radeon HD 5870", 188)]
    [InlineData("ATI Radeon HD 5800 Series", 188)]
    [InlineData("AMD Radeon HD 6970", 250)]
    [InlineData("AMD Radeon HD 7900 Series", 250)]
    [InlineData("AMD Radeon HD 7850", 130)]
    [InlineData("AMD Radeon R9 290X", 290)]
    [InlineData("AMD Radeon R9 200 Series", 290)]
    [InlineData("AMD Radeon (TM) R9 390 Series", 275)]
    [InlineData("Radeon (TM) RX 480 Graphics", 150)]
    [InlineData("Radeon RX 580 Series", 185)]
    [InlineData("Radeon RX 570 Series", 150)]
    [InlineData("AMD Radeon VII", 300)]
    [InlineData("AMD FirePro W9100", 275)]
    [InlineData("Radeon Pro WX 7100 Graphics", 130)]
    [InlineData("AMD Radeon PRO W7900", 295)]
    [InlineData("AMD Radeon(TM) PRO W6800", 250)]
    [InlineData("Intel(R) Arc(TM) Pro A60 Graphics", 130)]
    public void Older_workstation_and_datacenter_cards_are_rated_by_their_published_board_power(string name, double watts)
        => TdpTable.Bundled.Gpu(name).ShouldBe(watts);

    [Fact]
    public void A_processors_graphics_are_never_rated_as_a_card()
    {
        // "RX Vega" alone would rate a Ryzen's built-in "RX Vega 11 Graphics" as a 295 W card.
        TdpTable.Bundled.Gpu("AMD Radeon(TM) RX Vega 11 Graphics").ShouldBeNull();
        TdpTable.Bundled.Gpu("Intel(R) HD Graphics 5000").ShouldBeNull();
    }

    [Theory]
    [InlineData(0UL, 75)]                   // memory unknown
    [InlineData(1UL << 30, 75)]
    [InlineData(2UL << 30, 75)]
    [InlineData((2UL << 30) + 1, 120)]
    [InlineData(4UL << 30, 120)]
    [InlineData(6UL << 30, 180)]
    [InlineData(8UL << 30, 180)]
    [InlineData(12UL << 30, 250)]
    [InlineData(48UL << 30, 250)]
    public void A_card_the_table_does_not_know_gets_a_rough_figure_from_its_memory(ulong bytes, double watts)
        => TdpTable.GuessFromMemory(bytes).ShouldBe(watts);

    [Fact]
    public void A_cards_rating_says_whether_it_is_published_or_guessed()
    {
        TdpTable.Bundled.GpuOrGuess("NVIDIA Quadro 6000", 6UL << 30).ShouldBe(new GpuRating(204, Rough: false));
        TdpTable.Bundled.GpuOrGuess("Matrox Imaginary 9000", 6UL << 30).ShouldBe(new GpuRating(180, Rough: true));
        TdpTable.Bundled.GpuOrGuess(null, 0).ShouldBe(new GpuRating(75, Rough: true));
    }

    [Fact]
    public void The_bundled_table_has_no_key_twice_and_only_plausible_watts()
    {
        // A key written twice would quietly keep only the second figure.
        using var stream = typeof(TdpTable).Assembly.GetManifestResourceStream("PowerLedger.Sensors.tdp-table.json").ShouldNotBeNull();
        using var json = System.Text.Json.JsonDocument.Parse(stream);
        foreach (var part in new[] { "cpu", "gpu" })
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in json.RootElement.GetProperty(part).EnumerateObject())
            {
                keys.Add(entry.Name).ShouldBeTrue($"{part} lists {entry.Name} twice");
                entry.Value.GetDouble().ShouldBeInRange(5, 700, entry.Name);
                entry.Name.ShouldBe(entry.Name.Trim());
            }
        }
    }

    [Fact]
    public void Matching_ignores_case()
        => TdpTable.Bundled.Cpu("intel core i7-1165g7").ShouldBe(28);

    [Fact]
    public void The_bundled_table_is_loaded_once_and_holds_real_entries()
    {
        TdpTable.Bundled.ShouldBeSameAs(TdpTable.Bundled);
        TdpTable.Bundled.CpuCount.ShouldBeGreaterThan(300);
        TdpTable.Bundled.GpuCount.ShouldBeGreaterThan(250);
    }
}
