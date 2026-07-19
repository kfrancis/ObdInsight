# Cross-platform BLE Transport Design (roadmap B9)

**Status:** Draft for review; implementation proceeding per Phase 2 (flag objections —
the surface can still move).
**Date:** 2026-07-19

## 1. Problem

All three BLE stacks in the repo are WinRT-only (`Windows.Devices.Bluetooth`) and
hardcode the FFF0/FFF1/FFF2 GATT profile. EvTestDrive ships on Android + iOS with the
Vgate iCar Pro as reference adapter — which commonly exposes the **FFE0 service with a
single FFE1 characteristic doing both write and notify**, a profile the repo cannot
speak at all. Two of the WinRT files also masquerade as `ObdInsight.Core.*` namespaces
from inside the console app (audit A5/M3.3).

## 2. Goals / non-goals

**Goals**

1. `IElmTransport` implementation on [Plugin.BLE](https://github.com/dotnet-bluetooth-le/dotnet-bluetooth-le)
   compiling for `net9.0-android` + `net9.0-ios` (+ plain `net9.0` for reference).
2. GATT profile table + auto-probe: FFE0/FFE1 single-characteristic (Vgate iCar Pro),
   FFF0/FFF1/FFF2 (Veepeak), Nordic UART — pick by service/characteristic discovery,
   allow forcing a profile.
3. Profile-selection logic unit-testable with zero BLE dependencies.
4. Connection-state signal for B10 to consume.
5. Namespace-masquerade cleanup: WinRT transports stop claiming `ObdInsight.Core.*`.

**Non-goals**

- Reconnect/retry policy — B10 layers that on top.
- WiFi/serial ELM transports (roadmap breadth, later).
- Replacing the WinRT transports for the Windows console app (they work; they just get
  honest namespaces).

## 3. Shape

New project `src/ObdInsight.Transports.Ble`:
`TargetFrameworks: net9.0;net9.0-android;net9.0-ios`, references Core + Plugin.BLE.

```
BleAdapterProfile          record: service/write/notify UUIDs, write mode, chunk size
  └─ KnownProfiles         ordered table: VgateICarPro, VeepeakFff0, NordicUart
BleProfileResolver         PURE function: discovered GATT topology → best profile
  (input = plain records: no Plugin.BLE types → unit-testable everywhere)
PluginBleElmTransport      IElmTransport over Plugin.BLE IAdapter/IDevice:
  OpenAsync: connect → discover → resolve profile → subscribe notify
  WriteAsync: chunk by profile.MaxWriteSize (BLE ≤20 bytes default MTU)
  ReadAsync: notification-fed byte queue (no busy-poll)
  ConnectionStateChanged   event fed by Plugin.BLE DeviceDisconnected/ConnectionLost
```

### Profile auto-probe rules (BleProfileResolver)

1. Exact known-profile match: service UUID present AND its write/notify characteristics
   present with the right capabilities → highest-priority match wins (table order:
   Vgate FFE0 first — it's the EvTestDrive reference; FFF0 second; Nordic UART third).
2. Single-characteristic fallback within a known service: if the service is known but
   only one characteristic exists with both write+notify, use it for both roles
   (Vgate clones vary here).
3. Generic fallback: any service with a (write, notify) characteristic pair — last
   resort, logged.
4. Nothing usable → null; the transport surfaces a clean failure ("no compatible OBD
   GATT profile"), not a crash.

UUIDs are compared through 16-bit short-form expansion (`0000xxxx-0000-1000-8000-00805f9b34fb`),
so adapters advertising short or long forms both match.

### Plugin.BLE choice

Battle-tested, MIT, supports Android/iOS/macOS/Windows on .NET 8/9. Interfaces
(`IAdapter`, `IDevice`, `ICharacteristic`) allow later test doubles, but the
unit-testing seam here is the pure resolver — the thin adapter wrapper is
hardware-verified instead (flagged pending, per working rule 4).

### WinRT namespace cleanup (folded in)

- `src/ObdInsight/Core/Communication/Elm327/BleElmTransport.cs` →
  `src/ObdInsight/Transports/BleElmTransport.cs`, namespace `ObdInsight.Transports.WindowsBle`.
- `src/ObdInsight/Core/Communication/Bluetooth/BleScanner.cs` → same treatment.
- Consumers updated (Program.cs, DevTools compat, integration fixture). Full
  console/DevTools BLE-stack consolidation stays M3.3 — this only ends the namespace lie.

## 4. Test plan

- `BleProfileResolverTests` (pure, replay-free): exact Vgate FFE0/FFE1 topology;
  Veepeak FFF0 triple; Nordic UART; short-vs-long UUID forms; single-char fallback;
  generic pair fallback; empty topology → null; priority when multiple known profiles
  present.
- Chunking: write payloads split at MaxWriteSize boundaries (pure helper test).
- `PluginBleElmTransport` against a live iCar Pro: **hardware check pending** — the
  wrapper is deliberately thin so the untested surface is minimal.

## 5. Consequences

- EvTestDrive registers `PluginBleElmTransport` in MauiProgram; desktop keeps WinRT.
- B10's supervisor consumes `ConnectionStateChanged` without caring which transport
  produced it.
- DevTools' `BleDeviceProfile.OBDLink` had `Guid.Parse("fff0")` — an invalid GUID that
  would throw a `TypeInitializationException` on first touch of the profile table;
  fixed in passing to the full 128-bit form.
