using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Communication.Slcan;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;
using ObdInsight.Simulation;

namespace ObdInsight.Tests.Elm327;

/// <summary>
///     <see cref="CanMonitor" /> over an <see cref="ICanFrameSource" /> - the CANable path. The
///     source is the production <see cref="SlcanFrameSource" /> over the replay transport, so
///     this pins the whole chain a raw adapter goes through: SLCAN bytes → frames → fan-out →
///     latest cache → typed decoders → Leaf broadcast capabilities. Nothing ELM327-shaped is
///     involved, which is the point: the consumers above the monitor must not care.
/// </summary>
[Timeout(30_000)]
public class CanMonitorFrameSourceTests
{
    private const string CanableBanner = "16e7497-dirty github.com/normaldotcom/canable2.git";

    /// <summary>Replay transport scripted as a stock CANable: silent on every command except <c>V</c>.</summary>
    private static ReplayElmTransport CanableTransport()
    {
        var transport = new ReplayElmTransport();
        foreach (var command in new[] { "C", "S6", "M0", "M1", "O", "L" })
        {
            transport.AutoRespond(command, "");
        }

        transport.AutoRespond("V", CanableBanner + "\r");
        return transport;
    }

    private static (ReplayElmTransport Transport, SlcanFrameSource Source, CanMonitor Monitor) CreateMonitor()
    {
        var transport = CanableTransport();
        var source = new SlcanFrameSource(transport);
        var monitor = new CanMonitor(source);
        return (transport, source, monitor);
    }

    private static async Task WaitForLatestAsync(CanMonitor monitor, int canId, CancellationToken ct)
    {
        while (!monitor.TryGetLatest(canId, out _))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }

    private static async Task WaitForEndAsync(CanMonitor monitor, CancellationToken ct)
    {
        while (monitor.EndReason == MonitoringEndReason.None || monitor.IsRunning)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(10, ct);
        }
    }

    [Test]
    public async Task Start_OpensTheSourceListenOnly(CancellationToken token)
    {
        var (transport, source, monitor) = CreateMonitor();
        await using var lifetime = monitor;

        await monitor.StartAsync(token);

        await Assert.That(monitor.IsRunning).IsTrue();
        await Assert.That(monitor.IsFrameSourceBacked).IsTrue();
        await Assert.That(source.Dialect).IsEqualTo(SlcanDialect.Canable);
        await Assert.That(transport.SentCommands).IsEquivalentTo(new[] { "C", "V", "S6", "M1", "O" });
    }

    [Test]
    public async Task Frames_AreDemuxedAndCached(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        await using var lifetime = monitor;
        await monitor.StartAsync(token);

        var batteryFrames = new List<RawCanFrame>();
        var stream = monitor.Subscribe(new[] { 0x1DB }, token);
        var reader = Task.Run(async () =>
        {
            await foreach (var frame in stream) batteryFrames.Add(frame);
        }, token);

        transport.EnqueueIncoming("t1DB80102030405060708\rt55A81112131415161718\rt1DB82122232425262728\r");
        await WaitForLatestAsync(monitor, 0x55A, token);
        await WaitForLatestAsync(monitor, 0x1DB, token);

        await monitor.StopAsync(token);
        await reader.WaitAsync(token);

        await Assert.That(batteryFrames.Count).IsEqualTo(2);
        await Assert.That(monitor.TryGetLatest(0x1DB, out var latest)).IsTrue();
        await Assert.That(Convert.ToHexString(latest.Data.ToArray())).IsEqualTo("2122232425262728");
        await Assert.That(monitor.EndReason).IsEqualTo(MonitoringEndReason.Stopped);
    }

    /// <summary>The typed layer is source-agnostic: generated decoders run on SLCAN frames unchanged.</summary>
    [Test]
    public async Task TypedCache_DecodesSlcanFrames(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        await using var lifetime = monitor;
        await monitor.StartAsync(token);

        // Same bytes GeneratedFrameDecodingTests use for 0x1DB; only the wire wrapper differs.
        transport.EnqueueIncoming("t1DB8" + "0000C4A0" + "00000000" + "\r");
        await WaitForLatestAsync(monitor, 0x1DB, token);

        await Assert.That(monitor.TryGetLatest<BatteryFrame_1DB_AZE0>(out var battery)).IsTrue();
        await Assert.That(battery).IsNotNull();
        await Assert.That(double.IsFinite(battery.Voltage)).IsTrue();
    }

    [Test]
    public async Task Stop_ClosesTheChannel_AndCanRestart(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        await using var lifetime = monitor;

        await monitor.StartAsync(token);
        await monitor.StopAsync(token);
        await monitor.StartAsync(token);
        await monitor.StopAsync(token);

        // Open-close-open-close: C on every open (defensive) and on every close.
        await Assert.That(transport.SentCommands.Count(c => c == "O")).IsEqualTo(2);
        await Assert.That(transport.SentCommands.Count(c => c == "C")).IsEqualTo(4);
        await Assert.That(monitor.EndReason).IsEqualTo(MonitoringEndReason.Stopped);
    }

    /// <summary>
    ///     A raw adapter has no BUFFER FULL to recover from; the one failure it has is the link
    ///     dying, and that must surface as a permanent end with the right reason so consumers
    ///     (and a reconnect layer) can tell it from a clean stop.
    /// </summary>
    [Test]
    public async Task LinkLoss_EndsTheMonitorWithTransportError(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        await using var lifetime = monitor;
        await monitor.StartAsync(token);

        transport.EnqueueIncoming("t1DB80102030405060708\r");
        await WaitForLatestAsync(monitor, 0x1DB, token);

        transport.SimulateConnectionLost();
        await WaitForEndAsync(monitor, token);

        await Assert.That(monitor.EndReason).IsEqualTo(MonitoringEndReason.TransportError);
        // The cache survives the end: last-known values stay readable.
        await Assert.That(monitor.TryGetLatest(0x1DB, out _)).IsTrue();
    }

    /// <summary>
    ///     Suspension exists for UDS arbitration on an ELM327. A raw source has nothing to
    ///     arbitrate, but the contract still has to hold: stop the source, keep subscriptions and
    ///     cache, restart on dispose.
    /// </summary>
    [Test]
    public async Task Suspend_StopsAndRestartsTheSource_KeepingSubscribers(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        await using var lifetime = monitor;
        await monitor.StartAsync(token);

        var received = new List<RawCanFrame>();
        var stream = monitor.Subscribe(new[] { 0x1DB }, token);
        var reader = Task.Run(async () =>
        {
            await foreach (var frame in stream) received.Add(frame);
        }, token);

        transport.EnqueueIncoming("t1DB80102030405060708\r");
        await WaitForLatestAsync(monitor, 0x1DB, token);

        var opensBefore = transport.SentCommands.Count(c => c == "O");
        await using (await monitor.SuspendAsync(token))
        {
            await Assert.That(monitor.IsRunning).IsFalse();
            await Assert.That(monitor.EndReason).IsEqualTo(MonitoringEndReason.None);
        }

        await Assert.That(monitor.IsRunning).IsTrue();
        await Assert.That(transport.SentCommands.Count(c => c == "O")).IsEqualTo(opensBefore + 1);

        transport.EnqueueIncoming("t1DB82122232425262728\r");
        while (received.Count < 2)
        {
            await Task.Delay(10, token);
        }

        await monitor.StopAsync(token);
        await reader.WaitAsync(token);
        await Assert.That(received.Count).IsEqualTo(2);
    }

    [Test]
    public async Task FilterRotation_IsRejected_ForAFrameSource(CancellationToken token)
    {
        var (_, _, monitor) = CreateMonitor();
        await using var lifetime = monitor;
        monitor.FilterRotation = LeafAze0Contexts.SharedBroadcastRotation;

        await Assert.That(async () => await monitor.StartAsync(token)).Throws<InvalidOperationException>();
    }

    /// <summary>
    ///     The Leaf command set over a raw source: every broadcast capability is there, the ones
    ///     that would have to transmit are reported unsupported instead of failing later.
    /// </summary>
    [Test]
    public async Task LeafCommandSet_OverAFrameSource_IsBroadcastOnly(CancellationToken token)
    {
        var transport = CanableTransport();
        var source = new SlcanFrameSource(transport);
        var commands = new LeafAze0CommandSet(source);
        await using var monitor = commands.Monitor;
        var vehicle = new VehicleSession(commands);

        await Assert.That(monitor.IsFrameSourceBacked).IsTrue();
        await Assert.That(vehicle.Supports<IHvac>()).IsTrue();
        await Assert.That(vehicle.Supports<IMotorController>()).IsTrue();
        await Assert.That(vehicle.Supports<IVcm>()).IsTrue();
        await Assert.That(vehicle.Supports<IBrake>()).IsTrue();
        await Assert.That(vehicle.Supports<IAntilockBrakingSystem>()).IsTrue();
        await Assert.That(vehicle.Supports<IBodyControl>()).IsTrue();
        await Assert.That(vehicle.Supports<IOnboardCharger>()).IsTrue();

        await Assert.That(vehicle.Supports<IBatteryManagementSystem>()).IsFalse();
        await Assert.That(vehicle.Supports<IVehicleIdentification>()).IsFalse();
        await Assert.That(vehicle.Supports<IDiagnosticTroubleCodes>()).IsFalse();
        await Assert.That(vehicle.Supports<ISteering>()).IsFalse();
    }

    /// <summary>
    ///     End to end through a capability: an SLCAN 0x1DB line becomes a decoded battery
    ///     snapshot via the typed cache, with the monitor started by the capability itself.
    /// </summary>
    [Test]
    public async Task LeafCommandSet_OverAFrameSource_TypedStreamDecodes(CancellationToken token)
    {
        var transport = CanableTransport();
        var source = new SlcanFrameSource(transport);
        var commands = new LeafAze0CommandSet(source);
        await using var monitor = commands.Monitor;

        var stream = monitor.Subscribe<BatteryFrame_1DB_AZE0>(token);
        var first = Task.Run(async () =>
        {
            await foreach (var frame in stream) return frame;
            return null;
        }, token);

        await monitor.StartAsync(token);
        transport.EnqueueIncoming("t1DB80000C4A000000000\r");

        var decoded = await first.WaitAsync(TimeSpan.FromSeconds(5), token);
        await Assert.That(decoded).IsNotNull();
        await monitor.StopAsync(token);
    }
}
