using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>The look switcher over fake windows and a real <see cref="ThemeManager"/> on the harness's application, whose
/// palette each test takes out again so the harness's own stays in charge.</summary>
[Trait("Category", "UI")]
public class LookSwitcherTests
{
    /// <summary>A shell window that only remembers what was done to it, and says so in <paramref name="events"/>.</summary>
    private sealed class FakeWindow(Look look, List<string> events, bool showFails = false) : IShellWindow
    {
        private Window? _window;

        public Look Look { get; } = look;

        public Rect Bounds { get; set; }

        public WindowState State { get; set; }

        public Page Page { get; set; }

        public bool ClosedForSwitch { get; private set; }

        public event EventHandler? Closed;

        public Window Window => _window ??= new Window();

        public void Show()
        {
            if (showFails) throw new InvalidOperationException("the window would not show");
            events.Add($"show {Look}");
        }

        public void CloseForSwitch()
        {
            ClosedForSwitch = true;
            events.Add($"close {Look}");
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A shell window over a real <see cref="ShellViewModel"/>, which maps the page it is given as the look's
    /// window does: Midnight has no Now and Classic no Dashboard, and Midnight's window moves the shell off Now as it
    /// opens, as MidnightWindow does.</summary>
    private sealed class ShellWindowFake : IShellWindow
    {
        private readonly ShellViewModel _shell;
        private readonly bool _showFails;
        private Window? _window;

        public ShellWindowFake(Look look, ShellViewModel shell, bool showFails)
        {
            Look = look;
            _shell = shell;
            _showFails = showFails;
            if (look == Look.Midnight && shell.Page == Page.Now) shell.Page = Page.Dashboard;
        }

        public Look Look { get; }

        public Rect Bounds { get; set; }

        public WindowState State { get; set; }

        public Page Page
        {
            get => _shell.Page;
            set => _shell.Page = (Look, value) switch
            {
                (Look.Midnight, Page.Now) => Page.Dashboard,
                (Look.Classic, Page.Dashboard) => Page.Now,
                _ => value,
            };
        }

        public event EventHandler? Closed;

        public Window Window => _window ??= new Window();

        public void Show()
        {
            if (_showFails) throw new InvalidOperationException("the window would not show");
        }

        public void CloseForSwitch() => Closed?.Invoke(this, EventArgs.Empty);
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 0, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private readonly List<string> _events = [];
    private readonly List<string> _log = [];
    private readonly List<FakeWindow> _opened = [];
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeRangeHistory _history = new();

    /// <summary>Runs <paramref name="test"/> with a manager on the harness's application, loading palettes through
    /// <paramref name="load"/> when it is given.</summary>
    private static void WithTheme(Action<ThemeManager, Collection<ResourceDictionary>> test, Func<Uri, ResourceDictionary>? load = null)
        => UiHarness.OnUi(() =>
        {
            var merged = Application.Current.Resources.MergedDictionaries;
            using var theme = new ThemeManager(Application.Current, ThemeChoice.Dark, Look.Classic, load);
            try
            {
                test(theme, merged);
            }
            finally
            {
                merged.RemoveAt(0);   // the manager's palette; the harness keeps its own
            }
        });

    private LookSwitcher Switcher(ThemeManager theme, Func<Look, IShellWindow>? open = null) => new(
        open ?? (look =>
        {
            var window = new FakeWindow(look, _events);
            _opened.Add(window);
            return window;
        }),
        theme, window => _events.Add($"retarget {((FakeWindow)window).Look}"), _log.Add);

    /// <summary>The App's one shell, with Midnight's Dashboard, over <see cref="_history"/> and <see cref="_clock"/>.</summary>
    private ShellViewModel Shell()
    {
        var link = new FakeLink();
        var ui = new FakeUiSettings();
        var machine = new FakeMachineHistory();
        var households = new FakeHouseholdHistory();
        var now = new NowViewModel(new FakeLink(), new FakeHistory(), UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4, () => { });
        return new ShellViewModel(
            now,
            new BreakdownViewModel(link, _history, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English),
            new ReportViewModel(link, _history, households, new FakeSleep(), new FakeSaver(), _ => [], UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, 0.4),
            new HouseholdViewModel(link, households, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, FakeAccount.Model(link)),
            new SettingsViewModel(link, machine, ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "USD"),
            new WizardViewModel(link, machine, ui, UiThreads.Inline, _clock, TimeZoneInfo.Utc, English, "USD"),
            "0.1.0",
            dashboard: new DashboardViewModel(now, _history, new FakeHistory(), _clock, TimeZoneInfo.Utc, English, UiThreads.Inline));
    }

    /// <summary>Review round: a window that builds but won't show has already moved the shared shell to its own look's
    /// page (Midnight's window takes Now to the Dashboard as it opens; Classic's, the Dashboard to Now). The shell goes back
    /// to the page it was on, so the old window doesn't show a page it has no view for and the Dashboard stops reading.</summary>
    [Theory]
    [InlineData("Classic", "Now", "Midnight")]
    [InlineData("Midnight", "Dashboard", "Classic")]
    public void A_window_that_will_not_show_leaves_the_shell_on_the_page_it_was_on(string fromLook, string onPage, string toLook)
        => WithTheme((theme, _) =>
        {
            var (from, page, to) = (Enum.Parse<Look>(fromLook), Enum.Parse<Page>(onPage), Enum.Parse<Look>(toLook));
            var shell = Shell();
            theme.Apply(from);
            var looks = Switcher(theme, look => new ShellWindowFake(look, shell, showFails: look == to));
            looks.Current.Page = page;
            shell.Page.ShouldBe(page);

            looks.Switch(to).ShouldBe("Couldn't open the " + to + " look: the window would not show");

            shell.Page.ShouldBe(page);
            shell.Current.ShouldBe(page == Page.Now ? shell.Now : shell.Dashboard);
            var read = _history.Reads.Count;
            _clock.Advance(DashboardViewModel.RefreshEvery * 3);
            (_history.Reads.Count > read).ShouldBe(page == Page.Dashboard);
        });

    /// <summary>Review round: a palette that won't load is a look that won't open, like a window that won't: the old
    /// look stays, with its palette and its window, nothing new opens, and the reason comes back.</summary>
    [Fact]
    public void A_palette_that_will_not_load_leaves_the_old_look_and_opens_nothing()
        => WithTheme((theme, merged) =>
        {
            var classic = LookRules.PaletteFor(Look.Classic, Theme.Dark);
            var looks = Switcher(theme);
            var first = looks.Current;

            looks.Switch(Look.Midnight).ShouldBe("Couldn't open the Midnight look: no palette");

            looks.Current.ShouldBeSameAs(first);
            (looks.Look, theme.Look).ShouldBe((Look.Classic, Look.Classic));
            merged[0].Source.ShouldBe(classic);
            _opened.Count.ShouldBe(1);
            _events.ShouldBeEmpty();
            _log.Single().ShouldContain("no palette");

            var unopened = Switcher(theme);
            unopened.Switch(Look.Midnight).ShouldBe("Couldn't open the Midnight look: no palette");
            (unopened.IsOpen, unopened.Look).ShouldBe((false, Look.Classic));
        }, source => source == LookRules.PaletteFor(Look.Midnight, Theme.Dark)
            ? throw new IOException("no palette")
            : new ResourceDictionary { Source = source });

    [Fact]
    public void A_switch_opens_the_other_look_at_the_same_bounds_state_and_page_shows_it_retargets_then_closes_the_old()
        => WithTheme((theme, merged) =>
        {
            var looks = Switcher(theme);
            looks.IsOpen.ShouldBeFalse();
            var first = (FakeWindow)looks.Current;
            looks.IsOpen.ShouldBeTrue();
            first.Look.ShouldBe(Look.Classic);
            first.Bounds = new Rect(10, 20, 1000, 700);
            first.State = WindowState.Maximized;
            first.Page = Page.Report;

            looks.Switch(Look.Midnight).ShouldBeNull();

            var second = (FakeWindow)looks.Current;
            second.ShouldNotBeSameAs(first);
            second.Look.ShouldBe(Look.Midnight);
            second.Bounds.ShouldBe(new Rect(10, 20, 1000, 700));
            second.State.ShouldBe(WindowState.Maximized);
            second.Page.ShouldBe(Page.Report);
            _events.ShouldBe(["show Midnight", "retarget Midnight", "close Classic"]);
            looks.Look.ShouldBe(Look.Midnight);
            merged[0].Source.ShouldBe(LookRules.PaletteFor(Look.Midnight, Theme.Dark));
            _log.ShouldBeEmpty();

            looks.Switch(Look.Midnight).ShouldBeNull();   // already there: nothing opens, nothing closes
            _opened.Count.ShouldBe(2);
            _events.Count.ShouldBe(3);
        });

    [Fact]
    public void A_look_whose_window_will_not_open_leaves_the_old_window_and_its_palette_and_says_why()
        => WithTheme((theme, merged) =>
        {
            var looks = Switcher(theme, look => look == Look.Midnight ? throw new InvalidOperationException("no XAML") : new FakeWindow(look, _events));
            var first = (FakeWindow)looks.Current;

            looks.Switch(Look.Midnight).ShouldBe("Couldn't open the Midnight look: no XAML");

            looks.Current.ShouldBeSameAs(first);
            first.ClosedForSwitch.ShouldBeFalse();
            looks.Look.ShouldBe(Look.Classic);
            merged[0].Source.ShouldBe(LookRules.PaletteFor(Look.Classic, Theme.Dark));
            _events.ShouldBeEmpty();
            _log.Single().ShouldContain("no XAML");
        });

    [Fact]
    public void A_window_that_opens_but_will_not_show_is_closed_again_and_the_old_one_stays()
        => WithTheme((theme, merged) =>
        {
            var looks = Switcher(theme, look => look == Look.Midnight ? new FakeWindow(look, _events, showFails: true) : new FakeWindow(look, _events));
            var first = looks.Current;

            looks.Switch(Look.Midnight).ShouldBe("Couldn't open the Midnight look: the window would not show");

            looks.Current.ShouldBeSameAs(first);
            _events.ShouldBe(["close Midnight"]);
            merged[0].Source.ShouldBe(LookRules.PaletteFor(Look.Classic, Theme.Dark));
        });

    [Fact]
    public void Before_any_window_has_opened_a_switch_only_changes_the_look_the_first_window_opens_in()
        => WithTheme((theme, merged) =>
        {
            var looks = Switcher(theme);

            looks.Switch(Look.Midnight).ShouldBeNull();

            looks.IsOpen.ShouldBeFalse();
            looks.Look.ShouldBe(Look.Midnight);
            merged[0].Source.ShouldBe(LookRules.PaletteFor(Look.Midnight, Theme.Dark));
            ((FakeWindow)looks.Current).Look.ShouldBe(Look.Midnight);
            _events.ShouldBeEmpty();   // opened, not shown: the App shows it
        });
}
