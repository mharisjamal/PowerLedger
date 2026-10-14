using System.Diagnostics;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class EcoQosTests
{
    [Fact]
    public void A_process_asked_to_run_at_the_efficiency_setting_reads_back_so_and_others_are_left_alone()
    {
        // A child of the test, not the test's own process, which would run every other test slower.
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/d /q /k")
        {
            RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        using var other = Process.Start(new ProcessStartInfo("cmd.exe", "/d /q /k")
        {
            RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        try
        {
            EcoQos.IsOn(child.SafeHandle).ShouldBe(false);
            EcoQos.Enable(child.SafeHandle).ShouldBeTrue();
            EcoQos.IsOn(child.SafeHandle).ShouldBe(true);
            EcoQos.IsOn(other.SafeHandle).ShouldBe(false);
        }
        finally
        {
            child.Kill();
            other.Kill();
        }
    }
}
