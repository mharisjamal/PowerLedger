using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;
using static PowerLedger.App.Tests.MidnightHost;
using static PowerLedger.App.Tests.UiHarness;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan O tasks M2-1 to M2-4: Midnight's History, Report, Household and Settings pages over the Classic ViewModels,
/// drawn in both Midnight palettes at the page's full length, and in the room a short window (880 × 560) leaves a page,
/// where each scrolls and cuts nothing off at the side. Each page is checked for what it must show, without a ViewModel
/// of its own to test.
/// </summary>
[Trait("Category", "UI")]
public class MidnightPageRenderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>The room a short Midnight window leaves a page: 880 less the 232 px sidebar, 560 less the 56 px top bar and the page header row.</summary>
    private const double ShortWidth = 880 - 232;
    private const double ShortHeight = 560 - 56 - 64;

    [Fact]
    public void History_shows_the_range_pills_the_chart_card_and_a_row_per_part_with_its_share_bar_in_both_themes()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                using var styles = Midnight(theme);
                var model = BreakdownScreen();
                model.Range.Choice = RangeChoice.SevenDays;
                model.Show();
                var view = new Midnight.HistoryView { DataContext = model };
                using var page = Page(view, 1010);
                var pills = AllOf<RadioButton>(view).Where(p => p.Content is string).ToList();
                pills.Select(p => p.Content).ShouldBe(["Today", "7 days", "30 days", "This month", "Last month", "Custom", "W", "Wh"], ignoreOrder: true, theme.ToString());
                pills.Where(p => p.IsChecked == true).Select(p => p.Content).ShouldBe(["7 days", "W"], ignoreOrder: true, theme.ToString());
                Find<StackedChart>(view).ShouldNotBeNull(theme.ToString()).ActualHeight.ShouldBe(300, theme.ToString());

                // A row per part and the total, each part with a bar as long as its share of the track.
                var rows = Find<ItemsControl>(view, items => items.ItemsSource == model.Parts).ShouldNotBeNull(theme.ToString());
                model.Parts.Count.ShouldBe(5, theme.ToString());
                foreach (var part in model.Parts)
                {
                    var row = rows.ItemContainerGenerator.ContainerFromItem(part).ShouldBeAssignableTo<DependencyObject>(theme.ToString());
                    Find<TextBlock>(row, t => t.Text == part.Name).ShouldNotBeNull($"{part.Name} on {theme}");
                    Find<TextBlock>(row, t => t.Text == part.Energy).ShouldNotBeNull($"{part.Name} on {theme}");
                    var bar = Find<ShareBar>(row, b => System.Windows.Automation.AutomationProperties.GetName(b) == $"{part.Name} share").ShouldNotBeNull($"{part.Name} on {theme}");
                    if (part.Part is null) bar.Visibility.ShouldBe(Visibility.Hidden, theme.ToString());
                    else (bar.Value, bar.ActualWidth).ShouldBe((part.Fraction, 120.0), $"{part.Name} on {theme}");
                }
                Render(page.Host, (int)page.Host.ActualWidth, (int)page.Host.ActualHeight, $"midnight-history-{theme}.png");

                model.Range.Choice = RangeChoice.Custom;
                Pump(TimeSpan.FromMilliseconds(300));
                page.Host.UpdateLayout();
                Find<DatePicker>(view).ShouldNotBeNull(theme.ToString()).IsVisible.ShouldBeTrue(theme.ToString());
                Render(page.Host, (int)page.Host.ActualWidth, (int)page.Host.ActualHeight, $"midnight-history-custom-{theme}.png");
            }
        });
        Sizes(30_000, "history", "history-custom");
    }

    [Fact]
    public void Report_shows_the_export_row_and_the_sheet_as_cards_and_its_png_export_draws_the_sheet_in_both_themes()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                using var styles = Midnight(theme);
                using var saver = new FakeSaver();
                var model = ReportScreen(saver);
                model.Show();
                var view = new Midnight.ReportView { DataContext = model };
                using var page = Page(view, 1010);
                AllOf<Button>(view).Where(b => b.TemplatedParent is null).Select(b => b.Content)
                    .ShouldBe(["PDF", "PNG", "CSV · 1 h", "CSV · 1 min", "CSV · raw"], ignoreOrder: true, theme.ToString());
                Find<CheckBox>(view, c => Equals(c.Content, "Include my household")).ShouldNotBeNull(theme.ToString()).Visibility.ShouldBe(Visibility.Collapsed, theme.ToString());
                foreach (var title in new[] { "BILL", "TIME", "BY COMPONENT", "EVERYDAY EQUIVALENTS", "IDLE WASTE", "DATA QUALITY", "DAILY ENERGY" })
                {
                    Find<TextBlock>(view, t => t.Text == title).ShouldNotBeNull($"{title} on {theme}").IsVisible.ShouldBeTrue($"{title} on {theme}");
                }
                Find<TextBlock>(view, t => t.Text == model.Data.Title).ShouldNotBeNull(theme.ToString());
                Find<TextBlock>(view, t => t.Text == model.Data.Energy).ShouldNotBeNull(theme.ToString());
                Render(page.Host, (int)page.Host.ActualWidth, (int)page.Host.ActualHeight, $"midnight-report-{theme}.png");

                // PNG draws the sheet, the element named Sheet, into the file the saver names.
                view.FindName("Sheet").ShouldBeOfType<Grid>(theme.ToString());
                Find<Button>(view, b => Equals(b.Content, "PNG")).ShouldNotBeNull(theme.ToString()).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                new FileInfo(saver.Chosen).Length.ShouldBeGreaterThan(30_000, theme.ToString());
                File.Copy(saver.Chosen, Path.Combine(Folder, $"midnight-report-picture-{theme}.png"), overwrite: true);
            }
        });
        Sizes(30_000, "report");
    }

    [Fact]
    public void Household_shows_three_ledger_cards_a_row_per_pc_with_its_share_bar_and_the_manage_and_sign_in_cards_in_both_themes()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                using var styles = Midnight(theme);
                var model = HouseholdScreen();
                model.Show();
                var view = new Midnight.HouseholdView { DataContext = model };
                using var page = Page(view, 1010);
                foreach (var title in new[] { "TODAY", "THIS WEEK", "THIS MONTH", "Your PCs", "MANAGE THIS HOUSEHOLD", "SIGN IN" })
                {
                    Find<TextBlock>(view, t => t.Text == title).ShouldNotBeNull($"{title} on {theme}").IsVisible.ShouldBeTrue($"{title} on {theme}");
                }
                Find<TextBlock>(view, t => t.Text == "HOUSEHOLD").ShouldNotBeNull(theme.ToString()).IsVisible.ShouldBeFalse($"the explainer on {theme}");
                Find<TextBlock>(view, t => t.Text == "46.8").ShouldNotBeNull($"this month's energy on {theme}");

                // A row per PC: this one with no Remove, the other with its bar as long as its share of the busiest.
                var rows = Find<ItemsControl>(view, items => items.ItemsSource == model.Members).ShouldNotBeNull(theme.ToString());
                model.Members.Count.ShouldBe(2, theme.ToString());
                foreach (var member in model.Members)
                {
                    var row = rows.ItemContainerGenerator.ContainerFromItem(member).ShouldBeAssignableTo<DependencyObject>(theme.ToString());
                    Find<TextBlock>(row, t => t.Text == member.Name).ShouldNotBeNull($"{member.Name} on {theme}");
                    Find<TextBlock>(row, t => t.Text == member.Energy).ShouldNotBeNull($"{member.Name} on {theme}");
                    Find<TextBlock>(row, t => t.Text == "this PC").ShouldNotBeNull($"{member.Name} on {theme}").IsVisible.ShouldBe(member.IsThisPc, $"{member.Name} on {theme}");
                    Find<Button>(row, b => Equals(b.Content, "Remove")).ShouldNotBeNull($"{member.Name} on {theme}").IsVisible.ShouldBe(!member.IsThisPc, $"{member.Name} on {theme}");
                    Find<Button>(row, b => Equals(b.Content, "Remove its rows")).ShouldNotBeNull($"{member.Name} on {theme}").IsVisible.ShouldBeFalse($"{member.Name} on {theme}");
                    Find<ShareBar>(row, b => System.Windows.Automation.AutomationProperties.GetName(b) == $"{member.Name} share").ShouldNotBeNull($"{member.Name} on {theme}")
                        .Value.ShouldBe(member.Share, $"{member.Name} on {theme}");
                    Find<Initials>(row).ShouldNotBeNull($"{member.Name} on {theme}").Member.ShouldBe(member.Name, $"{member.Name} on {theme}");
                }
                AllOf<Button>(view).Count(b => Equals(b.Content, "Add a PC") && b.IsVisible).ShouldBe(1, theme.ToString());
                Render(page.Host, (int)page.Host.ActualWidth, (int)page.Host.ActualHeight, $"midnight-household-{theme}.png");
            }
        });
        Sizes(30_000, "household");
    }

    [Fact]
    public void Household_before_one_exists_shows_the_explainer_with_add_a_pc_and_a_left_member_offers_to_remove_its_rows()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                using var styles = Midnight(theme);

                var none = NoHouseholdScreen();
                none.Show();
                var explainer = new Midnight.HouseholdView { DataContext = none };
                using (var page = Page(explainer, 1010))
                {
                    Find<TextBlock>(explainer, t => t.Text == HouseholdViewModel.Explanation).ShouldNotBeNull(theme.ToString()).IsVisible.ShouldBeTrue(theme.ToString());
                    Find<TextBlock>(explainer, t => t.Text == "Your PCs").ShouldNotBeNull(theme.ToString()).IsVisible.ShouldBeFalse(theme.ToString());
                    var add = AllOf<Button>(explainer).Where(b => Equals(b.Content, "Add a PC") && b.IsVisible).ToList();
                    add.Count.ShouldBe(1, theme.ToString());
                    add[0].Style.ShouldBe(Application.Current.FindResource("M.Button.Primary"), theme.ToString());
                    Find<TextBlock>(explainer, t => t.Text == "SIGN IN").ShouldNotBeNull(theme.ToString()).IsVisible.ShouldBeTrue(theme.ToString());
                    Render(page.Host, (int)page.Host.ActualWidth, (int)page.Host.ActualHeight, $"midnight-household-none-{theme}.png");
                }

                var left = LeftMemberScreen();
                left.Show();
                left.AskRemove.Execute(left.Members.Single(m => m.IsLeft));
                var members = new Midnight.HouseholdView { DataContext = left };
                using (var page = Page(members, 1010))
                {
                    var rows = Find<ItemsControl>(members, items => items.ItemsSource == left.Members).ShouldNotBeNull(theme.ToString());
                    var row = rows.ItemContainerGenerator.ContainerFromItem(left.Members.Single(m => m.IsLeft)).ShouldBeAssignableTo<DependencyObject>(theme.ToString());
                    Find<Button>(row, b => Equals(b.Content, "Remove its rows")).ShouldNotBeNull(theme.ToString()).IsVisible.ShouldBeTrue(theme.ToString());
                    Find<Button>(row, b => Equals(b.Content, "Remove")).ShouldNotBeNull(theme.ToString()).IsVisible.ShouldBeFalse(theme.ToString());
                    // The confirm block lives in the Manage card here and in the explainer card before a household exists; the visible one counts.
                    Find<TextBlock>(members, t => t.Text == "Remove Laptop-2's rows? This can't be undone." && t.IsVisible).ShouldNotBeNull(theme.ToString());
                    Find<Button>(members, b => Equals(b.Content, "Confirm") && b.IsVisible).ShouldNotBeNull(theme.ToString());
                    Render(page.Host, (int)page.Host.ActualWidth, (int)page.Host.ActualHeight, $"midnight-household-left-{theme}.png");
                }
            }
        });
        Sizes(10_000, "household-none", "household-left");   // two short cards, and a table of two rows
    }

    [Fact]
    public void Settings_shows_each_section_as_a_card_with_the_look_and_theme_pills_and_the_privacy_switches_in_both_themes()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                using var styles = Midnight(theme);
                var model = SettingsScreen();
                model.Show();
                model.Service.RamIsDdr5 = true;   // saved itself, so the line that says how the last save went shows
                model.Service.Message.ShouldBe("Saved.");
                var view = new Midnight.SettingsView { DataContext = model };
                using var page = Page(view, 1010);
                foreach (var title in new[] { "TARIFF", "MACHINE", "SAMPLING AND HISTORY", "CALIBRATION", "PREFERENCES", "PRIVACY", "HOUSEHOLD", "ABOUT" })
                {
                    var eyebrow = Find<TextBlock>(view, t => t.Text == title).ShouldNotBeNull($"{title} on {theme}");
                    eyebrow.IsVisible.ShouldBeTrue($"{title} on {theme}");
                    Ancestor<Border>(eyebrow, b => b.Style == Application.Current.FindResource("M.Card")).ShouldNotBeNull($"{title} in a card on {theme}");
                }
                AllOf<RadioButton>(view).Where(p => p.Content is "Classic" or "Midnight" or "Like Windows" or "Dark" or "Light").Count().ShouldBe(5, theme.ToString());
                AllOf<RadioButton>(view).Where(p => p.Content is "Like Windows").Single().IsChecked.ShouldBe(true, theme.ToString());
                var switches = AllOf<ToggleButton>(view).Where(t => t is not CheckBox and not RadioButton).ToList();
                switches.Select(t => t.Content).ShouldContain("Crash and sensor reports", theme.ToString());
                var toggle = (Style)Application.Current.FindResource("M.Switch");
                switches.ShouldAllBe(t => t.Style == toggle || t.Style!.BasedOn == toggle, theme.ToString());
                model.Service.Monitors.Count.ShouldBe(2, theme.ToString());
                foreach (var monitor in model.Service.Monitors)
                {
                    Find<TextBox>(view, box => System.Windows.Automation.AutomationProperties.GetName(box) == $"Watts for {monitor.Name}").ShouldNotBeNull($"{monitor.Name} on {theme}")
                        .IsVisible.ShouldBeTrue($"{monitor.Name} on {theme}");
                }
                Find<TextBlock>(view, t => t.Text == "Saved.").ShouldNotBeNull(theme.ToString()).IsVisible.ShouldBeTrue(theme.ToString());
                Render(page.Host, (int)page.Host.ActualWidth, (int)page.Host.ActualHeight, $"midnight-settings-{theme}.png");
            }
        });
        Sizes(30_000, "settings");
    }

    /// <summary>The same behaviour Classic's Settings has, through the shared SettingsEntry behaviour (M2-4).</summary>
    [Fact]
    public void Settings_saves_a_typed_value_once_enter_is_pressed_and_a_tick_at_once()
        => OnUi(() =>
        {
            using var styles = Midnight(Theme.Dark);
            var link = new FakeLink();
            var model = SettingsScreen(link);
            model.Show();
            var view = new Midnight.SettingsView { DataContext = model };
            var window = new Window
            {
                Content = view, Width = 1010, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            try
            {
                Pump(TimeSpan.FromMilliseconds(300));
                var fans = Find<TextBox>(view, box => System.Windows.Automation.AutomationProperties.GetName(box) == "Fans").ShouldNotBeNull();
                FocusManager.SetFocusedElement(window, fans);
                fans.Text = "3";
                link.Writes.ShouldBeEmpty();
                fans.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(fans), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                ((ServiceSettings)link.Writes[^1]).Profile.FanCount.ShouldBe(3);

                Find<CheckBox>(view, box => Equals(box.Content, "DDR5")).ShouldNotBeNull().IsChecked = true;
                ((ServiceSettings)link.Writes[^1]).Profile.RamIsDdr5.ShouldBeTrue();
                link.Writes.Count.ShouldBe(2);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void The_pages_scroll_in_the_room_a_short_window_leaves_and_cut_nothing_off_at_the_side()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            using var styles = Midnight(Theme.Dark);
            foreach (var (name, view) in ShortPages())
            {
                var window = new Window
                {
                    Content = view, Width = ShortWidth, Height = ShortHeight, Background = (Brush)Application.Current.FindResource("M.Ground"),
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.None,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(600));
                    var scroller = view.Content.ShouldBeOfType<ScrollViewer>(name);
                    (scroller.ExtentHeight > scroller.ViewportHeight).ShouldBeTrue($"{name} is {scroller.ExtentHeight:0} tall in a view {scroller.ViewportHeight:0} tall");
                    scroller.ComputedVerticalScrollBarVisibility.ShouldBe(Visibility.Visible, name);
                    foreach (var element in AllOf<FrameworkElement>((DependencyObject)scroller.Content).Where(e => e.IsVisible && e is TextBlock or Button or Border))
                    {
                        element.TranslatePoint(new Point(element.ActualWidth, 0), scroller).X.ShouldBeLessThanOrEqualTo(scroller.ViewportWidth + 0.5, $"{name}: {Describe(element)} runs past the side");
                    }
                    Render(window, (int)ShortWidth, (int)ShortHeight, $"midnight-short-{name}.png");
                    scroller.ScrollToEnd();
                    Pump(TimeSpan.FromMilliseconds(200));
                    scroller.VerticalOffset.ShouldBe(Math.Max(0, scroller.ExtentHeight - scroller.ViewportHeight), 0.5, $"the end of {name} is out of reach");
                    Render(window, (int)ShortWidth, (int)ShortHeight, $"midnight-short-{name}-end.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>The pages a short window shows, each with the data the Classic renders use.</summary>
    private static IEnumerable<(string Name, UserControl View)> ShortPages()
    {
        var history = BreakdownScreen();
        history.Range.Choice = RangeChoice.SevenDays;
        history.Show();
        yield return ("history", new Midnight.HistoryView { DataContext = history });
        var report = ReportScreen(new FakeSaver());
        report.Show();
        yield return ("report", new Midnight.ReportView { DataContext = report });
        var household = HouseholdScreen();
        household.Show();
        yield return ("household", new Midnight.HouseholdView { DataContext = household });
        var settings = SettingsScreen();
        settings.Show();
        yield return ("settings", new Midnight.SettingsView { DataContext = settings });
    }

    /// <summary>Settings against a running service, as the Classic renders show it: a tariff, this laptop's detection, two
    /// external monitors, and a UPS and a power supply read over USB. The service is <paramref name="link"/> when given.</summary>
    private static SettingsViewModel SettingsScreen(FakeLink? link = null)
    {
        link ??= new FakeLink();
        link.Status = Statuses.WithMonitors(Statuses.Dell, Statuses.Portable) with { PowerDevices = [Statuses.Ups, Statuses.PowerSupply] };
        link.Connect(true);
        return new SettingsViewModel(link, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, "USD");
    }

    /// <summary>The nearest <typeparamref name="T"/> above <paramref name="element"/> that <paramref name="match"/> accepts.</summary>
    private static T? Ancestor<T>(DependencyObject element, Func<T, bool> match)
        where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is T candidate && match(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>A household of two PCs, as the Classic renders show it: this desktop, well ahead this month, and a laptop last seen three days ago.</summary>
    private static HouseholdViewModel HouseholdScreen()
    {
        var link = new FakeLink
        {
            Status = Statuses.Running() with { Household = new HouseholdStatus("hh1", "aaaa", "Desktop-1", ChassisKind.Desktop, true, [], null) },
        };
        link.Connect(true);
        var history = new FakeHouseholdHistory
        {
            Answer = _ => new HouseholdSnapshot(
                new HouseholdRangeTotals(1.62, [new CurrencyCost("USD", 0.28m)], []),
                new HouseholdRangeTotals(11.4, [new CurrencyCost("USD", 1.94m)], []),
                new HouseholdRangeTotals(46.8, [new CurrencyCost("USD", 7.96m)], [new DeviceEnergy("aaaa", 34.2), new DeviceEnergy("bbbb", 12.6)]),
                [
                    new HouseholdMemberRow("aaaa", "Desktop-1", ChassisKind.Desktop, Now.AddDays(-40), null, Now.AddMinutes(-2)),
                    new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now.AddDays(-20), null, Now.AddDays(-3)),
                ]),
        };
        return new HouseholdViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, FakeAccount.Model(link));
    }

    /// <summary>No household yet, and no rows left from one.</summary>
    private static HouseholdViewModel NoHouseholdScreen()
    {
        var link = new FakeLink();
        link.Connect(true);
        return new HouseholdViewModel(link, new FakeHouseholdHistory(), UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, FakeAccount.Model(link));
    }

    /// <summary>A household whose laptop left three days ago and whose rows are still on file (task 0.8's removeOldRows).</summary>
    private static HouseholdViewModel LeftMemberScreen()
    {
        var link = new FakeLink
        {
            Status = Statuses.Running() with { Household = new HouseholdStatus("hh1", "aaaa", "Desktop-1", ChassisKind.Desktop, true, [], null) },
        };
        link.Connect(true);
        var history = new FakeHouseholdHistory
        {
            Answer = _ => new HouseholdSnapshot(
                new HouseholdRangeTotals(0, [], []), new HouseholdRangeTotals(0, [], []), new HouseholdRangeTotals(0, [], []),
                [
                    new HouseholdMemberRow("aaaa", "Desktop-1", ChassisKind.Desktop, Now.AddDays(-40), null, Now),
                    new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now.AddDays(-20), Now.AddDays(-3), Now.AddDays(-3)),
                ]),
        };
        return new HouseholdViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, FakeAccount.Model(link));
    }

    /// <summary>September so far on the same machine the Classic renders show, with a tariff and a plan that sleeps after three hours.</summary>
    private static ReportViewModel ReportScreen(FakeSaver saver)
    {
        var history = new FakeRangeHistory
        {
            Answer = range =>
            {
                var report = Reports.Typical(range);
                var weights = report.Days.Select((_, i) => 0.7 + 0.15 * (i * 3 % 5)).ToList();
                var days = report.Days.Select((d, i) => d with { EnergyKwh = report.Totals.EnergyKwh * weights[i] / weights.Sum() }).ToList();
                return report with { Days = days };
            },
        };
        return new ReportViewModel(new FakeLink(), history, new FakeHouseholdHistory(), new FakeSleep(), saver, _ => [], UiThreads.Inline,
            new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, 0.38);
    }

    /// <summary>Each page's PNG in both themes holds at least <paramref name="atLeast"/> bytes: a page drawn, not a blank.</summary>
    private static void Sizes(long atLeast, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                new FileInfo(Path.Combine(Folder, $"midnight-{name}-{theme}.png")).Length.ShouldBeGreaterThan(atLeast, $"{name} on {theme}");
            }
        }
    }

    private static string Describe(FrameworkElement element) => element switch
    {
        TextBlock text => $"text \"{text.Text}\"",
        Button button => $"button {button.Content}",
        _ => element.GetType().Name + (element.Name.Length > 0 ? " " + element.Name : ""),
    };

    /// <summary>
    /// Midnight's palette, styles and page styles over the application's dictionaries until disposed, the page styles
    /// merged after the styles as MidnightWindow will.
    /// </summary>
    private static IDisposable Midnight(Theme theme)
    {
        var palette = UsePalette(theme);
        var styles = Use(MidnightStylesTests.Load());
        var pages = Use(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PowerLedger;component/Midnight/Styles.Midnight.Pages.xaml", UriKind.Absolute),
        });
        return new Undo(() =>
        {
            pages.Dispose();
            styles.Dispose();
            palette.Dispose();
        });
    }

    /// <summary>A page laid out at <paramref name="width"/> and its whole length, on Midnight's ground, as the window's page host would show it.</summary>
    private static PageHost Page(FrameworkElement view, double width)
    {
        view.Width = width;
        var host = new Border { Background = (Brush)Application.Current.FindResource("M.Ground"), Child = view };
        var window = new Window
        {
            Content = host, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
        };
        window.Show();
        Pump(TimeSpan.FromMilliseconds(600));
        host.UpdateLayout();
        return new PageHost(host, window);
    }

    private sealed record PageHost(Border Host, Window Window) : IDisposable
    {
        public void Dispose() => Window.Close();
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>The same machine's last seven days the Classic renders show: asleep overnight, working days, quiet evenings.</summary>
    private static BreakdownViewModel BreakdownScreen()
    {
        var link = new FakeLink();
        link.Connect(true);
        var history = new FakeRangeHistory { Answer = range => Reports.Typical(range) with { Series = Week(range) } };
        return new BreakdownViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English);
    }

    private static IReadOnlyList<Aggregate> Week(DateRange range)
    {
        var series = new List<Aggregate>();
        var seed = 5;
        double Noise()
        {
            seed = (seed * 9301 + 49297) % 233280;
            return seed / 233280.0 - 0.5;
        }
        for (var start = range.From; start < range.To; start += range.Bucket)
        {
            var hour = start.Hour;
            var (watts, on) = hour switch
            {
                < 7 or 23 => (0.0, 0.0),
                7 => (16 + Noise() * 2, 1800.0),
                < 12 => (36 + Noise() * 10, 3600.0),
                < 13 => (18 + Noise() * 3, 3600.0),
                < 18 => (42 + Noise() * 14, 3600.0),
                _ => (15 + Noise() * 3, 3600.0),
            };
            var wh = watts * on / 3600;
            series.Add(Aggregate.Empty(start) with
            {
                EnergyWh = wh, CpuWh = wh * 0.45, GpuWh = wh * 0.1, DisplayWh = 4 * on / 3600, RestWh = wh * 0.45 - 4 * on / 3600,
                OnSeconds = on, GapSeconds = 3600 - on,
            });
        }
        return series;
    }
}
