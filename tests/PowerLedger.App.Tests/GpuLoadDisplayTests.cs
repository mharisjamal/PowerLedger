using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>What the App shows for the graphics cards: their names, their load, and whether their watts were measured,
/// estimated from their load and rating, or only roughly estimated from a rating the service guessed from their memory.</summary>
public class GpuLoadDisplayTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 15, 14, 30, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeLink _link = new();
    private readonly FakeTimeProvider _clock = new(At);

    private NowViewModel NowScreen(string gpu = "AMD Radeon RX 7800 XT", bool rough = false)
    {
        var history = new FakeHistory
        {
            Snapshot = Snapshots.Typical(At) with { Machine = new MachineNames("AMD Ryzen 7 7700X 8-Core Processor", gpu, 0, rough) },
        };
        var model = new NowViewModel(_link, history, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, co2KgPerKwh: 0.38, startService: () => { });
        model.RefreshHistory();
        return model;
    }

    private SettingsViewModel SettingsScreen()
        => new(_link, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "USD");

    [Fact]
    public void The_cards_row_gives_its_name_and_load_and_reads_as_estimated_from_load()
    {
        var model = NowScreen();

        // The service sends this card's load and the watts the model made of it; it never has measured watts.
        _link.Push(Frames.At(At, totalW: 180, quality: Quality.Estimated, gpu: 83.6, gpuMeasured: false, gpuLoad: 0.31));

        model.Live.Budget[1].Name.ShouldBe("GPU");
        model.Live.Budget[1].Detail.ShouldBe("Radeon RX 7800 XT · 31% load · estimated from load");
        model.Live.Budget[1].Watts.ShouldBe("83.6 W");
    }

    [Fact]
    public void Before_the_first_load_arrives_the_card_is_still_there_and_estimated_as_idle()
    {
        var model = NowScreen();

        _link.Push(Frames.At(At, totalW: 90, quality: Quality.Estimated, gpu: 3, gpuMeasured: false, gpuLoad: null));

        model.Live.Budget[1].Detail.ShouldBe("Radeon RX 7800 XT · estimated from load");
    }

    [Fact]
    public void Only_a_machine_with_no_card_at_all_says_it_has_no_discrete_gpu()
    {
        var model = NowScreen();

        _link.Push(Frames.At(At, totalW: 60, quality: Quality.Estimated, gpu: 0, gpuMeasured: false, gpuLoad: null));

        model.Live.Budget[1].Detail.ShouldBe("no discrete GPU found");
    }

    [Fact]
    public void Feedback_issue_4_the_quadro_6000_is_named_and_estimated_from_its_load()
    {
        var model = NowScreen("NVIDIA Quadro 6000");

        _link.Push(Frames.At(At, totalW: 260, quality: Quality.Estimated, gpu: 103.5, gpuMeasured: false, gpuLoad: 0.5));

        model.Live.Budget[1].Detail.ShouldBe("Quadro 6000 · 50% load · estimated from load");
        model.Live.Budget[1].Watts.ShouldBe("103.5 W");
    }

    [Fact]
    public void Every_card_is_named_and_the_row_says_measured_only_when_every_card_was()
    {
        var model = NowScreen("NVIDIA Quadro 6000 + NVIDIA GeForce GTX 1080");

        _link.Push(Frames.At(At, totalW: 400, quality: Quality.Estimated, gpu: 253.5, gpuMeasured: false, gpuLoad: 0.6));
        model.Live.Budget[1].Detail.ShouldBe("Quadro 6000 + GeForce GTX 1080 · 60% load · estimated from load");

        _link.Push(Frames.At(At, totalW: 400, quality: Quality.Estimated, gpu: 270, gpuMeasured: true, gpuLoad: 0.6));
        model.Live.Budget[1].Detail.ShouldBe("Quadro 6000 + GeForce GTX 1080 · 60% load · measured");
    }

    [Fact]
    public void A_card_the_service_rates_only_from_its_memory_is_a_rough_estimate()
    {
        var model = NowScreen("NVIDIA Quadro 6000 + Matrox C900", rough: true);

        _link.Push(Frames.At(At, totalW: 300, quality: Quality.Estimated, gpu: 150, gpuMeasured: false, gpuLoad: 0.4));
        model.Live.Budget[1].Detail.ShouldBe("Quadro 6000 + Matrox C900 · 40% load · rough estimate");

        // A figure every card measured needs no rating at all.
        _link.Push(Frames.At(At, totalW: 300, quality: Quality.Estimated, gpu: 150, gpuMeasured: true, gpuLoad: 0.4));
        model.Live.Budget[1].Detail.ShouldBe("Quadro 6000 + Matrox C900 · 40% load · measured");
    }

    [Fact]
    public void A_rating_the_user_typed_is_no_rough_estimate()
    {
        _link.Settings = ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop with { GpuTdpOverrideW = 150 } };
        _link.Status = Statuses.Running();
        var model = NowScreen("Matrox C900", rough: true);
        model.Start();
        _link.Connect(true);

        _link.Push(Frames.At(At, totalW: 300, quality: Quality.Estimated, gpu: 150, gpuMeasured: false, gpuLoad: 1));

        model.Live.Budget[1].Detail.ShouldBe("Matrox C900 · 100% load · estimated from load");
    }

    [Fact]
    public void About_names_the_gpu_load_source_when_it_reads_the_card()
    {
        _link.Status = Statuses.Running() with
        {
            Sources =
            [
                new SourceStatus("nvidia-gpu", false, "no NVIDIA driver installed", 0, null),
                new SourceStatus("amd-gpu", false, "no AMD discrete GPU", 0, null),
                new SourceStatus("arc-gpu", false, "no Intel Arc discrete GPU", 0, null),
                new SourceStatus("gpu-load", true, null, 0, null),
            ],
        };
        _link.Connect(true);
        var model = SettingsScreen();
        model.Show();

        model.Sources.ShouldBe(
        [
            new SourceLine("NVIDIA graphics", "not on this machine", "no NVIDIA driver installed"),
            new SourceLine("AMD graphics", "not on this machine", "no AMD discrete GPU"),
            new SourceLine("Intel Arc graphics", "not on this machine", "no Intel Arc discrete GPU"),
            new SourceLine("Graphics load", "working", ""),
        ]);
    }

    [Fact]
    public void About_says_why_the_gpu_load_source_has_nothing_to_read()
    {
        _link.Status = Statuses.Running() with
        {
            Sources = [new SourceStatus("gpu-load", false, "no AMD or Intel discrete GPU", 0, null)],
        };
        _link.Connect(true);
        var model = SettingsScreen();
        model.Show();

        model.Sources.ShouldHaveSingleItem().ShouldBe(new SourceLine("Graphics load", "not on this machine", "no AMD or Intel discrete GPU"));
    }
}
