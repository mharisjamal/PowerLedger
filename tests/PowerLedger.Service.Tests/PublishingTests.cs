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
        (frame.Total, frame.GpuScope).ShouldBe((TotalSource.Model, GpuPowerScope.Board));
    }

    [Theory]
    [InlineData(TotalSource.Battery, GpuPowerScope.Board)]
    [InlineData(TotalSource.Ups, GpuPowerScope.ChipOnly)]
    [InlineData(TotalSource.PowerSupply, GpuPowerScope.Package)]
    public void A_frame_says_where_its_total_came_from_and_what_the_gpu_reading_covered(TotalSource source, GpuPowerScope scope)
    {
        var reading = Readings.At(5) with { TotalSource = source, GpuScope = scope };
        var frame = Frames.From(reading, Samples.At(reading.Timestamp));
        (frame.Total, frame.GpuScope).ShouldBe((source, scope));
    }

    [Fact]
    public void A_ups_and_a_power_supply_the_sample_read_are_listed_with_their_watts_and_how_they_were_found()
    {
        var sample = Samples.At(Samples.T0) with
        {
            UpsOutputW = 142, UpsSource = UpsPowerSource.LoadOfRatedWatts, UpsName = "APC Back-UPS ES 850G2",
            PsuOutputW = 312.5, PsuName = "Corsair HX1000i",
        };

        Frames.PowerDevices(sample).ShouldBe(
        [
            new PowerDeviceStatus(PowerDeviceKind.Ups, "APC Back-UPS ES 850G2", 142, "load of its rated watts"),
            new PowerDeviceStatus(PowerDeviceKind.PowerSupply, "Corsair HX1000i", 312.5, "DC output, all rails"),
        ]);
    }

    [Fact]
    public void A_supply_that_reports_what_it_draws_from_the_wall_is_listed_as_the_wall_power_it_is()
    {
        // The two figures are not the same thing: one is the whole draw and the other is the rails before the losses.
        var sample = Samples.At(Samples.T0) with { PsuWallW = 312.5, PsuName = "Corsair HX1000i" };

        Frames.PowerDevices(sample).ShouldBe(
        [
            new PowerDeviceStatus(PowerDeviceKind.PowerSupply, "Corsair HX1000i", 312.5, "wall power, as the supply reports it"),
        ]);
    }

    [Fact]
    public void A_supply_that_gave_a_wall_figure_no_one_can_show_is_listed_without_watts()
    {
        Frames.PowerDevices(Samples.At(Samples.T0) with { PsuWallW = double.NaN, PsuName = "Corsair HX1000i" }).ShouldBe(
        [
            new PowerDeviceStatus(PowerDeviceKind.PowerSupply, "Corsair HX1000i", null, ""),
        ]);
    }

    [Theory]
    [InlineData(UpsPowerSource.ActivePower, "real output power")]
    [InlineData(UpsPowerSource.LoadOfRatedWatts, "load of its rated watts")]
    [InlineData(UpsPowerSource.LoadOfRatedVoltAmps, "load of its rated VA, estimated")]
    public void A_ups_says_how_its_watts_were_found(UpsPowerSource source, string how)
        => Frames.PowerDevices(Samples.At(Samples.T0) with { UpsOutputW = 142, UpsSource = source, UpsName = "APC Back-UPS ES 850G2" })
            .ShouldHaveSingleItem().How.ShouldBe(how);

    [Fact]
    public void A_device_that_gave_no_watts_yet_is_listed_without_them_and_nothing_detected_lists_nothing()
    {
        Frames.PowerDevices(Samples.At(Samples.T0)).ShouldBeEmpty();
        Frames.PowerDevices(Samples.At(Samples.T0) with { UpsName = " APC Back-UPS ES 850G2 ", UpsOutputW = double.NaN, PsuName = "Corsair HX1000i" }).ShouldBe(
        [
            new PowerDeviceStatus(PowerDeviceKind.Ups, "APC Back-UPS ES 850G2", null, ""),
            new PowerDeviceStatus(PowerDeviceKind.PowerSupply, "Corsair HX1000i", null, ""),
        ]);
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
