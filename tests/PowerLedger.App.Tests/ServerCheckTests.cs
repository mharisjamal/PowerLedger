using System.IO.Pipes;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class ServerCheckTests : IDisposable
{
    private const string NotTheService = "The program serving PowerLedger's pipe isn't the installed service, so nothing was sent.";

    private readonly NamedPipeServerStream _server;
    private readonly NamedPipeClientStream _client;

    public ServerCheckTests()
    {
        var name = $"PowerLedger.check-test.{Guid.NewGuid():N}";
        _server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = _server.WaitForConnectionAsync();
        _client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        _client.Connect(2000);
        waiting.Wait(2000);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void The_server_behind_a_pipe_is_known_by_its_process()
        => InstalledServiceCheck.ServerProcess(_client.SafePipeHandle).ShouldBe((uint)Environment.ProcessId);

    [Fact]
    public void The_installed_service_may_be_sent_changes()
        => new InstalledServiceCheck(() => (uint)Environment.ProcessId).Refusal(_client.SafePipeHandle).ShouldBeNull();

    [Fact]
    public void Another_program_serving_the_pipe_is_refused()
        => new InstalledServiceCheck(() => 4u).Refusal(_client.SafePipeHandle).ShouldBe(NotTheService);   // 4 is Windows' System process

    [Fact]
    public void While_the_installed_service_isnt_running_whatever_serves_the_pipe_is_refused()
        => new InstalledServiceCheck(() => 0u).Refusal(_client.SafePipeHandle).ShouldBe(NotTheService);

    [Fact]
    public void Without_an_installed_service_changes_are_refused()
        => new InstalledServiceCheck(() => null).Refusal(_client.SafePipeHandle)
            .ShouldBe("The PowerLedger service isn't installed, so nothing can be changed.");

    [Fact]
    public void A_development_run_trusts_its_own_pipe() => new TrustAnyServer().Refusal(_client.SafePipeHandle).ShouldBeNull();

    [Fact]
    public void A_service_that_isnt_installed_has_no_process()
        => InstalledServiceCheck.ServiceProcess($"PowerLedger.NoSuchService.{Guid.NewGuid():N}").ShouldBeNull();

    /// <summary>Every Windows serves \\.\pipe\eventlog from its EventLog service, in a process an unelevated test can't
    /// open, just as the App can't open the installed service's. The Service Control Manager still names it.</summary>
    [Fact]
    public void A_real_service_is_known_behind_its_pipe_and_passes_the_check()
    {
        using var client = new NamedPipeClientStream(".", "eventlog", PipeDirection.InOut);
        client.Connect(2000);
        InstalledServiceCheck.ServerProcess(client.SafePipeHandle).ShouldBe(InstalledServiceCheck.ServiceProcess("EventLog"));
        new InstalledServiceCheck(() => InstalledServiceCheck.ServiceProcess("EventLog")).Refusal(client.SafePipeHandle).ShouldBeNull();
    }
}
