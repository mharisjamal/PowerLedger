namespace PowerLedger.App;

/// <summary>
/// Replaces the shell window with the other look's (Midnight look design §2): the palette first, then the new window at
/// the old one's bounds, state and page, shown before the old one closes, so the switch reads as the window changing its
/// clothes. <paramref name="open"/> makes a look's window; <paramref name="retarget"/> tells the App the window the tray
/// and the dialogs' owner now are; <paramref name="log"/> takes a line when a window fails to open. The first window opens
/// in the look whose palette <paramref name="theme"/> has on, the first time <see cref="Current"/> is asked for or
/// <see cref="Show"/> called; <paramref name="fellBack"/> hears when that first window had to be Classic's instead.
/// </summary>
internal sealed class LookSwitcher(
    Func<Look, IShellWindow> open, ThemeManager theme, Action<IShellWindow> retarget, Action<string> log, Action<Look>? fellBack = null)
{
    private IShellWindow? _current;

    /// <summary>The window of the look in use, opened (not shown) the first time it is asked for.</summary>
    public IShellWindow Current => _current ??= open(Look);

    /// <summary>
    /// Shows the window of the look in use, opening it the first time. Review 6: a saved look other than Classic whose
    /// window won't open or show would leave the App with no window at all, so Classic opens instead, with its palette
    /// and on its own page; why goes to the log, and <c>fellBack</c> hears it, for the App to save Classic so the next
    /// start doesn't fail the same way. Classic failing, on its own or as the fallback, is not caught: there is nothing
    /// left to fall back to.
    /// </summary>
    public IShellWindow Show()
    {
        if (_current is { } shown)
        {
            shown.Show();
            return shown;
        }
        var look = Look;
        IShellWindow? window = null;
        try
        {
            _current = window = open(look);   // current as it shows, so the App counts it as the window on screen
            window.Show();
            return window;
        }
        catch (Exception error) when (error is not OutOfMemoryException && look != Look.Classic)
        {
            _current = null;   // no longer the current one, so the App's hide-to-tray lets its close through
            log($"The {look} look didn't open at start, so Classic opens instead: {error}");
            TryClose(window);
            theme.Apply(Look.Classic);
            var classic = _current = open(Look.Classic);
            classic.Page = classic.Page;   // the failed window may have moved the shell to a page only it has
            classic.Show();
            fellBack?.Invoke(Look.Classic);
            return classic;
        }
    }

    /// <summary>Whether a window has been opened yet: the App's dialogs want an owner that has shown, or none at all.</summary>
    public bool IsOpen => _current is not null;

    /// <summary>The look in use: the one whose palette is on.</summary>
    public Look Look => theme.Look;

    /// <summary>Switches to <paramref name="target"/>. Null when it went well, or when nothing changes; otherwise what
    /// went wrong loading its palette or opening its window, which is then closed again, the old one staying with its
    /// palette and on its page (design §5).</summary>
    public string? Switch(Look target)
    {
        if (target == Look) return null;
        var previous = Look;
        var old = _current;
        var page = old?.Page;   // the shell is shared: the new window may move it to its own look's page as it opens
        IShellWindow? next = null;
        try
        {
            theme.Apply(target);
            if (old is not null)
            {
                next = open(target);
                next.Bounds = old.Bounds;
                next.State = old.State;
                next.Page = old.Page;
                next.Show();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            log($"The {target} look didn't open: {error}");
            theme.Apply(previous);
            TryClose(next);
            if (old is not null && page is { } was) old.Page = was;
            return $"Couldn't open the {target} look: {error.Message}";
        }
        if (old is null || next is null) return null;   // nothing open yet: the first window opens in the new look
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
