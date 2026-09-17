using PowerLedger.Sensors;

namespace PowerLedger.Sensors.Tests;

/// <summary>A source under the test's control: it can fill fields, throw, or claim to be unsupported.</summary>
internal sealed class FakeSource(string name, Action<SampleDraft> contribute) : ISensorSource
{
    private readonly bool _supported = true;

    public string Name { get; } = name;

    public bool Supported
    {
        get => SupportedThrows is { } error ? throw error : _supported;
        init => _supported = value;
    }

    public string? Unavailable { get; init; }
    public int Calls { get; private set; }
    public bool Disposed { get; private set; }
    public Exception? Throw { get; set; }

    /// <summary>What asking whether the source is supported throws, as a library that has gone would.</summary>
    public Exception? SupportedThrows { get; set; }

    /// <summary>What closing the source throws, as a device Windows will not let go of would.</summary>
    public Exception? DisposeThrows { get; set; }

    public void Contribute(SampleDraft draft)
    {
        Calls++;
        if (Throw is { } error) throw error;
        contribute(draft);
    }

    public void Dispose()
    {
        Disposed = true;
        if (DisposeThrows is { } error) throw error;
    }
}
