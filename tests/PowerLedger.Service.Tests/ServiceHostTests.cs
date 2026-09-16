using System.IO.Pipes;
using Microsoft.Data.Sqlite;
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

[Trait("Category", "Hardware")]
public class ServiceHostTests
{
    [Fact]
    public async Task From_the_console_the_service_records_readings_and_answers_on_its_pipe()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"powerledger-host-{Guid.NewGuid():N}");
        var pipeName = $"PowerLedger.test.{Guid.NewGuid():N}";
        var host = ServiceHost.Build(["--data", folder, "--pipe", pipeName]);
        await host.StartAsync();
        try
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            await using var client = new MessageChannel(pipe);

            ServiceStatus? status = null;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            for (var id = 1; status is null; id++)
            {
                await client.WriteAsync(new GetStatusRequest(id));
                if (await client.ReadAsync() is StatusReply { Status.Ticks: >= 3 } reply) status = reply.Status;
                else if (DateTime.UtcNow > deadline) throw new TimeoutException("The service never reported three readings.");
                else await Task.Delay(500);
            }

            status.Sources.ShouldContain(s => s.Name == "battery");
            status.Last.ShouldNotBeNull().TotalW.ShouldBeGreaterThan(0);
            status.InventoryHash.Length.ShouldBe(16);
            status.Monitors.ShouldNotBeNull();              // a list, even with no external monitor, so the App knows to report brightness
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
            Serilog.Log.CloseAndFlush();
        }

        using (var database = new SqliteDatabase(Path.Combine(folder, "power.db"), readOnly: true))
        {
            new RawSampleRepository(database).Count().ShouldBeGreaterThanOrEqualTo(3);
            new SessionRepository(database).List(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1))
                .Single().EndReason.ShouldBe(SessionReason.ServiceStop);
        }
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
