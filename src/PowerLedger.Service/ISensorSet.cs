using PowerLedger.Core;
using PowerLedger.Sensors;

namespace PowerLedger.Service;

/// <summary>The sensor layer as the loop uses it: one validated sample a tick, and what the status screen needs.
/// Single-threaded: <see cref="SensorWorker"/> makes every call from its own thread.</summary>
internal interface ISensorSet : IDisposable
{
    Sample Read(DateTimeOffset timestamp, double deltaSeconds);

    IReadOnlyList<SourceHealth> Health { get; }

    int SuspectCount { get; }
}

/// <summary>The real sensors, assembled by Plan B's factory.</summary>
internal sealed class MachineSensorSet(MachineSensors sensors) : ISensorSet
{
    public Sample Read(DateTimeOffset timestamp, double deltaSeconds) => sensors.Read(timestamp, deltaSeconds);

    public IReadOnlyList<SourceHealth> Health => sensors.Sampler.Health;

    public int SuspectCount => sensors.Validator.SuspectCount;

    public void Dispose() => sensors.Dispose();
}
