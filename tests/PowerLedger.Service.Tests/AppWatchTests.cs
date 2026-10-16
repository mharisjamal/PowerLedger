using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Sensors;
using PowerLedger.Storage;
using Shouldly;

namespace PowerLedger.Service.Tests;

public sealed class AppWatchTests : IDisposable
{
    private static readonly DateTimeOffset Nine = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    private const string ChromePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string CalculatorPath = @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2_x64__8wekyb3d8bbwe\CalculatorApp.exe";
    private const string CalculatorFamily = "Microsoft.WindowsCalculator_8wekyb3d8bbwe";
    private static readonly string Chrome = ChromePath.ToLowerInvariant();
    private static readonly string Calculator = AppKeys.Package(CalculatorFamily);

    private readonly TestDatabase _database = new();
    private readonly FakeProcesses _processes = new();
    private readonly List<uint> _front = [];

    public void Dispose() => _database.Dispose();

    /// <summary>A one-second reading at <paramref name="at"/>: at 3600 W a tick is a watt-hour, so shares read as figures.</summary>
    private static Reading Reading(DateTimeOffset at, double cpuW = 3600, double gpuW = 0, double load = 0.5, double delta = 1)
        => new(at, delta, cpuW + gpuW + 5, Quality.Measured, new Components(cpuW, gpuW, 5, 0, 0, 0, 0, 0, 0, 0),
            OnBattery: false, DisplayOn: true, UserIdle: false, SessionLocked: false, CpuLoad: load, GpuLoad: null, Brightness: 0.5, Suspect: false);

    private static Sample Sample(DateTimeOffset at, double load = 0.5, GraphicsWork? graphics = null, double? igpuW = null, double delta = 1)
        => Samples.At(at, delta, cpuLoad: load) with { Graphics = graphics, IGpuW = igpuW };

    /// <summary>A watch that looks every tick, so each tick is split by its own work.</summary>
    private AppWatch Watch(TimeSpan? lookEvery = null, IEngineCounters? engines = null) => new(_processes, new AppHoursRepository(_database.Db), front =>
    {
        front.Clear();
        front.AddRange(_front);
    }, lookEvery ?? TimeSpan.Zero, engines);

    /// <summary>Engine counters that give the next window from a script, and count their reads.</summary>
    private sealed class FakeEngines(Func<int, GraphicsWork?> script) : IEngineCounters
    {
        public int Reads { get; private set; }

        public GraphicsWork? Read() => script(++Reads);
    }

    private static GraphicsWork OnCard(uint pid, long time) => new(new Dictionary<uint, GraphicsTime> { [pid] = new(time, time, 0, 0) });

    [Fact]
    public void Where_the_sensors_read_no_engine_counters_the_look_reads_them_and_the_cards_watts_follow_its_work()
    {
        AddMachine();
        var engines = new FakeEngines(read => read == 1 ? null : OnCard(300, 100));   // the calculator renders on the card
        using var watch = Watch(engines: engines);
        watch.Start();
        engines.Reads.ShouldBe(1);                                  // where the window starts
        Tick(watch, Nine.AddSeconds(1), cpuW: 0, gpuW: 3600);       // the sample carries no engine work
        watch.Write();
        engines.Reads.ShouldBe(2);
        Stored(Nine)[Calculator].GpuWh.ShouldBe(1, 1e-12);
    }

    [Fact]
    public void Engine_work_the_sensors_already_read_is_used_and_the_counters_are_not_read_again()
    {
        AddMachine();
        var engines = new FakeEngines(_ => OnCard(300, 100));
        using var watch = Watch(engines: engines);
        watch.Start();
        Tick(watch, Nine.AddSeconds(1), cpuW: 0, gpuW: 3600, graphics: OnCard(200, 100));
        watch.Write();
        engines.Reads.ShouldBe(1);                                  // the start's alone
        Stored(Nine)[Chrome].GpuWh.ShouldBe(1, 1e-12);
    }

    [Fact]
    public void Counters_that_cannot_be_read_leave_the_cards_watts_to_follow_the_processor()
    {
        AddMachine();
        var engines = new FakeEngines(read => read == 2 ? throw new InvalidOperationException("gone") : null);
        using var watch = Watch(engines: engines);
        watch.Start();
        Tick(watch, Nine.AddSeconds(1), cpuW: 0, gpuW: 3600);
        Tick(watch, Nine.AddSeconds(2), cpuW: 0, gpuW: 3600);
        watch.Write();
        var rows = Stored(Nine);
        rows[Chrome].GpuWh.ShouldBe(1.2, 1e-12);
        rows[Calculator].GpuWh.ShouldBe(0.4, 1e-12);
    }

    [Fact]
    public void At_a_one_second_pace_the_counters_are_read_with_the_look_at_most_every_five_seconds()
    {
        AddMachine();
        var engines = new FakeEngines(_ => OnCard(300, 100));
        using var watch = Watch(AppWatch.LookEvery, engines);
        watch.Start();
        for (var s = 1; s <= 12; s++) Tick(watch, Nine.AddSeconds(s), cpuW: 0, gpuW: 3600);
        engines.Reads.ShouldBe(3);                                  // the start's, then with the looks at 5 and 10 s
        watch.Write();                                              // a write looks at once, but the counters wait
        engines.Reads.ShouldBe(3);
        Stored(Nine)[Calculator].GpuWh.ShouldBe(12, 1e-12);
    }

    private void Tick(AppWatch watch, DateTimeOffset at, double cpuW = 3600, double gpuW = 0, GraphicsWork? graphics = null, double? igpuW = null,
        double load = 0.5, double gapSeconds = 60)
        => watch.Observe(Reading(at, cpuW, gpuW, load), Sample(at, load, graphics, igpuW), gapSeconds);

    private Dictionary<string, AppHourRow> Stored(DateTimeOffset hour)
        => new AppHoursRepository(_database.Db).ReadHour(hour).ToDictionary(row => row.Key);

    private const string Me = "S-1-5-21-1-2-3-1001";
    private const string Someone = "S-1-5-21-1-2-3-1002";
    private const string Notepad = @"c:\program files\notepad++\notepad++.exe";

    /// <summary>Windows' own (the kernel, a service under the Windows folder, the registry), two of my apps (one a Store
    /// app), someone else's app and a service of PowerLedger's own, run by SYSTEM: 1000 cycles a read in all.</summary>
    private void AddMachine()
    {
        _processes.Add(new FakeProcesses.Fake(4, "System", session: 0), 50, 50);
        _processes.Add(new FakeProcesses.Fake(100, "svchost.exe", session: 0, path: @"C:\WINDOWS\System32\svchost.exe", sid: "S-1-5-18"), 50, 50);
        _processes.Add(new FakeProcesses.Fake(120, "Registry", session: 0));
        _processes.Add(new FakeProcesses.Fake(200, "chrome.exe", session: 1, path: ChromePath, sid: Me), 600, 600);
        _processes.Add(new FakeProcesses.Fake(300, "CalculatorApp.exe", session: 1, path: CalculatorPath, package: CalculatorFamily, sid: Me), 200, 200);
        _processes.Add(new FakeProcesses.Fake(400, "notepad++.exe", session: 2, path: @"C:\Program Files\Notepad++\notepad++.exe", sid: Someone), 100, 100);
        _processes.Add(new FakeProcesses.Fake(500, "PowerLedger.Service.exe", session: 0, path: @"C:\Program Files\PowerLedger\Service\PowerLedger.Service.exe", sid: "S-1-5-18"));
    }

    [Fact]
    public void Each_app_takes_its_cycles_share_of_the_processor_tagged_with_who_ran_it_and_windows_is_one_row()
    {
        AddMachine();
        using var watch = Watch();
        watch.Start();
        Tick(watch, Nine.AddSeconds(1));
        watch.Write();

        var rows = Stored(Nine);
        rows.Keys.ShouldBe([Chrome, Calculator, AppKeys.Windows, Notepad], ignoreOrder: true);   // PowerLedger used nothing
        (rows[Chrome].User, rows[Calculator].User, rows[Notepad].User, rows[AppKeys.Windows].User)
            .ShouldBe((AppUsers.Of(Me), AppUsers.Of(Me), AppUsers.Of(Someone), AppUsers.Machine));   // review items 8 and 10
        rows[Chrome].CpuWh.ShouldBe(0.6, 1e-12);
        (rows[Chrome].Name, rows[Chrome].Path, rows[Chrome].RunSeconds, rows[Chrome].FrontSeconds).ShouldBe(("Name of chrome", ChromePath, 1.0, 0.0));
        rows[Calculator].CpuWh.ShouldBe(0.2, 1e-12);
        (rows[Calculator].Name, rows[Calculator].Path).ShouldBe(("Name of CalculatorApp", CalculatorPath));
        rows[AppKeys.Windows].CpuWh.ShouldBe(0.1, 1e-12);
        rows[Notepad].CpuWh.ShouldBe(0.1, 1e-12);
        rows.Values.Sum(row => row.CpuWh).ShouldBe(1, 1e-12);
    }

    [Fact]
    public void Busy_time_nobody_running_explains_goes_to_closed_programs_and_the_drivers_time_to_windows()
    {
        var chrome = _processes.Add(new FakeProcesses.Fake(200, "chrome.exe", path: ChromePath), 600, 600);
        _processes.ClosedPerRead = 300;
        _processes.DriversPerRead = 100;
        using var watch = Watch();
        watch.Start();
        Tick(watch, Nine.AddSeconds(1));                            // busy 1000: chrome 600, drivers 100, nobody 300

        chrome.Running = false;                                     // chrome ends and a game starts between two looks
        _processes.Add(new FakeProcesses.Fake(200, "game.exe", path: @"D:\Games\game.exe", created: 2), 400, 400);
        Tick(watch, Nine.AddSeconds(2));                            // busy 800: the game 400 since it started
        watch.Write();

        var rows = Stored(Nine);
        rows[Chrome].CpuWh.ShouldBe(0.6, 1e-12);
        rows[@"d:\games\game.exe"].CpuWh.ShouldBe(0.5, 1e-12);
        rows[AppKeys.Windows].CpuWh.ShouldBe(0.1 + 0.125, 1e-12);
        rows[AppKeys.Closed].CpuWh.ShouldBe(0.3 + 0.375, 1e-12);
        rows.Values.Sum(row => row.CpuWh).ShouldBe(2, 1e-12);
    }

    [Fact]
    public void What_the_processor_draws_at_rest_goes_to_idle_once_learned()
    {
        _processes.Add(new FakeProcesses.Fake(200, "chrome.exe", path: ChromePath), 10, 10);
        using var watch = Watch();
        watch.Start();
        for (var s = 1; s <= 120; s++) Tick(watch, Nine.AddSeconds(s), cpuW: 2, load: 0.01);
        watch.Write();
        var quiet = Stored(Nine);

        Tick(watch, Nine.AddSeconds(121), cpuW: 10, load: 0.6);
        watch.Write();
        var rows = Stored(Nine);
        (rows[AppKeys.Idle].CpuWh - quiet[AppKeys.Idle].CpuWh).ShouldBe(2 / 3600.0, 1e-12);
        (rows[Chrome].CpuWh - quiet[Chrome].CpuWh).ShouldBe(8 / 3600.0, 1e-12);
        rows.Values.Sum(row => row.CpuWh).ShouldBe((120 * 2 + 10) / 3600.0, 1e-12);
    }

    [Fact]
    public void A_cards_working_energy_goes_by_time_on_its_engines_or_else_follows_the_processor()
    {
        AddMachine();
        using var watch = Watch();
        watch.Start();
        var graphics = new GraphicsWork(new Dictionary<uint, GraphicsTime>
        {
            [200] = new(CardLoad: 300, CardAny: 300, ProcessorLoad: 0, ProcessorAny: 0),
            [999] = new(CardLoad: 100, CardAny: 100, ProcessorLoad: 0, ProcessorAny: 0),    // a game that has closed since
        });
        Tick(watch, Nine.AddSeconds(1), cpuW: 0, gpuW: 3600, graphics: graphics);
        watch.Write();
        var rows = Stored(Nine);
        rows[Chrome].GpuWh.ShouldBe(0.75, 1e-12);
        rows[AppKeys.Closed].GpuWh.ShouldBe(0.25, 1e-12);

        Tick(watch, Nine.AddSeconds(2), cpuW: 0, gpuW: 3600, graphics: null);   // nothing reads the card's engines
        watch.Write();
        rows = Stored(Nine);
        rows[Chrome].GpuWh.ShouldBe(0.75 + 0.6, 1e-12);
        rows[Calculator].GpuWh.ShouldBe(0.2, 1e-12);
        rows.Values.Sum(row => row.GpuWh).ShouldBe(2, 1e-12);
    }

    [Fact]
    public void The_processors_graphics_on_their_rail_go_by_time_on_their_engines()
    {
        AddMachine();
        using var watch = Watch();
        watch.Start();
        var graphics = new GraphicsWork(new Dictionary<uint, GraphicsTime> { [300] = new(0, 0, ProcessorLoad: 50, ProcessorAny: 80) });
        Tick(watch, Nine.AddSeconds(1), cpuW: 3600, graphics: graphics, igpuW: 900);   // a quarter of the package
        watch.Write();
        var rows = Stored(Nine);
        rows[Calculator].CpuWh.ShouldBe(0.25 + 0.75 * 0.2, 1e-12);
        rows[Chrome].CpuWh.ShouldBe(0.75 * 0.6, 1e-12);
        rows.Values.Sum(row => row.CpuWh).ShouldBe(1, 1e-12);
    }

    [Fact]
    public void The_app_in_front_and_each_app_running_count_their_time_once_a_tick()
    {
        AddMachine();
        _processes.Add(new FakeProcesses.Fake(201, "chrome.exe", path: ChromePath), 0, 0);   // a second chrome process
        using var watch = Watch();
        watch.Start();
        _front.AddRange([200, 400]);                                // my chrome, and someone's notepad++ in their session
        Tick(watch, Nine.AddSeconds(1));
        _front.Clear();
        Tick(watch, Nine.AddSeconds(2));
        watch.Write();
        var rows = Stored(Nine);
        (rows[Chrome].RunSeconds, rows[Chrome].FrontSeconds).ShouldBe((2.0, 1.0));
        (rows[Notepad].RunSeconds, rows[Notepad].FrontSeconds).ShouldBe((2.0, 1.0));
        rows[AppKeys.Windows].RunSeconds.ShouldBe(2);
    }

    [Fact]
    public void A_gap_books_nothing_and_the_next_tick_counts_only_what_ran_since()
    {
        var chrome = _processes.Add(new FakeProcesses.Fake(200, "chrome.exe", path: ChromePath), 600, 600);
        var other = _processes.Add(new FakeProcesses.Fake(300, "game.exe", path: @"D:\Games\game.exe"), 0, 0);
        using var watch = Watch();
        watch.Start();
        Tick(watch, Nine.AddSeconds(1), gapSeconds: 0);             // reaches back across a sleep
        Tick(watch, Nine.AddSeconds(2), gapSeconds: 0.5);           // longer than the gap threshold
        chrome.CyclesPerRead = 0;
        other.CyclesPerRead = 100;
        Tick(watch, Nine.AddSeconds(3));
        watch.Write();
        var rows = Stored(Nine);
        rows.ContainsKey(Chrome).ShouldBeFalse();
        rows[@"d:\games\game.exe"].CpuWh.ShouldBe(1, 1e-12);
        rows[@"d:\games\game.exe"].RunSeconds.ShouldBe(1);
    }

    [Fact]
    public void A_restart_inside_the_hour_carries_on_from_what_the_last_run_wrote()
    {
        AddMachine();
        using (var first = Watch())
        {
            first.Start();
            Tick(first, Nine.AddSeconds(1));
            first.Write();
        }
        using var second = Watch();
        second.Start();
        Tick(second, Nine.AddSeconds(30));
        Tick(second, Nine.AddSeconds(31));
        second.Write();
        var rows = Stored(Nine);
        rows[Chrome].CpuWh.ShouldBe(1.8, 1e-12);
        rows[Chrome].RunSeconds.ShouldBe(3);
        rows.Values.Sum(row => row.CpuWh).ShouldBe(3, 1e-12);
    }

    [Fact]
    public void Each_tick_goes_to_the_hour_of_its_timestamp_and_both_hours_are_written()
    {
        AddMachine();
        using var watch = Watch();
        watch.Start();
        Tick(watch, Nine.AddSeconds(3599));
        Tick(watch, Nine.AddSeconds(3600));
        Tick(watch, Nine.AddSeconds(3601));
        watch.Write();
        Stored(Nine).Values.Sum(row => row.CpuWh).ShouldBe(1, 1e-12);
        Stored(Nine.AddHours(1)).Values.Sum(row => row.CpuWh).ShouldBe(2, 1e-12);
    }

    [Fact]
    public void A_look_windows_will_not_give_books_the_tick_to_idle_at_rest_and_other_apps_beyond()
    {
        AddMachine();
        using var watch = Watch();
        watch.Start();
        _processes.Failing = true;
        Tick(watch, Nine.AddSeconds(1));
        watch.Write();
        var rows = Stored(Nine);
        rows.Keys.ShouldBe([AppKeys.Other]);
        rows[AppKeys.Other].CpuWh.ShouldBe(1, 1e-12);
    }

    [Fact]
    public void At_a_one_second_pace_the_ticks_wait_for_a_look_every_five_seconds_and_a_write_looks_at_once()
    {
        var chrome = _processes.Add(new FakeProcesses.Fake(200, "chrome.exe", path: ChromePath), 600, 600);
        var game = _processes.Add(new FakeProcesses.Fake(300, "game.exe", path: @"D:\Games\game.exe"), 0, 0);
        using var watch = Watch(AppWatch.LookEvery);
        watch.Start();
        for (var s = 1; s <= 4; s++) Tick(watch, Nine.AddSeconds(s));
        watch.Looks.ShouldBe(1);

        chrome.CyclesPerRead = 0;                                   // what ran up to the fifth tick's look: the game alone
        game.CyclesPerRead = 600;
        Tick(watch, Nine.AddSeconds(5));
        watch.Looks.ShouldBe(2);
        Tick(watch, Nine.AddSeconds(6));
        watch.Looks.ShouldBe(2);
        watch.Write();
        watch.Looks.ShouldBe(3);

        var rows = Stored(Nine);
        rows.Keys.ShouldBe([@"d:\games\game.exe"]);
        rows[@"d:\games\game.exe"].CpuWh.ShouldBe(6, 1e-12);       // all six ticks, split by the work of their own time
        rows[@"d:\games\game.exe"].RunSeconds.ShouldBe(6);
    }

    [Fact]
    public void A_process_is_opened_once_for_its_image_however_long_it_runs()
    {
        AddMachine();
        using var watch = Watch();
        watch.Start();
        var opens = _processes.Opens;
        for (var s = 1; s <= 10; s++) Tick(watch, Nine.AddSeconds(s));
        _processes.Opens.ShouldBe(opens);
    }
}
