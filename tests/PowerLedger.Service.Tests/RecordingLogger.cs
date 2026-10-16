using Microsoft.Extensions.Logging;

namespace PowerLedger.Service.Tests;

/// <summary>A logger that keeps what was logged at warning level and above, its message and its exception's together.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_warnings) return [.. _warnings];
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel < LogLevel.Warning) return;
        lock (_warnings) _warnings.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
