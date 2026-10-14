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
/// The tray's pictures (perf review P2): each drawn when a reading first needs it, and kept for the readings that come back
/// to it, the most recent first. One pushed out by newer ones is freed; the one shown is always the most recent, so it is
/// never among them. Call on one thread.
/// </summary>
internal sealed class TrayIcons(Func<string?, int, Icon> draw, int capacity = TrayIcons.Kept) : IDisposable
{
    /// <summary>How many pictures are kept: a PC's readings mostly move within a few watts.</summary>
    public const int Kept = 16;

    private readonly LinkedList<(string? Text, int Size, Icon Icon)> _recent = new();

    /// <summary>How many are kept now.</summary>
    public int Count => _recent.Count;

    /// <summary>The picture for <paramref name="text"/> at <paramref name="size"/> pixels, drawn only if it isn't kept.</summary>
    public Icon Get(string? text, int size)
    {
        for (var node = _recent.First; node is not null; node = node.Next)
        {
            if (node.Value.Text != text || node.Value.Size != size) continue;
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Icon;
        }
        var icon = draw(text, size);
        _recent.AddFirst((text, size, icon));
        while (_recent.Count > capacity)
        {
            _recent.Last!.Value.Icon.Dispose();
            _recent.RemoveLast();
        }
        return icon;
    }

    public void Dispose()
    {
        foreach (var (_, _, icon) in _recent) icon.Dispose();
        _recent.Clear();
    }
}

/// <summary>
/// The tray icon (spec §9). It draws the live watts on the brand's amber tile, or the logo until there is a reading, and
/// redraws only when the rounded value changes, from pictures kept for the values seen lately; the tooltip gives now and
/// today, and goes to Windows only when its words change; the menu opens the window, restarts into a downloaded update
/// while one is ready (spec §13), toggles start with Windows, and exits the UI while the service keeps logging. Call it on
/// the UI thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _update;
    private readonly ToolStripMenuItem _overlay;
    private readonly TrayIcons _icons = new(TrayArt.DrawIcon);
    private string? _tooltip;
    private int? _shown;
    private int _size;
    private bool _drawn;
    private bool _disposed;
    private Action? _clicked;
    private Action? _install;
    private Action? _toggleOverlay;

    public TrayIcon(Action open, Action exit, StartWithWindows autostart)
    {
        var startWithWindows = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = autostart.IsEnabled };
        startWithWindows.CheckedChanged += (_, _) => autostart.Set(startWithWindows.Checked);
        _update = new ToolStripMenuItem("Restart to update") { Visible = false };
        _update.Click += (_, _) => _install?.Invoke();
        _overlay = new ToolStripMenuItem("Watts overlay") { Available = false };
        _overlay.Click += (_, _) => _toggleOverlay?.Invoke();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => open());
        menu.Items.Add(_overlay);
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

    /// <summary>Updates the tooltip when its words changed, and the icon when the rounded watts or the small-icon size did:
    /// a new size, from a change of scale, also lets the digits be fitted to it afresh.</summary>
    public void Show(double? watts, string tooltip)
    {
        if (_disposed) return;
        var text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (text != _tooltip)
        {
            _tooltip = text;
            _icon.Text = text;   // each change is a message to Explorer
        }
        var rounded = TrayGlyph.Round(watts);
        var size = SystemInformation.SmallIconSize.Width;
        if (_drawn && rounded == _shown && size == _size) return;
        if (_drawn && size != _size) TrayArt.ClearFits();
        _drawn = true;
        _shown = rounded;
        _size = size;
        _icon.Icon = _icons.Get(TrayGlyph.Text(rounded), size);
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

    /// <summary>"Watts overlay" in the menu, ticked while it is on, while <paramref name="offered"/>: only in the Aero look,
    /// the overlay's only one (Aero look design §5). A click runs <paramref name="toggle"/>.</summary>
    public void OfferOverlay(bool offered, bool on, Action toggle)
    {
        if (_disposed) return;
        _toggleOverlay = toggle;
        _overlay.Checked = on;
        _overlay.Available = offered;   // Available, not Visible, which reads false while the menu is closed
    }

    /// <summary>The overlay's item is in the menu.</summary>
    internal bool OverlayOffered => _overlay.Available;

    internal bool OverlayOn => _overlay.Checked;

    /// <summary>As a click on the overlay's item, for a test.</summary>
    internal void ClickOverlay() => _overlay.PerformClick();

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
        _icons.Dispose();
    }
}
