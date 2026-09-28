using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan S Task 0: Aero's window as the look switcher and the App see it, before agent D draws its shell. It takes the
/// bounds a switch carries rather than fitting itself to the screen, shows its own page for Classic's Now and every page
/// Aero has as it is, moves the shell off Now as it shows, draws on the Aero palette put on it, and a close for the switch
/// closes it.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]   // its window's glass sets the reduce-motion override, one for the whole process
public class AeroWindowTests
{
    [Fact]
    public void Aeros_window_takes_the_bounds_and_page_a_switch_carries_and_closes_for_it()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = MidnightFixtures.Shell(saver);
            var aero = AeroHost.Window(shell);
            IShellWindow window = aero;
            window.Window.ShouldBeSameAs(aero);
            var closed = 0;
            window.Closed += (_, _) => closed++;

            window.Bounds = new Rect(-20000, 10, 1000, 700);
            window.Page = Page.Now;
            shell.Page.ShouldBe(Page.Dashboard, "Classic's Now arrives as the Dashboard");
            foreach (var own in new[] { Page.Parts, Page.Insights, Page.Breakdown, Page.Report, Page.Household, Page.Settings, Page.Dashboard })
            {
                window.Page = own;
                shell.Page.ShouldBe(own, $"Aero has a {own} page");
            }
            window.Page = Page.Insights;
            try
            {
                window.Show();
                aero.UpdateLayout();
                foreach (var bounds in new[] { new Rect(aero.Left, aero.Top, aero.ActualWidth, aero.ActualHeight), window.Bounds })
                {
                    bounds.X.ShouldBe(-20000, 1);
                    bounds.Y.ShouldBe(10, 1);
                    bounds.Width.ShouldBe(1000, 1);
                    bounds.Height.ShouldBe(700, 1);
                }
                window.State.ShouldBe(WindowState.Normal);
                window.Page.ShouldBe(Page.Insights);
            }
            finally
            {
                window.CloseForSwitch();
            }
            closed.ShouldBe(1);
        });

    /// <summary>As MidnightWindow: a shell left on Classic's Now moves to the Dashboard once the window shows, and while
    /// it shows; before it shows, nothing in the shell changes, so a switch whose window fails to show can put it back.</summary>
    [Fact]
    public void Shown_it_moves_the_shell_off_classics_now_and_keeps_it_off()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = MidnightFixtures.Shell(saver);
            shell.Page = Page.Now;
            var window = AeroHost.Window(shell);
            shell.Page.ShouldBe(Page.Now, "nothing changes before the window shows");
            try
            {
                window.Show();
                shell.Page.ShouldBe(Page.Dashboard);

                shell.Page = Page.Now;

                shell.Page.ShouldBe(Page.Dashboard);
            }
            finally
            {
                window.CloseForSwitch();
            }

            shell.Page = Page.Now;
            shell.Page.ShouldBe(Page.Now, "a closed window no longer follows the shell");
        });

    /// <summary>Review 11: the window draws on the Aero palette put on it, in each theme, whatever the application's is:
    /// its ground is the palette's plain ground where the backdrop is plain, and the palette's wash over the desktop where
    /// Windows can show the desktop through it (Plan S G4).</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void It_draws_on_the_aero_palettes_ground(string themeName)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            using var saver = new FakeSaver();
            var window = AeroHost.Window(MidnightFixtures.Shell(saver), theme);
            window.Width = 960;
            window.Height = 640;
            try
            {
                window.Show();
                window.UpdateLayout();
                var palette = ThemeManager.Palette(Look.Aero, theme);
                var room = (Border)window.FindName("Room");
                if (window.BackdropKind == Aero.BackdropKind.SeeThrough)
                    ((SolidColorBrush)room.Background).Color.ShouldBe((Color)palette["A.C.SeeThroughWash"], $"Aero's {theme} wash over the desktop");
                else
                    MidnightHost.PixelOf(window, 960, 640, 2, 2).ShouldBe((Color)palette["A.C.Plain"], $"Aero's {theme} plain ground");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });
}
