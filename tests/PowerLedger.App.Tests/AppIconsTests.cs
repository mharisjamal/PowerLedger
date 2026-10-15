using System.IO;
using System.Windows.Media;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Plan AE: programs' icons, read once from their files and kept, a glyph where there is none to read.</summary>
public class AppIconsTests
{
    private static readonly string Notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

    [Fact]
    public void A_programs_icon_is_read_from_its_file_frozen_at_the_size_asked()
    {
        var icon = AppIcons.Read(Notepad).ShouldNotBeNull();
        icon.IsFrozen.ShouldBeTrue("drawn by any thread");
        (icon.Width, icon.Height).ShouldBe((AppIcons.ReadSize, AppIcons.ReadSize));
    }

    [Fact]
    public void A_file_that_is_gone_or_on_a_network_share_has_no_icon_to_read()
    {
        AppIcons.Read(Path.Combine(Path.GetTempPath(), $"gone-{Guid.NewGuid():N}.exe")).ShouldBeNull();
        AppIcons.Read(@"\\nowhere.invalid\share\app.exe").ShouldBeNull();
        AppIcons.Read("//nowhere.invalid/share/app.exe").ShouldBeNull();
    }

    [Fact]
    public async Task Each_file_is_read_once_whatever_the_case_of_its_path()
    {
        var reads = 0;
        var icons = new AppIcons(_ =>
        {
            Interlocked.Increment(ref reads);
            return null;
        });

        var first = icons.For(@"C:\Apps\Tool.exe");
        icons.For(@"c:\apps\tool.exe").ShouldBeSameAs(first);
        (await first).ShouldBeNull();
        reads.ShouldBe(1);
    }

    [Fact]
    public async Task A_reader_that_throws_reads_as_no_icon()
    {
        var icons = new AppIcons(_ => throw new InvalidOperationException("broken file"));
        (await icons.For(@"c:\broken.exe")).ShouldBeNull();
    }

    [Fact]
    public void Every_glyph_has_a_figure_and_the_total_none()
    {
        foreach (var glyph in Enum.GetValues<AppGlyph>())
        {
            var figure = AppIcon.Figure(glyph);
            figure.IsFrozen.ShouldBeTrue(glyph.ToString());
            if (glyph == AppGlyph.Total) figure.Bounds.IsEmpty.ShouldBeTrue();
            else figure.Bounds.Width.ShouldBeGreaterThan(10, glyph.ToString());
        }
    }
}
