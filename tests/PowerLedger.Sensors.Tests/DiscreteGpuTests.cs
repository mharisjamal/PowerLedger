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
    [InlineData("NVIDIA GeForce RTX 4090")]               // NVIDIA's own source reads NVIDIA cards
    [InlineData("NVIDIA RTX A2000 Laptop GPU")]
    [InlineData("Microsoft Basic Render Driver")]
    [InlineData("")]
    [InlineData(null)]
    public void Processor_graphics_and_other_names_are_not(string? name)
        => DiscreteGpu.IsDiscreteName(name).ShouldBeFalse();

    [Fact]
    public void A_candidate_is_amd_or_intel_hardware_with_a_card_name_and_memory_of_its_own()
    {
        DiscreteGpu.IsCandidate(Adapter("AMD Radeon RX 7800 XT", DiscreteGpu.AmdVendor, 16 * GiB)).ShouldBeTrue();
        DiscreteGpu.IsCandidate(Adapter("Intel(R) Arc(TM) B580 Graphics", DiscreteGpu.IntelVendor, 12 * GiB)).ShouldBeTrue();
        DiscreteGpu.IsCandidate(Adapter("Intel(R) Arc(TM) A310 Graphics", DiscreteGpu.IntelVendor, GiB)).ShouldBeTrue();

        DiscreteGpu.IsCandidate(Adapter("NVIDIA GeForce MX330", 0x10DE, 1968UL << 20)).ShouldBeFalse();
        DiscreteGpu.IsCandidate(Adapter("AMD Radeon RX 7800 XT", DiscreteGpu.AmdVendor, 16 * GiB, software: true)).ShouldBeFalse();
        DiscreteGpu.IsCandidate(Adapter("AMD Radeon RX 7800 XT", 0x1414, 16 * GiB)).ShouldBeFalse();
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
    public void The_development_laptop_has_no_candidate()
    {
        GpuAdapter[] laptop =
        [
            Adapter("Intel(R) Iris(R) Xe Graphics", DiscreteGpu.IntelVendor, 128UL << 20, luid: 0xD935),
            Adapter("NVIDIA GeForce MX330", 0x10DE, 1968UL << 20, luid: 0xDCDD),
            Adapter("Microsoft Basic Render Driver", 0x1414, 0, software: true, luid: 0xDCAA),
        ];

        DiscreteGpu.Choose(laptop).ShouldBeNull();
    }

    [Fact]
    public void The_first_card_windows_lists_is_chosen()
    {
        GpuAdapter[] desktop =
        [
            Adapter("Intel(R) UHD Graphics 770", DiscreteGpu.IntelVendor, 128UL << 20, luid: 1),
            Adapter("AMD Radeon RX 7800 XT", DiscreteGpu.AmdVendor, 16 * GiB, luid: 2),
            Adapter("Intel(R) Arc(TM) A380 Graphics", DiscreteGpu.IntelVendor, 6 * GiB, luid: 3),
        ];

        DiscreteGpu.Choose(desktop).ShouldNotBeNull().LuidLow.ShouldBe(2u);
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
