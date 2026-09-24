using System.Windows;

namespace PowerLedger.App;

/// <summary>
/// TEMPORARY (plan O M1): the switch's contract from Task 0.4, so MidnightWindow compiles and its tests run before F5
/// lands the real one; replaced by plan-o/f's LookSwitcher at the merge. Order: the palette for the target, the target
/// window, its bounds, state and page copied, shown, the tray retargeted, the old window closed for the switch. A window
/// that will not open keeps the old one, its palette put back, and the message goes to the caller.
/// </summary>
internal sealed class LookSwitcher(Func<Look, IShellWindow> open, ThemeManager theme, Action<IShellWindow> retarget)
{
    public IShellWindow Current { get; private set; } = null!;

    public Look Look { get; private set; }

    /// <summary>Null on success; a message when the new window can't open (the old stays).</summary>
    public string? Switch(Look target)
    {
        var old = Current;
        theme.Apply(target);
        IShellWindow fresh;
        try
        {
            fresh = open(target);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            theme.Apply(Look);
            return $"The {target} look couldn't open: {error.Message}";
        }
        if (old is not null)
        {
            fresh.Bounds = old.Bounds;
            fresh.State = old.State;
            fresh.Page = old.Page;
        }
        fresh.Show();
        Current = fresh;
        Look = target;
        retarget(fresh);
        old?.CloseForSwitch();
        return null;
    }
}
