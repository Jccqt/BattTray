# BattTray

A Windows tray app that shows the battery level of your PC peripherals.

Goal: one place to see the charge of every peripheral, whether it is attached over
Bluetooth, wired USB, or a 2.4 GHz dongle.

## Status

| Transport | State |
|---|---|
| Bluetooth | Implemented |
| Wired USB | Not started |
| 2.4 GHz dongle | Not started |

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
`Settings…`, or `Exit`. Hovering shows the lowest connected level without opening
anything. Only one instance runs at a time.

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
rather than state. Both variants are embedded in the exe (`Assets/*.ico`, 16-256px) and
the app swaps them on `SystemEvents.UserPreferenceChanged`, so it follows a theme change
immediately rather than at the next poll.

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
and Bluetooth always reports `Unknown`, because "not charging" would be a guess. Wired USB
HID devices do report charging, so the field will earn its keep once that provider lands.

## Diagnostics

`tools/BattTray.Diagnostics` is an accuracy harness. It drives the real providers through
the real `IPeripheralProvider` seam rather than reimplementing them — a harness that
duplicates what it is checking can only ever confirm itself.

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
value or as a band.

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

Run it before writing a provider. On the development machine (201 present nodes, 11,851
properties, ~1.1 s) the answer was **no**: every battery property found was under a `BTH*`
enumerator, already covered. The decisive case was one device present on two transports at
once — an 8BitDo Ultimate 2C, which reports 87% on its `BTHLE` node, has neither a battery
key nor any byte in 0-100 on the `USB\VID_2DC8&PID_301C` and `HID\VID_2DC8&PID_301C` nodes
it publishes in its non-Bluetooth mode. A battery Windows demonstrably knows about over one
transport is absent over the other, so a USB provider cannot be a copy of the Bluetooth one:
the percentage will have to come from HID reports (usage page 0x85), not a device property.

Add `--all` to dump every node rather than the peripheral-looking ones. Device *interfaces*
are swept the same way and reported separately, because a node and its interfaces are
separate property stores rather than two views of one — and that sweep found nothing
outside the Bluetooth enumerators either.

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

Anything on usage page `0x85` (Battery System), `0x84` (Power Device) or `0x06` usage `0x20`
(Battery Strength) is flagged. All three are checked deliberately — the third is the one
modern gamepads and BLE-derived HID devices tend to use, and looking only at `0x85` would
miss them. Where a *feature* report carries a flagged usage it is read back on the spot, with
the raw bytes printed next to the decoded value; a device may only refresh a feature report
when polled, so read it twice before trusting a fixed number, and check the logical range
before reading a value as a percentage.

On the development machine the answer is **no**: 15 to 20 HID interfaces depending on what is
awake, every one of them opened, and not one declaring a battery usage on any of the three
pages. That is a fact about what is attached, not about the approach, which is the reason the
flag exists — the day a mouse, headset or controller that does report over HID is plugged in,
the answer is one command away.

The sweep costs ~105 ms, nearly all of it in opening handles and parsing descriptors rather
than in enumeration (~2 ms). That is fine on demand and far outside the single-digit
milliseconds `IPeripheralProvider` is polled against, which is why this lives in the
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
interfaces costs ~2 ms, but opening all 15 handles and parsing their capabilities costs
~105 ms (measured by `--probe-hid`, above), and `IPeripheralProvider` is polled from the
UI thread against a single-digit-millisecond budget — so a HID provider cannot re-read
everything per poll, and the only safe way to hold the result between polls is to know
when it went out of date. `InvalidateDeviceCache()` on the provider interface is that
signal.

Two honest caveats about what that bought the transport already shipped. The **cache** is
close to pointless here: a full refresh measures ~10 ms in a Release build on the
development machine, of which re-listing the device ids is only ~1 ms — the rest is the
pairing-record call and the property reads, neither of which a device-change event can
authorise caching. (The ~2 ms quoted above and below is the `cfgmgr32` sweep alone, not a
whole scan.) The Bluetooth provider caches because the seam should have a user that
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
There are no package dependencies — the Bluetooth work is direct P/Invoke into
`bluetoothapis.dll` and `cfgmgr32.dll`.

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

## Uninstalling

Delete the exe. Two things live outside it: `%APPDATA%\BattTray\settings.json`, and the
autostart entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` if it was ever
switched on. Turning `Start with Windows` off before deleting removes the second.

## Contributing

Pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). Readings from hardware
I do not own are the most useful thing to send: run the diagnostics harness and paste its
output, since the raw bytes are what separate a decoding bug here from a device reporting
something strange. Wired USB and 2.4 GHz support are unimplemented and are the clearest
place to start on code.

## License

[MIT](LICENSE)
