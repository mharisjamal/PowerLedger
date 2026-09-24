using System.Collections.ObjectModel;
using System.Windows;
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

    private readonly List<string> _events = [];
    private readonly List<string> _log = [];
    private readonly List<FakeWindow> _opened = [];

    private static void WithTheme(Action<ThemeManager, Collection<ResourceDictionary>> test) => UiHarness.OnUi(() =>
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        using var theme = new ThemeManager(Application.Current, ThemeChoice.Dark);
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
