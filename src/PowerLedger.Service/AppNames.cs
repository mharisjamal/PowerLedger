using System.Collections.Concurrent;

namespace PowerLedger.Service;

/// <summary>
/// Apps' names, read from their images on a thread of their own (review item 1): a file's version information can wait on a
/// slow disk, which must never hold the loop's tick. A name not read yet is null, and the minute's write after it is read
/// has it. Each image is read once; a few thousand names at most are kept.
/// </summary>
/// <param name="read">Reads an image's name; may throw, which names it by its file.</param>
/// <param name="inline">Read on the caller's thread, for tests.</param>
internal sealed class AppNames(Func<string, string> read, bool inline = false)
{
    private const int Most = 4096;

    private readonly ConcurrentDictionary<string, string> _read = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _waiting = new();
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);
    private int _reading;

    /// <summary>Names read on the caller's thread.</summary>
    public static AppNames Inline(Func<string, string> read) => new(read, inline: true);

    /// <summary>The image's name once read; null until then, the read asked for once. Called from the loop's thread only.</summary>
    public string? For(string path)
    {
        if (_read.TryGetValue(path, out var name)) return name;
        if (inline) return _read[path] = Read(path);
        if (_asked.Count >= Most)
        {
            _asked.Clear();
            _read.Clear();
        }
        if (!_asked.Add(path)) return null;
        _waiting.Enqueue(path);
        if (Interlocked.CompareExchange(ref _reading, 1, 0) == 0) _ = Task.Run(Drain);
        return null;
    }

    /// <summary>Reads what waits, one at a time, until nothing does.</summary>
    private void Drain()
    {
        do
        {
            while (_waiting.TryDequeue(out var path)) _read[path] = Read(path);
            Volatile.Write(ref _reading, 0);
        }
        while (!_waiting.IsEmpty && Interlocked.CompareExchange(ref _reading, 1, 0) == 0);
    }

    private string Read(string path)
    {
        try
        {
            return read(path);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Path.GetFileNameWithoutExtension(path);
        }
    }
}
