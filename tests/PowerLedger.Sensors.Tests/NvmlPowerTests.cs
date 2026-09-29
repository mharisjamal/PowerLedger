using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

public class NvmlPowerTests
{
    private static (FakeNvmlDevice Device, FakeClock Clock, NvmlPower Power) Card(ulong energy = 5_000_000, uint milliwatts = 140_000)
    {
        var device = new FakeNvmlDevice { EnergyMillijoules = energy, PowerMilliwatts = milliwatts };
        var clock = new FakeClock();
        return (device, clock, new NvmlPower(device, clock.Read));
    }

    [Fact]
    public void The_energy_counter_gives_the_exact_average_between_two_reads()
    {
        var (device, clock, power) = Card();

        // The first read has no interval yet, so it takes the power figure.
        power.Read().PowerWatts.ShouldBe(140.0);

        // 150 J over one second is 150 W, whatever the power figure's own one-second average says.
        clock.Advance(1);
        device.EnergyMillijoules += 150_000;
        var reading = power.Read();
        reading.PowerWatts.ShouldNotBeNull().ShouldBe(150.0, 1e-9);
        reading.LoadFraction.ShouldBe(0.4);
        reading.Present.ShouldBeTrue();

        clock.Advance(2);
        device.EnergyMillijoules += 90_000;
        power.Read().PowerWatts.ShouldNotBeNull().ShouldBe(45.0, 1e-9);
    }

    [Fact]
    public void A_card_before_volta_has_no_counter_and_keeps_the_power_figure_without_being_asked_again()
    {
        var (device, clock, power) = Card();
        device.EnergyResult = FakeNvmlDevice.NotSupported;

        power.Read().PowerWatts.ShouldBe(140.0);
        clock.Advance(1);
        device.PowerMilliwatts = 95_500;
        power.Read().PowerWatts.ShouldBe(95.5);
        device.EnergyCalls.ShouldBe(1);
    }

    [Fact]
    public void A_driver_too_old_for_the_call_keeps_the_power_figure()
    {
        var (device, clock, power) = Card();
        device.EnergyThrows = new EntryPointNotFoundException("nvmlDeviceGetTotalEnergyConsumption");

        power.Read().PowerWatts.ShouldBe(140.0);
        clock.Advance(1);
        power.Read().PowerWatts.ShouldBe(140.0);
        device.EnergyCalls.ShouldBe(1);
    }

    [Fact]
    public void A_counter_that_went_backwards_was_reset_and_starts_the_next_interval()
    {
        // The counter runs from the driver's load, so a driver restart or a wrap brings it back down.
        var (device, clock, power) = Card(energy: 9_000_000);
        power.Read();

        clock.Advance(1);
        device.EnergyMillijoules = 20_000;
        power.Read().PowerWatts.ShouldBe(140.0);

        clock.Advance(1);
        device.EnergyMillijoules = 20_000 + 60_000;
        power.Read().PowerWatts.ShouldNotBeNull().ShouldBe(60.0, 1e-9);
    }

    [Fact]
    public void A_counter_that_declined_once_takes_the_power_figure_and_starts_again()
    {
        var (device, clock, power) = Card();
        power.Read();

        clock.Advance(1);
        device.EnergyMillijoules += 150_000;
        device.EnergyResult = FakeNvmlDevice.Timeout;
        power.Read().PowerWatts.ShouldBe(140.0);

        // The interval restarts, so the energy across the declined read is never spread over the wrong time.
        device.EnergyResult = FakeNvmlDevice.Success;
        clock.Advance(1);
        device.EnergyMillijoules += 100_000;
        power.Read().PowerWatts.ShouldBe(140.0);

        clock.Advance(1);
        device.EnergyMillijoules += 100_000;
        power.Read().PowerWatts.ShouldNotBeNull().ShouldBe(100.0, 1e-9);
        device.EnergyCalls.ShouldBe(4);
    }

    [Fact]
    public void A_long_gap_as_while_windows_had_the_card_off_starts_the_next_interval()
    {
        // Averaged over a gap of many ticks, the energy would be charged to one tick at the gap's average.
        var (device, clock, power) = Card();
        power.Read();

        clock.Advance(NvmlPower.LongestInterval.TotalSeconds + 1);
        device.EnergyMillijoules += 400_000;
        power.Read().PowerWatts.ShouldBe(140.0);

        clock.Advance(1);
        device.EnergyMillijoules += 70_000;
        power.Read().PowerWatts.ShouldNotBeNull().ShouldBe(70.0, 1e-9);
    }

    [Fact]
    public void A_read_too_soon_after_the_last_keeps_the_interval_open_rather_than_dividing_by_almost_nothing()
    {
        var (device, clock, power) = Card();
        power.Read();

        clock.Advance(0.01);
        device.EnergyMillijoules += 3_000;
        power.Read().PowerWatts.ShouldBe(140.0);

        clock.Advance(0.99);
        device.EnergyMillijoules += 147_000;
        power.Read().PowerWatts.ShouldNotBeNull().ShouldBe(150.0, 1e-9);
    }

    [Fact]
    public void A_lost_gpu_or_an_unloaded_driver_is_a_broken_source_not_a_missing_reading()
    {
        var (device, _, power) = Card();
        device.EnergyResult = FakeNvmlDevice.GpuIsLost;
        Should.Throw<InvalidOperationException>(() => power.Read());

        var (other, _, second) = Card();
        other.EnergyResult = FakeNvmlDevice.NotSupported;
        other.PowerResult = FakeNvmlDevice.Uninitialized;
        Should.Throw<InvalidOperationException>(() => second.Read());
    }

    [Fact]
    public void A_card_that_measures_nothing_gives_no_watts_and_still_its_load()
    {
        var (device, _, power) = Card();
        device.EnergyResult = FakeNvmlDevice.NotSupported;
        device.PowerResult = FakeNvmlDevice.NotSupported;

        var reading = power.Read();
        reading.PowerWatts.ShouldBeNull();
        reading.LoadFraction.ShouldBe(0.4);

        device.LoadResult = FakeNvmlDevice.NotSupported;
        power.Read().LoadFraction.ShouldBeNull();
    }

    [Fact]
    public void Each_card_of_several_keeps_its_own_interval()
    {
        var clock = new FakeClock();
        var first = new FakeNvmlDevice { EnergyMillijoules = 1_000_000, PowerMilliwatts = 200_000 };
        var second = new FakeNvmlDevice { EnergyMillijoules = 50_000, PowerMilliwatts = 30_000 };
        var one = new NvmlPower(first, clock.Read);
        var two = new NvmlPower(second, clock.Read);
        one.Read();
        two.Read();

        clock.Advance(1);
        first.EnergyMillijoules += 250_000;
        second.EnergyMillijoules = 10;           // the second card's driver instance was reset; the first's is untouched
        one.Read().PowerWatts.ShouldNotBeNull().ShouldBe(250.0, 1e-9);
        two.Read().PowerWatts.ShouldBe(30.0);

        clock.Advance(1);
        first.EnergyMillijoules += 240_000;
        second.EnergyMillijoules += 25_000;
        one.Read().PowerWatts.ShouldNotBeNull().ShouldBe(240.0, 1e-9);
        two.Read().PowerWatts.ShouldNotBeNull().ShouldBe(25.0, 1e-9);
    }

    [Fact]
    public void Two_cards_read_from_their_counters_are_both_added_to_the_draft()
    {
        var clock = new FakeClock();
        var first = new FakeNvmlDevice { EnergyMillijoules = 1_000_000, PowerMilliwatts = 200_000 };
        var second = new FakeNvmlDevice { EnergyMillijoules = 50_000, PowerMilliwatts = 30_000 };
        var one = new NvmlPower(first, clock.Read);
        var two = new NvmlPower(second, clock.Read);
        var source = new NvidiaSource([
            new NvidiaCard("NVIDIA GeForce RTX 4090", 0x2684, null, one.Read, static () => false),
            new NvidiaCard("NVIDIA GeForce RTX 4070", 0x2786, null, two.Read, static () => false),
        ], present: true, unavailable: null, loadsEveryCard: true);
        source.Contribute(new SampleDraft());

        clock.Advance(1);
        first.EnergyMillijoules += 300_000;
        second.EnergyMillijoules += 120_000;
        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.Gpus.Count.ShouldBe(2);
        draft.Gpus[0].Watts.ShouldNotBeNull().ShouldBe(300.0, 1e-9);
        draft.Gpus[1].Watts.ShouldNotBeNull().ShouldBe(120.0, 1e-9);
        draft.DGpuW.ShouldNotBeNull().ShouldBe(420.0, 1e-9);
    }
}
