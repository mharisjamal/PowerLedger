using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Aero look design §1, §3 and §5: Aero's Settings page, every section in glass with the Glass and Overlay sections first,
/// drawn in both themes with each Glass style chosen, at 960 and 1440 px wide. PNGs go to
/// <c>%TEMP%\powerledger-renders\aero-settings-*</c>. The palette and styles go on the window drawn (review 11).
/// </summary>
[Trait("Category", "UI")]
[Collection(AeroMotionScope.Name)]   // its GlassMaterial sets AeroMotion's override from the settings, one for the process
public class AeroSettingsViewTests
{
    private static (Window Window, Aero.SettingsView View) Page(SettingsViewModel settings, Theme theme, double width)
    {
        settings.Show();
        var view = new Aero.SettingsView { DataContext = settings };
        var window = AeroHost.Dressed(new Window
        {
            Content = view, Width = width, Height = 3400, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
        }, theme);
        window.SetResourceReference(Control.BackgroundProperty, "Brush.Ground");
        // The glass as the settings have it, as the Aero window paints it (GlassMaterial), after the palette so it wins; the
        // theme manager's own palette comes off the application again (review 11).
        var app = Application.Current.Resources.MergedDictionaries;
        var before = app.Count;
        var themes = new ThemeManager(Application.Current, theme == Theme.Dark ? ThemeChoice.Dark : ThemeChoice.Light);
        if (app.Count == before + 1) app.RemoveAt(0);
        var material = GlassMaterial.For(window, settings, themes);
        window.Closed += (_, _) =>
        {
            material.Dispose();
            themes.Dispose();
        };
        window.Show();
        UiHarness.Pump(TimeSpan.FromMilliseconds(600));   // a switch whose value arrives after its template springs there first
        window.UpdateLayout();
        return (window, view);
    }

    /// <summary>The whole page, however tall: a window is held to the screen's height, so the page's panel is drawn onto
    /// the window's ground at its own height rather than the window captured.</summary>
    private static DrawingVisual WholePage(Window window, Aero.SettingsView view, int width)
    {
        var panel = (FrameworkElement)((ScrollViewer)view.Content).Content;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, WholeHeight(view)));
            var left = (width - panel.ActualWidth) / 2;
            var offset = VisualTreeHelper.GetOffset(panel);   // a visual brush draws the panel where it sits in its parent
            drawing.DrawRectangle(new VisualBrush(panel) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(offset.X, offset.Y, panel.ActualWidth, panel.ActualHeight) }, null,
                new Rect(left, panel.Margin.Top, panel.ActualWidth, panel.ActualHeight));
        }
        return visual;
    }

    private static int WholeHeight(Aero.SettingsView view)
    {
        var panel = (FrameworkElement)((ScrollViewer)view.Content).Content;
        return (int)Math.Ceiling(panel.ActualHeight + panel.Margin.Top + panel.Margin.Bottom);
    }

    private static SettingsViewModel Screen(GlassSettings glass, OverlaySettings? overlay = null)
        => MidnightFixtures.SettingsScreen(new FakeUiSettings
        {
            Current = UiPreferences.Default with { LookIntroduced = true, Glass = glass, Overlay = overlay ?? OverlaySettings.Default },
        });

    [Theory]
    [InlineData("Dark", "Clear", 1440)]
    [InlineData("Dark", "Tinted", 1440)]
    [InlineData("Dark", "Dark", 1440)]
    [InlineData("Dark", "Colour", 1440)]
    [InlineData("Light", "Clear", 1440)]
    [InlineData("Light", "Tinted", 1440)]
    [InlineData("Light", "Dark", 1440)]
    [InlineData("Light", "Colour", 1440)]
    [InlineData("Dark", "Colour", 960)]
    [InlineData("Light", "Tinted", 960)]
    public void It_draws_every_section_in_glass_with_the_glass_style_chosen(string themeName, string styleName, int width)
        => UiHarness.OnUi(() =>
        {
            var theme = Enum.Parse<Theme>(themeName);
            var style = Enum.Parse<GlassStyle>(styleName);
            var settings = Screen(GlassSettings.Default with { Style = style, TintColor = "#2A9D8F" });
            var (window, view) = Page(settings, theme, width);
            try
            {
                var titles = MidnightHost.AllOf<TextBlock>(view).Select(t => t.Text).ToList();
                foreach (var section in new[] { "Glass", "Overlay", "Tariff", "Machine", "Sampling and history", "Calibration", "Preferences", "Privacy", "Household", "About" })
                {
                    titles.ShouldContain(section);
                }
                MidnightHost.AllOf<GlassPanel>(view).Count().ShouldBeGreaterThanOrEqualTo(10, "each section a glass pane, and the preview's");
                var styles = MidnightHost.AllOf<RadioButton>(view).Where(r => r.Content is "Clear" or "Tinted" or "Dark" or "Colour" && r.Style == view.FindResource("A.SegOpt")).ToList();
                styles.Single(r => r.IsChecked == true).Content.ShouldBe(styleName);
                var wheel = MidnightHost.AllOf<ColourWheel>(view).Single();
                wheel.IsVisible.ShouldBe(style == GlassStyle.Colour, "the presets and the wheel show for Colour only");
                if (style == GlassStyle.Colour)
                {
                    MidnightHost.AllOf<Button>(view).Count(b => b.Tag is true).ShouldBe(1, "the chosen preset is ringed");
                }

                Directory.CreateDirectory(UiHarness.Folder);
                UiHarness.Render(WholePage(window, view, width), width, WholeHeight(view), $"aero-settings-{themeName.ToLowerInvariant()}-{styleName.ToLowerInvariant()}-{width}.png");
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>A choice made on the page goes through SettingsViewModel.Glass and .Overlay, as the section models do.</summary>
    [Fact]
    public void A_choice_on_the_page_is_saved()
        => UiHarness.OnUi(() =>
        {
            var ui = new FakeUiSettings { Current = UiPreferences.Default with { LookIntroduced = true } };
            var settings = MidnightFixtures.SettingsScreen(ui);
            var (window, view) = Page(settings, Theme.Dark, 1440);
            try
            {
                var dark = MidnightHost.AllOf<RadioButton>(view).First(r => r.Content is "Dark" && r.Style == view.FindResource("A.SegOpt"));
                dark.IsChecked = true;
                var overlay = MidnightHost.AllOf<GlassSwitch>(view).Single(s => System.Windows.Automation.AutomationProperties.GetName(s) == "Watts overlay");
                overlay.IsChecked = true;

                settings.Glass.Style.ShouldBe(GlassStyle.Dark);
                settings.Overlay.Enabled.ShouldBeTrue();
                ui.Changes.ShouldBe(["glass Dark", "overlay on TopRight"]);
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>The switches show what is saved.</summary>
    [Fact]
    public void The_switches_show_the_saved_glass()
        => UiHarness.OnUi(() =>
        {
            var settings = Screen(GlassSettings.Default with { ReduceMotion = false, IncreaseContrast = true });
            var (window, view) = Page(settings, Theme.Dark, 1440);
            try
            {
                GlassSwitch Named(string name) => MidnightHost.AllOf<GlassSwitch>(view).Single(s => System.Windows.Automation.AutomationProperties.GetName(s) == name);

                // No Tilt and parallax in Aero's Settings (0.10.4): the free-form window never tilts.
                MidnightHost.AllOf<GlassSwitch>(view).ShouldNotContain(s => System.Windows.Automation.AutomationProperties.GetName(s) == "Tilt and parallax");
                Named("Increase contrast").KnobOffset.ShouldBe(GlassSwitch.Travel);
                Named("Reduce transparency").KnobOffset.ShouldBe(0);
                Named("Reduce motion").IsChecked.ShouldBe(false);
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>Every style shows what is really behind PowerLedger (0.10.6): Settings has no "Behind the glass" choice,
    /// nor a Frost slider, which would do nothing.</summary>
    [Theory]
    [InlineData("Clear")]
    [InlineData("Tinted")]
    public void The_glass_section_offers_no_backdrop_and_no_frost(string style)
        => UiHarness.OnUi(() =>
        {
            var settings = Screen(GlassSettings.Default with { Style = Enum.Parse<GlassStyle>(style) });
            var (window, view) = Page(settings, Theme.Dark, 1440);
            try
            {
                view.FindName("BackdropChoice").ShouldBeNull();
                view.FindName("ClearNote").ShouldBeNull();
                MidnightHost.AllOf<TextBlock>(view).ShouldNotContain(t => t.Text == "Behind the glass" || t.Text == "Frost");
                MidnightHost.AllOf<RadioButton>(view).ShouldNotContain(r => r.Content as string == "Aero bloom" || r.Content as string == "My desktop");
                MidnightHost.AllOf<Slider>(view).ShouldNotContain(s => System.Windows.Automation.AutomationProperties.GetName(s) == "Frost");
                MidnightHost.AllOf<RadioButton>(view).ShouldNotContain(r => r.Content as string == "Plain");
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>The UPS on another computer (Network UPS Tools, 0.10.5), as Classic and Midnight ask for it, in Aero's own
    /// wells: the server and port, the UPS's name there, a username and a password that goes one way, what the service says
    /// of the server and the note. Drawn in both themes to <c>aero-settings-nut-*.png</c>.</summary>
    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_network_ups_section_is_drawn_in_aero_s_wells(string themeName)
        => UiHarness.OnUi(() =>
        {
            var settings = Screen(GlassSettings.Default);
            var (window, view) = Page(settings, Enum.Parse<Theme>(themeName), 1440);
            try
            {
                settings.Service.NutHost = "nas.local";
                settings.Service.NutUps = "myups";
                settings.Service.ShowNut([new PowerLedger.Contracts.SourceStatus("nut", true, "nas.local can't be reached: nothing answered at that address", 0, null)]);
                window.UpdateLayout();

                var texts = MidnightHost.AllOf<TextBlock>(view).Where(t => t.IsVisible).Select(t => t.Text).ToList();
                texts.ShouldContain("UPS on another computer (NUT)");
                texts.ShouldContain("UPS name on that computer");
                texts.ShouldContain("Username · password, if asked");
                texts.ShouldContain("nas.local can't be reached: nothing answered at that address");
                texts.ShouldContain(t => t.StartsWith("For a UPS that another computer, such as a NAS, shares with Network UPS Tools.", StringComparison.Ordinal));

                TextBox Box(string name) => MidnightHost.AllOf<TextBox>(view).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == name);
                foreach (var name in new[] { "UPS server", "UPS server port", "UPS name on the server", "UPS server username" })
                {
                    Box(name).IsVisible.ShouldBeTrue(name);
                    Box(name).Style.ShouldBe(view.FindResource("Field"), name);
                }
                Box("UPS server").Text.ShouldBe("nas.local");
                Box("UPS server port").Text.ShouldBe("3493");

                // Drawn while the service's word on the server shows, before the save a typed password makes clears it.
                Directory.CreateDirectory(UiHarness.Folder);
                UiHarness.Render(WholePage(window, view, 1440), 1440, WholeHeight(view), $"aero-settings-nut-{themeName.ToLowerInvariant()}.png");

                var secret = MidnightHost.AllOf<PasswordBox>(view).Single();
                System.Windows.Automation.AutomationProperties.GetName(secret).ShouldBe("UPS server password");
                secret.Style.ShouldBe(view.FindResource("A.Secret"));
                secret.ActualHeight.ShouldBe(Box("UPS server username").ActualHeight, 0.5, "the password sits in a well of the field's height");
                secret.Password = "sec ret";
                secret.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, 0, secret, null)
                {
                    RoutedEvent = UIElement.LostKeyboardFocusEvent,
                });
                UiHarness.Pump(TimeSpan.FromMilliseconds(200));
                settings.Service.NutHasPassword.ShouldBeTrue("handed on and saved when the box is left, as Classic and Midnight do");
                window.UpdateLayout();
                MidnightHost.AllOf<TextBlock>(view).ShouldContain(t => t.Text == "saved" && t.IsVisible);

            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>Every control a keyboard reaches has a name a screen reader can say.</summary>
    [Fact]
    public void Every_switch_slider_and_swatch_has_a_name()
        => UiHarness.OnUi(() =>
        {
            var settings = Screen(GlassSettings.Default with { Style = GlassStyle.Colour });
            var (window, view) = Page(settings, Theme.Dark, 1440);
            try
            {
                var unnamed = MidnightHost.AllOf<Control>(view)
                    .Where(c => c is GlassSwitch or Slider || (c is ButtonBase { Content: null, TemplatedParent: null } && c.IsVisible))
                    .Where(c => string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(c)))
                    .Select(c => c.GetType().Name)
                    .ToList();
                unnamed.ShouldBeEmpty();
                string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(MidnightHost.AllOf<ColourWheel>(view).Single())).ShouldBeFalse();
            }
            finally
            {
                window.Close();
            }
        });
}
