# Plan K — Settings that save themselves; monitors switched off, refresh rate, 200-nit figures and HDR

**Goal:** A change made in Settings is kept the moment it is made, with no Save button to miss. A monitor that is
switched off stops counting as on wherever the monitor says so, a monitor driven above 60 Hz counts what its refresh
rate adds, ENERGY STAR's figures are taken where 200 cd/m² sits on a monitor's brightness scale, and HDR is flagged.
Version 0.4.1.

**Architecture:** Settings' service form saves itself (`ServiceForm`, `savesItself`), one save at a time, while the
wizard's still saves when Next is pressed. The App's monitor reader also asks each monitor for its power mode, MCCS VCP
code D6, every minute, and a new `DisplayConfigReader` reads the refresh rate and HDR state Windows drives each monitor
at; `reportBrightness` carries both to the service. `MonitorBoard` counts a monitor that says it is off at its off watts
and one on standby at its sleep watts, adds a refresh term for an LCD panel above 60 Hz, scales the listed figure from
its 200-nit point, and ages what the App reported only while the displays are on. Design:
`docs/superpowers/specs/2026-09-17-powerledger-autosave-monitor-power-design.md`; the monitors spec and the main spec
describe the result.

**Tech Stack:** .NET 10 WPF (CommunityToolkit.Mvvm), Dxva2 and User32 P/Invoke (`GetVCPFeatureAndVCPFeatureReply`,
`QueryDisplayConfig`, `DisplayConfigGetDeviceInfo`), System.Text.Json source generation, xUnit + Shouldly +
`FakeTimeProvider`.

---

## Why

On a desktop with two monitors, the owner ticked "Count it" in Settings, went to the Now screen, found the display still
at 0 W, and found the box unticked on coming back. The tick took effect only when **Save settings**, two sections
further down, was pressed, and leaving Settings loaded the page again from the service. The owner asked for Settings to
save by itself, and for PowerLedger to tell by itself when one of two connected monitors is switched off, leaving the
Count tick to decide only where it can't.

While that was built, the owner asked for the refresh rate of modern monitors to count too, since, as the owner put it,
a monitor running a game at a high refresh rate draws more than one showing a web page. Research gave a term for LCD
panels, and with it two more findings: ENERGY STAR's figure is taken at 200 cd/m², not at 75% of a monitor's range, and
HDR can double what a monitor draws. The owner chose all three for 0.4.1.

## Execution

| Phase | Agent | Branch (worktree) | Work |
|---|---|---|---|
| 0 | Lead | `main` | the design; the report's and the status's power state; version 0.4.1 |
| 1 | autosave | `plan-k/autosave` | Settings saves by itself; no Save settings button; one line says how saving went |
| 1 | power-svc | `plan-k/power-svc` | off watts; the board counts a monitor off or on standby, and gives its state |
| 1 | power-ddc | `plan-k/power-ddc` | each monitor asked its power mode, and the states reported every minute |
| 1 | power-ui | `plan-k/power-ui` | each row says on, standby, off or can't tell; the Now screen counts those off |
| 2 | Lead | `main` | the refresh term, 200-nit anchor and HDR flag designed, with their report and status fields |
| 2 | power-svc | `plan-k/power-svc` | 0.23 W asleep, 200-nit anchor, refresh term, ageing only while displays are on |
| 2 | power-ddc | `plan-k/power-ddc` | power mode first, unsupported after three failures; `DisplayConfigReader` |
| 2 | power-ui | `plan-k/power-ui` | each row's refresh rate and what it adds; "estimated" for it on Now; HDR on both |
| 3 | Lead | `main` | review, installers, Sandbox, CI, release 0.4.1 |

A fifth agent researched what refresh rate, ENERGY STAR's test conditions and HDR mean for a monitor's draw while phase
1 ran; its findings are the design's §3 to §5.

## Results

2026-09-17. Built in two phases of parallel agents, each in a worktree of its own: four at once, then three of them
again once the owner had chosen the refresh rate, the 200-nit anchor and HDR. Before each phase the lead gave the report
and the status the fields the agents shared, and merged each branch onto `main` as it finished. `main` holds Plan K's 33
commits at `d95c4f9`.

**Verified**

- `dotnet build -c Release`: 0 warnings. Every test outside `Hardware` and `Installed`, run at `d95c4f9`: 1,525 pass
  (Core 156, Storage 49, Sensors 417, Service 229, App 674 with the UI renders).
- The typical figures are the shipped table's medians: 0.23 W asleep over its 1,580 monitors, and 0.16 W off over the
  1,572 that give it. 32 listings give an OLED panel, which adds no refresh term.
- The estimate's on-mode figures are unchanged: with each of the table's 1,576 monitors up to 57" estimated from all the
  others, it lands within 9.04% for half of them and 25.6% for nine in ten.
- On the development laptop, the display configuration reader found one path, the built-in panel, at 60.052 Hz with HDR
  off, and the DDC/CI reader got no answer from the panel in two reads, as expected of a laptop's own screen.
<!-- lead: review: findings, and the fix for each -->
<!-- lead: installers at full compression: universal, x64 and Arm64 sizes -->
<!-- lead: Windows Sandbox, end to end: N of N, and what the run covered -->
<!-- lead: CI run and both jobs' results -->
<!-- lead: release link -->

**Deviations from the design**: a monitor whose power-mode request fails is taken not to support power mode only after
three failed requests in a row, each in a read in which its brightness answered, where the design took one; its
brightness is asked after each failure, due or not, to tell a monitor without power mode from one that has stopped
answering. What the App reported goes stale after three minutes of the displays being on, not three minutes of any kind,
and a brightness after 15 minutes of the same, so a monitor last known to be off goes on counting as off while the
displays sleep. The refresh term applies to a monitor that hasn't said whether it is on, as well as to one that says it
is on. Not in the design: the power mode is asked before the brightness, and a monitor that says it is off or on standby
isn't asked its brightness; the typical sleep figure is the table's median, 0.23 W, where it had been 0.2 W; and with
reads every minute, a monitor that has given a brightness and then fails is first left alone for a minute rather than
five, the wait still doubling up to an hour.

**Not covered**: neither the power-mode request nor the display configuration reader has met a real external monitor,
since the development laptop's only screen is its built-in panel. The refresh coefficient rests on two published
measurements, and what HDR adds isn't measured.
