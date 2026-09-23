using System.IO;
using System.Text.Json;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class CrashForwarderTests : IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"powerledger-forward-{Guid.NewGuid():N}");
    private readonly FakeLink _link = new();

    public CrashForwarderTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private void WriteCrash(string name, DateTime? lastWriteUtc = null)
    {
        var report = new CrashReport(DateTimeOffset.UtcNow, "app", "0.6.0", ["System.Exception"], "boom", "at X");
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, JsonSerializer.Serialize(report, Options));
        if (lastWriteUtc is { } at) File.SetLastWriteTimeUtc(path, at);
    }

    [Fact]
    public async Task Nothing_is_sent_without_diagnostics_consent()
    {
        WriteCrash("app-1.json");
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, true, true, true));   // diagnostics off
        _link.Connect(true);

        await new CrashForwarder(_link, UiThreads.Inline, _folder).RunAsync();

        _link.Writes.ShouldBeEmpty();
        Directory.GetFiles(_folder).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_file_is_sent_and_deleted_on_ok_once_diagnostics_is_on()
    {
        WriteCrash("app-1.json");
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, true, false, false, false));
        _link.Connect(true);

        await new CrashForwarder(_link, UiThreads.Inline, _folder).RunAsync();

        _link.Writes.OfType<CrashReport>().Count().ShouldBe(1);
        Directory.GetFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_is_kept_when_the_send_fails()
    {
        WriteCrash("app-1.json");
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, true, false, false, false));
        _link.Connect(true);
        _link.Answer = new WriteResult("Refused.");

        await new CrashForwarder(_link, UiThreads.Inline, _folder).RunAsync();

        Directory.GetFiles(_folder).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_file_older_than_seven_days_is_deleted_whatever_the_consent()
    {
        WriteCrash("app-1.json", DateTime.UtcNow - CrashForwarder.MaxAge - TimeSpan.FromDays(1));
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, false, false, false, false));   // no consent at all
        _link.Connect(true);

        await new CrashForwarder(_link, UiThreads.Inline, _folder).RunAsync();

        Directory.GetFiles(_folder).ShouldBeEmpty();
    }

    [Fact]
    public void It_runs_once_the_link_first_connects()
    {
        WriteCrash("app-1.json");
        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, true, false, false, false));
        var forwarder = new CrashForwarder(_link, UiThreads.Inline, _folder);

        forwarder.Start();
        _link.Writes.ShouldBeEmpty();   // not connected yet

        _link.Connect(true);

        _link.Writes.OfType<CrashReport>().Count().ShouldBe(1);
    }

    /// <summary>The pipe can connect before the service has published a Sharing status — just starting, or an old
    /// service that never will. Either way, that must not use up the session's one run: it must try again on the next
    /// connection rather than leaving the file stranded until the App restarts.</summary>
    [Fact]
    public void It_retries_on_the_next_connection_if_the_service_has_not_published_sharing_yet()
    {
        WriteCrash("app-1.json");
        _link.Status = Statuses.Running();   // connected, but no Sharing yet: still starting, or an old service
        var forwarder = new CrashForwarder(_link, UiThreads.Inline, _folder);

        forwarder.Start();
        _link.Connect(true);

        _link.Writes.ShouldBeEmpty();
        Directory.GetFiles(_folder).ShouldHaveSingleItem();   // no Sharing status seen yet: not sent, but not given up on either

        _link.Status = Statuses.WithSharing(new Consent(ConsentText.Version, true, false, false, false));
        _link.Connect(true);   // the service finishes starting and reconnects with a Sharing status

        _link.Writes.OfType<CrashReport>().Count().ShouldBe(1);
        Directory.GetFiles(_folder).ShouldBeEmpty();
    }
}
