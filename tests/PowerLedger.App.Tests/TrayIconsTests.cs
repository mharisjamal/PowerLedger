using System.Drawing;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Perf review P2: the tray redrew its icon in 41 % of seconds, each time fitting its font again by drawing and scanning
/// the ten digits at every size tried. A fit is now worked out once for its font, slots and size, and a picture is drawn
/// once and kept while its value keeps coming back.
/// </summary>
[Trait("Category", "UI")]
public class TrayIconsTests
{
    [Fact]
    public void A_fit_is_worked_out_once_for_its_font_slots_and_size_and_forgotten_on_a_change_of_scale()
    {
        const int size = 37;   // no other test draws at this size, so its fits are this test's alone
        TrayArt.ClearFits();

        TrayArt.DrawIcon("12", size).Dispose();
        TrayArt.DrawIcon("34", size).Dispose();
        TrayArt.DrawIcon("7", size).Dispose();
        TrayArt.FitsKept(size).ShouldBe(1, "one or two digits share one fit");

        TrayArt.DrawIcon("123", size).Dispose();
        TrayArt.DrawIcon("2k", size).Dispose();
        TrayArt.FitsKept(size).ShouldBe(2, "three digits and thousands have their own");

        TrayArt.ClearFits();
        TrayArt.FitsKept(size).ShouldBe(0);
    }

    [Fact]
    public void A_kept_fit_draws_the_same_picture_as_one_worked_out_afresh()
    {
        const int size = 39;
        TrayArt.ClearFits();
        using var fresh = TrayArt.Draw("48", size);
        using var kept = TrayArt.Draw("48", size);

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
            kept.GetPixel(x, y).ShouldBe(fresh.GetPixel(x, y));
    }

    [Fact]
    public void A_picture_is_drawn_when_first_needed_and_kept_while_its_value_comes_back()
    {
        var drawn = new List<string?>();
        using var icons = new TrayIcons((text, size) =>
        {
            drawn.Add(text);
            return TrayArt.DrawIcon(text, size);
        }, capacity: 3);

        var twelve = icons.Get("12", 20);
        icons.Get("13", 20);
        icons.Get("12", 20).ShouldBeSameAs(twelve);
        icons.Get(null, 20);
        drawn.ShouldBe(["12", "13", null]);

        icons.Get("14", 20);   // past three, the least recent goes: 13
        icons.Count.ShouldBe(3);
        icons.Get("12", 20).ShouldBeSameAs(twelve);
        icons.Get("13", 20);
        drawn.ShouldBe(["12", "13", null, "14", "13"]);

        icons.Get("12", 24).ShouldNotBeSameAs(twelve, "a new scale draws its own");
    }

    [Fact]
    public void A_picture_pushed_out_is_freed_and_the_one_shown_never_is()
    {
        var all = new List<Icon>();
        var icons = new TrayIcons((text, size) =>
        {
            var icon = TrayArt.DrawIcon(text, size);
            all.Add(icon);
            return icon;
        }, capacity: 2);

        Icon shown = null!;
        foreach (var watts in new[] { "10", "11", "12", "11", "13" }) shown = icons.Get(watts, 20);

        Freed(all[0]).ShouldBeTrue("10 went when 12 came");
        Freed(all[1]).ShouldBeFalse("11 came back, so 12 went when 13 came");
        Freed(all[2]).ShouldBeTrue();
        Freed(shown).ShouldBeFalse();
        icons.Dispose();
        all.ShouldAllBe(icon => Freed(icon));
    }

    /// <summary>Whether <paramref name="icon"/> has been disposed: its handle is gone, and asking for it throws.</summary>
    private static bool Freed(Icon icon)
    {
        try
        {
            _ = icon.Handle;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
