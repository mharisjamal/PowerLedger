# Monitors' measured power

`energy-star-monitors.csv` lists certified computer monitors with the power each drew when it was tested (on, sleep and
off watts), along with the screen size, resolution and panel PowerLedger uses to recognise a monitor or to estimate one
it doesn't know. It ships inside PowerLedger, so nothing is fetched at runtime.

## Where it comes from

The dataset **ENERGY STAR Certified Displays**, published by the US Environmental Protection Agency at
<https://data.energystar.gov/Active-Specifications/ENERGY-STAR-Certified-Displays/qbg3-d468> (API:
<https://data.energystar.gov/resource/qbg3-d468.json>). Only its monitors are used, not its signage displays. Fetched
2026-09-16; the dataset's rows were last updated 2026-09-15.

It is a work of the US Government, in the public domain, published under the
[EPA Data License](https://edg.epa.gov/EPA_Data_License.html). ENERGY STAR is a registered trademark of the EPA, named
here only to say where the figures come from. PowerLedger is not certified by, endorsed by or affiliated with ENERGY
STAR or the EPA, and doesn't use the ENERGY STAR mark.

## Columns

`brand, model_number, model_name, alternatives, inches, width, height, panel, on_w, sleep_w, off_w, max_nits, hdr, certified`

`alternatives` holds the other identifiers a monitor is listed under, joined with `|`. `max_nits` is the maximum
luminance in cd/m², `hdr` the DisplayHDR tier (empty where the list says N/A), and `certified` the date, yyyy-MM-dd.
Numbers use `.` and have no trailing zeros; an empty field is one the list doesn't give. The file is UTF-8 without a
BOM, with CRLF line ends and RFC 4180 quoting.

## Refreshing it

Run `pwsh assets\monitors\make-monitor-table.ps1` (PowerShell 7.5 or later). It downloads the list again, rewrites the
table and says what it dropped. Commit the table and update the date above.

## How the list is cleaned

- Monitors only.
- A monitor with no brand, no model number or name, no screen size, no resolution that reads as "width x height", or no
  on-mode watts is dropped.
- A monitor whose on-mode watts are under 1 W, or more than three times the median of the monitors of its size rounded
  to the nearest inch, is dropped. The list has typing errors of that kind, such as a 24" monitor at 151.3 W.
- The resolution becomes whole-number `width` and `height`, in the order the list gives them (some give the shorter side
  first); the size becomes a decimal.
- Brand, model number, model name and panel are written exactly as the list has them. PowerLedger normalises model
  strings itself when it matches.
- `alternatives` comes from the additional model information, model number and model name, split at commas,
  semicolons, slashes, brackets and the words "and" and "or". A piece is kept when it contains only letters, digits,
  `-`, `_` and the wildcards `*`, `?` and `#`, has at least one letter and one digit, and isn't the monitor's own model
  number or name; each is kept once. The prose around them ("* can be any alphanumeric character") is dropped.
  Wildcards, including runs of `X` standing for any character, are kept as written.
- Rows are sorted by brand, then model number, ignoring case, so a refresh diffs cleanly.
