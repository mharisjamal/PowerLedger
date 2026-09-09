namespace PowerLedger.Storage;

/// <summary>Service-owned settings. Keys are dotted names such as "tariff.currency"; values are strings.</summary>
public sealed class SettingsRepository(SqliteDatabase db)
{
    public string? Get(string key)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        Rows.Add(cmd, "$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO settings(key, value) VALUES ($key, $value)";
        Rows.Add(cmd, "$key", key);
        Rows.Add(cmd, "$value", value);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, string> All()
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM settings";
        using var r = cmd.ExecuteReader();
        var dict = new Dictionary<string, string>();
        while (r.Read()) dict[r.GetString(0)] = r.GetString(1);
        return dict;
    }
}
