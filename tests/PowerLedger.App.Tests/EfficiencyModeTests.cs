using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Efficiency mode for the App while none of its windows shows, but never before the first window has painted or a
/// start in the tray has settled: asked for at start, it slowed the first window's first paint.</summary>
public class EfficiencyModeTests
{
    private readonly List<bool> _asked = [];

    [Fact]
    public void Nothing_is_asked_before_the_first_window_shows_and_after_that_it_follows_the_windows()
    {
        var mode = new EfficiencyMode(_asked.Add);
        mode.Windows(shows: false);                  // the watch's first look, before the window is laid out
        _asked.ShouldBeEmpty();
        mode.Windows(shows: true);
        _asked.ShouldBeEmpty("never asked for, so nothing to hand back");

        mode.Windows(shows: false);
        mode.Windows(shows: false);
        _asked.ShouldBe([true]);
        mode.Windows(shows: true);
        _asked.ShouldBe([true, false]);
        mode.Settled();
        _asked.ShouldBe([true, false]);
    }

    [Fact]
    public void A_start_in_the_tray_is_efficient_once_it_has_settled()
    {
        var mode = new EfficiencyMode(_asked.Add);
        mode.Windows(shows: false);
        _asked.ShouldBeEmpty();
        mode.Settled();
        _asked.ShouldBe([true]);
        mode.Windows(shows: true);
        _asked.ShouldBe([true, false]);
    }

    [Fact]
    public void A_window_shown_before_the_start_settled_keeps_the_app_out_of_efficiency_mode()
    {
        var mode = new EfficiencyMode(_asked.Add);
        mode.Windows(shows: true);
        mode.Settled();
        _asked.ShouldBeEmpty();
    }
}
