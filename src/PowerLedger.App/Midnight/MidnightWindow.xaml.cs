using System.Windows;

namespace PowerLedger.App;

/// <summary>The App's window in the Midnight look (Midnight look design §2), over the same <see cref="ShellViewModel"/>
/// as Classic's. A stand-in until the shell itself lands: it takes what the shell will take, and shows nothing yet.</summary>
public partial class MidnightWindow : Window, IShellWindow
{
    private readonly ShellViewModel _shell;

    /// <param name="looks">For the top bar's Switch look button.</param>
    /// <param name="theme">For the top bar's sun and moon.</param>
    /// <param name="updates">For the foot's update card.</param>
    /// <param name="feedback">Opens the Send feedback window, from the sidebar's Support item.</param>
    internal MidnightWindow(ShellViewModel shell, LookSwitcher looks, ThemeManager theme, Updater updates, Action feedback)
    {
        _shell = shell;
        InitializeComponent();
        DataContext = shell;
    }

    /// <summary>The bounds a switch carries over: the restored ones once shown. Setting them places the window by hand.</summary>
    Rect IShellWindow.Bounds
    {
        get => RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        set
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = value.X;
            Top = value.Y;
            Width = value.Width;
            Height = value.Height;
        }
    }

    WindowState IShellWindow.State
    {
        get => WindowState;
        set => WindowState = value;
    }

    /// <summary>Midnight has no Now page: Classic's landing page maps to the Dashboard.</summary>
    Page IShellWindow.Page
    {
        get => _shell.Page;
        set => _shell.Page = value == Page.Now ? Page.Dashboard : value;
    }

    Window IShellWindow.Window => this;

    /// <summary>Closes for good: the App's Closing handler hides a window to the tray only while it is the current one.</summary>
    public void CloseForSwitch() => Close();
}
