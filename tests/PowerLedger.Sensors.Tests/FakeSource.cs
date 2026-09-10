using PowerLedger.Sensors;

namespace PowerLedger.Sensors.Tests;

/// <summary>A source under the test's control: it can fill fields, throw, or claim to be unsupported.</summary>
internal sealed class FakeSource(string name, Action<SampleDraft> contribute) : ISensorSource
{
    public string Name { get; } = name;
    public bool Supported { get; init; } = true;
    public string? Unavailable { get; init; }
    public int Calls { get; private set; }
    public bool Disposed { get; private set; }
    public Exception? Throw { get; set; }

    public void Contribute(SampleDraft draft)
    {
        Calls++;
        if (Throw is { } error) throw error;
        contribute(draft);
    }

    public void Dispose() => Disposed = true;
}
