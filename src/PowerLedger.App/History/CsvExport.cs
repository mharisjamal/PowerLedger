using System.Globalization;
using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>History as CSV lines (spec §9 exports): invariant culture, UTC timestamps in ISO 8601, one column per stored field.</summary>
internal static class CsvExport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static IReadOnlyList<string> Raw(IReadOnlyList<Reading> readings)
    {
        var lines = new List<string>(readings.Count + 1)
        {
            "timestamp_utc,delta_s,total_w,quality,cpu_w,gpu_w,display_w,monitors_w,ram_w,storage_w,board_w,extras_w,psu_loss_w,unattributed_w," +
            "on_battery,display_on,user_idle,locked,cpu_load,gpu_load,brightness,suspect",
        };
        foreach (var r in readings)
        {
            var p = r.Components;
            lines.Add(string.Join(',',
                Time(r.Timestamp), N(r.DeltaSeconds), N(r.TotalW), r.Quality.ToString(),
                N(p.Cpu), N(p.Gpu), N(p.Display), N(p.Monitors), N(p.Ram), N(p.Storage), N(p.Board), N(p.Extras), N(p.PsuLoss), N(p.Unattributed),
                B(r.OnBattery), B(r.DisplayOn), B(r.UserIdle), B(r.SessionLocked), N(r.CpuLoad), N(r.GpuLoad), N(r.Brightness), B(r.Suspect)));
        }
        return lines;
    }

    public static IReadOnlyList<string> Rows(IReadOnlyList<Aggregate> rows)
    {
        var lines = new List<string>(rows.Count + 1)
        {
            "start_utc,avg_w,max_w,energy_wh,cpu_wh,gpu_wh,display_wh,rest_wh,idle_on_wh,idle_off_wh,idle_on_s,idle_off_s," +
            "on_s,battery_s,gap_s,samples,measured_s,calibrated_s,estimated_s",
        };
        foreach (var a in rows)
        {
            lines.Add(string.Join(',',
                Time(a.Start), N(a.AvgW), N(a.MaxW), N(a.EnergyWh), N(a.CpuWh), N(a.GpuWh), N(a.DisplayWh), N(a.RestWh),
                N(a.IdleOnWh), N(a.IdleOffWh), N(a.IdleOnSeconds), N(a.IdleOffSeconds),
                N(a.OnSeconds), N(a.BatterySeconds), N(a.GapSeconds), a.SampleCount.ToString(Invariant),
                N(a.MeasuredSeconds), N(a.CalibratedSeconds), N(a.EstimatedSeconds)));
        }
        return lines;
    }

    private static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Invariant);

    private static string N(double value) => double.IsFinite(value) ? value.ToString("0.######", Invariant) : "";

    private static string N(double? value) => value is { } v ? N(v) : "";

    private static string B(bool value) => value ? "true" : "false";
}
