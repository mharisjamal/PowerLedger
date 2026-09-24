using System.Windows;

namespace PowerLedger.App;

/// <summary>What a shell window offers the look switcher and the App (Midnight look design §2): where it is, which page
/// it shows, and a close that skips the hide-to-tray. Classic's <see cref="MainWindow"/> and <see cref="MidnightWindow"/>
/// are both one, over the same <see cref="ShellViewModel"/>.</summary>
internal interface IShellWindow
{
    /// <summary>Left, top, width and height in device-independent pixels; a maximised window's are the ones it restores to.</summary>
    Rect Bounds { get; set; }

    WindowState State { get; set; }

    /// <summary><see cref="ShellViewModel.Page"/>; setting it shows that page, mapped to the look's own when it has no
    /// such page (Now and Dashboard stand for each other).</summary>
    Page Page { get; set; }

    void Show();

    /// <summary>Closes for good, without the hide-to-tray behaviour an ordinary close has.</summary>
    void CloseForSwitch();

    event EventHandler? Closed;

    /// <summary>The window itself, for the tray and as the dialogs' owner.</summary>
    Window Window { get; }
}
