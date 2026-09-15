using System.Collections.Concurrent;
using System.Globalization;

namespace PowerLedger.App;

/// <summary>Money in the tariff's currency (spec §10), written the way the reader's culture writes money but with the
/// currency's own symbol. A currency no culture knows is written with its ISO code after the amount.</summary>
internal static class Money
{
    private static readonly ConcurrentDictionary<string, string?> Symbols = new(StringComparer.Ordinal);

    public static string Format(decimal amount, string currency, CultureInfo culture, int decimals = 2)
    {
        if (SymbolFor(currency) is not { } symbol)
        {
            return amount.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), culture) + " " + currency;
        }
        var numbers = (NumberFormatInfo)culture.NumberFormat.Clone();
        numbers.CurrencySymbol = symbol;
        numbers.CurrencyDecimalDigits = decimals;
        return amount.ToString("C", numbers);
    }

    /// <summary>A price per kWh: two decimals, or up to four when the price needs them ("$0.17", "$0.125").</summary>
    public static string Rate(decimal price, string currency, CultureInfo culture)
    {
        var decimals = 2;
        while (decimals < 4 && decimal.Round(price, decimals) != price) decimals++;
        return Format(decimal.Round(price, decimals, MidpointRounding.AwayFromZero), currency, culture, decimals);
    }

    /// <summary>
    /// The symbol for an ISO 4217 code, or null when no region uses it. The reader's own region wins when it uses the
    /// currency, then the currency's home country (the code's first two letters, as with USD and US), then any region.
    /// </summary>
    internal static string? SymbolFor(string currency) => Symbols.GetOrAdd(currency, static code =>
    {
        if (RegionInfo.CurrentRegion.ISOCurrencySymbol == code) return RegionInfo.CurrentRegion.CurrencySymbol;
        if (Region(code[..Math.Min(2, code.Length)]) is { } home && home.ISOCurrencySymbol == code) return home.CurrencySymbol;
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            if (Region(culture.Name) is { } region && region.ISOCurrencySymbol == code) return region.CurrencySymbol;
        }
        return null;
    });

    private static RegionInfo? Region(string name)
    {
        try
        {
            return new RegionInfo(name);
        }
        catch (ArgumentException)
        {
            return null;   // not a region, or a culture with no region of its own
        }
    }
}
