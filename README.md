# BattTray

A Windows tray app that shows the battery level of your PC peripherals.

Goal: one place to see the charge of every peripheral, whether it is attached over
Bluetooth, wired USB, or a 2.4 GHz dongle.

## Status

| Transport | State |
|---|---|
| Bluetooth | Implemented |
| Wired USB | Not started |
| 2.4 GHz dongle | Partial — XInput controllers only, in four bands |

Charging state is modelled but not yet reported by any source — see below.

## Install

Download `BattTray.exe` from the [latest release](https://github.com/Jccqt/BattTray/releases/latest)
and run it. There is no installer and nothing to unpack — it is a single self-contained
file, so no .NET runtime needs to be installed first.

Requires 64-bit Windows 10 or 11 with a Bluetooth radio. To have it start with Windows,
use `Settings… > Start with Windows` rather than moving the exe anywhere in particular;
keep it somewhere permanent, since that setting points at wherever the file currently is.

The exe is not code-signed, so SmartScreen will show "Windows protected your PC" on first
run — `More info > Run anyway`. If you would rather not take an unsigned binary on trust,
[build it yourself](#building-from-source); the release is built from the tagged commit by
the workflow in `.github/workflows/release.yml`.

## Using it

The app has no resident window. It adds a tray icon; right-click it for the device list,
`Settings…`, `Save diagnostics…`, or `Exit`. Hovering shows the lowest connected level
without opening anything. Only one instance runs at a time.

Starting the exe yourself opens the settings dialog, so a launch that only adds one more
icon to a crowded tray still says it worked. Starting it again while it is already running
does the same — the second process asks the first for the dialog and exits, rather than
disappearing without a word. Windows starting the app at login does neither: the Run entry
passes `--autostart`, and that launch goes straight to the tray.

### Settings

`Settings…` opens a small dialog, built on demand and disposed on close so the idle
footprint is unchanged (~10 MB).

| Setting | Notes |
|---|---|
| Start with Windows | Writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` with `--autostart`. No admin rights, no scheduled task. |
| Warn me when a device runs low | Balloon tip naming the device. Off does not mean "catch up later" — see below. |
| Alert at or below | 10 / 20 / 30% only. |
| Hide disconnected devices | Display only; a hidden device can still raise a low-battery alert. |
| Refresh every | 15 s – 5 min. How often battery *levels* are re-read; a scan costs ~10 ms, so this is not about cost. Devices appearing and disappearing no longer wait for it — see below. |

Everything except the startup entry is stored in `%APPDATA%\BattTray\settings.json`.
Autostart deliberately has no copy there: the registry is the single source of truth, so
removing the entry with another startup manager is reflected rather than fought.

The thresholds are restricted to multiples of 10 because some devices report battery in
coarse 10-step buckets rather than true percentages (the diagnostics tool below will tell
you which yours does). A 25% threshold would really mean "the 3rd bucket" and would
misrepresent its own precision.

An XInput controller is coarser still — four bands, no percentage — and the menu shows it
as `low` rather than as a number. The threshold you pick still decides when it warns: at
10% only `empty` alerts, at 20% or 30% both `empty` and `low` do. See
[XInput controllers](#how-xinput-controller-detection-works).

### Low-battery alerts

The rules are mostly about *not* firing:

- Each device latches after alerting, and only re-arms once it climbs 15 points clear of
  the threshold. Otherwise every poll produces another toast.
- Stale readings never alert. A disconnected headset still reporting a four-day-old 15%
  would otherwise warn forever.
- A latch survives disconnection, so unplugging a low device and plugging it back in is
  treated as one discharge rather than two.
- Turning alerts off still latches, so turning them back on does not replay the backlog.
- Several devices going low together produce one balloon, not a flicker of competing ones.

### Why the tray icon is fixed

The icon identifies the app and says nothing about charge. It is tempting to have it
track a battery level, but once several peripherals are tracked there is no single level
it could honestly show — a headset at 100% next to a mouse at 20% would make the icon
flip between readings that each look authoritative, without saying which device it meant.
Levels live in the tooltip and the menu, where they stay attached to a device name.

The only thing that varies is black versus white, which is contrast against the taskbar
rather than state. Both variants are embedded in the exe
(`Assets/batttray-black.ico` and `-white.ico`, 16-256px) and the app swaps them on
`SystemEvents.UserPreferenceChanged`, so it follows a theme change immediately rather than
at the next poll.

The icon Explorer shows for the exe is a third piece of artwork, and needs to be. The tray
can swap variants because it knows what the taskbar is doing; a downloaded file is drawn
once, on a background the exe never learns, so the bare black glyph that is right on a light
taskbar all but disappears in a dark Explorer window. `Assets/batttray-app.ico` therefore
carries its own background — a mid-blue tile with the mark knocked out in white — and is not
embedded, being the only one nothing in the app ever loads. Blue rather than green for the
same reason the tray icon shows no level: green on a battery reads as "charged", and the
file icon is saying which app this is. It is generated from the black glyph by
`tools/icons/Build-AppIcon.ps1` rather than drawn separately, so the mark on the exe cannot
drift from the mark in the tray.

The icon also survives an `explorer.exe` restart — verified rather than assumed, by
killing Explorer and re-reading the notification area. WinForms `NotifyIcon` handles the
`TaskbarCreated` broadcast itself; several other tray apps on the same machine did not
come back.

## How Bluetooth detection works

Windows splits the information across two places, so `BluetoothPeripheralProvider` joins
them on the radio address:

- **`bluetoothapis.dll`** (`BluetoothFindFirstDevice`) gives the clean product name, the
  class-of-device word, and a live `fConnected` flag — but no battery. The search is
  configured with `fIssueInquiry = false` so it never starts a radio scan.
- **`cfgmgr32.dll`** walks the `BTHENUM` / `BTHLE` / `BTHLEDevice` PnP enumerators and
  reads the battery percentage from device property `{104ea319-6ee2-4701-bd47-8ddbf425bbe5}`,
  PID 2 — the same one the Windows Settings app displays. PID 7 on that key holds a
  `FILETIME` of when the reading was last refreshed.

The battery property does not sit on the device you would expect. For a headset it lives
on the Hands-Free profile child node (`soundcore R60i NC Hands-Free AG`), which is why the
name comes from the Bluetooth API rather than from the PnP tree.

BattTray intentionally ignores devices that Bluetooth classifies as phones, including
iPhones and Android phones. They are not PC peripherals, and some phones rotate their LE
address on reconnect, which can otherwise leave duplicate battery nodes behind.

### Two behaviours worth knowing

**Readings survive disconnection.** Windows keeps the last known percentage after a device
goes away, so a value alone does not mean the device is live. Devices are marked
`IsStale` when they report a battery but are not connected, and the menu shows how long
ago the reading was taken.

**Charging state is not available over Bluetooth.** A full dump of every device property
on a battery-reporting node showed no charging flag — Bluetooth Classic simply does not
expose one. `ChargeState` is therefore tri-state (`Unknown` / `Discharging` / `Charging`)
and Bluetooth always reports `Unknown`, because "not charging" would be a guess. No other
source fills it in either — XInput looked as though it would and does not, for reasons worth
reading before trying again: [`WIRED` is not a cable](#wired-is-not-a-cable).

Correlating the two sources would be the way to put a charge state on a *Bluetooth* row,
and on the hardware here that is weaker than it sounds. A cable being attached is a real
charging signal — the catch is that the node which knows about the cable is not the node
which knows the percentage. Windows files a device's BLE and USB faces as separate
containers under different PIDs: this controller is container
`3718a527-31ac-5f1f-ac4b-b763b32cf562` at PID `301B` over BLE and
`fcb6d6dc-5fca-5a5b-846e-f04e82c61d38` at PID `310A` wired. Correlating the two is a
vendor-id-and-name heuristic rather than a lookup — the VID matches, the container id does
not, and the wired composite node calls itself only "USB Composite Device", with the product
string on its HID interfaces ("8BitDo Ultimate 2C Wireless Controller") not an exact match
for the BLE node's name ("8BitDo Ultimate 2C Wireless") either. The other route is a device
that declares charge in a HID report, which none attached here does — see
[`--probe-hid`](#probing-for-a-battery-in-hid-report-descriptors).

Worse than a loose match: the heuristic's premise is unsound as stated. The pad's 2.4 GHz
receiver enumerates under that same PID `310A`, so "a USB node with this VID is present" does
not mean a cable is attached — it means the pad is reachable over USB by *some* route, which
is the same wall [`WIRED` is not a cable](#wired-is-not-a-cable) runs into. The one thread
left is the USB serial: the receiver is `USB\VID_2DC8&PID_310A\991252A6A7`, and if a cabled
pad enumerates a different one, that distinguishes them — per device, by observation, not as
a rule that generalises to hardware nobody has held.

The model would have to move first, too. A merged row would want to read
`87% (stale) · charging` — a live device carrying an old number — and `Peripheral.IsStale`
is derived as `!IsConnected && BatteryPercent is not null`, so that state cannot be
expressed. Present-over-USB with a reading from the last BLE session is exactly the case the
derivation rules out, and both `DescribeDevice` and the low-battery check branch on
`IsConnected` before anything else. Unpicking staleness from connectedness in the record is
the first move, not the VID heuristic.

## How XInput controller detection works

`XInputGamepadProvider` asks `xinput1_4.dll` about its four controller slots. It is the
only non-Bluetooth battery source anything in this repo has found — both property sweeps
and the HID descriptor sweep came back empty — and it is deliberately narrow:

- **A USB-attached pad is listed with no reading at all.** `XInputGetBatteryInformation`
  returns `BATTERY_TYPE_WIRED` with `BATTERY_LEVEL_FULL` beside it. The level byte has to
  hold something and that something is not a reading, so taking it would put a confident
  100% in the tray for a device that never claimed one. The row reads
  `Gamepad 1 (XInput) — no battery reported · connected`: the controller is there, and its
  battery is not knowable from here. **`WIRED` does not mean a cable** — see below.
- **A pad on a radio reports one of four levels** — `EMPTY` / `LOW` / `MEDIUM` / `FULL` —
  and that is the whole scale, which is why such a row would read
  `Gamepad 1 (XInput) — low · connected` rather than showing a percentage. No hardware here
  has produced one; see below.

`Windows.Gaming.Input`'s `TryGetBatteryReport()` looks like the better source and is not.
Measured on the 8BitDo Ultimate 2C it returned `RemainingCapacityInMilliwattHours = 1000`
of `FullChargeCapacityInMilliwattHours = 1000` with `Status = Discharging`, while the pad
was plugged in and charging. Those milliwatt-hours are the same four-step byte scaled up,
so a WinRT dependency would buy a number that is no more accurate, reads as though it were,
and is wrong about the charge direction too.

### Bands, and why the model grew a field

`Peripheral.BatteryBand` is a provider saying "the percentage beside this is a stand-in".
Sorting and the low-battery threshold are numeric and always will be, so a four-level
source still has to supply a number — but nothing the user reads shows it. The levels map
to 5 / 20 / 60 / 100, spaced so each permitted threshold cuts *between* bands rather than
through one, and widely enough that climbing a band clears the notifier's 15-point re-arm
margin. (One exception: a pad that alerted at `empty` against a 10% threshold stays latched
through `low` and re-arms at `medium`.)

The 10-step scale some Bluetooth headsets report is not marked this way. It is coarse too,
but the device spells its buckets as percentages, so showing the number repeats what was
said instead of inventing two digits.

### `WIRED` is not a cable

`BATTERY_TYPE_WIRED` reads like a positive signal that a cable is attached, and spending it
on `ChargeState.Charging` is the obvious way to make that field mean something. It does not
survive contact with the hardware.

On the 8BitDo Ultimate 2C with the **cable physically out**, connected through its own 2.4
GHz receiver, slot 0 still answers `BATTERY_TYPE_WIRED` — with the same `VID_2DC8`, the same
`PID_310A` and the same `&IG_00` interface the cable produced, because the receiver is itself
a bus-powered USB device. Nothing in XInput's answer separates a pad on a cable from a pad on
a dongle. A row saying either would be wrong half the time, so the byte is read as the only
thing it supports — *XInput has no battery for this device* — and the peripheral carries no
reading and `ChargeState.Unknown`.

Two consequences follow, and both are about this hardware rather than about the approach.

**Nothing sets `Charging`.** Bluetooth cannot (no charging flag exists on the property), HID
cannot ([no descriptor here declares one](#probing-for-a-battery-in-hid-report-descriptors)),
and XInput's one candidate byte turned out to mean something else. The rendering in
`DescribeDevice` and the latch release in `LowBatteryNotifier` are both kept, and both are
unreached — they are the shape a charge source would plug into, not behaviour the app has
performed. The latch release carries a note about ordering: a source reporting charge without
a percentage needs its test moved above the percentage guard.

**The banded path has never run either.** Every occupied slot observed on the development
machine, wired and on the receiver, reported `WIRED`. `ChargeState.Discharging`, the four
band names and the 5/20/60/100 stand-ins are supported by `XInput.h` and by nothing that has
been plugged in — a pad whose XInput answer names a battery type would exercise them, and
none here does.

### What it costs

A whole four-slot sweep measures ~0.45 ms, so this provider needs no cache and no
device-change handling of its own. The distribution is the surprise: an occupied slot answers
in ~0.005 ms and an **empty** one takes ~0.155 ms, thirty times longer, which makes three
empty slots about 97% of the cost — the poll gets cheaper as controllers are connected. That
is the old "never poll disconnected controllers" problem, still measurable and no longer
expensive.

The first call costs ~13 ms, which is loading `xinput1_4.dll`. It falls in the first refresh
during construction, before the message loop starts, so it is startup cost rather than a
stutter in the tray.

### Two limits worth knowing

**A slot is not an identity.** XInput exposes an index from 0 to 3 with no name, no VID/PID
and no serial, and a pad that reconnects can land in a different slot. So the rows are named
for the slot and say so — the number matches the player light on the controller — and the
low-battery latch belongs to the slot rather than to the pad. Swap a low controller for
another low controller in the same slot and the second inherits the first one's warning;
the alternative is an alert that re-fires on every reconnect.

**An Xbox pad on Bluetooth can appear twice**, once under its product name from the
Bluetooth provider and once as a slot here, because such a pad reaches XInput *and*
publishes a battery to the PnP tree. Nothing XInput exposes could correlate the two, so the
duplicate is left legible rather than guessed away: one row carries a real name, the other
is plainly a slot. For the same reason a row with a reading is reported as `Dongle` — XInput
never says which radio it is talking over, but every reading only reachable here arrives over
one. The wired row is the exception and needs no guess: it is `Usb`, `WIRED` being the one
attachment XInput names outright.

## Diagnostics

### From the exe: `Save diagnostics…`

Right-click the tray icon and choose `Save diagnostics…`. It writes one file to `%TEMP%` and
opens Explorer with the file selected, ready to be dragged into an issue. No SDK, no clone,
no console — which is the entire point: the hardware this project cannot buy belongs to
people who downloaded a single exe, and a request that starts "install the .NET SDK" is a
request not to answer.

The file carries all three dumps described below — the providers' raw evidence, the device
property sweep and the HID sweep — under a header naming the build, the Windows version and
the moment it was taken. A pasted dump with no build number is a bug report about an unknown
binary. Expect around 1.5 MB of it, which is why it says to attach the file rather than paste
its contents.

There is a command-line form too, for anyone collecting dumps across several machines:

```bash
BattTray.exe --diagnostics           # to %TEMP%, then revealed in Explorer
BattTray.exe --diagnostics dump.txt  # to that path, no window; the exit code is the answer
```

The flag is handled before the single-instance check, because none of the three dumps needs
the tray: the providers are constructed on the spot and the probes talk to Windows rather
than to the app. So a dump is neither refused because a copy is already running — which is
exactly when you want one — nor does it raise a second tray icon on its way out. A named path
suppresses the Explorer window, on the grounds that a script asking for a specific file does
not need to be shown where it went.

### From a clone: the accuracy harness

`tools/BattTray.Diagnostics` is an accuracy harness. It drives the real providers through
the real `IPeripheralProvider` seam rather than reimplementing them — a harness that
duplicates what it is checking can only ever confirm itself. The two probes below and the
evidence dump live in `BattTray/Diagnostics` for the same reason, so the harness and the
menu row run the same code rather than two copies of it; what the harness adds is watch
mode, `--log`, and `--all`.

```bash
dotnet run --project tools/BattTray.Diagnostics -- --once
```

It prints the raw evidence behind every reading: the property key, the bytes as reported,
and what this app decoded them into. A percentage that disagrees with the vendor app is
then either a decoding bug (bytes fine, value wrong) or the device lying (both agree), and
only the raw form separates those.

Without `--once` it keeps watching, logging every change with a wall-clock time, and on
`Ctrl+C` reports per device whether the values seen look like true 0-100 granularity or
the coarse 10-step scale — which is what decides whether a number should be read as a
value or as a band. Where a provider already knows its source is coarse it says so per
reading, and that answer is reported rather than inferred: XInput's four levels stand in as
5/20/60/100, which the multiples-of-10 test would otherwise misread as full granularity.

```bash
dotnet run --project tools/BattTray.Diagnostics -- --interval 30 --log drain.txt
```

Any new transport is covered the moment its provider implements `GetDiagnostics()`, which
has an empty default so a provider can be prototyped without one.

### Probing for a transport nobody has written a provider for

`--probe` asks a different question: not "is this reading right?" but "does Windows already
know a battery percentage for devices BattTray does not cover yet?". It sweeps every present
PnP node — all enumerators, not just the Bluetooth ones — asks each what property keys it
publishes, and sorts anything battery-shaped into three tiers: keys under the battery format
GUID, `DEVPROP_TYPE_BYTE` values in 0-100, and integers in 1-100 on peripheral-looking nodes.

```bash
dotnet run --project tools/BattTray.Diagnostics -- --probe --log probe.txt
```

Run it before writing a provider. On the development machine (216 present nodes, 12,701
properties, 234 distinct keys, ~1.8 s) the answer was **no**: every battery property found
was under a `BTH*` enumerator, already covered. The decisive case was one device present on
two transports at once — an 8BitDo Ultimate 2C, paired over BLE and plugged in by cable at
the same moment. Over BLE it is `BTHLE\Dev_e417d8248eb3`, with `BTHLEDevice` children under
`VID&022dc8_PID&301b`, and it reports 87%. Over the cable the same controller enumerates as
`USB\VID_2DC8&PID_310A`, a composite device with three interfaces — `MI_00` an XInput pad,
`MI_01` carrying keyboard, consumer-control and mouse collections, `MI_02` vendor-defined —
and not one of those nodes carries a battery key or a byte in 0-100. The PID is a function
of the connection mode rather than of the device, which is why the mode is worth naming
beside the id: `301B` over BLE, `310A` wired.

Device *interfaces* are swept the same way and reported separately, because a node and its
interfaces are separate property stores rather than two views of one: a node carries what
the PnP tree knows about a device, an interface what a driver chose to publish alongside the
handle it hands out. That sweep (286 interfaces, 2,199 properties, ~0.2 s) closed the gap
the node sweep left. No interface publishes any key under the battery format GUID at all,
and the only percentage-shaped bytes in it were Bluetooth GATT plumbing and a storage
volume. Add `--all` to dump every subject rather than the peripheral-looking ones.

Between them the two sweeps cover everything Windows publishes as a *property* about a
device, so this is "not there" rather than "not found yet": a battery Windows demonstrably
knows about over one transport is absent over the other, and a USB provider therefore cannot
be a copy of the Bluetooth one. The percentage will have to come from HID reports — and from
any of three usage pages, not only the documented `0x85` (Battery System): `0x06` usage
`0x20` (Battery Strength) is what many modern gamepads and BLE-derived HID devices actually
use, and `0x84` (Power Device) is a third possibility. `--probe-hid` below checks all three.

### Probing for a battery in HID report descriptors

`--probe-hid` covers the one place `--probe` cannot reach. A HID battery is not a device
property but a usage inside the report descriptor, invisible to the PnP tree that publishes
everything else about the device. With a property-based USB provider ruled out, this is what
says whether the remaining route — reading HID reports — has anything to read.

```bash
dotnet run --project tools/BattTray.Diagnostics -- --probe-hid --log hid.txt
```

It opens every `GUID_DEVINTERFACE_HID` interface with `dwDesiredAccess = 0` — enough for the
descriptor, and the only access many devices will grant, since plenty are held exclusively by
their own software — then prints per interface the product string, VID/PID/version, top-level
usage, the three report lengths, and every value and button cap with its report id, usage,
bit size, logical and physical range and link collection. Interfaces that refuse to open are
reported with their `GetLastError` rather than skipped: unopened is a different answer from
no battery.

Anything on any of the three pages above is flagged, and the dump's own header names them, so
a reader has the list in front of them rather than in the previous section. Where a *feature*
report carries a flagged usage it is read back on the spot, with the raw bytes printed next
to the decoded value; a device may only refresh a feature report when polled, so read it
twice before trusting a fixed number, and check the logical range before reading a value as
a percentage.

On the development machine the answer is **no**: 15 to 20 HID interfaces depending on what is
awake, every one of them opened, and not one declaring a battery usage on any of the three
pages. That is a fact about what is attached, not about the approach, which is the reason the
flag exists — the day a mouse, headset or controller that does report over HID is plugged in,
the answer is one command away.

The sweep costs ~165 ms for the 20 interfaces above, nearly all of it in opening handles and
parsing descriptors rather than in enumeration (~5 ms) — the cost tracks how many devices are
attached, so it is the ratio rather than either figure that carries. That is fine on demand
and far outside the single-digit milliseconds `IPeripheralProvider` is polled against,
which is why this lives in the
diagnostics tool and why a HID provider will need a cheaper shape than a full sweep. That
shape is a cache invalidated by device-change events rather than rebuilt per poll — see
[Design notes](#device-change-notifications-and-the-poll-behind-them), where the seam for
it already exists.

## Design notes

`cfgmgr32` was chosen over the WinRT `DeviceInformation` APIs after measuring both on real
hardware: the WinRT Bluetooth endpoint query took ~30 s (it triggers radio work), while the
PnP sweep takes ~2 ms. It also avoids pinning a Windows SDK version in the build.

Adding a transport means implementing `IPeripheralProvider` and registering it in
`TrayApplicationContext`; the model, sorting, tray icon, menu, notifications and
diagnostics are already transport-agnostic.

### Device-change notifications, and the poll behind them

Refresh is driven by two things: `CM_Register_Notification` for arrival and removal, and
the timer for everything else. The split follows what each one can actually observe —
**a battery level changing is not a device change and never arrives as an event**, so the
poll is what moves a percentage and the interval setting still means what it says.

The events were added for the transport that does not exist yet. Enumerating HID
interfaces costs ~5 ms, but opening all 20 handles and parsing their capabilities costs
~160 ms (measured by `--probe-hid`, above), and `IPeripheralProvider` is polled from the
UI thread against a single-digit-millisecond budget — so a HID provider cannot re-read
everything per poll, and the only safe way to hold the result between polls is to know
when it went out of date. `InvalidateDeviceCache()` on the provider interface is that
signal.

Two honest caveats about what that bought the transport already shipped. The **cache** is
close to pointless here: a full refresh measures ~10 ms in a Release build on the
development machine, of which re-listing the device ids is only ~1 ms — the rest is the
pairing-record call and the property reads, neither of which a device-change event can
authorise caching. (The ~2 ms quoted above is the `cfgmgr32` sweep alone, not a whole
scan.) The Bluetooth provider caches because the seam should have a user that
exercises it, not because 1 ms mattered. The **events** are the part that earns its keep,
and even then not in the menu — the menu was never the stale part, since it rescans as it
opens. It is the hover tooltip and the low-battery check that were stale, and both now
follow a device appearing or disappearing within ~0.4 s instead of waiting out the poll.

`CM_Register_Notification` was chosen over `WM_DEVICECHANGE` because the window message
needs a window to be delivered to, and this app has none — see "Why the tray icon is
fixed"; a hidden window created purely to receive broadcasts would be the only window it
owns.

Four things about the shape it took are non-obvious:

- **Both filters are registered as broadly as they go** — every interface class, every
  device instance — which is the opposite of what the per-class filter exists for. The
  events authorise a cache, and a filter narrow enough to miss one arrival leaves that
  cache wrong until the device goes away again. Breadth costs nothing to sit on: an idle
  machine has no device changes to report, whatever the filter says, and a burst from a
  busy one collapses into a single scan below.
- **A burst is coalesced over 400 ms.** One headset connecting starts several profile
  nodes and publishes several interfaces, each reported separately. The timer is started,
  never restarted: a device that kept reporting itself for longer than the interval would
  otherwise push a restarting timer back indefinitely.
- **Callbacks arrive on threadpool threads** and everything downstream is UI-thread bound,
  so they are posted through the synchronization context, and `Refresh` holds a re-entrancy
  guard — an open menu and the settings dialog pump messages of their own, so a tick can
  land part-way through a scan.
- **A failed registration is not an error.** `DeviceChangeWatcher.TryStart` returns null,
  `PeripheralMonitor.DeviceChangesAreWatched` stays false, and every poll then invalidates
  the caches itself — which is exactly what the providers did before any of this existed.
  The registration is also all-or-nothing, since half of it would claim a coverage it does
  not have. The same default is why the diagnostics harness, which never registers
  anything, still re-enumerates on every scan.

What arrives promptly is *presence*, not percentage. A device's battery property often
appears seconds after its nodes start, so the value behind a freshly connected device is
still the poll's to fill in; no settle interval could fix that.

## Building from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Windows.
There are no package dependencies — it is direct P/Invoke into `bluetoothapis.dll`,
`cfgmgr32.dll` and `xinput1_4.dll`, all of which ship with Windows.

```bash
dotnet run --project BattTray
```

To produce the same single-file exe the releases ship:

```bash
dotnet publish BattTray/BattTray.csproj -p:PublishProfile=win-x64
```

The result lands in `BattTray/bin/publish/win-x64/`. Those settings live in a publish
profile rather than the csproj because `SelfContained` applies to the whole build graph:
set in the csproj it breaks `dotnet run` and stops the diagnostics harness from
referencing the app.

### Tests

```bash
dotnet test tests/BattTray.Tests/BattTray.Tests.csproj
```

147 tests, about a tenth of a second, and none of them touches the machine they run on: no
registry, no radio, no XInput, no tray icon. That is the line the suite is drawn along
rather than a coincidence. Everything that talks to Windows is P/Invoke whose failure modes
are the operating system's, and mocking it would only assert that the mocks were written the
way the code expects — which is why the hardware-facing half is covered by the diagnostics
harness below, against real devices, instead.

What is left is pure and worth pinning: the low-battery latching rules, settings clamping,
the Run-key command parser, class-of-device categorisation, radio addresses in device
instance ids, the strings the menu rows are built from, and everything around the
diagnostics dump except the two sweeps themselves — its header, where the file goes, and
how the flag is read. Most of it had only ever been
verified by running the app and watching — which for the latching rules means waiting for a
headset to discharge, and for the reading-age boundaries means waiting a day.

The tests run on every push and, in the release workflow, between the build and the publish,
so a tag cannot produce a binary the suite has not passed.

## Uninstalling

Delete the exe. Two things live outside it: `%APPDATA%\BattTray\settings.json`, and the
autostart entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` if it was ever
switched on. Turning `Start with Windows` off before deleting removes the second.

## Contributing

Pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). Readings from hardware
I do not own are the most useful thing to send, and cost one menu row:
[`Save diagnostics…`](#from-the-exe-save-diagnostics) writes a file and opens Explorer on it.
The raw bytes in it are what separate a decoding bug here from a device reporting something
strange. Wired USB is unimplemented, and 2.4 GHz reaches only XInput controllers, so both are
the clearest place to start on code.

## License

[MIT](LICENSE)
