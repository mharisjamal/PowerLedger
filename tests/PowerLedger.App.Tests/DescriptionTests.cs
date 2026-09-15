using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>The drawn controls tell a screen reader what they show.</summary>
public class DescriptionTests
{
    private static string NameOf(UIElement control) => UIElementAutomationPeer.CreatePeerForElement(control).GetName();

    [Fact]
    public void Drawn_controls_describe_what_they_show()
    {
        var names = Sta.Run(() =>
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            return new[]
            {
                NameOf(new LiveReadout { Value = 34.2 }),
                NameOf(new MeterScale { Value = 34.2, Average = 41, Peak = 68, Range = MeterRange.For(68) }),
                NameOf(new Sparkline { Values = [new SparkSample(10, 30.4), new SparkSample(0, 34.2)] }),
                NameOf(new BudgetBar { Rows = [new BudgetRow(Part.Cpu, "CPU package", "", "14.6 W", "43%", 0.43)] }),
                NameOf(new QualityBar { Mix = new QualityMix(0.62, 0.2, 0.18) }),
                NameOf(new DailyBars { Days = [new DayBar(new DateOnly(2026, 9, 1), 0.3), new DayBar(new DateOnly(2026, 9, 2), 0.5)] }),
                NameOf(new StackedChart { Model = ChartModel.Empty with { Description = "Today: power by component." } }),
            };
        });

        names.ShouldBe(new[]
        {
            "34.2 watts",
            "Meter: 34.2 W now, average 41 W, peak 68 W today.",
            "Last 60 seconds: between 30 and 34 W.",
            "Power budget: CPU package 14.6 W (43%).",
            "Quality: 62% measured, 20% calibrated, 18% estimated.",
            "Daily energy over 2 days; the highest was 0.500 kWh on 2 Sep.",
            "Today: power by component.",
        });
    }

    [Fact]
    public void A_name_the_view_gives_wins()
        => Sta.Run(() =>
        {
            var chart = new StackedChart();
            AutomationProperties.SetName(chart, "Today's chart");
            return NameOf(chart);
        }).ShouldBe("Today's chart");
}
