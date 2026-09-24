using System.Globalization;
using System.Text.RegularExpressions;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;
using PowerLedger.Storage;

namespace PowerLedger.Service.Sharing;

/// <summary>
/// Builds one day's upload (data-sharing design §3). It copies named fields into a new report and never serialises a
/// service object whole, so a field added to one later can't leak. Every text sent is scrubbed of names, paths and device
/// IDs, and cut to what the server takes; a figure outside what the server takes goes as unknown.
/// </summary>
internal static partial class ReportBuilder
{
    public const int SchemaVersion = 1;
    private const int MaxName = 200, MaxError = 2000, MaxSources = 32, MaxCrashes = 20, MaxMonitors = 16, MaxGpus = 8, MaxCount = 1_000_000;

    /// <summary>Each graphics source, with the maker of the cards it reads.</summary>
    private static readonly Dictionary<string, string> GraphicsSources = new(StringComparer.Ordinal)
    {
        ["nvidia-gpu"] = "nvidia",
        ["amd-gpu"] = "amd",
        ["arc-gpu"] = "intel",
    };

    /// <summary>The report: the diagnostics while that switch is on; the usage while it is on and the App sent counts for
    /// the day; the power while it is on and there are minutes or the parts go.</summary>
    public static ReportV1 Build(ReportInputs inputs)
    {
        var consent = inputs.Consent;
        return new ReportV1(
            SchemaVersion, inputs.InstallId, inputs.Host.App, inputs.Host.Windows, inputs.Host.Arch,
            inputs.Settings.Profile.Chassis == ChassisKind.Laptop ? "laptop" : "desktop",
            new ConsentDto(consent.Version, consent.Diagnostics, consent.Usage, consent.Power, consent.Share),
            inputs.Day, inputs.UtcOffsetMinutes,
            consent.Diagnostics ? Diagnostics(inputs) : null,
            consent.Usage && inputs.Events.Usage is { } usage ? Usage(usage) : null,
            consent.Power && (inputs.Minutes.Count > 0 || inputs.WithHardware)
                ? new PowerDto(inputs.WithHardware ? Hardware(inputs) : null, Minutes(inputs.Minutes))
                : null);
    }

    private static DiagnosticsDto Diagnostics(ReportInputs inputs)
    {
        var sources = (inputs.Status?.Sources ?? [])
            .Where(source => source?.Name is not null && SourceId().IsMatch(source.Name))
            .Take(MaxSources)
            .Select(source => Source(source, inputs))
            .ToList();
        var crashes = inputs.Events.Crashes.Take(MaxCrashes).Select(crash => Crash(crash, inputs.Names)).ToList();
        return new DiagnosticsDto(sources, crashes);
    }

    /// <summary>A source's state as the About page words it: not on this machine, failing, a note, or working. It is failing,
    /// with a last error, only from the failures counted into the day, which start when Crash and sensor reports was turned
    /// on: the status's own count and error run from when the sensors were built, which can be before. Only a source the
    /// machine has names the device it reads, and a graphics source only a card of its own maker.</summary>
    private static SourceDto Source(SourceStatus source, ReportInputs inputs)
    {
        var day = inputs.Events.Sources.GetValueOrDefault(source.Name);
        var failures = Math.Clamp(day?.Failures ?? 0, 0, MaxCount);
        var (state, detail) =
            !source.Supported ? ("notOnMachine", source.Unavailable)
            : failures > 0 ? ("failing", null)
            : !string.IsNullOrEmpty(source.Unavailable) ? ("note", source.Unavailable)
            : ("working", null);
        var device = !source.Supported ? null : source.Name switch
        {
            _ when GraphicsSources.TryGetValue(source.Name, out var maker) =>
                inputs.Facts?.GpuNames.FirstOrDefault(gpu => Vendor(gpu) == maker),
            "ups" => DeviceName(inputs.Status, PowerDeviceKind.Ups),
            "power-supply" => DeviceName(inputs.Status, PowerDeviceKind.PowerSupply),
            _ => null,
        };
        return new SourceDto(
            source.Name, state, failures, Text(day?.LastError ?? detail, MaxError, inputs.Names), Text(device, MaxName, inputs.Names));
    }

    private static CrashDto Crash(CrashReport crash, ScrubNames names) => new(
        crash.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        crash.Component == "app" ? "app" : "service",
        ServiceVersion.Plain(crash.Version),
        (crash.Types ?? []).Take(CrashReport.MaxTypes).Select(type => Cut(Scrubber.Scrub(type, names), CrashReport.MaxTypeLength))
            .DefaultIfEmpty("System.Exception").ToList(),
        Cut(Scrubber.Scrub(crash.Message, names), CrashReport.MaxMessageLength),
        Cut(Scrubber.Scrub(crash.Stack, names), CrashReport.MaxStackLength));

    private static UsageDto Usage(UsageCounts usage) => new(
        Count(usage.AppOpens),
        usage.Pages.ToDictionary(page => page.Key, page => Count(page.Value)),
        usage.Settings.ToDictionary(setting => setting.Key, setting => Count(setting.Value)),
        Count(usage.ReportsExported), Count(usage.UpdatesInstalled), Math.Clamp(usage.DaysSinceFirstRun, 0, 36500), usage.Theme, usage.Language);

    /// <summary>The parts, from the detected facts, the profile, the latest status and the tariff in force.</summary>
    public static HardwareDto Hardware(ReportInputs inputs)
    {
        var profile = inputs.Settings.Profile;
        var facts = inputs.Facts;
        var names = inputs.Names;
        var cpu = new CpuDto(
            Text(facts?.CpuName, MaxName, names), Cores: null, Within(inputs.Host.Threads, 1, 2048),
            Watts(profile.CpuTdpOverrideW ?? facts?.CpuTdpW, 1, 1000), TdpTyped: profile.CpuTdpOverrideW is not null);
        // Each card the inventory names is a part of its own. The typed figure rates the only card, as the model has it;
        // beside another card it rates only one the table does not know.
        var cards = facts?.GpuNames ?? [];
        var gpus = cards.Take(MaxGpus).Select(gpu =>
        {
            var table = TdpTable.Bundled.Gpu(gpu);
            var typed = profile.GpuTdpOverrideW is not null && (cards.Count == 1 || table is null);
            return new GpuDto(Vendor(gpu), Text(gpu, MaxName, names), MemoryMb: null, inputs.DiscreteGpu,
                Watts(typed ? profile.GpuTdpOverrideW : table, 1, 2000), TdpTyped: typed);
        }).ToArray();
        var monitors = (inputs.Status?.Monitors ?? []).Take(MaxMonitors).Select(monitor => Monitor(monitor, names)).ToList();
        var psuName = DeviceName(inputs.Status, PowerDeviceKind.PowerSupply);
        var psu = new PsuDto(Tier(profile.PsuTier), Within(PsuEfficiency.RatedWatts(psuName), 50, 5000), Text(psuName, MaxName, names));
        var ups = inputs.Status?.PowerDevices?.FirstOrDefault(device => device.Kind == PowerDeviceKind.Ups) is { } found
            ? new UpsDto(Text(found.Name, MaxName, names), Powers(profile.UpsLoad))
            : null;
        var machine = new ProfileDto(
            Math.Clamp(profile.RamSticks, 0, 64), profile.RamIsDdr5, Math.Clamp(profile.SsdCount, 0, 64), Math.Clamp(profile.HddCount, 0, 64),
            Math.Clamp(profile.FanCount, 0, 64), Watts(profile.ExtrasWatts, 0, 5000) ?? 0, Watts(profile.DisplayDiagonalInches, 1, 150));
        var tariff = inputs.Tariff is { PricePerKwh: >= 0 and <= MaxPricePerKwh } rate && Currency().IsMatch(rate.Currency ?? "")
            ? new TariffDto(rate.PricePerKwh, rate.Currency!)
            : null;
        return new HardwareDto(
            cpu, gpus, Watts(inputs.Host.MemoryGb, 0, 16384), monitors, psu, ups, machine, inputs.Settings.SampleIntervalSeconds, tariff);
    }

    /// <summary>A monitor's make, size, modes and figure. Its instance, its key and any serial number are never copied: the
    /// maker and product codes are read from the instance's second part, <c>DISPLAY\GSM5B7F\…</c>.</summary>
    private static MonitorDto Monitor(MonitorStatus monitor, ScrubNames names)
    {
        var parts = (monitor.Instance ?? "").Split('\\');
        var id = parts.Length > 1 ? parts[1].ToUpperInvariant() : "";
        var known = MonitorId().IsMatch(id);
        return new MonitorDto(
            known ? id[..3] : null, known ? id[3..] : null, Text(monitor.Name, MaxName, names),
            Watts(monitor.Inches, 5, 150), Within(monitor.Width, 1, 20000), Within(monitor.Height, 1, 20000),
            Watts(monitor.RefreshHz, 1, 1000), monitor.Hdr, monitor.OwnPlug, monitor.Counted, Watts(monitor.OnWatts, 0, 1000),
            monitor.Source switch
            {
                MonitorSource.Model => "energyStar",
                MonitorSource.Estimate => "estimated",
                MonitorSource.Typed => "typed",
                _ => "unknown",
            });
    }

    /// <summary>The highest price per kWh the pipe accepts and the schema takes, with room for currencies of small units.</summary>
    private const decimal MaxPricePerKwh = 1_000_000m;

    /// <summary>The minutes as columns: watts and seconds to a tenth, loads and brightness to a thousandth.</summary>
    private static MinutesDto Minutes(IReadOnlyList<MinuteRow> all)
    {
        // A minute the server would refuse would take the whole day with it, so one with a figure past its ranges (a
        // monitor's typed figure absurdly high, say) is left out instead, and so is one whose index doesn't come after the
        // last kept, as the server wants them rising. MinuteBuilder.ForReport counts a day's minutes from one start, so
        // theirs always rise.
        var minutes = new List<MinuteRow>(all.Count);
        foreach (var minute in all.Where(WithinServerRanges))
        {
            if (minutes.Count == 0 || minute.Minute > minutes[^1].Minute) minutes.Add(minute);
        }
        double[] Tenths(Func<MinuteRow, double> column) => [.. minutes.Select(minute => Round(column(minute), 1))];
        double[] Thousandths(Func<MinuteRow, double> column) => [.. minutes.Select(minute => Round(column(minute), 3))];
        double?[] Optional(Func<MinuteRow, double?> column) =>
            [.. minutes.Select(minute => column(minute) is { } value ? Round(value, 3) : (double?)null)];
        return new MinutesDto(
            [.. minutes.Select(minute => minute.Minute)],
            Tenths(m => m.AvgW), Tenths(m => m.MaxW), Tenths(m => m.CpuW), Tenths(m => m.GpuW), Tenths(m => m.DisplayW),
            Tenths(m => m.RamW), Tenths(m => m.StorageW), Tenths(m => m.BoardW), Tenths(m => m.ExtrasW), Tenths(m => m.MonitorsW),
            Tenths(m => m.PsuLossW), Tenths(m => m.UnattributedW),
            Thousandths(m => m.CpuLoad), Optional(m => m.GpuLoad), Optional(m => m.Brightness),
            Tenths(m => m.DisplayOnS), Tenths(m => m.IdleS), Tenths(m => m.LockedS), Tenths(m => m.BatteryS),
            Tenths(m => m.MeasuredS), Tenths(m => m.CalibratedS), Tenths(m => m.EstimatedS),
            [.. minutes.Select(minute => minute.Samples)], [.. minutes.Select(minute => minute.TotalSource)],
            [.. minutes.Select(minute => minute.GpuScope)], [.. minutes.Select(minute => minute.MeasuredMask)]);
    }

    /// <summary>True when every figure of the minute is inside the ranges the server checks (server/src/minutes.ts).</summary>
    private static bool WithinServerRanges(MinuteRow m)
    {
        static bool In(double value, double low, double high) => double.IsFinite(value) && value >= low && value <= high;
        static bool Share(double? value) => value is null || In(value.Value, 0, 1);
        return m.Minute is >= 0 and <= 1499
            && new[] { m.AvgW, m.MaxW, m.CpuW, m.GpuW, m.DisplayW, m.RamW, m.StorageW, m.BoardW, m.ExtrasW, m.MonitorsW, m.PsuLossW }
                .All(watts => In(watts, 0, 5000))
            && In(m.UnattributedW, -5000, 5000)
            && In(m.CpuLoad, 0, 1) && Share(m.GpuLoad) && Share(m.Brightness)
            && new[] { m.DisplayOnS, m.IdleS, m.LockedS, m.BatteryS, m.MeasuredS, m.CalibratedS, m.EstimatedS }
                .All(seconds => In(seconds, 0, 120))
            && m.Samples is >= 0 and <= 100_000 && m.TotalSource is >= 0 and <= 4 && m.GpuScope is >= 0 and <= 2
            && m.MeasuredMask is >= 0 and <= 7;
    }

    /// <summary>The graphics card's maker, from the words in its name.</summary>
    private static string Vendor(string name) =>
        Word(name, "NVIDIA") ? "nvidia"
        : Word(name, "AMD") || Word(name, "Radeon") ? "amd"
        : Word(name, "Intel") || Word(name, "Arc") ? "intel"
        : Word(name, "Qualcomm") || Word(name, "Adreno") ? "qualcomm"
        : "other";

    private static bool Word(string text, string word) =>
        Regex.IsMatch(text, $@"(?<![A-Za-z0-9]){word}(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Tier(PsuTier tier) => tier switch
    {
        PsuTier.White => "white",
        PsuTier.Silver => "silver",
        PsuTier.Gold => "gold",
        PsuTier.Platinum => "platinum",
        PsuTier.Titanium => "titanium",
        _ => "bronze",
    };

    private static string Powers(UpsLoad load) => load switch
    {
        UpsLoad.ThisPc => "thisPc",
        UpsLoad.ThisPcAndMonitors => "thisPcAndMonitors",
        UpsLoad.More => "more",
        _ => "notSaid",
    };

    private static string? DeviceName(ServiceStatus? status, PowerDeviceKind kind) =>
        status?.PowerDevices?.FirstOrDefault(device => device.Kind == kind)?.Name is { Length: > 0 } name ? name : null;

    /// <summary>Text as it may be sent: scrubbed, trimmed and cut to <paramref name="max"/>; null when there is none.</summary>
    private static string? Text(string? text, int max, ScrubNames names) =>
        string.IsNullOrWhiteSpace(text) ? null : Cut(Scrubber.Scrub(text.Trim(), names), max);

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>A figure to a tenth, or null when unknown or outside what the server takes.</summary>
    private static double? Watts(double? value, double min, double max) =>
        value is { } v && double.IsFinite(v) && v >= min && v <= max ? Round(v, 1) : null;

    private static int? Within(int? value, int min, int max) => value is { } v && v >= min && v <= max ? v : null;

    private static int Count(int count) => Math.Clamp(count, 0, MaxCount);

    /// <summary>Rounded, and never <c>-0</c>.</summary>
    private static double Round(double value, int digits) => Math.Round(value, digits) + 0.0;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex SourceId();

    [GeneratedRegex("^[A-Z]{3}[0-9A-F]{4}$")]
    private static partial Regex MonitorId();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex Currency();
}
