using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// The harness the UI tests share. A pump used to run whatever other tests had queued for the one UI thread, so their
/// windows took this test's keyboard focus and swapped the palette under its renders; the tests waiting their turn held
/// thread-pool threads in a wait the pool couldn't see, so everything else async in the process (a pipe accept, a
/// sign-in's continuation, WaitFor's own timer) queued behind them for seconds.
/// </summary>
[Trait("Category", "UI")]
public class UiHarnessTests
{
    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [Fact]
    public async Task Another_tests_ui_work_waits_until_this_ones_is_done_even_while_it_pumps()
    {
        var order = new ConcurrentQueue<string>();
        using var pumping = new ManualResetEventSlim();
        var secondAsked = 0;
        var first = Task.Run(() => UiHarness.OnUi(() =>
        {
            pumping.Set();
            UiHarness.PumpUntil(() => Volatile.Read(ref secondAsked) == 1, TimeSpan.FromSeconds(10), "the second test to ask");
            UiHarness.Pump(TimeSpan.FromMilliseconds(300));   // long enough for work already queued to have run in this pump
            order.Enqueue("first done");
        }));
        pumping.Wait();
        var second = Task.Run(() =>
        {
            Volatile.Write(ref secondAsked, 1);
            UiHarness.OnUi(() => order.Enqueue("second ran"));
        });
        await Task.WhenAll(first, second);
        order.ShouldBe(["first done", "second ran"]);
    }

    [Fact]
    public void Ui_work_that_asks_for_the_ui_thread_again_runs_at_once()
        => UiHarness.OnUi(() => UiHarness.OnUi(() => 42)).ShouldBe(42);

    /// <summary>Four times as many tests waiting for the UI thread as the pool keeps threads for, and
    /// work queued to the pool after them still runs at once rather than after the pool has slowly grown past them.</summary>
    [Fact]
    public async Task Tests_waiting_their_turn_leave_the_thread_pool_free_for_everything_else()
    {
        using var release = new ManualResetEventSlim();
        using var holding = new ManualResetEventSlim();
        var holder = Task.Run(() => UiHarness.OnUi(() =>
        {
            holding.Set();
            UiHarness.PumpUntil(() => release.IsSet, TimeSpan.FromSeconds(30), "the probe to run");
        }));
        holding.Wait();
        ThreadPool.GetMinThreads(out var minimum, out _);
        var waiting = Enumerable.Range(0, 4 * minimum).Select(_ => Task.Run(() => UiHarness.OnUi(() => { }))).ToList();
        await Task.Delay(200);   // the waiters have taken their threads
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ran = await Task.Run(() => watch.ElapsedMilliseconds);
        release.Set();
        await holder;
        await Task.WhenAll(waiting);
        ran.ShouldBeLessThan(250, "a pool work item queued behind the waiting tests");
    }

    /// <summary>What happens to a test window when another process takes the foreground, as a second test run or the
    /// person at the PC does: Windows takes the Win32 focus from it, WPF drops the keyboard focus, and the window keeps
    /// the element that had it as its focused element, which it gives the focus back to when next active. So a test
    /// checks where the product put the focus with <see cref="UiHarness.HasFocus"/>; IsKeyboardFocused alone says as much
    /// about the desktop as about the product.</summary>
    [Fact]
    public void Losing_the_win32_focus_drops_the_keyboard_focus_and_the_window_keeps_its_focused_element()
        => UiHarness.OnUi(() =>
        {
            var button = new Button { Content = "Focus" };
            var other = new Button { Content = "Other" };
            var window = new Window
            {
                Content = new StackPanel { Children = { button, other } }, Width = 120, Height = 80, WindowStyle = WindowStyle.None,
                ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
            };
            window.Show();
            try
            {
                button.Focus().ShouldBeTrue();
                button.IsKeyboardFocused.ShouldBeTrue();
                UiHarness.HasFocus(button).ShouldBeTrue();
                UiHarness.HasFocus(other).ShouldBeFalse();
                SetFocus(IntPtr.Zero);   // what a foreground change elsewhere does to this thread's focus
                UiHarness.Pump(TimeSpan.FromMilliseconds(50));
                button.IsKeyboardFocused.ShouldBeFalse();
                FocusManager.GetFocusedElement(window).ShouldBeSameAs(button);
                UiHarness.HasFocus(button).ShouldBeTrue("the focus the product gave it, which comes back with the foreground");
                UiHarness.HasFocus(other).ShouldBeFalse();
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void Pump_until_returns_once_the_condition_holds_and_otherwise_says_what_never_came()
        => UiHarness.OnUi(() =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var at = watch.ElapsedMilliseconds + 150;
            UiHarness.PumpUntil(() => watch.ElapsedMilliseconds >= at, TimeSpan.FromSeconds(10), "the moment");
            watch.ElapsedMilliseconds.ShouldBeLessThan(5000);
            Should.Throw<TimeoutException>(() => UiHarness.PumpUntil(() => false, TimeSpan.FromMilliseconds(100), "nothing"))
                .Message.ShouldContain("nothing");
        });
}
