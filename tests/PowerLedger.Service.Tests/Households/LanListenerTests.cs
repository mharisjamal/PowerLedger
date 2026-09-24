using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using PowerLedger.Service.Households.Lan;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>The household's listener (households design §3): once told to stop, its port is closed before the call returns,
/// so the reply saying other PCs can no longer find this one is already true when it goes.</summary>
public class LanListenerTests
{
    [Fact]
    public async Task Its_port_is_closed_before_disposing_first_returns()
    {
        for (var round = 0; round < 50; round++)
        {
            var listener = new LanListener(IPAddress.Loopback, (_, _) => Task.CompletedTask, NullLogger.Instance);
            listener.Start();
            var port = listener.Port;

            var disposing = listener.DisposeAsync();                            // not awaited: the port must be shut already

            await Should.ThrowAsync<IOException>(() => LanConnector.ConnectAsync(IPAddress.Loopback, port, TimeSpan.FromSeconds(5)));
            await disposing;
        }
    }
}
