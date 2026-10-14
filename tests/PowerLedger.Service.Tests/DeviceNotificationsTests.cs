using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class DeviceNotificationsTests
{
    [Fact]
    public async Task Graphics_adapters_and_batteries_are_followed_and_each_that_comes_or_goes_tells_the_loop()
    {
        var commands = new LoopCommands();
        var notifications = new DeviceNotifications(commands, NullLogger<DeviceNotifications>.Instance);
        await notifications.StartAsync(CancellationToken.None);
        try
        {
            notifications.Registered.ShouldBe(2);
            notifications.OnNotify(IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero, 0);    // arrival
            notifications.OnNotify(IntPtr.Zero, IntPtr.Zero, 1, IntPtr.Zero, 0);    // removal
            notifications.OnNotify(IntPtr.Zero, IntPtr.Zero, 2, IntPtr.Zero, 0);    // a query to remove changes nothing yet
        }
        finally
        {
            await notifications.StopAsync(CancellationToken.None);
        }
        notifications.Registered.ShouldBe(0);

        var told = new List<LoopCommand>();
        while (commands.Reader.TryRead(out var command)) told.Add(command);
        told.Count.ShouldBe(2);
        told.ShouldAllBe(command => command is DevicesChangedCommand);
    }
}
