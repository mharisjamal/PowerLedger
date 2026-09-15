using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>Draws the window with each screen, in both themes, to PNGs for a person to look at.</summary>
[Trait("Category", "UI")]
public class RenderingTests
{
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), "powerledger-renders");
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static readonly (Page Page, string Name, Action<ShellViewModel> Prepare, Func<ShellViewModel, FrameworkElement> View)[] Pages =
    [
        (Page.Now, "now", _ => { }, shell => new NowView { DataContext = shell.Now }),
        (Page.Breakdown, "breakdown", shell => shell.Breakdown.Range.Choice = RangeChoice.SevenDays, shell => new BreakdownView { DataContext = shell.Breakdown }),
        (Page.Breakdown, "custom", shell => shell.Breakdown.Range.Choice = RangeChoice.Custom, shell => new BreakdownView { DataContext = shell.Breakdown }),
        (Page.Report, "report", _ => { }, shell => new ReportView { DataContext = shell.Report }),
        (Page.Settings, "settings", _ => { }, shell => new SettingsView { DataContext = shell.Settings }),
    ];

    [Fact]
    public void The_window_draws_every_screen_in_both_themes()
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

        new FileInfo(Path.Combine(Folder, "report-picture.png")).Length.ShouldBeGreaterThan(30_000);
        foreach (var (_, name, _, _) in Pages)
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                new FileInfo(Path.Combine(Folder, $"{name}-{theme}.png")).Length.ShouldBeGreaterThan(30_000);
            }
        }
    }

    private static void Render()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PowerLedger;component/Theme/Styles.xaml", UriKind.Absolute),
        });
        using var saver = new FakeSaver();
        var shell = new ShellViewModel(NowScreen(), BreakdownScreen(), ReportScreen(saver), SettingsScreen(), "0.1.0");
        ResourceDictionary? palette = null;
        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            if (palette is not null) application.Resources.MergedDictionaries.Remove(palette);
            palette = ThemeManager.Palette(theme);
            application.Resources.MergedDictionaries.Insert(0, palette);

            foreach (var (page, name, prepare, view) in Pages)
            {
                prepare(shell);
                shell.Page = page;
                var window = new MainWindow
                {
                    DataContext = shell, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                };
                window.Show();
                Pump(TimeSpan.FromMilliseconds(1200));   // the live readout settles over 900 ms
                Save(window, (int)window.ActualWidth, (int)window.ActualHeight, $"{name}-{theme}.png");
                if (page == Page.Report && FindButton(window, "PNG") is { } picture)
                {
                    picture.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // the view draws the report into the file the saver names
                    File.Copy(saver.Chosen, Path.Combine(Folder, "report-picture.png"), overwrite: true);
                }
                window.Close();

                // Windows keeps a window within the screen, so each screen's whole length is drawn from its view alone.
                var child = view(shell);
                child.Width = 1010;
                var host = new Border { Background = (Brush)application.FindResource("Brush.Panel"), Child = child };
                host.Measure(new Size(1010, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize));
                Pump(TimeSpan.FromMilliseconds(1200));
                host.UpdateLayout();
                Save(host, (int)host.ActualWidth, (int)host.ActualHeight, $"{name}-{theme}-full.png");
            }
        }
        shell.Page = Page.Now;
        File.WriteAllBytes(Path.Combine(Folder, "report.pdf"), ReportDocument.Generate(shell.Report.Data, "0.1.0", Now, English));
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
    private static NowViewModel NowScreen()
    {
        var link = new FakeLink();
        var history = new FakeHistory();
        var model = new NowViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English,
            co2KgPerKwh: 0.38, startService: () => { });
        link.Connect(true);
        history.Snapshot = Snapshots.Typical(Now, Series());
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

    /// <summary>The same machine's last seven days: asleep overnight, working days, quiet evenings.</summary>
    private static BreakdownViewModel BreakdownScreen()
    {
        var history = new FakeRangeHistory { Answer = range => Reports.Typical(range) with { Series = Week(range) } };
        var model = new BreakdownViewModel(history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English);
        return model;
    }

    /// <summary>September so far on the same machine, with a tariff and a plan that sleeps after three hours.</summary>
    private static ReportViewModel ReportScreen(FakeSaver saver)
    {
        var history = new FakeRangeHistory { Answer = Month };
        return new ReportViewModel(history, new FakeSleep(), saver, _ => [], UiThreads.Inline, new FakeTimeProvider(Now),
            TimeZoneInfo.Utc, English, 0.38);
    }

    /// <summary>Settings against a running service, with a tariff and this laptop's detection.</summary>
    private static SettingsViewModel SettingsScreen()
    {
        var link = new FakeLink();
        link.Connect(true);
        return new SettingsViewModel(link, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline, new FakeTimeProvider(Now),
            TimeZoneInfo.Utc, English, "USD");
    }

    private static RangeReport Month(DateRange range)
    {
        var report = Reports.Typical(range);
        var weights = report.Days.Select((_, i) => 0.7 + 0.15 * (i * 3 % 5)).ToList();
        var days = report.Days.Select((d, i) => d with { EnergyKwh = report.Totals.EnergyKwh * weights[i] / weights.Sum() }).ToList();
        return report with { Days = days };
    }

    private static IReadOnlyList<Aggregate> Week(DateRange range)
    {
        var series = new List<Aggregate>();
        var seed = 5;
        double Noise()
        {
            seed = (seed * 9301 + 49297) % 233280;
            return seed / 233280.0 - 0.5;
        }
        for (var start = range.From; start < range.To; start += range.Bucket)
        {
            var hour = start.Hour;
            var (watts, on) = hour switch
            {
                < 7 or 23 => (0.0, 0.0),
                7 => (16 + Noise() * 2, 1800.0),
                < 12 => (36 + Noise() * 10, 3600.0),
                < 13 => (18 + Noise() * 3, 3600.0),
                < 18 => (42 + Noise() * 14, 3600.0),
                _ => (15 + Noise() * 3, 3600.0),
            };
            var wh = watts * on / 3600;
            series.Add(Aggregate.Empty(start) with
            {
                EnergyWh = wh, CpuWh = wh * 0.45, GpuWh = wh * 0.1, DisplayWh = 4 * on / 3600, RestWh = wh * 0.45 - 4 * on / 3600,
                OnSeconds = on, GapSeconds = 3600 - on,
            });
        }
        return series;
    }

    private static IReadOnlyList<Aggregate> Series()
    {
        var dayStart = new DateTimeOffset(Now.Date, TimeSpan.Zero);
        var series = new List<Aggregate>();
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
            var start = dayStart.AddMinutes(5 * i);
            series.Add(total <= 0
                ? Aggregate.Empty(start) with { GapSeconds = 300 }
                : Aggregate.Empty(start) with
                {
                    CpuWh = total * 0.45 / 12, GpuWh = total * 0.11 / 12, DisplayWh = 4 / 12.0, RestWh = (total * 0.44 - 4) / 12,
                    EnergyWh = total / 12, OnSeconds = 300,
                });
        }
        return series;
    }

    private static Button? FindButton(DependencyObject root, string content)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button && Equals(button.Content, content)) return button;
            if (FindButton(child, content) is { } found) return found;
        }
        return null;
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
