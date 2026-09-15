using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PowerLedger.App;

/// <summary>
/// What the tray icon says: whole watts and thousands as "k"; without a reading it says nothing and shows the logo.
/// "1.2k" was tried and left out: at 16 pixels its digits are too small to read.
/// </summary>
internal static class TrayGlyph
{
    public static int? Round(double? watts) => watts is { } w && double.IsFinite(w) ? (int)Math.Round(Math.Max(0, w)) : null;

    public static string? Text(int? watts) => watts switch
    {
        null => null,
        < 1000 => watts.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => Math.Round(watts.Value / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture) + "k",
    };
}

/// <summary>
/// The tray icon (spec §9). It draws the live watts on the brand's amber tile, or the logo until there is a reading, and
/// redraws only when the rounded value changes; the tooltip gives now and today; the menu opens the window, toggles
/// start with Windows, and exits the UI while the service keeps logging. Call it on the UI thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private Icon? _current;
    private int? _shown;
    private bool _drawn;
    private bool _disposed;
    private string? _open;

    public TrayIcon(Action open, Action exit, StartWithWindows autostart)
    {
        var startWithWindows = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = autostart.IsEnabled };
        startWithWindows.CheckedChanged += (_, _) => autostart.Set(startWithWindows.Checked);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => open());
        menu.Items.Add(startWithWindows);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit UI", null, (_, _) => exit());
        menu.Opening += (_, _) =>
        {
            if (startWithWindows.Checked != autostart.IsEnabled) startWithWindows.Checked = autostart.IsEnabled;   // Settings may have changed it
        };
        _icon = new NotifyIcon { ContextMenuStrip = menu, Text = "PowerLedger", Visible = true };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => Launch(_open);
        Show(null, "PowerLedger · waiting for the service");
    }

    /// <summary>Updates the tooltip, and the icon when the rounded watts changed.</summary>
    public void Show(double? watts, string tooltip)
    {
        if (_disposed) return;
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        var rounded = TrayGlyph.Round(watts);
        if (_drawn && rounded == _shown) return;
        _drawn = true;
        _shown = rounded;
        var next = Render(TrayGlyph.Text(rounded));
        _icon.Icon = next;
        _current?.Dispose();
        _current = next;
    }

    /// <summary>A notification from the tray, the monthly report's (spec §9). Clicking it opens <paramref name="open"/>.</summary>
    public void Notify(string title, string text, string? open)
    {
        if (_disposed) return;
        _open = open;
        _icon.ShowBalloonTip(10_000, title, text, ToolTipIcon.None);
    }

    private static void Launch(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            using var viewer = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No app opens PDFs here; the file is still in Documents.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _current?.Dispose();
    }

    /// <summary>
    /// The text on the amber tile, or the logo without it, at the small-icon size of the system's scale. The icon owns its
    /// handle, which <see cref="Show"/> and <see cref="Dispose"/> free with the icon.
    /// </summary>
    private static Icon Render(string? text) => TrayArt.DrawIcon(text, SystemInformation.SmallIconSize.Width);
}
