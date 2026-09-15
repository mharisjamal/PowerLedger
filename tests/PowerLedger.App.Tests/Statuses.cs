using PowerLedger.Contracts;

namespace PowerLedger.App.Tests;

/// <summary>Service statuses as the service would report them.</summary>
internal static class Statuses
{
    public static ServiceStatus Running(bool energyMeter = true, bool battery = true) => new(
        "0.1.0+b688a18", DateTimeOffset.UnixEpoch, 10,
        [
            new SourceStatus("energy-meter", energyMeter, energyMeter ? null : "this machine publishes no processor power rails", 0, null),
            new SourceStatus("battery", battery, battery ? null : "no battery fitted", 0, null),
        ],
        SuspectTicks: 0, SensorRestarts: 0, new CalibrationStatus(900, 1800, 0, 12), "hash",
        DatabaseBytes: 31L * 1024 * 1024, WriteProblem: null, DatabaseNotice: null, Last: null);
}
