using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.Core.Tests;

public class AppHourTests
{
    private static readonly DateTimeOffset Nine = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);

    private static string App(int i) => $@"c:\apps\app{i:D3}.exe";

    private static string Named(AppTotals app) => $"name of {app.Key}";

    [Fact]
    public void An_app_keeps_its_totals_and_a_tick_counts_once_however_many_processes_it_has()
    {
        var hour = new AppHour(Nine);
        var chrome = hour.For(App(1), @"C:\Apps\App001.exe");
        hour.For(App(1), null).ShouldBeSameAs(chrome);
        chrome.Path.ShouldBe(@"C:\Apps\App001.exe");

        chrome.Run(tick: 1, seconds: 1);
        chrome.Run(tick: 1, seconds: 1);                            // a second process in the same tick
        chrome.Front(tick: 1, seconds: 1);
        chrome.Run(tick: 2, seconds: 5);
        (chrome.RunSeconds, chrome.FrontSeconds).ShouldBe((6, 1));
    }

    [Fact]
    public void Past_500_apps_in_an_hour_the_rest_go_to_other_apps_and_the_special_keys_always_have_their_own()
    {
        var hour = new AppHour(Nine);
        for (var i = 0; i < AppHour.MostApps; i++) hour.For(App(i), null).CpuWh += 1;
        var late = hour.For(@"c:\late.exe", null);
        late.Key.ShouldBe(AppKeys.Other);
        hour.For(App(7), null).Key.ShouldBe(App(7));                // one it has still has its own
        hour.For(AppKeys.Windows, null).Key.ShouldBe(AppKeys.Windows);
        hour.For(AppKeys.Idle, null).Key.ShouldBe(AppKeys.Idle);
    }

    [Fact]
    public void Rows_keep_the_busiest_25_and_the_special_keys_and_put_the_rest_with_other_apps()
    {
        var hour = new AppHour(Nine);
        for (var i = 0; i < 30; i++)
        {
            var app = hour.For(App(i), App(i).ToUpperInvariant());
            app.CpuWh = i + 1;
            app.GpuWh = i == 0 ? 100 : 0;                           // the least on the processor, the most on the card
            app.Run(1, 60 * (i + 1));
            app.Front(1, i < 3 ? 10 : 0);
        }
        hour.For(App(99), null).Run(1, 3600);                       // ran all hour, used nothing: no row
        var other = hour.For(AppKeys.Other, null);
        other.CpuWh = 0.5;
        other.Run(1, 7000);
        hour.For(AppKeys.Idle, null).CpuWh = 2;
        hour.For(AppKeys.Closed, null);                             // nothing at all: no row

        var rows = hour.Rows(Named);
        rows.Count.ShouldBe(27);
        rows.Where(r => !AppKeys.IsSpecial(r.Key)).Select(r => r.Key)
            .ShouldBe([App(0), .. Enumerable.Range(6, 24).Reverse().Select(App)]);
        rows.Single(r => r.Key == App(0)).ShouldBe(new AppHourRow(App(0), $"name of {App(0)}", App(0).ToUpperInvariant(), 1, 100, 60, 10));
        // Apps 1 to 5 merge into Other apps: energy and time in front add up; it ran as long as the longest of them.
        rows.Single(r => r.Key == AppKeys.Other).ShouldBe(new AppHourRow(AppKeys.Other, "Other apps", null, 0.5 + 2 + 3 + 4 + 5 + 6, 0, 7000, 20));
        rows.Single(r => r.Key == AppKeys.Idle).ShouldBe(new AppHourRow(AppKeys.Idle, "Idle", null, 2, 0, 0, 0));
        rows.Sum(r => r.CpuWh).ShouldBe(Enumerable.Range(1, 30).Sum() + 0.5 + 2, 1e-9);
    }

    [Fact]
    public void An_hour_already_stored_carries_on_from_its_rows()
    {
        AppHourRow[] stored =
        [
            new(App(1), "First", @"C:\Apps\App001.exe", 1, 0.5, 600, 60),
            new(AppKeys.Other, "Other apps", null, 0.25, 0, 300, 0),
        ];
        var hour = new AppHour(Nine, stored);
        var first = hour.For(App(1), null);
        first.CpuWh += 1;
        first.Run(1, 5);
        hour.For(AppKeys.Other, null).GpuWh += 0.75;

        hour.Rows(_ => throw new InvalidOperationException("a stored app keeps its name")).ShouldBe(
        [
            new AppHourRow(App(1), "First", @"C:\Apps\App001.exe", 2, 0.5, 605, 60),
            new AppHourRow(AppKeys.Other, "Other apps", null, 0.25, 0.75, 300, 0),
        ], ignoreOrder: true);
    }
}
