using System.Windows;

namespace PowerLedger.App;

/// <summary>
/// What a shell window offers the look switch (Midnight look design §2, plan O 0.4): its place and state to carry over,
/// the page it shows, and a close that skips the hide-to-tray a user's close gets.
/// </summary>
internal interface IShellWindow
{
    /// <summary>Left, top, width and height in device-independent pixels.</summary>
    Rect Bounds { get; set; }

    WindowState State { get; set; }

    /// <summary>The shell's page; setting it shows that page.</summary>
    Page Page { get; set; }

    void Show();

    /// <summary>Closes for good, without the hide-to-tray behaviour.</summary>
    void CloseForSwitch();

    event EventHandler? Closed;

    /// <summary>For the tray and the dialogs' owners.</summary>
    Window Window { get; }
}
