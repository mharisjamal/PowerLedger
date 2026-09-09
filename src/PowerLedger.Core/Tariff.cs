namespace PowerLedger.Core;

/// <param name="Currency">ISO 4217 code, e.g. "USD".</param>
public sealed record Tariff(DateTimeOffset EffectiveFrom, decimal PricePerKwh, string Currency);
