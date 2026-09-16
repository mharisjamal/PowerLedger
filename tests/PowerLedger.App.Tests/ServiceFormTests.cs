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
    public async Task A_save_keeps_the_choices_for_monitors_not_attached_now()
    {
        var unplugged = new MonitorChoice { Key = "GSM5B09-104NTAB2C123", Counted = false, Watts = 41 };
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultLaptop with { Monitors = [new MonitorChoice { Key = Statuses.Dell.Key, Counted = false }, unplugged] },
        });
        (await form.SaveAsync()).ShouldBeTrue();   // no monitors shown at all

        form.ShowMonitors([Statuses.Dell]);
        form.Monitors.Single().Counted.ShouldBeFalse();
        form.Monitors.Single().Counted = true;
        (await form.SaveAsync()).ShouldBeTrue();

        var sent = _link.Writes.Cast<ServiceSettings>().ToList();
        sent.Count.ShouldBe(2);
        sent[0].Profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Dell.Key, Counted = false }, unplugged]);
        sent[1].Profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Dell.Key, Counted = true }, unplugged]);
    }

    [Fact]
    public async Task Settings_from_a_service_that_keeps_no_monitor_choices_save_none()
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { Monitors = null! } });
        form.ShowMonitors([]);

        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("en-US", "26.9", null)]      // the figure shown, left as it was
    [InlineData("en-US", " 26.90 ", null)]   // the same figure, typed differently
    [InlineData("en-US", "", null)]          // blank: PowerLedger's own figure
    [InlineData("en-US", "27", 27.0)]
    [InlineData("en-US", "0", 0.0)]
    [InlineData("de-DE", "26,9", null)]
    [InlineData("de-DE", "26.9", null)]      // a point is read too, as in every number the form takes
    [InlineData("de-DE", "31,5", 31.5)]
    public async Task Only_a_figure_other_than_the_one_powerledger_worked_out_is_saved_as_typed(string culture, string typed, double? saved)
    {
        var form = new ServiceForm(_link, UiThreads.Inline, CultureInfo.GetCultureInfo(culture));
        form.Load(ServiceSettings.Default);
        form.ShowMonitors([Statuses.Dell with { OnWatts = 26.94 }]);
        form.Monitors.Single().Watts.ShouldBe(26.9.ToString(CultureInfo.GetCultureInfo(culture)));

        form.Monitors.Single().Watts = typed;
        (await form.SaveAsync()).ShouldBeTrue();

        ((ServiceSettings)_link.Writes.Single()).Profile.Monitors.Single().Watts.ShouldBe(saved);
    }

    [Fact]
    public async Task A_typed_figure_stays_typed_until_it_is_cleared()
    {
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with
        {
            Profile = MachineProfile.DefaultLaptop with { Monitors = [new MonitorChoice { Key = Statuses.Dell.Key, Watts = 30 }] },
        });
        form.ShowMonitors([Statuses.Dell with { OnWatts = 30, Source = MonitorSource.Typed }]);
        var row = form.Monitors.Single();
        (row.Watts, row.Source).ShouldBe(("30", "typed"));

        (await form.SaveAsync()).ShouldBeTrue();
        row.Watts = "";
        (await form.SaveAsync()).ShouldBeTrue();

        _link.Writes.Cast<ServiceSettings>().Select(s => s.Profile.Monitors.Single().Watts).ShouldBe([30.0, null]);
    }

    [Fact]
    public void A_status_older_than_the_choices_doesnt_pass_a_typed_figure_off_as_powerledgers()
    {
        // Just after a save that cleared a typed figure, the service may still report it: the box stays blank, for
        // PowerLedger's figure, rather than filling with the old one, which a second save would type back in.
        var form = new ServiceForm(_link, UiThreads.Inline, English);
        form.Load(ServiceSettings.Default with { Profile = MachineProfile.DefaultLaptop with { Monitors = [new MonitorChoice { Key = Statuses.Dell.Key }] } });

        form.ShowMonitors([Statuses.Dell with { OnWatts = 30, Source = MonitorSource.Typed }]);

        (form.Monitors.Single().Watts, form.Monitors.Single().Source).ShouldBe(("", ""));
    }

    [Theory]
    [InlineData("lots", "Type the watts for 24B1XH5 as a number.")]
    [InlineData("-3", "Type the watts for 24B1XH5 as a number.")]
    [InlineData("900", "A monitor must draw between 0 and 500 W.")]
    public async Task A_monitors_figure_that_cannot_be_sent_is_said(string typed, string message)
    {
        var form = Form();
        form.ShowMonitors([Statuses.Dell, Statuses.Aoc]);
        form.Monitors[1].Watts = typed;

        (await form.SaveAsync()).ShouldBeFalse();

        form.Message.ShouldBe(message);
        _link.Writes.ShouldBeEmpty();
    }

    [Fact]
    public void Monitors_are_listed_once_each_and_without_size_or_resolution_when_the_service_has_none()
    {
        var form = Form();
        var bare = new MonitorStatus { Key = "AOC2402-0", Instance = @"DISPLAY\AOC2402\1", Name = "", OnWatts = 14.3, Source = MonitorSource.Estimate };

        form.ShowMonitors([bare, bare with { Name = "AOC 2402" }, Statuses.Dell with { Width = 0, Height = 0 }]);

        form.Monitors.Select(m => (m.Name, m.Size)).ShouldBe([("External monitor", ""), ("DELL U2723QE", "27 in")]);
    }

    [Fact]
    public void Loading_the_settings_again_lists_the_monitors_afresh()
    {
        var form = Form();
        form.ShowMonitors([Statuses.Dell]);
        form.HasMonitors.ShouldBeTrue();

        form.Load(ServiceSettings.Default);

        form.HasMonitors.ShouldBeFalse();
        form.Monitors.ShouldBeEmpty();
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
