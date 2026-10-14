namespace PowerLedger.Core;

/// <param name="IdleThresholdSeconds">The user counts as idle after this many seconds without input (inclusive). User-configurable 60–1800 s; range-checked at the pipe.</param>
/// <param name="LaptopAdapterEfficiency">A flat efficiency for a laptop's adapter on AC, in (0, 1]; null, the default, reads the
/// adapter's curve at its load with its no-load draw (<see cref="AdapterEfficiency"/>). Battery mode uses 1.0.</param>
/// <param name="LaptopAdapterRatedW">The adapter's rating its curve is read at.</param>
public sealed record PowerModelOptions(
    double IdleThresholdSeconds = 300,
    double? LaptopAdapterEfficiency = null,
    double LaptopAdapterRatedW = AdapterEfficiency.DefaultRatedW);
