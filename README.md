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

The app has no window. It adds a tray icon; right-click it for the device list, or `Exit`
to quit. Hovering shows the lowest connected level without opening anything. Only one
instance runs at a time.

### Why the tray icon is fixed

The icon identifies the app and says nothing about charge. It is tempting to have it
track a battery level, but once several peripherals are tracked there is no single level
it could honestly show — a headset at 100% next to a mouse at 20% would make the icon
flip between readings that each look authoritative, without saying which device it meant.
Levels live in the tooltip and the menu, where they stay attached to a device name.

The only thing that varies is black versus white, which is contrast against the taskbar
rather than state. Both variants are embedded in the exe (`Assets/*.ico`, 16-256px) and
the app re-checks the system light/dark setting on each poll, so it follows a theme change
within a minute.

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

## Design notes

`cfgmgr32` was chosen over the WinRT `DeviceInformation` APIs after measuring both on real
hardware: the WinRT Bluetooth endpoint query took ~30 s (it triggers radio work), while the
PnP sweep takes ~2 ms. It also avoids pinning a Windows SDK version in the build.

Adding a transport means implementing `IPeripheralProvider` and registering it in
`TrayApplicationContext`; the model, sorting, tray icon, and menu are already
transport-agnostic.
