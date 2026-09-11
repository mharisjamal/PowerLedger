using Microsoft.Data.Sqlite;
using PowerLedger.Core;

namespace PowerLedger.Service;

/// <summary>
/// Readings waiting to be written as one batch (spec §7): flushed once a minute, on suspend and on stop. When a write
/// fails, as it does on a full disk, the readings stay here and the next flush retries them, up to
/// <paramref name="capacity"/> readings (an hour at one a second); past that the oldest are dropped (spec §10).
/// </summary>
internal sealed class WriteBuffer(Action<IReadOnlyList<Reading>> write, int capacity = 3600)
{
    private readonly List<Reading> _pending = [];
    private SqliteException? _lastError;

    public int Count => _pending.Count;

    /// <summary>Readings dropped because the buffer overflowed while writes were failing.</summary>
    public long Dropped { get; private set; }

    /// <summary>Null while writes succeed; otherwise what is wrong, for the status screen.</summary>
    public string? Problem => _lastError is null
        ? null
        : $"Writes are failing ({_lastError.Message}); {Count} readings are held in memory"
          + (Dropped > 0 ? $" and {Dropped} older ones were dropped." : ".");

    public void Add(Reading reading)
    {
        _pending.Add(reading);
        if (_pending.Count <= capacity) return;
        _pending.RemoveAt(0);
        Dropped++;
    }

    /// <summary>Writes everything held. True when the buffer is empty afterwards.</summary>
    public bool Flush()
    {
        if (_pending.Count == 0) return true;
        try
        {
            write(_pending);
            _pending.Clear();
            _lastError = null;
            return true;
        }
        catch (SqliteException error)
        {
            _lastError = error;
            return false;
        }
    }
}
