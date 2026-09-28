using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using PowerLedger.App.Aero;
using Shouldly;

namespace PowerLedger.App.Tests;

/// <summary>
/// Aero look design §1, §3 and §5: Aero's Settings page, every section in glass with the Glass and Overlay sections first,
/// drawn in both themes with each Glass style chosen, at 960 and 1440 px wide. PNGs go to
/// <c>%TEMP%\powerledger-renders\aero-settings-*</c>. The palette and styles go on the window drawn (review 11).
/// </summary>
[Trait("Category", "UI")]
public class AeroSettingsViewTests
{
    private static (Window Window, Aero.SettingsView View) Page(SettingsViewModel settings, Theme theme, double width)
    {
        var view = new Aero.SettingsView { DataContext = settings };
        var window = AeroHost.Dressed(new Window
        {
            Content = view, Width = width, Height = 3400, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0,
        }, theme);
        window.SetResourceReference(Control.BackgroundProperty, "Brush.Ground");
        window.Show();
        window.UpdateLayout();
        return (window, view);
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
                var styles = MidnightHost.AllOf<RadioButton>(view).Where(r => r.Content is "Clear" or "Tinted" or "Dark" or "Colour" && r.Style == view.FindResource("A.OptItem")).ToList();
                styles.Single(r => r.IsChecked == true).Content.ShouldBe(styleName);
                var wheel = MidnightHost.AllOf<TintWheel>(view).Single();
                wheel.IsVisible.ShouldBe(style == GlassStyle.Colour, "the presets and the wheel show for Colour only");
                if (style == GlassStyle.Colour)
                {
                    MidnightHost.AllOf<Button>(view).Count(b => b.Tag is true).ShouldBe(1, "the chosen preset is ringed");
                }

                Directory.CreateDirectory(UiHarness.Folder);
                var height = (int)Math.Ceiling(((FrameworkElement)((ScrollViewer)view.Content).Content).ActualHeight);
                UiHarness.Render(window, width, Math.Min(height, 3400), $"aero-settings-{themeName.ToLowerInvariant()}-{styleName.ToLowerInvariant()}-{width}.png");
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
                var dark = MidnightHost.AllOf<RadioButton>(view).First(r => r.Content is "Dark" && r.Style == view.FindResource("A.OptItem"));
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
                string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(MidnightHost.AllOf<TintWheel>(view).Single())).ShouldBeFalse();
            }
            finally
            {
                window.Close();
            }
        });
}
