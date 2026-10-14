namespace PowerLedger.App;

/// <summary>
/// When the App asks Windows for efficiency mode (perf review P3, see <see cref="ProcessQos"/>): while none of its windows
/// shows, but only once the first window has shown, or a start in the tray has settled. Asked for at start, before the
/// first window was laid out, it ran that window's first paint at the processor's slowest clocks. Each change is applied
/// once; the mode is handed back only if it was asked for. Call on the UI thread.
/// </summary>
/// <param name="apply">Asks for the mode, or with false hands the choice back to Windows.</param>
internal sealed class EfficiencyMode(Action<bool> apply)
{
    private bool _on;
    private bool _ready;
    private bool _shows;

    /// <summary>Whether any of the App's windows is on screen now.</summary>
    public void Windows(bool shows)
    {
        _shows = shows;
        if (shows) _ready = true;
        Apply();
    }

    /// <summary>A start in the tray is done with its first work: with no window shown, the App may run efficiently now.</summary>
    public void Settled()
    {
        _ready = true;
        Apply();
    }

    private void Apply()
    {
        var want = _ready && !_shows;
        if (want == _on) return;
        _on = want;
        apply(want);
    }
}
