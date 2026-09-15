using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class NowViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeLink _link = new();
    private readonly FakeHistory _history = new();
    private int _starts;

    private NowViewModel Model()
        => new(_link, _history, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, co2KgPerKwh: 0.38, startService: () => _starts++);

    [Fact]
    public void A_reading_fills_the_live_panel()
    {
        var model = Model();
        _link.Push(Frames.At(Now, totalW: 34.2));

        model.Live.Watts.ShouldBe(34.2);
        model.Live.Eyebrow.ShouldBe("Live · battery discharge · 14:32:07");
        model.Live.Quality.ShouldBe(Quality.Measured);
        model.Live.QualityNote.ShouldBe("Windows battery report · 1 s samples");
        model.Live.Spark.ShouldBe(new[] { new SparkSample(0, 34.2) });
        model.Live.Budget.Select(r => r.Name).ShouldBe(new[] { "CPU package", "GPU", "Display", "Rest of system" });
        model.Live.Budget[0].Watts.ShouldBe("14.6 W");
        model.Live.Budget[0].Percent.ShouldBe("43%");
        model.Live.BudgetTotalW.ShouldBe(34.2, 1e-9);
        model.TrayTooltip.ShouldStartWith("34.2 W · Measured");
    }

    [Fact]
    public void Budget_rows_say_what_each_part_is_and_how_it_was_known()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();
        _link.Push(Frames.At(Now));

        model.Live.Budget[0].Detail.ShouldBe("Core i7-1165G7 · 30% load · measured");
        model.Live.Budget[1].Detail.ShouldBe("GeForce MX330 · 31% load · modelled");
        model.Live.Budget[2].Detail.ShouldBe("15.3 in · brightness 60%");
        model.Live.Budget[3].Detail.ShouldBe("RAM, SSD, board, radios · measured remainder");
    }

    [Fact]
    public void A_card_windows_has_switched_off_says_so()
    {
        var model = Model();
        _link.Push(Frames.At(Now, gpu: 0, gpuMeasured: true, gpuLoad: 0));
        model.Live.Budget[1].Detail.ShouldBe("switched off");
    }

    [Fact]
    public void History_fills_today_s_ledger_the_way_the_user_reads_it()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();

        model.Today.Date.ShouldBe("Tue 8 Sep");
        model.Today.Energy.ShouldBe("0.284");
        model.Today.Cost.ShouldBe("$0.05");
        model.Today.Tariff.ShouldBe("at $0.17 / kWh");
        model.Today.Average.ShouldBe("41");
        model.Today.Peak.ShouldBe("68");
        model.Today.PeakAt.ShouldBe("at 14:00");
        model.Today.On.ShouldBe("7h 05m");
        model.Today.Idle.ShouldBe("45m");
        model.Today.IdleWasted.ShouldBe("11 Wh wasted");
        model.Today.Asleep.ShouldBe("7h 30m");
        model.Today.Co2.ShouldBe("0.11");
        model.Today.Co2Factor.ShouldBe("0.38 kg / kWh grid");
        model.IsCollecting.ShouldBeFalse();
    }

    [Fact]
    public void History_fills_the_month_s_ledger()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();

        model.Month.Name.ShouldBe("September");
        model.Month.Summary.ShouldBe("8 days · 62% measured");
        model.Month.Energy.ShouldBe("2.74");
        model.Month.Cost.ShouldBe("$0.47");
        model.Month.Projected.ShouldBe("$1.85");
        model.Month.ProjectedEnergy.ShouldBe("10.8 kWh");
        model.Month.DailyAverage.ShouldBe("0.360 kWh");
        model.Month.IdleWaste.ShouldBe("0.210 kWh");
        model.Month.NextReport.ShouldBe("1 Oct");
    }

    [Fact]
    public void The_meter_marks_today_s_average_and_the_higher_of_history_s_peak_and_the_live_one()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();
        _link.Push(Frames.At(Now, totalW: 90));

        model.Live.PeakW.ShouldBe(90);
        model.Live.AverageW.ShouldBe(41);
        model.Live.Meter.Max.ShouldBe(100);
    }

    [Fact]
    public void Until_today_has_a_folded_minute_the_ledgers_are_collecting()
    {
        var model = Model();
        model.IsCollecting.ShouldBeTrue();
        var empty = Snapshots.Typical(Now);
        _history.Snapshot = empty with { Today = empty.Today with { OnHours = 0 } };
        model.RefreshHistory();
        model.IsCollecting.ShouldBeTrue();
    }

    [Fact]
    public void Without_the_service_the_screen_says_so_after_the_grace_period_and_offers_to_start_it()
    {
        var model = Model();
        model.Start();
        model.Connection.ShouldBe(Connection.Connecting);
        _clock.Advance(NowViewModel.ConnectGrace);
        model.IsServiceDown.ShouldBeTrue();
        model.Status.State.ShouldBe("Service not running");

        model.StartService.Execute(null);
        _starts.ShouldBe(1);

        _link.Connect(true);
        model.IsServiceDown.ShouldBeFalse();
        model.Status.State.ShouldBe("Service running");
        model.Status.Service.ShouldBe("Service 0.1.0");
        model.Status.Sampling.ShouldBe("Sampling 1 s");
        model.Status.Calibration.ShouldBe("Calibrating · 15m of 30m on battery");
        model.Status.Database.ShouldBe("Database 31 MB");
    }

    [Fact]
    public void With_no_energy_meter_and_no_battery_the_screen_says_so_honestly()
    {
        _link.Status = Statuses.Running(energyMeter: false, battery: false);
        var model = Model();
        _link.Connect(true);
        model.IsSensorless.ShouldBeTrue();
    }

    [Fact]
    public void Losing_the_service_clears_the_live_panel()
    {
        var model = Model();
        _link.Connect(true);
        _link.Push(Frames.At(Now));
        _link.Connect(false);
        model.Live.ShouldBe(LivePanel.Waiting);
        model.IsServiceDown.ShouldBeTrue();
    }

    [Theory]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "Core i7-1165G7")]
    [InlineData("AMD Ryzen 7 5800U with Radeon Graphics", "Ryzen 7 5800U")]
    [InlineData("NVIDIA GeForce MX330", "GeForce MX330")]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", "Core Ultra 7 155H")]
    public void Names_lose_the_trademarks_the_maker_and_the_clock_speed(string full, string expected)
        => NowViewModel.ShortName(full).ShouldBe(expected);

    [Fact]
    public void Todays_chart_spans_the_day_and_its_legend_gives_each_bands_energy()
    {
        var model = Model();
        _history.Snapshot = Snapshots.Typical(Now);
        model.RefreshHistory();

        model.Chart.Capacity.ShouldBe(288);
        model.Chart.NowAt.ShouldNotBeNull().ShouldBe(174.42, 0.01);           // 14:32:07 in five-minute buckets
        model.Legend.ShouldBe(new ChartLegend("120 Wh", "30 Wh", "28 Wh", "106 Wh"));
    }
}
