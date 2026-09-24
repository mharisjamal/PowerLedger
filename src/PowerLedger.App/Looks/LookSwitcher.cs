namespace PowerLedger.App;

/// <summary>
/// Replaces the shell window with the other look's (Midnight look design §2): the palette first, then the new window at
/// the old one's bounds, state and page, shown before the old one closes, so the switch reads as the window changing its
/// clothes. <paramref name="open"/> makes a look's window; <paramref name="retarget"/> tells the App the window the tray
/// and the dialogs' owner now are; <paramref name="log"/> takes a line when a window fails to open. The first window opens
/// in the look whose palette <paramref name="theme"/> has on, the first time <see cref="Current"/> is asked for.
/// </summary>
internal sealed class LookSwitcher(Func<Look, IShellWindow> open, ThemeManager theme, Action<IShellWindow> retarget, Action<string> log)
{
    private IShellWindow? _current;

    /// <summary>The window of the look in use, opened (not shown) the first time it is asked for.</summary>
    public IShellWindow Current => _current ??= open(Look);

    /// <summary>Whether a window has been opened yet: the App's dialogs want an owner that has shown, or none at all.</summary>
    public bool IsOpen => _current is not null;

    /// <summary>The look in use: the one whose palette is on.</summary>
    public Look Look => theme.Look;

    /// <summary>Switches to <paramref name="target"/>. Null when it went well, or when nothing changes; otherwise what
    /// went wrong opening the new window, which is then closed again, the old one and its palette staying (design §5).</summary>
    public string? Switch(Look target)
    {
        if (target == Look) return null;
        var previous = Look;
        var old = _current;
        theme.Apply(target);
        if (old is null) return null;   // nothing open yet: the first window opens in the new look

        IShellWindow? next = null;
        try
        {
            next = open(target);
            next.Bounds = old.Bounds;
            next.State = old.State;
            next.Page = old.Page;
            next.Show();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log($"The {target} look didn't open: {error}");
            theme.Apply(previous);
            TryClose(next);
            return $"Couldn't open the {target} look: {error.Message}";
        }
        _current = next;
        retarget(next);
        old.CloseForSwitch();
        return null;
    }

    /// <summary>A window that failed part-way through opening may not close cleanly either; nothing more can be done for it.</summary>
    private static void TryClose(IShellWindow? window)
    {
        try
        {
            window?.CloseForSwitch();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Left as it is; the old window carries on.
        }
    }
}
