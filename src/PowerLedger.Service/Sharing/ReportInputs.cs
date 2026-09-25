using System.Globalization;
using System.Runtime.InteropServices;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;
using PowerLedger.Storage;

namespace PowerLedger.Service.Sharing;

/// <summary>Everything one upload is built from (data-sharing design §3). <see cref="ReportBuilder"/> copies named fields out
/// of these; it never sends any of them whole.</summary>
/// <param name="Host">What the PC and the service say of themselves.</param>
/// <param name="Consent">What the user agreed to now: a section goes only when its switch is on.</param>
/// <param name="Day">The local day described, <c>yyyy-MM-dd</c>.</param>
/// <param name="UtcOffsetMinutes">The day's UTC offset at its start.</param>
/// <param name="Minutes">The day's minutes, oldest first.</param>
/// <param name="Events">The day's source failures, crashes and usage counts.</param>
/// <param name="Status">The service's latest status: the sources, monitors and USB power devices.</param>
/// <param name="Facts">The detected hardware.</param>
/// <param name="Tariff">The tariff in force now.</param>
/// <param name="DiscreteGpu">Whether the latest reading found a discrete graphics card.</param>
/// <param name="WithHardware">Whether the parts go with the minutes.</param>
/// <param name="Names">The names scrubbed out of every text sent.</param>
/// <param name="Complete">True for a day that is over, false for today so far (Plan Q §1).</param>
internal sealed record ReportInputs(
    HostFacts Host,
    string InstallId,
    Consent Consent,
    string Day,
    int UtcOffsetMinutes,
    IReadOnlyList<MinuteRow> Minutes,
    DayEvents Events,
    ServiceStatus? Status,
    InventoryFacts? Facts,
    ServiceSettings Settings,
    Tariff? Tariff,
    bool DiscreteGpu,
    bool WithHardware,
    ScrubNames Names,
    bool Complete = true);

/// <summary>What the PC running the service says of itself.</summary>
/// <param name="App">The service's version, <c>X.Y.Z</c>.</param>
/// <param name="Windows">Windows' version, <c>major.minor.build</c>.</param>
/// <param name="Arch"><c>x64</c>, <c>arm64</c> or <c>x86</c>: Windows' own architecture.</param>
/// <param name="Threads">Logical processors.</param>
/// <param name="MemoryGb">Memory, in GiB to a tenth.</param>
internal sealed record HostFacts(string App, string Windows, string Arch, int Threads, double? MemoryGb)
{
    public static HostFacts Here()
    {
        var windows = Environment.OSVersion.Version;
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new HostFacts(
            ServiceVersion.Short,
            string.Create(CultureInfo.InvariantCulture, $"{windows.Major}.{windows.Minor}.{windows.Build}"),
            ArchName(RuntimeInformation.OSArchitecture),
            Environment.ProcessorCount,
            memory > 0 ? Math.Round(memory / (double)(1L << 30), 1) : null);
    }

    /// <summary>The report schema's name for a Windows architecture; PowerLedger is built for these three only.</summary>
    internal static string ArchName(Architecture architecture) => architecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };
}

/// <summary>What the outbox holds for one day besides its minutes.</summary>
/// <param name="Sources">Each source's failures that day, and its last error, scrubbed.</param>
/// <param name="Crashes">Crashes recorded that day, scrubbed, oldest first.</param>
/// <param name="Usage">The App's counts for the day, merged; null when it sent none.</param>
internal sealed record DayEvents(IReadOnlyDictionary<string, SourceDay> Sources, IReadOnlyList<CrashReport> Crashes, UsageCounts? Usage)
{
    public static DayEvents None { get; } = new(new Dictionary<string, SourceDay>(), [], null);
}

/// <summary>One source's day: how many times it failed, and the last thing it said when it did.</summary>
internal sealed record SourceDay(int Failures, string? LastError);
