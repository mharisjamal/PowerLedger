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

    /// <summary>Which front end the window opens in (Midnight look design §1): Midnight, the default (the owner's decision,
    /// 2026-09-25), for a new install and for a ui.json from before the look existed; Classic once chosen. A name this
    /// version doesn't know reads as the default rather than failing the whole file. It has a setter rather than init for
    /// the missing case: the JSON source generator gives an init-only property missing from the file its type's default,
    /// which is Classic, where a setter is left alone.</summary>
    [JsonConverter(typeof(LookJsonConverter))]
    public Look Look { get; set; } = Look.Midnight;

    /// <summary>The one-time banner that says this is the new look, with Switch back and Got it, has been retired: by one
    /// of its buttons or by any look switch (Midnight look design §1). It never shows again, in either look.</summary>
    public bool LookIntroduced { get; init; }

    /// <summary>Kilograms of CO₂ per kWh used for every CO₂ figure; spec §9's default is the world average, which a
    /// ui.json without the field keeps. It has a setter rather than init for that: the JSON source generator gives an
    /// init-only property missing from the file its type's default, 0, where a setter is left alone, and
    /// <see cref="Sanitised"/> cannot tell that 0 from a real factor.</summary>
    public double Co2KgPerKwh { get; set; } = Co2.DefaultKgPerKwh;

    /// <summary>The first-run wizard was finished once; it shows until then (spec §9).</summary>
    public bool FirstRunDone { get; init; }

    /// <summary>When the wizard finished (data-sharing design §3), for usage's "days since you started". Backfilled once
    /// for an install that already had <see cref="FirstRunDone"/> before this field existed.</summary>
    public DateTimeOffset? FirstRunAt { get; init; }

    /// <summary>Read each external monitor over its cable, read-only: whether it is on each minute, and its brightness every
    /// five minutes (Plans J and K). It stops only the requests sent to monitors; the refresh rate and HDR state Windows
    /// drives each at are read whatever it says, as that asks no monitor anything. On until the user unticks it; a ui.json
    /// from before it existed keeps it on. It has a setter rather than init for that: the JSON source generator gives an
    /// init-only property missing from the file its type's default, false, where a setter is left alone.</summary>
    public bool ReadMonitorBrightness { get; set; } = true;

    /// <summary>The newest version the tray has announced, so each version is announced once.</summary>
    public string? AnnouncedVersion { get; init; }

    /// <summary>The version that last ran, so the first start of a newer one can say it was updated.</summary>
    public string? LastVersion { get; init; }

    /// <summary>N2's signed-in e-mail, from the ID token, shown on the Household page; never sent anywhere, and never
    /// the account's authority, which is the session the service holds (households design §7).</summary>
    public string? SignedInEmail { get; init; }

    /// <summary>What the Midnight Dashboard's Energy used card covers, as its period menu last chose it. Since start until
    /// the menu is used: a ui.json from before the menu existed has no such field, and nothing older stood for it, so every
    /// PC starts there after the update. A name this version doesn't know reads as the default rather than failing the
    /// whole file; the setter keeps the default for a missing field, as <see cref="Look"/>'s does.</summary>
    [JsonConverter(typeof(EnergyPeriodJsonConverter))]
    public EnergyPeriod EnergyPeriod { get; set; } = EnergyPeriod.SinceStart;

    public static UiPreferences Default { get; } = new();

    /// <summary>The same preferences with anything out of range put back to its default.</summary>
    public UiPreferences Sanitised() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : ThemeChoice.System,
        Look = Enum.IsDefined(Look) ? Look : Default.Look,
        EnergyPeriod = Enum.IsDefined(EnergyPeriod) ? EnergyPeriod : Default.EnergyPeriod,
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

/// <summary>A look by its name, and any name this version doesn't know as the default look, so a ui.json written by a
/// newer App still loads whole; the string-enum converter would refuse the file instead.</summary>
internal sealed class LookJsonConverter : JsonConverter<Look>
{
    public override Look Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && Enum.TryParse<Look>(reader.GetString(), ignoreCase: true, out var look) && Enum.IsDefined(look)
            ? look
            : UiPreferences.Default.Look;

    public override void Write(Utf8JsonWriter writer, Look value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

/// <summary>An energy period by its name, and anything else (a name this version doesn't know, a number, null) as the
/// default, so the file still loads whole.</summary>
internal sealed class EnergyPeriodJsonConverter : JsonConverter<EnergyPeriod>
{
    public override EnergyPeriod Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && Enum.TryParse<EnergyPeriod>(reader.GetString(), ignoreCase: true, out var period)
           && Enum.IsDefined(period) && !int.TryParse(reader.GetString(), out _)
            ? period
            : UiPreferences.Default.EnergyPeriod;

    public override void Write(Utf8JsonWriter writer, EnergyPeriod value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(UiPreferences))]
internal sealed partial class UiJson : JsonSerializerContext;
