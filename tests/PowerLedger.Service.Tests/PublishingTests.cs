using PowerLedger.Contracts;
using PowerLedger.Sensors;
using Shouldly;

namespace PowerLedger.Service.Tests;

public class PublishingTests
{
    [Fact]
    public void A_reading_becomes_a_frame_field_for_field_with_the_reports_bands()
    {
        var reading = Readings.At(5, watts: 36);
        var frame = Frames.From(reading, Samples.At(reading.Timestamp));   // energy meter present, no discrete GPU
        frame.CpuMeasured.ShouldBeTrue();
        frame.GpuMeasured.ShouldBeFalse();
        frame.Timestamp.ShouldBe(reading.Timestamp);
        frame.TotalW.ShouldBe(36);
        frame.Quality.ShouldBe(Quality.Measured);
        frame.Components.Cpu.ShouldBe(18);
        frame.Components.Unattributed.ShouldBe(14);
        frame.DisplayBandW.ShouldBe(4);
        frame.RestBandW.ShouldBe(14);
        frame.OnBattery.ShouldBeTrue();
        frame.Brightness.ShouldBe(0.5);
    }

    [Fact]
    public void Source_health_becomes_source_status()
        => Frames.From(new SourceHealth("nvidia-gpu", true, null, 3, 12, "NVML error 15"))
            .ShouldBe(new SourceStatus("nvidia-gpu", true, null, 3, "NVML error 15"));

    [Fact]
    public async Task Every_subscriber_gets_every_frame()
    {
        var feed = new LiveFeed();
        var a = feed.Subscribe();
        var b = feed.Subscribe();
        feed.Publish(PipeProtocolTests.Frame(10));
        (await a.ReadAsync()).TotalW.ShouldBe(10);
        (await b.ReadAsync()).TotalW.ShouldBe(10);
    }

    [Fact]
    public void A_subscriber_that_falls_behind_loses_its_oldest_frames_and_holds_nobody_up()
    {
        var feed = new LiveFeed();
        var slow = feed.Subscribe();
        for (var i = 0; i < 20; i++) feed.Publish(PipeProtocolTests.Frame(i));
        var kept = new List<double>();
        while (slow.TryRead(out var frame)) kept.Add(frame.TotalW);
        kept.ShouldBe(Enumerable.Range(20 - LiveFeed.Backlog, LiveFeed.Backlog).Select(i => (double)i));
    }

    [Fact]
    public async Task Unsubscribing_ends_the_stream()
    {
        var feed = new LiveFeed();
        var reader = feed.Subscribe();
        feed.Unsubscribe(reader);
        feed.Subscribers.ShouldBe(0);
        (await reader.WaitToReadAsync()).ShouldBeFalse();
        feed.Publish(PipeProtocolTests.Frame());   // nobody listening is fine
    }

    [Fact]
    public void The_board_holds_the_latest_of_each()
    {
        var board = new StatusBoard();
        board.Status.ShouldBeNull();
        board.Settings.ShouldBeNull();
        board.Publish(ServiceSettings.Default);
        board.Settings.ShouldBeSameAs(ServiceSettings.Default);
    }
}
