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
| Refresh every | 15 s – 5 min. A scan costs ~2 ms, so this is about connect/disconnect lag, not cost. |

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

Add `--all` to dump every node rather than the peripheral-looking ones. Device *interface*
properties are not swept — that needs SetupAPI and a class GUID per interface, and is the
next place to look if a node dump comes back empty.

## Design notes

`cfgmgr32` was chosen over the WinRT `DeviceInformation` APIs after measuring both on real
hardware: the WinRT Bluetooth endpoint query took ~30 s (it triggers radio work), while the
PnP sweep takes ~2 ms. It also avoids pinning a Windows SDK version in the build.

Adding a transport means implementing `IPeripheralProvider` and registering it in
`TrayApplicationContext`; the model, sorting, tray icon, menu, notifications and
diagnostics are already transport-agnostic.

Refresh is a plain timer rather than `WM_DEVICECHANGE` registration. At ~2 ms per scan the
poll is not a cost problem, so the interval was exposed as a setting instead; event-driven
refresh remains the way to make connect/disconnect instant without polling faster.

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
