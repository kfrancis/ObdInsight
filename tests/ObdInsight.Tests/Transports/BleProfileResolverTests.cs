using ObdInsight.Transports.Ble;

namespace OdbTestApp.Tests.Transports;

/// <summary>
/// Roadmap B9: pure GATT profile auto-probe tests (docs/BLE_TRANSPORT_DESIGN.md §4).
/// No BLE dependencies — topology in, resolution out.
/// </summary>
[Timeout(30_000)]
public class BleProfileResolverTests
{
    private static readonly Guid Ffe0 = BleUuid.FromShortId(0xFFE0);
    private static readonly Guid Ffe1 = BleUuid.FromShortId(0xFFE1);
    private static readonly Guid Fff0 = BleUuid.FromShortId(0xFFF0);
    private static readonly Guid Fff1 = BleUuid.FromShortId(0xFFF1);
    private static readonly Guid Fff2 = BleUuid.FromShortId(0xFFF2);

    [Test]
    public async Task VgateICarPro_SingleCharacteristic_Resolves(CancellationToken _)
    {
        var topology = new[]
        {
            new GattServiceInfo(Ffe0, [new GattCharacteristicInfo(Ffe1, CanWrite: true, CanNotify: true)]),
        };

        var resolved = BleProfileResolver.Resolve(topology);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Name).Contains("Vgate");
        await Assert.That(resolved.WriteCharacteristicUuid).IsEqualTo(Ffe1);
        await Assert.That(resolved.NotifyCharacteristicUuid).IsEqualTo(Ffe1);
    }

    [Test]
    public async Task VeepeakFff0_SplitCharacteristics_Resolves(CancellationToken _)
    {
        var topology = new[]
        {
            new GattServiceInfo(Fff0,
            [
                new GattCharacteristicInfo(Fff1, CanWrite: true, CanNotify: false),
                new GattCharacteristicInfo(Fff2, CanWrite: false, CanNotify: true),
            ]),
        };

        var resolved = BleProfileResolver.Resolve(topology);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Name).Contains("Veepeak");
        await Assert.That(resolved.WriteCharacteristicUuid).IsEqualTo(Fff1);
        await Assert.That(resolved.NotifyCharacteristicUuid).IsEqualTo(Fff2);
    }

    [Test]
    public async Task NordicUart_Resolves(CancellationToken _)
    {
        var service = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");
        var rx = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
        var tx = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
        var topology = new[]
        {
            new GattServiceInfo(service,
            [
                new GattCharacteristicInfo(rx, CanWrite: true, CanNotify: false),
                new GattCharacteristicInfo(tx, CanWrite: false, CanNotify: true),
            ]),
        };

        var resolved = BleProfileResolver.Resolve(topology);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Name).Contains("Nordic");
    }

    [Test]
    public async Task VgatePriority_BeatsVeepeak_WhenBothPresent(CancellationToken _)
    {
        var topology = new[]
        {
            new GattServiceInfo(Fff0,
            [
                new GattCharacteristicInfo(Fff1, CanWrite: true, CanNotify: false),
                new GattCharacteristicInfo(Fff2, CanWrite: false, CanNotify: true),
            ]),
            new GattServiceInfo(Ffe0, [new GattCharacteristicInfo(Ffe1, CanWrite: true, CanNotify: true)]),
        };

        var resolved = BleProfileResolver.Resolve(topology);

        await Assert.That(resolved!.Name).Contains("Vgate");
    }

    [Test]
    public async Task KnownService_MissingSplit_FallsBackToDualCharacteristic(CancellationToken _)
    {
        // FFF0 service but no FFF1/FFF2 — a lone dual-role characteristic instead.
        var oddChar = BleUuid.FromShortId(0xFFF4);
        var topology = new[]
        {
            new GattServiceInfo(Fff0, [new GattCharacteristicInfo(oddChar, CanWrite: true, CanNotify: true)]),
        };

        var resolved = BleProfileResolver.Resolve(topology);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Name).Contains("fallback");
        await Assert.That(resolved.WriteCharacteristicUuid).IsEqualTo(oddChar);
        await Assert.That(resolved.NotifyCharacteristicUuid).IsEqualTo(oddChar);
    }

    [Test]
    public async Task UnknownService_WithUsablePair_GenericFallback(CancellationToken _)
    {
        var service = Guid.NewGuid();
        var w = Guid.NewGuid();
        var n = Guid.NewGuid();
        var topology = new[]
        {
            new GattServiceInfo(service,
            [
                new GattCharacteristicInfo(w, CanWrite: true, CanNotify: false),
                new GattCharacteristicInfo(n, CanWrite: false, CanNotify: true),
            ]),
        };

        var resolved = BleProfileResolver.Resolve(topology);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Name).Contains("Generic");
    }

    [Test]
    public async Task UnknownService_WithVeepeakDeviceName_HintsInGenericFallback(CancellationToken _)
    {
        var service = Guid.NewGuid();
        var w = Guid.NewGuid();
        var n = Guid.NewGuid();
        var topology = new[]
        {
            new GattServiceInfo(service,
            [
                new GattCharacteristicInfo(w, CanWrite: true, CanNotify: false),
                new GattCharacteristicInfo(n, CanWrite: false, CanNotify: true),
            ]),
        };

        var resolved = BleProfileResolver.Resolve(topology, deviceName: "Veepeak OBDII");

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Name).Contains("Veepeak-like");
    }

    [Test]
    public async Task KnownService_MismatchedCharacteristicUuids_FallsBackWithinService(CancellationToken _)
    {
        // FFF0 service present (matches Veepeak), but the write/notify UUIDs are a
        // different firmware batch's FFF3/FFF4 — not FFF1/FFF2, and not a single
        // dual-role characteristic either.
        var altWrite = BleUuid.FromShortId(0xFFF3);
        var altNotify = BleUuid.FromShortId(0xFFF4);
        var topology = new[]
        {
            new GattServiceInfo(Fff0,
            [
                new GattCharacteristicInfo(altWrite, CanWrite: true, CanNotify: false),
                new GattCharacteristicInfo(altNotify, CanWrite: false, CanNotify: true),
            ]),
        };

        var resolved = BleProfileResolver.Resolve(topology);

        await Assert.That(resolved).IsNotNull();
        await Assert.That(resolved!.Name).Contains("Veepeak");
        await Assert.That(resolved.Name).Contains("characteristic fallback");
        await Assert.That(resolved.WriteCharacteristicUuid).IsEqualTo(altWrite);
        await Assert.That(resolved.NotifyCharacteristicUuid).IsEqualTo(altNotify);
    }

    [Test]
    public async Task NoUsableTopology_ReturnsNull(CancellationToken _)
    {
        var topology = new[]
        {
            new GattServiceInfo(Guid.NewGuid(),
                [new GattCharacteristicInfo(Guid.NewGuid(), CanWrite: false, CanNotify: false)]),
        };

        await Assert.That(BleProfileResolver.Resolve(topology)).IsNull();
        await Assert.That(BleProfileResolver.Resolve([])).IsNull();
    }

    [Test]
    public async Task ShortAndLongUuidForms_Match(CancellationToken token)
    {
        await Assert.That(BleUuid.Matches(Ffe0, Guid.Parse("0000ffe0-0000-1000-8000-00805f9b34fb"))).IsTrue();
        await Assert.That(BleUuid.TryGetShortId(Ffe0, out var shortId)).IsTrue();
        await Assert.That((int)shortId).IsEqualTo(0xFFE0);
        // A random 128-bit UUID is not short-form.
        await Assert.That(BleUuid.TryGetShortId(Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e"), out var none)).IsFalse();
        await Assert.That((int)none).IsEqualTo(0);
    }

    [Test]
    public async Task Chunk_SplitsAtBoundary(CancellationToken _)
    {
        var data = new byte[45];
        var chunks = BleProfileResolver.Chunk(data, 20).ToList();

        await Assert.That(chunks.Count).IsEqualTo(3);
        await Assert.That(chunks[0].Length).IsEqualTo(20);
        await Assert.That(chunks[1].Length).IsEqualTo(20);
        await Assert.That(chunks[2].Length).IsEqualTo(5);
    }
}
