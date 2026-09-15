using Microsoft.Win32;
using Shouldly;

namespace PowerLedger.App.Tests;

public sealed class TrayTests : IDisposable
{
    private readonly string _key = $@"Software\PowerLedger.Tests.{Guid.NewGuid():N}";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);

    [Theory]
    [InlineData(null, "–")]
    [InlineData(34.4, "34")]
    [InlineData(999.4, "999")]
    [InlineData(1499.0, "1k")]
    [InlineData(2600.0, "3k")]
    public void The_icon_shows_whole_watts_and_thousands_as_k(double? watts, string expected)
        => TrayGlyph.Text(TrayGlyph.Round(watts)).ShouldBe(expected);

    [Fact]
    public void Start_with_windows_writes_and_removes_the_run_entry()
    {
        var autostart = new StartWithWindows(@"C:\Program Files\PowerLedger\PowerLedger.exe", _key);
        autostart.IsEnabled.ShouldBeFalse();

        autostart.Set(true);
        autostart.IsEnabled.ShouldBeTrue();
        using (var key = Registry.CurrentUser.OpenSubKey(_key)!)
        {
            key.GetValue("PowerLedger").ShouldBe("\"C:\\Program Files\\PowerLedger\\PowerLedger.exe\" --tray");
        }

        autostart.Set(false);
        autostart.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task A_second_start_finds_the_first_and_asks_it_to_show_itself()
    {
        var name = $"PowerLedger.test.{Guid.NewGuid():N}";
        using var first = new SingleInstance(name);
        using var second = new SingleInstance(name);
        first.IsFirst.ShouldBeTrue();
        second.IsFirst.ShouldBeFalse();

        var shown = 0;
        first.OnShowRequested(() => Interlocked.Increment(ref shown));
        second.SignalFirst();
        await WaitFor.True(() => Volatile.Read(ref shown) == 1);
    }
}
