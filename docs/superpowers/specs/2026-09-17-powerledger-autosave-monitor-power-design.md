# Settings that save themselves, and monitors that are switched off

2026-09-17. Approved by the owner the same day for 0.4.1. Changes the main spec's §5, §9 and §11, and the monitors spec.

## Why

On a desktop with two monitors, the owner ticked "Count it", went to the Now screen, saw the display still at 0 W, and
found the box unticked when coming back. Nothing was wrong in the service. The tick only takes effect when **Save
settings** is pressed, and that button sits two sections further down. Leaving Settings reloads it from the service, so the
tick was dropped without a word.

The owner also often has two monitors connected with one switched off. PowerLedger counts that monitor as on until the
user unticks it, and has to be told again when it is switched back on.

## Decisions

| Question | Decision |
|---|---|
| Save button or saving by itself | Settings saves by itself. |
| What saves when | A tick or a segmented button saves at once. A typed number saves when its box loses focus or Enter is pressed. |
| A value that can't be saved | It is named right there, next to where the settings are said to be saved, and nothing is sent. |
| Tariff and CO₂ | They keep their Save buttons. Saving a tariff starts a new dated price, so it mustn't happen on every keystroke. |
| The setup wizard | It is unchanged: each step saves when Next is pressed. |
| How PowerLedger tells a monitor is off | It asks each monitor for its power mode over the display cable once a minute (DDC/CI, VCP code D6), read-only. A monitor Windows no longer lists already stops counting. |
| A monitor that doesn't say | PowerLedger can't tell, so the user's Count tick decides, and the row says so. |

## 1. Settings saves by itself

- **When it saves.** The service part of Settings (Machine, Sampling and history) sends the settings whenever the user
  changes something.
  - A tick box (Count it, has its own plug, DDR5) or a segmented button (chassis, power supply rating, sample interval)
    saves at once.
  - A typed box (memory sticks, drives, fans, panel size, extras, rated watts, a monitor's watts, idle minutes, hours and
    years of history) takes its new value when it loses focus or Enter is pressed, and that saves.
- **What the user sees.** "Save settings" is gone. One line says "Saving…", "Saved." or the first problem, e.g. "Type the
  fans as a whole number." It sits where it can be seen while editing either section. A problem sends nothing, and the value
  stays in its box to be corrected.
- **Only the user's changes save.** Loading the form, the ten-second status refresh and a save's own read-back never send
  anything.
- **One save at a time.** A change made while a save is on its way is sent after it, so the last change always wins.
- **Nothing typed is lost.** A save doesn't refill the form: it takes what it sent as the settings the service holds. The
  "read the service's settings again when behind" rule stays.
- **Monitors.** A choice is saved only when it differs from the service's defaults, as now.

## 2. Monitors that are switched off

- **Asking the monitor.** The App's brightness reader also asks each monitor for its power mode (MCCS VCP D6) with
  `GetVCPFeatureAndVCPFeatureReply`. That is a read, like the brightness request.
  - It asks once a minute, while the service says the displays are on. Brightness is still read every five minutes.
  - The answer maps as: 1 is on; 2 (standby) and 3 (suspend) are standby; 4 and 5 are off.
  - A monitor that fails this one request, while it answers the others, is taken not to support it until a display change
    or a resume. The failure and back-off rules for brightness apply as they are.
- **Reporting it.** The App sends the answers with the brightness report (`reportBrightness` gains a power list). The
  service keeps each attached monitor's state. A state older than three minutes, or never reported, is unknown.
- **What the model counts** for a monitor that counts:
  - off: its off watts;
  - standby: its sleep watts;
  - on or unknown: as today, meaning its figure at its brightness while the displays are on, and its sleep watts while
    they are off.
  - Off watts come from the ENERGY STAR list's `off_w` for a matched model, or the median of alike monitors for an
    estimated one.
  - This applies to monitors with their own plug and to those running off the PC alike.
- **What the user sees.**
  - A monitor row says "on", "standby" or "off", read from the monitor, or "can't tell if it's on".
  - The Now screen's display row says how many of the monitors it adds are off.
  - The Settings note says PowerLedger reads whether each monitor is on, and that where it can't tell, unticking a monitor
    while it's off keeps it out.
- **Honest limits.** Many monitors stop answering once switched off at their own button, and some docks and adapters pass
  nothing on. For those, PowerLedger can't tell, and the Count tick decides. A monitor switched off can take up to a
  minute to be noticed.

## Testing

- **Settings.**
  - A tick saves once.
  - A committed typed value saves, and a typed value not yet committed doesn't.
  - An invalid value names the problem and sends nothing.
  - Loading and status refreshes send nothing.
  - Changes made during a save are sent after it.
  - A save doesn't overwrite other boxes.
  - The Settings renders pass in both themes.
- **The reader.**
  - D6 answers map to states.
  - A monitor without D6 is taken not to support it.
  - The power mode is read every minute and brightness every five.
  - Only Get requests are sent.
- **The service.**
  - Off counts off watts and standby counts sleep watts.
  - An unknown or stale state counts as before.
  - Only attached instances are kept.
  - The report is validated: at most 16 entries, a valid state and instance.
- **Windows Sandbox.** A typed Settings value saves itself, with no Save button, and survives leaving and coming back.
