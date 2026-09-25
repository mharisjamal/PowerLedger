using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerLedger.Service.Updates;

/// <summary>
/// A version whose setup didn't install (Plan Q §4): <c>failed.json</c> in the Updates folder. A setup that fails after it
/// has stopped the service leaves the old service to start again, which would otherwise install the same installer again
/// every few minutes, closing the App each time. The version is tried once more a day after its first failure, then left
/// to the App's own updater; a newer release starts afresh.
/// </summary>
/// <param name="Version">The version setup was installing.</param>
/// <param name="Tries">How many times its setup has failed.</param>
/// <param name="AtMs">When it last failed, UTC milliseconds.</param>
internal sealed record FailedUpdate(string Version, int Tries, long AtMs)
{
    /// <summary>The record's file name in the Updates folder.</summary>
    public const string FileName = "failed.json";

    /// <summary>How long after its first failure a version is tried again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(24);

    /// <summary>How many failures a version is given before it is left to the App.</summary>
    public const int MaxTries = 2;

    /// <summary>Whether <paramref name="version"/> is held back at <paramref name="now"/>: this record's version, failed as
    /// often as it may be, or not yet a day since it failed.</summary>
    public bool Holds(Version version, DateTimeOffset now)
        => System.Version.TryParse(Version, out var failed) && failed == version
            && (Tries >= MaxTries || now.ToUnixTimeMilliseconds() < AtMs + (long)RetryAfter.TotalMilliseconds);

    /// <summary>The record once <paramref name="version"/> has failed again at <paramref name="now"/>.</summary>
    public static FailedUpdate After(FailedUpdate? before, string version, DateTimeOffset now)
        => new(version, before?.Version == version ? before.Tries + 1 : 1, now.ToUnixTimeMilliseconds());

    /// <summary>Writes the record into <paramref name="folder"/>.</summary>
    public void Write(string folder) => File.WriteAllBytes(Path.Combine(folder, FileName), JsonSerializer.SerializeToUtf8Bytes(this, FailedJson.Default.FailedUpdate));

    /// <summary>The record in <paramref name="folder"/>, or null when there is none or it can't be read.</summary>
    public static FailedUpdate? Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), FailedJson.Default.FailedUpdate);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Deletes the record in <paramref name="folder"/>, if any.</summary>
    public static void Delete(string folder) => File.Delete(Path.Combine(folder, FileName));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FailedUpdate))]
internal sealed partial class FailedJson : JsonSerializerContext;
