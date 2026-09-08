using PowerLedger.Contracts;

namespace PowerLedger.Core;

/// <summary>Supply efficiency per 80 PLUS tier (spec §5). Unknown tiers from a newer profile fall back to Bronze.</summary>
public static class PsuEfficiency
{
    public static double For(PsuTier tier) => tier switch
    {
        PsuTier.White => 0.82,
        PsuTier.Bronze => 0.85,
        PsuTier.Silver => 0.87,
        PsuTier.Gold => 0.90,
        PsuTier.Platinum => 0.92,
        PsuTier.Titanium => 0.94,
        _ => 0.85,
    };
}
