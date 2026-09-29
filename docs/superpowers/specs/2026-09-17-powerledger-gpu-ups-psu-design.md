# Measured AMD and Intel graphics, UPS and power supply readings

2026-09-17. The owner asked for more measured readings and fewer estimates. They chose AMD/Intel GPU power and PSU/UPS
telemetry ("read all, as this will be used by many"). Their own desktop has no USB power supply and no UPS, so those paths
can't be tried on the owner's hardware. The facts below come from a research pass: vendor SDK headers, NUT's HID tables,
the Linux corsair-psu driver, liquidctl's documentation (protocol facts only, no code) and TTController (MIT).

## 1. AMD Radeon, discrete only

- **Reading it.** Read ADLX `IADLXGPUMetrics::GPUTotalBoardPower` (scope Board) where it is supported, else `GPUPower`
  (ChipOnly), through ADLX's C vtables (`amdadlx64.dll`, installed with the driver). Where ADLX isn't there, fall back to
  ADL2 `ADL2_New_QueryPMLogData_Get` (`atiadlxx.dll`): sensor 73 `BOARD_POWER` is Board, sensor 23 `ASIC_POWER` is
  ChipOnly.
- **Which GPUs.** Integrated Radeon GPUs are skipped: their draw is inside the CPU package.

## 2. Intel Arc, discrete only

- Read Level Zero Sysman (`ze_loader.dll`): `zesInit`, then the devices; skip any flagged `ZES_DEVICE_PROPERTY_FLAG_INTEGRATED`.
- **Scope.** Use the power domain whose extended properties say CARD (scope Board), else PACKAGE (scope Package).
- **Watts.** Energy counter deltas (µJ over µs). A card asleep in D3 reads 0 W.
- **Checked on the owner's laptop.** The Iris Xe's one PACKAGE domain matched the CPU package counter, which is why
  integrated GPUs are skipped.

## 3. The model for a chip or package reading

A ChipOnly or Package figure leaves out the card's memory regulators, VRM losses and fans. Measured cards drew 12–25% more
than their ASIC figure (RX 5700 XT 180 W to 202 W; RX 6800 XT 255 W to about 300 W). The model counts such a reading × 1.15,
and the App says "chip measured, rest of card estimated".

## 4. UPS over USB (HID Power Device class)

- **Reading it.** Open the UPS's top-level collection (page 0x84, usage 0x04 UPS or 0x24 PowerSummary) with shared access,
  which Windows allows alongside `hidbatt`. Read feature reports only (`HidD_GetFeature` and `HidP_GetUsageValue`, applying
  each unit's exponent), and never set anything.
- **Watts,** from the best source the UPS has:
  - Output ActivePower (0x34);
  - else PercentLoad (0x35) × ConfigActivePower (0x44);
  - else PercentLoad × ConfigApparentPower (0x43) × 0.8, which is an estimate.
- **What it covers.** Everything on the UPS's outlets, so its reading is used only when the user says what it powers, in
  Settings:
  - **This PC.** The total is the UPS's watts, plus the counted own-plug monitors.
  - **This PC and its monitors.** The total is the UPS's watts.
  - **More.** Not used.
  - **Not said.** Not used.
- **Honest limits.** Load is in whole percents, so readings move in steps of 1% of the rating (5–9 W on a consumer UPS).
  The output is what the PC draws; the UPS's own overhead is not counted.

## 5. Power supplies over USB

- **Corsair HXi and RMi** (HID 1B1C): read the unit's own total (command 0xEE, PMBus LINEAR11), with read commands only.
  That total is the AC it draws from the wall, not the DC its rails put out: it reads above the sum of the unit's own rails
  by about its tier's losses, the unit measures its input volts and amps (0x88, 0x89), and PMBus has a per-rail output
  command but none for a whole unit's input. It is therefore taken as the total undivided.
- **NZXT E500/E650/E850** (HID 7793): sum the per-rail outputs.
- **Thermaltake DPS G** (HID 264A:2329): sum volts × amps per rail.
- **Not supported:** Corsair AXi (needs a vendor driver) and ASUS ROG Thor (no protocol known).
- **Other programs.** Such a device answers one program at a time, so it is skipped while iCUE, CAM or Thermaltake's app
  is running.
- **Wall power,** for a supply that reports its rails, is the DC output ÷ its efficiency at that load: its 80 PLUS tier's
  curve, read at the load the supply is carrying against the rating its model name gives, and the tier's flat half-load
  figure where the name says none. A laptop read this way uses its adapter's efficiency, since no supply tier describes an
  adapter. The App says "Power supply's DC output, with its efficiency", and "Power supply's own wall reading" for a unit
  that reports what it draws.
- **Settings** has a tick to stop reading it (on by default).

## 6. What the user sees

- **Now screen.** The live note says where the total came from (UPS, a power supply's own wall reading, a power supply's DC
  output with its efficiency, the battery or the model), and the GPU row says chip or package measured, rest estimated.
- **Settings.** Detected UPSes and power supplies are listed with their watts. A UPS asks what it powers, and a power supply
  has its tick. Both save by themselves.
- **Quality.** A UPS total (ActivePower or load of rated watts) and a power supply total count as Measured; the load of rated
  VA counts as Estimated.

## 7. Measure more, estimate less (2026-09-30)

The research "measure more" (2026-09-30) ranked driver-free sources; part A adds three.

- **Snapdragon X energy rails.** The Energy Meter's `soc`, `cpu_cluster_N`, `gpu`, `npu`, `memory` and `system` are
  classified through one nesting table (`QualcommRails`): `soc` is the CPU package (never added to its clusters, graphics
  or NPU), `gpu` the integrated graphics, and `system` the whole platform, display included. A laptop's total from `system`
  is Measured (source `PlatformMeter`, "platform meter" on the Now screen), over the adapter's efficiency on AC. The names
  come from npu-watt; the nesting, and whether `system` sits behind the charger, are **unverified on real hardware** and are
  corrected in that table alone once a device dump says otherwise.
- **NVIDIA total energy.** Volta and newer cards are read from `nvmlDeviceGetTotalEnergyConsumption` deltas, exact over each
  tick; the power figure stays for the first read, a counter reset or wrap, a gap over 10 s, a declined read and older cards.
  Quality is unchanged: an NVML figure was already a measured part.
- **Battery.** A battery with `BATTERY_CAPACITY_RELATIVE` gives no watts. On AC a discharge is a floor under the total
  (quality unchanged, since it bounds the total rather than measuring it). Charging is never counted as consumption.
- **Sharing.** The shared report's `totalSource` column is 0 to 4 on the server (server/src/minutes.ts), so a minute whose
  source is `PlatformMeter` (5) is left out of the report until the server accepts it.

## Honest limits

- None of AMD, Intel Arc, UPS or power supply reading has met real hardware; tests use fakes, plus Hardware tests that run
  where the DLL or device exists.
- Reading Corsair's 0xEE as the wall draw is reasoned, not measured. The conclusive check, 0xEE against the rails read one
  by one, needs a write to the page register, which these rules turn down. If it is the DC output after all, the wall figure
  is low by the supply's losses rather than high by them.
- A reading of nought watts from a UPS or a supply is taken as no reading, and both figures have plausible ceilings in the
  validator (UPS 5 kW, supply 2 kW); past those the report has been misread rather than the outlets loaded.
- Whether ADLX, ADL and Level Zero answer from the service in session 0 is untested. Where they don't, the reading falls back
  to the load estimate, as today.
