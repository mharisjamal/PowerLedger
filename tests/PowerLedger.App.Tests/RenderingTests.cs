using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Draws the window with the Now screen, in both themes, to PNGs for a person to look at.</summary>
[Trait("Category", "UI")]
public class RenderingTests
{
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), "powerledger-renders");
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);

    [Fact]
    public void The_window_draws_the_now_screen_in_both_themes()
    {
        Directory.CreateDirectory(Folder);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Render();
            }
            catch (Exception error)
            {
                failure = error;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();

        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            new FileInfo(Path.Combine(Folder, $"now-{theme}.png")).Length.ShouldBeGreaterThan(30_000);
        }
    }

    private static void Render()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PowerLedger;component/Theme/Styles.xaml", UriKind.Absolute),
        });
        var shell = new ShellViewModel(Model(), "0.1.0");
        ResourceDictionary? palette = null;
        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            if (palette is not null) application.Resources.MergedDictionaries.Remove(palette);
            palette = ThemeManager.Palette(theme);
            application.Resources.MergedDictionaries.Insert(0, palette);

            var window = new MainWindow
            {
                DataContext = shell, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            Pump(TimeSpan.FromMilliseconds(1200));   // the live readout settles over 900 ms
            Save(window, (int)window.ActualWidth, (int)window.ActualHeight, $"now-{theme}.png");
            window.Close();

            // Windows keeps a window within the screen, so the whole screen's length is drawn from the view alone.
            var host = new System.Windows.Controls.Border
            {
                Background = (Brush)application.FindResource("Brush.Panel"),
                Child = new NowView { DataContext = shell.Now, Width = 1010 },
            };
            host.Measure(new Size(1010, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            Pump(TimeSpan.FromMilliseconds(1200));
            host.UpdateLayout();
            Save(host, (int)host.ActualWidth, (int)host.ActualHeight, $"now-{theme}-full.png");
        }
    }

    private static void Save(Visual visual, int width, int height, string name)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Folder, name));
        png.Save(file);
    }

    /// <summary>A Tuesday afternoon eight days into September: asleep until 07:30, a working morning, an idle patch, a peak at 14:00.</summary>
    private static NowViewModel Model()
    {
        var link = new FakeLink();
        var history = new FakeHistory();
        var model = new NowViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc,
            CultureInfo.GetCultureInfo("en-US"), co2KgPerKwh: 0.38, startService: () => { });
        link.Connect(true);
        history.Snapshot = Snapshots.Typical(Now, Slots());
        model.RefreshHistory();
        var seed = 11;
        var watts = 33.0;
        for (var s = 59; s >= 0; s--)
        {
            seed = (seed * 9301 + 49297) % 233280;
            watts = Math.Clamp(watts + (seed / 233280.0 - 0.5) * 3, 27, 41);
            link.Push(Frames.At(Now.AddSeconds(-s), totalW: watts, cpu: watts * 0.43, gpu: watts * 0.12, display: 4.0));
        }
        return model;
    }

    private static IReadOnlyList<DaySlot> Slots()
    {
        var dayStart = new DateTimeOffset(Now.Date, TimeSpan.Zero);
        var slots = new List<DaySlot>();
        var seed = 7;
        double Noise()
        {
            seed = (seed * 9301 + 49297) % 233280;
            return seed / 233280.0 - 0.5;
        }
        for (var i = 0; i < 175; i++)
        {
            var total = i switch
            {
                < 90 => 0,
                < 108 => 14 + Noise() * 3,
                < 150 => 38 + 10 * Math.Sin((i - 108) / 42.0 * Math.PI) + Noise() * 8,
                < 159 => 15 + Noise() * 2,
                168 => 68,
                _ => 44 + Noise() * 6,
            };
            var start = dayStart + i * DaySlots.Length;
            slots.Add(total <= 0
                ? new DaySlot(start, 0, 0, 0, 0, 0, 300)
                : new DaySlot(start, total * 0.45, total * 0.11, 4, total * 0.44 - 4, 300, 0));
        }
        return slots;
    }

    private static void Pump(TimeSpan duration)
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
}
