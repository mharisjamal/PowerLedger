using System.Runtime.InteropServices;
using PowerLedger.Service.Sharing;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class HostFactsTests
{
    [Theory]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.Arm64, "arm64")]
    [InlineData(Architecture.X86, "x86")]
    public void Each_Windows_architecture_is_reported_as_itself(Architecture architecture, string name)
        => HostFacts.ArchName(architecture).ShouldBe(name);
}
