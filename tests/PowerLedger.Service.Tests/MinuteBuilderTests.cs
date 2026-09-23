using FsCheck.Xunit;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Service.Sharing;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>The minute rows data sharing sends, folded from the readings (data-sharing design §3, §4).</summary>
public class MinuteBuilderTests
{
    /// <summary>London, whose clocks change in March and October; Windows knows it by its own name.</summary>
    internal static readonly TimeZoneInfo London =
        TimeZoneInfo.TryFindSystemTimeZoneById("GMT Standard Time", out var windows) ? windows : TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    /// <summary>Santiago, whose clocks go forward at midnight, so a day can have no 00:00.</summary>
    private static readonly TimeZoneInfo Santiago =
        TimeZoneInfo.TryFindSystemTimeZoneById("Pacific SA Standard Time", out var windows) ? windows : TimeZoneInfo.FindSystemTimeZoneById("America/Santiago");

    /// <summary>12:00 in London, in summer time, on a minute boundary.</summary>
    private static readonly DateTimeOffset Noon = new(2026, 9, 12, 11, 0, 0, TimeSpan.Zero);

    [Property(MaxTest = 300)]
    public bool Every_watt_second_of_the_readings_that_count_is_in_the_minutes(int[] draws, int startSecond)
    {
        // Anywhere in the two days around noon, so midnight falls inside some runs; deltas include gaps, and some rows are suspect.
        var at = Noon.AddSeconds(Math.Abs(startSecond % 172_800) - 86_400);
        var rows = new List<Reading>();
        foreach (var draw in draws ?? [])
        {
            var delta = Math.Abs(draw % 8) switch { 0 => 6.5, 1 => 0.4, var n => n * 0.5 };
            at = at.AddSeconds(delta);
            rows.Add(Row(at, watts: Math.Abs(draw % 4000) / 10.0, delta, (Quality)(Math.Abs(draw / 8) % 3), suspect: draw % 13 == 0) with
            {
                UserIdle = (draw & 16) != 0,
                GpuLoad = (draw & 32) != 0 ? null : 0.3,
            });
        }

        var minutes = MinuteBuilder.Build(rows, London);

        var counted = rows.Where(r => !r.Suspect && r.DeltaSeconds <= EnergyIntegrator.MaxDeltaSeconds).ToList();
        var energy = counted.Sum(r => r.TotalW * r.DeltaSeconds);
        var cpuEnergy = counted.Sum(r => r.Components.Cpu * r.DeltaSeconds);
        var fromMinutes = minutes.Sum(m => m.AvgW * (m.MeasuredS + m.CalibratedS + m.EstimatedS));
        var cpuFromMinutes = minutes.Sum(m => m.CpuW * (m.MeasuredS + m.CalibratedS + m.EstimatedS));
        return Math.Abs(fromMinutes - energy) < 1e-9
            && Math.Abs(cpuFromMinutes - cpuEnergy) < 1e-9
            && minutes.Sum(m => m.Samples) == counted.Count
            && minutes.All(m => m.Minute is >= 0 and < 1500)
            && minutes.Select(m => m.StartMs).Distinct().Count() == minutes.Count;
    }

    [Fact]
    public void On_the_25_hour_day_a_minute_after_the_repeated_hour_counts_from_the_days_real_start()
    {
        // 25 October 2026: London's clocks go back from 02:00 BST to 01:00 GMT, so the day began at 23:00 UTC the day before.
        var minutes = MinuteBuilder.Build(
        [
            Row(new DateTimeOffset(2026, 10, 24, 23, 0, 30, TimeSpan.Zero)),     // 00:00:30 BST, the day's first minute
            Row(new DateTimeOffset(2026, 10, 25, 0, 30, 30, TimeSpan.Zero)),     // 01:30:30 BST, the first time round
            Row(new DateTimeOffset(2026, 10, 25, 1, 30, 30, TimeSpan.Zero)),     // 01:30:30 GMT, the second
            Row(new DateTimeOffset(2026, 10, 25, 2, 0, 30, TimeSpan.Zero)),      // 02:00:30 GMT, after the repeated hour
            Row(new DateTimeOffset(2026, 10, 25, 23, 59, 30, TimeSpan.Zero)),    // 23:59:30 GMT, the day's last minute
        ], London);

        minutes.Select(m => (m.Day, m.Minute)).ShouldBe(new[]
        {
            ("2026-10-25", 0), ("2026-10-25", 90), ("2026-10-25", 150), ("2026-10-25", 180), ("2026-10-25", 1499),
        });
        minutes.ShouldAllBe(m => Decoded(m, London) == m.StartMs);
    }

    [Fact]
    public void On_the_23_hour_day_a_minute_after_the_skipped_hour_counts_from_midnight()
    {
        // 29 March 2026: London's clocks go forward from 01:00 GMT to 02:00 BST.
        var minutes = MinuteBuilder.Build(
        [
            Row(new DateTimeOffset(2026, 3, 29, 0, 59, 30, TimeSpan.Zero)),      // 00:59:30 GMT
            Row(new DateTimeOffset(2026, 3, 29, 1, 0, 30, TimeSpan.Zero)),       // 02:00:30 BST
            Row(new DateTimeOffset(2026, 3, 29, 22, 59, 30, TimeSpan.Zero)),     // 23:59:30 BST, the day's last minute
            Row(new DateTimeOffset(2026, 3, 29, 23, 0, 30, TimeSpan.Zero)),      // 00:00:30 BST on the 30th
        ], London);

        minutes.Select(m => (m.Day, m.Minute)).ShouldBe(new[]
        {
            ("2026-03-29", 59), ("2026-03-29", 60), ("2026-03-29", 1379), ("2026-03-30", 0),
        });
        minutes.ShouldAllBe(m => Decoded(m, London) == m.StartMs);
    }

    [Fact]
    public void On_a_day_whose_midnight_a_clock_change_skips_a_minute_counts_from_midnight_at_the_days_offset()
    {
        // 6 September 2026: Santiago's clocks go from 00:00 at UTC-4 straight to 01:00 at UTC-3, so the day starts at
        // 01:00 and its offset is UTC-3's. Counted from its midnight at that offset, its first minute is 60, not 0.
        var day = new DateOnly(2026, 9, 6);
        Santiago.IsInvalidTime(day.ToDateTime(TimeOnly.MinValue)).ShouldBeTrue();

        var minutes = MinuteBuilder.Build(
        [
            Row(new DateTimeOffset(2026, 9, 6, 3, 59, 30, TimeSpan.Zero)),       // 23:59:30 on the 5th, UTC-4
            Row(new DateTimeOffset(2026, 9, 6, 4, 0, 30, TimeSpan.Zero)),        // 01:00:30 at UTC-3, the day's first minute
            Row(new DateTimeOffset(2026, 9, 7, 2, 59, 30, TimeSpan.Zero)),       // 23:59:30 at UTC-3, its last
        ], Santiago);

        minutes.Select(m => (m.Day, m.Minute)).ShouldBe(new[] { ("2026-09-05", 1439), ("2026-09-06", 60), ("2026-09-06", 1439) });
        LocalDays.UtcOffsetMinutes(day, Santiago).ShouldBe(-180);
        minutes.ShouldAllBe(m => Decoded(m, Santiago) == m.StartMs);
    }

    [Fact]
    public void Watts_and_loads_are_averaged_over_the_seconds_and_a_load_no_reading_had_is_null()
    {
        var minute = MinuteBuilder.Build(
        [
            Row(Noon.AddSeconds(10), watts: 40, delta: 1) with { GpuLoad = null, Brightness = null },
            Row(Noon.AddSeconds(13), watts: 80, delta: 3) with { GpuLoad = 0.2, Brightness = null, CpuLoad = 0.5 },
            Row(Noon.AddSeconds(14), watts: 20, delta: 1) with { GpuLoad = 0.7, Brightness = null },
        ], London).ShouldHaveSingleItem();

        (minute.StartMs, minute.Day, minute.Minute).ShouldBe((Noon.ToUnixTimeMilliseconds(), "2026-09-12", 720));
        minute.AvgW.ShouldBe(60, 1e-12);                                       // (40 + 240 + 20) / 5
        minute.MaxW.ShouldBe(80);
        minute.CpuW.ShouldBe(0.3 * 60, 1e-12);
        minute.UnattributedW.ShouldBe(0.35 * 60 - 20, 1e-12);
        minute.CpuLoad.ShouldBe((0.25 + 1.5 + 0.25) / 5, 1e-12);
        minute.GpuLoad.ShouldNotBeNull().ShouldBe((0.6 + 0.7) / 4, 1e-12);   // over the four seconds that had one
        minute.Brightness.ShouldBeNull();
        minute.Samples.ShouldBe(3);
    }

    [Fact]
    public void Each_states_seconds_and_each_qualitys_are_counted()
    {
        var minute = MinuteBuilder.Build(
        [
            Row(Noon.AddSeconds(2), delta: 2, quality: Quality.Measured) with { DisplayOn = true, UserIdle = true, OnBattery = true },
            Row(Noon.AddSeconds(3), delta: 1, quality: Quality.Calibrated) with { DisplayOn = false, UserIdle = true, SessionLocked = true },
            Row(Noon.AddSeconds(3.5), delta: 0.5, quality: Quality.Estimated) with { DisplayOn = true },
        ], London).ShouldHaveSingleItem();

        (minute.DisplayOnS, minute.IdleS, minute.LockedS, minute.BatteryS).ShouldBe((2.5, 3.0, 1.0, 2.0));
        (minute.MeasuredS, minute.CalibratedS, minute.EstimatedS).ShouldBe((2.0, 1.0, 0.5));
    }

    [Fact]
    public void Where_the_total_came_from_and_what_the_card_covered_go_to_the_most_seconds_and_the_lowest_on_a_tie()
    {
        static Reading At(int second, TotalSource source, GpuPowerScope scope, double delta = 1) =>
            Row(Noon.AddSeconds(second), delta: delta) with { TotalSource = source, GpuScope = scope };

        var tie = MinuteBuilder.Build([At(1, TotalSource.Ups, GpuPowerScope.Package), At(2, TotalSource.Battery, GpuPowerScope.ChipOnly)], London)
            .ShouldHaveSingleItem();
        (tie.TotalSource, tie.GpuScope).ShouldBe(((int)TotalSource.Battery, (int)GpuPowerScope.ChipOnly));

        var most = MinuteBuilder.Build([At(2, TotalSource.Ups, GpuPowerScope.Package, delta: 2), At(3, TotalSource.Battery, GpuPowerScope.ChipOnly)], London)
            .ShouldHaveSingleItem();
        (most.TotalSource, most.GpuScope).ShouldBe(((int)TotalSource.Ups, (int)GpuPowerScope.Package));
    }

    [Fact]
    public void A_part_counts_as_measured_when_it_was_for_at_least_half_the_minutes_seconds()
    {
        static Reading At(int second, MeasuredParts measured, double delta) => Row(Noon.AddSeconds(second), delta: delta) with { Measured = measured };

        // Of four seconds, the processor was measured for three, the card for exactly two and the total for one.
        var minute = MinuteBuilder.Build(
        [
            At(2, MeasuredParts.Cpu | MeasuredParts.Gpu, 2),
            At(3, MeasuredParts.Cpu | MeasuredParts.Total, 1),
            At(4, MeasuredParts.None, 1),
        ], London).ShouldHaveSingleItem();

        minute.MeasuredMask.ShouldBe((int)(MeasuredParts.Cpu | MeasuredParts.Gpu));
    }

    [Fact]
    public void A_suspect_reading_is_left_out_and_a_minute_of_nothing_else_is_not_there()
    {
        var minutes = MinuteBuilder.Build(
        [
            Row(Noon.AddSeconds(1), watts: 30),
            Row(Noon.AddSeconds(2), watts: 3000, suspect: true),
            Row(Noon.AddSeconds(61), watts: 3000, suspect: true),
        ], London);

        var minute = minutes.ShouldHaveSingleItem();
        (minute.AvgW, minute.MaxW, minute.Samples, minute.EstimatedS).ShouldBe((30.0, 30.0, 1, 1.0));
    }

    [Fact]
    public void A_gap_is_left_out_as_the_ledger_counts_no_energy_for_it()
    {
        var minute = MinuteBuilder.Build([Row(Noon.AddSeconds(1), watts: 30), Row(Noon.AddSeconds(40), watts: 90, delta: 39)], London)
            .ShouldHaveSingleItem();
        (minute.AvgW, minute.EstimatedS, minute.Samples).ShouldBe((30.0, 1.0, 1));

        // A slower sample interval raises the threshold, as it does the ledger's.
        MinuteBuilder.Build([Row(Noon.AddSeconds(8), watts: 30, delta: 8)], London, maxDeltaSeconds: 10)
            .ShouldHaveSingleItem().EstimatedS.ShouldBe(8);
    }

    [Fact]
    public void A_minute_a_clock_set_back_filled_beyond_what_the_server_takes_is_left_out()
    {
        // Two passes over the same minute of wall-clock time hold 121.2 seconds of readings; the server takes at most 120.
        var rows = Enumerable.Range(0, 120).Select(i => Row(Noon.AddMilliseconds(i * 500), delta: 1.01)).ToList();
        MinuteBuilder.Build(rows, London).ShouldBeEmpty();
    }

    /// <summary>The minute's UTC start as a reader of the report works it out: its day's midnight, less the day's offset in
    /// the header, plus its index.</summary>
    private static long Decoded(MinuteRow minute, TimeZoneInfo zone)
    {
        var day = LocalDays.Parse(minute.Day);
        var midnight = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return (midnight - TimeSpan.FromMinutes(LocalDays.UtcOffsetMinutes(day, zone)) + TimeSpan.FromMinutes(minute.Minute)).ToUnixTimeMilliseconds();
    }

    /// <summary>An estimated reading ending at <paramref name="at"/>, its watts split over the parts with the rest unattributed.</summary>
    private static Reading Row(DateTimeOffset at, double watts = 50, double delta = 1, Quality quality = Quality.Estimated, bool suspect = false) => new(
        at, delta, watts, quality,
        new Components(Cpu: watts * 0.3, Gpu: watts * 0.2, Display: watts * 0.1, Ram: 4, Storage: 3, Board: 10, Extras: 1, Monitors: 2,
            PsuLoss: watts * 0.05, Unattributed: watts * 0.35 - 20),
        OnBattery: false, DisplayOn: false, UserIdle: false, SessionLocked: false, CpuLoad: 0.25, GpuLoad: 0.5, Brightness: 0.6, Suspect: suspect);
}
