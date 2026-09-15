using System.IO.Pipes;
using PowerLedger.Contracts;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// These run only against an installed PowerLedger, unelevated, after installing it:
/// <c>dotnet test -c Release --filter Category=Installed</c>. They prove what an elevated run or a --pipe run can't: that
/// the installed service passes the server check, that it takes a real write, and that the App can read the database
/// under the service's folder ACL.
/// </summary>
[Trait("Category", "Installed")]
public class InstalledTests
{
    [Fact]
    public void The_installed_service_passes_the_check()
    {
        using var client = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut);
        client.Connect(2000);
        InstalledServiceCheck.FromServiceManager().Refusal(client.SafePipeHandle).ShouldBeNull();
    }

    [Fact]
    public async Task A_real_write_that_changes_nothing_is_accepted()
    {
        await using var link = new PipeServiceLink(
            PipeProtocol.PipeName, new LastInputIdleSource(), TimeProvider.System, InstalledServiceCheck.FromServiceManager());
        link.Start();
        var settings = await SettingsOf(link);
        (await link.SetSettingsAsync(settings)).ShouldBe(WriteResult.Done);
    }

    [Fact]
    public void History_reads_from_the_installed_database()
    {
        using var database = new SqliteDatabase(AppOptions.Parse([]).DatabasePath, readOnly: true);
        new HistoryReader(database).Read(DateTimeOffset.Now, TimeZoneInfo.Local).ShouldNotBeNull();
    }

    /// <summary>The service's settings, once the link has connected and the service has finished starting; at most 15 seconds.</summary>
    private static async Task<ServiceSettings> SettingsOf(PipeServiceLink link)
    {
        using var within = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                if (link.IsConnected && await link.GetSettingsAsync(within.Token) is { } settings) return settings;
                await Task.Delay(100, within.Token);
            }
        }
        catch (OperationCanceledException) when (within.IsCancellationRequested)
        {
            throw new TimeoutException("The installed service didn't connect and send its settings within 15 seconds.");
        }
    }
}
