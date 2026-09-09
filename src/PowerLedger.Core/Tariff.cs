namespace PowerLedger.Core;

/// <summary>One price per kWh from a point in time onward (spec §7). Money is decimal end to end.</summary>
/// <param name="Currency">ISO 4217 code, e.g. "USD".</param>
public sealed record Tariff(DateTimeOffset EffectiveFrom, decimal PricePerKwh, string Currency);
