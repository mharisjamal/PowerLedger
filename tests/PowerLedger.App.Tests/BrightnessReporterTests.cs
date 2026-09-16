using System.IO;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class BrightnessReporterTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeLink _link = new() { Status = Statuses.WithMonitors() };
    private readonly FakeReader _reader = new();
    private readonly FakeDisplays _displays = new();
    private readonly FakeUiSettings _ui = new();
    private readonly BrightnessReporter _reporter;

    public BrightnessReporterTests()
    {
        _link.Connect(true);
        _reporter = new BrightnessReporter(_link, _reader, _displays, _ui, _clock);
    }

    public void Dispose() => _reporter.Dispose();

    private static MonitorBrightness Reported(MonitorStatus monitor, double brightness) => new() { Instance = monitor.Instance, Brightness = brightness };

    private static MonitorPowerReading Reported(MonitorStatus monitor, MonitorPowerState state) => new() { Instance = monitor.Instance, State = state };

    private static MonitorDisplayReading Driven(MonitorStatus monitor, double refreshHz, bool hdr) => new() { Instance = monitor.Instance, RefreshHz = refreshHz, Hdr = hdr };

    [Fact]
    public void The_monitors_are_read_a_minute_after_start_and_every_minute_after()
    {
        _reporter.Start();

        _clock.Advance(BrightnessReporter.FirstRead - TimeSpan.FromSeconds(1));
        _reader.Reads.ShouldBe(0);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _reader.Reads.ShouldBe(1);
        _displays.Reads.ShouldBe(1);
        _link.BrightnessReports.Count.ShouldBe(1);

        _clock.Advance(BrightnessReporter.ReadEvery - TimeSpan.FromSeconds(1));
        _reader.Reads.ShouldBe(1);
        _clock.Advance(TimeSpan.FromSeconds(1));
        _reader.Reads.ShouldBe(2);
        _displays.Reads.ShouldBe(2);
        _link.BrightnessReports.Count.ShouldBe(2);

        BrightnessReporter.FirstRead.ShouldBe(TimeSpan.FromMinutes(1));
        BrightnessReporter.ReadEvery.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Each_reading_is_reported_under_the_instance_the_service_names_the_monitor_by()
    {
        _reader.Readings = [new DdcReading(Statuses.DellPath, 0.6, MonitorPowerState.On), new DdcReading(Statuses.AocPath, 0.35, null)];

        await _reporter.ReportAsync();

        var report = _link.BrightnessReports.Single();
        report.Monitors.ShouldBe([Reported(Statuses.Dell, 0.6), Reported(Statuses.Aoc, 0.35)]);
        report.Power.ShouldBe([Reported(Statuses.Dell, MonitorPowerState.On)]);   // the AOC gave no power state
    }

    [Fact]
    public async Task A_report_is_sent_with_power_states_alone_when_no_brightness_was_read()
    {
        // Brightness is asked less often than the power mode, so most reads give power states only.
        _reader.Readings = [new DdcReading(Statuses.DellPath, null, MonitorPowerState.Off), new DdcReading(Statuses.AocPath, null, MonitorPowerState.Standby)];

        await _reporter.ReportAsync();

        var report = _link.BrightnessReports.Single();
        report.Monitors.ShouldBeEmpty();
        report.Power.ShouldBe([Reported(Statuses.Dell, MonitorPowerState.Off), Reported(Statuses.Aoc, MonitorPowerState.Standby)]);
    }

    [Fact]
    public async Task How_Windows_drives_each_monitor_is_reported_under_the_instance_the_service_names_it_by()
    {
        _displays.Readings = [new DisplayReading(Statuses.DellPath, 143.998, true), new DisplayReading(Statuses.AocPath, 60, false)];

        await _reporter.ReportAsync();

        _link.BrightnessReports.Single().Displays.ShouldBe([Driven(Statuses.Dell, 143.998, true), Driven(Statuses.Aoc, 60, false)]);
    }

    [Fact]
    public async Task A_report_is_sent_with_display_settings_alone_when_no_monitor_answers_over_its_cable()
    {
        // Windows describes a monitor whether or not it answers DDC/CI, as many behind docks don't.
        _reader.Readings = [];
        _displays.Readings = [new DisplayReading(Statuses.AocPath, 75, false)];

        await _reporter.ReportAsync();

        var report = _link.BrightnessReports.Single();
        report.Monitors.ShouldBeEmpty();
        report.Power.ShouldBeEmpty();
        report.Displays.ShouldBe([Driven(Statuses.Aoc, 75, false)]);
    }

    [Fact]
    public async Task Only_the_monitors_the_service_lists_are_reported()
    {
        _link.Status = Statuses.WithMonitors(Statuses.Dell);
        _reader.Readings =
        [
            new DdcReading(Statuses.AocPath, 0.35, MonitorPowerState.On),                                                     // attached, but not listed yet
            new DdcReading(@"MONITOR\DELA0B1\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001", 0.5, MonitorPowerState.On),       // not a display's path
            new DdcReading(Statuses.DellPath, 0.6, MonitorPowerState.On),
        ];
        _displays.Readings =
        [
            new DisplayReading(Statuses.AocPath, 75, false),
            new DisplayReading(@"\\?\DISPLAY#BOE0A1C#4&1a2b3c4d&0&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", 120, false),   // a laptop's panel
            new DisplayReading(Statuses.DellPath, 144, true),
        ];

        await _reporter.ReportAsync();

        var report = _link.BrightnessReports.Single();
        report.Monitors.ShouldBe([Reported(Statuses.Dell, 0.6)]);
        report.Power.ShouldBe([Reported(Statuses.Dell, MonitorPowerState.On)]);
        report.Displays.ShouldBe([Driven(Statuses.Dell, 144, true)]);
    }

    [Fact]
    public async Task A_service_from_before_monitors_is_sent_nothing_and_no_monitor_is_asked()
    {
        // It sends no list, and would drop a connection that sent it a message it doesn't know.
        _link.Status = Statuses.Running() with { Monitors = null };

        await _reporter.ReportAsync();

        _reader.Reads.ShouldBe(0);
        _displays.Reads.ShouldBe(0);
        _link.BrightnessReports.ShouldBeEmpty();
    }

    [Fact]
    public async Task With_no_external_monitors_no_monitor_is_asked()
    {
        _link.Status = Statuses.Running();

        await _reporter.ReportAsync();

        _reader.Reads.ShouldBe(0);
        _displays.Reads.ShouldBe(0);
        _link.BrightnessReports.ShouldBeEmpty();
    }

    [Fact]
    public async Task While_the_displays_are_off_no_monitor_is_asked()
    {
        _reader.Readings = [new DdcReading(Statuses.DellPath, null, MonitorPowerState.Standby)];
        _link.Status = Statuses.WithMonitors() with { Last = Frames.At(_clock.GetUtcNow()) with { DisplayOn = false } };

        await _reporter.ReportAsync();

        _reader.Reads.ShouldBe(0);
        _displays.Reads.ShouldBe(0);
        _link.BrightnessReports.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_monitors_are_read_while_the_displays_are_on_or_before_the_first_reading()
    {
        _link.Status = Statuses.WithMonitors() with { Last = Frames.At(_clock.GetUtcNow()) };
        await _reporter.ReportAsync();

        _link.Status = Statuses.WithMonitors() with { Last = null };
        await _reporter.ReportAsync();

        _reader.Reads.ShouldBe(2);
        _link.BrightnessReports.Count.ShouldBe(2);
    }

    [Fact]
    public void While_the_user_doesnt_allow_it_no_monitor_is_asked_but_how_Windows_drives_each_is_still_reported()
    {
        // The tick stops the requests sent to the monitors; Windows' display settings ask no monitor anything.
        _reporter.Start();
        _ui.ReadMonitorBrightness(false);

        _clock.Advance(BrightnessReporter.FirstRead + BrightnessReporter.ReadEvery);
        _reader.Reads.ShouldBe(0);
        _displays.Reads.ShouldBe(2);
        _link.BrightnessReports.Count.ShouldBe(2);
        foreach (var report in _link.BrightnessReports)
        {
            report.Monitors.ShouldBeEmpty();
            report.Power.ShouldBeEmpty();
            report.Displays.ShouldBe([Driven(Statuses.Dell, 60, false)]);
        }

        _ui.ReadMonitorBrightness(true);
        _clock.Advance(BrightnessReporter.ReadEvery);
        _reader.Reads.ShouldBe(1);
        _displays.Reads.ShouldBe(3);
        _link.BrightnessReports[^1].Power.ShouldBe([Reported(Statuses.Dell, MonitorPowerState.On)]);
    }

    [Fact]
    public async Task While_the_user_doesnt_allow_it_and_Windows_describes_no_listed_monitor_nothing_is_sent()
    {
        _ui.ReadMonitorBrightness(false);
        _displays.Readings = [];

        await _reporter.ReportAsync();

        _reader.Reads.ShouldBe(0);
        _link.BrightnessReports.ShouldBeEmpty();
    }

    [Fact]
    public async Task Without_the_service_no_monitor_is_asked()
    {
        _link.Connect(false);

        await _reporter.ReportAsync();

        _reader.Reads.ShouldBe(0);
        _displays.Reads.ShouldBe(0);
    }

    [Fact]
    public async Task When_neither_the_monitors_nor_Windows_give_anything_nothing_is_sent()
    {
        _reader.Readings = [];
        _displays.Readings = [];

        await _reporter.ReportAsync();

        _link.BrightnessReports.ShouldBeEmpty();
    }

    [Fact]
    public void A_report_that_fails_is_swallowed_and_the_next_one_still_goes()
    {
        _reporter.Start();
        _link.ReportThrows = new IOException("The connection to the service closed.");

        Should.NotThrow(() => _clock.Advance(BrightnessReporter.FirstRead));
        _reader.Reads.ShouldBe(1);

        _link.ReportThrows = null;
        _link.Answer = WriteResult.NoAnswer;
        _clock.Advance(BrightnessReporter.ReadEvery);
        _link.BrightnessReports.Count.ShouldBe(1);

        _link.Answer = WriteResult.Done;
        _clock.Advance(BrightnessReporter.ReadEvery);
        _link.BrightnessReports.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(true)]     // the monitors' reader
    [InlineData(false)]    // Windows' display configuration
    public async Task A_reader_that_throws_is_swallowed(bool monitors)
    {
        if (monitors) _reader.Throws = new InvalidOperationException("No desktop.");
        else _displays.Throws = new InvalidOperationException("No desktop.");

        await Should.NotThrowAsync(_reporter.ReportAsync);

        _link.BrightnessReports.ShouldBeEmpty();
    }

    [Fact]
    public void Once_disposed_it_reads_no_more()
    {
        _reporter.Start();
        _reporter.Dispose();

        _clock.Advance(BrightnessReporter.FirstRead + BrightnessReporter.ReadEvery);

        _reader.Reads.ShouldBe(0);
    }

    /// <summary>Monitors as a test sets them up, counting the reads.</summary>
    private sealed class FakeReader : IBrightnessReader
    {
        public IReadOnlyList<DdcReading> Readings { get; set; } = [new DdcReading(Statuses.DellPath, 0.6, MonitorPowerState.On)];

        public Exception? Throws { get; set; }

        public int Reads { get; private set; }

        public IReadOnlyList<DdcReading> Read()
        {
            Reads++;
            return Throws is { } error ? throw error : Readings;
        }
    }

    /// <summary>Windows' display configuration as a test sets it up, counting the reads.</summary>
    private sealed class FakeDisplays : IDisplayReader
    {
        public IReadOnlyList<DisplayReading> Readings { get; set; } = [new DisplayReading(Statuses.DellPath, 60, false)];

        public Exception? Throws { get; set; }

        public int Reads { get; private set; }

        public IReadOnlyList<DisplayReading> Read()
        {
            Reads++;
            return Throws is { } error ? throw error : Readings;
        }
    }
}
