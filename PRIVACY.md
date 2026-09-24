# PowerLedger privacy policy

Last updated: 24 September 2026, for PowerLedger 0.7.0.

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

## Households

A household is a group of your PCs, or your family's, that show what they use together. It is a separate choice from
the switches above. Nothing is shared until you add a PC and confirm it on that PC.

- **What a household shares:**
  - each PC's hourly totals: energy, cost and currency, the split by part, and idle, on-battery and measured time;
  - each PC's name and whether it is a laptop or a desktop;
  - nothing finer than an hour.
- **Who can read it:** only the PCs in the household. The totals are encrypted on each PC with a key only the household's
  PCs hold (AES-256-GCM), and the server can't read them.
- **What the server keeps:**
  - the household's random ID;
  - each PC's random ID and public keys;
  - when each PC sends and how much;
  - the encrypted totals, for 90 days, so a PC away from home can catch up. Each PC sends all its totals again about
    once a month, still encrypted, so a new or returning PC can read the whole year.

  A pairing code's meeting place lasts 10 minutes. The server uses your IP address only to limit how often it is called,
  and doesn't keep it.
- **On your network:** while **Let my other PCs find this one on the network** is on (Settings → Household), this PC
  tells other devices on a private network that it runs PowerLedger, with its name. Turn it off and it doesn't.
- **Removing a PC or leaving:** a removed PC keeps what it already had but can read nothing new, since the household's
  key changes. When the last PC leaves, the server deletes the totals, keys, requests and sign-in links it kept for the
  household. It keeps only the household's random ID and its PCs' random IDs, public keys and when they joined and left,
  so a PC that comes back is told it was removed.

## Signing in

Signing in with Microsoft or Google is optional. It lets a new PC join your household once one of your other PCs
approves it, and lets you get the household back with your recovery code if you lose every PC.

- **What the server keeps:**
  - an account ID made from the ID your provider gives it;
  - which household your account is linked to;
  - a sign-in session for each PC;
  - requests from PCs waiting to join, for up to a day, or up to 7 days once approved.

  Your e-mail address is shown on your PC and not kept on the server.
- **Approving a PC:** both PCs show the same 6-digit code before anything is handed over. Check they match. The server
  passes the keys along, and a server that swapped them would make the codes differ.
- **Your recovery code:**
  - It is shown once. The server keeps the household key and its list of PCs encrypted under a key made from that code,
    and it can't read the code, the key or the list.
  - Signing in on a new PC with the code brings the household back to that PC alone: your other PCs are removed, the
    code is used up, and the PC shows you a new one.
  - Keep the code safe: with it and your account, anyone can get into your household's totals.
- **Deleting your account** (on the Household page) deletes the account, its link, its sessions and its recovery copy.
  Your household carries on without sign-in.

## Feedback

The bug button at the foot of the window sends a report only when you press **Send**, and only what the window shows.

- **What a report carries:** what you typed; the images you added, including any PowerLedger screenshot you asked for;
  the app version, your Windows version and whether the PC is x64, Arm64 or 32-bit; and, while the box is ticked, the
  last lines of PowerLedger's logs. Your e-mail address goes only if you type it, and is used only to reply.
- **Where it goes:** through PowerLedger's server to a private issue tracker on GitHub that only the controller can see.
  GitHub, Inc. stores it there. The server keeps no copy; it uses your IP address only to limit how often it is called.
- **How long:** until the controller has dealt with it and deletes it. Write to the address above to have a report
  deleted sooner.
- **Offline:** a report that couldn't be sent waits in `%LOCALAPPDATA%\PowerLedger\Feedback` on your PC, for up to 30
  days, and goes when the PC is online. Delete the file to stop it.

## Changes

When what a switch sends changes, the consent version goes up. PowerLedger then sends nothing until you have chosen
again. Changes to this policy are listed in the release notes and in this file's history on GitHub.
