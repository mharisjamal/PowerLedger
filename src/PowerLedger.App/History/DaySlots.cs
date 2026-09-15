using PowerLedger.Core;

namespace PowerLedger.App;

/// <summary>Five minutes of today's chart. Watts are averages over the time the machine was on in the slot; AsleepSeconds is
/// how much of the slot it slept while the service ran.</summary>
internal sealed record DaySlot(DateTimeOffset Start, double CpuW, double GpuW, double DisplayW, double RestW, double OnSeconds, double AsleepSeconds)
{
    /// <summary>The height to draw. A negative rest, which measured mode shows when the parts over-report, counts as zero
    /// on every chart (spec §9).</summary>
    public double TotalW => CpuW + GpuW + DisplayW + Math.Max(0, RestW);

    public bool HasReadings => OnSeconds > 0;
}

internal static class DaySlots
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>One slot per five minutes from <paramref name="dayStart"/> through the slot holding <paramref name="now"/>.</summary>
    public static IReadOnlyList<DaySlot> Build(IReadOnlyList<Aggregate> minutes, DateTimeOffset dayStart, DateTimeOffset now)
    {
        var count = Math.Max(1, (int)Math.Ceiling((now - dayStart) / Length));
        var cpu = new double[count];
        var gpu = new double[count];
        var display = new double[count];
        var rest = new double[count];
        var on = new double[count];
        var asleep = new double[count];

        foreach (var m in minutes)
        {
            var index = (int)Math.Floor((m.Start - dayStart) / Length);
            if (index >= 0 && index < count)
            {
                cpu[index] += m.CpuWh;
                gpu[index] += m.GpuWh;
                display[index] += m.DisplayWh;
                rest[index] += m.RestWh;
                on[index] += m.OnSeconds;
            }
            Lay(asleep, m.Start + Minute, m.GapSeconds, dayStart);
        }

        var slots = new DaySlot[count];
        for (var i = 0; i < count; i++)
        {
            slots[i] = new DaySlot(
                dayStart + i * Length, Watts(cpu[i], on[i]), Watts(gpu[i], on[i]), Watts(display[i], on[i]), Watts(rest[i], on[i]),
                on[i], Math.Min(asleep[i], Length.TotalSeconds));
        }
        return slots;
    }

    /// <summary>Spreads a sleep of <paramref name="seconds"/> that ended at <paramref name="end"/> backwards over the slots it covered.</summary>
    private static void Lay(double[] asleep, DateTimeOffset end, double seconds, DateTimeOffset dayStart)
    {
        var cursor = end;
        while (seconds > 0 && cursor > dayStart)
        {
            var index = (int)Math.Floor((cursor - dayStart - TimeSpan.FromTicks(1)) / Length);
            var slotStart = dayStart + index * Length;
            var taken = Math.Min(seconds, (cursor - slotStart).TotalSeconds);
            if (index < asleep.Length) asleep[index] += taken;
            seconds -= taken;
            cursor = slotStart;
        }
    }

    private static double Watts(double wh, double onSeconds) => onSeconds > 0 ? wh * 3600 / onSeconds : 0;
}
