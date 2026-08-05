# Contributing

Pull requests are welcome. The most useful contributions are the ones this app cannot
get any other way: readings from hardware I do not own.

## Reporting a wrong or missing battery level

Run the diagnostics harness and paste its output into the issue:

```bash
dotnet run --project tools/BattTray.Diagnostics -- --once
```

It prints the raw property bytes alongside the decoded value, which is what separates a
decoding bug in this app from a device reporting something odd. Include the device name
and, if you have one, what the vendor's own app shows for the same device.

## Building

```bash
dotnet build BattTray.slnx
dotnet run --project BattTray
```

Requires the .NET 10 SDK and Windows. There are no external dependencies — the Bluetooth
work is direct P/Invoke into `bluetoothapis.dll` and `cfgmgr32.dll`.

## Adding a transport

Wired USB and 2.4 GHz dongles are unimplemented and are the clearest place to start.
Implement `IPeripheralProvider` and register it in `TrayApplicationContext`; the model,
sorting, tray icon, menu, notifications and diagnostics are already transport-agnostic, so
a provider is the only new code. `GetDiagnostics()` has an empty default, so a provider can
be prototyped before it reports anything to the harness.

## Style

Match the surrounding code. Comments here explain *why* a non-obvious choice was made
rather than restating what the line does — several of the decisions in this codebase look
arbitrary until you know what was measured, so please keep that in the code rather than
only in the PR description.
