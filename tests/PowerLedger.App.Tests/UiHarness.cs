using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PowerLedger.App.Tests;

/// <summary>
/// The one WPF application the UI tests share, on an STA thread of its own for as long as the process lives, with the
/// App's styles and a palette of the test's choosing; and the small tools that draw a visual to a PNG under
/// <see cref="Folder"/>, find a control in a tree, and let the dispatcher run for a while.
/// </summary>
internal static class UiHarness
{
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), "powerledger-renders");
    private static readonly Lazy<Dispatcher> Ui = new(StartUi);
    private static ResourceDictionary? _palette;
    private static bool _working;
    private static Exception? _stray;
    private static readonly SemaphoreSlim Turn = new(1, 1);

    /// <summary>
    /// Runs <paramref name="work"/> on the application's thread, and throws here what it threw there. One test's work at
    /// a time: a test waits its turn here, not in the dispatcher's queue, where another test's pump would run it in the
    /// middle of that test (its windows took the keyboard focus, shut tooltips and menus and swapped the palette under
    /// the other's renders). Work already on the application's thread runs at once. A test waits on tasks, for its turn
    /// and for its work, as the thread pool adds a thread for a pool thread blocked on a task: blocked on
    /// Dispatcher.Invoke, the tests waiting for the one UI thread held the pool's threads, and the rest of the process
    /// (a pipe's accept, a sign-in's continuation, WaitFor's own timer) queued behind them for seconds.
    /// </summary>
    public static void OnUi(Action work)
    {
        if (Ui.Value.CheckAccess())
        {
            Run(work);
            return;
        }
        Turn.WaitAsync().Wait();
        try
        {
            Ui.Value.InvokeAsync(() => Run(work), DispatcherPriority.Send).Task.Wait();
        }
        finally
        {
            Turn.Release();
        }
    }

    private static void Run(Action work)
    {
        ExceptionDispatchInfo? failure = null;
        _working = true;
        try
        {
            if (_stray is { } stray) ExceptionDispatchInfo.Capture(stray).Throw();
            work();
        }
        catch (Exception error)
        {
            failure = ExceptionDispatchInfo.Capture(error);
        }
        finally
        {
            _working = false;
            _stray = null;
        }
        failure?.Throw();
    }

    /// <summary>Runs <paramref name="work"/> on the application's thread and brings back what it returns.</summary>
    public static T OnUi<T>(Func<T> work)
    {
        T result = default!;
        OnUi(() =>
        {
            result = work();   // a block, so this binds to the Action overload rather than to itself
        });
        return result;
    }

    /// <summary>Puts Classic's palette for <paramref name="theme"/> first among the application's dictionaries, where the App
    /// keeps it. Midnight's goes on the window a test draws instead (MidnightHost.Dressed, review 11): tests pump the one
    /// dispatcher, so a palette put here while one draws would come out in another's renders.</summary>
    public static void UseTheme(Theme theme) => UsePalette(ThemeManager.Palette(Look.Classic, theme));

    /// <summary>Hands every window's liquid glass (0.10.9) a source with no picture, unless a test has handed it one of its
    /// own: a test never captures the real screen.</summary>
    public static void NoCapture()
        => PowerLedger.App.Aero.LiquidGlassSources.Override ??= _ => new FakeGlassSource(null, Rect.Empty, PowerLedger.App.Aero.LiquidGlassSourceKind.None);

    /// <summary>Draws <paramref name="visual"/> at <paramref name="width"/> × <paramref name="height"/> to <paramref name="name"/> under <see cref="Folder"/>.</summary>
    public static void Render(Visual visual, int width, int height, string name)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Folder);   // a test run on its own, before any other has made it
        using var file = File.Create(Path.Combine(Folder, name));
        png.Save(file);
    }

    /// <summary>The first <typeparamref name="T"/> under <paramref name="root"/>, outermost first, that <paramref name="match"/> accepts.</summary>
    public static T? Find<T>(DependencyObject root, Func<T, bool>? match = null)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && (match is null || match(found))) return found;
            if (Find(child, match) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Lets the dispatcher run its queue, timers included, for <paramref name="duration"/>.</summary>
    public static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Lets the dispatcher run, timers and frames included, until <paramref name="condition"/> holds, and fails
    /// naming <paramref name="what"/> should it not within <paramref name="within"/>. For what lands with the frames, such
    /// as a fade's end or a popup's opening: under load a frame can come later than a fixed pump lasts.</summary>
    public static void PumpUntil(Func<bool> condition, TimeSpan within, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > within) throw new TimeoutException($"Waited {within.TotalSeconds:0.#} s for {what}.");
            Pump(TimeSpan.FromMilliseconds(15));
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    /// <summary>Whether <paramref name="element"/> has the keyboard focus, or had it when another process took the
    /// foreground: Windows then leaves this thread no Win32 focus, WPF drops the keyboard focus, and the element stays its
    /// window's focused element, to have the focus back when the window is next active. A second test run, or the person
    /// at the PC, can take the foreground at any moment, so IsKeyboardFocused alone says as much about the desktop as
    /// about the product.</summary>
    public static bool HasFocus(UIElement element)
        => element.IsKeyboardFocused
           || GetFocus() == IntPtr.Zero && FocusManager.GetFocusedElement(FocusManager.GetFocusScope(element)) == element;

    private static void UsePalette(ResourceDictionary palette)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_palette is not null) merged.Remove(_palette);
        _palette = palette;
        merged.Insert(0, palette);
    }

    /// <summary>
    /// Starts the application, with the App's styles, on an STA thread that runs its dispatcher until the process ends.
    /// A failure while no test is running is kept for the next test to throw rather than ending the process.
    /// </summary>
    private static Dispatcher StartUi()
    {
        var started = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/PowerLedger;component/Theme/Styles.xaml", UriKind.Absolute),
                });
                NoCapture();
                application.DispatcherUnhandledException += (_, e) =>
                {
                    if (_working) return;
                    _stray = e.Exception;
                    e.Handled = true;
                };
                started.SetResult(Dispatcher.CurrentDispatcher);
            }
            catch (Exception error)
            {
                started.SetException(error);
                return;
            }
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return started.Task.GetAwaiter().GetResult();
    }
}
