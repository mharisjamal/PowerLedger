namespace PowerLedger.Core;

/// <summary>One process's time on graphics engines over the counters' last window, in 100 ns units: on the discrete cards
/// and on the processor's own graphics, on their shader engines (3D and compute) and on any engine.</summary>
public readonly record struct GraphicsTime(long CardLoad, long CardAny, long ProcessorLoad, long ProcessorAny);

/// <summary>Each process's time on the graphics engines, by process id, over the window between Windows' GPU Engine
/// counters' last two reads (Plan AE). The reads come seconds apart, so a tick carries the last window's.</summary>
public sealed record GraphicsWork(IReadOnlyDictionary<uint, GraphicsTime> ByProcess);
