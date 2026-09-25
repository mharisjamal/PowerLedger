using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerLedger.Service.Updates;

/// <summary>
/// What the service leaves itself before it runs setup (Plan Q §4): <c>relaunch.json</c> in the Updates folder, which only
/// SYSTEM and Administrators can write. The service that starts next, the new one or this one again if setup failed, opens
/// the App in the console user's session, with its window only if it was showing, and deletes the note.
/// </summary>
/// <param name="Version">The version setup was installing.</param>
/// <param name="WindowWasVisible">Whether the App's main window was showing when the service closed it.</param>
internal sealed record RelaunchNote(string Version, bool WindowWasVisible)
{
    /// <summary>The note's file name in the Updates folder.</summary>
    public const string FileName = "relaunch.json";

    /// <summary>What the App is started with: <c>--after-update</c>, and <c>--tray</c> unless its window was showing.</summary>
    public string AppArguments => WindowWasVisible ? "--after-update" : "--after-update --tray";

    /// <summary>Writes the note into <paramref name="folder"/>.</summary>
    public void Write(string folder) => File.WriteAllBytes(Path.Combine(folder, FileName), JsonSerializer.SerializeToUtf8Bytes(this, RelaunchJson.Default.RelaunchNote));

    /// <summary>The note in <paramref name="folder"/>, or null when there is none. One that can't be read still asks for the
    /// App to be opened, in the tray.</summary>
    public static RelaunchNote? Read(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), RelaunchJson.Default.RelaunchNote) ?? new RelaunchNote("", false);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return new RelaunchNote("", false);
        }
    }

    /// <summary>Deletes the note in <paramref name="folder"/>, if any.</summary>
    public static void Delete(string folder) => File.Delete(Path.Combine(folder, FileName));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RelaunchNote))]
internal sealed partial class RelaunchJson : JsonSerializerContext;
