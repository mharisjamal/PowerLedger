using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Draws the window with each screen, in both themes and in a short window, to PNGs for a person to look at, and checks
/// that the window fits the screen it opens on and that a short window cuts no page off. The tests share the test
/// process's only WPF application, which lives on an STA thread of its own for as long as the process does.
/// </summary>
[Trait("Category", "UI")]
public class RenderingTests
{
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), "powerledger-renders");
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly Lazy<Dispatcher> Ui = new(StartUi);
    private static ResourceDictionary? _palette;
    private static bool _working;
    private static Exception? _stray;

    private static readonly (Page Page, string Name, Action<ShellViewModel> Prepare, Func<ShellViewModel, FrameworkElement> View)[] Pages =
    [
        (Page.Now, "now", _ => { }, shell => new NowView { DataContext = shell.Now }),
        (Page.Breakdown, "breakdown", shell => shell.Breakdown.Range.Choice = RangeChoice.SevenDays, shell => new BreakdownView { DataContext = shell.Breakdown }),
        (Page.Breakdown, "custom", shell => shell.Breakdown.Range.Choice = RangeChoice.Custom, shell => new BreakdownView { DataContext = shell.Breakdown }),
        (Page.Report, "report", _ => { }, shell => new ReportView { DataContext = shell.Report }),
        (Page.Settings, "settings", _ => { }, shell => new SettingsView { DataContext = shell.Settings }),
        (Page.Now, "wizard", shell => shell.BeginSetup(), shell => new WizardView { DataContext = shell.Wizard }),
    ];

    /// <summary>
    /// The pages in a short window, 880 wide, as narrow as the window gets: each is drawn at the top and, when it scrolls,
    /// at the end. At 560 high every page but the wizard is longer than the window and scrolls; the wizard, at its longest
    /// step, fits whole, and it scrolls in a window shorter still.
    /// </summary>
    private static readonly (string Name, Page Page, Action<ShellViewModel> Prepare, double Height, bool Scrolls)[] ShortPages =
    [
        ("now", Page.Now, _ => { }, 560, true),
        ("breakdown", Page.Breakdown, shell => shell.Breakdown.Range.Choice = RangeChoice.SevenDays, 560, true),
        ("report", Page.Report, _ => { }, 560, true),
        ("settings", Page.Settings, _ => { }, 560, true),
        ("wizard", Page.Now, MachineStep, 560, false),
        ("wizard-448", Page.Now, MachineStep, 448, true),
    ];

    [Fact]
    public void The_window_draws_every_screen_in_both_themes()
    {
        Directory.CreateDirectory(Folder);
        OnUi(Render);

        new FileInfo(Path.Combine(Folder, "report-picture.png")).Length.ShouldBeGreaterThan(30_000);
        foreach (var (_, name, _, _) in Pages)
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                new FileInfo(Path.Combine(Folder, $"{name}-{theme}.png")).Length.ShouldBeGreaterThan(30_000);
            }
        }
    }

    [Fact]
    public void A_window_that_places_itself_opens_inside_the_work_area_of_its_screen()
        => OnUi(() =>
        {
            var window = new MainWindow { ShowInTaskbar = false };
            try
            {
                var handle = new WindowInteropHelper(window).EnsureHandle();   // sized and placed as for showing, but not shown
                var screen = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                GetMonitorInfo(MonitorFromWindow(handle, NearestMonitor), ref screen).ShouldBeTrue();
                GetWindowRect(handle, out var placed).ShouldBeTrue();
                var work = screen.Work;
                var inside = placed.Left >= work.Left && placed.Top >= work.Top && placed.Right <= work.Right && placed.Bottom <= work.Bottom;
                inside.ShouldBeTrue($"The window is at {placed} on a screen whose work area is {work}, in pixels.");

                // As big as it asks to be, 1180 × 900, unless that leaves less than the margin clear of the work area's edges.
                var scale = HwndSource.FromHwnd(handle).CompositionTarget.TransformToDevice.M11;
                double width = placed.Right - placed.Left;
                double height = placed.Bottom - placed.Top;
                width.ShouldBe(Math.Min(1180 * scale, work.Right - work.Left - 2 * WindowFit.Margin * scale), 1);
                height.ShouldBe(Math.Min(900 * scale, work.Bottom - work.Top - 2 * WindowFit.Margin * scale), 1);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void A_short_window_scrolls_the_pages_that_do_not_fit_and_cuts_none_off()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            UseTheme(Theme.Dark);
            using var saver = new FakeSaver();
            var shell = Shell(saver);
            foreach (var (name, page, prepare, height, scrolls) in ShortPages)
            {
                prepare(shell);
                shell.Page = page;
                var window = new MainWindow
                {
                    DataContext = shell, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false,
                };
                window.FitTo(new Bounds(-20000, 0, 880 + 2 * WindowFit.Margin, height + 2 * WindowFit.Margin));   // a small screen, far to the side
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(1200));
                    window.ActualWidth.ShouldBe(880, 0.5, name);
                    window.ActualHeight.ShouldBe(height, 0.5, name);
                    var scroller = Find<UserControl>(window).ShouldNotBeNull(name).Content.ShouldBeOfType<ScrollViewer>(name);
                    var statusTop = window.StatusBar.TranslatePoint(default, window).Y;
                    scroller.TranslatePoint(new Point(0, scroller.ActualHeight), window).Y.ShouldBeLessThanOrEqualTo(statusTop + 0.5, $"{name} runs under the status bar");
                    (statusTop + window.StatusBar.ActualHeight).ShouldBeLessThanOrEqualTo(window.ActualHeight, $"{name} pushes the status bar out");
                    (scroller.ExtentHeight > scroller.ViewportHeight).ShouldBe(scrolls, $"{name} is {scroller.ExtentHeight:0} tall in a view {scroller.ViewportHeight:0} tall");
                    scroller.ComputedVerticalScrollBarVisibility.ShouldBe(scrolls ? Visibility.Visible : Visibility.Collapsed, name);
                    Save(window, 880, (int)height, $"short-{name}.png");

                    scroller.ScrollToEnd();
                    Pump(TimeSpan.FromMilliseconds(200));
                    scroller.VerticalOffset.ShouldBe(Math.Max(0, scroller.ExtentHeight - scroller.ViewportHeight), 0.5, $"the end of {name} is out of reach");
                    window.StatusBar.TranslatePoint(default, window).Y.ShouldBe(statusTop, 0.01, $"the status bar moved with {name}");
                    if (scrolls) Save(window, 880, (int)height, $"short-{name}-end.png");
                }
                finally
                {
                    window.Close();
                    if (shell.IsSetup) shell.EndSetup();
                }
            }
        });
    }

    [Fact]
    public void The_update_card_draws_in_the_rail_in_both_themes()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            using var saver = new FakeSaver();
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                foreach (var (name, updates) in new[] { ("ready", ReadyUpdate()), ("available", AvailableUpdate()), ("updated", UpdatedApp()) })
                {
                    var shell = new ShellViewModel(NowScreen(), BreakdownScreen(), ReportScreen(saver), SettingsScreen(), WizardScreen(), "0.2.0", updates);
                    var window = new MainWindow
                    {
                        DataContext = shell, WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    };
                    window.Show();
                    try
                    {
                        Pump(TimeSpan.FromMilliseconds(1200));
                        var card = Find<Border>(window, border => AutomationProperties.GetName(border) == "Update").ShouldNotBeNull(name);
                        card.IsVisible.ShouldBeTrue(name);
                        Save(window, (int)window.ActualWidth, (int)window.ActualHeight, $"update-{name}-{theme}.png");
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
        });
        new FileInfo(Path.Combine(Folder, "update-ready-Dark.png")).Length.ShouldBeGreaterThan(30_000);
    }

    /// <summary>0.3.0 downloaded and waiting, on 0.2.0.</summary>
    private static Updater ReadyUpdate()
    {
        var feed = new FakeFeed { Latest = UpdaterTests.Release("0.3.0") };
        var updater = new Updater(feed, new FakeDownloader(), new FakeSetup(), new FakeCost(), new FakeUiSettings(), UiThreads.Inline,
            new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, new Version(0, 2, 0), (_, _) => { }, _ => { });
        updater.CheckAsync().GetAwaiter().GetResult();
        return updater;
    }

    /// <summary>0.3.0 found while the connection is metered.</summary>
    private static Updater AvailableUpdate()
    {
        var feed = new FakeFeed { Latest = UpdaterTests.Release("0.3.0") with { Size = 58 * 1024 * 1024 } };
        var updater = new Updater(feed, new FakeDownloader(), new FakeSetup(), new FakeCost { Metered = true }, new FakeUiSettings(),
            UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, new Version(0, 2, 0), (_, _) => { }, _ => { });
        updater.CheckAsync().GetAwaiter().GetResult();
        return updater;
    }

    /// <summary>The first start of 0.2.0 after 0.1.0.</summary>
    private static Updater UpdatedApp()
    {
        var ui = new FakeUiSettings { Current = UiPreferences.Default with { LastVersion = "0.1.0" } };
        var updater = new Updater(new FakeFeed(), new FakeDownloader(), new FakeSetup(), new FakeCost(), ui, UiThreads.Inline,
            new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, new Version(0, 2, 0), (_, _) => { }, _ => { });
        updater.Start();
        return updater;
    }

    /// <summary>The wizard at its longest step: the machine, for a desktop, which adds the power supply's rating.</summary>
    private static void MachineStep(ShellViewModel shell)
    {
        shell.BeginSetup();
        shell.Wizard.Next.Execute(null);
        shell.Wizard.Step.ShouldBe(SetupStep.Machine);
        shell.Wizard.Machine.Chassis = ChassisKind.Desktop;
    }

    private static void Render()
    {
        using var saver = new FakeSaver();
        var shell = Shell(saver);
        foreach (var theme in new[] { Theme.Dark, Theme.Light })
        {
            UseTheme(theme);

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
                if (page == Page.Report && Find<Button>(window, button => Equals(button.Content, "PNG")) is { } picture)
                {
                    picture.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // the view draws the report into the file the saver names
                    File.Copy(saver.Chosen, Path.Combine(Folder, "report-picture.png"), overwrite: true);
                }
                window.Close();
                if (shell.IsSetup) shell.EndSetup();

                // Windows keeps a window within the screen, so each screen's whole length is drawn from its view alone.
                var child = view(shell);
                child.Width = 1010;
                var host = new Border { Background = (Brush)Application.Current.FindResource("Brush.Panel"), Child = child };
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

    /// <summary>Runs <paramref name="work"/> on the application's thread, and throws here what it threw there.</summary>
    private static void OnUi(Action work)
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

    /// <summary>Puts <paramref name="theme"/>'s palette first among the application's dictionaries, where the App keeps it.</summary>
    private static void UseTheme(Theme theme)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_palette is not null) merged.Remove(_palette);
        _palette = ThemeManager.Palette(theme);
        merged.Insert(0, _palette);
    }

    private static ShellViewModel Shell(FakeSaver saver)
        => new(NowScreen(), BreakdownScreen(), ReportScreen(saver), SettingsScreen(), WizardScreen(), "0.1.0");

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

    private static WizardViewModel WizardScreen()
    {
        var link = new FakeLink();
        link.Connect(true);
        return new WizardViewModel(link, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline, new FakeTimeProvider(Now),
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

    /// <summary>The first <typeparamref name="T"/> under <paramref name="root"/>, outermost first, that <paramref name="match"/> accepts.</summary>
    private static T? Find<T>(DependencyObject root, Func<T, bool>? match = null)
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

    private const int NearestMonitor = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Pixels rect);

    /// <summary>A rectangle in pixels, as Windows gives it.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Pixels
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public override readonly string ToString() => $"({Left}, {Top}) to ({Right}, {Bottom})";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Pixels Screen;
        public Pixels Work;
        public int Flags;
    }
}
