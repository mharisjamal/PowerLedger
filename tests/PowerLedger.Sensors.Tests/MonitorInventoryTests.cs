using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class MonitorInventoryTests
{
    private const uint Hdmi = 5;
    private const uint DisplayPort = 10;
    private const uint Internal = 0x80000000;

    /// <summary>One display as WMI's four monitor classes describe it. A null connection or mode is one a class left out.</summary>
    private sealed record Display(string Instance, string Maker, string Product, string Serial, string Name, uint? Connection,
        bool Active = true, double WidthCm = 60, double HeightCm = 34, (int Width, int Height)? Mode = null);

    private static readonly Display Dell = new(@"DISPLAY\DELA0B1\5&2f5a1b&0&UID4353_0", "DEL", "A0B1", "7MKZG34", "DELL U2723QE",
        DisplayPort, Mode: (3840, 2160));

    private static readonly Display Lg = new(@"DISPLAY\GSM5B7F\5&2f5a1b&0&UID4354_0", "GSM", "5B7F", "", "LG HDR 4K",
        Hdmi, Mode: (3840, 2160));

    /// <summary>The development laptop's own panel, as Windows lists it.</summary>
    private static readonly Display Panel = new(@"DISPLAY\AUO4199\4&c5a02e3&0&UID8388688_0", "AUO", "4199", "0", "",
        Internal, WidthCm: 34, HeightCm: 19, Mode: (1920, 1080));

    /// <summary>Text as WMI gives it: a character code each, padded with zeros to the array's fixed length.</summary>
    private static ushort[] Codes(string text) => [.. text.Select(c => (ushort)c), .. new ushort[16 - text.Length]];

    private static IReadOnlyList<MonitorFacts> From(params Display[] displays) => MonitorInventory.From(
        [.. displays.Select(d => (d.Instance, Codes(d.Maker), Codes(d.Product), Codes(d.Serial), Codes(d.Name)))],
        displays.Where(d => d.Connection is not null).ToDictionary(d => d.Instance, d => d.Connection!.Value),
        [.. displays.Select(d => (d.Instance, d.Active, d.WidthCm, d.HeightCm))],
        displays.Where(d => d.Mode is not null).ToDictionary(d => d.Instance, d => d.Mode!.Value));

    [Theory]
    [InlineData(0x80000000u)]   // internal
    [InlineData(11u)]           // embedded DisplayPort
    [InlineData(13u)]           // embedded UDI
    [InlineData(6u)]            // LVDS, on older laptops
    public void Only_the_external_monitors_come_back(uint builtIn)
        => From(Dell, Panel with { Connection = builtIn }, Lg).Select(m => m.Name).ShouldBe(["DELL U2723QE", "LG HDR 4K"]);

    [Fact]
    public void A_monitor_windows_still_lists_but_isnt_showing_is_left_out()
        => From(Dell with { Active = false }, Lg).Select(m => m.Name).ShouldBe(["LG HDR 4K"]);

    [Fact]
    public void A_monitor_windows_gives_no_connection_for_is_left_out()
    {
        // It could be the laptop's own panel, which the panel model already counts.
        From(Dell with { Connection = null }).ShouldBeEmpty();
    }

    [Fact]
    public void The_name_and_codes_decode_from_zero_padded_character_codes()
    {
        From(Dell).ShouldHaveSingleItem().ShouldBe(new MonitorFacts(
            Instance: @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353",
            Key: "DELA0B1-7MKZG34",
            Maker: "DEL",
            ProductCode: "A0B1",
            Name: "DELL U2723QE",
            Inches: 27,
            Width: 3840,
            Height: 2160));
    }

    [Fact]
    public void A_monitor_without_a_name_reads_an_empty_one()
        => From(Dell with { Name = "" }).ShouldHaveSingleItem().Name.ShouldBe("");

    [Fact]
    public void A_monitor_that_lists_no_native_mode_reads_no_resolution()
    {
        var monitor = From(Dell with { Mode = null }).ShouldHaveSingleItem();

        monitor.Width.ShouldBe(0);
        monitor.Height.ShouldBe(0);
    }

    [Fact]
    public void Windows_instance_names_join_across_its_classes_whatever_their_case_or_suffix()
    {
        var monitors = MonitorInventory.From(
            [(@"DISPLAY\DELA0B1\5&2f5a1b&0&UID4353_0", Codes("DEL"), Codes("A0B1"), Codes("7MKZG34"), Codes("DELL U2723QE"))],
            new Dictionary<string, uint> { [@"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353"] = DisplayPort },
            [(@"display\dela0b1\5&2f5a1b&0&uid4353_0", true, 60, 34)],
            new Dictionary<string, (int Width, int Height)> { [@"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353_0"] = (3840, 2160) });

        monitors.ShouldHaveSingleItem().Width.ShouldBe(3840);
    }

    [Theory]
    [InlineData("7MKZG34", "DELA0B1-7MKZG34")]
    [InlineData("", @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353")]
    [InlineData("0", @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353")]           // a panel with no serial number, as Windows words it
    [InlineData("00000000", @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353")]
    public void A_monitor_is_known_by_its_serial_when_it_has_one_and_by_its_instance_otherwise(string serial, string key)
        => From(Dell with { Serial = serial }).ShouldHaveSingleItem().Key.ShouldBe(key);

    [Fact]
    public void Two_monitors_that_report_the_same_serial_are_known_by_their_instances()
    {
        // Some makers give every unit the same serial number. A key two monitors shared would merge their settings.
        var left = Dell with { Serial = "16843009" };
        var right = left with { Instance = @"DISPLAY\DELA0B1\5&2f5a1b&0&UID4355_0" };
        var third = Dell with { Instance = @"DISPLAY\DELA0B1\5&2f5a1b&0&UID4356_0" };     // a serial of its own

        From(left, right, third).Select(m => m.Key)
            .ShouldBe([@"DISPLAY\DELA0B1\5&2F5A1B&0&UID4353", @"DISPLAY\DELA0B1\5&2F5A1B&0&UID4355", "DELA0B1-7MKZG34"]);
    }

    [Theory]
    [InlineData(60, 34, 27)]
    [InlineData(34, 19, 15.6)]      // whole centimetres alone put this panel at 15.3
    [InlineData(29, 17, 13.3)]
    [InlineData(80, 33, 34)]        // an ultrawide
    [InlineData(120, 34, 49)]       // a super-ultrawide
    public void A_diagonal_snaps_to_the_common_size_it_is_within_half_an_inch_of(double widthCm, double heightCm, double inches)
        => MonitorInventory.Diagonal(widthCm, heightCm).ShouldBe(inches);

    [Fact]
    public void A_size_far_from_any_common_one_is_only_rounded()
        => MonitorInventory.Diagonal(81, 46).ShouldBe(36.7);    // 36.67 inches, between the 35- and 38-inch sizes

    [Theory]
    [InlineData(0, 0)]      // a projector: EDID leaves the size undefined
    [InlineData(79, 0)]     // EDID's aspect ratio in place of a size, here 16:9
    public void A_monitor_that_gives_no_size_has_no_diagonal(double widthCm, double heightCm)
        => MonitorInventory.Diagonal(widthCm, heightCm).ShouldBe(0);
}
