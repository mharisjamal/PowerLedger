namespace PowerLedger.Core;

public sealed record PowerModelOptions(
    double IdleThresholdSeconds = 300,
    double LaptopAdapterEfficiency = 0.90);
