namespace PowerLedger.Core;

/// <param name="Amount">Sum of the slices priced in <paramref name="Currency"/>, rounded to six decimals (the precision tariffs are stored at).</param>
/// <param name="Currency">Currency of the latest-effective tariff; null when the schedule is empty.</param>
/// <param name="Partial">True when some slices were priced under a tariff in a different currency and left out.</param>
public sealed record CostResult(decimal Amount, string? Currency, bool Partial);

/// <summary>
/// Spec §7: cost is computed at query time from the tariff in force when the energy was used.
/// A currency change starts a new cost history: slices priced under another currency are excluded and flagged.
/// </summary>
public sealed class TariffSchedule
{
    private readonly List<Tariff> _tariffs;

    /// <summary>Tariffs in any order. Ties on EffectiveFrom keep input order, so the later-listed tariff wins (TariffRepository orders by effective_from, id).</summary>
    public TariffSchedule(IEnumerable<Tariff> tariffs)
        => _tariffs = tariffs.OrderBy(t => t.EffectiveFrom).ToList();

    /// <summary>Currency of the latest-effective tariff (not the most recently entered one, once backdating is involved); null when empty.</summary>
    public string? Currency => _tariffs.Count == 0 ? null : _tariffs[^1].Currency;

    /// <summary>True when the schedule holds tariffs in more than one currency.</summary>
    public bool HasMixedCurrencies => _tariffs.Select(t => t.Currency).Distinct().Count() > 1;

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

    /// <summary>Prices each slice by the tariff in force at its start (a slice straddling a change is wholly priced at the older rate).
    /// Non-finite energy is ignored; slices priced in another currency are skipped and reported through Partial.
    /// The total is rounded to six decimals, which absorbs the floating-point residue of energy accumulated tick by tick.</summary>
    public CostResult Cost(IEnumerable<(DateTimeOffset Start, double Wh)> energy)
    {
        decimal total = 0;
        var partial = false;
        foreach (var (start, wh) in energy)
        {
            if (!double.IsFinite(wh)) continue;
            if (At(start) is not { } tariff) continue;
            if (tariff.Currency != Currency)
            {
                partial = true;
                continue;
            }
            total += (decimal)wh / 1000m * tariff.PricePerKwh;
        }
        return new CostResult(decimal.Round(total, 6, MidpointRounding.AwayFromZero), Currency, partial);
    }
}
