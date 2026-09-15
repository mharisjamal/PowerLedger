using System.IO.Pipes;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class ServerCheckTests : IDisposable
{
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
    public void The_server_behind_a_pipe_is_known_by_its_executable()
        => InstalledServiceCheck.ServerImage(_client.SafePipeHandle).ShouldBe(Environment.ProcessPath, StringCompareShould.IgnoreCase);

    [Fact]
    public void The_installed_service_may_be_sent_changes()
        => new InstalledServiceCheck(() => Environment.ProcessPath).Refusal(_client.SafePipeHandle).ShouldBeNull();

    [Fact]
    public void Another_program_serving_the_pipe_is_refused()
        => new InstalledServiceCheck(() => @"C:\Program Files\PowerLedger\PowerLedger.Service.exe").Refusal(_client.SafePipeHandle)
            .ShouldBe("The program serving PowerLedger's pipe isn't the installed service, so nothing was sent.");

    [Fact]
    public void Without_an_installed_service_changes_are_refused()
        => new InstalledServiceCheck(() => null).Refusal(_client.SafePipeHandle)
            .ShouldBe("The PowerLedger service isn't installed, so nothing can be changed.");

    [Fact]
    public void A_development_run_trusts_its_own_pipe() => new TrustAnyServer().Refusal(_client.SafePipeHandle).ShouldBeNull();

    [Theory]
    [InlineData("\"C:\\Program Files\\PowerLedger\\PowerLedger.Service.exe\" --data x", @"C:\Program Files\PowerLedger\PowerLedger.Service.exe")]
    [InlineData(@"C:\PowerLedger\PowerLedger.Service.exe --pipe dev", @"C:\PowerLedger\PowerLedger.Service.exe")]
    [InlineData(@"C:\PowerLedger\PowerLedger.Service.exe", @"C:\PowerLedger\PowerLedger.Service.exe")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_executable_comes_out_of_the_registered_command_line(string? commandLine, string? executable)
        => InstalledServiceCheck.ExecutableOf(commandLine).ShouldBe(executable);
}
