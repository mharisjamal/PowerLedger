using Microsoft.Data.Sqlite;

namespace PowerLedger.Storage;

/// <summary>Small helpers shared by the repositories. Microsoft.Data.Sqlite refuses to bind NaN, so callers never pass non-finite doubles (the integrator guards them upstream).</summary>
internal static class Rows
{
    public static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    public static DateTimeOffset Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    /// <summary>Price to integer micro-units: six decimals, halves rounded away from zero.</summary>
    public static long Micro(decimal price) => (long)decimal.Round(price * 1_000_000m, MidpointRounding.AwayFromZero);

    public static decimal Price(long micro) => micro / 1_000_000m;

    public static double? NullableDouble(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public static void Add(SqliteCommand cmd, string name, object? value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
