using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
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

    /// <summary>Runs <paramref name="work"/> on the application's thread, and throws here what it threw there.</summary>
    public static void OnUi(Action work)
    {
        ExceptionDispatchInfo? failure = null;
        Ui.Value.Invoke(() =>
        {
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
        });
        failure?.Throw();
    }

    /// <summary>Puts <paramref name="theme"/>'s palette first among the application's dictionaries, where the App keeps it.</summary>
    public static void UseTheme(Theme theme) => UsePalette(ThemeManager.Palette(theme));

    /// <summary>Draws <paramref name="visual"/> at <paramref name="width"/> × <paramref name="height"/> to <paramref name="name"/> under <see cref="Folder"/>.</summary>
    public static void Render(Visual visual, int width, int height, string name)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
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
