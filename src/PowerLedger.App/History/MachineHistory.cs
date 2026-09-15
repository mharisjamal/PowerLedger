using System.Globalization;
using System.Text.Json;
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>What the service detected about this machine (spec §5), as the wizard and Settings show it.</summary>
internal sealed record DetectedHardware(
    ChassisKind Chassis, string? CpuName, string? GpuName, int RamSticks, bool RamIsDdr5, int SsdCount, int HddCount,
    double DisplayDiagonalInches, int MonitorCount)
{
    /// <summary>"Laptop · Core i7-1165G7 · GeForce MX330 · 2 × DDR4 · 1 SSD · 15.3 in panel · 1 display".</summary>
    public string Summary(CultureInfo culture)
    {
        var parts = new List<string?>
        {
            Chassis == ChassisKind.Laptop ? "Laptop" : "Desktop",
            NowViewModel.ShortName(CpuName),
            NowViewModel.ShortName(GpuName),
            $"{RamSticks.ToString(culture)} × {(RamIsDdr5 ? "DDR5" : "DDR4")}",
            SsdCount > 0 ? $"{SsdCount.ToString(culture)} SSD" : null,
            HddCount > 0 ? $"{HddCount.ToString(culture)} HDD" : null,
            DisplayDiagonalInches > 0 ? $"{DisplayDiagonalInches.ToString("0.#", culture)} in panel" : null,
            $"{MonitorCount.ToString(culture)} {(MonitorCount == 1 ? "display" : "displays")}",
        };
        return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>The service's detection as stored, or null when it cannot be read.</summary>
    internal static DetectedHardware? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            int Int(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
            return new DetectedHardware(
                Int("Chassis") == (int)ChassisKind.Desktop ? ChassisKind.Desktop : ChassisKind.Laptop,
                root.TryGetProperty("CpuName", out var cpu) && cpu.ValueKind == JsonValueKind.String ? cpu.GetString() : null,
                root.TryGetProperty("GpuName", out var gpu) && gpu.ValueKind == JsonValueKind.String ? gpu.GetString() : null,
                Int("RamSticks"),
                root.TryGetProperty("RamIsDdr5", out var ddr5) && ddr5.ValueKind == JsonValueKind.True,
                Int("SsdCount"),
                Int("HddCount"),
                root.TryGetProperty("DisplayDiagonalInches", out var inches) && inches.TryGetDouble(out var size) ? size : 0,
                Int("MonitorCount"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The machine's tariffs and detection, read-only from the service's database.</summary>
internal interface IMachineHistory
{
    /// <summary>Every tariff, oldest first; null when the database cannot be read.</summary>
    IReadOnlyList<Tariff>? Tariffs();

    /// <summary>The newest detection; null when there is none yet, or it cannot be read.</summary>
    DetectedHardware? Detected();
}
