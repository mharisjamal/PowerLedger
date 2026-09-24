using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.Storage.Tests;

public class HouseholdQueriesTests
{
    private static readonly byte[] Key = [1, 2, 3];

    [Fact]
    public void Members_lists_each_one_oldest_first_with_when_it_joined_left_or_last_synced()
    {
        using var t = HouseholdSchema.Create();
        HouseholdSchema.AddMember(t, "bbbb", "Laptop-2", (int)ChassisKind.Laptop, Key, Key, Rows.Ms(Fixtures.T0.AddDays(-1)), lastSyncedMs: Rows.Ms(Fixtures.T0));
        HouseholdSchema.AddMember(t, "aaaa", "Desktop-1", (int)ChassisKind.Desktop, Key, Key, Rows.Ms(Fixtures.T0.AddDays(-2)));
        HouseholdSchema.AddMember(t, "cccc", "Old-PC", (int)ChassisKind.Desktop, Key, Key, Rows.Ms(Fixtures.T0.AddDays(-3)), leftMs: Rows.Ms(Fixtures.T0));

        var members = new HouseholdQueries(t.Db).Members();

        members.Count.ShouldBe(3);
        members[0].DeviceId.ShouldBe("cccc");           // oldest first, by added_ms
        members[1].DeviceId.ShouldBe("aaaa");
        members[2].DeviceId.ShouldBe("bbbb");
        members[2].Name.ShouldBe("Laptop-2");
        members[2].Kind.ShouldBe(ChassisKind.Laptop);
        members[2].LastSynced.ShouldBe(Fixtures.T0);
        members[2].Left.ShouldBeNull();
        members[0].Left.ShouldBe(Fixtures.T0);
    }

    [Fact]
    public void Totals_sums_energy_across_every_member_in_the_range()
    {
        using var t = HouseholdSchema.Create();
        HouseholdSchema.AddRow(t, "aaaa", Rows.Ms(Fixtures.T0), 1000, 200_000, "USD", Rows.Ms(Fixtures.T0));
        HouseholdSchema.AddRow(t, "bbbb", Rows.Ms(Fixtures.T0), 500, 100_000, "USD", Rows.Ms(Fixtures.T0));

        var totals = new HouseholdQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(1));

        totals.EnergyKwh.ShouldBe(1.5, 1e-9);
    }

    [Fact]
    public void Totals_splits_cost_by_currency_never_adding_them_together()
    {
        using var t = HouseholdSchema.Create();
        HouseholdSchema.AddRow(t, "aaaa", Rows.Ms(Fixtures.T0), 1000, 200_000, "USD", Rows.Ms(Fixtures.T0));
        HouseholdSchema.AddRow(t, "bbbb", Rows.Ms(Fixtures.T0), 1000, 150_000, "EUR", Rows.Ms(Fixtures.T0));
        HouseholdSchema.AddRow(t, "aaaa", Rows.Ms(Fixtures.T0.AddHours(1)), 1000, 50_000, "USD", Rows.Ms(Fixtures.T0));

        var totals = new HouseholdQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(2));

        totals.Costs.Count.ShouldBe(2);
        totals.Costs[0].Currency.ShouldBe("EUR");
        totals.Costs[0].Cost.ShouldBe(0.15m);
        totals.Costs[1].Currency.ShouldBe("USD");
        totals.Costs[1].Cost.ShouldBe(0.25m);
    }

    [Fact]
    public void Totals_gives_one_energy_bar_per_pc()
    {
        using var t = HouseholdSchema.Create();
        HouseholdSchema.AddRow(t, "aaaa", Rows.Ms(Fixtures.T0), 3000, 0, "USD", Rows.Ms(Fixtures.T0));
        HouseholdSchema.AddRow(t, "bbbb", Rows.Ms(Fixtures.T0), 1000, 0, "USD", Rows.Ms(Fixtures.T0));

        var totals = new HouseholdQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(1));

        totals.ByDevice.Count.ShouldBe(2);
        totals.ByDevice[0].DeviceId.ShouldBe("aaaa");
        totals.ByDevice[0].EnergyKwh.ShouldBe(3.0, 1e-9);
        totals.ByDevice[1].DeviceId.ShouldBe("bbbb");
        totals.ByDevice[1].EnergyKwh.ShouldBe(1.0, 1e-9);
    }

    [Fact]
    public void A_row_outside_the_range_is_left_out()
    {
        using var t = HouseholdSchema.Create();
        HouseholdSchema.AddRow(t, "aaaa", Rows.Ms(Fixtures.T0.AddHours(-1)), 1000, 0, "USD", Rows.Ms(Fixtures.T0));
        HouseholdSchema.AddRow(t, "aaaa", Rows.Ms(Fixtures.T0.AddHours(1)), 1000, 0, "USD", Rows.Ms(Fixtures.T0));

        var totals = new HouseholdQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(1));

        totals.EnergyKwh.ShouldBe(0);
        totals.ByDevice.ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_household_totals_to_zero_with_nothing_to_show()
    {
        using var t = HouseholdSchema.Create();
        var totals = new HouseholdQueries(t.Db).Totals(Fixtures.T0, Fixtures.T0.AddHours(1));

        totals.EnergyKwh.ShouldBe(0);
        totals.Costs.ShouldBeEmpty();
        totals.ByDevice.ShouldBeEmpty();
        new HouseholdQueries(t.Db).Members().ShouldBeEmpty();
    }

    [Fact]
    public void Device_reports_sum_each_bands_energy_and_cost_per_pc_for_the_report()
    {
        using var t = HouseholdSchema.Create();
        HouseholdSchema.AddRow(t, "aaaa", Rows.Ms(Fixtures.T0), 1000, 200_000, "USD", Rows.Ms(Fixtures.T0), cpuWh: 400, gpuWh: 100, displayWh: 50, restWh: 450);
        HouseholdSchema.AddRow(t, "aaaa", Rows.Ms(Fixtures.T0.AddHours(1)), 500, 50_000, "USD", Rows.Ms(Fixtures.T0), cpuWh: 200, gpuWh: 50, displayWh: 25, restWh: 225);
        HouseholdSchema.AddRow(t, "bbbb", Rows.Ms(Fixtures.T0), 300, 30_000, "EUR", Rows.Ms(Fixtures.T0), cpuWh: 100, gpuWh: 50, displayWh: 30, restWh: 120);

        var reports = new HouseholdQueries(t.Db).DeviceReports(Fixtures.T0, Fixtures.T0.AddHours(2));

        reports.Count.ShouldBe(2);
        var a = reports.Single(r => r.DeviceId == "aaaa");
        a.EnergyKwh.ShouldBe(1.5, 1e-9);
        a.CpuKwh.ShouldBe(0.6, 1e-9);
        a.GpuKwh.ShouldBe(0.15, 1e-9);
        a.DisplayKwh.ShouldBe(0.075, 1e-9);
        a.RestKwh.ShouldBe(0.675, 1e-9);
        a.Costs.ShouldBe([new CurrencyCost("USD", 0.25m)]);

        var b = reports.Single(r => r.DeviceId == "bbbb");
        b.EnergyKwh.ShouldBe(0.3, 1e-9);
        b.Costs.ShouldBe([new CurrencyCost("EUR", 0.03m)]);
    }

    [Fact]
    public void Device_reports_over_an_empty_range_is_empty()
    {
        using var t = HouseholdSchema.Create();
        new HouseholdQueries(t.Db).DeviceReports(Fixtures.T0, Fixtures.T0.AddHours(1)).ShouldBeEmpty();
    }
}
