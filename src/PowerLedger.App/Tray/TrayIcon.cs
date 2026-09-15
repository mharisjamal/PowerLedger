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
/// redraws only when the rounded value changes; the tooltip gives now and today; the menu opens the window, restarts into
/// a downloaded update while one is ready (spec §13), toggles start with Windows, and exits the UI while the service keeps
/// logging. Call it on the UI thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _update;
    private Icon? _current;
    private int? _shown;
    private bool _drawn;
    private bool _disposed;
    private Action? _clicked;
    private Action? _install;

    public TrayIcon(Action open, Action exit, StartWithWindows autostart)
    {
        var startWithWindows = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = autostart.IsEnabled };
        startWithWindows.CheckedChanged += (_, _) => autostart.Set(startWithWindows.Checked);
        _update = new ToolStripMenuItem("Restart to update") { Visible = false };
        _update.Click += (_, _) => _install?.Invoke();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => open());
        menu.Items.Add(_update);
        menu.Items.Add(startWithWindows);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit UI", null, (_, _) => exit());
        menu.Opening += (_, _) =>
        {
            if (startWithWindows.Checked != autostart.IsEnabled) startWithWindows.Checked = autostart.IsEnabled;   // Settings may have changed it
        };
        _icon = new NotifyIcon { ContextMenuStrip = menu, Text = "PowerLedger", Visible = true };
        _icon.DoubleClick += (_, _) => open();
        _icon.BalloonTipClicked += (_, _) => _clicked?.Invoke();
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
    public void Notify(string title, string text, string? open) => Balloon(title, text, () => Launch(open));

    /// <summary>A notification whose click runs <paramref name="clicked"/>: an update's, which opens the window (spec §13).</summary>
    public void Announce(string title, string text, Action clicked) => Balloon(title, text, clicked);

    /// <summary>"Restart to update to X.Y.Z" in the menu while <paramref name="version"/> is ready; null takes it away.</summary>
    public void OfferUpdate(string? version, Action install)
    {
        if (_disposed) return;
        _install = install;
        _update.Text = $"Restart to update to {version}";
        _update.Visible = version is not null;
    }

    private void Balloon(string title, string text, Action clicked)
    {
        if (_disposed) return;
        _clicked = clicked;
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
