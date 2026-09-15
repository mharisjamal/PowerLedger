using System.Globalization;
using PowerLedger.Contracts;
using Shouldly;

namespace PowerLedger.App.Tests;

public class ServiceFormTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly FakeLink _link = new();

    public ServiceFormTests() => _link.Connect(true);

    private ServiceForm Form()
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default);
        return form;
    }

    [Fact]
    public void Loading_fills_every_field()
    {
        var form = Form();
        form.IsLoaded.ShouldBeTrue();
        form.Chassis.ShouldBe(ChassisKind.Laptop);
        form.RamSticks.ShouldBe("1");
        form.PanelInches.ShouldBe("15.6");
        form.MonitorWatts.ShouldBe("25");
        form.CpuTdp.ShouldBe("");
        form.IdleMinutes.ShouldBe("5");
        form.SampleInterval.ShouldBe("1");
        form.RawHours.ShouldBe("48");
        form.HistoryYears.ShouldBe("2");
    }

    [Fact]
    public async Task What_is_typed_goes_to_the_service_whole()
    {
        var form = Form();
        form.Chassis = ChassisKind.Desktop;
        form.PsuTier = PsuTier.Gold;
        form.RamSticks = "4";
        form.RamIsDdr5 = true;
        form.SsdCount = "2";
        form.HddCount = "1";
        form.FanCount = "5";
        form.PanelInches = "0";
        form.ExternalMonitors = "2";
        form.IncludeMonitors = true;
        form.MonitorWatts = "30";
        form.ExtrasWatts = "12.5";
        form.CpuTdp = "125";
        form.GpuTdp = " ";
        form.IdleMinutes = "10";
        form.SampleInterval = "2";
        form.RawHours = "72";
        form.HistoryYears = "5";

        (await form.SaveAsync()).ShouldBeTrue();

        var sent = (ServiceSettings)_link.Writes.Single();
        sent.Profile.ShouldBe(new MachineProfile
        {
            Chassis = ChassisKind.Desktop, PsuTier = PsuTier.Gold, RamSticks = 4, RamIsDdr5 = true, SsdCount = 2, HddCount = 1,
            FanCount = 5, DisplayDiagonalInches = 0, ExternalMonitors = 2, IncludeMonitors = true, MonitorWatts = 30, ExtrasWatts = 12.5,
            CpuTdpOverrideW = 125, GpuTdpOverrideW = null,
        });
        (sent.IdleThresholdSeconds, sent.SampleIntervalSeconds, sent.RawRetentionHours, sent.HistoryRetentionYears).ShouldBe((600, 2, 72, 5));
        form.Message.ShouldBe("Saved.");
    }

    [Theory]
    [InlineData("RamSticks", "two", "Type the memory sticks as a whole number.")]
    [InlineData("MonitorWatts", "lots", "Type a monitor's watts as a number.")]
    [InlineData("IdleMinutes", "45", "The idle threshold is between 1 and 30 minutes.")]
    [InlineData("RawHours", "12", "Second-by-second history must be kept between 24 and 168 hours.")]
    [InlineData("PanelInches", "80", "The panel size must be 0 for none, or between 7 and 50 inches.")]
    public async Task What_cannot_be_sent_is_said(string field, string typed, string message)
    {
        var form = Form();
        typeof(ServiceForm).GetProperty(field)!.SetValue(form, typed);
        (await form.SaveAsync()).ShouldBeFalse();
        form.Message.ShouldBe(message);
        _link.Writes.ShouldBeEmpty();
    }

    [Fact]
    public void A_form_the_service_never_filled_cannot_be_saved()
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.IsLoaded.ShouldBeFalse();
        form.Save.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public void Choosing_desktop_shows_the_power_supply()
    {
        var form = Form();
        form.IsDesktop.ShouldBeFalse();
        form.Chassis = ChassisKind.Desktop;
        form.IsDesktop.ShouldBeTrue();
    }
}
