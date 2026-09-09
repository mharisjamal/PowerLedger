using PowerLedger.Core;

namespace PowerLedger.Storage;

public sealed class TariffRepository(SqliteDatabase db)
{
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

    public TariffSchedule Schedule() => new(All());
}
