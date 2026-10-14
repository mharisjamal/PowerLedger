using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>How the meter turns its counters into watts: by its own clock, from one collection a tick.</summary>
public class EnergyMeterClockTests
{
    /// <summary>Ten joules in picowatt-hours.</summary>
    private const long TenJoules = 2_777_777_778;

    [Fact]
    public void Watts_are_the_energy_over_the_meter_s_own_clock_not_the_service_s()
    {
        var counters = new FakeRailCounters(1).Set(0, energy: 1_000_000_000_000, milliseconds: 10_000);
        var wall = 0L;
        using var meter = new EnergyMeter(["RAPL_Package0_PKG"], counters, () => wall);

        counters.Set(0, energy: 1_000_000_000_000 + TenJoules, milliseconds: 12_000);
        wall += 1_000;                                       // the service's own clock saw one second, the meter two

        meter.Read().PackageW.ShouldNotBeNull().ShouldBe(5.0, 1e-6);
    }

    [Fact]
    public void A_tick_the_meter_s_clock_did_not_move_holds_the_last_reading_and_the_next_spans_both()
    {
        var counters = new FakeRailCounters(1).Set(0, energy: 0, milliseconds: 1_000);
        using var meter = new EnergyMeter(["soc"], counters, () => 0);

        counters.Set(0, energy: TenJoules, milliseconds: 2_000);
        meter.Read().PackageW.ShouldNotBeNull().ShouldBe(10.0, 1e-6);

        // A meter that updates about once a second, as Snapdragon's firmware does: nothing new this tick.
        meter.Read().PackageW.ShouldNotBeNull().ShouldBe(10.0, 1e-6);

        counters.Set(0, energy: 2 * TenJoules, milliseconds: 4_000);
        meter.Read().PackageW.ShouldNotBeNull().ShouldBe(5.0, 1e-6);
    }

    [Fact]
    public void A_tick_on_which_no_rail_s_energy_moved_is_stale_even_when_the_clock_moved()
    {
        var counters = new FakeRailCounters(2).Set(0, energy: 0, milliseconds: 1_000).Set(1, energy: 0, milliseconds: 1_000);
        using var meter = new EnergyMeter(["RAPL_Package0_PKG", "RAPL_Package0_PP1"], counters, () => 0);

        counters.Set(0, energy: TenJoules, milliseconds: 2_000).Set(1, energy: TenJoules / 10, milliseconds: 2_000);
        meter.Read().PackageW.ShouldNotBeNull().ShouldBe(10.0, 1e-6);

        counters.Set(0, energy: TenJoules, milliseconds: 3_000).Set(1, energy: TenJoules / 10, milliseconds: 3_000);
        var stale = meter.Read();
        stale.PackageW.ShouldNotBeNull().ShouldBe(10.0, 1e-6);
        stale.IntegratedGpuW.ShouldNotBeNull().ShouldBe(1.0, 1e-6);

        counters.Set(0, energy: 3 * TenJoules, milliseconds: 4_000).Set(1, energy: 3 * TenJoules / 10, milliseconds: 4_000);
        var fresh = meter.Read();
        fresh.PackageW.ShouldNotBeNull().ShouldBe(10.0, 1e-6);
        fresh.IntegratedGpuW.ShouldNotBeNull().ShouldBe(1.0, 1e-6);
    }

    [Fact]
    public void A_rail_that_never_moves_beside_one_that_does_reads_a_genuine_zero()
    {
        var counters = new FakeRailCounters(2).Set(0, energy: 0, milliseconds: 1_000).Set(1, energy: 0, milliseconds: 1_000);
        using var meter = new EnergyMeter(["RAPL_Package0_PKG", "RAPL_Package0_DRAM"], counters, () => 0);

        counters.Set(0, energy: TenJoules, milliseconds: 2_000).Set(1, energy: 0, milliseconds: 2_000);
        var reading = meter.Read();

        reading.PackageW.ShouldNotBeNull().ShouldBe(10.0, 1e-6);
        reading.MemoryW.ShouldBe(0);
    }

    [Fact]
    public void A_counter_that_went_backwards_reports_nothing_and_starts_again_from_there()
    {
        var counters = new FakeRailCounters(1).Set(0, energy: 5 * TenJoules, milliseconds: 1_000);
        using var meter = new EnergyMeter(["RAPL_Package0_PKG"], counters, () => 0);

        counters.Set(0, energy: TenJoules, milliseconds: 2_000);
        meter.Read().PackageW.ShouldBeNull();

        counters.Set(0, energy: 2 * TenJoules, milliseconds: 3_000);
        meter.Read().PackageW.ShouldNotBeNull().ShouldBe(10.0, 1e-6);
    }

    [Fact]
    public void Without_the_meter_s_clock_the_service_s_own_is_used()
    {
        var counters = new FakeRailCounters(1).Set(0, energy: 0, milliseconds: null);
        var wall = 50_000L;
        using var meter = new EnergyMeter(["RAPL_Package0_PKG"], counters, () => wall);

        counters.Set(0, energy: TenJoules, milliseconds: null);
        wall += 4_000;

        meter.Read().PackageW.ShouldNotBeNull().ShouldBe(2.5, 1e-6);
    }

    [Fact]
    public void A_rail_windows_gave_nothing_for_this_tick_costs_its_own_figure_only()
    {
        var counters = new FakeRailCounters(2).Set(0, energy: 0, milliseconds: 1_000).Set(1, energy: 0, milliseconds: 1_000);
        using var meter = new EnergyMeter(["RAPL_Package0_PKG", "RAPL_Package0_PP1"], counters, () => 0);

        counters.Set(0, energy: TenJoules, milliseconds: 2_000).Missing(1);
        var reading = meter.Read();

        reading.PackageW.ShouldNotBeNull().ShouldBe(10.0, 1e-6);
        reading.IntegratedGpuW.ShouldBeNull();
    }

    [Fact]
    public void Every_rail_is_read_in_one_collection_a_tick()
    {
        var counters = new FakeRailCounters(4);
        for (var rail = 0; rail < 4; rail++) counters.Set(rail, energy: 0, milliseconds: 1_000);
        using var meter = new EnergyMeter(["RAPL_Package0_PKG", "RAPL_Package0_PP0", "RAPL_Package0_PP1", "RAPL_Package0_DRAM"], counters, () => 0);
        counters.Collections.ShouldBe(1);                    // the first reading, taken when the meter opens

        for (var tick = 2; tick <= 4; tick++)
        {
            for (var rail = 0; rail < 4; rail++) counters.Set(rail, energy: tick * TenJoules, milliseconds: tick * 1_000);
            meter.Read();
        }

        counters.Collections.ShouldBe(4);
    }

    [Fact]
    public void Closing_the_meter_closes_its_counters()
    {
        var counters = new FakeRailCounters(1).Set(0, energy: 0, milliseconds: 0);
        var meter = new EnergyMeter(["RAPL_Package0_PKG"], counters, () => 0);

        meter.Dispose();

        counters.Disposed.ShouldBeTrue();
    }
}

/// <summary>Rails whose energy and clock a test sets between ticks.</summary>
internal sealed class FakeRailCounters(int rails) : IRailCounters
{
    private readonly RailCount?[] _counts = new RailCount?[rails];

    public int Collections { get; private set; }

    public bool Disposed { get; private set; }

    public FakeRailCounters Set(int rail, long energy, long? milliseconds)
    {
        _counts[rail] = new RailCount(energy, milliseconds);
        return this;
    }

    public FakeRailCounters Missing(int rail)
    {
        _counts[rail] = null;
        return this;
    }

    public void Read(RailCount?[] counts)
    {
        Collections++;
        _counts.CopyTo(counts, 0);
    }

    public void Dispose() => Disposed = true;
}
