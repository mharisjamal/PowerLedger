using PowerLedger.Service.Households;
using Shouldly;

namespace PowerLedger.Service.Tests;

/// <summary>What households take in from other PCs, checked on the way in (plan 0.6, 0.8).</summary>
public sealed class WireTests
{
    [Theory]
    [InlineData("  Study PC  ", "Study PC")]
    [InlineData("Laptop\u0007-2\r\n", "Laptop-2")]                          // control characters
    [InlineData("Laptop-2\u202Egnp.exe", "Laptop-2gnp.exe")]                 // right-to-left override: a name that reads backwards
    [InlineData("\u200BDesk\u200Dtop\u2066-7\u2069\uFEFF", "Desktop-7")]      // zero-width and isolate marks
    [InlineData("Line\u2028two\u2029", "Linetwo")]                          // line and paragraph separators
    [InlineData("Salle de séjour 🙂", "Salle de séjour 🙂")]                 // letters and emoji stay
    public void A_name_loses_what_could_hide_or_reorder_text_and_keeps_the_rest(string given, string kept)
    {
        Wire.Name(given).ShouldBe(kept);
    }

    [Fact]
    public void A_name_is_cleaned_by_unicode_scalar_so_tags_and_invisible_formatting_beyond_the_first_plane_go_too()
    {
        Wire.Name("Study" + "\uDB40\uDC41\uDB40\uDC7F" + " PC").ShouldBe("Study PC");   // tag characters, U+E0041 and U+E007F
        Wire.Name("Desk" + "\uD834\uDD73" + "top").ShouldBe("Desktop");   // musical formatting, U+1D173
        Wire.Name("Lap" + "\uD800" + "top" + "\uE000").ShouldBe("Laptop");   // a lone surrogate, private use
    }

    [Fact]
    public void A_long_name_is_cut_at_40_units_never_between_the_halves_of_a_pair()
    {
        var name = new string('x', Wire.MaxName - 1) + "\uD83D\uDE42" + "yz";   // the emoji's first half is the 40th unit

        Wire.Name(name).ShouldBe(new string('x', Wire.MaxName - 1));
        Wire.Name(new string('x', Wire.MaxName - 2) + "\uD83D\uDE42").ShouldBe(new string('x', Wire.MaxName - 2) + "\uD83D\uDE42");
    }

    [Fact]
    public void A_name_of_nothing_but_hidden_characters_is_no_name()
    {
        Wire.Name("\u202E\u200B \u2066").ShouldBeNull();
        Wire.Name(new string('x', 50)).ShouldBe(new string('x', Wire.MaxName));
    }
}
