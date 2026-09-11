using PowerLedger.Contracts;
using PowerLedger.Core;

namespace PowerLedger.Service.Tests;

internal static class Readings
{
    /// <summary>A measured one-second reading at <paramref name="at"/>: half the watts to the CPU, 4 W to the display.</summary>
    public static Reading At(DateTimeOffset at, double watts = 36, double delta = 1) => new(
        at, delta, watts, Quality.Measured,
        new Components(Cpu: watts / 2, Gpu: 0, Display: 4, Ram: 0, Storage: 0, Board: 0, Extras: 0, Monitors: 0, PsuLoss: 0, Unattributed: watts / 2 - 4),
        OnBattery: true, DisplayOn: true, UserIdle: false, SessionLocked: false, CpuLoad: 0.2, GpuLoad: null, Brightness: 0.5, Suspect: false);

    /// <summary>The same, <paramref name="second"/> seconds after <see cref="Samples.T0"/>.</summary>
    public static Reading At(int second, double watts = 36, double delta = 1) => At(Samples.T0.AddSeconds(second), watts, delta);
}
