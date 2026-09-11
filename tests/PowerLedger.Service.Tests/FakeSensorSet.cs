using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service.Tests;

/// <summary>A sensor set that answers from a script and records how it was used.</summary>
internal sealed class FakeSensorSet(Func<DateTimeOffset, double, Sample>? script = null) : ISensorSet
{
    private int _reads;
    private volatile bool _disposed;

    public int Reads => Volatile.Read(ref _reads);

    public int ThreadId { get; private set; }

    public bool Disposed => _disposed;

    public Sample Read(DateTimeOffset timestamp, double deltaSeconds)
    {
        Interlocked.Increment(ref _reads);
        ThreadId = Environment.CurrentManagedThreadId;
        return script?.Invoke(timestamp, deltaSeconds) ?? Samples.At(timestamp, deltaSeconds);
    }

    public IReadOnlyList<SourceHealth> Health => [new("fake", true, null, 0, 0, null)];

    public int SuspectCount => 0;

    public void Dispose() => _disposed = true;
}
