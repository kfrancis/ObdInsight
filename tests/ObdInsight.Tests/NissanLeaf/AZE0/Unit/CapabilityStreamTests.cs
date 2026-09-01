using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;
using ObdInsight.Simulation;

namespace OdbTestApp.Tests.NissanLeaf.AZE0.Unit;

/// <summary>
///     Streaming members on the broadcast capability interfaces (docs/STREAMING_MONITOR_DESIGN.md
///     P4): coalesce-on-any-contributing-frame over the shared monitor's cache, eager registration,
///     optional throttle, and survival across UDS arbitration windows. Driven through
///     <see cref="ReplayElmTransport" /> — no hardware.
/// </summary>
/// <remarks>
///     Every test here creates the stream first, then starts the monitor, enqueues frames, stops,
///     and only then drains the stream. That ordering is the point: registration happens when the
///     stream is created, so nothing enqueued in between is lost — and it keeps the tests free of
///     reader-thread timing.
/// </remarks>
[Timeout(30_000)]
public class CapabilityStreamTests
{
    // 0x54C: ClimateControlOn = bit 10, AcOn = bit 11 -> byte1 = 0x0C sets both.
    private const string Frame54CClimateOn = "54C 00 0C 00 00 00 00 00 00\r";

    // 0x54B: FanSpeed = bits 35-39 -> byte4 0x20 decodes as 4.
    private const string Frame54BFanSpeed = "54B 00 00 00 00 20 00 00 00\r";
    private const int Frame54BExpectedFanSpeed = 4;

    // 0x390: ChargeStatus = bits 46-51 -> byte6 bit0 set decodes as 4 ("charging").
    private const string Frame390Charging = "390 00 00 00 00 00 00 01 00\r";

    private static (ReplayElmTransport Transport, ElmSession Session, CanMonitor Monitor) CreateMonitor()
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var monitor = new CanMonitor(session, EcuContext.NissanLeafHvbatMonitor) { RestartDelay = TimeSpan.Zero };
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

    /// <summary>
    ///     Pulls exactly one emission, asserting the stream had one to give. Emissions are built
    ///     when the consumer pulls, so stepping the enumerator between frames is what makes the
    ///     coalescing observable (drain-at-the-end would only ever show the final cache state).
    /// </summary>
    private static async Task<T> NextAsync<T>(IAsyncEnumerator<T> enumerator)
    {
        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        return enumerator.Current;
    }

    private static async Task<List<T>> DrainAsync<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream)
        {
            items.Add(item);
        }

        return items;
    }

    [Test]
    public async Task StreamStatus_CoalescesAcrossContributingFrames(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        await using var owned = monitor;
        var hvac = new LeafAze0Hvac(monitor);

        var stream = hvac.StreamStatusAsync(ct: token);
        await monitor.StartAsync(token);
        await using var emissions = stream.GetAsyncEnumerator(token);

        transport.EnqueueIncoming(Frame54CClimateOn);
        await WaitForLatestAsync(monitor, 0x54C, token);

        // Only 0x54C has been seen, so fan speed is still absent.
        var first = await NextAsync(emissions);
        await Assert.That(first.ClimateControlOn).IsTrue();
        await Assert.That(first.AcOn).IsTrue();
        await Assert.That(first.FanSpeed).IsNull();

        transport.EnqueueIncoming(Frame54BFanSpeed);
        await WaitForLatestAsync(monitor, 0x54B, token);

        // Triggered by 0x54B, but carries 0x54C's fields from the cache.
        var second = await NextAsync(emissions);
        await Assert.That(second.FanSpeed).IsEqualTo(Frame54BExpectedFanSpeed);
        await Assert.That(second.ClimateControlOn).IsTrue();
        await Assert.That(second.AcOn).IsTrue();

        await monitor.StopAsync(token);
        await Assert.That(await emissions.MoveNextAsync()).IsFalse();
    }

    [Test]
    public async Task StreamStatus_RegistersEagerly_FramesBeforeIterationSurvive(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        await using var owned = monitor;
        var hvac = new LeafAze0Hvac(monitor);

        // Created but not iterated: registration must already have happened, or the frame below
        // is lost. (An async iterator would defer registration to the first MoveNext.)
        var stream = hvac.StreamStatusAsync(ct: token);

        await monitor.StartAsync(token);
        transport.EnqueueIncoming(Frame54CClimateOn);
        await WaitForLatestAsync(monitor, 0x54C, token);
        await monitor.StopAsync(token);

        var emissions = await DrainAsync(stream);

        await Assert.That(emissions.Count).IsEqualTo(1);
        await Assert.That(emissions[0].ClimateControlOn).IsTrue();
    }

    [Test]
    public async Task StreamStatus_MinInterval_SkipsEmissionsInsideTheWindow(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        await using var owned = monitor;
        var hvac = new LeafAze0Hvac(monitor);

        // A 10-second window: after the first emission every later frame is skipped.
        var stream = hvac.StreamStatusAsync(TimeSpan.FromSeconds(10), token);
        await monitor.StartAsync(token);
        await using var emissions = stream.GetAsyncEnumerator(token);

        transport.EnqueueIncoming(Frame54CClimateOn);
        await WaitForLatestAsync(monitor, 0x54C, token);

        var first = await NextAsync(emissions);
        await Assert.That(first.ClimateControlOn).IsTrue();

        // Inside the window: this frame updates the cache but must not produce an emission.
        transport.EnqueueIncoming(Frame54BFanSpeed);
        await WaitForLatestAsync(monitor, 0x54B, token);

        await monitor.StopAsync(token);
        await Assert.That(await emissions.MoveNextAsync()).IsFalse();
    }

    [Test]
    public async Task StreamStatus_SurvivesSuspensionForUdsQuery(CancellationToken token)
    {
        var (transport, session, monitor) = CreateMonitor();
        await using var owned = monitor;
        var hvac = new LeafAze0Hvac(monitor);

        var stream = hvac.StreamStatusAsync(ct: token);
        await monitor.StartAsync(token);
        await using var emissions = stream.GetAsyncEnumerator(token);

        transport.EnqueueIncoming(Frame54CClimateOn);
        await WaitForLatestAsync(monitor, 0x54C, token);
        await Assert.That((await NextAsync(emissions)).ClimateControlOn).IsTrue();

        // Wire order: the UDS query runs while suspended, then ATMA re-enters monitoring.
        transport.Expect("010C", "41 0C 1A F8\r\r>");
        transport.Expect("ATMA", "");
        await using (await monitor.SuspendAsync(token))
        {
            await Assert.That(monitor.IsRunning).IsFalse();
            var lines = await session.QueryAsync("010C", token);
            await Assert.That(lines[0]).IsEqualTo("41 0C 1A F8");
        }

        // The subscription outlived the suspension: post-resume frames still reach it.
        await Assert.That(monitor.IsRunning).IsTrue();
        transport.EnqueueIncoming(Frame54BFanSpeed);
        await WaitForLatestAsync(monitor, 0x54B, token);

        var afterResume = await NextAsync(emissions);
        await Assert.That(afterResume.FanSpeed).IsEqualTo(Frame54BExpectedFanSpeed);
        await Assert.That(afterResume.ClimateControlOn).IsTrue();

        await monitor.StopAsync(token);
        await Assert.That(await emissions.MoveNextAsync()).IsFalse();
    }

    [Test]
    public async Task StreamStatus_UnrelatedFrames_DoNotEmit(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        await using var owned = monitor;
        var hvac = new LeafAze0Hvac(monitor);

        var stream = hvac.StreamStatusAsync(ct: token);
        await monitor.StartAsync(token);

        // 0x1DB is a battery frame — not one of HVAC's contributing IDs.
        transport.EnqueueIncoming("1DB 00 00 B4 00 00 00 00 00\r");
        await WaitForLatestAsync(monitor, 0x1DB, token);
        await monitor.StopAsync(token);

        var emissions = await DrainAsync(stream);
        await Assert.That(emissions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task StreamStatus_MonitorAlreadyEnded_CompletesWithoutRestarting(CancellationToken token)
    {
        var (_, session, monitor) = CreateMonitor();
        await using var owned = monitor;
        var abs = new LeafAze0Abs(monitor);

        await monitor.StartAsync(token);
        await monitor.StopAsync(token);

        // Subscribing to a monitor that already ended must complete the stream, not silently
        // re-enter monitoring behind the caller's back.
        var emissions = await DrainAsync(abs.StreamStatusAsync(ct: token));

        await Assert.That(emissions.Count).IsEqualTo(0);
        await Assert.That(monitor.IsRunning).IsFalse();
        await Assert.That(monitor.EndReason).IsEqualTo(MonitoringEndReason.Stopped);
        await Assert.That(session.CurrentMode).IsEqualTo(EcuCommunicationMode.RequestResponse);
    }

    [Test]
    public async Task StreamChargingStatus_EmitsOnChargerFrame(CancellationToken token)
    {
        var (transport, _, monitor) = CreateMonitor();
        await using var owned = monitor;
        var charger = new LeafAze0Charger(monitor);

        var stream = charger.StreamChargingStatusAsync(ct: token);
        await monitor.StartAsync(token);

        transport.EnqueueIncoming(Frame390Charging);
        await WaitForLatestAsync(monitor, 0x390, token);
        await monitor.StopAsync(token);

        var emissions = await DrainAsync(stream);
        await Assert.That(emissions.Count).IsEqualTo(1);
        await Assert.That(emissions[0]).IsNotNull();
        await Assert.That(emissions[0]!.IsCharging).IsTrue();
        await Assert.That(emissions[0]!.IsPluggedIn).IsTrue();
    }
}
