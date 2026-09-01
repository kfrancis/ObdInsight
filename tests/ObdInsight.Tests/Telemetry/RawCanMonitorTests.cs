using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Simulation;
using ObdInsight.Telemetry;

namespace ObdInsight.Tests.Telemetry;

/// <summary>
///     Lifecycle + parsing coverage for <see cref="IRawCanMonitor" />: start/stream/stop over the
///     replay transport, timestamps attached per frame, independent of the ITelemetrySession path.
/// </summary>
[Timeout(30_000)]
public class RawCanMonitorTests
{
    private static (ReplayElmTransport Transport, RawCanMonitor Monitor) CreateMonitor()
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var monitor = new RawCanMonitor(session);
        // "ATMA" must stay silent — monitoring streams instead of prompting.
        transport.Expect("ATMA", "");
        return (transport, monitor);
    }

    [Test]
    public async Task MonitorRawFramesAsync_DeliversFramesWithTimestamps(CancellationToken token)
    {
        var (transport, monitor) = CreateMonitor();
        await monitor.StartAsync(token);

        var received = new List<RawCanFrame>();
        // Call MonitorRawFramesAsync (registers with the shared monitor) BEFORE any frame is
        // enqueued and BEFORE spawning the reader Task — calling it inside Task.Run races frame
        // delivery under load, since the Task may not be scheduled before frames arrive.
        var stream = monitor.MonitorRawFramesAsync(token);
        var readTask = Task.Run(async () =>
        {
            await foreach (var frame in stream)
            {
                received.Add(frame);
                if (received.Count >= 2)
                {
                    break;
                }
            }
        }, token);

        var before = DateTimeOffset.UtcNow;
        transport.EnqueueIncoming("1DB 01 02 03 04 05 06 07 08\r");
        transport.EnqueueIncoming("18DAF110 0A 0B\r"); // 29-bit ID alongside an 11-bit one
        await readTask.WaitAsync(token);
        var after = DateTimeOffset.UtcNow;

        await monitor.StopAsync(token);

        await Assert.That(received.Count).IsEqualTo(2);
        await Assert.That(received[0].CanId).IsEqualTo(0x1DB);
        await Assert.That(received[0].Data)
            .IsEquivalentTo(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 });
        await Assert.That(received[1].CanId).IsEqualTo(0x18DAF110);
        await Assert.That(received[1].Data).IsEquivalentTo(new byte[] { 0x0A, 0x0B });
        foreach (var frame in received)
        {
            await Assert.That(frame.Timestamp >= before && frame.Timestamp <= after).IsTrue();
        }
    }

    [Test]
    public async Task StartStop_TogglesIsRunning_AndReturnsAdapterToRequestResponse(CancellationToken token)
    {
        var (transport, monitor) = CreateMonitor();

        await Assert.That(monitor.IsRunning).IsFalse();

        await monitor.StartAsync(token);
        await Assert.That(monitor.IsRunning).IsTrue();
        await Assert.That(transport.SentCommands).Contains("ATMA");

        await monitor.StopAsync(token);
        await Assert.That(monitor.IsRunning).IsFalse();
    }

    [Test]
    public async Task StopAsync_CompletesInFlightStream(CancellationToken token)
    {
        var (transport, monitor) = CreateMonitor();
        await monitor.StartAsync(token);

        var received = new List<RawCanFrame>();
        var stream = monitor.MonitorRawFramesAsync(token);
        var readTask = Task.Run(async () =>
        {
            await foreach (var frame in stream)
            {
                received.Add(frame);
            }
        }, token);

        transport.EnqueueIncoming("1DB 01 00 00 00 00 00 00 00\r");
        while (received.Count == 0)
        {
            await Task.Delay(10, token);
        }

        await monitor.StopAsync(token);
        await readTask.WaitAsync(token);

        await Assert.That(received.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeAsync_StopsMonitoring(CancellationToken token)
    {
        var (_, monitor) = CreateMonitor();
        await monitor.StartAsync(token);

        await monitor.DisposeAsync();

        await Assert.That(monitor.IsRunning).IsFalse();
    }
}
