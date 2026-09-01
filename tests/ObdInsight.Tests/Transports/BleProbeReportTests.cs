using System.Reflection;
using System.Text.Json;
using ObdInsight.Transports.Ble;
using Plugin.BLE.Abstractions.Contracts;

namespace ObdInsight.Tests.Transports;

[Timeout(30_000)]
public sealed class BleProbeReportTests
{
    private static readonly Guid DeviceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid Ffe0 = BleUuid.FromShortId(0xFFE0);
    private static readonly Guid Ffe1 = BleUuid.FromShortId(0xFFE1);

    [Test]
    public async Task OpenAsync_KnownProfile_ReportsSuccessfulProbe(CancellationToken ct)
    {
        var characteristic = CreateCharacteristic(Ffe1, true, true);
        var transport = CreateTransport([CreateService(Ffe0, [characteristic])]);

        await transport.OpenAsync(ct);

        var report = transport.LastProbeReport;
        await Assert.That(report).IsNotNull();
        await Assert.That(report!.Stage).IsEqualTo(BleProbeStage.Completed);
        await Assert.That(report.FailureKind).IsNull();
        await Assert.That(report.Services).Count().IsEqualTo(1);
        await Assert.That(report.ResolvedProfile).IsNotNull();
        await Assert.That(report.ResolvedProfile!.Name).Contains("Vgate");
    }

    [Test]
    public async Task OpenAsync_FallbackProfile_ReportsSuccessfulProbe(CancellationToken ct)
    {
        var serviceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var writeId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var notifyId = Guid.Parse("10000000-0000-0000-0000-000000000003");
        var transport = CreateTransport(
        [
            CreateService(serviceId,
            [
                CreateCharacteristic(writeId, true, false),
                CreateCharacteristic(notifyId, false, true)
            ])
        ]);

        await transport.OpenAsync(ct);

        var report = transport.LastProbeReport;
        await Assert.That(report).IsNotNull();
        await Assert.That(report!.Stage).IsEqualTo(BleProbeStage.Completed);
        await Assert.That(report.ResolvedProfile).IsNotNull();
        await Assert.That(report.ResolvedProfile!.Name).Contains("Generic");
    }

    [Test]
    public async Task OpenAsync_UnusableTopology_ReportsNoCompatibleProfile(CancellationToken ct)
    {
        var serviceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var characteristicId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var transport = CreateTransport(
        [
            CreateService(serviceId,
                [CreateCharacteristic(characteristicId, false, false)])
        ]);

        await Assert.That(async () => await transport.OpenAsync(ct)).Throws<IOException>();

        await AssertFailure(
            transport,
            BleProbeStage.ResolvingProfile,
            BleProbeFailureKind.NoCompatibleProfile,
            1);
    }

    [Test]
    public async Task OpenAsync_ConnectionFailure_ReportsNormalizedFailure(CancellationToken ct)
    {
        var failure = new IOException(
            $"Device {DeviceId} at AA:BB:CC:DD:EE:FF failed; manufacturer payload DEADBEEF.\n" +
            "at Platform.Bluetooth.Connect()");
        var transport = CreateTransport([], failure);

        await Assert.That(async () => await transport.OpenAsync(ct)).Throws<IOException>();

        await AssertFailure(
            transport,
            BleProbeStage.Connecting,
            BleProbeFailureKind.ConnectionFailed,
            0);
    }

    [Test]
    public async Task OpenAsync_ServiceDiscoveryFailure_ReportsNormalizedFailure(CancellationToken ct)
    {
        var transport = CreateTransport([], serviceDiscoveryFailure: new IOException("Discovery failed."));

        await Assert.That(async () => await transport.OpenAsync(ct)).Throws<IOException>();

        await AssertFailure(
            transport,
            BleProbeStage.DiscoveringServices,
            BleProbeFailureKind.ServiceDiscoveryFailed,
            0);
    }

    [Test]
    public async Task OpenAsync_CharacteristicBindingFailure_RetainsSelectedProfile(CancellationToken ct)
    {
        var characteristic = CreateCharacteristic(Ffe1, true, true);
        var service = CreateService(
            Ffe0,
            [characteristic],
            1,
            new IOException("Binding failed."));
        var transport = CreateTransport([service]);

        await Assert.That(async () => await transport.OpenAsync(ct)).Throws<IOException>();

        await AssertFailure(
            transport,
            BleProbeStage.BindingCharacteristics,
            BleProbeFailureKind.CharacteristicBindingFailed,
            1);
        await Assert.That(transport.LastProbeReport!.ResolvedProfile).IsNotNull();
    }

    [Test]
    public async Task OpenAsync_NotificationSubscriptionFailure_RetainsSelectedProfile(CancellationToken ct)
    {
        var characteristic = CreateCharacteristic(
            Ffe1,
            true,
            true,
            new IOException("Subscription failed."));
        var transport = CreateTransport([CreateService(Ffe0, [characteristic])]);

        await Assert.That(async () => await transport.OpenAsync(ct)).Throws<IOException>();

        await AssertFailure(
            transport,
            BleProbeStage.SubscribingNotifications,
            BleProbeFailureKind.NotificationSubscriptionFailed,
            1);
        await Assert.That(transport.LastProbeReport!.ResolvedProfile).IsNotNull();
    }

    [Test]
    public async Task OpenAsync_Throw_PopulatesReportBeforeOriginalExceptionEscapes(CancellationToken ct)
    {
        var original = new IOException("Original connection failure.");
        var transport = CreateTransport([], original);
        BleProbeReport? observedReport = null;
        var eventCount = 0;
        transport.ProbeCompleted += report =>
        {
            eventCount++;
            observedReport = report;
            throw new InvalidOperationException("A subscriber must not replace the transport exception.");
        };

        Exception? caught = null;
        try
        {
            await transport.OpenAsync(ct);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsSameReferenceAs(original);
        await Assert.That(transport.LastProbeReport).IsNotNull();
        await Assert.That(observedReport).IsSameReferenceAs(transport.LastProbeReport);
        await Assert.That(eventCount).IsEqualTo(1);
    }

    [Test]
    public async Task OpenAsync_ProbeCompletedFiresExactlyOncePerAttempt(CancellationToken ct)
    {
        var characteristic = CreateCharacteristic(Ffe1, true, true);
        var transport = CreateTransport([CreateService(Ffe0, [characteristic])]);
        var eventCount = 0;
        transport.ProbeCompleted += _ => eventCount++;

        await transport.OpenAsync(ct);
        await transport.OpenAsync(ct);

        await Assert.That(eventCount).IsEqualTo(2);
    }

    [Test]
    public async Task FailureReport_DoesNotContainSensitiveProbeInputs(CancellationToken ct)
    {
        const string macAddress = "AA:BB:CC:DD:EE:FF";
        const string manufacturerPayload = "DEADBEEF";
        const string stackFrame = "Platform.Bluetooth.Connect";
        var failure = new IOException(
            $"Device {DeviceId} at {macAddress}; manufacturer payload {manufacturerPayload}.\n" +
            $"at {stackFrame}()");
        var transport = CreateTransport([], failure);

        await Assert.That(async () => await transport.OpenAsync(ct)).Throws<IOException>();

        var serialized = JsonSerializer.Serialize(transport.LastProbeReport);
        await Assert.That(serialized).DoesNotContain(DeviceId.ToString());
        await Assert.That(serialized).DoesNotContain(macAddress);
        await Assert.That(serialized).DoesNotContain(manufacturerPayload);
        await Assert.That(serialized).DoesNotContain(stackFrame);
        await Assert.That(serialized).DoesNotContain("StackTrace");
    }

    [Test]
    public async Task OpenAsync_Cancellation_ReportsAndRethrowsCancellation(CancellationToken ct)
    {
        var original = new OperationCanceledException(ct);
        var transport = CreateTransport([], original);
        var eventCount = 0;
        transport.ProbeCompleted += _ => eventCount++;

        await Assert.That(async () => await transport.OpenAsync(ct)).Throws<OperationCanceledException>();

        await AssertFailure(
            transport,
            BleProbeStage.Connecting,
            BleProbeFailureKind.Cancelled,
            0);
        await Assert.That(eventCount).IsEqualTo(1);
    }

    private static async Task AssertFailure(
        PluginBleElmTransport transport,
        BleProbeStage expectedStage,
        BleProbeFailureKind expectedKind,
        int expectedServiceCount)
    {
        var report = transport.LastProbeReport;
        await Assert.That(report).IsNotNull();
        await Assert.That(report!.Stage).IsEqualTo(expectedStage);
        await Assert.That(report.FailureKind).IsEqualTo(expectedKind);
        await Assert.That(report.FailureMessage).IsNotNull();
        await Assert.That(report.Services).Count().IsEqualTo(expectedServiceCount);
    }

    private static PluginBleElmTransport CreateTransport(
        IReadOnlyList<IService> services,
        Exception? connectionFailure = null,
        Exception? serviceDiscoveryFailure = null)
    {
        var device = CreateProxy<IDevice>((method, _) =>
            method.Name switch
            {
                "get_Id" => DeviceId,
                "get_Name" => null,
                "GetServicesAsync" when serviceDiscoveryFailure is not null =>
                    Task.FromException<IReadOnlyList<IService>>(serviceDiscoveryFailure),
                "GetServicesAsync" => Task.FromResult(services),
                _ => throw Unexpected(method)
            });

        var adapter = CreateProxy<IAdapter>((method, _) =>
            method.Name switch
            {
                "ConnectToKnownDeviceAsync" when connectionFailure is not null =>
                    Task.FromException<IDevice>(connectionFailure),
                "ConnectToKnownDeviceAsync" => Task.FromResult(device),
                "add_DeviceDisconnected" or "remove_DeviceDisconnected" or
                    "add_DeviceConnectionLost" or "remove_DeviceConnectionLost" => null,
                _ => throw Unexpected(method)
            });

        return new PluginBleElmTransport(adapter, DeviceId);
    }

    private static IService CreateService(
        Guid id,
        IReadOnlyList<ICharacteristic> characteristics,
        int? failureAfterCall = null,
        Exception? failure = null)
    {
        var calls = 0;
        return CreateProxy<IService>((method, _) =>
            method.Name switch
            {
                "get_Id" => id,
                "GetCharacteristicsAsync"
                    when failureAfterCall is not null && calls++ >= failureAfterCall =>
                    Task.FromException<IReadOnlyList<ICharacteristic>>(
                        failure ?? new IOException("Characteristic discovery failed.")),
                "GetCharacteristicsAsync" => Task.FromResult(characteristics),
                _ => throw Unexpected(method)
            });
    }

    private static ICharacteristic CreateCharacteristic(
        Guid id,
        bool canWrite,
        bool canNotify,
        Exception? subscriptionFailure = null) =>
        CreateProxy<ICharacteristic>((method, _) =>
            method.Name switch
            {
                "get_Id" => id,
                "get_CanWrite" => canWrite,
                "get_CanUpdate" => canNotify,
                "set_WriteType" or "add_ValueUpdated" or "remove_ValueUpdated" => null,
                "StartUpdatesAsync" when subscriptionFailure is not null =>
                    Task.FromException(subscriptionFailure),
                "StartUpdatesAsync" or "StopUpdatesAsync" => Task.CompletedTask,
                _ => throw Unexpected(method)
            });

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, TestDispatchProxy>();
        ((TestDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static InvalidOperationException Unexpected(MethodInfo method) =>
        new($"Unexpected test-double call: {method.DeclaringType?.Name}.{method.Name}");

    private class TestDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args);
    }
}
