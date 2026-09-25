namespace PowerLedger.Service.Updates;

/// <summary>
/// The oldest version the data server still takes (Plan Q §3), as the service last heard it: from <c>GET /v1/app-policy</c>
/// (the update worker) or from a <c>426</c> answer (the sharing worker). Shared by both, so a version the server has left
/// behind stops sending at once and updates at once. Thread-safe.
/// </summary>
internal sealed class AppPolicy
{
    private readonly Lock _gate = new();
    private Version? _min;

    /// <summary>Raised, off any lock, when the minimum changes.</summary>
    public event Action? Changed;

    /// <summary>The minimum as <c>X.Y.Z</c>, or null before the server has said one.</summary>
    public string? MinVersion { get { lock (_gate) return _min?.ToString(3); } }

    /// <summary>Records what the server said; a text that isn't <c>X.Y.Z</c> is ignored, keeping the last good one.</summary>
    public void Set(string? minVersion)
    {
        if (!TryParse(minVersion, out var parsed)) return;
        bool changed;
        lock (_gate)
        {
            changed = _min != parsed;
            _min = parsed;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Whether <paramref name="running"/> (<c>X.Y.Z</c>, anything after it ignored) is older than the minimum;
    /// false while no minimum is known.</summary>
    public bool IsBelowMinimum(string running)
    {
        lock (_gate)
            return _min is not null && TryParse(ServiceVersion.Plain(running), out var version) && version < _min;
    }

    private static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (text is null || ServiceVersion.Plain(text) != text.Trim() || !Version.TryParse(text.Trim(), out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
        return true;
    }
}
