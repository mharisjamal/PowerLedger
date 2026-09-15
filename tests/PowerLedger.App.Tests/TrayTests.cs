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

    [Fact]
    public async Task The_installer_can_ask_a_running_app_to_exit()
    {
        var name = $"PowerLedger.test.{Guid.NewGuid():N}";
        using var app = new SingleInstance(name);
        var exits = 0;
        app.OnExitRequested(() => Interlocked.Increment(ref exits));

        AskToExit(name);
        await WaitFor.True(() => Volatile.Read(ref exits) == 1, timeoutMs: 2000);
    }

    [Fact]
    public async Task An_exit_asked_for_before_the_app_listens_is_not_lost()
    {
        var name = $"PowerLedger.test.{Guid.NewGuid():N}";
        using var app = new SingleInstance(name);
        AskToExit(name);

        var exits = 0;
        app.OnExitRequested(() => Interlocked.Increment(ref exits));
        await WaitFor.True(() => Volatile.Read(ref exits) == 1, timeoutMs: 2000);
    }

    [Fact]
    public async Task The_exit_runs_once_even_when_asked_again()
    {
        var name = $"PowerLedger.test.{Guid.NewGuid():N}";
        using var app = new SingleInstance(name);
        var exits = 0;
        app.OnExitRequested(() => Interlocked.Increment(ref exits));

        AskToExit(name);
        await WaitFor.True(() => Volatile.Read(ref exits) == 1, timeoutMs: 2000);
        AskToExit(name);   // an installer retrying finds the App already on its way out
        await Task.Delay(250);
        Volatile.Read(ref exits).ShouldBe(1);
    }

    /// <summary>What the installer does: opens the App's exit event by name, sets it, and lets go of it.</summary>
    private static void AskToExit(string name)
    {
        using var exit = EventWaitHandle.OpenExisting($@"Local\{name}.Exit");
        exit.Set();
    }
}
