using Microsoft.Win32;
using Shouldly;
using Xunit.Abstractions;

namespace PowerLedger.App.Tests;

public class DdcBrightnessTests(ITestOutputHelper output)
{
    private const string Dell = @"\\?\DISPLAY#DELA0B1#5&2f5a1b&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Lg = @"\\?\DISPLAY#GSM5B09#5&2f5a1b&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Panel = @"\\?\DISPLAY#BOE0A1C#4&1a2b3c4d&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    /// <summary>How long a monitor a test holds waits to be let go before it gives up and answers anyway.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeWindows _windows = new();
    private readonly FakeSystemEvents _events = new();

    private DdcBrightness Reader(params FakeDisplay[] displays)
    {
        _windows.Screens.AddRange(displays);
        return new DdcBrightness(_windows, gap => _windows.Log.Add($"pause {gap.TotalMilliseconds:0} ms"), _events);
    }

    [Theory]
    [InlineData(0u, 50u, 100u, 0.5)]
    [InlineData(0u, 0u, 100u, 0.0)]
    [InlineData(0u, 100u, 100u, 1.0)]
    [InlineData(10u, 55u, 100u, 0.5)]                       // a minimum above zero
    [InlineData(0u, 30u, 60u, 0.5)]                         // a maximum other than 100
    [InlineData(20u, 5u, 100u, 0.0)]                        // below the minimum
    [InlineData(0u, 150u, 100u, 1.0)]                       // above the maximum
    [InlineData(0u, uint.MaxValue, uint.MaxValue, 1.0)]     // the top of the range doesn't overflow
    public void Brightness_is_where_the_setting_sits_between_its_minimum_and_maximum(uint minimum, uint current, uint maximum, double brightness)
        => DdcBrightness.Normalise(minimum, current, maximum).ShouldBe(brightness);

    [Theory]
    [InlineData(50u, 50u, 50u)]     // no range at all
    [InlineData(0u, 0u, 0u)]
    [InlineData(100u, 50u, 0u)]     // a maximum below the minimum
    public void A_setting_without_a_range_is_no_brightness(uint minimum, uint current, uint maximum)
        => DdcBrightness.Normalise(minimum, current, maximum).ShouldBeNull();

    [Fact]
    public void A_monitor_that_reports_brightness_is_read_under_its_device_path()
        => Reader(new FakeDisplay(new FakeMonitor(Dell) { Brightness = (0, 60, 100) })).Read().ShouldBe([new DdcReading(Dell, 0.6)]);

    [Fact]
    public void Each_monitor_is_asked_what_it_supports_once_then_read_every_time()
    {
        var dell = new FakeMonitor(Dell);
        var reader = Reader(new FakeDisplay(dell));

        for (var read = 0; read < 3; read++) reader.Read().ShouldBe([new DdcReading(Dell, 0.6)]);

        dell.CapabilityCalls.ShouldBe(1);
        dell.BrightnessCalls.ShouldBe(3);
    }

    [Theory]
    [InlineData(0x0u)]              // MC_CAPS_NONE
    [InlineData(0x4u)]              // contrast, but not brightness
    [InlineData(0xFFFFFFFDu)]       // everything but brightness
    public void A_monitor_that_doesnt_support_brightness_is_never_read_nor_asked_again(uint capabilities)
    {
        var dell = new FakeMonitor(Dell) { Capabilities = capabilities };
        var reader = Reader(new FakeDisplay(dell));

        reader.Read().ShouldBeEmpty();
        reader.Read().ShouldBeEmpty();
        reader.Read().ShouldBeEmpty();   // an answer isn't a failure, and no event fired, so nothing makes it worth asking again

        dell.CapabilityCalls.ShouldBe(1);
        dell.BrightnessCalls.ShouldBe(0);
        _windows.Log.ShouldBe(["capabilities " + Dell]);
    }

    [Theory]
    [InlineData(0x0u)]
    [InlineData(0x4u)]
    public void A_capabilities_answer_of_no_brightness_is_remembered_until_an_event_clears_it(uint capabilities)
    {
        var dell = new FakeMonitor(Dell) { Capabilities = capabilities };
        var reader = Reader(new FakeDisplay(dell));

        reader.Read().ShouldBeEmpty();
        dell.CapabilityCalls.ShouldBe(1);

        dell.Capabilities = 0x2 | 0x4;   // even if the monitor would now answer differently
        reader.Read().ShouldBeEmpty();   // the old answer stands, so it isn't asked to find out
        dell.CapabilityCalls.ShouldBe(1);

        _events.RaiseDisplaySettingsChanged();
        reader.Read().ShouldBe([new DdcReading(Dell, 0.6)]);
        dell.CapabilityCalls.ShouldBe(2);
    }

    [Theory]
    [InlineData("capabilities fail")]
    [InlineData("capabilities throw")]
    [InlineData("brightness fails")]
    [InlineData("brightness throws")]
    public void A_monitor_that_fails_any_call_is_not_asked_again_while_the_app_runs(string failure)
    {
        var dell = Failing(new FakeMonitor(Dell), failure);
        var reader = Reader(new FakeDisplay(dell), new FakeDisplay(new FakeMonitor(Lg)));

        reader.Read().ShouldBe([new DdcReading(Lg, 0.6)]);
        var asked = dell.Calls;
        asked.ShouldBeGreaterThan(0);

        // Many more reads stand in for the App running a long time: DdcBrightness keeps no clock, so nothing about
        // elapsed time can make it ask this monitor again - only a display change or a resume can.
        for (var read = 0; read < 20; read++) reader.Read().ShouldBe([new DdcReading(Lg, 0.6)]);

        dell.Calls.ShouldBe(asked);
    }

    [Theory]
    [InlineData(true)]     // a display change: a monitor plugged in or out, or display settings changed
    [InlineData(false)]    // a resume from sleep
    public void A_display_change_or_a_resume_clears_a_failure_and_the_monitor_is_asked_again(bool displayChange)
    {
        var dell = Failing(new FakeMonitor(Dell), "capabilities fail");
        var reader = Reader(new FakeDisplay(dell));

        reader.Read().ShouldBeEmpty();
        var askedWhatItSupports = dell.CapabilityCalls;

        Answering(dell);   // it was only asleep, or only just plugged back in
        if (displayChange) _events.RaiseDisplaySettingsChanged();
        else _events.RaisePowerModeChanged(PowerModes.Resume);
        reader.Read().ShouldBe([new DdcReading(Dell, 0.6)]);

        dell.CapabilityCalls.ShouldBe(askedWhatItSupports + 1);
    }

    [Theory]
    [InlineData(PowerModes.Suspend)]
    [InlineData(PowerModes.StatusChange)]
    public void A_power_mode_change_that_isnt_a_resume_does_not_clear_a_failure(PowerModes mode)
    {
        var dell = Failing(new FakeMonitor(Dell), "capabilities fail");
        var reader = Reader(new FakeDisplay(dell));

        reader.Read().ShouldBeEmpty();
        Answering(dell);
        _events.RaisePowerModeChanged(mode);
        reader.Read().ShouldBeEmpty();

        dell.CapabilityCalls.ShouldBe(1);
    }

    [Fact]
    public void A_monitor_that_fails_again_after_being_cleared_is_left_alone_until_the_next_event()
    {
        var dell = new FakeMonitor(Dell) { Capabilities = null };
        var reader = Reader(new FakeDisplay(dell));

        reader.Read().ShouldBeEmpty();
        _events.RaiseDisplaySettingsChanged();
        reader.Read().ShouldBeEmpty();   // asked again, but still fails

        dell.CapabilityCalls.ShouldBe(2);

        reader.Read().ShouldBeEmpty();   // not asked a third time without another event
        dell.CapabilityCalls.ShouldBe(2);

        Answering(dell);
        _events.RaisePowerModeChanged(PowerModes.Resume);
        reader.Read().ShouldBe([new DdcReading(Dell, 0.6)]);
        dell.CapabilityCalls.ShouldBe(3);
    }

    [Fact]
    public void Disposing_unsubscribes_from_the_events()
    {
        var dell = Failing(new FakeMonitor(Dell), "capabilities fail");
        var reader = Reader(new FakeDisplay(dell));
        reader.Read().ShouldBeEmpty();
        _events.HasSubscribers.ShouldBeTrue();

        reader.Dispose();
        _events.HasSubscribers.ShouldBeFalse();

        Answering(dell);
        _events.RaiseDisplaySettingsChanged();     // nothing is listening any more
        _events.RaisePowerModeChanged(PowerModes.Resume);
        reader.Read().ShouldBeEmpty();
        dell.CapabilityCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_display_change_while_a_read_waits_on_a_monitor_returns_at_once_and_the_next_read_asks_afresh()
    {
        var dell = Failing(new FakeMonitor(Dell), "capabilities fail");
        var lg = new FakeMonitor(Lg);
        var reader = Reader(new FakeDisplay(dell), new FakeDisplay(lg));
        reader.Read().ShouldBe([new DdcReading(Lg, 0.6)]);   // the Dell is left alone from here on
        Answering(dell);

        using var answer = new ManualResetEventSlim();
        var read = ReadHeldBy(lg, reader, answer);
        // SystemEvents raises the event on the thread that subscribed, which in the App is the UI thread; here it is this
        // one. A reset that waited for the read would still be waiting when the monitor gave up.
        _events.RaiseDisplaySettingsChanged();
        answer.Set();

        var (readings, letGo) = await read;
        letGo.ShouldBeTrue();
        readings.ShouldBe([new DdcReading(Lg, 0.6)]);   // the change came after the Dell's turn in that read
        reader.Read().ShouldBe([new DdcReading(Dell, 0.6), new DdcReading(Lg, 0.6)]);
        dell.CapabilityCalls.ShouldBe(2);
    }

    [Fact]
    public async Task A_resume_while_a_read_waits_on_a_monitor_returns_at_once_and_is_seen_before_the_next_monitor_is_asked()
    {
        // Two monitors showing one picture share a display, so the Dell is asked with the handles the LG's read opened.
        var lg = new FakeMonitor(Lg);
        var dell = Failing(new FakeMonitor(Dell), "capabilities fail");
        var reader = Reader(new FakeDisplay(lg, dell));
        reader.Read().ShouldBe([new DdcReading(Lg, 0.6)]);
        Answering(dell);

        using var answer = new ManualResetEventSlim();
        var read = ReadHeldBy(lg, reader, answer);
        _events.RaisePowerModeChanged(PowerModes.Resume);
        answer.Set();

        var (readings, letGo) = await read;
        letGo.ShouldBeTrue();
        readings.ShouldBe([new DdcReading(Lg, 0.6), new DdcReading(Dell, 0.6)]);   // the Dell's turn came after the resume
        dell.CapabilityCalls.ShouldBe(2);
    }

    [Fact]
    public void A_laptop_panel_without_DDC_CI_fails_quietly_and_its_display_isnt_opened_again_while_the_app_runs()
    {
        var panel = new FakeMonitor(Panel) { Capabilities = null };
        var laptop = new FakeDisplay(panel);
        var reader = Reader(laptop, new FakeDisplay(new FakeMonitor(Dell)));

        reader.Read().ShouldBe([new DdcReading(Dell, 0.6)]);
        reader.Read().ShouldBe([new DdcReading(Dell, 0.6)]);

        panel.CapabilityCalls.ShouldBe(1);
        panel.BrightnessCalls.ShouldBe(0);
        laptop.Opens.ShouldBe(1);

        reader.Read().ShouldBe([new DdcReading(Dell, 0.6)]);
        panel.CapabilityCalls.ShouldBe(1);
        laptop.Opens.ShouldBe(1);
    }

    [Fact]
    public void A_monitor_is_given_50_ms_between_saying_what_it_supports_and_being_read()
    {
        var reader = Reader(new FakeDisplay(new FakeMonitor(Dell)));

        reader.Read();
        reader.Read();

        DdcBrightness.RequestGap.ShouldBe(TimeSpan.FromMilliseconds(50));
        _windows.Log.ShouldBe(["capabilities " + Dell, "pause 50 ms", "brightness " + Dell, "brightness " + Dell]);
    }

    [Fact]
    public void A_monitor_that_fails_to_say_what_it_supports_is_not_paused_for()
    {
        var reader = Reader(new FakeDisplay(new FakeMonitor(Dell) { Capabilities = null }));

        reader.Read();

        _windows.Log.ShouldBe(["capabilities " + Dell]);
    }

    [Fact]
    public void A_monitor_left_alone_is_known_by_its_device_path_whatever_handle_it_gets_next()
    {
        // Two monitors showing one picture share a display, which is opened at every read, with new handles each time.
        var dell = new FakeMonitor(Dell) { CapabilitiesThrows = new InvalidOperationException("The monitor didn't answer.") };
        var display = new FakeDisplay(dell, new FakeMonitor(Lg));
        var reader = Reader(display);

        reader.Read().ShouldBe([new DdcReading(Lg, 0.6)]);
        reader.Read().ShouldBe([new DdcReading(Lg, 0.6)]);

        display.Opens.ShouldBe(2);
        dell.CapabilityCalls.ShouldBe(1);
    }

    [Fact]
    public void A_monitor_that_reports_no_range_gives_no_reading_and_is_read_again_next_time()
    {
        var dell = new FakeMonitor(Dell) { Brightness = (50, 50, 50) };
        var reader = Reader(new FakeDisplay(dell));

        reader.Read().ShouldBeEmpty();
        dell.Brightness = (0, 70, 100);
        reader.Read().ShouldBe([new DdcReading(Dell, 0.7)]);
    }

    [Fact]
    public void Handles_are_destroyed_after_a_call_throws()
    {
        var reader = Reader(new FakeDisplay(new FakeMonitor(Dell) { BrightnessThrows = new InvalidOperationException("The monitor didn't answer.") }, new FakeMonitor(Lg)));

        reader.Read().ShouldBe([new DdcReading(Lg, 0.6)]);

        _windows.Opened.Count.ShouldBe(1);
        _windows.Closed.ShouldBe(_windows.Opened);
        _windows.OpenHandles.ShouldBe(0);
    }

    [Fact]
    public void Handles_are_destroyed_even_when_an_exception_escapes_the_read()
    {
        var reader = Reader(new FakeDisplay(new FakeMonitor(Dell) { CapabilitiesThrows = new OutOfMemoryException() }));

        Should.Throw<OutOfMemoryException>(() => reader.Read());

        _windows.Opened.Count.ShouldBe(1);
        _windows.Closed.ShouldBe(_windows.Opened);
        _windows.OpenHandles.ShouldBe(0);
    }

    [Fact]
    public void Monitors_sharing_a_display_are_matched_with_its_handles_in_order()
        => Reader(new FakeDisplay(new FakeMonitor(Dell) { Brightness = (0, 60, 100) }, new FakeMonitor(Lg) { Brightness = (0, 30, 100) }))
            .Read().ShouldBe([new DdcReading(Dell, 0.6), new DdcReading(Lg, 0.3)]);

    [Fact]
    public void A_display_with_more_monitors_than_handles_is_not_asked_and_is_tried_again_next_time()
    {
        var dell = new FakeMonitor(Dell);
        var lg = new FakeMonitor(Lg);
        var display = new FakeDisplay(dell, lg) { Physical = [dell] };
        var reader = Reader(display);

        reader.Read().ShouldBeEmpty();
        (dell.Calls + lg.Calls).ShouldBe(0);
        _windows.OpenHandles.ShouldBe(0);

        display.Physical = null;   // Windows agrees with itself again
        reader.Read().ShouldBe([new DdcReading(Dell, 0.6), new DdcReading(Lg, 0.6)]);
    }

    [Fact]
    public void A_display_whose_handles_describe_its_monitors_in_another_order_is_not_asked()
    {
        var dell = new FakeMonitor(Dell, "DELL U2723QE");
        var lg = new FakeMonitor(Lg, "LG HDR 4K");
        var reader = Reader(new FakeDisplay(dell, lg) { Physical = [lg, dell] });

        reader.Read().ShouldBeEmpty();

        (dell.Calls + lg.Calls).ShouldBe(0);
        _windows.OpenHandles.ShouldBe(0);
    }

    [Fact]
    public void A_display_with_no_physical_monitors_is_not_asked()
    {
        var dell = new FakeMonitor(Dell);
        var reader = Reader(new FakeDisplay(dell) { Physical = [] });

        reader.Read().ShouldBeEmpty();

        dell.Calls.ShouldBe(0);
        _windows.OpenHandles.ShouldBe(0);
    }

    [Fact]
    public void A_display_Windows_wont_open_is_not_asked_and_is_tried_again_next_time()
    {
        var display = new FakeDisplay(new FakeMonitor(Dell)) { CanOpen = false };
        var reader = Reader(display);

        reader.Read().ShouldBeEmpty();
        display.CanOpen = true;
        reader.Read().ShouldBe([new DdcReading(Dell, 0.6)]);
    }

    [Fact]
    public void A_monitor_Windows_doesnt_present_as_on_takes_no_handle()
        => Reader(new FakeDisplay(new FakeMonitor(Lg) { Active = false }, new FakeMonitor(Dell))).Read().ShouldBe([new DdcReading(Dell, 0.6)]);

    [Fact]
    public void A_monitor_whose_path_isnt_a_display_is_not_asked_but_keeps_its_place()
    {
        var odd = new FakeMonitor(@"MONITOR\DELA0B1\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001");
        var reader = Reader(new FakeDisplay(odd, new FakeMonitor(Dell) { Brightness = (0, 40, 100) }));

        reader.Read().ShouldBe([new DdcReading(Dell, 0.4)]);

        odd.Calls.ShouldBe(0);
    }

    [Fact]
    public void A_display_Windows_wont_list_doesnt_stop_the_others()
        => Reader(new FakeDisplay(new FakeMonitor(Lg)) { ListThrows = new InvalidOperationException("The display went away.") }, new FakeDisplay(new FakeMonitor(Dell)))
            .Read().ShouldBe([new DdcReading(Dell, 0.6)]);

    [Fact]
    public void When_Windows_wont_list_its_displays_nothing_is_read()
    {
        _windows.ListingThrows = new InvalidOperationException("No desktop.");

        Reader(new FakeDisplay(new FakeMonitor(Dell))).Read().ShouldBeEmpty();
    }

    /// <summary>Asks this machine's own monitors, read-only. A laptop's panel has no DDC/CI, so on its own it gives nothing.</summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public void This_machines_monitors_are_read_without_throwing_and_every_brightness_is_between_0_and_1()
    {
        using var reader = new DdcBrightness();

        var readings = reader.Read().Concat(reader.Read()).ToList();   // the second read asks only the monitors that answered the first

        foreach (var reading in readings) output.WriteLine($"{reading.DevicePath}: {reading.Brightness:0.###}");
        output.WriteLine($"{readings.Count} reading(s) in two reads");
        readings.ShouldAllBe(reading => reading.Brightness >= 0 && reading.Brightness <= 1);
    }

    /// <summary>The Windows calls under the reader, short of DDC/CI: this machine has a screen whose monitor Windows names by
    /// its device path, and opens a handle for it. A struct laid out wrong gives no path or no description. Dxva2's handles
    /// are small numbers, and 0 is a good one.</summary>
    [Fact]
    [Trait("Category", "Hardware")]
    public void Windows_names_this_machines_monitors_and_opens_their_handles()
    {
        var windows = new WindowsMonitorCalls();
        var named = 0;
        foreach (var display in windows.Displays())
        {
            var attached = windows.Attached(display);
            foreach (var monitor in attached) output.WriteLine($"display {display}: {(monitor.Active ? "active" : "inactive")} \"{monitor.Description}\" {monitor.DevicePath}");
            named += attached.Count(monitor => monitor.Active && monitor.DevicePath.StartsWith(@"\\?\DISPLAY#", StringComparison.OrdinalIgnoreCase));
            if (windows.Open(display) is not { } physical)
            {
                output.WriteLine($"display {display}: Windows opened no physical monitors");
                continue;
            }
            try
            {
                foreach (var monitor in physical) output.WriteLine($"display {display}: handle {monitor.Handle} for \"{monitor.Description}\"");
                physical.ShouldAllBe(monitor => monitor.Description.Length > 0);
                physical.Select(monitor => monitor.Handle).Distinct().Count().ShouldBe(physical.Count);
            }
            finally
            {
                windows.Close(physical);
            }
        }
        named.ShouldBeGreaterThan(0);
    }

    private static FakeMonitor Failing(FakeMonitor monitor, string failure)
    {
        var silence = new InvalidOperationException("The monitor didn't answer.");
        if (failure == "capabilities fail") monitor.Capabilities = null;
        else if (failure == "capabilities throw") monitor.CapabilitiesThrows = silence;
        else if (failure == "brightness fails") monitor.Brightness = null;
        else if (failure == "brightness throws") monitor.BrightnessThrows = silence;
        else throw new ArgumentOutOfRangeException(nameof(failure), failure, "Not a failure these tests know.");
        return monitor;
    }

    /// <summary>Starts a read on another thread, as the App reads off its UI thread, and returns once the read is inside
    /// <paramref name="slow"/>'s brightness request, where it stays until <paramref name="answer"/> is set. The task gives
    /// the readings, and whether the monitor was let go that way rather than giving up after <see cref="Timeout"/>.</summary>
    private static Task<(IReadOnlyList<DdcReading> Readings, bool LetGo)> ReadHeldBy(FakeMonitor slow, DdcBrightness reader, ManualResetEventSlim answer)
    {
        using var asked = new ManualResetEventSlim();
        var letGo = false;
        slow.WhileAsked = () =>
        {
            slow.WhileAsked = null;   // only this read is held
            asked.Set();
            letGo = answer.Wait(Timeout);
        };
        var read = Task.Run(() =>
        {
            var readings = reader.Read();
            return (readings, letGo);
        });
        asked.Wait(Timeout).ShouldBeTrue();
        return read;
    }

    /// <summary>The monitor answers every call again, as a monitor that was asleep does once it wakes.</summary>
    private static void Answering(FakeMonitor monitor)
    {
        monitor.Capabilities = 0x2 | 0x4;
        monitor.CapabilitiesThrows = null;
        monitor.Brightness = (0, 60, 100);
        monitor.BrightnessThrows = null;
    }

    /// <summary>One monitor as a test sets it up, counting what it is asked.</summary>
    private sealed class FakeMonitor(string path, string description = "Generic PnP Monitor")
    {
        public string Path { get; } = path;

        public string Description { get; } = description;

        public bool Active { get; init; } = true;

        /// <summary>MC_CAPS flags, brightness and contrast unless a test says otherwise; null when the call fails.</summary>
        public uint? Capabilities { get; set; } = 0x2 | 0x4;

        /// <summary>Minimum, current and maximum; null when the call fails.</summary>
        public (uint Minimum, uint Current, uint Maximum)? Brightness { get; set; } = (0, 60, 100);

        public Exception? CapabilitiesThrows { get; set; }

        public Exception? BrightnessThrows { get; set; }

        /// <summary>Runs inside each brightness request, before the monitor answers, so a test can hold a read there.</summary>
        public Action? WhileAsked { get; set; }

        public int CapabilityCalls { get; set; }

        public int BrightnessCalls { get; set; }

        public int Calls => CapabilityCalls + BrightnessCalls;
    }

    /// <summary>One HMONITOR: the monitors Windows lists as attached to it, and the physical monitors Dxva2 opens for it.</summary>
    private sealed class FakeDisplay(params FakeMonitor[] monitors)
    {
        public IReadOnlyList<FakeMonitor> Monitors { get; } = monitors;

        /// <summary>What Dxva2 opens, when that isn't the active monitors in their order.</summary>
        public IReadOnlyList<FakeMonitor>? Physical { get; set; }

        public bool CanOpen { get; set; } = true;

        public Exception? ListThrows { get; init; }

        public int Opens { get; set; }
    }

    /// <summary>Windows as a test sets it up. Every opening hands out new handles, as Dxva2 does, and remembers which are
    /// still open.</summary>
    private sealed class FakeWindows : IMonitorCalls
    {
        private readonly Dictionary<IntPtr, FakeMonitor> _open = [];
        private int _lastHandle = 100;

        public List<FakeDisplay> Screens { get; } = [];

        public Exception? ListingThrows { get; set; }

        public List<IReadOnlyList<PhysicalMonitor>> Opened { get; } = [];

        public List<IReadOnlyList<PhysicalMonitor>> Closed { get; } = [];

        public int OpenHandles => _open.Count;

        /// <summary>What each monitor was asked, and the reader's pauses, in order.</summary>
        public List<string> Log { get; } = [];

        public IReadOnlyList<IntPtr> Displays()
            => ListingThrows is { } error ? throw error : [.. Enumerable.Range(1, Screens.Count).Select(number => (IntPtr)number)];

        public IReadOnlyList<AttachedMonitor> Attached(IntPtr display)
        {
            var screen = Screen(display);
            if (screen.ListThrows is { } error) throw error;
            return [.. screen.Monitors.Select(monitor => new AttachedMonitor(monitor.Path, monitor.Description, monitor.Active))];
        }

        public IReadOnlyList<PhysicalMonitor>? Open(IntPtr display)
        {
            var screen = Screen(display);
            screen.Opens++;
            if (!screen.CanOpen) return null;
            var opened = new List<PhysicalMonitor>();
            foreach (var monitor in screen.Physical ?? [.. screen.Monitors.Where(candidate => candidate.Active)])
            {
                var handle = (IntPtr)(++_lastHandle);
                _open.Add(handle, monitor);
                opened.Add(new PhysicalMonitor(handle, monitor.Description));
            }
            Opened.Add(opened);
            return opened;
        }

        public uint? Capabilities(IntPtr monitor)
        {
            var fake = _open[monitor];
            fake.CapabilityCalls++;
            Log.Add("capabilities " + fake.Path);
            return fake.CapabilitiesThrows is { } error ? throw error : fake.Capabilities;
        }

        public (uint Minimum, uint Current, uint Maximum)? Brightness(IntPtr monitor)
        {
            var fake = _open[monitor];
            fake.BrightnessCalls++;
            Log.Add("brightness " + fake.Path);
            fake.WhileAsked?.Invoke();
            return fake.BrightnessThrows is { } error ? throw error : fake.Brightness;
        }

        public void Close(IReadOnlyList<PhysicalMonitor> monitors)
        {
            Closed.Add(monitors);
            foreach (var monitor in monitors) _open.Remove(monitor.Handle);
        }

        private FakeDisplay Screen(IntPtr display) => Screens[(int)display - 1];
    }

    /// <summary>The two system events DdcBrightness listens for, raised here instead of by real hardware or Windows.</summary>
    private sealed class FakeSystemEvents : ISystemEvents
    {
        public event EventHandler? DisplaySettingsChanged;

        public event PowerModeChangedEventHandler? PowerModeChanged;

        public bool HasSubscribers => DisplaySettingsChanged is not null || PowerModeChanged is not null;

        public void RaiseDisplaySettingsChanged() => DisplaySettingsChanged?.Invoke(this, EventArgs.Empty);

        public void RaisePowerModeChanged(PowerModes mode) => PowerModeChanged?.Invoke(this, new PowerModeChangedEventArgs(mode));
    }
}
