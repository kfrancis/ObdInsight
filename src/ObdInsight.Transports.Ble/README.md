# ObdInsight.Transports.Ble

Cross-platform BLE ELM327 transport for ObdInsight on
[Plugin.BLE](https://github.com/dotnet-bluetooth-le/dotnet-bluetooth-le)
(`net10.0-android` / `net10.0-ios`, plus a plain `net10.0` reference target):

- **GATT profile auto-probe** — Vgate iCar Pro (FFE0 with a single dual-role FFE1
  characteristic), Veepeak (FFF0/FFF1/FFF2), Nordic UART, with single-characteristic
  and generic write/notify fallbacks for clone variance; 16-bit and 128-bit UUID
  forms both match. Force a profile when you know better.
- **Notification-fed reads** (no busy-polling) and MTU-sized write chunking.
- **`ConnectionLost` signal** — use `VehicleConnection` from
  `ObdInsight.Telemetry` to rebuild and initialize a fresh diagnostic generation after loss.

```csharp
await using var connection = new VehicleConnection(
    () => new PluginBleElmTransport(CrossBluetoothLE.Current.Adapter, bleDeviceId),
    [new NissanLeaf()]);
var generation = await connection.OpenAsync(ct);
```

Android needs `BLUETOOTH_SCAN`/`BLUETOOTH_CONNECT` runtime permissions; iOS needs
`NSBluetoothAlwaysUsageDescription` (and uses per-install device UUIDs — persist the
scanned ID, don't hardcode).

Docs: [transport design](https://github.com/kfrancis/ObdInsight/blob/main/docs/BLE_TRANSPORT_DESIGN.md) ·
[MAUI integration](https://github.com/kfrancis/ObdInsight/blob/main/docs/MAUI_INTEGRATION.md) ·
[repository](https://github.com/kfrancis/ObdInsight)
