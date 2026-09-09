namespace PowerLedger.Core;

/// <summary>Spec §7: cost is computed at query time from the tariff in force when the energy was used.</summary>
public sealed class TariffSchedule
{
    private readonly List<Tariff> _tariffs;

    public TariffSchedule(IEnumerable<Tariff> tariffs)
        => _tariffs = tariffs.OrderBy(t => t.EffectiveFrom).ToList();

    public string? Currency => _tariffs.Count == 0 ? null : _tariffs[^1].Currency;

    /// <summary>The latest tariff effective on or before <paramref name="at"/>; energy before the first tariff uses the first one.</summary>
    public Tariff? At(DateTimeOffset at)
    {
        Tariff? best = null;
        foreach (var t in _tariffs)
        {
            if (t.EffectiveFrom <= at) best = t;
            else break;
        }
        return best ?? (_tariffs.Count > 0 ? _tariffs[0] : null);
    }

    public decimal Cost(IEnumerable<(DateTimeOffset Start, double Wh)> energy)
    {
        decimal total = 0;
        foreach (var (start, wh) in energy)
        {
            if (At(start) is not { } tariff) continue;
            total += (decimal)wh / 1000m * tariff.PricePerKwh;
        }
        return total;
    }
}
