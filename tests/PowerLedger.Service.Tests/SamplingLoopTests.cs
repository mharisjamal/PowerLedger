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

        loop.Sets.Count.ShouldBe(2);                                // a fresh sensor set after the resume
        await WaitFor.True(() => loop.Sets.All(s => s.Disposed));

        new SettingsRepository(t.Db).Get(RetentionRunner.LastPurgeKey)
            .ShouldBe(Start.AddMinutes(5).ToString("O", CultureInfo.InvariantCulture));   // the 03:00 local purge
        var learned = new CalibrationLearner();
        learned.Import(new CalibrationRepository(t.Db).Load(Facts.Laptop().Hash));
        learned.TotalSamples.ShouldBe(480);

        var status = loop.Board.Status.ShouldNotBeNull();
        status.Ticks.ShouldBe(480);
        status.InventoryHash.ShouldBe(Facts.Laptop().Hash);
        status.Sources.Single().Name.ShouldBe("fake");
        ReadingFrame? last = null;
        while (frames.TryRead(out var frame)) last = frame;
        last.ShouldNotBeNull().Timestamp.ShouldBe(Start.AddMinutes(10));
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

    [Fact]
    public async Task A_hung_sensor_read_costs_one_tick_and_the_stall_becomes_a_gap()
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
        last.DeltaSeconds.ShouldBe(11.5, 1e-6);                      // from the last good tick, so it counts as a gap
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
        monitor.WattsNow.ShouldBe(28.32, 1e-9);
        status.Last.ShouldNotBeNull().Components.Monitors.ShouldBe(28.32, 1e-9);
        status.Last.TotalW.ShouldBe(20 + 28.32, 1e-9);                   // on top of the battery's measured 20 W
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
    public async Task Monitors_old_settings_had_but_did_not_count_stay_uncounted()
    {
        using var t = new TestDatabase();
        var store = new SettingsStore(new SettingsRepository(t.Db));
        store.Save(ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { ExternalMonitors = 2 } });
        store.SaveProfileHash(Facts.Laptop().Hash);
        await using var loop = new Harness(t);
        loop.Monitors.Detected([MonitorBoardTests.Dell]);
        await loop.StartAsync();

        await loop.Ticks(2);

        var profile = store.Load().ShouldNotBeNull().Profile;
        profile.Monitors.ShouldBe([new MonitorChoice { Key = MonitorBoardTests.Dell.Key, Counted = false }]);
        profile.ExternalMonitors.ShouldBe(0);
        var status = loop.Board.Status.ShouldNotBeNull();
        status.Monitors.ShouldNotBeNull().ShouldHaveSingleItem().Counted.ShouldBeFalse();
        status.Last.ShouldNotBeNull().Components.Monitors.ShouldBe(0);
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
        loop.Board.Status.ShouldNotBeNull().Last.ShouldNotBeNull().Components.Monitors.ShouldBe(28.32, 1e-9);
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

        /// <param name="loopClock">What the loop sees of <see cref="Clock"/>, when that is not the fake clock itself.</param>
        public Harness(
            TestDatabase database, LoopOptions? options = null, Func<int, FakeSensorSet>? makeSet = null,
            Func<FakeTimeProvider, TimeProvider>? loopClock = null)
        {
            Clock.SetLocalTimeZone(Plus2);
            Monitors = new MonitorBoard(MonitorBoardTests.Catalogue, Clock);
            makeSet ??= _ => new FakeSensorSet((ts, delta) => Samples.At(ts, delta, batteryW: 20));
            var environment = new LoopEnvironment(
                Sensors: () =>
                {
                    lock (_sets)
                    {
                        var set = makeSet(_sets.Count);
                        _sets.Add(set);
                        return set;
                    }
                },
                Inventory: () => Facts.Laptop(),
                SystemUptime: () => TimeSpan.FromHours(3),
                SystemShuttingDown: () => false,
                DatabaseNotice: null);
            var clock = loopClock?.Invoke(Clock) ?? Clock;
            _loop = new SamplingLoop(database.Db, environment, Commands, Feed, Board, Monitors, clock, NullLogger<SamplingLoop>.Instance, options);
        }

        public FakeTimeProvider Clock { get; } = new(Start);

        public MonitorBoard Monitors { get; }

        public LoopCommands Commands { get; } = new();

        public LiveFeed Feed { get; } = new();

        public StatusBoard Board { get; } = new();

        public long TickCount => _loop.Ticks;

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
