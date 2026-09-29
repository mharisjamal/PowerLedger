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

## 7. Workstation extras (2026-09-30, "measure more", part B)

- **A UPS on another computer (NUT).** Settings takes a server, port (3493), the UPS's name there, and a username and
  password for a server that lists only to known users. The service reads `LIST VAR <ups>` over TCP every 5 s on a thread of
  its own, over one connection kept open, never sends LOGIN, and keeps the last answer for 15 s. Timeouts of 5 s; a server
  that fails is retried after 5 s, doubling to a minute; DATA-STALE keeps the connection. Watts, best first:
  `ups.realpower` (Measured); `ups.power` × `output.powerfactor` (Measured); `ups.load` × `ups.realpower.nominal`
  (Measured); `ups.power` × 0.8 (Estimated); `ups.load` × `ups.power.nominal` × the factor or 0.8 (Estimated). It fills the
  UPS fields only when no UPS on USB gave watts, and "What does it power?" decides for it as for a USB UPS. The password is
  kept encrypted (DPAPI, the service's account) under its own settings key, never in the settings and never sent back: the
  App sends one only when typed, an empty one to forget it, and it goes when no server is set up.
- **Windows' power meters.** `\Power Meter(*)\Power` (milliwatts) is read only for an ACPI power meter (ACPI000D), the
  platform's input power, told apart by WMI's `Win32_PowerMeter` device paths matched to the instances in order. A battery's
  meter (PNP0C0A) is never read: the owner's laptop has only that one, reading nought on mains. Meters that can't be told
  apart are not read. With several platform meters the largest is taken. Nought is no reading.
- **A BMC (IPMI DCMI).** Where Windows' IPMI driver has a `root\wmi` `Microsoft_IPMI` instance, the service sends Get Power
  Reading (NetFn 0x2C, cmd 0x02, `DC 01 00 00`) every 5 s on a thread of its own. The current watts count only while the state
  byte says the measurement is on. A BMC without DCMI (0xC1) is asked again every 30 minutes.
- **The total.** A machine's own meter (power meter first, then the BMC) is its Measured total, wall power already, after a
  UPS the owner said powers this PC and before a power supply's readings; monitors with their own plug are added. Never on a
  laptop running on its battery. TotalSource PowerMeter (6) and Bmc (7); the Now note says "This PC's own power meter
  reading" or "This PC's management controller reading".
- **Intel Arc through IGCL.** Where ControlLib.dll starts and a discrete Intel card reports `totalCardEnergyCounter`
  (`ctlPowerTelemetryGet`, structure version 1), its joules over the telemetry timestamp's seconds are the card's Board
  watts. A card Windows has switched off draws nought and isn't asked; one asleep at start, a library that is missing or won't
  start, a card without the card counter, and five failures in a row all leave the card to Level Zero (§2), as before.

## Honest limits

- None of AMD, Intel Arc, UPS or power supply reading has met real hardware; tests use fakes, plus Hardware tests that run
  where the DLL or device exists.
- Reading Corsair's 0xEE as the wall draw is reasoned, not measured. The conclusive check, 0xEE against the rails read one
  by one, needs a write to the page register, which these rules turn down. If it is the DC output after all, the wall figure
  is low by the supply's losses rather than high by them.
- A reading of nought watts from a UPS or a supply is taken as no reading, and both figures have plausible ceilings in the
  validator (UPS 5 kW, supply 2 kW); past those the report has been misread rather than the outlets loaded.
- None of §7 has met real hardware beyond this: the owner's laptop has one power meter, its battery's, which is left alone;
  its IGCL answers "platform not supported" (Tiger Lake), so IGCL's structures are checked against the header, not a card.
  Whether IGCL answers in session 0 is unknown; where it doesn't, Level Zero reads the card. The layout of
  `Microsoft_IPMI.RequestResponse`'s answer (whether the completion code leads the data) is handled both ways, untested. The
  WMI and PDH order used to match power meters is assumed, and only matters beside a battery's meter.
- Whether ADLX, ADL and Level Zero answer from the service in session 0 is untested. Where they don't, the reading falls back
  to the load estimate, as today.
