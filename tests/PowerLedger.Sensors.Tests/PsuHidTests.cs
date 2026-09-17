using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>
/// How one overlapped transfer to a power supply ends. This is the one place a sensor thread could be blocked for good
/// — a device whose driver has wedged never finishes the transfer it was told to call off — so every wait here is
/// bounded, and the memory Windows may still own is let go of rather than freed under it.
/// </summary>
public class PsuHidTests
{
    [Fact]
    public void A_transfer_that_finishes_in_time_is_simply_waited_out()
    {
        var cancels = 0;

        var ended = WindowsHidPort.Settle(_ => true, () => cancels++, TimeSpan.FromMilliseconds(500));

        ended.ShouldBe(Transferred.Finished);
        cancels.ShouldBe(0);
    }

    [Fact]
    public void A_transfer_that_runs_out_of_time_is_called_off_and_waited_out()
    {
        var waits = new List<TimeSpan>();
        var cancels = 0;

        var ended = WindowsHidPort.Settle(
            timeout =>
            {
                waits.Add(timeout);
                return waits.Count > 1;                      // the cancel finishes, as Windows normally makes it
            },
            () => cancels++,
            TimeSpan.FromMilliseconds(500));

        ended.ShouldBe(Transferred.CalledOff);
        cancels.ShouldBe(1);
        waits[0].ShouldBe(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void A_device_that_will_not_even_finish_the_cancel_is_let_go_of_rather_than_waited_for()
    {
        var waits = new List<TimeSpan>();

        var ended = WindowsHidPort.Settle(
            timeout =>
            {
                waits.Add(timeout);
                return false;                                // the driver has wedged: nothing ever completes
            },
            static () => { },
            TimeSpan.FromMilliseconds(500));

        ended.ShouldBe(Transferred.Abandoned);

        // Both waits are bounded, so the sensor thread comes back whatever the device does. This is the finding:
        // the old code waited on the cancel with no timeout at all and a wedged supply held the thread for ever.
        waits.Count.ShouldBe(2);
        waits.ShouldAllBe(wait => wait > TimeSpan.Zero && wait < TimeSpan.FromSeconds(30));
        waits[1].ShouldBe(WindowsHidPort.CancelWait);
    }

    [Fact]
    public void The_bounded_waits_are_short_enough_that_a_tick_is_not_held_up()
    {
        // A read cycle has two seconds; one called-off transfer must fit inside it beside the reply timeout.
        (PsuWire.ReplyTimeout + WindowsHidPort.CancelWait).ShouldBeLessThan(PsuSession.Budget);
    }

    [Fact]
    public void A_block_of_transfer_memory_is_freed_when_it_is_let_go_of()
    {
        var block = new NativeBlock(64);

        block.IsInvalid.ShouldBeFalse();
        block.Address.ShouldNotBe(IntPtr.Zero);

        block.Dispose();

        block.IsClosed.ShouldBeTrue();
        Should.NotThrow(block.Dispose);
    }

    [Fact]
    public void A_block_windows_may_still_be_writing_into_is_let_go_of_without_being_freed()
    {
        var block = new NativeBlock(64);

        block.Abandon();

        // Freeing memory the kernel may still write into would corrupt the heap, so the block is leaked on purpose:
        // it is closed to us, its finalizer will not free it, and disposing it does nothing either.
        block.IsClosed.ShouldBeTrue();
        Should.NotThrow(block.Dispose);
    }
}
