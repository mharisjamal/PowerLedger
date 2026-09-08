namespace PowerLedger.Core;

/// <param name="IdleThresholdSeconds">The user counts as idle after this many seconds without input (inclusive). User-configurable 60–1800 s; range-checked at the pipe.</param>
/// <param name="LaptopAdapterEfficiency">Applied on AC only; battery mode uses 1.0. Must be in (0, 1]; range-checked at the pipe.</param>
public sealed record PowerModelOptions(
    double IdleThresholdSeconds = 300,
    double LaptopAdapterEfficiency = 0.90);
