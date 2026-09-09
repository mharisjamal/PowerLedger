# PowerLedger.Demo

A console preview of the finished Core and Storage libraries. It reads what Windows exposes
without a kernel driver (battery discharge rate, AC state, CPU load, user idle time), runs it
through the real `PowerModel`, `CalibrationLearner`, `EnergyIntegrator` and `Downsampler`, stores
the result with the real repositories, and prints a live reading plus a 30-day report.

This is a preview, not the shipping product. The real sensor adapters, the Windows service and
the WPF app come later; this just proves Core and Storage work end to end on real hardware.

## Commands

```
dotnet run --project samples/PowerLedger.Demo -c Release              # record 60 s, then report
dotnet run --project samples/PowerLedger.Demo -c Release -- 12        # record 12 s, then report
dotnet run --project samples/PowerLedger.Demo -c Release -- report    # report only, no recording
```

## Estimated vs. measured

While plugged into AC there is no kernel driver for true CPU package power, so readings are
`estimated` from the CPU load model. Unplug the charger and Windows reports a real battery
discharge rate, which the model uses directly — readings switch to `measured`. Stay on battery
long enough (about 15 ticks per brightness bucket) and it also starts reporting `calibrated`.

## Database

`%LOCALAPPDATA%\PowerLedger\demo.db` — created and migrated on first run. Delete it to start over.
