using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class SamplingLoopTests
{
    // 00:55 UTC is 02:55 in a UTC+2 zone: five minutes before both the 03:00 purge and the top of a UTC hour.
    private static readonly DateTimeOffset Start = new(2026, 9, 12, 0, 55, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("PL+2", TimeSpan.FromHours(2), "PL+2", "PL+2");

    [Fact]
    public async Task Ten_minutes_with_a_sleep_in_the_middle_leave_the_right_rows_behind()
    {
        using var t = new TestDatabase();
        await using var loop = new Harness(t);
        var frames = loop.Feed.Subscribe();
        await loop.StartAsync();
        await loop.Ticks(300);                                      // 00:55:01 to 01:00:00
        await loop.Send(new SuspendCommand());
        loop.Clock.Advance(TimeSpan.FromMinutes(2));                // asleep until 01:02:00; the timer's tick is ignored
        await loop.Send(new ResumeCommand());
        await loop.Ticks(180);                                      // 01:02:01 to 01:05:00
        await loop.StopAsync();

        var raw = new RawSampleRepository(t.Db);
        raw.Count().ShouldBe(480);

        var aggregates = new AggregateRepository(t.Db);
        var minutes = aggregates.ReadMinutes(Start, Start.AddMinutes(11));
        minutes.Select(m => m.Start.Minute).ShouldBe(new[] { 55, 56, 57, 58, 59, 0, 2, 3, 4, 5 });
        minutes.Sum(m => m.SampleCount).ShouldBe(480);
        minutes.Single(m => m.Start == Start.AddMinutes(7)).GapSeconds.ShouldBe(121, 1e-6);   // 01:00:00 to 01:02:01
        minutes.Sum(m => m.EnergyWh).ShouldBe(raw.Read(Start, Start.AddMinutes(11)).Sum(r => EnergyIntegrator.Integrate(r).Wh), 1e-9);

        var hour = aggregates.ReadHours(Start.AddHours(-1), Start.AddHours(2)).Single();       // 01:00 is not over yet
        hour.Start.ShouldBe(Start.AddMinutes(-55));
        hour.SampleCount.ShouldBe(299);

        var sessions = new SessionRepository(t.Db).List(Start.AddHours(-1), Start.AddHours(1));
        sessions.Select(s => (s.Reason, s.EndReason, s.Start, s.End)).ShouldBe(new (SessionReason, SessionReason?, DateTimeOffset, DateTimeOffset?)[]
        {
            (SessionReason.ServiceStart, SessionReason.Suspend, Start, Start.AddMinutes(5)),
            (SessionReason.Resume, SessionReason.ServiceStop, Start.AddMinutes(7), Start.AddMinutes(10)),
        });

        loop.Sets.Count.ShouldBe(2);                                // a fresh sensor set a minute after the resume
        await WaitFor.True(() => loop.Sets.All(s => s.Disposed));
        loop.Detections.ShouldBe(1);                                // the hardware, at start only

        new SettingsRepository(t.Db).Get(RetentionRunner.LastPurgeKey)
            .ShouldBe(Start.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture));   // the 03:00 local purge
        var learned = new CalibrationLearner();
        learned.Import(new CalibrationRepository(t.Db).Load(Facts.Laptop().Hash));
        learned.TotalSeconds.ShouldBe(479);                         // all but the first after the resume, which spans the sleep

        var status = loop.Board.Status.ShouldNotBeNull();
        status.Ticks.ShouldBe(480);
        status.InventoryHash.ShouldBe(Facts.Laptop().Hash);
        status.Sources.Single().Name.ShouldBe("fake");
        ReadingFrame? last = null;
        while (frames.TryRead(out var frame)) last = frame;
        last.ShouldNotBeNull().Timestamp.ShouldBe(Start.AddMinutes(10));
    }

    /// <summary>
    /// Plan AE: every hour's apps add up to the hour row's processor and graphics energy, through the top of an hour, a sleep
    /// and a restart inside an hour: the same watts and intervals, gaps left out the same way. Watts and loads vary, with
    /// quiet minutes for the resting draw to be learned from, a card measured beside the processor's graphics, busy time
    /// nobody running explains and drivers' time.
    /// </summary>
    [Fact]
    public async Task Each_hours_energy_by_app_adds_up_to_its_hour_row()
    {
        using var t = new TestDatabase();
        var count = 0;
        Sample Script(DateTimeOffset ts, double delta)
        {
            var n = Interlocked.Increment(ref count);
            var quiet = ts.Minute % 3 == 0;
            var cardW = quiet ? 4.0 : 4 + (n % 11);
            return Samples.At(ts, delta, cpuW: quiet ? 2 + (n % 3 * 0.1) : 6 + (n % 7), cpuLoad: quiet ? 0.01 : 0.4) with
            {
                IGpuW = quiet ? 0.2 : 1.5,
                DGpuPresent = true,
                DGpuW = cardW,
                DGpuLoad = quiet ? 0 : 0.3,
                Gpus = [new GpuCard("Radeon RX 7800 XT", cardW, quiet ? 0 : 0.3)],
                Graphics = new GraphicsWork(new Dictionary<uint, GraphicsTime> { [200] = new(300, 400, 20, 30), [999] = new(100, 100, 0, 5) }),
            };
        }
        FakeProcesses Machine()
        {
            var processes = new FakeProcesses { ClosedPerRead = 100, DriversPerRead = 50 };
            processes.Add(new FakeProcesses.Fake(200, "chrome.exe", path: @"C:\Program Files\Google\Chrome\Application\chrome.exe"), 600, 600);
            processes.Add(new FakeProcesses.Fake(300, "game.exe", path: @"D:\Games\game.exe"), 300, 300);
            processes.Add(new FakeProcesses.Fake(100, "svchost.exe", session: 0, path: @"C:\WINDOWS\System32\svchost.exe"), 100, 100);
            return processes;
        }

        var first = Machine();
        await using (var loop = new Harness(t, makeSet: _ => new FakeSensorSet(Script), processes: () => first, foreground: front =>
        {
            front.Clear();
            front.Add(200);
        }))
        {
            await loop.StartAsync();
            await loop.Ticks(300);                                  // 00:55:01 to 01:00:00
            await loop.Send(new SuspendCommand());
            loop.Clock.Advance(TimeSpan.FromSeconds(30));           // shorter than the gap threshold: a gap for the wake alone
            await loop.Send(new ResumeCommand());
            await loop.Ticks(240);                                  // 01:00:31, which reaches back across the sleep, to 01:04:30
            await loop.StopAsync();
        }
        await WaitFor.True(() => first.Disposed);                   // let go last, after the stop is done, as the sensors are

        var second = Machine();
        await using (var loop = new Harness(t, makeSet: _ => new FakeSensorSet(Script), processes: () => second, at: Start.AddMinutes(55)))
        {
            await loop.StartAsync();
            await loop.Ticks(660);                                  // 01:50:01 to 02:01:00
            await loop.StopAsync();
        }

        var aggregates = new AggregateRepository(t.Db);
        var hours = aggregates.ReadHours(Start.AddHours(-1), Start.AddHours(3));
        hours.Select(hour => hour.Start).ShouldBe([Start.AddMinutes(-55), Start.AddMinutes(5)]);     // 02:00 is not over
        var apps = new AppHoursRepository(t.Db);
        foreach (var hour in hours)
        {
            var rows = apps.ReadHour(hour.Start);
            hour.CpuWh.ShouldBeGreaterThan(0);
            hour.GpuWh.ShouldBeGreaterThan(0);
            rows.Sum(row => row.CpuWh).ShouldBe(hour.CpuWh, 1e-9, $"processor, {hour.Start:HH:mm}");
            rows.Sum(row => row.GpuWh).ShouldBe(hour.GpuWh, 1e-9, $"graphics, {hour.Start:HH:mm}");
            rows.Select(row => row.Key).ShouldBe(
                [@"c:\program files\google\chrome\application\chrome.exe", @"d:\games\game.exe", AppKeys.Windows, AppKeys.Idle, AppKeys.Closed], ignoreOrder: true);
        }
        var underway = aggregates.ReadMinutes(Start.AddMinutes(65), Start.AddMinutes(70));             // 02:00 so far
        apps.ReadHour(Start.AddMinutes(65)).Sum(row => row.CpuWh).ShouldBe(underway.Sum(minute => minute.CpuWh), 1e-9);

        var chrome = apps.ReadHour(Start.AddMinutes(-55)).Single(row => row.Key.EndsWith("chrome.exe", StringComparison.Ordinal));
        (chrome.RunSeconds, chrome.FrontSeconds).ShouldBe((299.0, 299.0));
    }

    [Fact]
    public async Task After_a_crash_the_open_session_closes_at_its_last_tick_and_the_unfolded_minutes_are_caught_up()
    {
        using var t = new TestDatabase();
        var sessions = new SessionRepository(t.Db);
        sessions.Open(SessionReason.Boot, Start.AddMinutes(-10));
        new RawSampleRepository(t.Db).InsertBatch([.. Enumerable.Range(0, 150).Select(s => Readings.At(Start.AddMinutes(-5).AddSeconds(s)))]);

        await using var loop = new Harness(t);
        await loop.StartAsync();
        await loop.StopAsync();

        var list = sessions.List(Start.AddHours(-1), Start.AddHours(1));
        list[0].EndReason.ShouldBe(SessionReason.CrashRecovered);
        list[0].End.ShouldBe(Start.AddMinutes(-5).AddSeconds(149));
        list[1].Reason.ShouldBe(SessionReason.CrashRecovered);
        new AggregateRepository(t.Db).ReadMinutes(Start.AddMinutes(-5), Start).Select(m => m.SampleCount).ShouldBe(new[] { 60, 60, 30 });
    }

    [Fact]
    public async Task New_settings_take_effect_between_ticks_and_bad_ones_are_refused()
    {
        using var t = new TestDatabase();
        await using var loop = new Harness(t);
        await loop.StartAsync();
        await loop.Ticks(3);

        var slower = loop.Board.Settings.ShouldNotBeNull() with { SampleIntervalSeconds = 2 };
        await loop.Send(new ApplySettingsCommand(slower));
        loop.Board.Settings.ShouldBe(slower);
        new SettingsStore(new SettingsRepository(t.Db)).Load().ShouldBe(slower);

        var before = loop.TickCount;
        loop.Clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(100);
        loop.TickCount.ShouldBe(before);                            // one second is no longer a tick
        loop.Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitFor.True(() => loop.TickCount == before + 1);

        await Should.ThrowAsync<ArgumentException>(() => loop.Send(new ApplySettingsCommand(slower with { SampleIntervalSeconds = 9 })));
        loop.Board.Settings.ShouldBe(slower);
    }

    /// <summary>Review item 13: a resume that arrives without its suspend opens no session, so the rollups count the tick
    /// after it as awake; the apps must count it too, the same wake deciding both.</summary>
    [Fact]
    public async Task A_resume_without_its_suspend_counts_the_next_tick_for_the_apps_as_the_rollups_do()
    {
        using var t = new TestDatabase();
        var processes = new FakeProcesses();
        processes.Add(new FakeProcesses.Fake(200, "chrome.exe", path: @"C:\Program Files\Chrome\chrome.exe"), 600, 600);
        await using (var loop = new Harness(t, processes: () => processes))
        {
            await loop.StartAsync();
            await loop.Ticks(30);
            await loop.Send(new ResumeCommand());                   // no suspend came before it
            await loop.Ticks(30);
            await loop.StopAsync();
        }
        var minutes = new AggregateRepository(t.Db).ReadMinutes(Start, Start.AddMinutes(2));
        new AppHoursRepository(t.Db).ReadHour(Start.AddMinutes(-55)).Sum(row => row.CpuWh).ShouldBe(minutes.Sum(minute => minute.CpuWh), 1e-9);
    }

    /// <summary>Review item 14: writing the energy by app failing minute after minute is logged at the 1st, 2nd, 4th and 8th
    /// failure, not every minute.</summary>
    [Fact]
    public async Task A_write_of_the_energy_by_app_failing_every_minute_is_logged_ever_more_rarely()
    {
        using var t = new TestDatabase();
        var log = new RecordingLogger<SamplingLoop>();
        var processes = new FakeProcesses();
        processes.Add(new FakeProcesses.Fake(200, "chrome.exe", path: @"C:\Users\alice\AppData\Local\Chrome\chrome.exe"), 600, 600);
        await using var loop = new Harness(t, processes: () => processes, log: log);
        await loop.StartAsync();
        using (var c = t.Db.Open())
        {
            using var fail = c.CreateCommand();
            fail.CommandText = "CREATE TRIGGER no_room BEFORE INSERT ON app_hours BEGIN SELECT RAISE(ABORT, 'no room'); END";
            fail.ExecuteNonQuery();
        }
        await loop.Ticks(4 * 60);                                   // four failed writes, to 00:59:00
        log.Warnings.Count(warning => warning.StartsWith("Writing the energy by app", StringComparison.Ordinal)).ShouldBe(3);
    }

    [Fact]
    public async Task A_hung_sensor_read_costs_one_tick_and_the_stall_counts_as_awake()
    {
        using var t = new TestDatabase();
        using var gate = new ManualResetEventSlim(false);
        var reads = 0;
        // 10.5 s keeps the watchdog's deadline off the one-second tick grid, so the fake clock fires them in a fixed order.
        var options = LoopOptions.Default with { SensorTimeout = TimeSpan.FromSeconds(10.5) };
        await using var loop = new Harness(t, options, index => index == 0
            ? new FakeSensorSet((ts, delta) =>
            {
                if (Interlocked.Increment(ref reads) == 3) gate.Wait();
                return Samples.At(ts, delta, batteryW: 20);
            })
            : new FakeSensorSet((ts, delta) => Samples.At(ts, delta, batteryW: 20)));
        try
        {
            await loop.StartAsync();
            await loop.Ticks(2);                                    // 00:55:01 and 00:55:02
            loop.Clock.Advance(TimeSpan.FromSeconds(1));            // the third read hangs
            await WaitFor.True(() => Volatile.Read(ref reads) == 3);
            loop.Clock.Advance(options.SensorTimeout);              // the watchdog gives up at 00:55:13.5 and a fresh set reads at once
            await WaitFor.True(() => loop.TickCount == 3);
            loop.Board.Status.ShouldNotBeNull().SensorRestarts.ShouldBe(1);
            loop.Sets.Count.ShouldBe(2);
        }
        finally
        {
            gate.Set();
        }
        await loop.StopAsync();

        var last = new RawSampleRepository(t.Db).Read(Start, Start.AddMinutes(1))[^1];
        last.Timestamp.ShouldBe(Start.AddSeconds(13.5));
        last.DeltaSeconds.ShouldBe(11.5, 1e-6);                      // from the last good tick
        var minute = new AggregateRepository(t.Db).ReadMinutes(Start, Start.AddMinutes(1)).ShouldHaveSingleItem();
        minute.GapSeconds.ShouldBe(0);                              // the machine was awake and drawing power (accuracy A17)
        minute.OnSeconds.ShouldBe(13.5, 1e-6);
    }

    [Fact]
    public async Task A_sleep_shorter_than_a_minute_is_still_a_gap()
    {
        using var t = new TestDatabase();
        await using var loop = new Harness(t);
        await loop.StartAsync();
        await loop.Ticks(10);                                       // 00:55:01 to 00:55:10
        await loop.Send(new SuspendCommand());
        loop.Clock.Advance(TimeSpan.FromSeconds(20));               // a short Modern Standby sleep
        await loop.Send(new ResumeCommand());
        await loop.Ticks(5);                                        // 00:55:31 to 00:55:35
        await loop.StopAsync();

        var minute = new AggregateRepository(t.Db).ReadMinutes(Start, Start.AddMinutes(1)).ShouldHaveSingleItem();
        minute.GapSeconds.ShouldBe(21, 1e-6);                       // 00:55:10 to 00:55:31
        minute.OnSeconds.ShouldBe(14, 1e-6);
    }

    /// <summary>Accuracy A10: a sleep is no longer free. Before anything is learned a laptop on battery loses half a watt,
    /// booked at the wall it was charged from (A9).</summary>
    [Fact]
    public Task A_sleep_books_what_the_laptop_draws_asleep_half_a_watt_before_it_has_learned_its_own()
        => SleepBooks(battery: null, expectedW: SleepPower.LaptopDrainW * BatteryRoundTrip.Factor, learned: null);

    /// <summary>Accuracy A10: the battery's charge as the laptop went to sleep and a minute after it woke, with what that
    /// minute awake drew from it added back, on battery throughout, teaches what it loses asleep, which the service keeps
    /// for the App and the sleeps after it are booked at. The sleep itself was booked as it woke, at what was known then.</summary>
    [Fact]
    public async Task A_sleep_on_battery_teaches_what_the_laptop_loses_asleep()
    {
        // 1.6 Wh in 2 h, and 59 s awake at 20 W from the battery before it is read again.
        var charges = new Queue<BatteryCharge>([new(OnAc: false, 40, 50), new(OnAc: false, 38.4 - (20 * 59 / 3600.0), 50)]);
        await SleepBooks(() => charges.Count > 0 ? charges.Dequeue() : null, expectedW: SleepPower.LaptopDrainW * BatteryRoundTrip.Factor, learned: 0.8);
    }

    /// <summary>Plan AC review: a battery read the moment the laptop wakes can still give its charge from before the sleep,
    /// which taught a drain of nothing. It is read again a minute on, and what that minute awake drew is added back.</summary>
    [Fact]
    public async Task The_charge_is_read_again_a_minute_after_the_wake_not_the_moment_of_it()
    {
        using var t = new TestDatabase();
        var woke = Start.AddSeconds(10).AddHours(2);
        FakeTimeProvider? clock = null;
        BatteryCharge? Battery() => clock!.GetUtcNow() < woke.AddSeconds(30)
            ? new(OnAc: false, 40, 50)                                   // as the sleep began, and stale at the wake
            : new(OnAc: false, 38.4 - (20 * 59 / 3600.0), 50);
        await using var loop = new Harness(t, battery: Battery);
        clock = loop.Clock;
        await loop.StartAsync();
        await loop.Ticks(10);                                       // 00:55:01 to 00:55:10
        await loop.Send(new SuspendCommand());
        loop.Clock.Advance(TimeSpan.FromHours(2));
        await loop.Send(new ResumeCommand());
        await loop.Ticks(59);
        SleepDrain.Parse(new SettingsRepository(t.Db).Get(SleepDrain.SettingsKey)).ShouldBe(SleepDrain.None);   // not yet
        await loop.Ticks(1);                                        // a minute after the wake
        await loop.StopAsync();

        SleepDrain.Parse(new SettingsRepository(t.Db).Get(SleepDrain.SettingsKey)).Watts.ShouldNotBeNull().ShouldBe(0.8, 1e-9);
    }

    /// <summary>Plan AC review: a laptop plugged in within the minute after it woke says nothing more of its sleep; one that
    /// sleeps again within it is read as it goes, which is as fresh as a charge gets.</summary>
    [Fact]
    public async Task Plugged_in_before_the_minute_nothing_is_learned_and_asleep_again_the_charge_then_is_taken()
    {
        using (var t = new TestDatabase())
        {
            var charges = new Queue<BatteryCharge>([new(OnAc: false, 40, 50), new(OnAc: true, 39, 50)]);
            var onAc = false;
            await using var loop = new Harness(
                t, battery: () => charges.Count > 0 ? charges.Dequeue() : null,
                makeSet: _ => new FakeSensorSet((ts, delta) => Volatile.Read(ref onAc) ? Samples.At(ts, delta) : Samples.At(ts, delta, batteryW: 20)));
            await loop.StartAsync();
            await loop.Ticks(10);
            await loop.Send(new SuspendCommand());
            loop.Clock.Advance(TimeSpan.FromHours(2));
            await loop.Send(new ResumeCommand());
            await loop.Ticks(10);
            Volatile.Write(ref onAc, true);
            await loop.Ticks(60);
            await loop.StopAsync();
            SleepDrain.Parse(new SettingsRepository(t.Db).Get(SleepDrain.SettingsKey)).ShouldBe(SleepDrain.None);
            charges.Count.ShouldBe(1, "the battery was not read again once on AC");
        }

        using (var t = new TestDatabase())
        {
            // 1.6 Wh in 2 h, then 9 s awake at 20 W before the next sleep.
            var charges = new Queue<BatteryCharge>([new(OnAc: false, 40, 50), new(OnAc: false, 38.4 - (20 * 9 / 3600.0), 50)]);
            await using var loop = new Harness(t, battery: () => charges.Count > 0 ? charges.Dequeue() : null);
            await loop.StartAsync();
            await loop.Ticks(10);
            await loop.Send(new SuspendCommand());
            loop.Clock.Advance(TimeSpan.FromHours(2));
            await loop.Send(new ResumeCommand());
            await loop.Ticks(10);
            await loop.Send(new SuspendCommand());
            await loop.StopAsync();
            SleepDrain.Parse(new SettingsRepository(t.Db).Get(SleepDrain.SettingsKey)).Watts.ShouldNotBeNull().ShouldBe(0.8, 1e-9);
        }
    }

    /// <summary>Plan AC review: a Fast Startup shutdown signs everyone out and then hibernates, which the service sees as a
    /// suspend. It draws nothing like a sleep, and the battery loses next to nothing in it, so it is booked as no energy
    /// and teaches nothing.</summary>
    [Fact]
    public async Task A_suspend_with_nobody_signed_in_books_nothing_and_learns_nothing()
    {
        var charges = new Queue<BatteryCharge>([new(OnAc: false, 40, 50), new(OnAc: false, 40, 50)]);
        await SleepBooks(() => charges.Count > 0 ? charges.Dequeue() : null, expectedW: 0, learned: null, signedIn: () => false);
    }

    /// <summary>Plan AC review: a hibernate draws nothing, so it books no energy and teaches nothing.</summary>
    [Fact]
    public async Task A_hibernate_books_nothing_and_learns_nothing()
    {
        var charges = new Queue<BatteryCharge>([new(OnAc: false, 40, 50), new(OnAc: false, 40, 50)]);
        await SleepBooks(() => charges.Count > 0 ? charges.Dequeue() : null, expectedW: 0, learned: null, hibernated: since => since.AddSeconds(3));
    }

    /// <summary>Plan AC review: a Modern Standby PC that hibernates hours into its sleep drew asleep until then.</summary>
    [Fact]
    public async Task A_sleep_that_went_on_into_a_hibernate_books_the_part_before_it()
    {
        var charges = new Queue<BatteryCharge>([new(OnAc: false, 40, 50), new(OnAc: false, 39.5, 50)]);
        await SleepBooks(
            () => charges.Count > 0 ? charges.Dequeue() : null, expectedW: SleepPower.LaptopDrainW * BatteryRoundTrip.Factor / 4, learned: null,
            hibernated: since => since.AddMinutes(30));
    }

    private static async Task SleepBooks(
        Func<BatteryCharge?>? battery, double expectedW, double? learned, Func<bool>? signedIn = null, Func<DateTimeOffset, DateTimeOffset?>? hibernated = null)
    {
        using var t = new TestDatabase();
        await using var loop = new Harness(t, battery: battery, signedIn: signedIn, hibernated: hibernated);
        await loop.StartAsync();
        await loop.Ticks(10);                                       // 00:55:01 to 00:55:10
        await loop.Send(new SuspendCommand());
        loop.Clock.Advance(TimeSpan.FromHours(2));
        await loop.Send(new ResumeCommand());
        await loop.Ticks(60);                                       // to the battery's second read, a minute on
        await loop.StopAsync();

        var minutes = new AggregateRepository(t.Db).ReadMinutes(Start, Start.AddHours(3));
        var gap = minutes.Sum(m => m.GapSeconds);
        gap.ShouldBe(2 * 3600 + 1, 1e-6);
        minutes.Sum(m => m.AsleepWh).ShouldBe(expectedW * gap / 3600, 1e-9);
        minutes.Sum(m => m.EnergyWh).ShouldBe(new RawSampleRepository(t.Db).Read(Start, Start.AddHours(3)).Sum(r => EnergyIntegrator.Integrate(r).Wh), 1e-9);
        var kept = SleepDrain.Parse(new SettingsRepository(t.Db).Get(SleepDrain.SettingsKey)).Watts;
        if (learned is { } watts) kept.ShouldNotBeNull().ShouldBe(watts, 1e-9);
        else kept.ShouldBeNull();
    }

    [Fact]
    public async Task Every_tick_runs_on_the_loops_own_thread_and_never_on_the_thread_pool()
    {
        using var t = new TestDatabase();
        var threads = new List<(int Id, bool Pool, string? Name)>();
        await using var loop = new Harness(t, makeSet: _ => new FakeSensorSet((ts, delta) =>
        {
            lock (threads) threads.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.Name));
            return Samples.At(ts, delta, batteryW: 20);
        }));
        await loop.StartAsync();
        await loop.Ticks(5);
        await loop.Send(new ApplySettingsCommand(loop.Board.Settings.ShouldNotBeNull() with { SampleIntervalSeconds = 2 }));
        loop.Clock.Advance(TimeSpan.FromSeconds(2));
        await WaitFor.True(() => loop.TickCount == 6);

        lock (threads)
        {
            threads.Count.ShouldBe(6);
            threads.Select(thread => thread.Id).Distinct().ShouldHaveSingleItem().ShouldNotBe(Environment.CurrentManagedThreadId);
            threads.ShouldAllBe(thread => !thread.Pool && thread.Name == "PowerLedger loop");
        }
    }

    [Fact]
    public async Task Stopping_while_a_sensor_read_hangs_still_writes_the_readings_and_closes_the_session()
    {
        using var t = new TestDatabase();
        using var gate = new ManualResetEventSlim(false);
        var reads = 0;
        await using var loop = new Harness(t, makeSet: _ => new FakeSensorSet((ts, delta) =>
        {
            if (Interlocked.Increment(ref reads) == 3) gate.Wait();
            return Samples.At(ts, delta, batteryW: 20);
        }));
        try
        {
            await loop.StartAsync();
            await loop.Ticks(2);
            loop.Clock.Advance(TimeSpan.FromSeconds(1));            // the third read hangs, long before the watchdog would
            await WaitFor.True(() => Volatile.Read(ref reads) == 3);
            await loop.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

            new RawSampleRepository(t.Db).Count().ShouldBe(2);
            new SessionRepository(t.Db).List(Start.AddHours(-1), Start.AddHours(1)).ShouldHaveSingleItem()
                .EndReason.ShouldBe(SessionReason.ServiceStop);
            loop.Board.Status.ShouldNotBeNull().SensorRestarts.ShouldBe(0);   // a stop is not a hang
            loop.Sets.ShouldHaveSingleItem().Disposed.ShouldBeFalse();         // still stuck in its call
        }
        finally
        {
            gate.Set();
        }
        await WaitFor.True(() => loop.Sets.Single().Disposed);
    }

    [Fact]
    public async Task A_resume_keeps_the_hardware_facts_and_the_sensor_set_refreshes_the_display_and_rebuilds_the_set_a_minute_later()
    {
        using var t = new TestDatabase();
        await using var loop = new Harness(t);
        await loop.StartAsync();
        await loop.Ticks(2);
        await loop.Send(new SuspendCommand());
        loop.Clock.Advance(TimeSpan.FromMinutes(5));
        await loop.Send(new ResumeCommand());
        loop.Detections.ShouldBe(1);
        loop.Sets.ShouldHaveSingleItem().DisplayRefreshes.ShouldBe(1);

        await loop.Ticks(59);                                       // 01:00:03 to 01:01:01: the set from before the sleep
        loop.Sets.ShouldHaveSingleItem().Reads.ShouldBe(61);
        await loop.Ticks(1);                                        // a minute after the resume
        loop.Sets.Count.ShouldBe(2);
        loop.Sets[0].Disposed.ShouldBeTrue();
        loop.Sets[1].Reads.ShouldBe(1);
        loop.Detections.ShouldBe(1);

        // A machine that goes back to sleep inside the minute rebuilds a minute after it next wakes.
        await loop.Send(new SuspendCommand());
        await loop.Send(new ResumeCommand());
        await loop.Ticks(30);
        await loop.Send(new SuspendCommand());
        await loop.Send(new ResumeCommand());
        await loop.Ticks(59);
        loop.Sets.Count.ShouldBe(2);
        await loop.Ticks(1);
        loop.Sets.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Devices_coming_and_going_have_the_hardware_detected_again_once_they_settle_and_the_set_rebuilt()
    {
        using var t = new TestDatabase();
        await using var loop = new Harness(t);
        await loop.StartAsync();
        await loop.Ticks(1);
        await loop.Send(new DevicesChangedCommand());
        await loop.Ticks(20);
        await loop.Send(new DevicesChangedCommand());               // a dock brings its devices one after another
        await loop.Ticks(29);
        (loop.Detections, loop.Sets.Count).ShouldBe((1, 1));
        await loop.Ticks(1);                                        // half a minute after the last
        (loop.Detections, loop.Sets.Count).ShouldBe((2, 2));
        await loop.Ticks(60);
        (loop.Detections, loop.Sets.Count).ShouldBe((2, 2));
    }

    [Fact]
    public async Task Detected_monitors_are_listed_in_the_status_and_added_to_the_reading()
    {
        using var t = new TestDatabase();
        await using var loop = new Harness(t);
        await loop.StartAsync();
        await loop.Ticks(1);
        var alone = loop.Board.Status.ShouldNotBeNull();
        alone.Monitors.ShouldNotBeNull().ShouldBeEmpty();
        alone.Last.ShouldNotBeNull().TotalW.ShouldBe(20, 1e-9);

        loop.Monitors.Detected([MonitorBoardTests.Dell]);                  // as the sensor thread does
        await loop.Ticks(1);

        var status = loop.Board.Status.ShouldNotBeNull();
        var monitor = status.Monitors.ShouldNotBeNull().ShouldHaveSingleItem();
        monitor.Name.ShouldBe("DELL U2723QE");
        monitor.WattsNow.ShouldBe(MonitorBoardTests.DellAt(null), 1e-9);
        status.Last.ShouldNotBeNull().Components.Monitors.ShouldBe(MonitorBoardTests.DellAt(null), 1e-9);
        status.Last.TotalW.ShouldBe(20 + MonitorBoardTests.DellAt(null), 1e-9);   // on top of the battery's measured 20 W
    }

    [Fact]
    public async Task The_status_lists_the_ups_the_latest_sample_read_and_once_it_is_said_to_power_this_pc_it_gives_the_total()
    {
        using var t = new TestDatabase();
        var answers = 0;
        await using var loop = new Harness(t, makeSet: _ => new FakeSensorSet((ts, delta) => Volatile.Read(ref answers) == 0
            ? Samples.At(ts, delta)
            : Samples.At(ts, delta) with { UpsOutputW = 142, UpsSource = UpsPowerSource.LoadOfRatedWatts, UpsName = "APC Back-UPS ES 850G2" }));
        await loop.StartAsync();
        await loop.Ticks(1);
        loop.Board.Status.ShouldNotBeNull().PowerDevices.ShouldNotBeNull().ShouldBeEmpty();

        Volatile.Write(ref answers, 1);                                    // the UPS is plugged in
        await loop.Ticks(1);
        var status = loop.Board.Status.ShouldNotBeNull();
        status.PowerDevices.ShouldNotBeNull().ShouldHaveSingleItem()
            .ShouldBe(new PowerDeviceStatus(PowerDeviceKind.Ups, "APC Back-UPS ES 850G2", 142, "load of its rated watts"));
        status.Last.ShouldNotBeNull().Total.ShouldBe(TotalSource.Model);  // until the user says what it powers

        var settings = loop.Board.Settings.ShouldNotBeNull();
        await loop.Send(new ApplySettingsCommand(settings with { Profile = settings.Profile with { UpsLoad = UpsLoad.ThisPc } }));
        await loop.Ticks(1);
        var last = loop.Board.Status.ShouldNotBeNull().Last.ShouldNotBeNull();
        (last.Total, last.Quality).ShouldBe((TotalSource.Ups, Quality.Measured));
        last.TotalW.ShouldBe(142, 1e-9);
    }

    [Fact]
    public async Task The_board_holds_the_detected_hardware_and_whether_the_last_reading_found_a_discrete_card()
    {
        using var t = new TestDatabase();
        var present = false;
        await using var loop = new Harness(t, makeSet: _ => new FakeSensorSet((ts, delta) =>
            Samples.At(ts, delta, batteryW: 20) with { DGpuPresent = Volatile.Read(ref present) }));
        await loop.StartAsync();
        loop.Board.Facts.ShouldBe(Facts.Laptop());
        await loop.Ticks(1);
        loop.Board.DiscreteGpu.ShouldBeFalse();

        Volatile.Write(ref present, true);
        await loop.Ticks(1);
        loop.Board.DiscreteGpu.ShouldBeTrue();
    }

    [Fact]
    public async Task Each_reading_is_written_with_where_its_total_came_from_what_the_card_covered_and_what_was_measured()
    {
        using var t = new TestDatabase();
        await using var loop = new Harness(t, makeSet: _ => new FakeSensorSet((ts, delta) =>
            Samples.At(ts, delta, batteryW: 20) with { DGpuPresent = true, DGpuW = 6, DGpuScope = GpuPowerScope.ChipOnly }));
        await loop.StartAsync();
        await loop.Ticks(3);
        await loop.StopAsync();

        var rows = new RawSampleRepository(t.Db).ReadRange(Start.ToUnixTimeMilliseconds(), Start.AddMinutes(1).ToUnixTimeMilliseconds());
        rows.Count.ShouldBe(3);
        foreach (var row in rows)
        {
            (row.TotalSource, row.GpuScope, row.Measured)
                .ShouldBe((TotalSource.Battery, GpuPowerScope.ChipOnly, MeasuredParts.Cpu | MeasuredParts.Gpu | MeasuredParts.Total));
        }
    }

    [Fact]
    public async Task A_monitor_running_off_the_laptop_is_in_its_battery_rate_and_is_kept_out_of_what_the_learner_learns()
    {
        using var t = new TestDatabase();
        new SettingsStore(new SettingsRepository(t.Db)).Save(ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultLaptop with { Monitors = [new MonitorChoice { Key = MonitorBoardTests.Portable.Key, Watts = 6 }] },
        });
        await using var loop = new Harness(t);
        loop.Monitors.Detected([MonitorBoardTests.Dell, MonitorBoardTests.Portable]);
        await loop.StartAsync();
        await loop.Ticks(3);
        await loop.StopAsync();

        // The battery's 20 W less the processor's 8 W, the 15.3-inch panel's 3.75 W at half brightness, and the portable
        // monitor's 6 W. The Dell has a plug of its own, so its 33.69 W are added to the battery's and none of them is learned.
        var status = loop.Board.Status.ShouldNotBeNull();
        status.Monitors.ShouldNotBeNull().Select(m => m.OwnPlug).ShouldBe([true, false]);
        var last = status.Last.ShouldNotBeNull();
        last.TotalW.ShouldBe(20 + MonitorBoardTests.DellAt(null), 1e-9);
        last.Components.Monitors.ShouldBe(MonitorBoardTests.DellAt(null) + 6, 1e-9);
        last.Components.Unattributed.ShouldBe(20 - 8 - 3.75 - 6, 1e-9);

        var learned = new CalibrationLearner();
        learned.Import(new CalibrationRepository(t.Db).Load(Facts.Laptop().Hash));
        var bucket = learned.Export().Buckets.ShouldHaveSingleItem();
        bucket.Samples.ShouldBe(3);
        bucket.BaselineW.ShouldBe(20 - 8 - 3.75 - 6, 1e-9);
    }

    [Fact]
    public async Task A_monitor_running_off_the_laptop_that_the_user_left_out_still_counts_and_is_kept_out_of_what_the_learner_learns()
    {
        using var t = new TestDatabase();
        new SettingsStore(new SettingsRepository(t.Db)).Save(ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultLaptop with
            {
                Monitors = [new MonitorChoice { Key = MonitorBoardTests.Portable.Key, Counted = false, Watts = 6 }],
            },
        });
        await using var loop = new Harness(t);
        loop.Monitors.Detected([MonitorBoardTests.Portable]);
        await loop.StartAsync();
        await loop.Ticks(3);
        await loop.StopAsync();

        // The battery's 20 W hold the monitor's 6 W whatever the choice says, so the monitor counts, and the learner takes its
        // figure out with the processor's 8 W and the panel's 3.75 W, which leaves 2.25 W.
        var status = loop.Board.Status.ShouldNotBeNull();
        var monitor = status.Monitors.ShouldNotBeNull().ShouldHaveSingleItem();
        (monitor.OwnPlug, monitor.Counted).ShouldBe((false, true));
        var last = status.Last.ShouldNotBeNull();
        last.TotalW.ShouldBe(20, 1e-9);
        last.Components.Monitors.ShouldBe(6, 1e-9);
        last.Components.Unattributed.ShouldBe(20 - 8 - 3.75 - 6, 1e-9);

        var learned = new CalibrationLearner();
        learned.Import(new CalibrationRepository(t.Db).Load(Facts.Laptop().Hash));
        var bucket = learned.Export().Buckets.ShouldHaveSingleItem();
        bucket.Samples.ShouldBe(3);
        bucket.BaselineW.ShouldBe(20 - 8 - 3.75 - 6, 1e-9);
    }

    [Fact]
    public async Task Nothing_is_learned_while_the_laptop_s_lid_is_shut()
    {
        using var t = new TestDatabase();
        await using var loop = new Harness(t, makeSet: _ => new FakeSensorSet((ts, delta) => Samples.At(ts, delta, batteryW: 20) with { PanelInactive = true }));
        await loop.StartAsync();
        await loop.Ticks(3);
        await loop.StopAsync();

        // The panel draws nothing then, so the rest of the laptop would be learned with the panel's figure in it.
        var learned = new CalibrationLearner();
        learned.Import(new CalibrationRepository(t.Db).Load(Facts.Laptop().Hash));
        learned.TotalSeconds.ShouldBe(0);
        loop.Board.Status.ShouldNotBeNull().Last.ShouldNotBeNull().Components.Display.ShouldBe(0);
    }

    [Fact]
    public async Task The_users_monitor_choices_reach_the_board_from_the_stored_settings_and_from_new_ones()
    {
        using var t = new TestDatabase();
        new SettingsStore(new SettingsRepository(t.Db)).Save(ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultLaptop with { Monitors = [new MonitorChoice { Key = MonitorBoardTests.Dell.Key, Watts = 40 }] },
        });
        await using var loop = new Harness(t);
        loop.Monitors.Detected([MonitorBoardTests.Dell]);
        await loop.StartAsync();
        await loop.Ticks(1);
        loop.Board.Status.ShouldNotBeNull().Last.ShouldNotBeNull().Components.Monitors.ShouldBe(40, 1e-9);

        var settings = loop.Board.Settings.ShouldNotBeNull();
        await loop.Send(new ApplySettingsCommand(settings with
        {
            Profile = settings.Profile with { Monitors = [new MonitorChoice { Key = MonitorBoardTests.Dell.Key, Counted = false }] },
        }));
        await loop.Ticks(1);

        var status = loop.Board.Status.ShouldNotBeNull();
        status.Last.ShouldNotBeNull().Components.Monitors.ShouldBe(0);
        status.Monitors.ShouldNotBeNull().ShouldHaveSingleItem().Counted.ShouldBeFalse();
    }

    [Fact]
    public async Task Old_monitor_settings_wait_for_a_monitor_to_be_detected_and_are_then_carried_over_once()
    {
        using var t = new TestDatabase();
        var store = new SettingsStore(new SettingsRepository(t.Db));
        var old = ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultLaptop with { ExternalMonitors = 1, IncludeMonitors = true, MonitorWatts = 30 },
        };
        store.Save(old);
        store.SaveProfileHash(Facts.Laptop().Hash);
        await using var loop = new Harness(t);
        await loop.StartAsync();

        // Upgraded with the monitor unplugged: the old settings stay as they are.
        await loop.Ticks(2);
        store.Load().ShouldBe(old);
        loop.Board.Settings.ShouldBe(old);

        loop.Monitors.Detected([MonitorBoardTests.Dell]);
        await loop.Ticks(1);
        var carried = old with
        {
            Profile = old.Profile with
            {
                ExternalMonitors = 0, IncludeMonitors = false, Monitors = [new MonitorChoice { Key = MonitorBoardTests.Dell.Key, Watts = 30 }],
            },
        };
        store.Load().ShouldBe(carried);
        loop.Board.Settings.ShouldBe(carried);

        await loop.Ticks(1);
        var status = loop.Board.Status.ShouldNotBeNull();
        status.Monitors.ShouldNotBeNull().ShouldHaveSingleItem().Source.ShouldBe(MonitorSource.Typed);
        status.Last.ShouldNotBeNull().Components.Monitors.ShouldBe(30, 1e-9);

        // Only once: a monitor plugged in later counts at its own figure, and the settings are left alone.
        loop.Monitors.Detected([MonitorBoardTests.Dell, MonitorBoardTests.Unnamed]);
        await loop.Ticks(2);
        store.Load().ShouldBe(carried);
        loop.Board.Status.ShouldNotBeNull().Monitors.ShouldNotBeNull().Select(m => m.Counted).ShouldBe([true, true]);
    }

    [Fact]
    public async Task Monitors_old_settings_had_but_did_not_count_stay_uncounted_and_so_does_one_attached_later()
    {
        using var t = new TestDatabase();
        var store = new SettingsStore(new SettingsRepository(t.Db));
        store.Save(ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { ExternalMonitors = 2 } });
        store.SaveProfileHash(Facts.Laptop().Hash);
        await using var loop = new Harness(t);
        loop.Monitors.Detected([MonitorBoardTests.Dell]);                  // the other monitor is switched off
        await loop.StartAsync();

        await loop.Ticks(2);

        var profile = store.Load().ShouldNotBeNull().Profile;
        profile.Monitors.ShouldBeEmpty();
        profile.CountMonitorsByDefault.ShouldBeFalse();
        profile.ExternalMonitors.ShouldBe(0);
        var status = loop.Board.Status.ShouldNotBeNull();
        status.Monitors.ShouldNotBeNull().ShouldHaveSingleItem().Counted.ShouldBeFalse();
        status.Last.ShouldNotBeNull().Components.Monitors.ShouldBe(0);

        // Switched on later, the other monitor is left out as the old settings left it.
        loop.Monitors.Detected([MonitorBoardTests.Dell, MonitorBoardTests.Unnamed]);
        await loop.Ticks(1);

        status = loop.Board.Status.ShouldNotBeNull();
        status.Monitors.ShouldNotBeNull().Select(m => m.Counted).ShouldBe([false, false]);
        status.Last.ShouldNotBeNull().Components.Monitors.ShouldBe(0);
        status.Last.TotalW.ShouldBe(20, 1e-9);
        store.Load().ShouldNotBeNull().Profile.ShouldBe(profile);
    }

    [Fact]
    public async Task A_fresh_install_carries_nothing_over_and_counts_the_monitors_it_detects()
    {
        using var t = new TestDatabase();
        await using var loop = new Harness(t);
        loop.Monitors.Detected([MonitorBoardTests.Dell]);
        await loop.StartAsync();

        await loop.Ticks(2);

        new SettingsStore(new SettingsRepository(t.Db)).Load().ShouldNotBeNull().Profile.Monitors.ShouldBeEmpty();
        loop.Board.Settings.ShouldNotBeNull().Profile.Monitors.ShouldBeEmpty();
        loop.Board.Status.ShouldNotBeNull().Last.ShouldNotBeNull().Components.Monitors.ShouldBe(MonitorBoardTests.DellAt(null), 1e-9);
    }

    [Fact]
    public async Task Unwatched_the_loop_reads_every_five_seconds_and_at_the_set_interval_from_the_moment_someone_looks()
    {
        using var t = new TestDatabase();
        var watched = false;
        await using var loop = new Harness(t, watched: () => Volatile.Read(ref watched));
        await loop.StartAsync();
        loop.Clock.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(100);
        loop.TickCount.ShouldBe(0);
        loop.Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitFor.True(() => loop.TickCount == 1);              // 00:55:05

        Volatile.Write(ref watched, true);                          // the App's window opens
        await loop.Send(new WatchersChangedCommand());
        await loop.Ticks(3);                                        // 00:55:06 to 00:55:08

        Volatile.Write(ref watched, false);
        await loop.Send(new WatchersChangedCommand());
        loop.Clock.Advance(TimeSpan.FromSeconds(4));
        await Task.Delay(100);
        loop.TickCount.ShouldBe(4);
        loop.Clock.Advance(TimeSpan.FromSeconds(1.5));              // a beat Windows put off a little
        await WaitFor.True(() => loop.TickCount == 5);              // 00:55:13.5
        await loop.StopAsync();

        var rows = new RawSampleRepository(t.Db).Read(Start, Start.AddMinutes(1));
        rows.Select(r => r.DeltaSeconds).ShouldBe([5, 1, 1, 1, 5.5]);
        var minute = new AggregateRepository(t.Db).ReadMinutes(Start, Start.AddMinutes(1)).ShouldHaveSingleItem();
        minute.GapSeconds.ShouldBe(0);                              // a five-second reading, late or not, is no gap
        minute.OnSeconds.ShouldBe(13.5, 1e-9);
    }

    [Fact]
    public async Task A_resume_is_done_only_once_its_new_timer_runs()
    {
        using var t = new TestDatabase();
        var resume = new ResumeCommand();
        await using var loop = new Harness(t, loopClock: clock => new LateTimerClock(clock, () => resume.Done.IsCompleted));
        await loop.StartAsync();
        await loop.Send(new SuspendCommand());
        await loop.Send(resume);
        await loop.Ticks(1);                                        // the clock moves the moment the resume is done
    }

    [Fact]
    public async Task A_new_interval_is_done_only_once_its_new_timer_runs()
    {
        using var t = new TestDatabase();
        ApplySettingsCommand? apply = null;
        await using var loop = new Harness(t, loopClock: clock => new LateTimerClock(clock, () => apply?.Done.IsCompleted == true));
        await loop.StartAsync();
        apply = new ApplySettingsCommand(loop.Board.Settings.ShouldNotBeNull() with { SampleIntervalSeconds = 2 });
        await loop.Send(apply);
        loop.Clock.Advance(TimeSpan.FromSeconds(2));                // the clock moves the moment the settings are done
        await WaitFor.True(() => loop.TickCount == 1);
    }

    /// <summary>
    /// The fake clock as the loop sees it, except that a periodic timer the loop asks for while <paramref name="late"/>
    /// holds is made only after the test has moved the clock: what a busy machine does when it parks the loop's thread
    /// just before the call. A timer made then is due a whole interval after the tick the test gave, which is never seen.
    /// </summary>
    private sealed class LateTimerClock(FakeTimeProvider clock, Func<bool> late) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => clock.LocalTimeZone;

        public override long TimestampFrequency => clock.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

        public override long GetTimestamp() => clock.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan && late())
            {
                var asked = clock.GetUtcNow();
                SpinWait.SpinUntil(() => clock.GetUtcNow() > asked, TimeSpan.FromSeconds(5));
            }
            return clock.CreateTimer(callback, state, dueTime, period);
        }
    }

    /// <summary>The loop wired to a fake clock, fake sensors that report 20 W from the battery, a monitor board that sees only
    /// the monitors a test says it detected, and a temp database.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly SamplingLoop _loop;
        private readonly List<FakeSensorSet> _sets = [];
        private int _detections;

        /// <param name="loopClock">What the loop sees of <see cref="Clock"/>, when that is not the fake clock itself.</param>
        /// <param name="watched">Whether anyone looks at the live figures; always, when left out.</param>
        /// <param name="processes">The processes for the energy by app; none worked out, when left out.</param>
        /// <param name="at">When the clock starts; <see cref="Start"/> when left out.</param>
        public Harness(
            TestDatabase database, LoopOptions? options = null, Func<int, FakeSensorSet>? makeSet = null,
            Func<FakeTimeProvider, TimeProvider>? loopClock = null, Func<bool>? watched = null, Func<BatteryCharge?>? battery = null,
            Func<bool>? signedIn = null, Func<DateTimeOffset, DateTimeOffset?>? hibernated = null,
            Func<Sensors.IProcessSource>? processes = null, Action<List<uint>>? foreground = null, DateTimeOffset? at = null,
            Microsoft.Extensions.Logging.ILogger<SamplingLoop>? log = null)
        {
            Clock = new FakeTimeProvider(at ?? Start);
            Clock.SetLocalTimeZone(Plus2);
            Monitors = new MonitorBoard(MonitorBoardTests.Catalogue, Clock);
            makeSet ??= _ => new FakeSensorSet((ts, delta) => Samples.At(ts, delta, batteryW: 20));
            var environment = new LoopEnvironment(
                Sensors: _ =>
                {
                    lock (_sets)
                    {
                        var set = makeSet(_sets.Count);
                        _sets.Add(set);
                        return set;
                    }
                },
                Inventory: () =>
                {
                    Interlocked.Increment(ref _detections);
                    return Facts.Laptop();
                },
                SystemUptime: () => TimeSpan.FromHours(3),
                SystemShuttingDown: () => false,
                DatabaseNotice: null,
                Watched: watched,
                Battery: battery,
                SignedIn: signedIn,
                Hibernated: hibernated,
                Processes: processes,
                Foreground: foreground);
            var clock = loopClock?.Invoke(Clock) ?? Clock;
            _loop = new SamplingLoop(database.Db, environment, Commands, Feed, Board, Monitors, clock, log ?? NullLogger<SamplingLoop>.Instance, options);
        }

        public FakeTimeProvider Clock { get; }

        public MonitorBoard Monitors { get; }

        public LoopCommands Commands { get; } = new();

        public LiveFeed Feed { get; } = new();

        public StatusBoard Board { get; } = new();

        public long TickCount => _loop.Ticks;

        /// <summary>How many times the loop detected the hardware.</summary>
        public int Detections => Volatile.Read(ref _detections);

        public List<FakeSensorSet> Sets
        {
            get
            {
                lock (_sets) return [.. _sets];
            }
        }

        /// <summary>Starts the loop and waits until its timer runs, so the first advance of the clock is a tick.</summary>
        public async Task StartAsync()
        {
            await _loop.StartAsync(CancellationToken.None);
            await _loop.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public Task StopAsync() => _loop.StopAsync(CancellationToken.None);

        /// <summary>Advances the clock a second at a time, waiting for the loop to finish each tick before the next.</summary>
        public async Task Ticks(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var expected = _loop.Ticks + 1;
                Clock.Advance(TimeSpan.FromSeconds(1));
                await WaitFor.True(() => _loop.Ticks >= expected);
            }
        }

        public Task Send(LoopCommand command) => Commands.SendAsync(command).WaitAsync(TimeSpan.FromSeconds(5));

        public async ValueTask DisposeAsync()
        {
            await _loop.StopAsync(CancellationToken.None);
            _loop.Dispose();
        }
    }
}
