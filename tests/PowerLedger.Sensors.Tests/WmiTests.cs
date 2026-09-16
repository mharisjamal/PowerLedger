using System.Management;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class WmiTests
{
    [Theory]
    [InlineData(ManagementStatus.NotSupported)]     // what WMI refuses a monitor class with once no display is left
    [InlineData(ManagementStatus.InvalidClass)]     // a class that isn't registered at all
    public void A_refusal_that_says_a_class_has_no_instances_means_it_has_none(ManagementStatus status)
        => Wmi.MeansNoInstances(status).ShouldBeTrue();

    [Theory]
    [InlineData(ManagementStatus.Timedout)]
    [InlineData(ManagementStatus.AccessDenied)]
    [InlineData(ManagementStatus.ProviderFailure)]
    [InlineData(ManagementStatus.ProviderLoadFailure)]
    [InlineData(ManagementStatus.ShuttingDown)]
    [InlineData(ManagementStatus.Failed)]
    [InlineData(ManagementStatus.InvalidNamespace)]
    [InlineData(ManagementStatus.InvalidQuery)]     // what a misspelt property gives
    public void Any_other_refusal_is_wmi_failing_to_answer(ManagementStatus status)
        => Wmi.MeansNoInstances(status).ShouldBeFalse();
}
