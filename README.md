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

## Running it

```bash
dotnet run --project BattTray
```

The app has no resident window. It adds a tray icon; right-click it for the device list,
`Settings…`, or `Exit`. Hovering shows the lowest connected level without opening
anything. Only one instance runs at a time.

### Settings

`Settings…` opens a small dialog, built on demand and disposed on close so the idle
footprint is unchanged (~10 MB).

| Setting | Notes |
|---|---|
| Start with Windows | Writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. No admin rights, no scheduled task. |
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
