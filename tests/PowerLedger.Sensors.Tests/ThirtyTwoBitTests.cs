using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>The 32-bit build: vendor libraries with no 32-bit version are switched off with a reason, never loaded.</summary>
public class ThirtyTwoBitTests
{
    [Fact]
    public void NVML_is_off_in_a_32_bit_process_with_a_reason()
    {
        using var nvml = new Nvml(is64BitProcess: false);

        nvml.Available.ShouldBeFalse();
        nvml.Unavailable.ShouldNotBeNull().ShouldContain("32-bit");
    }

    [Fact]
    public void ADLX_is_off_in_a_32_bit_process_with_a_reason()
    {
        var opening = Adlx.Open(is64BitProcess: false);

        opening.State.ShouldBe(AmdLibrary.NotInstalled);
        opening.Reason.ShouldNotBeNull().ShouldContain("32-bit");
    }

    [Fact]
    public void Level_Zero_is_off_in_a_32_bit_process()
    {
        var sysman = new FakeSysman();
        sysman.Add(new SysmanDevice(SysmanDevice.GpuType, DiscreteGpu.IntelVendor, 0x56A0, 0, 0), PowerDomain.Card);

        using var source = new ArcSource(sysman, _ => () => false, includeIntegrated: false, is64BitProcess: false);

        source.Supported.ShouldBeFalse();
        source.Unavailable.ShouldNotBeNull().ShouldContain("32-bit");
    }

    [Theory]
    [InlineData(true, "atiadlxx.dll")]
    [InlineData(false, "atiadlxy.dll")]
    public void ADL_loads_the_library_of_the_process_bitness(bool is64BitProcess, string library)
        => AdlLibrary.FileName(is64BitProcess).ShouldBe(library);
}
