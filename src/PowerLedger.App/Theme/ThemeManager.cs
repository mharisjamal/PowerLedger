using System.Windows;
using Microsoft.Win32;

namespace PowerLedger.App;

internal enum Theme
{
    Dark,
    Light,
}

/// <summary>Spec §9: both themes follow the system by default and can be forced in Settings.</summary>
internal static class ThemeRules
{
    public static Theme Resolve(ThemeChoice choice, bool windowsUsesLight) => choice switch
    {
        ThemeChoice.Dark => Theme.Dark,
        ThemeChoice.Light => Theme.Light,
        _ => windowsUsesLight ? Theme.Light : Theme.Dark,
    };
}

/// <summary>
/// Keeps the application's palette in step with the look, the choice and, for System, with Windows' app mode. The palette
/// is the first merged dictionary, and the only one there that defines a brush; everything else refers to its brushes with
/// DynamicResource, so a swap repaints at once.
/// </summary>
internal sealed class ThemeManager : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Application _application;
    private ThemeChoice _choice;
    private ResourceDictionary? _palette;

    public ThemeManager(Application application, ThemeChoice choice, Look look = Look.Classic)
    {
        _application = application;
        _choice = choice;
        Look = look;
        Apply();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    public Theme Current { get; private set; }

    /// <summary>The look whose palette is on (Midnight look design §3).</summary>
    public Look Look { get; private set; }

    public void Choose(ThemeChoice choice)
    {
        _choice = choice;
        Apply();
    }

    /// <summary>Draws in <paramref name="look"/>'s palette for the choice in force.</summary>
    public void Apply(Look look)
    {
        Look = look;
        Apply();
    }

    /// <summary>Windows' app mode; dark when it cannot be read.</summary>
    public static bool WindowsUsesLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    /// <summary>Classic's palette for a theme as a fresh dictionary, for the application or for a test that draws a view.</summary>
    public static ResourceDictionary Palette(Theme theme) => Palette(Look.Classic, theme);

    /// <summary>A look's palette for a theme as a fresh dictionary.</summary>
    public static ResourceDictionary Palette(Look look, Theme theme) => new() { Source = LookRules.PaletteFor(look, theme) };

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color) _application.Dispatcher.InvokeAsync(Apply);
    }

    private void Apply()
    {
        var theme = ThemeRules.Resolve(_choice, WindowsUsesLight());
        var source = LookRules.PaletteFor(Look, theme);
        if (_palette?.Source == source) return;
        var palette = new ResourceDictionary { Source = source };
        var merged = _application.Resources.MergedDictionaries;
        if (_palette is not null) merged.Remove(_palette);
        merged.Insert(0, palette);
        _palette = palette;
        Current = theme;
    }
}
