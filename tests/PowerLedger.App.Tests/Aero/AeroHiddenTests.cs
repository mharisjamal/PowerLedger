using PowerLedger.App.Aero;
using Shouldly;
using AeroDashboard = PowerLedger.App.Aero.DashboardView;

namespace PowerLedger.App.Tests;

/// <summary>
/// Perf review P1: hidden to the tray, the Aero window and its pages drew every reading nobody saw. The overlay keeps the
/// live panel going on its own, so readings still reach a hidden window; it leaves them be and shows the newest when it
/// shows again.
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]   // AeroMotion's override is one for the process
public class AeroHiddenTests
{
    [Fact]
    public void Hidden_the_window_and_its_dashboard_leave_readings_be_and_show_the_newest_when_shown_again()
        => UiHarness.OnUi(() =>
        {
            using var saver = new FakeSaver();
            var shell = AeroFixtures.Shell(saver, out var link);
            var window = AeroHost.Window(shell);
            using var motion = AeroMotion.Force(false);
            window.Width = 1440;
            window.Height = 900;
            window.IsVisibleChanged += (_, _) => shell.IsShown = window.IsVisible;   // as the App has it
            window.Show();
            try
            {
                UiHarness.PumpUntil(() => !window.IntroPending, TimeSpan.FromSeconds(10), "the intro to begin");
                var view = (AeroDashboard)window.PageHost.Showing!;
                var roll = UiHarness.Find<RollingNumber>(view, r => r.Name == "NowRoll")!;
                UiHarness.PumpUntil(() => view.Count == 1, TimeSpan.FromSeconds(20), "the count-up to end");
                window.Hide();
                var pcs = window.Pcs.ItemsSource;
                var value = roll.Value;

                link.Push(Frames.At(MidnightFixtures.Now.AddSeconds(1), totalW: 97.4));
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));

                window.Pcs.ItemsSource.ShouldBeSameAs(pcs, "hidden, Your PCs is not drawn again");
                roll.Value.ShouldBe(value, "nor is Power now");

                window.Show();
                UiHarness.PumpUntil(() => roll.Value == 97, TimeSpan.FromSeconds(10), "Power now to show the newest reading");
                ((IEnumerable<PcRow>)window.Pcs.ItemsSource).First().Figure.ShouldBe("97 W");
            }
            finally
            {
                window.CloseForSwitch();
            }
        });
}
