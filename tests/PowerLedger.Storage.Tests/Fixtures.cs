using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.Storage.Tests;

internal static class Fixtures
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    public static Reading Reading(int second, double totalW = 30, Quality q = Quality.Measured, bool idle = false, bool displayOn = true, double delta = 1.0)
        => new(T0.AddSeconds(second), delta, totalW, q,
               new Components(Cpu: totalW * 0.4, Gpu: totalW * 0.1, Display: 4, Ram: 0, Storage: 0, Board: 0, Extras: 0, Monitors: 0, PsuLoss: 0, Unattributed: totalW * 0.5 - 4),
               OnBattery: q == Quality.Measured, DisplayOn: displayOn, UserIdle: idle, SessionLocked: false,
               CpuLoad: 0.3, GpuLoad: 0.2, Brightness: 0.6, Suspect: false);

    public static List<Reading> Minute(int startSecond, double totalW = 30, Quality q = Quality.Measured)
        => Enumerable.Range(startSecond, 60).Select(s => Reading(s, totalW, q)).ToList();
}
