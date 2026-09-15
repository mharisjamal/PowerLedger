using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>Which palette to use: Windows' app mode, or one of the two forced.</summary>
internal enum ThemeChoice
{
    System,
    Dark,
    Light,
}

/// <summary>What only the App cares about (spec §9).</summary>
internal sealed record UiPreferences
{
    /// <summary>Grid intensity at or above this is not a real grid; the value is refused.</summary>
    public const double MaxCo2KgPerKwh = 2;

    public ThemeChoice Theme { get; init; } = ThemeChoice.System;

    /// <summary>Kilograms of CO₂ per kWh used for every CO₂ figure; spec §9's default is the world average, which a
    /// ui.json without the field keeps. It has a setter rather than init for that: the JSON source generator gives an
    /// init-only property missing from the file its type's default, 0, where a setter is left alone, and
    /// <see cref="Sanitised"/> cannot tell that 0 from a real factor.</summary>
    public double Co2KgPerKwh { get; set; } = Co2.DefaultKgPerKwh;

    /// <summary>The first-run wizard was finished once; it shows until then (spec §9).</summary>
    public bool FirstRunDone { get; init; }

    /// <summary>Look for new versions every few hours and download them quietly (spec §13). On until the user unticks it;
    /// a ui.json from before it existed keeps it on. It has a setter rather than init for that: the JSON source generator
    /// gives an init-only property missing from the file its type's default, false, where a setter is left alone.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>The newest version the tray has announced, so each version is announced once.</summary>
    public string? AnnouncedVersion { get; init; }

    /// <summary>The version that last ran, so the first start of a newer one can say it was updated.</summary>
    public string? LastVersion { get; init; }

    public static UiPreferences Default { get; } = new();

    /// <summary>The same preferences with anything out of range put back to its default.</summary>
    public UiPreferences Sanitised() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : ThemeChoice.System,
        Co2KgPerKwh = double.IsFinite(Co2KgPerKwh) && Co2KgPerKwh >= 0 && Co2KgPerKwh < MaxCo2KgPerKwh ? Co2KgPerKwh : Co2.DefaultKgPerKwh,
    };
}

/// <summary>Reads and writes ui.json. Reading never fails: a missing or damaged file gives the defaults.</summary>
internal sealed class UiPreferencesStore(string path)
{
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLedger", "ui.json");

    public UiPreferences Load()
    {
        try
        {
            if (!File.Exists(path)) return UiPreferences.Default;
            return (JsonSerializer.Deserialize(File.ReadAllText(path), UiJson.Default.UiPreferences) ?? UiPreferences.Default).Sanitised();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return UiPreferences.Default;
        }
    }

    public void Save(UiPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences, UiJson.Default.UiPreferences));
        File.Move(temporary, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(UiPreferences))]
internal sealed partial class UiJson : JsonSerializerContext;
