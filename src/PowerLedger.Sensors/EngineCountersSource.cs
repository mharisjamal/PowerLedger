using System.Diagnostics;

namespace PowerLedger.Sensors;

/// <summary>
/// Windows' GPU Engine counters as a sensor source, for the Apps page (Plan AE) where <see cref="GpuLoadSource"/> does not
/// read them: a PC whose every card NVIDIA's library reads (see <see cref="EngineCounters"/>). Built in the sensor set so
/// each read runs under the sensor watchdog, as every Windows answer does, rather than on the loop's own (review item 1).
/// It reads every <see cref="ReadEvery"/>, holds each process's time on the engines in between as the load counters do,
/// and stops for good once the registry names no card among the adapters the counters show: there is nothing to split.
/// </summary>
public sealed class EngineCountersSource : ISensorSource
{
    /// <summary>How often the counters are read; the ticks in between carry the last window's work.</summary>
    public static readonly TimeSpan ReadEvery = GpuLoadSource.ReadEvery;

    private readonly EngineCounters _counters;
    private readonly Func<TimeSpan> _clock;
    private TimeSpan? _readAt;
    private Core.GraphicsWork? _work;

    /// <summary>The counters and the DirectX registry; unsupported where Windows publishes no counters.</summary>
    public EngineCountersSource()
        : this(new EngineCounters(), Elapsed())
    {
        try
        {
            if (!GpuEngines.Published) (Supported, Unavailable) = (false, "Windows publishes no GPU engine counters");
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            (Supported, Unavailable) = (false, error.Message);
        }
    }

    /// <summary>Test seam: any counters and clock.</summary>
    internal EngineCountersSource(EngineCounters counters, Func<TimeSpan> clock)
    {
        _counters = counters;
        _clock = clock;
    }

    public string Name => "gpu-engines";

    public bool Supported { get; private set; } = true;

    public string? Unavailable { get; private set; }

    public void Contribute(SampleDraft draft)
    {
        var now = _clock();
        if (_readAt is not { } last || now - last >= ReadEvery)
        {
            _readAt = now;
            _work = _counters.Read();
            if (_counters.HasCard == false)
            {
                (Supported, Unavailable, _work) = (false, "no graphics card to split among the apps", null);
                return;
            }
        }
        draft.Graphics ??= _work;
    }

    public void Dispose()
    {
    }

    private static Func<TimeSpan> Elapsed()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed;
    }
}
