using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

public class CsvExportTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Raw_readings_are_one_line_each_with_every_part()
    {
        var reading = new Reading(T0, 1, 34.25, Quality.Measured,
            new Components(Cpu: 14.5, Gpu: 4, Display: 3, Ram: 0, Storage: 0, Board: 0, Extras: 0, Monitors: 1, PsuLoss: 0, Unattributed: 11.75),
            OnBattery: true, DisplayOn: true, UserIdle: false, SessionLocked: false, CpuLoad: 0.3, GpuLoad: null, Brightness: 0.6, Suspect: false);

        var lines = CsvExport.Raw([reading]);

        lines[0].ShouldBe("timestamp_utc,delta_s,total_w,quality,cpu_w,gpu_w,display_w,monitors_w,ram_w,storage_w,board_w,extras_w,psu_loss_w,unattributed_w,on_battery,display_on,user_idle,locked,cpu_load,gpu_load,brightness,suspect");
        lines[1].ShouldBe("2026-09-08T12:00:00Z,1,34.25,Measured,14.5,4,3,1,0,0,0,0,0,11.75,true,true,false,false,0.3,,0.6,false");
    }

    [Fact]
    public void Minute_and_hour_rows_are_one_line_each()
    {
        var row = Aggregate.Empty(T0) with { AvgW = 30, MaxW = 41.5, EnergyWh = 0.5, CpuWh = 0.2, RestWh = 0.3, OnSeconds = 60, SampleCount = 60, MeasuredSeconds = 60 };

        var lines = CsvExport.Rows([row]);

        lines[0].ShouldBe("start_utc,avg_w,max_w,energy_wh,cpu_wh,gpu_wh,display_wh,rest_wh,idle_on_wh,idle_off_wh,idle_on_s,idle_off_s,on_s,battery_s,gap_s,samples,measured_s,calibrated_s,estimated_s");
        lines[1].ShouldBe("2026-09-08T12:00:00Z,30,41.5,0.5,0.2,0,0,0.3,0,0,0,0,60,0,0,60,60,0,0");
    }
}
