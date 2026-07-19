using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;
using ObdInsight.Telemetry;
using ConnectionState = ObdInsight.Core.Communication.Elm327.ConnectionState;

namespace OdbTestApp.Tests.Telemetry;

/// <summary>
/// Roadmap B10 acceptance: transport death mid-drive over the fully composed stack
/// (ReconnectingElmTransport → ElmSession → RetryingElmSession → LeafAze0CommandSet →
/// TelemetrySession). The telemetry stream pauses and resumes on the SAME subscription,
/// and connection-state transitions surface through ITelemetrySession.
/// </summary>
[Timeout(60_000)]
public class TelemetryResilienceTests
{
    [Test]
    public async Task TransportDeathMidDrive_TelemetryResumes_StatesSurface(CancellationToken token)
    {
        var transports = new List<ReplayElmTransport>();
        ReplayElmTransport Factory()
        {
            var t = new ReplayElmTransport();
            t.AutoRespond("ATMA", "");
            t.AutoRespond("2101", LeafGoldenData.GoldenGroup01Lines.AsElmResponse());
            t.AutoRespond("2104", LeafGoldenData.GoldenGroup04Lines.AsElmResponse());
            lock (transports)
            {
                transports.Add(t);
            }

            return t;
        }

        var resilient = new ReconnectingElmTransport(Factory, new ReconnectOptions
        {
            MaxAttempts = 5,
            InitialDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(10),
        });
        await resilient.OpenAsync(token);

        var session = new ElmSession(new ElmFramer(resilient));
        var retrying = new RetryingElmSession(session, new QueryRetryOptions
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.FromMilliseconds(10),
        });
        var commands = new LeafAze0CommandSet(retrying);
        commands.Monitor.RestartDelay = TimeSpan.Zero;

        var subscription = new TelemetrySubscription(new Dictionary<TelemetrySignal, CadenceTier>
        {
            [TelemetrySignal.StateOfCharge] = CadenceTier.High,
            [TelemetrySignal.VehicleSpeed] = CadenceTier.High,
        });
        var options = new TelemetrySessionOptions
        {
            HighPeriod = TimeSpan.FromMilliseconds(150),
            CacheReadTimeout = TimeSpan.FromMilliseconds(300),
        };
        await using var telemetry = TelemetrySession.Create(
            commands, subscription, options, connectionState: resilient);

        var observedStates = new List<ConnectionState>();
        telemetry.ConnectionStateChanged += (_, e) =>
        {
            lock (observedStates)
            {
                observedStates.Add(e.NewState);
            }
        };

        // Keep the current (latest) transport fed with broadcast speed frames.
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pump = Task.Run(async () =>
        {
            while (!pumpCts.Token.IsCancellationRequested)
            {
                ReplayElmTransport current;
                lock (transports)
                {
                    current = transports[^1];
                }

                if (commands.Monitor.IsRunning)
                {
                    try
                    {
                        current.EnqueueIncoming("284 00 00 00 00 0A 00 76 FC\r");
                    }
                    catch (IOException)
                    {
                        // Current transport just died — next iteration feeds its successor.
                    }
                }

                await Task.Delay(20, pumpCts.Token);
            }
        }, pumpCts.Token);

        await telemetry.StartAsync(token);

        // Phase 1: healthy stream — wait for a SOC sample.
        await using var batches = telemetry.Batches(token).GetAsyncEnumerator(token);
        await WaitForSocSampleAsync(batches);
        await Assert.That(telemetry.ConnectionState).IsEqualTo(ConnectionState.Connected);

        // Kill the live transport mid-drive.
        ReplayElmTransport victim;
        lock (transports)
        {
            victim = transports[^1];
        }

        victim.SimulateConnectionLost();

        // Phase 2: the SAME batch subscription keeps producing after reconnection.
        await WaitForSocSampleAsync(batches);

        pumpCts.Cancel();
        try { await pump; } catch (OperationCanceledException) { }

        // Reconnected onto a replacement transport, and the UDS path really used it.
        List<ReplayElmTransport> snapshotTransports;
        lock (transports)
        {
            snapshotTransports = [.. transports];
        }

        await Assert.That(snapshotTransports.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(snapshotTransports[^1].SentCommands).Contains("2101");

        // State transitions surfaced through the telemetry session, in order.
        List<ConnectionState> states;
        lock (observedStates)
        {
            states = [.. observedStates];
        }

        await Assert.That(states).Contains(ConnectionState.Reconnecting);
        await Assert.That(states.Last()).IsEqualTo(ConnectionState.Connected);
        await Assert.That(telemetry.ConnectionState).IsEqualTo(ConnectionState.Connected);

        await telemetry.StopAsync(token);
        await commands.Monitor.StopAsync(token);
        await resilient.DisposeAsync();
    }

    private static async Task WaitForSocSampleAsync(
        IAsyncEnumerator<TelemetrySampleBatch> batches)
    {
        while (await batches.MoveNextAsync())
        {
            if (batches.Current.Samples.Any(s =>
                    s.Signal == TelemetrySignal.StateOfCharge && !s.Value.IsEmpty))
            {
                return;
            }
        }

        throw new InvalidOperationException("Batch stream ended before a SOC sample arrived.");
    }
}
