# Plan L — Measured AMD and Intel graphics, UPS and power supply readings

**Goal:** Read watts wherever the hardware reports them instead of modelling them. A discrete Radeon is read through
AMD's own libraries, an Intel Arc card through Level Zero Sysman, a UPS on USB through the HID power device class, and a
Corsair, NZXT or Thermaltake power supply through the DC output it reports. A graphics reading that covers only the chip
or the package counts the rest of the card, and where a UPS or a power supply covers this PC the total comes from it and
the App says so.

**Architecture:** Four sensor sources in `PowerLedger.Sensors` — `AmdSource` (ADLX, else ADL2's PMLog), `ArcSource`
(Level Zero Sysman energy counters), `UpsSource` (HID feature reports) and `PsuSource` (per-family read commands over
HID) — fill new `Sample` fields. `PowerModel` takes the total from the battery, then a UPS the user has spoken for, then
a power supply, then the model, counts a chip or package graphics reading at `RestOfCardFactor` 1.15, and records in
`Reading.TotalSource` which of the four gave it. `ReadingFrame.Total` and `ReadingFrame.GpuScope` carry that to the App,
`ServiceStatus.PowerDevices` lists each device the latest sample read, and `MachineProfile.UpsLoad` and
`MachineProfile.ReadPowerSupply` hold the two answers Settings asks for. Design:
`docs/superpowers/specs/2026-09-17-powerledger-gpu-ups-psu-design.md`; the main spec describes the result.

**Tech Stack:** .NET 10; ADLX's C vtables (`amdadlx64.dll`), ADL2's `ADL2_New_QueryPMLogData_Get` (`atiadlxx.dll`) and
Level Zero Sysman (`ze_loader.dll`), each loaded from System32 only; SetupAPI, hid.dll and overlapped file I/O for HID;
PMBus LINEAR11; xUnit + Shouldly, with a fake for every library and device.

---

## Why

The owner asked for more measured readings and fewer estimates, and chose graphics power on AMD and Intel and telemetry
from UPSes and power supplies: "read all, as this will be used by many". Until now only NVIDIA cards reported watts; a
Radeon or an Arc card fell back to the load model with a rated figure from the table, and a desktop's total was always
modelled, since the one whole-machine sensor a desktop might have is a UPS, which Windows reports as a battery and the
model ignores.

The owner's own desktop has neither a UPS nor a power supply that reports over USB, so none of the four paths could be
tried on the owner's hardware. The facts they are built on come from a research pass: the vendor SDK headers, NUT's HID
tables for UPSes, the Linux corsair-psu driver, liquidctl's protocol notes and TTController (MIT).

## Execution

| Phase | Agent | Work |
|---|---|---|
| 0 | Lead | the design; the sample, frame, status and profile fields the five agents shared |
| 1 | totals and screens | the total from a UPS or a power supply, the 1.15 factor, where each total came from; the frame and status fields; Settings' device rows and Now's notes |
| 1 | arc | Level Zero Sysman: the card's energy counter, its domain's scope, and a card Windows has switched off left asleep |
| 1 | psu | Corsair HXi and RMi, NZXT E and Thermaltake DPS G over HID, read commands only, with the owner's tick and the maker's program both silencing it |
| 1 | ups | the HID power device class: the output's active power, else the load of the rated watts, else of the rated volt-amperes |
| 1 | amd | ADLX for the board or the chip, ADL2's PMLog where ADLX won't answer, and the source ahead of the load counters |
| 2 | Lead | the merge; review, installers, Sandbox, CI and release |

The five worked at once, each in a worktree of its own, and the lead merged each branch onto `main` as it finished: the
model and the screens first, then Arc, the power supply, the UPS and AMD. Two commits followed the last merge: the
Windows HID calls the UPS and the power supply both make are declared once, with the overlapped reads and writes only a
power supply needs above them, and the assembled sensor set's health is asked with an expression a query can read, now
that three libraries may fill a card's watts. `main` holds Plan L's 14 commits, from `e135dfa` to `b6e4df1`.

## Results

2026-09-17. Built by five parallel agents in one phase, each in a worktree of its own, on the fields the lead had given
the sample, the frame, the status and the profile first. Nothing is released yet: the version at `b6e4df1` is still
0.4.1.

**Verified**

- `dotnet build -c Release`: 0 warnings. Every test outside `Hardware` and `Installed`, run at `b6e4df1`: 1,701 pass
  (Core 181, Storage 49, Sensors 534, Service 243, App 694 with the UI renders).
- The 15 Hardware tests in Sensors pass on the development laptop: its HID collections open and their descriptors parse,
  the UPS source and the power supply source each read the machine without throwing and find neither device, the two
  sources see the same devices because one layer lists them, the AMD source says "no AMD driver installed", and the Arc
  source says "no Intel Arc discrete GPU".
- Level Zero read the laptop's built-in Iris Xe through the one power domain it has, its package, at 8.734 W, while the
  processor's own energy meter read the package at 8.746 W over the same three seconds: 0.1% apart. That is the only
  reading in Plan L taken from real hardware, and it is the reason graphics inside the processor are skipped — both
  figures come from the same meter.
- The totals and the factor are covered in Core: a UPS said to power this PC gives the total with the monitors that have
  plugs of their own added, one said to power this PC and its monitors gives it whole, a total from the load of the
  rated volt-amperes is Estimated where the other two ways are Measured, a laptop running on its own battery reads
  neither device, a UPS said to power this PC wins over the power supply and one that powers more leaves the total to
  it, a power supply's DC output over its efficiency gives the total, the rest band closes the components to the total
  and goes negative where the parts come to more, and a chip or package reading counts at 1.15 while a modelled one
  never does.
- Nothing is said to a power supply until the loop has put settings in force, the owner's tick is asked afresh before
  every read, and a sensor set that has been retired says nothing more to the device.
<!-- lead: review: findings, and the fix for each -->
<!-- lead: installers at full compression: universal, x64 and Arm64 sizes -->
<!-- lead: Windows Sandbox, end to end: N of N, and what the run covered -->
<!-- lead: CI run and both jobs' results -->
<!-- lead: release link -->

**Deviations from the design**: the design divides a power supply's DC output by its efficiency at that load, from its
80 PLUS tier's curve; the model divides by the one figure the tier gives (`PsuEfficiency.For`), the same number the AC
model has always used, so the load is not taken into account. ADL2 is taken not only where ADLX is missing but also
where ADLX is installed and would not start or would not list the machine's cards, and a library that would not start is
opened again a minute later rather than settled, because ADLX answers nobody in session 0 until a user logs on. Not in
the design: where several UPSes are attached only the first is read and the status says so; a UPS that stops answering
keeps its last reading for 15 seconds and is then dropped and looked for again, which also gives it a fresh handle; a
power supply is looked for every 10 seconds for the first minute, since Windows is still enumerating USB devices at
boot; and the power supply is left alone entirely until the loop has published settings and once its sensor set has been
retired, as well as while the tick is off.

**Not covered**: no discrete Radeon, no Arc card, no UPS and no power supply that reports over USB has been read for
real, so all four paths rest on fakes and on Hardware tests that read whatever the machine running them has. Whether
ADLX, ADL and Level Zero answer from the service in session 0 is untested; where they do not, a card falls back to the
load estimate as before. A UPS reports its load in whole percents, so a total taken from the load moves in steps of 1%
of the rating, 5 to 9 W on a consumer unit, and the UPS's own overhead is not counted. Corsair's AXi, which needs a
vendor driver, and ASUS's ROG Thor, whose protocol is not published, are not read. The 1.15 factor rests on published
measurements of two cards, not on a card measured here.
