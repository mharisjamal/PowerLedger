using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Time.Testing;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Aero look design §5: the glass watts overlay over <see cref="NowViewModel.Live"/>, drawn in both themes over a dark
/// and a bright desktop, with and without its sparkline, and with no reading; and its menu, each choice saved through
/// <see cref="SettingsViewModel.Overlay"/>. PNGs go to <c>%TEMP%\powerledger-renders\aero-overlay-*</c>. Where it sits on
/// the real displays is <see cref="OverlayPlacementTests"/>' and the on-screen check's.
/// </summary>
[Trait("Category", "UI")]
public class OverlayWindowTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static SettingsViewModel Settings(FakeUiSettings ui) => new(
        new FakeLink(), new FakeMachineHistory(), ui, UiThreads.Inline, new FakeTimeProvider(MidnightFixtures.Now), TimeZoneInfo.Utc, English, "USD");

    /// <summary>A Now screen that has had no reading.</summary>
    private static NowViewModel Waiting() => new(
        new FakeLink(), new FakeHistory(), UiThreads.Inline, new FakeTimeProvider(MidnightFixtures.Now), TimeZoneInfo.Utc, English, 0.4, () => { });

    private static OverlayWindow Overlay(NowViewModel now, SettingsViewModel settings, Theme theme, OverlaySettings shown)
    {
        var window = AeroHost.Dressed(new OverlayWindow(now, settings) { Placing = false, Left = -20000, Top = 0 }, theme);
        window.Apply(shown);
        window.ShowOverlay();
        UiHarness.Pump(TimeSpan.FromMilliseconds(700));   // the pill's entrance is over
        window.UpdateLayout();
        return window;
    }

    [Theory]
    [InlineData("Dark", "reading", true)]
    [InlineData("Dark", "reading", false)]
    [InlineData("Dark", "none", true)]
    [InlineData("Light", "reading", true)]
    [InlineData("Light", "reading", false)]
    [InlineData("Light", "none", true)]
    public void It_draws_the_watts_the_sparkline_or_no_reading(string themeName, string reading, bool sparkline)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            var now = reading == "reading" ? MidnightFixtures.NowScreen() : Waiting();
            var window = Overlay(now, Settings(new FakeUiSettings()), theme, OverlaySettings.Default with { Enabled = true, Sparkline = sparkline });
            try
            {
                var number = UiHarness.Find<OverlayDigits>(window)!;
                var spark = UiHarness.Find<Aero.Sparkline>(window)!;
                var none = UiHarness.Find<TextBlock>(window, t => t.Text == "No reading")!;
                if (reading == "reading")
                {
                    number.Value.ShouldBe((int)Math.Round(now.Live.Watts));
                    window.Reading.ShouldBe($"{number.Value} watts now");
                    none.Visibility.ShouldBe(Visibility.Collapsed);
                    spark.Samples!.Count.ShouldBeGreaterThan(30, "a minute of readings, of which the sparkline draws the last 30 s");
                }
                else
                {
                    window.Reading.ShouldBe("No reading");
                    none.Visibility.ShouldBe(Visibility.Visible);
                    number.Visibility.ShouldBe(Visibility.Collapsed);
                }
                spark.Visibility.ShouldBe(sparkline ? Visibility.Visible : Visibility.Collapsed);
                var room = (FrameworkElement)window.Content;
                room.ActualWidth.ShouldBeGreaterThan(80);
                room.ActualHeight.ShouldBe(44, 0.5);

                Directory.CreateDirectory(UiHarness.Folder);
                foreach (var (desk, backdrop) in new[] { ("dark", Colors.Black), ("bright", Color.FromRgb(0xE8, 0xEC, 0xF2)) })
                {
                    window.Background = new SolidColorBrush(backdrop);
                    window.UpdateLayout();
                    var name = $"aero-overlay-{themeName.ToLowerInvariant()}-{reading}-{(sparkline ? "spark" : "plain")}-{desk}.png";
                    UiHarness.Render(window, (int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), name);
                    File.Exists(Path.Combine(UiHarness.Folder, name)).ShouldBeTrue();
                }
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void Each_reading_rolls_the_number_to_the_new_watts()
        => UiHarness.OnUi(() =>
        {
            var link = new FakeLink();
            var now = new NowViewModel(link, new FakeHistory(), UiThreads.Inline, new FakeTimeProvider(MidnightFixtures.Now), TimeZoneInfo.Utc, English, 0.4, () => { });
            link.Connect(true);
            var window = Overlay(now, Settings(new FakeUiSettings()), Theme.Dark, OverlaySettings.Default with { Enabled = true });
            try
            {
                window.Reading.ShouldBe("No reading");

                link.Push(Frames.At(MidnightFixtures.Now, totalW: 212.4));

                UiHarness.Find<OverlayDigits>(window)!.Value.ShouldBe(212);
                window.Reading.ShouldBe("212 watts now");
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void Its_menu_saves_each_choice_through_settings()
        => UiHarness.OnUi(() =>
        {
            var ui = new FakeUiSettings { Current = UiPreferences.Default with { Overlay = OverlaySettings.Default with { Enabled = true } } };
            var settings = Settings(ui);
            var window = Overlay(MidnightFixtures.NowScreen(), settings, Theme.Dark, settings.Overlay);
            try
            {
                void Choose(string header)
                {
                    var item = window.OpenMenu().Items.OfType<MenuItem>().Single(i => (string)i.Header == header);
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    window.Apply(settings.Overlay);   // as the host does on the change
                }

                window.OpenMenu().Items.OfType<MenuItem>().Where(i => i.IsChecked).Select(i => (string)i.Header)
                    .ShouldBe(["Top right", "100%", "Show the last 30 seconds"]);

                Choose("Bottom left");
                settings.Overlay.Position.ShouldBe(OverlayPosition.BottomLeft);
                Choose("Free (drag it anywhere)");
                settings.Overlay.Position.ShouldBe(OverlayPosition.Free);
                Choose("70%");
                settings.Overlay.Opacity.ShouldBe(0.7);
                Choose("Show the last 30 seconds");
                settings.Overlay.Sparkline.ShouldBeFalse();
                UiHarness.Find<Aero.Sparkline>(window)!.Visibility.ShouldBe(Visibility.Collapsed);
                Choose("Close overlay");
                settings.Overlay.Enabled.ShouldBeFalse();

                ui.Changes.ShouldBe(["overlay on BottomLeft", "overlay on Free", "overlay on Free", "overlay on Free", "overlay off Free"]);
                window.OpenMenu().Items.OfType<MenuItem>().Where(i => i.IsChecked).Select(i => (string)i.Header)
                    .ShouldBe(["Free (drag it anywhere)", "70%"]);
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>Aero look design §3: the pill takes the glass as Settings has it, live, as the main window does.</summary>
    [Fact]
    public void It_takes_the_accent_chosen_in_settings_at_once()
        => UiHarness.OnUi(() =>
        {
            var app = Application.Current.Resources.MergedDictionaries;
            var before = app.Count;
            using var theme = new ThemeManager(Application.Current, ThemeChoice.Dark);
            if (app.Count == before + 1) app.RemoveAt(0);   // the palette goes on the window drawn (review 11)
            var settings = Settings(new FakeUiSettings());
            var window = AeroHost.Dressed(new OverlayWindow(MidnightFixtures.NowScreen(), settings, theme) { Placing = false, Left = -20000, Top = 0 }, Theme.Dark);
            try
            {
                window.Apply(OverlaySettings.Default with { Enabled = true });
                window.ShowOverlay();
                Color Dot() => ((SolidColorBrush)MidnightHost.AllOf<System.Windows.Shapes.Ellipse>(window).First().Fill).Color;
                Dot().ShouldBe((Color)window.FindResource("A.C.Accent.Lime"));

                settings.GlassSection.Accent = GlassAccent.Rose;
                UiHarness.Pump(TimeSpan.FromMilliseconds(100));   // the material repaints at background priority

                Dot().ShouldBe((Color)window.FindResource("A.C.Accent.Rose"));
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>The overlay is a tool window: never in the taskbar or Alt Tab.</summary>
    [Fact]
    public void It_stays_out_of_the_taskbar_and_alt_tab()
        => UiHarness.OnUi(() =>
        {
            var window = Overlay(Waiting(), Settings(new FakeUiSettings()), Theme.Dark, OverlaySettings.Default with { Enabled = true });
            try
            {
                window.ShowInTaskbar.ShouldBeFalse();
                window.Topmost.ShouldBeTrue();
                OverlayWindowProbe.IsToolWindow(window).ShouldBeTrue();
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void Closed_it_fades_and_goes()
        => UiHarness.OnUi(() =>
        {
            var window = Overlay(Waiting(), Settings(new FakeUiSettings()), Theme.Dark, OverlaySettings.Default with { Enabled = true });
            var closed = false;
            window.Closed += (_, _) => closed = true;

            window.CloseOverlay();
            UiHarness.Pump(TimeSpan.FromMilliseconds(500));

            closed.ShouldBeTrue();
        });

    /// <summary>The displays Windows lists here: at least one, one of them the main one, each with a work area inside it.</summary>
    [Fact]
    public void Windows_lists_the_displays_with_their_scales()
    {
        var displays = OverlayNative.Displays();

        displays.ShouldNotBeEmpty();
        displays.Count(d => d.Primary).ShouldBe(1);
        displays.ShouldAllBe(d => d.Scale >= 1 && d.Bounds.Contains(d.WorkArea));
    }

    private static class OverlayWindowProbe
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        public static bool IsToolWindow(Window window)
            => ((long)GetWindowLongPtr(new System.Windows.Interop.WindowInteropHelper(window).Handle, -20) & 0x80) != 0;
    }
}
