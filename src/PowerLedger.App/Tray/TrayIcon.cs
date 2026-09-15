using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PowerLedger.App;

/// <summary>What the tray icon says: whole watts, thousands as "k", a dash without a reading.</summary>
internal static class TrayGlyph
{
    public static int? Round(double? watts) => watts is { } w && double.IsFinite(w) ? (int)Math.Round(Math.Max(0, w)) : null;

    public static string Text(int? watts) => watts switch
    {
        null => "–",
        < 1000 => watts.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => Math.Round(watts.Value / 1000.0).ToString(System.Globalization.CultureInfo.InvariantCulture) + "k",
    };
}

/// <summary>
/// The tray icon (spec §9). It draws the live watts as text and redraws only when the rounded value changes; the tooltip
/// gives now and today; the menu opens the window, toggles start with Windows, and exits the UI while the service keeps
/// logging. Call it on the UI thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private static readonly Color Amber = Color.FromArgb(0xF2, 0xB2, 0x33);

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

    /// <summary>The text in amber, as large as the small-icon size allows.</summary>
    private static Icon Render(string text)
    {
        var size = SystemInformation.SmallIconSize;
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.Clear(Color.Transparent);
            var em = size.Height * (text.Length <= 2 ? 0.78f : 0.62f);
            using var font = new Font("Bahnschrift", em, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Amber);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(text, font, brush, new RectangleF(-2, 0, size.Width + 4, size.Height), format);
        }
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
