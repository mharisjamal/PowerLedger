using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PowerLedger.App.Aero;
using PowerLedger.Storage;
using Shouldly;
using static PowerLedger.App.Tests.AeroPages;
using static PowerLedger.App.Tests.UiHarness;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan AE: Aero's Apps page drawn in both Aero palettes at the page widths a 960 and a 1440 px window leave, with a line
/// opened, the period switched, its empty and failed states, the names a screen reader hears, and nothing cut off.
/// Renders go to %TEMP%\powerledger-renders\aero-apps-*.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]
public class AppsRenderingTests
{
    /// <summary>Waits for the programs' icons this PC has to be read and drawn.</summary>
    private static void IconsRead(FrameworkElement view)
        => PumpUntil(() => MidnightHost.AllOf<AppIcon>(view).Where(i => i.Path is { } path && File.Exists(path)).All(i => i.Image is not null),
            TimeSpan.FromSeconds(10), "the programs' icons");

    private static ItemsControl Lines(AppsView view) => (ItemsControl)view.FindName("Lines");

    private static FrameworkElement Line(AppsView view, int index) => (FrameworkElement)Lines(view).ItemContainerGenerator.ContainerFromIndex(index);

    [Fact]
    public void Apps_lists_each_app_most_first_with_its_icon_share_kwh_and_cost()
    {
        OnUi(() =>
        {
            foreach (var theme in Themes)
            {
                foreach (var (width, size) in new[] { (Narrow, "960"), (Wide, "1440") })
                {
                    var at = $"{size} on {theme}";
                    var model = AppsFixtures.Screen();
                    model.Show();
                    var view = new AppsView { DataContext = model };
                    using var page = Page(view, width, theme);
                    IconsRead(view);

                    MidnightHost.AllOf<RadioButton>(view).Select(p => p.Content).ShouldBe(["Today", "Week", "Month"], at);
                    MidnightHost.AllOf<RadioButton>(view).Single(p => p.IsChecked == true).Content.ShouldBe("Today", at);
                    Find<TextBlock>(view, t => t.Text == model.Summary).ShouldNotBeNull(at).IsVisible.ShouldBeTrue(at);
                    model.Summary.ShouldStartWith("1.83 kWh, ", Case.Sensitive, at);

                    var names = model.Rows.Select(r => r.Name).ToList();
                    names.ShouldBe(["Google Chrome", "Visual Studio Code", "Microsoft Edge", "Spotify", "Windows Media Player", "Git Bash",
                        "Programs that already closed", "Other apps", "Screen", "Windows and idle", "Rest of your PC", "Total"], at);
                    Lines(view).Items.Count.ShouldBe(names.Count, at);
                    for (var i = 0; i < model.Rows.Count; i++)
                    {
                        var row = model.Rows[i];
                        var line = Line(view, i);
                        Find<TextBlock>(line, t => t.Text == row.Name).ShouldNotBeNull($"{row.Name} {at}").IsVisible.ShouldBeTrue($"{row.Name} {at}");
                        Find<TextBlock>(line, t => t.Text == row.Energy).ShouldNotBeNull($"{row.Name} {at}");
                        Find<TextBlock>(line, t => t.Text == row.Share).ShouldNotBeNull($"{row.Name} {at}");
                        Find<TextBlock>(line, t => t.Text == row.Cost).ShouldNotBeNull($"{row.Name} {at}").IsVisible.ShouldBeTrue($"{row.Name} {at}");
                        var head = Find<Disclosure>(line).ShouldNotBeNull($"{row.Name} {at}");
                        AutomationProperties.GetName(head).ShouldBe(row.Spoken, $"{row.Name} {at}");
                        AutomationProperties.GetHelpText(head).ShouldBe(row.Detail, $"{row.Name} {at}");
                        head.Focusable.ShouldBe(row.Kind != AppRowKind.Total, $"{row.Name} {at}");
                        var bar = Find<ShareBar>(line, b => b.Name == "Bar").ShouldNotBeNull($"{row.Name} {at}");
                        bar.Visibility.ShouldBe(row.Kind is AppRowKind.Beside or AppRowKind.Total ? Visibility.Hidden : Visibility.Visible, $"{row.Name} {at}");
                        Find<Border>(line, b => b.Name == "Rule")!.IsVisible.ShouldBe(row.Name == "Screen", $"{row.Name} {at}");
                        var picture = Find<AppIcon>(line).ShouldNotBeNull($"{row.Name} {at}");
                        (picture.Path, picture.Glyph).ShouldBe((row.Path, row.Glyph), $"{row.Name} {at}");
                    }
                    model.Rows[0].Spoken.ShouldBe($"Google Chrome, 0.344 kWh, 19 percent, {Money.Spoken(12.04m, "PKR", AppsFixtures.English)}", at);
                    page.Render($"apps-{size}");
                }
            }
        });
        Sizes(30_000, "apps-960", "apps-1440");
    }

    /// <summary>A line opens in place, lit as a well: its processor against its graphics, how long it ran and was in front,
    /// and the background tag; a screen reader hears it expand; opening another closes it.</summary>
    [Fact]
    public void A_line_opens_in_place_to_its_processor_and_graphics_its_time_and_the_background_tag()
    {
        OnUi(() =>
        {
            foreach (var theme in Themes)
            {
                var model = AppsFixtures.Screen();
                model.Show();
                var view = new AppsView { DataContext = model };
                using var page = Page(view, Narrow, theme);
                IconsRead(view);
                var at = theme.ToString();
                var edge = Line(view, 2);
                var head = Find<Disclosure>(edge)!;
                var details = Find<StackPanel>(edge, p => p.Name == "Details")!;
                details.IsVisible.ShouldBeFalse(at);

                var peer = (IExpandCollapseProvider)UIElementAutomationPeer.CreatePeerForElement(head).GetPattern(PatternInterface.ExpandCollapse);
                peer.ExpandCollapseState.ShouldBe(ExpandCollapseState.Collapsed, at);
                peer.Expand();
                PumpUntil(() => details.IsVisible && details.Opacity >= 1, TimeSpan.FromSeconds(3), "the details faded in");
                Pump(TimeSpan.FromMilliseconds(500));

                model.Rows[2].IsOpen.ShouldBeTrue(at);
                peer.ExpandCollapseState.ShouldBe(ExpandCollapseState.Expanded, at);
                foreach (var words in new[] { "Ran 7h 00m", "In front 25m", "Mostly in background" })
                {
                    Find<TextBlock>(details, t => t.Text == words).ShouldNotBeNull($"{words} {at}").IsVisible.ShouldBeTrue($"{words} {at}");
                }
                Find<TextBlock>(details, t => t.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Text == "0.096 kWh")).ShouldNotBeNull(at);
                Find<ShareBar>(details)!.Value.ShouldBe(96 / 108.0, 1e-9, at);
                Find<Border>(edge, b => b.Name == "Card")!.Background.ShouldNotBe(Brushes.Transparent, at);
                ((RotateTransform)Find<Icon>(head, i => i.Name == "Chevron")!.RenderTransform).Angle.ShouldBe(180, 0.5, at);
                page.Render("apps-open");

                // A line that is no app says what it holds; opening it closes the other.
                var screen = Line(view, 8);
                Find<Disclosure>(screen)!.Activate();
                Pump(TimeSpan.FromMilliseconds(600));
                details.IsVisible.ShouldBeFalse(at);
                model.Rows.Single(r => r.IsOpen).Name.ShouldBe("Screen", at);
                Find<TextBlock>(screen, t => t.Text == "The built-in panel and any monitors counted.").ShouldNotBeNull(at).IsVisible.ShouldBeTrue(at);
                Find<StackPanel>(screen, p => p.Name == "Split")!.IsVisible.ShouldBeFalse(at);
                page.Render("apps-open-screen");
            }
        });
        Sizes(30_000, "apps-open", "apps-open-screen");
    }

    [Fact]
    public void The_period_pill_slides_to_the_week_and_the_month_and_reads_them()
        => OnUi(() =>
        {
            var history = new FakeAppHistory();
            var model = AppsFixtures.Screen(history: history);
            model.Show();
            var view = new AppsView { DataContext = model };
            using var page = Page(view, Wide, Theme.Dark);
            var track = (SegTrack)view.FindName("Periods");
            var pills = MidnightHost.AllOf<RadioButton>(track).ToList();
            track.Chosen.ShouldBe(pills[0]);

            pills[2].IsChecked = true;
            Pump(TimeSpan.FromMilliseconds(900));

            model.Period.ShouldBe(AppsPeriod.Month);
            history.Reads[^1].From.ShouldBe(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
            track.Chosen.ShouldBe(pills[2]);
            var host = (FrameworkElement)track.Template.FindName("PART_Host", track);
            track.Indicator.X.ShouldBe(pills[2].TranslatePoint(default, host).X, 0.5);
            track.Indicator.Width.ShouldBe(pills[2].ActualWidth, 0.5);
            Find<TextBlock>(view, t => t.Text == "Apps counted from 2 Oct.").ShouldNotBeNull().IsVisible.ShouldBeTrue();
            page.Render("apps-month");
        });

    [Fact]
    public void Before_apps_have_been_watched_and_with_the_history_away_it_says_so()
    {
        OnUi(() =>
        {
            foreach (var theme in Themes)
            {
                foreach (var (answer, name, words) in new (Func<DateRange, AppsRange?>, string, string)[]
                {
                    (_ => AppsRange.None(AppsFixtures.Now.AddHours(-14), AppsFixtures.Now, null, "PKR"), "apps-empty",
                        "Apps show here once PowerLedger has watched them for a few minutes."),
                    (_ => null, "apps-failed", "Apps can't be read right now. They come back when the service is running."),
                })
                {
                    var model = AppsFixtures.Screen(answer);
                    model.Show();
                    var view = new AppsView { DataContext = model };
                    using var page = Page(view, Narrow, theme);
                    Find<TextBlock>(view, t => t.Text == words).ShouldNotBeNull($"{name} on {theme}").IsVisible.ShouldBeTrue($"{name} on {theme}");
                    Find<Border>(view, b => b.Style == view.FindResource("A.Table.Header"))!.IsVisible.ShouldBeFalse($"{name} on {theme}");
                    Lines(view).Items.Count.ShouldBe(0, $"{name} on {theme}");
                    page.Render(name);
                }
            }
        });
        Sizes(8_000, "apps-empty", "apps-failed");
    }

    /// <summary>Without a tariff the cost column goes and the period's total stands alone.</summary>
    [Fact]
    public void Without_a_tariff_the_cost_column_goes()
        => OnUi(() =>
        {
            var model = AppsFixtures.Screen(_ => AppsFixtures.Day(currency: null));
            model.Show();
            var view = new AppsView { DataContext = model };
            using var page = Page(view, Narrow, Theme.Dark);
            Find<TextBlock>(view, t => t.Text == "Cost")!.IsVisible.ShouldBeFalse();
            MidnightHost.AllOf<TextBlock>(view).Where(t => t.Text == Format.Missing).ShouldAllBe(t => !t.IsVisible);
            model.Summary.ShouldBe("1.83 kWh so far today");
            page.Render("apps-no-tariff");
        });

    /// <summary>Reduced motion: an opened line's details are simply there, faded in briefly, with no travel; the chevron
    /// turns at once.</summary>
    [Fact]
    public void Under_reduced_motion_a_line_opens_without_travel()
        => OnUi(() =>
        {
            using var reduced = AeroMotion.Force(true);
            var model = AppsFixtures.Screen();
            model.Show();
            var view = new AppsView { DataContext = model };
            using var page = Page(view, Narrow, Theme.Dark);
            var line = Line(view, 0);

            Find<Disclosure>(line)!.Activate();
            Pump(TimeSpan.FromMilliseconds(30));

            var details = Find<StackPanel>(line, p => p.Name == "Details")!;
            ((TranslateTransform)details.RenderTransform).Y.ShouldBe(0);
            ((RotateTransform)Find<Icon>(line, i => i.Name == "Chevron")!.RenderTransform).Angle.ShouldBe(180);
            PumpUntil(() => details.Opacity >= 1, TimeSpan.FromMilliseconds(AeroMotion.ReducedFade + 400), "the short fade");
        });

    /// <summary>The keyboard: one Tab stop for the ledger, the arrows from line to line, Enter opens one; the total is no
    /// stop at all.</summary>
    [Fact]
    public void The_keyboard_moves_along_the_lines_and_opens_one()
        => OnUi(() =>
        {
            var model = AppsFixtures.Screen();
            model.Show();
            var view = new AppsView { DataContext = model };
            using var page = Page(view, Narrow, Theme.Dark);
            var lines = Lines(view);
            KeyboardNavigation.GetTabNavigation(lines).ShouldBe(KeyboardNavigationMode.Once);
            KeyboardNavigation.GetDirectionalNavigation(lines).ShouldBe(KeyboardNavigationMode.Contained);
            var first = Find<Disclosure>(Line(view, 0))!;
            first.Focus();
            first.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down)).ShouldBeTrue();
            var second = Find<Disclosure>(Line(view, 1))!;
            FocusManager.GetFocusedElement(FocusManager.GetFocusScope(second)).ShouldBe(second);

            second.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(second)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Pump(TimeSpan.FromMilliseconds(100));

            model.Rows[1].IsOpen.ShouldBeTrue();
            Find<Disclosure>(Line(view, model.Rows.Count - 1))!.Focusable.ShouldBeFalse();
        });
}
