using System.Windows;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Perf review P1: whether any of the App's windows shows, which the live panel, the polling and the App's power
/// throttling follow, and whether any is visible, which the service hears.</summary>
[Trait("Category", "UI")]
public class WindowWatchTests
{
    private static Window OffScreen() => new()
    {
        Width = 200, Height = 120, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
    };

    [Fact]
    public void A_window_shows_while_visible_and_not_minimised_and_counts_until_it_closes()
        => UiHarness.OnUi(() =>
        {
            var watch = new WindowWatch();
            var changes = 0;
            watch.Changed += () => changes++;
            var shell = OffScreen();
            var overlay = OffScreen();
            var open = new HashSet<Window> { shell, overlay };
            shell.Closed += (_, _) => open.Remove(shell);
            overlay.Closed += (_, _) => open.Remove(overlay);
            try
            {
                watch.Watch(shell);
                watch.Watch(overlay);
                watch.Shows.ShouldBeFalse("nothing has shown yet");
                watch.Visible.ShouldBeFalse();

                shell.Show();
                watch.Shows.ShouldBeTrue();
                watch.Visible.ShouldBeTrue();
                changes.ShouldBe(1);

                shell.WindowState = WindowState.Minimized;
                watch.Shows.ShouldBeFalse("a minimised window shows nothing");
                watch.Visible.ShouldBeTrue("but it is still there to install an update under");

                overlay.Show();
                watch.Shows.ShouldBeTrue("the overlay shows");

                shell.Hide();
                overlay.Close();
                watch.Shows.ShouldBeFalse();
                watch.Visible.ShouldBeFalse();
                watch.Watch(shell);   // watched already: no second count
                shell.WindowState = WindowState.Normal;
                shell.Show();
                watch.Shows.ShouldBeTrue();
                changes.ShouldBe(5);
            }
            finally
            {
                foreach (var window in open.ToList()) window.Close();
            }
            watch.Shows.ShouldBeFalse("closed, it is watched no more");
        });

    [Fact]
    public void Every_window_shown_is_watched_without_being_handed_in()
        => UiHarness.OnUi(() =>
        {
            var watch = WindowWatch.EveryWindow();
            var window = OffScreen();
            try
            {
                window.Show();   // a plain window, with no Loaded handler of its own
                UiHarness.PumpUntil(() => watch.Shows, TimeSpan.FromSeconds(5), "the watch to see the window");
                window.Hide();
                watch.Shows.ShouldBeFalse();
            }
            finally
            {
                window.Close();
            }
        });
}
