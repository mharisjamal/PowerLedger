using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class DiscreteGpuTests
{
    private const ulong GiB = 1UL << 30;

    private static GpuAdapter Adapter(string name, uint vendor, ulong memory, bool software = false, uint luid = 0xD1A4)
        => new(name, vendor, memory, luid, 0, software);

    [Theory]
    [InlineData("AMD Radeon RX 7800 XT")]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("AMD Radeon RX 9070 XT")]
    [InlineData("Radeon RX 6800M")]                       // a laptop's discrete card
    [InlineData("AMD Radeon(TM) RX 6700S")]
    [InlineData("Radeon RX 580 Series")]
    [InlineData("Radeon RX Vega")]                        // Vega 56 and 64
    [InlineData("AMD Radeon RX Vega M GH Graphics")]      // a separately powered chip beside an Intel processor
    [InlineData("AMD Radeon Pro W7900")]
    [InlineData("AMD Radeon(TM) PRO W6800")]
    [InlineData("AMD FirePro W7100")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics")]
    [InlineData("Intel(R) Arc(TM) A380 Graphics")]
    [InlineData("Intel(R) Arc(TM) A370M Graphics")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics")]
    [InlineData("Intel Arc B570")]
    [InlineData("Intel(R) Arc(TM) Pro A60 Graphics")]
    [InlineData("AMD Radeon HD 7970")]
    [InlineData("AMD Radeon HD 7970M")]                    // a laptop's discrete card
    [InlineData("ATI Radeon HD 5800 Series")]
    [InlineData("AMD Radeon HD 7900 Series")]
    [InlineData("AMD Radeon R9 200 Series")]
    [InlineData("AMD Radeon (TM) R9 390 Series")]
    [InlineData("AMD Radeon (TM) R9 Fury Series")]
    [InlineData("AMD Radeon (TM) R7 370 Series")]
    [InlineData("AMD Radeon R7 M260")]
    [InlineData("AMD Radeon VII")]
    public void Card_names_are_discrete(string name)
        => DiscreteGpu.IsDiscreteName(name).ShouldBeTrue();

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics")]               // Ryzen 4000 onwards
    [InlineData("AMD Radeon 780M Graphics")]
    [InlineData("AMD Radeon(TM) 890M Graphics")]
    [InlineData("AMD Radeon(TM) 8060S Graphics")]
    [InlineData("AMD Radeon(TM) RX Vega 10 Graphics")]    // Ryzen 2000 and 3000 mobile
    [InlineData("Radeon RX Vega 11 Graphics")]            // Ryzen 5 2400G
    [InlineData("AMD Radeon(TM) Vega 8 Graphics")]
    [InlineData("Intel(R) Arc(TM) Graphics")]             // Meteor Lake's built-in graphics
    [InlineData("Intel(R) Arc(TM) 140V GPU (16GB)")]      // Lunar Lake's
    [InlineData("Intel(R) Iris(R) Xe Graphics")]
    [InlineData("Intel(R) UHD Graphics 620")]
    [InlineData("AMD Radeon HD 7660D")]                   // the APUs before Ryzen
    [InlineData("AMD Radeon HD 7660G")]
    [InlineData("AMD Radeon HD 6310 Graphics")]
    [InlineData("AMD Radeon(TM) R7 Graphics")]
    [InlineData("AMD Radeon R5 Graphics")]
    [InlineData("Intel(R) HD Graphics 5000")]
    [InlineData("NVIDIA GeForce RTX 4090")]               // an NVIDIA card is a card by its maker, not its name
    [InlineData("NVIDIA RTX A2000 Laptop GPU")]
    [InlineData("Microsoft Basic Render Driver")]
    [InlineData("")]
    [InlineData(null)]
    public void Processor_graphics_and_other_names_are_not(string? name)
        => DiscreteGpu.IsDiscreteName(name).ShouldBeFalse();

    [Fact]
    public void A_candidate_is_hardware_with_memory_of_its_own_and_for_amd_and_intel_a_cards_name()
    {
        DiscreteGpu.IsCandidate(Adapter("AMD Radeon RX 7800 XT", DiscreteGpu.AmdVendor, 16 * GiB)).ShouldBeTrue();
        DiscreteGpu.IsCandidate(Adapter("Intel(R) Arc(TM) B580 Graphics", DiscreteGpu.IntelVendor, 12 * GiB)).ShouldBeTrue();
        DiscreteGpu.IsCandidate(Adapter("Intel(R) Arc(TM) A310 Graphics", DiscreteGpu.IntelVendor, GiB)).ShouldBeTrue();
        DiscreteGpu.IsCandidate(Adapter("AMD Radeon HD 7970", DiscreteGpu.AmdVendor, 3 * GiB)).ShouldBeTrue();

        // NVIDIA puts no graphics in a Windows PC's processor, so its adapters are cards whatever they are called.
        DiscreteGpu.IsCandidate(Adapter("NVIDIA GeForce MX330", DiscreteGpu.NvidiaVendor, 1968UL << 20)).ShouldBeTrue();
        DiscreteGpu.IsCandidate(Adapter("NVIDIA Quadro 6000", DiscreteGpu.NvidiaVendor, 6 * GiB)).ShouldBeTrue();
        DiscreteGpu.IsCandidate(Adapter("Quadro 6000", DiscreteGpu.NvidiaVendor, 6 * GiB)).ShouldBeTrue();

        // Nor does any other maker, whose hardware with a card's memory is a card.
        DiscreteGpu.IsCandidate(Adapter("Matrox C900", 0x102B, 2 * GiB)).ShouldBeTrue();

        DiscreteGpu.IsCandidate(Adapter("NVIDIA Quadro NVS 295", DiscreteGpu.NvidiaVendor, 256UL << 20)).ShouldBeFalse();
        DiscreteGpu.IsCandidate(Adapter("ASPEED Graphics Family", 0x1A03, 16UL << 20)).ShouldBeFalse();   // a server's management chip
        DiscreteGpu.IsCandidate(Adapter("AMD Radeon RX 7800 XT", DiscreteGpu.AmdVendor, 16 * GiB, software: true)).ShouldBeFalse();
        DiscreteGpu.IsCandidate(Adapter("AMD Radeon RX 7800 XT", DiscreteGpu.MicrosoftVendor, 16 * GiB)).ShouldBeFalse();
        DiscreteGpu.IsCandidate(Adapter("Qualcomm(R) Adreno(TM) X1-85 GPU", 0x4D4F4351, 2 * GiB)).ShouldBeFalse();
    }

    [Fact]
    public void Built_in_graphics_are_not_candidates_whatever_memory_windows_sets_aside_for_them()
    {
        // An APU can be given gigabytes of system memory as its own; its name still says what it is.
        DiscreteGpu.IsCandidate(Adapter("AMD Radeon(TM) 8060S Graphics", DiscreteGpu.AmdVendor, 32 * GiB)).ShouldBeFalse();
        DiscreteGpu.IsCandidate(Adapter("AMD Radeon(TM) RX Vega 11 Graphics", DiscreteGpu.AmdVendor, 2 * GiB)).ShouldBeFalse();

        // A card-like name on built-in graphics, as Intel's newest processors carry, is caught by the small carve-out.
        DiscreteGpu.IsCandidate(Adapter("Intel(R) Arc(TM) B390 GPU", DiscreteGpu.IntelVendor, 128UL << 20)).ShouldBeFalse();
        DiscreteGpu.IsCandidate(Adapter("Intel(R) Arc(TM) A380 Graphics", DiscreteGpu.IntelVendor, GiB - 1)).ShouldBeFalse();
    }

    [Fact]
    public void The_development_laptops_only_candidate_is_its_geforce()
    {
        GpuAdapter[] laptop =
        [
            Adapter("Intel(R) Iris(R) Xe Graphics", DiscreteGpu.IntelVendor, 128UL << 20, luid: 0xD935),
            Adapter("NVIDIA GeForce MX330", DiscreteGpu.NvidiaVendor, 1968UL << 20, luid: 0xDCDD),
            Adapter("Microsoft Basic Render Driver", DiscreteGpu.MicrosoftVendor, 0, software: true, luid: 0xDCAA),
        ];

        DiscreteGpu.Candidates(laptop).ShouldHaveSingleItem().LuidLow.ShouldBe(0xDCDDu);
    }

    [Fact]
    public void Every_card_windows_lists_is_a_candidate_in_its_order()
    {
        GpuAdapter[] desktop =
        [
            Adapter("Intel(R) UHD Graphics 770", DiscreteGpu.IntelVendor, 128UL << 20, luid: 1),
            Adapter("AMD Radeon RX 7800 XT", DiscreteGpu.AmdVendor, 16 * GiB, luid: 2),
            Adapter("Intel(R) Arc(TM) A380 Graphics", DiscreteGpu.IntelVendor, 6 * GiB, luid: 3),
            Adapter("NVIDIA Quadro 6000", DiscreteGpu.NvidiaVendor, 6 * GiB, luid: 4),
        ];

        DiscreteGpu.Candidates(desktop).Select(a => a.LuidLow).ShouldBe([2u, 3u, 4u]);
    }

    [Theory]
    [InlineData("NVIDIA Quadro 6000", "NVIDIA", @"PCI\VEN_10DE&DEV_06D8&SUBSYS_076F10DE&REV_A3\4&1B5E2B8F&0&0010", 0UL, true)]
    [InlineData("NVIDIA GeForce MX330", "NVIDIA", @"PCI\VEN_10DE&DEV_1D16&SUBSYS_0A251028&REV_A1\4&A7FDABB&0&0030", 2147483648UL, true)]
    [InlineData("AMD Radeon RX 7800 XT", "Advanced Micro Devices, Inc.", @"PCI\VEN_1002&DEV_747E", 4293918720UL, true)]
    [InlineData("AMD Radeon(TM) Graphics", "Advanced Micro Devices, Inc.", @"PCI\VEN_1002&DEV_1638", 536870912UL, false)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "Intel Corporation", @"PCI\VEN_8086&DEV_9A49", 2147479552UL, false)]    // WMI gives it 2 GB
    [InlineData("Matrox C900", "Matrox Graphics Inc.", @"PCI\VEN_102B&DEV_0540", 2147483648UL, true)]
    [InlineData("ASPEED Graphics Family(WDDM)", "ASPEED Technology, Inc.", @"PCI\VEN_1A03&DEV_2000", 16777216UL, false)]
    [InlineData("Microsoft Basic Display Adapter", "(Standard display types)", @"PCI\VEN_10DE&DEV_06D8", 0UL, false)]   // no driver yet
    [InlineData("Microsoft Remote Display Adapter", "(Standard display types)", @"SWD\REMOTEDISPLAYENUM\RdpIdd_IndirectDisplay", 0UL, false)]
    [InlineData("DisplayLink USB Device", "DisplayLink", @"USB\VID_17E9&PID_4301", 0UL, false)]
    [InlineData("NVIDIA GeForce RTX 4070", "NVIDIA", null, 0UL, true)]
    [InlineData("AMD Radeon RX 6700S", "Advanced Micro Devices, Inc.", null, 0UL, true)]
    [InlineData(null, "NVIDIA", null, 0UL, false)]
    public void Wmi_tells_a_card_by_its_maker_its_name_and_its_memory(string? name, string? vendor, string? pnp, ulong ram, bool card)
        => DiscreteGpu.IsCard(new VideoController(name, vendor, pnp, ram)).ShouldBe(card);

    [Fact]
    public void Another_makers_card_beside_an_nvidia_one_is_noticed_without_waking_anything()
    {
        VideoController quadro = new("NVIDIA Quadro 6000", "NVIDIA", @"PCI\VEN_10DE&DEV_06D8");
        VideoController gtx = new("NVIDIA GeForce GTX 1080", "NVIDIA", @"PCI\VEN_10DE&DEV_1B80");
        VideoController radeon = new("AMD Radeon RX 7800 XT", "Advanced Micro Devices, Inc.", @"PCI\VEN_1002&DEV_747E");
        VideoController iris = new("Intel(R) Iris(R) Xe Graphics", "Intel Corporation", @"PCI\VEN_8086&DEV_9A49", 2147479552UL);

        DiscreteGpu.HasCardBesideNvidia([quadro, gtx]).ShouldBeFalse();
        DiscreteGpu.HasCardBesideNvidia([iris, quadro]).ShouldBeFalse();   // the development laptop: processor graphics only beside it
        DiscreteGpu.HasCardBesideNvidia([quadro, radeon]).ShouldBeTrue();
    }

    [Fact]
    public void The_pci_ids_are_read_out_of_a_plug_and_play_id()
    {
        const string Quadro = @"PCI\VEN_10DE&DEV_06D8&SUBSYS_076F10DE&REV_A3\4&1B5E2B8F&0&0010";
        DiscreteGpu.VendorIdIn(Quadro).ShouldBe(0x10DEu);
        DiscreteGpu.DeviceIdIn(Quadro).ShouldBe(0x06D8u);
        DiscreteGpu.DeviceIdIn(@"pci\ven_1002&dev_73bf").ShouldBe(0x73BFu);
        DiscreteGpu.DeviceIdIn(null).ShouldBe(0u);
        DiscreteGpu.DeviceIdIn(@"SWD\REMOTEDISPLAYENUM").ShouldBe(0u);
    }

    [Fact]
    public void The_inventory_names_an_nvidia_card_first()
    {
        // An AMD processor's graphics listed before the laptop's NVIDIA card used to win.
        DiscreteGpu.PreferredName(
        [
            ("AMD Radeon(TM) Graphics", "Advanced Micro Devices, Inc."),
            ("NVIDIA GeForce RTX 3050 Laptop GPU", "NVIDIA"),
        ]).ShouldBe("NVIDIA GeForce RTX 3050 Laptop GPU");

        DiscreteGpu.PreferredName(
        [
            ("AMD Radeon RX 6800M", "Advanced Micro Devices, Inc."),
            ("NVIDIA GeForce RTX 4070", "NVIDIA"),
        ]).ShouldBe("NVIDIA GeForce RTX 4070");
    }

    [Fact]
    public void Then_an_amd_or_intel_card_over_the_processors_graphics()
    {
        DiscreteGpu.PreferredName(
        [
            ("Intel(R) Iris(R) Xe Graphics", "Intel Corporation"),
            ("Intel(R) Arc(TM) A370M Graphics", "Intel Corporation"),
        ]).ShouldBe("Intel(R) Arc(TM) A370M Graphics");

        DiscreteGpu.PreferredName(
        [
            ("AMD Radeon(TM) Graphics", "Advanced Micro Devices, Inc."),
            ("AMD Radeon RX 6700S", "Advanced Micro Devices, Inc."),
        ]).ShouldBe("AMD Radeon RX 6700S");
    }

    [Fact]
    public void Otherwise_the_first_adapter_windows_lists()
    {
        DiscreteGpu.PreferredName(
        [
            (null, null),
            ("AMD Radeon(TM) Graphics", "Advanced Micro Devices, Inc."),
            ("Microsoft Remote Display Adapter", "(Standard display types)"),
        ]).ShouldBe("AMD Radeon(TM) Graphics");

        DiscreteGpu.PreferredName([]).ShouldBeNull();
    }
}
