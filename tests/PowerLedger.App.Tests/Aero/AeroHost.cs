using System.Windows;

namespace PowerLedger.App.Tests;

/// <summary>
/// What the Aero tests add to <see cref="UiHarness"/>, as <see cref="MidnightHost"/> does for Midnight's: Aero's palette
/// and styles on the window a test draws, never among the application's dictionaries (review 11), and Aero's window over
/// a shell. Plan S Task 0 makes it; agent G keeps <see cref="Dressed"/> merging whatever Aero's windows merge.
/// </summary>
internal static class AeroHost
{
    public const string Styles = "pack://application:,,,/PowerLedger;component/Aero/Styles.Aero.xaml";

    /// <summary>Aero's palette for <paramref name="theme"/> and Aero's styles on <paramref name="window"/>, where they win
    /// over the application's for everything the window holds; the window returned.</summary>
    public static T Dressed<T>(T window, Theme theme)
        where T : Window
    {
        window.Resources.MergedDictionaries.Add(ThemeManager.Palette(Look.Aero, theme));
        window.Resources.MergedDictionaries.Add(new SharedDictionary { Source = new Uri(Styles, UriKind.Absolute) });
        return window;
    }

    /// <summary>An Aero window over <paramref name="shell"/> in <paramref name="theme"/>'s Aero palette, off screen and
    /// unactivated, as <see cref="MidnightFixtures.Window"/> makes Midnight's: the palette on the window, and the Classic
    /// palette the theme manager puts on the application taken off again. Call on the UI thread.</summary>
    public static AeroWindow Window(ShellViewModel shell, Theme theme = Theme.Dark, Updater? updates = null, Action? feedback = null)
    {
        var app = Application.Current.Resources.MergedDictionaries;
        var before = app.Count;
        var manager = new ThemeManager(Application.Current, theme == Theme.Dark ? ThemeChoice.Dark : ThemeChoice.Light);
        if (app.Count == before + 1) app.RemoveAt(0);
        var looks = new LookSwitcher(_ => throw new InvalidOperationException("No switch in a render."), manager, _ => { }, _ => { });
        var window = new AeroWindow(shell, looks, manager, updates ?? MidnightFixtures.IdleUpdates(), feedback ?? (() => { }))
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = 0, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Resources.MergedDictionaries.Add(ThemeManager.Palette(Look.Aero, theme));
        window.Closed += (_, _) => manager.Dispose();
        return window;
    }
}
