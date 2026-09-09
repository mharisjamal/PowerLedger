using FsCheck.Xunit;
using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.Core.Tests;

public class EnergyProperties
{
    private static Reading Make(double totalW, double delta, bool idle, int second = 0)
    {
        var display = Math.Min(6, totalW * 0.1);
        var parts = new Components(Cpu: totalW * 0.5, Gpu: totalW * 0.2, Display: display, 0, 0, 0, 0, 0, 0, Unattributed: totalW * 0.3 - display);
        return new Reading(TestData.T0.AddSeconds(second), delta, totalW, Quality.Estimated, parts,
            OnBattery: false, DisplayOn: true, UserIdle: idle, SessionLocked: false, 0.5, null, 0.5, false);
    }

    [Property(MaxTest = 300)]
    public bool Energy_is_non_negative_bounded_by_five_seconds_and_gaps_match_the_rule(int w, int d)
    {
        var watts = Math.Abs(w % 1000);
        var delta = Math.Abs(d % 100) / 10.0;                       // 0.0 .. 9.9 s
        var e = EnergyIntegrator.Integrate(Make(watts, delta, idle: false));
        var bounded = e.Wh >= 0 && e.Wh <= watts * 5 / 3600.0 + 1e-9;
        var gapRule = e.Gap == (delta > 5);
        var seconds = e.Gap ? e.OnSeconds == 0 && e.GapSeconds == delta : e.GapSeconds == 0;
        return bounded && gapRule && seconds;
    }

    [Property(MaxTest = 200)]
    public bool Minute_energy_equals_the_sum_of_ticks_and_components_add_up(int[] ws)
    {
        if (ws.Length == 0) return true;
        var readings = ws.Take(60)
            .Select((w, i) => Make(Math.Abs(w % 500), delta: i % 7 == 0 ? 7.0 : 1.0, idle: i % 3 == 0, second: i))
            .ToList();
        var m = Downsampler.ToMinute(TestData.T0, readings);
        var expected = readings.Sum(r => EnergyIntegrator.Integrate(r).Wh);
        var conserved = Math.Abs(m.EnergyWh - expected) < 1e-9;
        var partsAddUp = Math.Abs(m.CpuWh + m.GpuWh + m.DisplayWh + m.RestWh - m.EnergyWh) < 1e-9;
        var idleWithin = m.IdleOnWh + m.IdleOffWh <= m.EnergyWh + 1e-9;
        var qualityPartition = Math.Abs(m.MeasuredSeconds + m.CalibratedSeconds + m.EstimatedSeconds - m.OnSeconds) < 1e-9;
        var gapsSummed = Math.Abs(m.GapSeconds - readings.Sum(r => EnergyIntegrator.Integrate(r).GapSeconds)) < 1e-9;
        return conserved && partsAddUp && idleWithin && qualityPartition && gapsSummed;
    }
}
