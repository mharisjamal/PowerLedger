using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Plan AE review item 7: the window in front is booked to the app it shows. A Store app's window is framed by Windows'
/// ApplicationFrameHost.exe, which owns the window in front; the app's own CoreWindow is a child of that frame.
/// </summary>
public class ForegroundAppTests
{
    private const nint Window = 100;
    private const string Host = @"C:\Windows\System32\ApplicationFrameHost.exe";

    private static uint? Owner(Dictionary<nint, uint> processes, Dictionary<uint, string> images, params (nint Child, string Class)[] children)
        => ForegroundApp.Owner(Window, w => processes.GetValueOrDefault(w), pid => images.GetValueOrDefault(pid), _ => children);

    [Fact]
    public void An_ordinary_window_is_its_own_processs()
        => Owner(new() { [Window] = 42 }, new() { [42] = @"C:\Program Files\Google\Chrome\Application\chrome.exe" }).ShouldBe(42u);

    [Fact]
    public void A_store_apps_window_is_the_app_inside_its_frame()
        => Owner(new() { [Window] = 7, [201] = 7, [202] = 99 }, new() { [7] = Host, [99] = @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator\CalculatorApp.exe" },
                (201, "ApplicationFrameTitleBarWindow"), (202, ForegroundApp.CoreWindowClass))
            .ShouldBe(99u);

    [Fact]
    public void A_frame_with_no_app_inside_is_the_frames_own()
        => Owner(new() { [Window] = 7, [201] = 7 }, new() { [7] = Host }, (201, "ApplicationFrameTitleBarWindow")).ShouldBe(7u);

    [Fact]
    public void A_core_window_of_the_frames_own_is_passed_over()
        => Owner(new() { [Window] = 7, [202] = 7, [203] = 55 }, new() { [7] = Host.ToLowerInvariant() },
                (202, ForegroundApp.CoreWindowClass), (203, ForegroundApp.CoreWindowClass))
            .ShouldBe(55u);

    [Fact]
    public void Only_the_frame_host_is_looked_inside()
        => Owner(new() { [Window] = 42, [202] = 99 }, new() { [42] = @"C:\Tools\host.exe" }, (202, ForegroundApp.CoreWindowClass)).ShouldBe(42u);

    [Fact]
    public void No_window_or_no_process_is_none()
    {
        ForegroundApp.Owner(0, _ => 42, _ => null, _ => []).ShouldBeNull();
        Owner([], []).ShouldBeNull();
    }

    /// <summary>The real thing reads whatever is in front now, or nothing in a session without a desktop, and never throws.</summary>
    [Fact]
    public void The_real_window_in_front_reads_without_throwing()
        => Should.NotThrow(() => new LastInputIdleSource().ForegroundProcessId());
}
