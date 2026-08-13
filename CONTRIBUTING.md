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

Requires the .NET 10 SDK and Windows. There are no external dependencies — it is direct
P/Invoke into `bluetoothapis.dll`, `cfgmgr32.dll` and `xinput1_4.dll`.

## Tests

```bash
dotnet test tests/BattTray.Tests/BattTray.Tests.csproj
```

They run on every push, and again before a tag can produce a binary.

Nothing in the suite touches the machine it runs on, and nothing is mocked. Those are the
same decision: the interop layer's failure modes belong to Windows, so a mock of it would
only assert that the mock was written the way the code expects. Evidence about hardware
comes from the diagnostics harness instead, which is why that tool exists and why a new
provider is asked for a `GetDiagnostics()` before it is trusted.

So the suite covers the rules rather than the plumbing — latching, clamping, parsing,
categorising, and the strings the menu is built from. If you add a rule with an edge case
you had to think about, it belongs here; that is most of what is already in it. If you find
yourself wanting to mock `ConfigManager` to get at one, the rule probably wants lifting out
into something pure first, which is what `LowBatteryNotifier`'s injected alert did for the
notification rules.

One thing worth copying rather than the tests themselves: each was checked by breaking the
code it guards and confirming it failed. A test that has only ever passed has not been shown
to test anything.

## Adding a transport

Wired USB is unimplemented, and 2.4 GHz reaches only XInput controllers, so both are the
clearest place to start. Implement `IPeripheralProvider` and register it in
`TrayApplicationContext`; the model, sorting, tray icon, menu, notifications and diagnostics
are already transport-agnostic, so a provider is the only new code. `GetDiagnostics()` has an
empty default, so a provider can be prototyped before it reports anything to the harness.

If your source reports a handful of steps rather than a percentage, set
`Peripheral.BatteryBand` as well. That is a promise that the percentage beside it is a
stand-in for sorting and the alert threshold, and it is what stops the menu, the tooltip and
the balloon rendering four levels as a precise-looking number. Choose the stand-ins so the
10/20/30 thresholds fall between your bands rather than inside one, and so climbing a band
clears `LowBatteryNotifier`'s 15-point re-arm margin; `XInputGamepadProvider` works an
example through, and `XInputBandTests` states those two constraints as properties you can
point at your own numbers.

`InvalidateDeviceCache()` has an empty default too, and most providers should leave it
that way. Implement it only if enumerating is genuinely expensive — a HID provider has to
open handles and parse report descriptors, which is fifty times the cost of listing the
interfaces — in which case hold the result and drop it here. Whatever you hold has to be
dropped here and nowhere else: the monitor calls this on every poll when device-change
notifications could not be registered, so a cache that answers to anything other than this
method goes stale in exactly the case that fallback exists for.

## Style

Match the surrounding code. Comments here explain *why* a non-obvious choice was made
rather than restating what the line does — several of the decisions in this codebase look
arbitrary until you know what was measured, so please keep that in the code rather than
only in the PR description.
