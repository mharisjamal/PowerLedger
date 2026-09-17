using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>
/// The tick the sensor thread asks before it says anything to a power supply. The loop replaces its settings whenever
/// the App applies new ones, so the answer comes from the status board, which is written for any thread to read.
/// </summary>
public class PowerSupplySwitchTests
{
    [Fact]
    public void Nothing_is_read_until_the_loop_has_put_settings_in_force()
    {
        var board = new StatusBoard();

        ServiceHost.PowerSupplySwitch(board, CancellationToken.None)().ShouldBeFalse();
    }

    [Fact]
    public void The_answer_follows_the_settings_the_loop_last_published()
    {
        var board = new StatusBoard();
        var reading = ServiceHost.PowerSupplySwitch(board, CancellationToken.None);

        board.Publish(Settings(readPowerSupply: true));
        reading().ShouldBeTrue();

        board.Publish(Settings(readPowerSupply: false));
        reading().ShouldBeFalse();

        board.Publish(Settings(readPowerSupply: true));
        reading().ShouldBeTrue();
    }

    [Fact]
    public void A_sensor_set_that_has_been_retired_says_nothing_more_to_the_device()
    {
        var board = new StatusBoard();
        board.Publish(Settings(readPowerSupply: true));
        using var retired = new CancellationTokenSource();
        var reading = ServiceHost.PowerSupplySwitch(board, retired.Token);
        reading().ShouldBeTrue();

        retired.Cancel();

        reading().ShouldBeFalse();
    }

    private static ServiceSettings Settings(bool readPowerSupply)
        => ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop with { ReadPowerSupply = readPowerSupply } };
}
