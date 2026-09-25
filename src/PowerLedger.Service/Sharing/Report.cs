using System.Text.Json.Serialization;

namespace PowerLedger.Service.Sharing;

// The upload as it goes on the wire: version 1 of the report, server/schema/report-v1.schema.json (data-sharing design §3).
// Every field name, nesting and null is as in server/test/fixtures/valid-full.json. ReportBuilder fills these from named
// fields only, so nothing added to a service type later can reach the server by accident.

/// <summary>One PC's day: the header, then a section for each switch that is on and has something to say.
/// <see cref="Complete"/> (Plan Q §1) is false for today so far, sent every hour, and true for the day once it is over;
/// the service always writes it, and a report without it, from an older app, is a complete day.</summary>
internal sealed record ReportV1(
    int Schema,
    string InstallId,
    string App,
    string Windows,
    string Arch,
    string Chassis,
    ConsentDto Consent,
    string Day,
    int UtcOffsetMinutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Complete,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DiagnosticsDto? Diagnostics,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UsageDto? Usage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PowerDto? Power);

internal sealed record ConsentDto(int Version, bool Diagnostics, bool Usage, bool Power, bool Share);

internal sealed record DiagnosticsDto(IReadOnlyList<SourceDto> Sources, IReadOnlyList<CrashDto> Crashes);

/// <param name="Id">The service's own name for the source, such as <c>energy-meter</c>.</param>
/// <param name="State"><c>working</c>, <c>notOnMachine</c>, <c>failing</c> or <c>note</c>.</param>
/// <param name="Failures">How many times it failed that day.</param>
/// <param name="Device">The graphics card, UPS or power supply it reads.</param>
internal sealed record SourceDto(string Id, string State, int Failures, string? LastError, string? Device);

/// <param name="At">UTC, <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>.</param>
internal sealed record CrashDto(string At, string Component, string Version, IReadOnlyList<string> Types, string Message, string Stack);

internal sealed record UsageDto(
    int AppOpens,
    IReadOnlyDictionary<string, int> Pages,
    IReadOnlyDictionary<string, int> Settings,
    int ReportsExported,
    int UpdatesInstalled,
    int DaysSinceFirstRun,
    string Theme,
    string Language);

/// <param name="Hardware">Null unless the parts changed since they were last sent.</param>
internal sealed record PowerDto(HardwareDto? Hardware, MinutesDto Minutes);

internal sealed record HardwareDto(
    CpuDto Cpu,
    IReadOnlyList<GpuDto> Gpus,
    double? MemoryGb,
    IReadOnlyList<MonitorDto> Monitors,
    PsuDto Psu,
    UpsDto? Ups,
    ProfileDto Profile,
    double SampleIntervalS,
    TariffDto? Tariff);

/// <param name="TdpTyped">True when <paramref name="TdpW"/> is the figure the user typed, false when it was detected.</param>
internal sealed record CpuDto(string? Model, int? Cores, int? Threads, double? TdpW, bool TdpTyped);

/// <param name="Vendor"><c>nvidia</c>, <c>amd</c>, <c>intel</c>, <c>qualcomm</c> or <c>other</c>.</param>
internal sealed record GpuDto(string Vendor, string? Model, int? MemoryMb, bool Discrete, double? TdpW, bool TdpTyped);

/// <param name="Maker">The three-letter maker code from the monitor's own ID, such as <c>GSM</c>.</param>
/// <param name="Product">Its four-digit product code, such as <c>5B7F</c>.</param>
/// <param name="FigureFrom"><c>energyStar</c>, <c>estimated</c>, <c>typed</c> or <c>unknown</c>.</param>
internal sealed record MonitorDto(
    string? Maker,
    string? Product,
    string? Model,
    double? SizeIn,
    int? Width,
    int? Height,
    double? RefreshHz,
    bool? Hdr,
    bool OwnPlug,
    bool Counted,
    double? FigureW,
    string FigureFrom);

/// <param name="Tier">The 80 PLUS tier in the profile, in lower case.</param>
internal sealed record PsuDto(string Tier, int? RatedW, string? Name);

/// <param name="Powers"><c>notSaid</c>, <c>thisPc</c>, <c>thisPcAndMonitors</c> or <c>more</c>.</param>
internal sealed record UpsDto(string? Model, string Powers);

/// <param name="PanelIn">The built-in panel's diagonal, or null when there is none or it is unknown.</param>
internal sealed record ProfileDto(int RamSticks, bool RamDdr5, int SsdCount, int HddCount, int FanCount, double ExtrasW, double? PanelIn);

internal sealed record TariffDto(decimal PricePerKwh, string Currency);

/// <summary>The day's minutes, one array per column, all the same length; <see cref="T"/> is each minute's index from the
/// local day's start.</summary>
internal sealed record MinutesDto(
    int[] T,
    double[] AvgW,
    double[] MaxW,
    double[] CpuW,
    double[] GpuW,
    double[] DisplayW,
    double[] RamW,
    double[] StorageW,
    double[] BoardW,
    double[] ExtrasW,
    double[] MonitorsW,
    double[] PsuLossW,
    double[] UnattributedW,
    double[] CpuLoad,
    double?[] GpuLoad,
    double?[] Brightness,
    double[] DisplayOnS,
    double[] IdleS,
    double[] LockedS,
    double[] BatteryS,
    double[] MeasuredS,
    double[] CalibratedS,
    double[] EstimatedS,
    int[] Samples,
    int[] TotalSource,
    int[] GpuScope,
    int[] MeasuredMask);
