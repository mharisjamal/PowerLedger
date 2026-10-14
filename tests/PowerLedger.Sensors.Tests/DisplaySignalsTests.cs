using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Sensors.Tests;

/// <summary>The display source asks WMI when Windows says something changed, and only rarely otherwise.</summary>
public class DisplaySignalsTests
{
    private readonly Signals _signals = new();
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddDays(1000);
    private bool _displayOn = true;
    private int _queries;
    private DisplayState _wmi = new(Brightness: 0.5, MonitorCount: 1);

    [Fact]
    public void Wmi_is_asked_once_a_quarter_of_an_hour_when_nothing_is_announced()
    {
        var source = Source();

        for (var minute = 0; minute <= 30; minute++)
        {
            source.Contribute(new SampleDraft());
            _now = _now.AddMinutes(1);
        }

        DisplaySource.SafetyPoll.ShouldBe(TimeSpan.FromMinutes(15));
        _queries.ShouldBe(3);                                // at 0, 15 and 30 minutes
    }

    [Fact]
    public void A_brightness_windows_announces_is_taken_at_once_without_asking_wmi()
    {
        var source = Source();
        source.Contribute(new SampleDraft());

        _signals.Announce(brightness: 30);
        _now = _now.AddSeconds(1);
        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.Brightness.ShouldBe(0.3);
        _queries.ShouldBe(1);
    }

    [Fact]
    public void A_machine_whose_wmi_gives_no_brightness_is_given_none_by_an_announcement_either()
    {
        _wmi = new DisplayState(null, 1);
        var source = Source();
        source.Contribute(new SampleDraft());

        _signals.Announce(brightness: 100);
        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.Brightness.ShouldBeNull();
    }

    [Fact]
    public void A_monitor_arriving_or_leaving_is_read_on_the_next_tick_and_again_once_it_has_settled()
    {
        var source = Source();
        source.Contribute(new SampleDraft());

        _wmi = new DisplayState(0.5, 2);
        _signals.MonitorChanges++;
        _now = _now.AddSeconds(1);
        var draft = new SampleDraft();
        source.Contribute(draft);

        draft.MonitorCount.ShouldBe(2);
        _queries.ShouldBe(2);

        for (var second = 0; second < 15; second++)
        {
            _now = _now.AddSeconds(1);
            source.Contribute(new SampleDraft());
        }

        _queries.ShouldBe(3);                                // once more, 10 s on, for a monitor Windows was still setting up
    }

    [Fact]
    public void The_display_coming_back_on_reads_the_monitors_again()
    {
        var source = Source();
        source.Contribute(new SampleDraft());
        _displayOn = false;
        _now = _now.AddSeconds(1);
        source.Contribute(new SampleDraft());
        _queries.ShouldBe(1);

        _displayOn = true;
        _now = _now.AddSeconds(1);
        source.Contribute(new SampleDraft());

        _queries.ShouldBe(2);
    }

    private DisplaySource Source() => new(
        () => { _queries++; return _wmi; }, () => _displayOn, signals: _signals, clock: () => _now);

    private sealed class Signals : ISystemSignals
    {
        public int BatteryChanges { get; set; }
        public int MonitorChanges { get; set; }
        public int BrightnessChanges { get; set; }
        public int? Brightness { get; set; }

        public void Announce(int brightness)
        {
            Brightness = brightness;
            BrightnessChanges++;
        }
    }
}
