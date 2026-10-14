using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Perf review P3: Windows' efficiency mode for the App's process while none of its windows shows, handed back to
/// Windows when one does. Tried on the test's own process, and handed back after.</summary>
public class ProcessQosTests
{
    [Fact]
    public void Efficiency_mode_is_asked_for_and_handed_back()
    {
        try
        {
            ProcessQos.Efficient(true).ShouldBeTrue("Windows 11 takes it");
            ProcessQos.IsEfficient()?.ShouldBeTrue();

            ProcessQos.Efficient(false).ShouldBeTrue();
            ProcessQos.IsEfficient()?.ShouldBeFalse();
        }
        finally
        {
            ProcessQos.Efficient(false);
        }
    }
}
