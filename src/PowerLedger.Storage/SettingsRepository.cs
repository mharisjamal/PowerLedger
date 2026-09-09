namespace PowerLedger.Storage;

/// <summary>Service-owned settings. Keys are dotted names such as "tariff.currency"; values are strings.</summary>
public sealed class SettingsRepository(SqliteDatabase db)
{
    /// <summary>The stored value, or null when the key is absent.</summary>
    public string? Get(string key)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        Rows.Add(cmd, "$key", key);
        return (string?)cmd.ExecuteScalar();
    }

    /// <summary>Writes or replaces one key.</summary>
    public void Set(string key, string value)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO settings(key, value) VALUES ($key, $value)";
        Rows.Add(cmd, "$key", key);
        Rows.Add(cmd, "$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every setting as a key/value map.</summary>
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
