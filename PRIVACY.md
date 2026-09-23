# PowerLedger privacy policy

Last updated: 24 September 2026, for PowerLedger 0.6.0.

PowerLedger works fully without sending anything. It sends data only when you turn on one of the switches described
here, and only what that switch covers. Until you choose, nothing is sent.

## Who is responsible

Muhammad Haris, developer of PowerLedger, is responsible for the data PowerLedger sends. For anything about your data,
write to **muhammadharis1302@gmail.com**. Quote your install ID, which Settings → Privacy shows, so the data sent from your PC
can be found.

## Your choices

PowerLedger asks once, in a dialog with four switches, all off until you turn them on. **Allow all**, **Allow none**
and **Save choices** are there, and you can change any switch at any time in Settings → Privacy. The choice covers
the whole PC, since the data describes the PC.

| Switch | What it sends | Why |
|---|---|---|
| Crash and sensor reports | Crashes and errors of PowerLedger, with their messages and stack traces. Which of its sensors work or fail, with the graphics card, UPS or power supply each read. PowerLedger's and Windows' versions, the processor architecture, and whether the PC is a laptop or a desktop. | To find and fix what goes wrong on hardware the developer doesn't have. |
| Usage | How often PowerLedger's window opens, which of its pages are opened, which settings are changed (never what they are changed to), reports exported, updates installed, days since PowerLedger was set up, the theme and the display language. | To decide what to improve next. |
| Hardware and power | The models of the processor, graphics card, monitors, power supply and UPS. The memory size and the machine details in Settings. The tariff's price and currency. And for each minute, the watts of each part, the processor and graphics loads, the brightness, whether the display was on and the PC idle, locked or on battery, and which figures were measured and which estimated. | To learn what real hardware draws, so PowerLedger's estimates become more accurate for everyone. |
| Share my detailed data | Nothing more than Hardware and power. It can only be on while that is. | It lets the data Hardware and power sends be given or sold, as it is, to researchers, hardware makers and energy companies. |

Every upload also carries a random install ID, the switches you chose, the day it covers and that day's offset from UTC.

**Consent is the basis for all of it.** Turning a switch off stops that data from then on, and deletes what was waiting
to be sent.

## What is never sent

- your name, or your PC's name, user account or domain;
- serial numbers, or Windows' device IDs;
- your files and folders, and the other programs you run;
- your location, beyond your country.

Crash and error text is cleaned before it is sent: user, PC and domain names, file and folder paths, device paths, IDs
and serial numbers, e-mail addresses and IP addresses are replaced by placeholders. A stack trace keeps only the name
of PowerLedger's own source file.

**Your IP address is not stored.** The server uses it to limit how often it can be called and to find your country
(two letters), then drops it.

## How it is sent and stored

- **How often:** once a day, one upload per complete day, over HTTPS.
- **Who can add to it:** each PC has its own random key, so no one else can add to or delete the data under its ID.
- **Where it is stored:** on Cloudflare, which runs the server (Workers, R2 storage and D1 database) as a processor on
  the developer's behalf. Cloudflare may handle it in any country where it operates. Cloudflare handles your IP address
  to deliver each request, under its own privacy policy.

**When data is shared** under Share my detailed data, it goes out as it is, except that the install ID is replaced by a
different pseudonym. Shared copies can't be recalled: deleting your data later removes it from the server and from
everything shared afterwards, but not from copies already given or sold.

## How long it is kept

| Where | How long |
|---|---|
| On your PC | Data waiting to be sent: at most 14 days. Copies of the last 30 uploads, so you can see them. |
| On the server | 3 years from the day the data describes, unless you delete it sooner. |

## Your rights

- **See what was sent:** Settings → Privacy → What's been sent shows each upload as it went. See what would be sent,
  in the dialog, shows it before you agree.
- **Delete it:** Settings → Privacy → Delete my data deletes everything sent from your PC from the server at once, and
  turns every switch off.
- **Withdraw consent:** turn the switches off at any time. What was sent before stays until you delete it or it
  expires.
- **Anything else:** access, correction or a question — write to the address above with your install ID.
- **Complaints:** you can complain to the data protection authority where you live.

## Changes

When what a switch sends changes, the consent version goes up. PowerLedger then sends nothing until you have chosen
again. Changes to this policy are listed in the release notes and in this file's history on GitHub.
