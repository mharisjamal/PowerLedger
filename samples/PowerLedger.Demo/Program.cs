using System.Diagnostics;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;
using PowerLedger.Storage;

namespace PowerLedger.Demo;

/// <summary>Preview runner: samples the machine for a while, stores the result, and prints the ledger.</summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerLedger", "demo.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = SqliteDatabase.OpenAndMigrate(path);

        var tariffs = new TariffRepository(db);
        if (tariffs.All().Count == 0) tariffs.Add(new Tariff(DateTimeOffset.UtcNow.AddYears(-1), 0.17m, "USD"));

        Console.WriteLine();
        Console.WriteLine($"  PowerLedger preview    database {path}");
        Console.WriteLine();

        if (args.Length > 0 && args[0].Equals("report", StringComparison.OrdinalIgnoreCase))
        {
            PrintReport(db);
            return;
        }

        var seconds = args.Length > 0 && int.TryParse(args[0], out var requested) ? Math.Clamp(requested, 5, 3600) : 60;
        Record(db, seconds);
        PrintReport(db);
    }

    private static void Record(SqliteDatabase db, int seconds)
    {
        var facts = HardwareInventory.Detect();
        using var sensors = MachineSensors.Create(displayOn: () => true, sessionLocked: () => false);

        var profile = facts.ToProfile(facts.Chassis == ChassisKind.Laptop ? MachineProfile.DefaultLaptop : MachineProfile.DefaultDesktop);
        var hardware = new HardwareFacts(
            facts.CpuTdpW ?? (facts.Chassis == ChassisKind.Laptop ? HardwareFacts.LaptopDefaults.CpuTdpW : HardwareFacts.DesktopDefaults.CpuTdpW),
            facts.GpuTdpW ?? (facts.Chassis == ChassisKind.Laptop ? HardwareFacts.LaptopDefaults.GpuTdpW : HardwareFacts.DesktopDefaults.GpuTdpW));

        // Learn fast so a short preview can reach Calibrated; the service uses 10-minute thresholds.
        var learner = new CalibrationLearner(new CalibrationOptions(HalfLifeSamples: 60, MinBucketSamples: 15, MinTotalSamples: 15));
        var calibration = new CalibrationRepository(db);
        learner.Import(calibration.Load(facts.Hash));
        var model = new PowerModel(profile, hardware, new PowerModelOptions(), learner);

        new InventoryRepository(db).Upsert(new InventoryRecord(facts.Hash, DateTimeOffset.UtcNow, facts.ToJson()));

        var raw = new RawSampleRepository(db);
        var aggregates = new AggregateRepository(db);
        var sessions = new SessionRepository(db);
        sessions.CloseAllOpen(DateTimeOffset.UtcNow);
        var session = sessions.Open(SessionReason.ServiceStart, DateTimeOffset.UtcNow);

        Console.WriteLine($"  {facts.CpuName}");
        Console.WriteLine($"  {facts.GpuName ?? "no discrete GPU"}    {facts.Chassis}    inventory {facts.Hash}");
        Console.WriteLine();
        Console.WriteLine($"  Recording for {seconds} s. Unplug the charger to see measured readings.");
        Console.WriteLine();

        var clock = Stopwatch.StartNew();
        var previous = TimeSpan.Zero;
        var pending = new List<Reading>();
        var minute = new List<Reading>();
        var minuteStart = FloorMinute(DateTimeOffset.UtcNow);

        for (var tick = 0; tick < seconds; tick++)
        {
            Thread.Sleep(1000);
            var elapsed = clock.Elapsed;
            var delta = (elapsed - previous).TotalSeconds;
            previous = elapsed;

            var now = DateTimeOffset.UtcNow;
            var sample = sensors.Read(now, delta);
            var reading = model.Evaluate(sample);
            learner.Observe(sample, reading.Components.Cpu, reading.Components.Gpu, reading.Components.Display);

            if (FloorMinute(now) != minuteStart)
            {
                if (minute.Count > 0) aggregates.UpsertMinute(Downsampler.ToMinute(minuteStart, minute, EnergyIntegrator.GapThresholdFor(1)));
                minute.Clear();
                minuteStart = FloorMinute(now);
            }

            minute.Add(reading);
            pending.Add(reading);
            if (pending.Count >= 10)
            {
                raw.InsertBatch(pending);
                pending.Clear();
            }

            PrintLive(reading, learner);
        }

        if (pending.Count > 0) raw.InsertBatch(pending);
        if (minute.Count > 0) aggregates.UpsertMinute(Downsampler.ToMinute(minuteStart, minute, EnergyIntegrator.GapThresholdFor(1)));
        calibration.Save(facts.Hash, learner.Export(), DateTimeOffset.UtcNow);
        sessions.Close(session, DateTimeOffset.UtcNow, SessionReason.ServiceStop);

        Console.WriteLine();
        Console.WriteLine();
        foreach (var health in sensors.Sampler.Health.Where(h => !h.Supported))
        {
            Console.WriteLine($"  {health.Name} unavailable: {health.Unavailable}");
        }
        if (sensors.Validator.SuspectCount > 0) Console.WriteLine($"  {sensors.Validator.SuspectCount} ticks marked suspect");
        Console.WriteLine();
    }

    private static void PrintLive(Reading r, CalibrationLearner learner)
    {
        var label = r.Quality switch
        {
            Quality.Measured => "measured  ",
            Quality.Calibrated => "calibrated",
            _ => "estimated ",
        };
        var p = r.Components;
        var rest = p.Unattributed + p.Ram + p.Storage + p.Board + p.Extras + p.PsuLoss + p.Monitors;
        Console.Write($"\r  {r.TotalW,6:F1} W   {label}   cpu {p.Cpu,5:F1}   gpu {p.Gpu,4:F1}   display {p.Display,4:F1}   rest {rest,5:F1}   learned {learner.TotalSamples,4} ticks ");
    }

    private static void PrintReport(SqliteDatabase db)
    {
        var now = DateTimeOffset.UtcNow;
        var (totals, days) = new ReportQueries(db).Report(now.AddDays(-30), now, TimeZoneInfo.Local);
        var currency = totals.Currency ?? "";

        Console.WriteLine("  Last 30 days");
        Console.WriteLine("  " + new string('─', 58));
        Row("Energy", $"{totals.EnergyKwh:F3} kWh");
        Row("Cost", $"{totals.Cost:F2} {currency}");
        Row("Average while on", $"{totals.AvgW:F1} W");
        Row("Peak", totals.PeakAt is { } at ? $"{totals.PeakW:F1} W at {at.ToLocalTime():ddd HH:mm}" : "none yet");
        Row("Recorded", Duration(totals.OnHours));
        Row("Idle, display on", Duration(totals.IdleOnHours));
        Row("Asleep", Duration(totals.AsleepHours));
        Row("Not monitored", Duration(totals.UnmonitoredHours));
        Row("CO2", $"{Co2.Kg(totals.EnergyKwh, Co2.DefaultKgPerKwh):F3} kg");
        Row("Same as", $"{Comparisons.LedBulbHours(totals.EnergyKwh):F1} h of a 10 W LED bulb");
        Row("Quality", $"{totals.MeasuredShare:P0} measured, {totals.CalibratedShare:P0} calibrated, {totals.EstimatedShare:P0} estimated");
        Console.WriteLine();

        if (days.Count > 0)
        {
            Console.WriteLine("  By day");
            Console.WriteLine("  " + new string('─', 58));
            foreach (var day in days)
            {
                var bar = new string('█', (int)Math.Clamp(day.EnergyKwh * 200, 1, 30));
                Console.WriteLine($"  {day.Day:ddd dd MMM}   {day.EnergyKwh,7:F3} kWh   {day.Cost,6:F2} {currency}   {bar}");
            }
            Console.WriteLine();
        }
    }

    private static void Row(string label, string value)
        => Console.WriteLine($"  {label} {new string('.', Math.Max(2, 26 - label.Length))} {value}");

    private static string Duration(double hours)
    {
        var span = TimeSpan.FromHours(hours);
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
            : $"{span.Minutes}m {span.Seconds:00}s";
    }

    private static DateTimeOffset FloorMinute(DateTimeOffset t) => t.AddTicks(-(t.Ticks % TimeSpan.TicksPerMinute));
}
