using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>The battery's state and a switched-off card's power state, asked of Windows only as often as they can change.</summary>
public class StateCachingTests
{
    private readonly FakeSignals _signals = new();
    private long _now = 1_000_000;
    private int _batteryReads;
    private Win32.BatteryState _battery = new(AcOnLine: false, Present: true, Charging: false, Discharging: true, RateMilliwatts: -20_000);

    [Fact]
    public void The_battery_is_read_every_five_seconds_not_every_tick()
    {
        var source = Battery();
        _batteryReads.ShouldBe(1);                           // once, to say whether the machine has one

        for (var second = 0; second <= 10; second++)
        {
            Tick(source);
            _now += 1_000;
        }

        _batteryReads.ShouldBe(3);                           // at 5 s and 10 s after the first
    }

    [Fact]
    public void Between_reads_every_tick_carries_the_last_state()
    {
        var source = Battery();
        _battery = _battery with { RateMilliwatts = -30_000 };
        _now += 1_000;

        Tick(source).BatteryRateW.ShouldBe(20.0);

        _now += 4_000;
        Tick(source).BatteryRateW.ShouldBe(30.0);
    }

    [Fact]
    public void Unplugging_is_read_on_the_next_tick_once_windows_announces_it()
    {
        var source = Battery();
        _battery = _battery with { AcOnLine = true, RateMilliwatts = 15_000 };
        _now += 1_000;
        Tick(source).OnBattery.ShouldBeTrue();               // nothing announced yet

        _signals.BatteryChanges++;
        _now += 1_000;
        var draft = Tick(source);

        draft.OnBattery.ShouldBeFalse();
        draft.BatteryChargeW.ShouldBe(15.0);
        _batteryReads.ShouldBe(2);
    }

    [Fact]
    public void A_battery_windows_would_not_describe_is_asked_again_on_the_next_tick()
    {
        var answers = new Queue<Win32.BatteryState?>([_battery, null, _battery with { RateMilliwatts = -9_000 }]);
        var source = new BatterySource(() => answers.Dequeue(), relativeUnits: false, () => _now, _signals);
        _now += 5_000;
        Tick(source).BatteryRateW.ShouldBe(20.0);            // the last good state, when Windows declines

        _now += 1_000;
        Tick(source).BatteryRateW.ShouldBe(9.0);
    }

    [Fact]
    public void Whether_the_batteries_are_short_term_is_asked_once_however_often_the_battery_is_read()
    {
        var asked = 0;
        var read = BatterySource.ShortTermAskedOnce(() => { asked++; return true; }, shortTerm => _battery with { ShortTerm = shortTerm });

        for (var tick = 0; tick < 10; tick++) read().ShouldNotBeNull().ShortTerm.ShouldBeTrue();

        asked.ShouldBe(1);
    }

    [Fact]
    public void A_card_s_device_node_is_located_once()
    {
        var nodes = new FakeNodes { State = Off };
        var watch = Watch(nodes);

        for (var tick = 0; tick < 20; tick++)
        {
            watch.State();
            _now += 1_000;
        }

        nodes.Finds.ShouldBe(1);
        nodes.Locates.ShouldBe(1);
    }

    [Fact]
    public void A_switched_off_card_is_asked_again_only_every_few_seconds()
    {
        var nodes = new FakeNodes { State = Off };
        var watch = Watch(nodes);

        for (var tick = 0; tick < 9; tick++)
        {
            watch.State().ShouldBe(DevicePower.Off);
            _now += 1_000;
        }

        nodes.Asks.ShouldBe(3);                              // at 0 s, 3 s and 6 s
    }

    [Fact]
    public void A_card_that_is_on_is_asked_every_tick_since_its_library_is_asked_next_and_must_never_wake_it()
    {
        var nodes = new FakeNodes { State = 1 };
        var watch = Watch(nodes);

        for (var tick = 0; tick < 5; tick++) watch.State().ShouldBe(DevicePower.On);

        nodes.Asks.ShouldBe(5);
    }

    [Fact]
    public void A_card_windows_can_t_find_is_unknown_and_looked_for_again_only_once_a_minute()
    {
        var nodes = new FakeNodes { Id = null };
        var watch = Watch(nodes);

        for (var second = 0; second < 120; second++)
        {
            watch.State().ShouldBe(DevicePower.Unknown);
            _now += 1_000;
        }

        nodes.Finds.ShouldBe(2);
    }

    [Fact]
    public void A_node_that_stops_answering_is_located_afresh_a_minute_later()
    {
        var nodes = new FakeNodes { State = 1 };
        var watch = Watch(nodes);
        watch.State().ShouldBe(DevicePower.On);

        nodes.State = null;                                  // the driver was reinstalled and the old node went with it
        watch.State().ShouldBe(DevicePower.Unknown);
        nodes.State = 1;
        _now += 60_000;
        watch.State().ShouldBe(DevicePower.On);

        nodes.Locates.ShouldBe(2);
    }

    [Fact]
    public void A_card_whose_power_state_is_unknown_is_read_every_ten_seconds_not_every_tick()
    {
        var reads = 0;
        var source = new NvidiaSource(
            [new NvidiaCard("A", 1, null, () => new GpuReading(true, 10 + reads++, 0.5), Power: () => DevicePower.Unknown)],
            present: true, unavailable: null, loadsEveryCard: true, clockMilliseconds: () => _now);

        var watts = new List<double?>();
        for (var second = 0; second < 25; second++)
        {
            var draft = new SampleDraft();
            source.Contribute(draft);
            watts.Add(draft.DGpuW);
            _now += 1_000;
        }

        reads.ShouldBe(3);                                   // at 0 s, 10 s and 20 s
        watts.ShouldAllBe(value => value != null);           // the last reading carries the ticks between
        watts[9].ShouldBe(10);
        watts[10].ShouldBe(11);
    }

    [Fact]
    public void A_card_that_is_on_is_read_every_tick()
    {
        var reads = 0;
        var source = new NvidiaSource(
            [new NvidiaCard("A", 1, null, () => { reads++; return new GpuReading(true, 50, 0.5); }, Power: () => DevicePower.On)],
            present: true, unavailable: null, loadsEveryCard: true, clockMilliseconds: () => _now);

        for (var tick = 0; tick < 5; tick++) source.Contribute(new SampleDraft());

        reads.ShouldBe(5);
    }

    private const int Off = 4;

    private DevicePowerWatch Watch(FakeNodes nodes) => new(nodes.Find, nodes, () => _now);

    private BatterySource Battery() => new(() => { _batteryReads++; return _battery; }, relativeUnits: false, () => _now, _signals);

    private static SampleDraft Tick(ISensorSource source)
    {
        var draft = new SampleDraft();
        source.Contribute(draft);
        return draft;
    }

    private sealed class FakeSignals : ISystemSignals
    {
        public int BatteryChanges { get; set; }
        public int MonitorChanges { get; set; }
        public int BrightnessChanges { get; set; }
        public int? Brightness { get; set; }
    }

    private sealed class FakeNodes : IDeviceNodes
    {
        public string? Id { get; set; } = @"PCI\VEN_10DE&DEV_1C8D\4&1";
        public int? State { get; set; }
        public int Finds { get; private set; }
        public int Locates { get; private set; }
        public int Asks { get; private set; }

        public string? Find()
        {
            Finds++;
            return Id;
        }

        public uint? Locate(string instanceId)
        {
            Locates++;
            return 7;
        }

        public int? PowerState(uint node)
        {
            Asks++;
            return State;
        }
    }
}
