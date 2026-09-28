using System.IO;
using System.Windows;
using System.Windows.Controls;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S D4: Aero's shell drawn to PNGs under %TEMP%\powerledger-renders for a person to hold against the approved demo:
/// every page, both themes, 960 and 1440 px wide, the short window, the banner, the bell's menu, a toast, a dialog, and
/// the intro played through; with what a person would check on each (no text cut short, the glass drawn).
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class AeroShellRenderingTests
{
    private static void Draw(string name, Func<ShellViewModel> shell, double width, double height, Theme theme, Action<AeroWindow>? act = null, bool reduced = true)
    {
        Directory.CreateDirectory(UiHarness.Folder);
        UiHarness.OnUi(() =>
        {
            using var motion = AeroMotion.Force(reduced ? true : false);
            var window = AeroHost.Window(shell(), theme);
            window.Width = width;
            window.Height = height;
            window.Show();
            try
            {
                UiHarness.Pump(TimeSpan.FromMilliseconds(reduced ? 500 : 3200));
                act?.Invoke(window);
                window.UpdateLayout();
                UiHarness.Render(window, (int)width, (int)height, name);
            }
            finally
            {
                window.OpenMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
                window.CloseForSwitch();
            }
        });
        new FileInfo(Path.Combine(UiHarness.Folder, name)).Length.ShouldBeGreaterThan(20_000, name);
    }

    [Theory]
    [InlineData("Dark", 1440, 900)]
    [InlineData("Light", 1440, 900)]
    [InlineData("Dark", 960, 640)]
    [InlineData("Light", 960, 640)]
    public void Every_page_in_both_themes_wide_and_narrow(string themeName, int width, int height)
    {
        var theme = Enum.Parse<Theme>(themeName);
        foreach (var page in new[] { Page.Dashboard, Page.Breakdown, Page.Parts, Page.Insights, Page.Report, Page.Household, Page.Settings })
        {
            using var saver = new FakeSaver();
            Draw($"aero-{page}-{theme}-{width}.png", () => AeroFixtures.Shell(saver), width, height, theme, window =>
            {
                window.Page = page;
                UiHarness.Pump(TimeSpan.FromMilliseconds(400));
                window.PageHost.Showing.ShouldNotBeNull();
            });
        }
    }

    /// <summary>The Dashboard's figures are whole: no text in it trimmed at 1440 px, and its panes are glass.</summary>
    [Fact]
    public void The_dashboards_text_is_never_cut_short_and_its_panes_are_glass()
    {
        using var saver = new FakeSaver();
        Draw("aero-dashboard-checked.png", () => AeroFixtures.Shell(saver), 1440, 900, Theme.Dark, window =>
        {
            var view = (Aero.DashboardView)window.PageHost.Showing!;
            view.Panes.Length.ShouldBe(5);
            view.Panes.ShouldAllBe(p => p.Template != null && p.ActualWidth > 0);
            foreach (var text in MidnightHost.AllOf<TextBlock>(view).Where(t => t.IsVisible && t.ActualWidth > 0 && !string.IsNullOrEmpty(t.Text)))
            {
                text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                text.DesiredSize.Width.ShouldBeLessThanOrEqualTo(text.ActualWidth + 1, $"\"{text.Text}\" is cut short");
            }
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_short_window_the_banner_and_the_intro_played_through(string themeName)
    {
        var theme = Enum.Parse<Theme>(themeName);
        using var saver = new FakeSaver();
        Draw($"aero-short-{theme}.png", () => AeroFixtures.Shell(saver), 880, 560, theme, window => window.SideColumn().ShouldBe(76));
        Draw($"aero-banner-{theme}.png", () => AeroFixtures.Shell(saver, AeroFixtures.Moved(Look.Classic)), 1440, 900, theme);
        Draw($"aero-intro-{theme}.png", () => AeroFixtures.Shell(saver), 1440, 900, theme, window =>
        {
            var view = (Aero.DashboardView)window.PageHost.Showing!;
            view.Panes.ShouldAllBe(p => p.Opacity > 0.99, "the intro ends with every pane in place");
            UiHarness.Find<LiveChart>(view)!.Reveal.ShouldBe(1, 0.01);
            UiHarness.Find<DailyChart>(view)!.Reveal.ShouldBe(1, 0.01);
            UiHarness.Find<PieChart3D>(view)!.Rise.ShouldBe(1, 0.01);
        }, reduced: false);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_bell_a_toast_and_a_dialog(string themeName)
    {
        var theme = Enum.Parse<Theme>(themeName);
        using var saver = new FakeSaver();
        var hour = new DateTimeOffset(DateTime.Now.Date.AddHours(DateTime.Now.Hour));
        var insights = new FakeInsights { Answer = FakeInsights.Empty with { Anomalies = [new UsageAnomaly(hour, 0.42, 0.12, 3.5)] } };
        Draw($"aero-bell-{theme}.png", () => AeroFixtures.Shell(saver, insights: insights, pendingApprovals: 1), 1440, 900, theme, window =>
        {
            var bell = UiHarness.Find<Button>(window, b => b.Name == "BellButton")!;
            bell.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, bell));
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));
            var menu = window.OpenMenu!;
            menu.UpdateLayout();
            // A render of the window leaves popups out: the menu is drawn on its own.
            UiHarness.Render(menu, (int)Math.Max(1, menu.ActualWidth), (int)Math.Max(1, menu.ActualHeight), $"aero-bell-menu-{theme}.png");
            window.Toast("Saved PowerLedger history by day.csv");
            window.OpenModal(UiHarness.Find<Button>(window, b => b.Name == "ServiceButton"), new TextBlock { Text = "Restart the service?", FontSize = 20 });
            UiHarness.Pump(TimeSpan.FromMilliseconds(400));
        });
    }
}
