using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Perf review P15: both programs are published ahead of time compiled, and the App raises no PropertyChanging,
/// which nothing listens for.</summary>
public class BuildSettingsTests
{
    [Fact]
    public void Every_program_is_published_ready_to_run()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "scripts", "publish.ps1"));

        var publishes = Regex.Matches(script, @"^\s*dotnet publish .*$", RegexOptions.Multiline);
        publishes.Count.ShouldBe(1, "one publish, for the App and the service in turn");
        publishes[0].Value.ShouldContain("-p:PublishReadyToRun=true");
        publishes[0].Value.ShouldContain("--self-contained true");
    }

    [Fact]
    public void The_app_raises_no_property_changing()
    {
        // The App's own runtimeconfig, copied beside the tests: the switch the MVVM Toolkit reads at start.
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PowerLedger.runtimeconfig.json")));

        config.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties")
            .GetProperty("MVVMTOOLKIT_ENABLE_INOTIFYPROPERTYCHANGING_SUPPORT").GetBoolean().ShouldBeFalse();
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PowerLedger.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found above the test binaries.");
    }
}
