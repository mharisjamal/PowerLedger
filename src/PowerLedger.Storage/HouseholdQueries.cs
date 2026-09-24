using PowerLedger.Contracts;

namespace PowerLedger.Storage;

/// <summary>One member of the household, as household_members holds it (households design §1).</summary>
public sealed record HouseholdMemberRow(
    string DeviceId, string Name, ChassisKind Kind, DateTimeOffset Added, DateTimeOffset? Left, DateTimeOffset? LastSynced);

/// <summary>What the household spent in one currency over a range.</summary>
public sealed record CurrencyCost(string Currency, decimal Cost);

/// <summary>One PC's energy over a range, for the bar per PC.</summary>
public sealed record DeviceEnergy(string DeviceId, double EnergyKwh);

/// <summary>The household's energy and cost over a range (households design §2): one energy figure, since a watt-hour
/// needs no currency, and cost split by currency, since amounts in different currencies are never added together.</summary>
public sealed record HouseholdRangeTotals(double EnergyKwh, IReadOnlyList<CurrencyCost> Costs, IReadOnlyList<DeviceEnergy> ByDevice);

/// <summary>One PC's own report over a range (Plan N task A5: a page per member PC), the same bands the main report
/// uses, from its rows in household_rows.</summary>
public sealed record DeviceReport(
    string DeviceId, double EnergyKwh, double CpuKwh, double GpuKwh, double DisplayKwh, double RestKwh, IReadOnlyList<CurrencyCost> Costs);

/// <summary>Read side of the Household page (households design §2, Plan N task A1): every member, and totals over a range,
/// from household_rows and household_members (Storage V3, task C1). The App reads these tables read-only, as it does the
/// rest of history; each row already carries its own cost and currency, priced by the PC that made it, so no tariff
/// lookup happens here.</summary>
public sealed class HouseholdQueries(SqliteDatabase db)
{
    /// <summary>Every member, oldest first, whether or not it has left.</summary>
    public IReadOnlyList<HouseholdMemberRow> Members()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT device_id, name, kind, added_ms, left_ms, last_synced_ms FROM household_members ORDER BY added_ms";
        using var reader = cmd.ExecuteReader();
        var rows = new List<HouseholdMemberRow>();
        while (reader.Read())
        {
            rows.Add(new HouseholdMemberRow(
                reader.GetString(0),
                reader.GetString(1),
                (ChassisKind)reader.GetInt32(2),
                Rows.Time(reader.GetInt64(3)),
                Rows.NullableLong(reader, 4) is { } left ? Rows.Time(left) : null,
                Rows.NullableLong(reader, 5) is { } synced ? Rows.Time(synced) : null));
        }
        return rows;
    }

    /// <summary>Energy and cost for every hour in [<paramref name="from"/>, <paramref name="to"/>), across every member
    /// that has ever synced a row, whether or not it is still in the household.</summary>
    public HouseholdRangeTotals Totals(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT device_id, energy_wh, cost_micro, currency FROM household_rows WHERE hour_ms >= $from AND hour_ms < $to";
        Rows.Add(cmd, "$from", Rows.Ms(from));
        Rows.Add(cmd, "$to", Rows.Ms(to));

        var rows = new List<(string Device, double EnergyWh, long? CostMicro, string? Currency)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetDouble(1), Rows.NullableLong(reader, 2), Rows.NullableString(reader, 3)));
        }

        var costs = rows
            .Where(r => r.Currency is not null && r.CostMicro is not null)
            .GroupBy(r => r.Currency!, StringComparer.Ordinal)
            .Select(g => new CurrencyCost(g.Key, Rows.Price(g.Sum(r => r.CostMicro!.Value))))
            .OrderBy(c => c.Currency, StringComparer.Ordinal)
            .ToList();
        var byDevice = rows
            .GroupBy(r => r.Device, StringComparer.Ordinal)
            .Select(g => new DeviceEnergy(g.Key, g.Sum(r => r.EnergyWh) / 1000))
            .OrderBy(d => d.DeviceId, StringComparer.Ordinal)
            .ToList();
        return new HouseholdRangeTotals(rows.Sum(r => r.EnergyWh) / 1000, costs, byDevice);
    }

    /// <summary>One report per PC that has rows in [<paramref name="from"/>, <paramref name="to"/>) (Plan N task A5),
    /// each with its own by-component bands and cost by currency.</summary>
    public IReadOnlyList<DeviceReport> DeviceReports(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT device_id, energy_wh, cpu_wh, gpu_wh, display_wh, rest_wh, cost_micro, currency FROM household_rows
            WHERE hour_ms >= $from AND hour_ms < $to
            """;
        Rows.Add(cmd, "$from", Rows.Ms(from));
        Rows.Add(cmd, "$to", Rows.Ms(to));

        var rows = new List<(string Device, double EnergyWh, double CpuWh, double GpuWh, double DisplayWh, double RestWh, long? CostMicro, string? Currency)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4),
                    reader.GetDouble(5), Rows.NullableLong(reader, 6), Rows.NullableString(reader, 7)));
            }
        }

        return [.. rows
            .GroupBy(r => r.Device, StringComparer.Ordinal)
            .Select(g => new DeviceReport(
                g.Key, g.Sum(r => r.EnergyWh) / 1000, g.Sum(r => r.CpuWh) / 1000, g.Sum(r => r.GpuWh) / 1000, g.Sum(r => r.DisplayWh) / 1000,
                g.Sum(r => r.RestWh) / 1000,
                [.. g.Where(r => r.Currency is not null && r.CostMicro is not null)
                    .GroupBy(r => r.Currency!, StringComparer.Ordinal)
                    .Select(cg => new CurrencyCost(cg.Key, Rows.Price(cg.Sum(r => r.CostMicro!.Value))))
                    .OrderBy(cc => cc.Currency, StringComparer.Ordinal)]))
            .OrderBy(d => d.DeviceId, StringComparer.Ordinal)];
    }
}
