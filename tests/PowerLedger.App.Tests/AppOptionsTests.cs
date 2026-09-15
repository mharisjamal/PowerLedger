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
        options.UpdateFeed.ShouldBeNull();
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

    [Theory]
    [InlineData("http://127.0.0.1:8765", "http://127.0.0.1:8765/")]
    [InlineData("http://localhost:8765/feed/", "http://localhost:8765/feed/")]
    [InlineData("https://localhost:8443/pl", "https://localhost:8443/pl/")]
    public void A_test_feed_is_on_this_machine(string given, string expected)
        => AppOptions.Parse(["--update-feed", given]).UpdateFeed.ShouldBe(new Uri(expected));

    /// <summary>Anything that can change the App's command line could otherwise offer an installer GitHub doesn't list.</summary>
    [Theory]
    [InlineData("https://example.org/pl")]
    [InlineData("http://example.org/")]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("not a url")]
    [InlineData(@"C:\feed")]
    public void A_feed_anywhere_else_is_ignored(string given)
        => AppOptions.Parse(["--update-feed", given]).UpdateFeed.ShouldBeNull();
}
