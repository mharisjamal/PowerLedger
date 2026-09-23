using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;

namespace PowerLedger.Service.Sharing;

/// <summary>
/// Readings folded into the minute rows data sharing sends (data-sharing design §3, §4). Pure: the same readings and zone
/// always give the same rows.
/// <list type="bullet">
/// <item>A reading counts in the minute its timestamp falls in. Suspect readings are left out, and so are gaps, readings
/// whose Δt is past the gap threshold, which the ledger counts as no energy at all.</item>
/// <item>Watts and the processor's load are averaged over the seconds; the graphics load and the brightness over the
/// seconds of the readings that had one, and are null when none did.</item>
/// <item>Where the total came from and what the card's figure covered go to the value holding the most seconds, the lowest
/// on a tie; a part counts as measured when it was for at least half the seconds.</item>
/// </list>
/// So the minutes' watts times their seconds add up to the readings' energy, as the ledger integrates it.
/// </summary>
internal static class MinuteBuilder
{
    /// <summary>The most seconds a minute may hold: a reading's Δt reaches back into the minute before, and the server
    /// refuses more. Only a clock set back gives more, and such a minute is left out.</summary>
    public const double MaxSeconds = 120;

    private const long MinuteMs = 60_000;

    /// <param name="maxDeltaSeconds">The gap threshold for the sample interval the readings were taken at
    /// (<see cref="EnergyIntegrator.GapThresholdFor"/>).</param>
    public static IReadOnlyList<MinuteRow> Build(IReadOnlyList<Reading> rows, TimeZoneInfo zone, double maxDeltaSeconds = EnergyIntegrator.MaxDeltaSeconds)
    {
        var minutes = new List<MinuteRow>();
        var groups = rows
            .Where(row => Counts(row, maxDeltaSeconds))
            .GroupBy(row => Math.DivRem(row.Timestamp.ToUnixTimeMilliseconds(), MinuteMs).Quotient)
            .OrderBy(group => group.Key);
        foreach (var group in groups)
        {
            if (Fold(group.Key * MinuteMs, group, zone) is { } minute) minutes.Add(minute);
        }
        return minutes;
    }

    private static bool Counts(Reading row, double maxDeltaSeconds) =>
        !row.Suspect && double.IsFinite(row.DeltaSeconds) && row.DeltaSeconds > 0 && row.DeltaSeconds <= maxDeltaSeconds
        && double.IsFinite(row.TotalW);

    private static MinuteRow? Fold(long startMs, IEnumerable<Reading> readings, TimeZoneInfo zone)
    {
        var sums = new Sums();
        foreach (var reading in readings) sums.Add(reading);
        if (sums.Seconds > MaxSeconds) return null;

        var start = DateTimeOffset.FromUnixTimeMilliseconds(startMs);
        var day = LocalDays.Of(start, zone);
        var index = (int)((start - LocalDays.Start(day, zone)).Ticks / TimeSpan.TicksPerMinute);
        var s = sums.Seconds;
        return new MinuteRow(
            startMs, LocalDays.Text(day), index,
            sums.Total / s, sums.Max,
            sums.Cpu / s, sums.Gpu / s, sums.Display / s, sums.Ram / s, sums.Storage / s,
            sums.Board / s, sums.Extras / s, sums.Monitors / s, sums.PsuLoss / s, sums.Unattributed / s,
            sums.CpuLoad / s,
            sums.GpuLoadSeconds > 0 ? sums.GpuLoad / sums.GpuLoadSeconds : null,
            sums.BrightnessSeconds > 0 ? sums.Brightness / sums.BrightnessSeconds : null,
            sums.DisplayOn, sums.Idle, sums.Locked, sums.Battery,
            sums.Measured, sums.Calibrated, sums.Estimated,
            sums.Count, Majority(sums.Sources), Majority(sums.Scopes), sums.Mask());
    }

    /// <summary>The value holding the most seconds, the lowest on a tie.</summary>
    private static int Majority(SortedDictionary<int, double> seconds)
    {
        var best = 0;
        var most = double.NegativeInfinity;
        foreach (var (value, held) in seconds)
        {
            if (held > most)
            {
                best = value;
                most = held;
            }
        }
        return best;
    }

    /// <summary>Running sums over one minute's readings, each weighted by its Δt.</summary>
    private sealed class Sums
    {
        private static readonly MeasuredParts[] Bits = [MeasuredParts.Cpu, MeasuredParts.Gpu, MeasuredParts.Total];
        private readonly double[] _bitSeconds = new double[Bits.Length];

        public double Seconds, Total, Max = double.NegativeInfinity;
        public double Cpu, Gpu, Display, Ram, Storage, Board, Extras, Monitors, PsuLoss, Unattributed;
        public double CpuLoad, GpuLoad, GpuLoadSeconds, Brightness, BrightnessSeconds;
        public double DisplayOn, Idle, Locked, Battery, Measured, Calibrated, Estimated;
        public int Count;
        public readonly SortedDictionary<int, double> Sources = [];
        public readonly SortedDictionary<int, double> Scopes = [];

        public void Add(Reading r)
        {
            var d = r.DeltaSeconds;
            var p = r.Components;
            Count++;
            Seconds += d;
            Total += r.TotalW * d;
            Max = Math.Max(Max, r.TotalW);
            Cpu += p.Cpu * d;
            Gpu += p.Gpu * d;
            Display += p.Display * d;
            Ram += p.Ram * d;
            Storage += p.Storage * d;
            Board += p.Board * d;
            Extras += p.Extras * d;
            Monitors += p.Monitors * d;
            PsuLoss += p.PsuLoss * d;
            Unattributed += p.Unattributed * d;
            CpuLoad += r.CpuLoad * d;
            if (r.GpuLoad is { } gpuLoad)
            {
                GpuLoad += gpuLoad * d;
                GpuLoadSeconds += d;
            }
            if (r.Brightness is { } brightness)
            {
                Brightness += brightness * d;
                BrightnessSeconds += d;
            }
            if (r.DisplayOn) DisplayOn += d;
            if (r.UserIdle) Idle += d;
            if (r.SessionLocked) Locked += d;
            if (r.OnBattery) Battery += d;
            switch (r.Quality)
            {
                case Quality.Measured:
                    Measured += d;
                    break;
                case Quality.Calibrated:
                    Calibrated += d;
                    break;
                default:
                    Estimated += d;
                    break;
            }
            Sources[(int)r.TotalSource] = Sources.GetValueOrDefault((int)r.TotalSource) + d;
            Scopes[(int)r.GpuScope] = Scopes.GetValueOrDefault((int)r.GpuScope) + d;
            for (var i = 0; i < Bits.Length; i++)
            {
                if (r.Measured.HasFlag(Bits[i])) _bitSeconds[i] += d;
            }
        }

        /// <summary>Each part measured for at least half the seconds.</summary>
        public int Mask()
        {
            var mask = MeasuredParts.None;
            for (var i = 0; i < Bits.Length; i++)
            {
                if (_bitSeconds[i] >= Seconds / 2) mask |= Bits[i];
            }
            return (int)mask;
        }
    }
}
