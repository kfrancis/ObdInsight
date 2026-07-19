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

    private static EcuContext ActiveMonitorContext(string? keepAlive = null, int keepAliveMs = 2000) => new()
    {
        Name = "EPS Monitor",
        TxHeader = "742",
        RxFilter = "",
        FlowControlHeader = "742",
        CommunicationMode = EcuCommunicationMode.ActiveMonitoring,
        MonitoringCommand = "ATMA",
        SessionActivationCommand = "1081",
        RequiresSessionActivation = true,
        KeepAliveCommand = keepAlive,
        KeepAliveIntervalMs = keepAliveMs,
        EnableHeaders = true,
        EnableAutoFormatting = false,
    };

    [Test]
    public async Task Start_WithActivationContext_ActivatesSessionBeforeMonitoring(CancellationToken token)
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var monitor = new CanMonitor(session, ActiveMonitorContext());

        // Suppress-positive activation: empty response with prompt is success.
        transport.Expect("1081", "\r>");
        transport.Expect("ATMA", "");

        await monitor.StartAsync(token);
        transport.EnqueueIncoming("002 01 02 03 04 05 06 07 08\r");
        await WaitForLatestAsync(monitor, 0x002, token);
        await monitor.StopAsync(token);

        var sent = transport.SentCommands.ToList();
        await Assert.That(sent).Contains("1081");
        await Assert.That(sent.IndexOf("1081") < sent.IndexOf("ATMA")).IsTrue();
        // Activation configured the EPS TX header before sending 1081.
        await Assert.That(sent.IndexOf("AT SH 742") < sent.IndexOf("1081")).IsTrue();
    }

    [Test]
    public async Task KeepAlive_PeriodicallySendsTesterPresent_AndResumesMonitoring(CancellationToken token)
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var monitor = new CanMonitor(session, ActiveMonitorContext(keepAlive: "3E80", keepAliveMs: 100));

        transport.Expect("1081", "\r>");
        // Monitoring re-enters after every keep-alive beat and TesterPresent repeats — use
        // canned responses instead of ordered script entries.
        transport.AutoRespond("ATMA", "");
        transport.AutoRespond("3E80", "\r>");

        await monitor.StartAsync(token);
        var stream = monitor.Subscribe(new[] { 0x002 }, token);

        transport.EnqueueIncoming("002 01 00 00 00 00 00 00 00\r");
        await WaitForLatestAsync(monitor, 0x002, token);

        // Keep-alive beat: TesterPresent sent, monitoring re-entered.
        while (!transport.SentCommands.Contains("3E80"))
            await Task.Delay(10, token);
        while (transport.SentCommands.Count(c => c == "ATMA") < 2)
            await Task.Delay(10, token);

        // Frames still flow after the cycle. (Re-enqueue while polling: a concurrent beat's
        // buffer drain can eat a frame that arrives exactly during the suspend window.)
        while (!(monitor.TryGetLatest(0x002, out var latest) && latest.Data.Span[0] == 0x02))
        {
            transport.EnqueueIncoming("002 02 00 00 00 00 00 00 00\r");
            await Task.Delay(20, token);
        }

        await monitor.StopAsync(token);
        await Assert.That(monitor.EndReason).IsEqualTo(MonitoringEndReason.Stopped);

        // Subscription survived the keep-alive cycles and saw frames from both windows.
        var received = new List<RawCanFrame>();
        await foreach (var f in stream) received.Add(f);
        await Assert.That(received.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(received[0].Data.Span[0]).IsEqualTo((byte)0x01);
        await Assert.That(received[^1].Data.Span[0]).IsEqualTo((byte)0x02);
    }

    [Test]
    public async Task Suspend_AllowsQueries_ResumePreservesSubscribers(CancellationToken token)
    {
        var (transport, session, monitor) = CreateMonitor();

        await monitor.StartAsync(token);
        var stream = monitor.Subscribe(new[] { 0x1DB }, token);

        transport.EnqueueIncoming("1DB 01 00 00 00 00 00 00 00\r");
        await WaitForLatestAsync(monitor, 0x1DB, token);

        // Suspend: monitoring halts, request/response works, nothing torn down.
        // Wire order: query during suspension, then ATMA again on resume.
        transport.Expect("010C", "41 0C 1A F8\r\r>");
        transport.Expect("ATMA", "");
        await using (await monitor.SuspendAsync(token))
        {
            await Assert.That(monitor.IsRunning).IsFalse();
            await Assert.That(session.CurrentMode).IsEqualTo(EcuCommunicationMode.RequestResponse);

            var lines = await session.QueryAsync("010C", token);
            await Assert.That(lines[0]).IsEqualTo("41 0C 1A F8");

            await Assert.That(monitor.EndReason).IsEqualTo(MonitoringEndReason.None);
        }

        // Resumed: loop running again, same subscription still receives frames.
        await Assert.That(monitor.IsRunning).IsTrue();
        transport.EnqueueIncoming("1DB 02 00 00 00 00 00 00 00\r");
        while (!(monitor.TryGetLatest(0x1DB, out var latest) && latest.Data.Span[0] == 0x02))
            await Task.Delay(10, token);

        await monitor.StopAsync(token);

        var received = new List<RawCanFrame>();
        await foreach (var f in stream) received.Add(f);
        await Assert.That(received.Count).IsEqualTo(2);
        await Assert.That(received[0].Data.Span[0]).IsEqualTo((byte)0x01);
        await Assert.That(received[1].Data.Span[0]).IsEqualTo((byte)0x02);
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

        // OVMS layout: current = 11-bit two's complement (byte0 + byte1[7..5]), 0.5 A/bit —
        // raw -32 = 0x7E0 => FC 00 => -16.0 A. Voltage = byte2 + byte3[7..6], 0.5 V/bit —
        // raw 720 => B4 00 => 360.0 V.
        transport.EnqueueIncoming("1DB FC 00 B4 00 00 00 00 00\r");
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
        // OVMS layout: voltage raw 720 in byte2 + byte3[7..6] => 360.0 V, zero current.
        transport.EnqueueIncoming("1DB 00 00 B4 00 00 00 00 00\r");
        await WaitForLatestAsync(monitor, 0x1DB, token);
        await monitor.StopAsync(token);

        await Assert.That(monitor.TryGetLatest<BatteryFrame_1DB_AZE0>(out var frame)).IsTrue();
        await Assert.That(frame.Voltage).IsEqualTo(360.0);
        await Assert.That(frame.Current).IsEqualTo(0.0);
    }

    [Test]
    public async Task FilterRotation_CyclesHardwareFilters_AndAccumulatesCache(CancellationToken token)
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var monitor = new CanMonitor(session, EcuContext.NissanLeafHvbatMonitor)
        {
            RestartDelay = TimeSpan.Zero,
            FilterRotation =
            [
                new CanFilterWindow("700", "100", TimeSpan.FromMilliseconds(150)),
                new CanFilterWindow("700", "500", TimeSpan.FromMilliseconds(150)),
            ],
        };
        transport.AutoRespond("ATMA", ""); // one enter per window, unbounded

        await monitor.StartAsync(token);

        // Window 1 (0x1xx): hardware filter applied, battery frame arrives.
        while (!transport.SentCommands.Contains("AT CF 100"))
            await Task.Delay(10, token);
        while (!monitor.TryGetLatest(0x1DB, out _))
        {
            transport.EnqueueIncoming("1DB 01 00 00 00 00 00 00 00\r");
            await Task.Delay(20, token);
        }

        // Rotation: window 2 (0x5xx) filter applied, HVAC frame arrives; 0x1DB stays cached.
        while (!transport.SentCommands.Contains("AT CF 500"))
            await Task.Delay(10, token);
        while (!monitor.TryGetLatest(0x54C, out _))
        {
            transport.EnqueueIncoming("54C 01 00 00 00 00 00 00 00\r");
            await Task.Delay(20, token);
        }

        await Assert.That(monitor.TryGetLatest(0x1DB, out _)).IsTrue();
        await Assert.That(monitor.TryGetLatest(0x54C, out _)).IsTrue();
        await Assert.That(transport.SentCommands).Contains("AT CM 700");
        await Assert.That(transport.SentCommands.Count(c => c == "ATMA")).IsGreaterThanOrEqualTo(2);

        await monitor.StopAsync(token);
        await Assert.That(monitor.EndReason).IsEqualTo(MonitoringEndReason.Stopped);
    }

    [Test]
    public async Task BufferFull_WithResidualPromptBytes_RestartSurvives(CancellationToken token)
    {
        // Hardware regression (2026-07-18): BUFFER FULL leaves a stray "\r>" in the stream.
        // Without buffer clearing on re-enter, the AT sequence desyncs off-by-one and the
        // monitor dies with PromptDetected. The restart must survive residual bytes.
        var (transport, _, monitor) = CreateMonitor();
        transport.Expect("ATMA", ""); // re-enter after BUFFER FULL

        await monitor.StartAsync(token);
        transport.EnqueueIncoming("1DB 01 00 00 00 00 00 00 00\r");
        await WaitForLatestAsync(monitor, 0x1DB, token);

        // Adapter overflows AND dumps its prompt into the stream.
        transport.EnqueueIncoming("BUFFER FULL\r\r>");

        while (transport.SentCommands.Count(c => c == "ATMA") < 2)
            await Task.Delay(10, token);

        // Monitor is alive after the restart and still delivers frames.
        while (!(monitor.TryGetLatest(0x1DB, out var latest) && latest.Data.Span[0] == 0x02))
        {
            transport.EnqueueIncoming("1DB 02 00 00 00 00 00 00 00\r");
            await Task.Delay(20, token);
        }

        await Assert.That(monitor.IsRunning).IsTrue();
        await monitor.StopAsync(token);
        await Assert.That(monitor.EndReason).IsEqualTo(MonitoringEndReason.Stopped);
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
