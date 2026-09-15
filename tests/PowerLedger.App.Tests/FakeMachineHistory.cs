using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.App.Tests;

/// <summary>Tariffs and a detection as the test sets them.</summary>
internal sealed class FakeMachineHistory : IMachineHistory
{
    public List<Tariff>? TariffList { get; set; } = [new Tariff(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), 0.17m, "USD")];

    public DetectedHardware? Hardware { get; set; } = new(
        ChassisKind.Laptop, "11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "NVIDIA GeForce MX330", 2, false, 1, 0, 15.3, 1);

    public IReadOnlyList<Tariff>? Tariffs() => TariffList;

    public DetectedHardware? Detected() => Hardware;
}
