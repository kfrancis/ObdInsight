# ObdInsight.Transports.WindowsBle

Windows BLE transport for [ObdInsight](https://github.com/kfrancis/ObdInsight): an
`IConnectionAwareTransport` and an `IBleScanner` built directly on WinRT
(`Windows.Devices.Bluetooth`), for ELM327-family BLE OBD-II adapters.

Sibling of `ObdInsight.Transports.Ble`, which covers Android/iOS through Plugin.BLE.
This package exists separately because WinRT GATT needs Windows-specific connection and
CCCD-write retry handling that the cross-platform stack does not model.

## Usage

Scan for adapters, then open a transport against the one you want:

```csharp
using var scanner = new BleScanner();
scanner.DeviceDiscovered += (_, e) => Console.WriteLine($"{e.Device.Name} {e.Device.Address}");
await scanner.StartScanAsync(ct: ct);
// ...pick a device, then:
await scanner.StopScanAsync(ct);

await using var transport = new BleElmTransport(device.Address, logger);
await transport.OpenAsync(ct);

var session = new ElmSession(new ElmFramer(transport));
await session.InitializeAndLockAsync(ct);
```

`StartScanAsync` takes an optional `BleScanFilter` to narrow discovery by advertised
service UUID or device name. `logger` is an optional `ILogger<BleElmTransport>`; omit it
and the transport logs nothing.

## Notes

- Quiet reads wait until bytes, cancellation, or link loss; they do not return false EOF.
  Physical disconnect raises `ConnectionLost` once. Use a fresh transport after loss or
  disposal, normally through `VehicleConnection` in `ObdInsight.Telemetry`.
- The repository's [stationary smoke guide](../../docs/HARDWARE_SMOKE_TEST.md) exercises
  owner-managed recovery and snapshots without the legacy console orchestration.

- Targets `net10.0-windows10.0.19041.0`; minimum supported OS is Windows 10 1809
  (`10.0.17763.0`).
- The GATT profile is the common serial service `FFF0` with `FFF1` (notify) and `FFF2`
  (write) — Veepeak-class adapters. Adapters using other profiles (Vgate `FFE0/FFE1`,
  Nordic UART) are handled by `ObdInsight.Transports.Ble`.
- `BleElmTransport` retries the CCCD write, because WinRT frequently reports a GATT
  session as ready slightly before notifications can actually be enabled.
