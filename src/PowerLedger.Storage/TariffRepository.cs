using PowerLedger.Core;

namespace PowerLedger.Storage;

/// <summary>Tariff history. Prices are stored as integer micro-units; ordering by (effective_from, id) lets a later-inserted tariff win a tie.</summary>
public sealed class TariffRepository(SqliteDatabase db)
{
    /// <summary>Appends a tariff. Existing rows are never modified, so history stays intact.</summary>
    public void Add(Tariff tariff)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO tariffs(effective_from_ms, price_micro, currency) VALUES ($from, $price, $currency)";
        Rows.Add(cmd, "$from", Rows.Ms(tariff.EffectiveFrom));
        Rows.Add(cmd, "$price", Rows.Micro(tariff.PricePerKwh));
        Rows.Add(cmd, "$currency", tariff.Currency);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every tariff, oldest first.</summary>
    public List<Tariff> All()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT effective_from_ms, price_micro, currency FROM tariffs ORDER BY effective_from_ms, id";
        using var r = cmd.ExecuteReader();
        var list = new List<Tariff>();
        while (r.Read()) list.Add(new Tariff(Rows.Time(r.GetInt64(0)), Rows.Price(r.GetInt64(1)), r.GetString(2)));
        return list;
    }

    /// <summary>A schedule over every stored tariff. Build one per report and reuse it.</summary>
    public TariffSchedule Schedule() => new(All());
}
