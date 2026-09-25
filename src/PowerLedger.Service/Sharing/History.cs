using PowerLedger.Core;

namespace PowerLedger.Service.Sharing;

// The history upload (Plan Q §2): the hourly totals already on the PC when Hardware and power was turned on under consent
// version 2, sent once, oldest first, a chunk at a time to POST /v1/history. Each hour carries every numeric column of
// samples_1h (src/PowerLedger.Storage/Schema.cs), in camelCase, and nothing else.

/// <summary>One chunk of the history: at most <see cref="HistoryBuilder.ChunkDays"/> days of hours, oldest first.</summary>
/// <param name="Schema">Always <c>history-v1</c>.</param>
/// <param name="App">The service's version, <c>X.Y.Z</c>.</param>
/// <param name="Consent">The switches as they are now.</param>
/// <param name="UtcOffsetMinutes">The PC's offset from UTC when the chunk was built; each hour's <see cref="HourDto.T"/> is
/// UTC already.</param>
internal sealed record HistoryV1(
    string Schema,
    string InstallId,
    string App,
    ConsentDto Consent,
    int UtcOffsetMinutes,
    IReadOnlyList<HourDto> Hours);

/// <summary>One row of samples_1h.</summary>
/// <param name="T">The hour's start, UTC milliseconds (<c>start_ms</c>).</param>
internal sealed record HourDto(
    long T,
    double AvgW,
    double MaxW,
    double EnergyWh,
    double CpuWh,
    double GpuWh,
    double DisplayWh,
    double RestWh,
    double IdleOnWh,
    double IdleOffWh,
    double IdleOnS,
    double IdleOffS,
    double OnS,
    double BatteryS,
    double GapS,
    int SampleCount,
    double MeasuredS,
    double CalibratedS,
    double EstimatedS);

/// <summary>Builds the history's chunks (Plan Q §2). Pure. Figures go to three decimals, a thousandth of a watt, watt-hour or
/// second; an hour with a figure that isn't a number is left out, as JSON can't carry it.</summary>
internal static class HistoryBuilder
{
    public const string SchemaName = "history-v1";

    /// <summary>The most days one chunk covers, so a chunk stays well under the server's 1 MB.</summary>
    public const int ChunkDays = 31;

    /// <summary>Where the chunk that starts with the hour at <paramref name="first"/> ends, exclusive: 31 days on, or the
    /// history's end if that is sooner.</summary>
    public static DateTimeOffset ChunkEnd(DateTimeOffset first, DateTimeOffset until) =>
        first.AddDays(ChunkDays) < until ? first.AddDays(ChunkDays) : until;

    public static HistoryV1 Build(string installId, string app, Contracts.Consent consent, int utcOffsetMinutes, IEnumerable<Aggregate> hours) =>
        new(SchemaName, installId, app, new ConsentDto(consent.Version, consent.Diagnostics, consent.Usage, consent.Power, consent.Share),
            utcOffsetMinutes, [.. hours.Where(Finite).Select(Hour)]);

    private static HourDto Hour(Aggregate hour) => new(
        hour.Start.ToUnixTimeMilliseconds(), R(hour.AvgW), R(hour.MaxW),
        R(hour.EnergyWh), R(hour.CpuWh), R(hour.GpuWh), R(hour.DisplayWh), R(hour.RestWh),
        R(hour.IdleOnWh), R(hour.IdleOffWh), R(hour.IdleOnSeconds), R(hour.IdleOffSeconds),
        R(hour.OnSeconds), R(hour.BatterySeconds), R(hour.GapSeconds), hour.SampleCount,
        R(hour.MeasuredSeconds), R(hour.CalibratedSeconds), R(hour.EstimatedSeconds));

    private static bool Finite(Aggregate hour) =>
        new[]
        {
            hour.AvgW, hour.MaxW, hour.EnergyWh, hour.CpuWh, hour.GpuWh, hour.DisplayWh, hour.RestWh, hour.IdleOnWh, hour.IdleOffWh,
            hour.IdleOnSeconds, hour.IdleOffSeconds, hour.OnSeconds, hour.BatterySeconds, hour.GapSeconds, hour.MeasuredSeconds,
            hour.CalibratedSeconds, hour.EstimatedSeconds,
        }.All(double.IsFinite);

    private static double R(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}
