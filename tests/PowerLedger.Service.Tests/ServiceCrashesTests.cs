using PowerLedger.Contracts;
using PowerLedger.Service.Sharing;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>The service's own crashes, written to a file whatever the consent, for the sharing worker to record or delete
/// (data-sharing design §5).</summary>
public sealed class ServiceCrashesTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 9, 24, 10, 11, 12, 345, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-crashes-{Guid.NewGuid():N}");

    [Fact]
    public void A_crash_is_written_as_a_report_with_its_types_outermost_first_and_its_stacks_in_the_same_order()
    {
        var error = Caught(() => Outer());

        var path = ServiceCrashes.TryWrite(_folder, error, At, "0.6.0").ShouldNotBeNull();

        Path.GetFileName(path).ShouldBe($"service-{At.UtcTicks}.json");
        var report = SharingJson.Read(File.ReadAllText(path), SharingJson.Default.CrashReport).ShouldNotBeNull();
        report.Validate().ShouldBeNull();
        (report.At, report.Component, report.Version, report.Message).ShouldBe((At, "service", "0.6.0", "The outer call failed."));
        report.Types.ShouldBe(new[]
        {
            "System.InvalidOperationException", "System.AggregateException", "System.IO.IOException", "System.TimeoutException",
        });
        var stack = report.Stack;
        stack.IndexOf(nameof(Outer), StringComparison.Ordinal).ShouldBeLessThan(stack.IndexOf("System.IO.IOException: The disk is full.", StringComparison.Ordinal));
        stack.IndexOf(nameof(Disk), StringComparison.Ordinal).ShouldBeLessThan(stack.IndexOf("System.TimeoutException: Too slow.", StringComparison.Ordinal));
        Directory.GetFiles(_folder).ShouldBe(new[] { path });              // nothing half-written is left beside it
    }

    [Fact]
    public void A_crash_too_big_for_the_server_is_cut_down_to_what_it_takes()
    {
        Exception error = new InvalidOperationException(new string('m', 5000));
        for (var depth = 0; depth < 15; depth++) error = new InvalidOperationException("Wrapped.", error);

        var report = ServiceCrashes.Report(error, At, "0.6.0");

        report.Validate().ShouldBeNull();
        report.Types.Count.ShouldBe(CrashReport.MaxTypes);
        report.Message.ShouldBe("Wrapped.");
    }

    [Fact]
    public void Writing_a_crash_never_throws()
    {
        File.WriteAllText(_folder, "a file where the folder should be");

        ServiceCrashes.TryWrite(_folder, new InvalidOperationException("Boom."), At, "0.6.0").ShouldBeNull();
        ServiceCrashes.TryWrite(_folder, null!, At, "0.6.0").ShouldBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        else if (File.Exists(_folder)) File.Delete(_folder);
    }

    private static Exception Caught(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            return error;
        }
        throw new InvalidOperationException("Nothing was thrown.");
    }

    private static void Outer()
    {
        try
        {
            var both = new List<Exception> { Caught(Disk), Caught(() => throw new TimeoutException("Too slow.")) };
            throw new AggregateException(both);
        }
        catch (AggregateException error)
        {
            throw new InvalidOperationException("The outer call failed.", error);
        }
    }

    private static void Disk() => throw new IOException("The disk is full.");
}
