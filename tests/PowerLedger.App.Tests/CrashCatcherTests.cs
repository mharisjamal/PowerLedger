using System.IO;
using System.Text.Json;
using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class CrashCatcherTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-crashes-{Guid.NewGuid():N}");
    private static readonly ScrubNames Names = new("alice", "DESKTOP-ALICE", "CONTOSO");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void The_report_names_the_app_and_carries_the_outermost_message()
    {
        var error = new InvalidOperationException("boom");
        var report = CrashCatcher.Build(error, "0.6.0");

        report.Component.ShouldBe("app");
        report.Version.ShouldBe("0.6.0");
        report.Message.ShouldBe("boom");
        report.Types.ShouldBe(new[] { "System.InvalidOperationException" });
        report.Stack.ShouldContain("InvalidOperationException");
    }

    [Fact]
    public void A_nested_exception_lists_its_types_outermost_first()
    {
        var inner = new ArgumentNullException("thing");
        var outer = new InvalidOperationException("wrapped", inner);

        var report = CrashCatcher.Build(outer, "0.6.0");

        report.Types.ShouldBe(new[] { "System.InvalidOperationException", "System.ArgumentNullException" });
        report.Message.ShouldBe("wrapped");
    }

    [Fact]
    public void An_aggregate_exception_flattens_every_branch()
    {
        var aggregate = new AggregateException("many", new InvalidOperationException("a"), new ArgumentException("b", new FormatException("c")));

        var report = CrashCatcher.Build(aggregate, "0.6.0");

        report.Types.ShouldBe(new[]
        {
            "System.AggregateException", "System.InvalidOperationException", "System.ArgumentException", "System.FormatException",
        });
    }

    [Fact]
    public void Writing_scrubs_and_trims_before_the_file_lands()
    {
        var error = new InvalidOperationException(@"failed for alice at C:\Users\alice\data.db");

        CrashCatcher.Write(error, _folder, "0.6.0", Names);

        var path = Directory.GetFiles(_folder, "app-*.json").Single();
        var saved = JsonSerializer.Deserialize<CrashReport>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        saved.ShouldNotBeNull();
        saved.Message.ShouldBe(@"failed for <user> at %USERPROFILE%\data.db");
        saved.Component.ShouldBe("app");
    }

    [Fact]
    public void Writing_never_throws_even_when_the_folder_cannot_be_made()
    {
        // A file where a directory is expected: Directory.CreateDirectory must fail.
        var blocker = Path.Combine(Path.GetTempPath(), $"powerledger-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "x");
        try
        {
            Should.NotThrow(() => CrashCatcher.Write(new Exception("boom"), Path.Combine(blocker, "Crashes"), "0.6.0", Names));
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
