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
/// Keeps the application's palette in step with the choice and, for System, with Windows' app mode. The palette is the
/// first merged dictionary; everything else refers to its brushes with DynamicResource, so a swap repaints at once.
/// </summary>
internal sealed class ThemeManager : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Application _application;
    private ThemeChoice _choice;
    private ResourceDictionary? _palette;

    public ThemeManager(Application application, ThemeChoice choice)
    {
        _application = application;
        _choice = choice;
        Apply();
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    public Theme Current { get; private set; }

    public void Choose(ThemeChoice choice)
    {
        _choice = choice;
        Apply();
    }

    /// <summary>Windows' app mode; dark when it cannot be read.</summary>
    public static bool WindowsUsesLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    /// <summary>A palette as a fresh dictionary, for the application or for a test that draws a view.</summary>
    public static ResourceDictionary Palette(Theme theme)
        => new() { Source = new Uri($"pack://application:,,,/PowerLedger;component/Theme/Palette.{theme}.xaml", UriKind.Absolute) };

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color) _application.Dispatcher.InvokeAsync(Apply);
    }

    private void Apply()
    {
        var theme = ThemeRules.Resolve(_choice, WindowsUsesLight());
        if (_palette is not null && theme == Current) return;
        var palette = Palette(theme);
        var merged = _application.Resources.MergedDictionaries;
        if (_palette is not null) merged.Remove(_palette);
        merged.Insert(0, palette);
        _palette = palette;
        Current = theme;
    }
}
