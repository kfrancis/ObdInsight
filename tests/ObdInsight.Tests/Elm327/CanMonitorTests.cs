using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles.Implementations.Nissan.AZE0;
using ObdInsight.Tests.Base;

namespace OdbTestApp.Tests.Elm327;

/// <summary>
/// Phase 1 tests for <see cref="CanMonitor"/> (docs/STREAMING_MONITOR_DESIGN.md) over the
/// replay transport: demux, drop-oldest, latest cache, BUFFER FULL restart, end reasons.
/// </summary>
[Timeout(30_000)]
public class CanMonitorTests
{
    private static (ReplayElmTransport Transport, ElmSession Session, CanMonitor Monitor) CreateMonitor()
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var monitor = new CanMonitor(session, EcuContext.NissanLeafHvbatMonitor)
        {
            RestartDelay = TimeSpan.Zero,
        };
        // "ATMA" must stay silent — monitoring streams instead of prompting.
        transport.Expect("ATMA", "");
        return (transport, session, monitor);
    }

    /// <summary>Polls until the monitor's cache holds a frame for canId (loop has processed it).</summary>
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
    public async Task Subscribers_WithDisjointIds_EachReceiveOnlyTheirFrames(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        await monitor.StartAsync(token);

        var batteryFrames = new List<RawCanFrame>();
        var inverterFrames = new List<RawCanFrame>();
        var batteryTask = Task.Run(async () =>
        {
            await foreach (var f in monitor.Subscribe(new[] { 0x1DB }, token)) batteryFrames.Add(f);
        }, token);
        var inverterTask = Task.Run(async () =>
        {
            await foreach (var f in monitor.Subscribe(new[] { 0x1DA }, token)) inverterFrames.Add(f);
        }, token);

        transport.EnqueueIncoming("1DB 01 02 03 04 05 06 07 08\r");
        transport.EnqueueIncoming("1DA 11 12 13 14 15 16 17 18\r");
        transport.EnqueueIncoming("1DB 21 22 23 24 25 26 27 28\r");
        transport.EnqueueIncoming("5BC 31 32 33 34 35 36 37 38\r"); // nobody subscribed
        await WaitForLatestAsync(monitor, 0x5BC, token);

        await monitor.StopAsync(token);
        await Task.WhenAll(batteryTask, inverterTask).WaitAsync(token);

        await Assert.That(batteryFrames.Count).IsEqualTo(2);
        await Assert.That(batteryFrames.All(f => f.CanId == 0x1DB)).IsTrue();
        await Assert.That(inverterFrames.Count).IsEqualTo(1);
        await Assert.That(inverterFrames[0].CanId).IsEqualTo(0x1DA);
    }

    [Test]
    public async Task SlowSubscriber_DropsOldestFrames_KeepsNewest(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        monitor.SubscriberBufferSize = 2;
        await monitor.StartAsync(token);

        // Register but do NOT read yet — the channel fills while we "fall behind".
        var stream = monitor.Subscribe(new[] { 0x1DB }, token);

        for (var i = 1; i <= 4; i++)
            transport.EnqueueIncoming($"1DB 0{i} 00 00 00 00 00 00 00\r");

        // Wait until the loop has processed the last frame.
        while (!(monitor.TryGetLatest(0x1DB, out var latest) && latest.Data.Span[0] == 0x04))
            await Task.Delay(10, token);

        await monitor.StopAsync(token);

        var received = new List<RawCanFrame>();
        await foreach (var f in stream) received.Add(f);

        // Capacity 2, drop-oldest: frames 1 and 2 gone, 3 and 4 survive.
        await Assert.That(received.Count).IsEqualTo(2);
        await Assert.That(received[0].Data.Span[0]).IsEqualTo((byte)0x03);
        await Assert.That(received[1].Data.Span[0]).IsEqualTo((byte)0x04);
    }

    [Test]
    public async Task TryGetLatest_ReturnsNewestFrame_AndFalseWhenCold(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();

        await Assert.That(monitor.TryGetLatest(0x1DB, out _)).IsFalse();

        await monitor.StartAsync(token);
        transport.EnqueueIncoming("1DB 01 00 00 00 00 00 00 00\r");
        transport.EnqueueIncoming("1DB 02 00 00 00 00 00 00 00\r");

        while (!(monitor.TryGetLatest(0x1DB, out var latest) && latest.Data.Span[0] == 0x02))
            await Task.Delay(10, token);

        await monitor.StopAsync(token);

        await Assert.That(monitor.TryGetLatest(0x1DB, out var frame)).IsTrue();
        await Assert.That(frame.Data.Span[0]).IsEqualTo((byte)0x02);
        await Assert.That(monitor.TryGetLatest(0x7FF, out _)).IsFalse();
    }

    [Test]
    public async Task BufferFull_RestartsMonitoring_ThenGivesUpWithoutProgress(CancellationToken token)
    {
        var (transport, session, monitor) = CreateMonitor();
        monitor.MaxBufferFullRestarts = 1;
        transport.Expect("ATMA", ""); // second enter after the first BUFFER FULL

        await monitor.StartAsync(token);
        var stream = monitor.Subscribe(ReadOnlyMemory<int>.Empty, token);

        // Run 1: a frame flows, then the adapter overruns.
        transport.EnqueueIncoming("1DB 01 00 00 00 00 00 00 00\r");
        await WaitForLatestAsync(monitor, 0x1DB, token);
        transport.EnqueueIncoming("BUFFER FULL\r");

        // Run 2 (auto-restart): overruns immediately — no progress, retries exhausted.
        // Wait for the restart's ATMA to be consumed before feeding the second overrun.
        while (transport.SentCommands.Count(c => c == "ATMA") < 2)
            await Task.Delay(10, token);
        transport.EnqueueIncoming("BUFFER FULL\r");

        await WaitForEndAsync(monitor, token);

        await Assert.That(monitor.EndReason).IsEqualTo(MonitoringEndReason.BufferFull);
        await Assert.That(session.LastMonitoringEndReason).IsEqualTo(MonitoringEndReason.BufferFull);

        // Subscriber stream completed (not cancelled) and delivered the frame from run 1.
        var received = new List<RawCanFrame>();
        await foreach (var f in stream) received.Add(f);
        await Assert.That(received.Count).IsEqualTo(1);
        await Assert.That(received[0].CanId).IsEqualTo(0x1DB);
    }

    [Test]
    public async Task TypedSubscribe_DecodesProductionFrames(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        await monitor.StartAsync(token);

        var decoded = new List<BatteryFrame_1DB_AZE0>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var f in monitor.Subscribe<BatteryFrame_1DB_AZE0>(token)) decoded.Add(f);
        }, token);

        // Battery current: bit 13, 11 bits signed, Factor 0.5. Raw -32 => -16.0 A (charging).
        // Voltage: bit 30, 10 bits, Factor 0.5. Raw 720 => 360.0 V.
        var raw = ((ulong)(-32 & 0x7FF) << 13) | (720ul << 30);
        transport.EnqueueIncoming($"1DB {string.Join(" ", BitConverter.GetBytes(raw).Select(b => b.ToString("X2")))}\r");
        transport.EnqueueIncoming("1DA 00 00 00 00 00 00 00 00\r"); // different ID — must not reach the typed stream

        await WaitForLatestAsync(monitor, 0x1DA, token);
        await monitor.StopAsync(token);
        await readTask.WaitAsync(token);

        await Assert.That(decoded.Count).IsEqualTo(1);
        await Assert.That(decoded[0].Current).IsEqualTo(-16.0);
        await Assert.That(decoded[0].Voltage).IsEqualTo(360.0);
    }

    [Test]
    public async Task TypedTryGetLatest_DecodesCachedFrame(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();

        await Assert.That(monitor.TryGetLatest<BatteryFrame_1DB_AZE0>(out _)).IsFalse();

        await monitor.StartAsync(token);
        var raw = 720ul << 30; // 360.0 V, zero current
        transport.EnqueueIncoming($"1DB {string.Join(" ", BitConverter.GetBytes(raw).Select(b => b.ToString("X2")))}\r");
        await WaitForLatestAsync(monitor, 0x1DB, token);
        await monitor.StopAsync(token);

        await Assert.That(monitor.TryGetLatest<BatteryFrame_1DB_AZE0>(out var frame)).IsTrue();
        await Assert.That(frame.Voltage).IsEqualTo(360.0);
        await Assert.That(frame.Current).IsEqualTo(0.0);
    }

    [Test]
    public async Task Stop_EndsWithStoppedReason_AndRestoresRequestResponseMode(CancellationToken token)
    {
        var (transport, session, monitor) = CreateMonitor();
        await monitor.StartAsync(token);
        var stream = monitor.Subscribe(ReadOnlyMemory<int>.Empty, token);

        transport.EnqueueIncoming("1DB 01 00 00 00 00 00 00 00\r");
        await WaitForLatestAsync(monitor, 0x1DB, token);

        await monitor.StopAsync(token);

        await Assert.That(monitor.EndReason).IsEqualTo(MonitoringEndReason.Stopped);
        await Assert.That(session.CurrentMode).IsEqualTo(EcuCommunicationMode.RequestResponse);
        await Assert.That(monitor.IsRunning).IsFalse();

        // Stream completes normally after stop.
        var received = new List<RawCanFrame>();
        await foreach (var f in stream) received.Add(f);
        await Assert.That(received.Count).IsEqualTo(1);

        // A subscription taken after the monitor ended completes immediately.
        var post = new List<RawCanFrame>();
        await foreach (var f in monitor.Subscribe(ReadOnlyMemory<int>.Empty, token)) post.Add(f);
        await Assert.That(post.Count).IsEqualTo(0);
    }
}
