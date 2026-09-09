using Microsoft.Data.Sqlite;

namespace PowerLedger.Storage;

/// <summary>Small helpers shared by the repositories.</summary>
internal static class Rows
{
    public static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    public static DateTimeOffset Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    public static long Micro(decimal price) => (long)decimal.Round(price * 1_000_000m);

    public static decimal Price(long micro) => micro / 1_000_000m;

    public static double? NullableDouble(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public static void Add(SqliteCommand cmd, string name, object? value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
}
