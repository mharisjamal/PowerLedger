using System.Globalization;

namespace PowerLedger.Service.Sharing;

/// <summary>The PC's local days, which data sharing sends one at a time (data-sharing design §3, §4).</summary>
internal static class LocalDays
{
    /// <summary>The local day <paramref name="at"/> falls on.</summary>
    public static DateOnly Of(DateTimeOffset at, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    /// <summary>
    /// The instant <paramref name="day"/> starts: local midnight, or the first local time after it when a clock change skips
    /// midnight, and the earlier of the two when a clock change repeats it.
    /// </summary>
    public static DateTimeOffset Start(DateOnly day, TimeZoneInfo zone) => At(day, TimeSpan.Zero, zone);

    /// <summary>The instant the local clock on <paramref name="day"/> reads <paramref name="time"/>, as <see cref="Start"/>
    /// settles a skipped or repeated time.</summary>
    public static DateTimeOffset At(DateOnly day, TimeSpan time, TimeZoneInfo zone)
    {
        var wall = day.ToDateTime(TimeOnly.MinValue) + time;
        for (var steps = 0; zone.IsInvalidTime(wall) && steps < 24 * 60; steps++) wall = wall.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset);
    }

    /// <summary>The day's UTC offset in minutes at its start, as the report's header gives it. With it a minute's index gives
    /// back the minute's UTC time: the date's midnight, less the offset, plus the index (see <see cref="Origin"/>).</summary>
    public static int UtcOffsetMinutes(DateOnly day, TimeZoneInfo zone) => (int)zone.GetUtcOffset(Start(day, zone)).TotalMinutes;

    /// <summary>
    /// The instant a minute's index counts from: the day's midnight at <see cref="UtcOffsetMinutes"/>. That is the day's
    /// <see cref="Start"/>, except on a day whose midnight a clock change skips, which starts later at the new offset, so
    /// its first indexes go unused and each minute still decodes to its own time.
    /// </summary>
    public static DateTimeOffset Origin(DateOnly day, TimeZoneInfo zone) =>
        new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromMinutes(UtcOffsetMinutes(day, zone)));

    /// <summary><c>yyyy-MM-dd</c>, as the outbox and the server keep a day.</summary>
    public static string Text(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly Parse(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
