using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.Contracts;
using PowerLedger.Core;
using PowerLedger.Storage;
using Shouldly;
using static PowerLedger.App.Tests.UiHarness;

namespace PowerLedger.App.Tests;

/// <summary>
/// Draws the window with each screen, in both themes and in a short window, to PNGs for a person to look at, and checks
/// that the window fits the screen it opens on and that a short window cuts no page off. The tests share the test
/// process's only WPF application, which lives on an STA thread of its own for as long as the process does.
/// </summary>
[Trait("Category", "UI")]
public class RenderingTests
{
    public static readonly string Folder = UiHarness.Folder;
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 14, 32, 7, TimeSpan.Zero);
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static readonly (Page Page, string Name, Action<ShellViewModel> Prepare, Func<ShellViewModel, FrameworkElement> View)[] Pages =
    [
        (Page.Now, "now", _ => { }, shell => new NowView { DataContext = shell.Now }),
        (Page.Breakdown, "breakdown", shell => shell.Breakdown.Range.Choice = RangeChoice.SevenDays, shell => new BreakdownView { DataContext = shell.Breakdown }),
        (Page.Breakdown, "custom", shell => shell.Breakdown.Range.Choice = RangeChoice.Custom, shell => new BreakdownView { DataContext = shell.Breakdown }),
        (Page.Report, "report", _ => { }, shell => new ReportView { DataContext = shell.Report }),
        (Page.Household, "household", _ => { }, shell => new HouseholdView { DataContext = shell.Household }),
        (Page.Settings, "settings", SavedSettings, shell => new SettingsView { DataContext = shell.Settings }),
        (Page.Now, "wizard", shell => shell.BeginSetup(), shell => new WizardView { DataContext = shell.Wizard }),
        (Page.Now, "wizard-machine", MachineStep, shell => new WizardView { DataContext = shell.Wizard }),
        (Page.Now, "wizard-laptop", LaptopStep, shell => new WizardView { DataContext = shell.Wizard }),
    ];

    /// <summary>
    /// The pages in a short window, 880 wide, as narrow as the window gets: each is drawn at the top and, when it scrolls,
    /// at the end. At 560 high every page but the wizard is longer than the window and scrolls; the wizard's machine step,
    /// its longest, fits whole for a desktop, which adds the power supply's rating, and for a laptop, which asks of each
    /// monitor whether it has a plug of its own, and it scrolls in a window shorter still.
    /// </summary>
    private static readonly (string Name, Page Page, Action<ShellViewModel> Prepare, double Height, bool Scrolls)[] ShortPages =
    [
        ("now", Page.Now, _ => { }, 560, true),
        ("breakdown", Page.Breakdown, shell => shell.Breakdown.Range.Choice = RangeChoice.SevenDays, 560, true),
        ("report", Page.Report, _ => { }, 560, true),
        ("household", Page.Household, _ => { }, 560, true),
        ("settings", Page.Settings, _ => { }, 560, true),
        ("wizard", Page.Now, MachineStep, 560, false),
        ("wizard-laptop", Page.Now, LaptopStep, 560, false),
        ("wizard-448", Page.Now, MachineStep, 448, true),
    ];

    /// <summary>What the notes under the monitor rows say before what is particular to Settings or the wizard.</summary>
    private const string LaptopMonitorsNote = "A monitor of 17.3 inches or less is taken to run off this laptop, so it counts as part of what the laptop draws; "
        + "tick \"has its own plug\" if it has one. Untick a monitor with its own plug to leave it out.";

    private const string DesktopMonitorsNote = "Untick a monitor to leave it out.";

    /// <summary>The preference that stops every request sent to the monitors, both of which it names.</summary>
    private const string MonitorsTick = "Read monitors' brightness and whether they're on (read-only)";

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
                    if (shell.Current is WizardViewModel or SettingsViewModel)
                    {
                        var form = shell.IsSetup ? shell.Wizard.Machine : shell.Settings.Service;
                        form.Monitors.Count.ShouldBe(2, name);
                        var rows = Find<ItemsControl>(window, items => items.ItemsSource == form.Monitors).ShouldNotBeNull(name);
                        foreach (var monitor in form.Monitors)
                        {
                            var row = rows.ItemContainerGenerator.ContainerFromItem(monitor).ShouldBeAssignableTo<DependencyObject>(name);
                            Find<TextBox>(row, box => AutomationProperties.GetName(box) == $"Watts for {monitor.Name}").ShouldNotBeNull(name).IsVisible.ShouldBeTrue(name);
                            // A row keeps its boxes and where its figure came from inside the page, however many boxes it shows.
                            var plug = Find<CheckBox>(row, box => AutomationProperties.GetName(box) == $"{monitor.Name} has its own plug").ShouldNotBeNull(name);
                            plug.IsVisible.ShouldBe(!form.IsDesktop || monitor.ShowsPlug, name);
                            var source = Find<TextBlock>(row, text => text.Text == monitor.Source).ShouldNotBeNull(name);
                            foreach (var part in plug.IsVisible ? new FrameworkElement[] { plug, source } : [source])
                            {
                                part.TranslatePoint(new Point(part.ActualWidth, 0), scroller).X.ShouldBeLessThanOrEqualTo(scroller.ViewportWidth + 0.5, $"{name}: {monitor.Name}");
                            }
                        }
                    }
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
    public void Only_a_laptop_asks_whether_each_monitor_has_a_plug_of_its_own_and_one_that_runs_off_the_pc_stays_counted()
        => OnUi(() =>
        {
            UseTheme(Theme.Dark);
            var settings = SettingsScreen();
            settings.Show();
            var wizard = WizardScreen();
            wizard.Start();
            wizard.Next.Execute(null);
            wizard.Step.ShouldBe(SetupStep.Machine);
            (string Name, ServiceForm Form, FrameworkElement View, string After)[] screens =
            [
                ("settings", settings.Service, new SettingsView { DataContext = settings },
                    " PowerLedger reads whether each monitor is on, if allowed under Preferences; where it can't tell, unticking a monitor"
                    + " while it's off keeps it out. Clear a monitor's watts to go back to PowerLedger's own figure."),
                ("wizard", wizard.Machine, new WizardView { DataContext = wizard }, " The rest of the machine, memory and drives among them, is in Settings."),
            ];
            foreach (var (name, form, view, after) in screens)
            {
                var window = new Window
                {
                    Content = view, Width = 1180, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                    ShowInTaskbar = false, ShowActivated = false,
                };
                window.Show();
                try
                {
                    // Settings lists a portable monitor, which runs off the laptop; the wizard's second monitor is said to.
                    var (own, runsOff) = (form.Monitors[0], form.Monitors[1]);
                    runsOff.OwnPlug = false;
                    Pump(TimeSpan.FromMilliseconds(300));
                    var rows = Find<ItemsControl>(view, items => items.ItemsSource == form.Monitors).ShouldNotBeNull(name);
                    foreach (var chassis in new[] { ChassisKind.Laptop, ChassisKind.Desktop })
                    {
                        form.Chassis = chassis;
                        Pump(TimeSpan.FromMilliseconds(300));
                        var what = $"{name} on a {chassis}";

                        var note = Find<TextBlock>(view, text => text.IsVisible && text.Text.EndsWith(after, StringComparison.Ordinal)).ShouldNotBeNull(what);
                        note.Text.ShouldBe((chassis == ChassisKind.Laptop ? LaptopMonitorsNote : DesktopMonitorsNote) + after, what);

                        // A desktop doesn't ask about plugs, but still shows the box of a monitor held to run off it, so that can be undone.
                        Box(own, $"{own.Name} has its own plug").IsVisible.ShouldBe(chassis == ChassisKind.Laptop, what);
                        Box(runsOff, $"{runsOff.Name} has its own plug").IsVisible.ShouldBeTrue(what);

                        var counts = Box(own, $"Count {own.Name}");
                        (counts.IsEnabled, counts.IsChecked).ShouldBe((true, (bool?)true), what);
                        counts.ToolTip.ShouldBeNull(what);
                        var alwaysCounts = Box(runsOff, $"Count {runsOff.Name}");
                        (alwaysCounts.IsEnabled, alwaysCounts.IsChecked).ShouldBe((false, (bool?)true), what);
                        alwaysCounts.ToolTip.ShouldBe("Runs off this PC, so it counts as part of what the PC draws", what);
                        ToolTipService.GetShowOnDisabled(alwaysCounts).ShouldBeTrue(what);
                    }

                    // Ticked on a desktop, the box stays under the pointer, so it can be unticked again.
                    var plug = Box(runsOff, $"{runsOff.Name} has its own plug");
                    foreach (var ticked in new[] { true, false })
                    {
                        plug.IsChecked = ticked;
                        Pump(TimeSpan.FromMilliseconds(300));
                        (runsOff.OwnPlug, plug.IsVisible).ShouldBe((ticked, true), $"{name} on a desktop, ticked: {ticked}");
                    }

                    CheckBox Box(MonitorRow monitor, string label)
                        => Find<CheckBox>(rows.ItemContainerGenerator.ContainerFromItem(monitor).ShouldBeAssignableTo<DependencyObject>(name), box => AutomationProperties.GetName(box) == label)
                            .ShouldNotBeNull($"{name}: {label}");
                }
                finally
                {
                    window.Close();
                }
            }
        });

    [Fact]
    public void Settings_saves_a_typed_value_once_its_box_is_left_or_enter_is_pressed_and_a_tick_at_once()
        => OnUi(() =>
        {
            UseTheme(Theme.Dark);
            var link = new FakeLink();
            var settings = SettingsScreen(link);
            settings.Show();
            var view = new SettingsView { DataContext = settings };
            var window = new Window
            {
                Content = view, Width = 1180, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            try
            {
                Pump(TimeSpan.FromMilliseconds(300));
                Find<Button>(view, button => Equals(button.Content, "Save settings")).ShouldBeNull();
                var (fans, ssds, dell) = (Box("Fans"), Box("SSDs"), Box($"Watts for {Statuses.Dell.Name}"));

                // What is typed waits in its box until Enter is pressed there.
                FocusManager.SetFocusedElement(window, fans);
                fans.Text = "3";
                link.Writes.ShouldBeEmpty();
                PressEnter(fans);
                Sent().Profile.FanCount.ShouldBe(3);

                // Or until the box is left.
                FocusManager.SetFocusedElement(window, ssds);
                ssds.Text = "2";
                link.Writes.Count.ShouldBe(1);
                FocusManager.SetFocusedElement(window, dell);
                Sent().Profile.SsdCount.ShouldBe(2);

                // A monitor's figure too, inside its row.
                dell.Text = "30";
                PressEnter(dell);
                Sent().Profile.Monitors.ShouldBe([new MonitorChoice { Key = Statuses.Dell.Key, Watts = 30 }]);

                // A tick saves at once, with the figures given to the form and not one still being typed.
                dell.Text = "40";
                Find<CheckBox>(view, box => Equals(box.Content, "DDR5")).ShouldNotBeNull().IsChecked = true;
                (Sent().Profile.RamIsDdr5, Sent().Profile.Monitors.Single().Watts).ShouldBe((true, 30.0));
                link.Writes.Count.ShouldBe(4);

                // One line says how the last save went, between the machine and sampling, where it shows while either is edited.
                Pump(TimeSpan.FromMilliseconds(300));
                var said = Find<TextBlock>(view, text => text.Text == "Saved.").ShouldNotBeNull();
                said.IsVisible.ShouldBeTrue();
                var rated = Find<TextBlock>(view, text => text.Text == "Processor · graphics rated power, W").ShouldNotBeNull();
                var sampling = Find<TextBlock>(view, text => text.Text == "SAMPLING AND HISTORY").ShouldNotBeNull();
                Top(said).ShouldBeGreaterThan(Top(rated));
                Top(said).ShouldBeLessThan(Top(sampling));
            }
            finally
            {
                window.Close();
            }

            TextBox Box(string name) => Find<TextBox>(view, box => AutomationProperties.GetName(box) == name).ShouldNotBeNull(name);

            ServiceSettings Sent() => (ServiceSettings)link.Writes[^1];

            double Top(FrameworkElement element) => element.TranslatePoint(default, view).Y;

            static void PressEnter(TextBox box)
                => box.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        });

    [Fact]
    public void Settings_names_both_readings_its_monitor_tick_stops_and_the_tick_is_the_preference()
        => OnUi(() =>
        {
            UseTheme(Theme.Dark);
            var settings = SettingsScreen();
            var view = new SettingsView { DataContext = settings };
            var window = new Window
            {
                Content = view, Width = 1180, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            try
            {
                Pump(TimeSpan.FromMilliseconds(300));
                var tick = Find<CheckBox>(view, box => Equals(box.Content, MonitorsTick)).ShouldNotBeNull();
                tick.IsChecked.ShouldBe(true);

                tick.IsChecked = false;
                settings.ReadMonitorBrightness.ShouldBeFalse();
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void Settings_lists_the_ups_and_the_power_supply_asks_what_the_ups_powers_and_offers_to_stop_reading_the_supply()
        => OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink();
                var settings = SettingsScreen(link);
                settings.Show();
                var view = new SettingsView { DataContext = settings };
                var window = new Window
                {
                    Content = view, Width = 880, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                    ShowInTaskbar = false, ShowActivated = false,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    // As narrow as the window gets, so nothing here runs past the page.
                    var scroller = view.Content.ShouldBeOfType<ScrollViewer>();
                    string[] lines =
                    [
                        "UPS · APC Back-UPS ES 850G2 · 142 W (load of its rated watts)",
                        "Power supply · Corsair HX1000i · 312 W (DC output, all rails)",
                        "What does it power?",
                    ];
                    foreach (var text in lines)
                    {
                        var line = Find<TextBlock>(view, block => block.Text == text).ShouldNotBeNull($"{text} on {theme}");
                        line.IsVisible.ShouldBeTrue($"{text} on {theme}");
                        Inside(line, scroller, $"{text} on {theme}");
                    }

                    // Nothing is chosen until the user says what the UPS powers.
                    var choices = new[] { "This PC", "This PC and its monitors", "More" }
                        .Select(content => Find<RadioButton>(view, button => Equals(button.Content, content)).ShouldNotBeNull($"{content} on {theme}"))
                        .ToList();
                    choices.ShouldAllBe(button => button.IsChecked == false);
                    Inside(choices[^1], scroller, $"what the UPS powers on {theme}");

                    choices[1].IsChecked = true;
                    settings.Service.UpsLoad.ShouldBe(UpsLoad.ThisPcAndMonitors);
                    ((ServiceSettings)link.Writes[^1]).Profile.UpsLoad.ShouldBe(UpsLoad.ThisPcAndMonitors);

                    var tick = Find<CheckBox>(view, box => Equals(box.Content, "Read this power supply")).ShouldNotBeNull(theme.ToString());
                    (tick.IsVisible, tick.IsChecked).ShouldBe((true, (bool?)true), theme.ToString());
                    Inside(tick, scroller, $"the power supply tick on {theme}");

                    tick.IsChecked = false;
                    ((ServiceSettings)link.Writes[^1]).Profile.ReadPowerSupply.ShouldBeFalse();
                    link.Writes.Count.ShouldBe(2);
                }
                finally
                {
                    window.Close();
                }
            }

            static void Inside(FrameworkElement part, ScrollViewer scroller, string what)
                => part.TranslatePoint(new Point(part.ActualWidth, 0), scroller).X.ShouldBeLessThanOrEqualTo(scroller.ViewportWidth + 0.5, what);
        });

    /// <summary>The new sources (spec: GPU/UPS/PSU) are named in Settings, a UPS not yet found reads sensibly rather than
    /// "working", and neither the Settings calibration line nor Now's status bar calls a desktop's readings estimated
    /// when a UPS or power supply measures them.</summary>
    [Fact]
    public void A_desktop_with_a_measured_ups_or_power_supply_reading_is_named_and_worded_without_clipping()
        => OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);

                var settingsLink = new FakeLink
                {
                    Settings = ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop },
                    Status = Statuses.Running() with
                    {
                        Sources =
                        [
                            new SourceStatus("arc-gpu", false, "no Intel Arc discrete GPU", 0, null),
                            new SourceStatus("ups", true, "no UPS found on USB", 0, null),
                            new SourceStatus("power-supply", true, null, 0, null),
                        ],
                        PowerDevices = [Statuses.Ups, Statuses.PowerSupply],   // so the "Power supply" placeholder label stays empty
                        Last = Frames.At(Now, quality: Quality.Measured) with { Total = TotalSource.PowerSupply },
                    },
                };
                settingsLink.Connect(true);
                var settings = new SettingsViewModel(settingsLink, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline,
                    new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, "USD");
                settings.Show();
                var settingsView = new SettingsView { DataContext = settings };
                var settingsWindow = new Window
                {
                    Content = settingsView, Width = 880, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                    ShowInTaskbar = false, ShowActivated = false,
                };
                settingsWindow.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    var scroller = settingsView.Content.ShouldBeOfType<ScrollViewer>();
                    string[] lines =
                    [
                        "Intel Arc graphics", "not on this machine", "no Intel Arc discrete GPU",
                        "UPS", "note", "no UPS found on USB",
                        "Power supply", "working",
                        "Not used on a desktop: a UPS or power supply measures its readings.",
                    ];
                    foreach (var text in lines)
                    {
                        var line = Find<TextBlock>(settingsView, block => block.Text == text).ShouldNotBeNull($"{text} on {theme}");
                        line.IsVisible.ShouldBeTrue($"{text} on {theme}");
                        line.TranslatePoint(new Point(line.ActualWidth, 0), scroller).X.ShouldBeLessThanOrEqualTo(scroller.ViewportWidth + 0.5, $"{text} on {theme}");
                    }
                    Save(settingsWindow, 880, 900, $"desktop-sources-settings-{theme}.png");
                }
                finally
                {
                    settingsWindow.Close();
                }

                using var saver = new FakeSaver();
                var nowLink = new FakeLink
                {
                    Settings = ServiceSettings.Default with { Profile = MachineProfile.DefaultDesktop },
                    Status = Statuses.Running() with { Last = Frames.At(Now, quality: Quality.Measured) with { Total = TotalSource.Ups } },
                };
                var now = new NowViewModel(nowLink, new FakeHistory(), UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English,
                    co2KgPerKwh: 0.38, startService: () => { });
                now.Start();
                nowLink.Connect(true);
                var shell = new ShellViewModel(now, BreakdownScreen(), ReportScreen(saver), HouseholdScreen(), SettingsScreen(), WizardScreen(), "0.1.0");
                shell.Page = Page.Now;
                var nowWindow = new MainWindow
                {
                    DataContext = shell, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                };
                nowWindow.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(1200));
                    var chip = Find<TextBlock>(nowWindow, t => t.Text == "Desktop · measured").ShouldNotBeNull($"now chip on {theme}");
                    chip.IsVisible.ShouldBeTrue($"now chip on {theme}");
                    chip.TranslatePoint(new Point(chip.ActualWidth, 0), nowWindow).X.ShouldBeLessThanOrEqualTo(nowWindow.ActualWidth + 0.5, $"now chip on {theme}");
                    Save(nowWindow, (int)nowWindow.ActualWidth, (int)nowWindow.ActualHeight, $"desktop-now-{theme}.png");
                }
                finally
                {
                    nowWindow.Close();
                }
            }
        });

    /// <summary>Plan L follow-up: the wizard's Readings step names the UPS or power supply giving a desktop's total and
    /// calls it measured, instead of the old blanket "estimated" claim, and the new sentence fits the page.</summary>
    [Fact]
    public void The_wizards_readings_step_says_a_desktop_measured_by_its_ups_by_name_without_clipping()
        => OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink
                {
                    Status = Statuses.WithMonitors() with
                    {
                        Last = Frames.At(Now, quality: Quality.Measured) with { Total = TotalSource.Ups },
                        PowerDevices = [Statuses.Ups],
                    },
                };
                link.Connect(true);
                var wizard = new WizardViewModel(link, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline,
                    new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, "USD");
                wizard.Start();
                wizard.Next.Execute(null);
                wizard.Step.ShouldBe(SetupStep.Machine);
                wizard.Machine.Chassis = ChassisKind.Desktop;
                wizard.Next.Execute(null);
                wizard.Step.ShouldBe(SetupStep.Readings);

                var view = new WizardView { DataContext = wizard };
                var window = new Window
                {
                    Content = view, Width = 880, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                    ShowInTaskbar = false, ShowActivated = false,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    var scroller = view.Content.ShouldBeOfType<ScrollViewer>();
                    var line = Find<TextBlock>(view, block => block.Text == "This machine reads its APC Back-UPS ES 850G2 over USB, so its readings are measured.")
                        .ShouldNotBeNull(theme.ToString());
                    line.IsVisible.ShouldBeTrue(theme.ToString());
                    line.TranslatePoint(new Point(line.ActualWidth, 0), scroller).X.ShouldBeLessThanOrEqualTo(scroller.ViewportWidth + 0.5, theme.ToString());
                    Save(window, 880, 900, $"wizard-readings-ups-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });

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
                    var shell = new ShellViewModel(NowScreen(), BreakdownScreen(), ReportScreen(saver), HouseholdScreen(), SettingsScreen(), WizardScreen(), "0.2.0", updates);
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

    /// <summary>Plan M: the consent dialog first shown and with detail open, Settings scrolled to Privacy with sharing under
    /// way, and What's been sent listing two files with the newest selected.</summary>
    [Fact]
    public void The_consent_dialog_the_privacy_section_and_the_sent_list_draw_in_both_themes()
    {
        Directory.CreateDirectory(Folder);
        var sentFolder = Path.Combine(Path.GetTempPath(), "powerledger-renders-sent-sample");
        Directory.CreateDirectory(sentFolder);
        File.WriteAllBytes(Path.Combine(sentFolder, "2026-09-07.json.gz"), new byte[8_192]);
        File.WriteAllBytes(Path.Combine(sentFolder, "2026-09-06.json.gz"), new byte[9_400]);

        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);

                // 1. The consent dialog: as first shown, all off; then with Hardware and power + Share on and "What's sent" open.
                var consentLink = new FakeLink();
                consentLink.Connect(true);
                var consentFirst = new ConsentViewModel(consentLink, UiThreads.Inline, Consent.Unanswered, _ => { }, _ => { });
                var dialogFirst = new ConsentDialog(consentFirst)
                {
                    Width = 640, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                    ShowInTaskbar = false, ShowActivated = false,
                };
                dialogFirst.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<CheckBox>(dialogFirst, box => box.Content is TextBlock text && text.Text == "Share my detailed data")
                        .ShouldNotBeNull(theme.ToString()).IsChecked.ShouldBe((bool?)false, theme.ToString());
                    Save(dialogFirst, (int)dialogFirst.ActualWidth, (int)dialogFirst.ActualHeight, $"consent-first-{theme}.png");
                }
                finally
                {
                    dialogFirst.Close();
                }

                var consentOpen = new ConsentViewModel(consentLink, UiThreads.Inline, Consent.Unanswered, _ => { }, _ => { })
                {
                    Power = true, Share = true,
                };
                var dialogOpen = new ConsentDialog(consentOpen)
                {
                    Width = 640, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                    ShowInTaskbar = false, ShowActivated = false,
                };
                dialogOpen.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    foreach (var expander in AllOf<Expander>(dialogOpen)) expander.IsExpanded = true;
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<CheckBox>(dialogOpen, box => box.Content is TextBlock text && text.Text == "Share my detailed data")
                        .ShouldNotBeNull(theme.ToString()).IsChecked.ShouldBe((bool?)true, theme.ToString());
                    Save(dialogOpen, (int)dialogOpen.ActualWidth, (int)dialogOpen.ActualHeight, $"consent-open-{theme}.png");
                }
                finally
                {
                    dialogOpen.Close();
                }

                // 2. Settings, scrolled to Privacy: consent all on, an install id, and a status line with a last-sent size and days waiting.
                var privacyLink = new FakeLink
                {
                    Status = Statuses.WithSharing(
                        new Consent(ConsentText.Version, true, true, true, true), installId: "a83f1c6e-9b2d-4a71-8e4c-5f9a0d3b7c21",
                        lastSentAt: Now.AddDays(-1), lastSentBytes: 41_000, daysWaiting: 2),
                };
                privacyLink.Connect(true);
                var settings = new SettingsViewModel(privacyLink, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline,
                    new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, "USD");
                settings.Show();
                var settingsView = new SettingsView { DataContext = settings };
                var settingsWindow = new Window
                {
                    Content = settingsView, Width = 880, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                    ShowInTaskbar = false, ShowActivated = false,
                };
                settingsWindow.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    settings.Privacy.Status.ShouldBe("Last sent 7 Sep 2026 · 41 KB · 2 days waiting", theme.ToString());
                    var scroller = settingsView.Content.ShouldBeOfType<ScrollViewer>();
                    var header = Find<TextBlock>(settingsView, text => text.Text == "PRIVACY").ShouldNotBeNull(theme.ToString());
                    scroller.ScrollToVerticalOffset(Math.Max(0, header.TranslatePoint(default, scroller).Y - 12));
                    Pump(TimeSpan.FromMilliseconds(300));
                    Save(settingsWindow, 880, 900, $"settings-privacy-{theme}.png");
                }
                finally
                {
                    settingsWindow.Close();
                }

                // 3. What's been sent: two files from a folder fed straight to the view model, the newest selected.
                var sentLink = new FakeLink();
                sentLink.Connect(true);
                var sentModel = new SentViewModel(sentLink, UiThreads.Inline, sentFolder, English, _ => { });
                var sentWindow = new SentWindow(sentModel)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                };
                sentWindow.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    sentModel.Rows.Count.ShouldBe(2, theme.ToString());
                    var list = Find<ListBox>(sentWindow).ShouldNotBeNull(theme.ToString());
                    // RowSelected (SentWindow.xaml.cs) opens the row's JSON and clears the selection at once; unhooked here,
                    // without touching the window's own code, only so the screenshot can hold a selected row.
                    var rowSelected = typeof(SentWindow).GetMethod("RowSelected", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    list.RemoveHandler(Selector.SelectionChangedEvent,
                        (SelectionChangedEventHandler)Delegate.CreateDelegate(typeof(SelectionChangedEventHandler), sentWindow, rowSelected));
                    list.SelectedIndex = 0;
                    Pump(TimeSpan.FromMilliseconds(300));
                    list.SelectedIndex.ShouldBe(0, theme.ToString());
                    Save(sentWindow, (int)sentWindow.ActualWidth, (int)sentWindow.ActualHeight, $"sent-{theme}.png");
                }
                finally
                {
                    sentWindow.Close();
                }
            }
        });

        static IEnumerable<T> AllOf<T>(DependencyObject root)
            where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var deeper in AllOf<T>(child)) yield return deeper;
            }
        }
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

    /// <summary>Settings just after DDR5 was ticked, which saved itself, so the line that says how the last save went shows.</summary>
    private static void SavedSettings(ShellViewModel shell)
    {
        shell.Page = Page.Settings;
        shell.Settings.Service.RamIsDdr5 = true;
        shell.Settings.Service.Message.ShouldBe("Saved.");
    }

    /// <summary>The wizard at its longest step: the machine, for a desktop, which adds the power supply's rating, with two
    /// external monitors listed.</summary>
    private static void MachineStep(ShellViewModel shell)
    {
        LaptopStep(shell);
        shell.Wizard.Machine.Chassis = ChassisKind.Desktop;
    }

    /// <summary>The wizard's machine step as the service detected it, a laptop, which asks of each of the two external
    /// monitors listed whether it has a plug of its own.</summary>
    private static void LaptopStep(ShellViewModel shell)
    {
        shell.BeginSetup();
        shell.Wizard.Next.Execute(null);
        shell.Wizard.Step.ShouldBe(SetupStep.Machine);
        shell.Wizard.Machine.Chassis.ShouldBe(ChassisKind.Laptop);
        shell.Wizard.Machine.Monitors.Count.ShouldBe(2);
    }

    /// <summary>On a screen shorter than the dialog (a 1366 × 768 laptop's work area is about 728 pixels high), the text
    /// scrolls and the three buttons stay in view, so the choice can always be made.</summary>
    [Fact]
    public void The_consent_dialogs_buttons_stay_in_view_when_the_screen_is_too_short_for_all_of_it()
    {
        OnUi(() =>
        {
            UseTheme(Theme.Light);
            var link = new FakeLink();
            link.Connect(true);
            var dialog = new ConsentDialog(new ConsentViewModel(link, UiThreads.Inline, Consent.Unanswered, _ => { }, _ => { }))
            {
                Width = 640, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
                ShowInTaskbar = false, ShowActivated = false,
            };
            dialog.MaxHeight = 420;
            dialog.Show();
            try
            {
                Pump(TimeSpan.FromMilliseconds(300));
                dialog.ActualHeight.ShouldBeLessThanOrEqualTo(420);
                var content = (FrameworkElement)dialog.Content;
                foreach (var label in new[] { "Allow all", "Allow none", "Save choices" })
                {
                    var button = Find<Button>(dialog, candidate => candidate.Content as string == label).ShouldNotBeNull(label);
                    button.TranslatePoint(new Point(0, button.ActualHeight), content).Y.ShouldBeLessThanOrEqualTo(content.ActualHeight, label);
                }
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    /// <summary>The Join prompt (households design §2, §3): the service's own wording, which already carries the leave
    /// warning for a PC already in one, the comparison code, and its buttons fitting a short screen, in both themes.</summary>
    [Fact]
    public void The_join_prompt_shows_the_household_name_the_code_and_the_leave_warning()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink
                {
                    Status = Statuses.Running() with { Household = new HouseholdStatus("hh1", "aaaa", "This-PC", ChassisKind.Desktop, true, [], null) },
                };
                link.Connect(true);
                var notice = new HouseholdNotice(
                    NoticeKind.JoinPrompt, "p1", "Join Desktop-7's household? Joining leaves the household this PC is in now.", "Desktop-7", "482 913",
                    Now.AddMinutes(2));
                var model = new JoinPromptViewModel(link, UiThreads.Inline, new FakeTimeProvider(Now), notice);
                var window = new JoinPromptWindow(model)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 420,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<TextBlock>(window, t => t.Text == "Join Desktop-7's household? Joining leaves the household this PC is in now.").ShouldNotBeNull(theme.ToString());
                    Find<TextBlock>(window, t => t.Text == "Check Desktop-7 shows this code").ShouldNotBeNull(theme.ToString());
                    Find<TextBlock>(window, t => t.Text == "482 913").ShouldNotBeNull(theme.ToString());
                    window.ActualHeight.ShouldBeLessThanOrEqualTo(420);
                    var content = (FrameworkElement)window.Content;
                    foreach (var label in new[] { "Don't join", "Join" })
                    {
                        var button = Find<Button>(window, b => Equals(b.Content, label)).ShouldNotBeNull($"{label} on {theme}");
                        button.TranslatePoint(new Point(0, button.ActualHeight), content).Y.ShouldBeLessThanOrEqualTo(content.ActualHeight, $"{label} on {theme}");
                    }
                    Save(window, 420, (int)window.ActualHeight, $"join-prompt-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>Service gap C reported: a code pairing's Join prompt has no FromName and no comparison code — the
    /// window shows the service's own no-name wording, nothing that reads "null" or empty quotes, and fits a short
    /// screen, in both themes.</summary>
    [Fact]
    public void The_join_prompt_for_a_code_pairing_shows_no_name_and_no_null_or_empty_quotes()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink();
                link.Connect(true);
                var notice = new HouseholdNotice(
                    NoticeKind.JoinPrompt, "p1", "Join the household of the PC that made this code?", null, null, Now.AddMinutes(2));
                var model = new JoinPromptViewModel(link, UiThreads.Inline, new FakeTimeProvider(Now), notice);
                var window = new JoinPromptWindow(model)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 420,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<TextBlock>(window, t => t.Text == "Join the household of the PC that made this code?").ShouldNotBeNull(theme.ToString());
                    Find<TextBlock>(window, t => t.Text != null && t.Text.Contains("null")).ShouldBeNull(theme.ToString());
                    window.ActualHeight.ShouldBeLessThanOrEqualTo(420);
                    Save(window, 420, (int)window.ActualHeight, $"join-prompt-code-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>The Approve prompt (households design §7, review finding A2): the service's own wording, with the
    /// approver's own check code shown prominently, and its buttons fitting a short screen, in both themes.</summary>
    [Fact]
    public void The_approve_prompt_shows_the_comparison_code_prominently()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink();
                link.Connect(true);
                var notice = new HouseholdNotice(
                    NoticeKind.ApprovePrompt, "p2", "A PC signed in as you asks to join your household. Approve it?", null, "482 913", Now.AddMinutes(2));
                var model = new ApprovePromptViewModel(link, UiThreads.Inline, new FakeTimeProvider(Now), notice);
                var window = new ApprovePromptWindow(model)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 420,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<TextBlock>(window, t => t.Text == "A PC signed in as you asks to join your household. Approve it?").ShouldNotBeNull(theme.ToString());
                    Find<TextBlock>(window, t => t.Text == "Check the other PC shows this code").ShouldNotBeNull(theme.ToString());
                    Find<TextBlock>(window, t => t.Text == "482 913").ShouldNotBeNull(theme.ToString());
                    window.ActualHeight.ShouldBeLessThanOrEqualTo(420);
                    var content = (FrameworkElement)window.Content;
                    foreach (var label in new[] { "Don't approve", "Approve" })
                    {
                        var button = Find<Button>(window, b => Equals(b.Content, label)).ShouldNotBeNull($"{label} on {theme}");
                        button.TranslatePoint(new Point(0, button.ActualHeight), content).Y.ShouldBeLessThanOrEqualTo(content.ActualHeight, $"{label} on {theme}");
                    }
                    // Review round: Approve can't be pressed before the code is even read.
                    Find<Button>(window, b => Equals(b.Content, "Approve")).ShouldNotBeNull(theme.ToString()).IsEnabled.ShouldBeFalse(theme.ToString());
                    Save(window, 420, (int)window.ActualHeight, $"approve-prompt-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>Review round: the App has no name or device ID to tell requests apart by, so a prompt replacing one that
    /// closed within the last two minutes shows a line above the code that it may have changed, in both themes.</summary>
    [Fact]
    public void The_approve_prompt_shows_a_warning_when_the_request_changed()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink();
                link.Connect(true);
                var notice = new HouseholdNotice(
                    NoticeKind.ApprovePrompt, "p2", "A PC signed in as you asks to join your household. Approve it?", null, "482 913", Now.AddMinutes(2));
                var model = new ApprovePromptViewModel(link, UiThreads.Inline, new FakeTimeProvider(Now), notice, requestChanged: true);
                var window = new ApprovePromptWindow(model)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 420,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<TextBlock>(window, t => t.Text == ApprovePromptViewModel.RequestChangedWarning).ShouldNotBeNull(theme.ToString())
                        .Visibility.ShouldBe(Visibility.Visible, theme.ToString());
                    window.ActualHeight.ShouldBeLessThanOrEqualTo(420);
                    Save(window, 420, (int)window.ActualHeight, $"approve-prompt-request-changed-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>The Confirm join prompt (households design §7, plan 0.9): sent before the approver has actually
    /// approved anything, so its heading never says "approved"; the comparison code shows prominently, and its buttons
    /// fit a short screen, in both themes.</summary>
    [Fact]
    public void The_confirm_join_prompt_shows_the_heading_and_the_comparison_code()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink();
                link.Connect(true);
                var notice = new HouseholdNotice(
                    NoticeKind.ConfirmJoin, "p3", "Does your other PC show 482 913? Approve it there too.", null, "482 913",
                    Now.AddMinutes(2));
                var model = new ConfirmJoinViewModel(link, UiThreads.Inline, new FakeTimeProvider(Now), notice);
                var window = new ConfirmJoinWindow(model)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 420,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<TextBlock>(window, t => t.Text == "Does your other PC show 482 913? Approve it there too.").ShouldNotBeNull(theme.ToString());
                    Find<TextBlock>(window, t => t.Text != null && t.Text.Contains("approved")).ShouldBeNull(theme.ToString());
                    Find<TextBlock>(window, t => t.Text == "482 913").ShouldNotBeNull(theme.ToString());
                    window.ActualHeight.ShouldBeLessThanOrEqualTo(420);
                    var content = (FrameworkElement)window.Content;
                    foreach (var label in new[] { "They don't match", "Codes match" })
                    {
                        var button = Find<Button>(window, b => Equals(b.Content, label)).ShouldNotBeNull($"{label} on {theme}");
                        button.TranslatePoint(new Point(0, button.ActualHeight), content).Y.ShouldBeLessThanOrEqualTo(content.ActualHeight, $"{label} on {theme}");
                    }
                    Save(window, 420, (int)window.ActualHeight, $"confirm-join-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>Add a PC (households design §2, §3, §4, review findings A1/A3): once the key exchange with a PC on this
    /// network is done, the adder's own confirm check shows its question and code prominently and fits a short screen.</summary>
    [Fact]
    public void Add_a_pc_shows_the_confirm_check_prominently_and_it_fits_a_short_screen()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink();
                link.Connect(true);
                var model = new AddPcViewModel(link, UiThreads.Inline, new FakeTimeProvider(Now));
                link.PushNotice(new HouseholdNotice(NoticeKind.ConfirmCode, "confirm-1", "Does Laptop-2 show 482 913?", "Laptop-2", "482 913", Now.AddMinutes(2)));
                var window = new AddPcWindow(model)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 420,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<TextBlock>(window, t => t.Text == "Does Laptop-2 show 482 913?").ShouldNotBeNull(theme.ToString());
                    Find<TextBlock>(window, t => t.Text == "482 913").ShouldNotBeNull(theme.ToString());
                    Find<Button>(window, b => Equals(b.Content, "Codes match")).ShouldNotBeNull(theme.ToString());
                    Find<Button>(window, b => Equals(b.Content, "Cancel pairing")).ShouldNotBeNull(theme.ToString());
                    window.ActualHeight.ShouldBeLessThanOrEqualTo(420);
                    Save(window, 480, (int)window.ActualHeight, $"add-pc-confirm-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>The recovery code (households design §7, task 0.8, review finding A6): shown once from its own pushed
    /// notice, with Copy, Save as text file and OK fitting a short screen, in both themes.</summary>
    [Fact]
    public void The_recovery_code_shows_once_with_its_buttons_fitting_a_short_screen()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink();
                link.Connect(true);
                var notice = new HouseholdNotice(
                    NoticeKind.RecoveryCode, "recovery-1", "Here's your recovery code.", null, null, null, "K7QM-2XHD-9PW4-R8TA-VMNP-3QWE");
                var model = new RecoveryCodeViewModel(link, notice, new FakeSaver(), _ => { });
                var window = new RecoveryCodeWindow(model)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 420,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<TextBox>(window, t => t.Text == "K7QM-2XHD-9PW4-R8TA-VMNP-3QWE").ShouldNotBeNull(theme.ToString());
                    window.ActualHeight.ShouldBeLessThanOrEqualTo(420);
                    var content = (FrameworkElement)window.Content;
                    foreach (var label in new[] { "Copy", "Save as text file", "OK" })
                    {
                        var button = Find<Button>(window, b => Equals(b.Content, label)).ShouldNotBeNull($"{label} on {theme}");
                        button.TranslatePoint(new Point(0, button.ActualHeight), content).Y.ShouldBeLessThanOrEqualTo(content.ActualHeight, $"{label} on {theme}");
                    }
                    Save(window, 440, (int)window.ActualHeight, $"recovery-code-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>Task 0.8's removeOldRows, review follow-up: a left member offers Remove its rows, and asking shows what
    /// will happen with Cancel and Confirm, in both themes.</summary>
    [Fact]
    public void Removing_a_left_members_rows_shows_its_confirm_text_and_buttons()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink
                {
                    Status = Statuses.Running() with { Household = new HouseholdStatus("hh1", "aaaa", "Desktop-1", ChassisKind.Desktop, true, [], null) },
                };
                link.Connect(true);
                var history = new FakeHouseholdHistory
                {
                    Answer = _ => new HouseholdSnapshot(
                        new HouseholdRangeTotals(0, [], []), new HouseholdRangeTotals(0, [], []), new HouseholdRangeTotals(0, [], []),
                        [
                            new HouseholdMemberRow("aaaa", "Desktop-1", ChassisKind.Desktop, Now.AddDays(-40), null, Now),
                            new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now.AddDays(-20), Now.AddDays(-3), Now.AddDays(-3)),
                        ]),
                };
                var model = new HouseholdViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, FakeAccount.Model(link));
                model.Show();
                model.AskRemove.Execute(model.Members.Single(m => m.DeviceId == "bbbb"));

                var window = new Window
                {
                    Content = new HouseholdView { DataContext = model }, Width = 480, SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 560,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<TextBlock>(window, t => t.Text == "Remove Laptop-2's rows? This can't be undone.").ShouldNotBeNull(theme.ToString());
                    Find<Button>(window, b => Equals(b.Content, "Cancel")).ShouldNotBeNull(theme.ToString());
                    Find<Button>(window, b => Equals(b.Content, "Confirm")).ShouldNotBeNull(theme.ToString());
                    Save(window, 480, (int)window.ActualHeight, $"remove-left-member-rows-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>Task 0.8's removeOldRows with no device named, review follow-up: with no household but old rows on
    /// file, Remove the old household's rows shows what will happen with Cancel and Confirm, in both themes.</summary>
    [Fact]
    public void Removing_the_old_households_rows_shows_its_confirm_text_and_buttons()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink
                {
                    Status = Statuses.Running() with { Household = new HouseholdStatus(null, "aaaa", "Desktop-1", ChassisKind.Desktop, true, [], null) },
                };
                link.Connect(true);
                var history = new FakeHouseholdHistory
                {
                    Answer = _ => FakeHouseholdHistory.Empty with
                    {
                        Members = [new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now.AddDays(-20), Now.AddDays(-3), Now.AddDays(-3))],
                    },
                };
                var model = new HouseholdViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, FakeAccount.Model(link));
                model.Show();
                model.AskRemoveAllOldRows.Execute(null);

                var window = new Window
                {
                    Content = new HouseholdView { DataContext = model }, Width = 480, SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 560,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<TextBlock>(window, t => t.Text == "Remove the old household's rows? This can't be undone.").ShouldNotBeNull(theme.ToString());
                    Find<Button>(window, b => Equals(b.Content, "Cancel")).ShouldNotBeNull(theme.ToString());
                    Find<Button>(window, b => Equals(b.Content, "Confirm")).ShouldNotBeNull(theme.ToString());
                    Save(window, 480, (int)window.ActualHeight, $"remove-old-household-rows-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>Plan 0.9: a typed recovery code warns before the browser opens, since it removes the household's other
    /// PCs, with Continue and Cancel fitting a short screen, in both themes.</summary>
    [Fact]
    public void A_recovery_code_sign_in_shows_its_warning_and_buttons()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink();
                link.Connect(true);
                var signIn = new SignIn(() => new FakeLoopbackServer(), _ => { }, new System.Net.Http.HttpClient(), new FakeTimeProvider(Now));
                var account = new SignInViewModel(link, new FakeUiSettings(), signIn, UiThreads.Inline, "ms-client", "google-client");
                var model = new HouseholdViewModel(link, new FakeHouseholdHistory(), UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, account);
                model.Show();
                account.RecoveryCodeInput = "K7QM-2XHD-9PW4-R8TA-VMNP-3QWE";
                account.SignInWithMicrosoft.Execute(null);

                var window = new Window
                {
                    Content = new HouseholdView { DataContext = model }, Width = 480, SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 560,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<TextBlock>(window, t => t.Text == SignInViewModel.RecoveryWarning).ShouldNotBeNull(theme.ToString());
                    Find<Button>(window, b => Equals(b.Content, "Cancel")).ShouldNotBeNull(theme.ToString());
                    Find<Button>(window, b => Equals(b.Content, "Continue")).ShouldNotBeNull(theme.ToString());
                    window.ActualHeight.ShouldBeLessThanOrEqualTo(560);
                    Save(window, 480, (int)window.ActualHeight, $"recovery-sign-in-warning-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>Security round, review: with no Google secret built in, only Microsoft is offered — no Google button,
    /// and no "isn't set up yet" placeholder either.</summary>
    [Fact]
    public void With_only_a_microsoft_client_id_only_microsoft_is_offered()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink();
                link.Connect(true);
                var signIn = new SignIn(() => new FakeLoopbackServer(), _ => { }, new System.Net.Http.HttpClient(), new FakeTimeProvider(Now));
                var account = new SignInViewModel(link, new FakeUiSettings(), signIn, UiThreads.Inline, "ms-client", "");
                var model = new HouseholdViewModel(link, new FakeHouseholdHistory(), UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, account);
                model.Show();

                var window = new Window
                {
                    Content = new HouseholdView { DataContext = model }, Width = 480, SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 560,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    // Find walks the visual tree regardless of Visibility, so a Collapsed element is still findable —
                    // the check that matters is its Visibility, not whether Find returns it at all.
                    Find<Button>(window, b => Equals(b.Content, "Sign in with Microsoft")).ShouldNotBeNull(theme.ToString())
                        .Visibility.ShouldBe(Visibility.Visible, theme.ToString());
                    Find<Button>(window, b => Equals(b.Content, "Sign in with Google")).ShouldNotBeNull(theme.ToString())
                        .Visibility.ShouldBe(Visibility.Collapsed, theme.ToString());
                    Find<TextBlock>(window, t => t.Text == SignInViewModel.Unavailable).ShouldNotBeNull(theme.ToString())
                        .Visibility.ShouldBe(Visibility.Collapsed, theme.ToString());
                    Save(window, 480, (int)window.ActualHeight, $"sign-in-only-microsoft-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>Security round, review: with no client at all, the section explains sign-in isn't available in this
    /// build instead of showing any button.</summary>
    [Fact]
    public void With_no_client_at_all_sign_in_explains_it_is_unavailable()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var link = new FakeLink();
                link.Connect(true);
                var signIn = new SignIn(() => new FakeLoopbackServer(), _ => { }, new System.Net.Http.HttpClient(), new FakeTimeProvider(Now));
                var account = new SignInViewModel(link, new FakeUiSettings(), signIn, UiThreads.Inline, "", "");
                var model = new HouseholdViewModel(link, new FakeHouseholdHistory(), UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, account);
                model.Show();

                var window = new Window
                {
                    Content = new HouseholdView { DataContext = model }, Width = 480, SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 560,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    // Find walks the visual tree regardless of Visibility, so a Collapsed element is still findable —
                    // the check that matters is its Visibility, not whether Find returns it at all.
                    Find<TextBlock>(window, t => t.Text == SignInViewModel.Unavailable).ShouldNotBeNull(theme.ToString())
                        .Visibility.ShouldBe(Visibility.Visible, theme.ToString());
                    Find<Button>(window, b => Equals(b.Content, "Sign in with Microsoft")).ShouldNotBeNull(theme.ToString())
                        .Visibility.ShouldBe(Visibility.Collapsed, theme.ToString());
                    Find<Button>(window, b => Equals(b.Content, "Sign in with Google")).ShouldNotBeNull(theme.ToString())
                        .Visibility.ShouldBe(Visibility.Collapsed, theme.ToString());
                    Save(window, 480, (int)window.ActualHeight, $"sign-in-unavailable-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>Send feedback's rail button (bug-icon glyph, spec's feedback feature): sits at the foot of the rail,
    /// its tooltip names what it's for, and pressing it fires the App's own open request.</summary>
    [Fact]
    public void The_rails_feedback_button_shows_and_asks_to_open_the_window()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            using var saver = new FakeSaver();
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var shell = new ShellViewModel(NowScreen(), BreakdownScreen(), ReportScreen(saver), HouseholdScreen(), SettingsScreen(), WizardScreen(), "0.2.0");
                var window = new MainWindow
                {
                    DataContext = shell, WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    var button = Find<Button>(window, b => AutomationProperties.GetName(b) == "Send feedback").ShouldNotBeNull(theme.ToString());
                    button.IsVisible.ShouldBeTrue(theme.ToString());
                    button.ToolTip.ShouldBe("Send feedback or report a bug", theme.ToString());
                    // App.xaml.cs wires FeedbackRequested to open the window (ShellViewModelTests covers the command
                    // itself); this only checks the button in the rail is bound to it.
                    button.Command.ShouldBeSameAs(shell.Feedback, theme.ToString());
                    Save(window, (int)window.ActualWidth, (int)window.ActualHeight, $"feedback-button-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
    }

    /// <summary>Send feedback (spec's feedback feature): its heading, images row, log tick and Send/Cancel fit a short
    /// screen, in both themes.</summary>
    [Fact]
    public void Send_feedback_shows_its_fields_and_fits_a_short_screen()
    {
        Directory.CreateDirectory(Folder);
        OnUi(() =>
        {
            foreach (var theme in new[] { Theme.Dark, Theme.Light })
            {
                UseTheme(theme);
                var sender = new FeedbackSender(
                    new FakeHttp().Client(), Path.Combine(Path.GetTempPath(), "pl-feedback-render-tests"), new FakeTimeProvider(Now));
                var model = new FeedbackViewModel(sender, UiThreads.Inline, () => null);
                var window = new SendFeedbackWindow(model, null, new FakeImagePicker())
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
                    MaxHeight = 420,
                };
                window.Show();
                try
                {
                    Pump(TimeSpan.FromMilliseconds(300));
                    Find<TextBlock>(window, t => t.Text == "Tell us what's wrong, or what you'd like").ShouldNotBeNull(theme.ToString());
                    Find<TextBlock>(window, t => t.Text == "This goes to the developer's private issue tracker on GitHub.").ShouldNotBeNull(theme.ToString());
                    Find<CheckBox>(window, c => Equals(c.Content, "Attach the log, it helps with bugs")).ShouldNotBeNull(theme.ToString())
                        .IsChecked.ShouldBe(true, theme.ToString());
                    window.ActualHeight.ShouldBeLessThanOrEqualTo(420);
                    var content = (FrameworkElement)window.Content;
                    foreach (var label in new[] { "Cancel", "Send" })
                    {
                        var button = Find<Button>(window, b => Equals(b.Content, label)).ShouldNotBeNull($"{label} on {theme}");
                        button.TranslatePoint(new Point(0, button.ActualHeight), content).Y.ShouldBeLessThanOrEqualTo(content.ActualHeight, $"{label} on {theme}");
                    }
                    Save(window, 460, (int)window.ActualHeight, $"send-feedback-{theme}.png");
                }
                finally
                {
                    window.Close();
                }
            }
        });
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

    private static ShellViewModel Shell(FakeSaver saver)
        => new(NowScreen(), BreakdownScreen(), ReportScreen(saver), HouseholdScreen(), SettingsScreen(), WizardScreen(), "0.1.0");

    private static void Save(Visual visual, int width, int height, string name) => UiHarness.Render(visual, width, height, name);

    /// <summary>A Tuesday afternoon eight days into September: asleep until 07:30, a working morning, an idle patch, a peak at
    /// 14:00; and now, on battery, the two external monitors Settings lists: the Dell, on a plug of its own, counted on top of
    /// the battery's report, and the portable one, running off the laptop, which the report already holds.</summary>
    private static NowViewModel NowScreen()
    {
        var link = new FakeLink { Status = Statuses.WithMonitors(Statuses.Dell, Statuses.Portable) };
        var ownPlug = Statuses.Dell.WattsNow;
        var monitors = ownPlug + Statuses.Portable.WattsNow;
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
            link.Push(Frames.At(Now.AddSeconds(-s), totalW: watts + ownPlug, cpu: watts * 0.43, gpu: watts * 0.12, display: 4.0, monitors: monitors));
        }
        return model;
    }

    /// <summary>The same machine's last seven days: asleep overnight, working days, quiet evenings; a laptop, so its display
    /// band holds its panel.</summary>
    private static BreakdownViewModel BreakdownScreen()
    {
        var link = new FakeLink();
        link.Connect(true);
        var history = new FakeRangeHistory { Answer = range => Reports.Typical(range) with { Series = Week(range) } };
        var model = new BreakdownViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English);
        return model;
    }

    /// <summary>September so far on the same machine, with a tariff and a plan that sleeps after three hours.</summary>
    private static ReportViewModel ReportScreen(FakeSaver saver)
    {
        var history = new FakeRangeHistory { Answer = Month };
        return new ReportViewModel(new FakeLink(), history, new FakeHouseholdHistory(), new FakeSleep(), saver, _ => [], UiThreads.Inline,
            new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, 0.38);
    }

    /// <summary>A household of two PCs: this desktop, well ahead this month, and a laptop last seen three days ago.</summary>
    private static HouseholdViewModel HouseholdScreen()
    {
        var link = new FakeLink
        {
            Status = Statuses.Running() with
            {
                Household = new HouseholdStatus("hh1", "aaaa", "Desktop-1", ChassisKind.Desktop, true, [], null),
            },
        };
        link.Connect(true);
        var history = new FakeHouseholdHistory
        {
            Answer = _ => new HouseholdSnapshot(
                new HouseholdRangeTotals(1.62, [new CurrencyCost("USD", 0.28m)], []),
                new HouseholdRangeTotals(11.4, [new CurrencyCost("USD", 1.94m)], []),
                new HouseholdRangeTotals(46.8, [new CurrencyCost("USD", 7.96m)], [new DeviceEnergy("aaaa", 34.2), new DeviceEnergy("bbbb", 12.6)]),
                [
                    new HouseholdMemberRow("aaaa", "Desktop-1", ChassisKind.Desktop, Now.AddDays(-40), null, Now.AddMinutes(-2)),
                    new HouseholdMemberRow("bbbb", "Laptop-2", ChassisKind.Laptop, Now.AddDays(-20), null, Now.AddDays(-3)),
                ]),
        };
        return new HouseholdViewModel(link, history, UiThreads.Inline, new FakeTimeProvider(Now), TimeZoneInfo.Utc, English, FakeAccount.Model(link));
    }

    /// <summary>Settings against a running service, with a tariff, this laptop's detection, two external monitors — one in
    /// Energy Star's list whose brightness was read, on a plug of its own, and a portable one estimated from its size whose
    /// brightness wasn't, running off the laptop — and a UPS and a power supply read over USB. The service is
    /// <paramref name="link"/> when it is given.</summary>
    private static SettingsViewModel SettingsScreen(FakeLink? link = null)
    {
        link ??= new FakeLink();
        link.Status = Statuses.WithMonitors(Statuses.Dell, Statuses.Portable) with { PowerDevices = [Statuses.Ups, Statuses.PowerSupply] };
        link.Connect(true);
        return new SettingsViewModel(link, new FakeMachineHistory(), new FakeUiSettings(), UiThreads.Inline, new FakeTimeProvider(Now),
            TimeZoneInfo.Utc, English, "USD");
    }

    /// <summary>The wizard against a running service that detected two external monitors: one in Energy Star's list, one
    /// estimated from its size.</summary>
    private static WizardViewModel WizardScreen()
    {
        var link = new FakeLink { Status = Statuses.WithMonitors() };
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
