# PowerLedger.Demo

A console preview of the finished Core, Storage and Sensors libraries — not the shipping product;
the Windows service and the WPF app come later. It detects the machine and reads it through
`PowerLedger.Sensors` — no kernel driver — runs every tick through the real `PowerModel`,
`CalibrationLearner`, `EnergyIntegrator` and `Downsampler`, stores it with the real repositories,
and prints a live reading plus a 30-day report.

## Commands

```
dotnet run --project samples/PowerLedger.Demo -c Release              # record 60 s, then report
dotnet run --project samples/PowerLedger.Demo -c Release -- 12        # record 12 s, then report
dotnet run --project samples/PowerLedger.Demo -c Release -- report    # report only, no recording
```

## Estimated vs. measured

CPU watts come from the Windows Energy Meter Interface with no kernel driver, so `cpu` is a real
measured reading whenever the meter is present. Discrete GPU watts come from NVML when the card
reports power telemetry; a card with no measurement hardware (most low-end laptop GPUs, including
this machine's MX330) reports none and falls back to a load-based estimate. The `estimated` /
`calibrated` / `measured` label describes the *total*, not the CPU figure: it is `measured` only
on battery, where the discharge rate is ground truth. Calibration is saved keyed to this machine's
hardware hash and reloaded next run, so it keeps improving across sessions instead of starting over.

## Database

`%LOCALAPPDATA%\PowerLedger\demo.db` — created and migrated on first run. Delete it to start over.
