using System.IO;
using Shouldly;

namespace PowerLedger.App.Tests;

public class AppOptionsTests
{
    [Fact]
    public void With_no_switches_the_App_reaches_the_installed_service()
    {
        var options = AppOptions.Parse([]);
        options.PipeName.ShouldBe("PowerLedger.v1");
        options.DatabasePath.ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PowerLedger", "power.db"));
        options.StartInTray.ShouldBeFalse();
    }

    [Fact]
    public void Switches_reach_a_development_service_and_start_in_the_tray()
    {
        var options = AppOptions.Parse(["--pipe", "PowerLedger.dev", "--data", @"C:\Temp\pl-run", "--tray"]);
        options.PipeName.ShouldBe("PowerLedger.dev");
        options.DatabasePath.ShouldBe(@"C:\Temp\pl-run\power.db");
        options.StartInTray.ShouldBeTrue();
    }

    [Fact]
    public void A_switch_without_its_value_is_ignored()
        => AppOptions.Parse(["--pipe"]).PipeName.ShouldBe("PowerLedger.v1");
}
