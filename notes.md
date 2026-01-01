A few Windows-specific BLE “gotchas” are biting you here. Your code is *close*, but right now it relies on timing (fixed delays), treats “write” as a simple operation, and doesn’t fully leverage WinRT’s session/status APIs—so it tends to look flaky compared to Plugin.BLE-style stacks.

Below are the changes that usually make WinRT BLE on Windows 10/11 behave *dramatically* more reliably, with concrete suggestions against your current implementation.

---

## 1) Stop using fixed “stabilize” delays as a correctness mechanism

You currently do a **1s delay** after creating the device, plus a **2s delay** before returning “connected”  . These help sometimes, but they’re also a classic reason Windows BLE looks random: Windows may be ready sooner… or later… or never (if pairing/auth is required).

**Better pattern on Windows:**

* Create a `GattSession`, set `MaintainConnection = true` (good that you do this ), *then* wait for a real signal:

  * `GattSession.SessionStatusChanged` (and optionally `MaxPduSizeChanged`)
  * first successful GATT op (like `GetCharacteristicsForUuidAsync` or `WriteClientCharacteristicConfigurationDescriptorAsync`)
* Use a `TaskCompletionSource` for “connected enough to talk”.

Microsoft’s guidance: `MaintainConnection` means the system will keep trying, but there’s nothing “awaitable” about it—you need to observe session/device status changes. ([Microsoft Learn][1])

---

## 2) Prefer “targeted enumeration” over enumerating everything Uncached

You do:

* `GetGattServicesAsync(BluetoothCacheMode.Uncached)` with retries 
* then `GetCharacteristicsAsync(BluetoothCacheMode.Uncached)` with retries 

This is slow and can fail transiently on Windows (especially right after connect). Instead:

**Use UUID-targeted calls** (fewer moving parts, less time for the stack to wobble):

* `GetGattServicesForUuidAsync(Profile.ServiceUuid, Cached)` first
* If that fails or returns 0, try `Uncached`
* Same for characteristics: `GetCharacteristicsForUuidAsync(...)`

That avoids enumerating unrelated services/characteristics and reduces “Unreachable” windows.

---

## 3) Switch writes to `WriteValueWithResultAsync` and log protocol errors

You’re using `WriteValueAsync(buffer, writeType)` and only get `GattCommunicationStatus` .

On Windows, `WriteValueWithResultAsync` gives you a `GattWriteResult` with a **ProtocolError** field, which is *huge* for diagnosing:

* insufficient authentication / pairing problems
* invalid offset / long write not supported
* attribute not permitted

Docs: `WriteValueWithResultAsync` exists specifically for getting richer results. ([Microsoft Learn][2])

**Why it matters for your scenario:** ELM327 BLE clones and “UART-over-BLE” devices can be picky about write type, MTU/fragmenting, and auth state.

---

## 4) Serialize all writes (SemaphoreSlim) + add a “write pacing” knob

Windows BLE really doesn’t like concurrent writes. Even if *you* don’t call it concurrently, upstream code often can (especially if you do “send command” + “poll” + “init sequence”).

Right now there is no write lock; you retry 3 times and then mark disconnected  .

**Do this:**

* `private readonly SemaphoreSlim _writeGate = new(1,1);`
* `await _writeGate.WaitAsync(ct); try { ... } finally { _writeGate.Release(); }`
* Add a tiny inter-write delay (configurable). For some adapters, 10–30ms is the difference between “works” and “randomly dies”.

---

## 5) Don’t treat “CCCD enable failed” as “maybe ok”

You currently:

* try CCCD enable up to 3 times,
* if it still fails, you continue anyway 

For many BLE designs, if CCCD didn’t enable, you will **never** receive responses (so later “writes” appear to succeed but you get no data).

There *are* devices that stream data without CCCD, but OBD BLE UART-style adapters typically require it.

**Recommended behavior:**

* If your profile says notifications are required for RX, fail connect if CCCD fails.
* If you truly want fallback, make it explicit: `Profile.NotificationsOptional`.

Also: CCCD writes can fail the *first time* for non-bonded devices on Windows (people often workaround by retrying after a short delay, or doing a benign read first). ([Stack Overflow][3])

---

## 6) Hook `GattSession` events and handle “reconnect churn” properly

You already hook `_device.ConnectionStatusChanged` , but Windows BLE’s “Connected” at the device level is not always meaningful for GATT readiness.

Add:

* `_gattSession.SessionStatusChanged += ...`
* `_gattSession.MaxPduSizeChanged += ...`

Then:

* If the session drops, move to a “Reconnecting” state, and attempt to re-enable notifications + re-resolve characteristics (Windows sometimes invalidates handles after reconnect).

Also note: if the device is no longer bonded and you keep `MaintainConnection=true`, Windows can get into a “connect/disconnect loop”. ([Microsoft Learn][4])

---

## 7) Use `BluetoothCacheMode.Cached` first (Windows can be *worse* with Uncached)

Counterintuitive, but on Windows:

* `Cached` is often more reliable immediately after connecting
* `Uncached` can be slower and sometimes returns empty/Unreachable transiently

So: **Cached → fallback to Uncached** is a better default than Uncached-everywhere.

---

## 8) Reduce allocations in notification path

You do `args.CharacteristicValue.ToArray()` on every notification .

For OBD adapters that stream a lot, that allocation churn can cause GC hiccups and “missed” reads that look like BLE flakiness.

At minimum:

* rent from `ArrayPool<byte>` and copy
* or change `OnDataReceived` to accept an `IBuffer`/`ReadOnlySpan<byte>` path (if you can)

---

## 9) Dispose() shouldn’t block on async (can deadlock / hang shutdown)

`Dispose()` calls `DisconnectAsync().GetAwaiter().GetResult();` .

In UI contexts (or even some WinRT threading setups), this can deadlock. Prefer:

* `IAsyncDisposable` with `DisposeAsync`
* keep `Dispose()` best-effort non-blocking (or call a sync cleanup that doesn’t await)

---

# A concrete “Windows BLE reliable” write + connect skeleton

Here’s the shape I’d move toward (fits your code style, keeps your profile approach):

```csharp
private readonly SemaphoreSlim _writeGate = new(1, 1);
private TaskCompletionSource<bool>? _gattReadyTcs;

public override async Task<bool> ConnectAsync(string deviceAddress, CancellationToken ct = default)
{
    _userDisconnecting = false;
    _isConnected = false;
    SetConnectionState(BleConnectionState.Connecting);

    var mac = ParseMacAddress(deviceAddress);
    _device = await BluetoothLEDevice.FromBluetoothAddressAsync(mac).AsTask(ct);
    if (_device is null) return false;

    _device.ConnectionStatusChanged += OnConnectionStatusChanged;

    _gattSession = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(ct);
    if (_gattSession is not null)
    {
        _gattSession.MaintainConnection = true;
        _gattSession.SessionStatusChanged += (_, __) => TrySignalGattReady();
        _gattSession.MaxPduSizeChanged += (_, __) => { /* log MTU */ };
    }

    _gattReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Targeted service fetch (Cached then Uncached)
    var svc = await GetServiceForUuidAsync(Profile.ServiceUuid, ct);
    if (svc is null) return false;
    _service = svc;

    _writeCharacteristic = await GetCharacteristicForUuidAsync(_service, Profile.WriteCharacteristicUuid, ct);
    if (_writeCharacteristic is null) return false;

    _notifyCharacteristic = await GetCharacteristicForUuidAsync(_service, Profile.NotifyCharacteristicUuid, ct);
    if (_notifyCharacteristic is not null)
    {
        _notifyCharacteristic.ValueChanged += OnCharacteristicValueChanged;
        var ok = await EnableNotifyAsync(_notifyCharacteristic, ct);
        if (!ok /* && Profile.NotificationsRequired */) return false;
    }

    TrySignalGattReady();
    using var reg = ct.Register(() => _gattReadyTcs.TrySetCanceled(ct));
    await _gattReadyTcs.Task; // wait for real readiness, not fixed delays

    ClearBuffer();
    _isConnected = true;
    SetConnectionState(BleConnectionState.Connected);
    return true;
}

protected override async Task WriteCharacteristicAsync(byte[] data, CancellationToken ct)
{
    if (_writeCharacteristic is null) throw new InvalidOperationException();

    await _writeGate.WaitAsync(ct);
    try
    {
        // Prefer richer result
        var opt = Profile.WriteWithResponse ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        var res = await _writeCharacteristic.WriteValueWithResultAsync(data.AsBuffer(), opt).AsTask(ct);

        if (res.Status != GattCommunicationStatus.Success)
            throw new IOException($"Write failed: {res.Status}, protoErr={res.ProtocolError?.ToString() ?? "n/a"}");

        // optional pacing
        // await Task.Delay(Profile.InterWriteDelayMs, ct);
    }
    finally { _writeGate.Release(); }
}
```

(That snippet is intentionally showing the “shape”; you’d fill in `GetServiceForUuidAsync`, `GetCharacteristicForUuidAsync`, `EnableNotifyAsync`, and `TrySignalGattReady()`.)

---

## 10) One more thing that often explains “works elsewhere, not on Windows”

If the BLE characteristic requires authentication/encryption, Windows will return errors like insufficient authentication when writing CCCD or writing values (and it can be inconsistent across adapters). That’s a real-world issue people hit with CCCD writes. ([Microsoft Learn][5])

Using `WriteValueWithResultAsync` + logging `ProtocolError` is the fastest way to know whether you’re facing:

* pairing/auth required
* MTU/long-write issues
* simply “device out of range / unreachable”

---

If you paste your `BleDeviceProfile` for the adapter (service UUID + RX/TX characteristic UUIDs + whether it’s Notify vs Indicate), I can suggest the most correct “notifications required” behavior and a safe default write option for ELM327 BLE clones.

[1]: https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/gatt-client?utm_source=chatgpt.com "Bluetooth GATT Client - UWP applications"
[2]: https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.genericattributeprofile.gattcharacteristic.writevaluewithresultasync?view=winrt-26100&utm_source=chatgpt.com "GattCharacteristic.WriteValueWithResultAsync Method"
[3]: https://stackoverflow.com/questions/55545720/windows-10-ble-c-subscribing-to-notifications-from-the-peripheral-using-cccd-f?utm_source=chatgpt.com "Windows 10 BLE C#: Subscribing to notifications from the ..."
[4]: https://learn.microsoft.com/en-us/answers/questions/1685221/bluetooth-le-bluetoothledevice-frombluetoothaddres?utm_source=chatgpt.com "BluetoothLEDevice.FromBluetoothAddressAsync returns ..."
[5]: https://learn.microsoft.com/en-us/answers/questions/1848957/uwp-ble-not-working-for-authenticated-attributes?utm_source=chatgpt.com "UWP BLE not working for authenticated attributes"
