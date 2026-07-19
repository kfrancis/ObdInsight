using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Simulation;

namespace OdbTestApp.Tests.Elm327;

/// <summary>
/// Roadmap B10 (docs/RESILIENCE_DESIGN.md): reconnecting transport decorator +
/// per-request retry policy.
/// </summary>
[Timeout(30_000)]
public class ResilienceTests
{
    private static readonly ReconnectOptions FastReconnect = new()
    {
        MaxAttempts = 3,
        InitialDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(5),
    };

    [Test]
    public async Task Reconnect_TransportDies_IoResumesOnReplacement(CancellationToken token)
    {
        var transports = new List<ReplayElmTransport>();
        ReplayElmTransport Factory()
        {
            var t = new ReplayElmTransport();
            t.AutoRespond("ATI", "ELM327 v1.5\r\r>");
            transports.Add(t);
            return t;
        }

        var states = new List<ConnectionState>();
        var resilient = new ReconnectingElmTransport(Factory, FastReconnect);
        resilient.StateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add(e.NewState);
            }
        };

        await resilient.OpenAsync(token);
        await Assert.That(resilient.State).IsEqualTo(ConnectionState.Connected);

        // Round-trip through transport #1.
        var framer = new ElmFramer(resilient);
        var reply = await framer.SendAndReadFrameAsync("ATI", TimeSpan.FromSeconds(2), token);
        await Assert.That(reply).Contains("ELM327");

        // Kill #1 — the proactive ConnectionLost event triggers reconnection.
        transports[0].SimulateConnectionLost();

        // I/O issued during/after the outage completes against transport #2.
        var reply2 = await framer.SendAndReadFrameAsync("ATI", TimeSpan.FromSeconds(5), token);
        await Assert.That(reply2).Contains("ELM327");
        await Assert.That(transports.Count).IsEqualTo(2);
        await Assert.That(transports[1].SentCommands).Contains("ATI");

        // State transitions observed in order.
        List<ConnectionState> snapshot;
        lock (states) snapshot = [.. states];
        await Assert.That(snapshot).Contains(ConnectionState.Reconnecting);
        await Assert.That(snapshot.Last()).IsEqualTo(ConnectionState.Connected);
        await Assert.That(snapshot.IndexOf(ConnectionState.Reconnecting))
            .IsLessThan(snapshot.LastIndexOf(ConnectionState.Connected));

        await resilient.DisposeAsync();
    }

    [Test]
    public async Task Reconnect_FactoryKeepsFailing_EndsLost_IoThrows(CancellationToken token)
    {
        var first = new ReplayElmTransport();
        var callCount = 0;
        IElmTransport Factory()
        {
            callCount++;
            if (callCount == 1)
            {
                return first;
            }

            throw new IOException("no adapter in range");
        }

        var resilient = new ReconnectingElmTransport(Factory, FastReconnect);
        var sawLost = new TaskCompletionSource();
        resilient.StateChanged += (_, e) =>
        {
            if (e.NewState == ConnectionState.Lost)
            {
                sawLost.TrySetResult();
            }
        };

        await resilient.OpenAsync(token);
        first.SimulateConnectionLost();

        await sawLost.Task.WaitAsync(token);
        await Assert.That(resilient.State).IsEqualTo(ConnectionState.Lost);
        // 1 initial + 3 reconnect attempts.
        await Assert.That(callCount).IsEqualTo(4);

        var buffer = new byte[8];
        await Assert.That(async () => await resilient.ReadAsync(buffer, token))
            .Throws<IOException>();

        await resilient.DisposeAsync();
    }

    [Test]
    public async Task RetryPolicy_TransientIoException_RetriesThenSucceeds(CancellationToken token)
    {
        var inner = new FlakySession(failuresBeforeSuccess: 2);
        var session = new RetryingElmSession(inner, new QueryRetryOptions
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.FromMilliseconds(1),
        });

        var lines = await session.QueryAsync("2101", token);

        await Assert.That(lines).Contains("OK");
        await Assert.That(inner.Attempts).IsEqualTo(3);
    }

    [Test]
    public async Task RetryPolicy_AttemptsExhausted_Throws(CancellationToken token)
    {
        var inner = new FlakySession(failuresBeforeSuccess: 99);
        var session = new RetryingElmSession(inner, new QueryRetryOptions
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.FromMilliseconds(1),
        });

        await Assert.That(async () => await session.QueryAsync("2101", token))
            .Throws<IOException>();
        await Assert.That(inner.Attempts).IsEqualTo(3);
    }

    [Test]
    public async Task RetryPolicy_Cancellation_NeverRetried(CancellationToken token)
    {
        var inner = new FlakySession(failuresBeforeSuccess: 0) { ThrowOce = true };
        var session = new RetryingElmSession(inner, new QueryRetryOptions
        {
            MaxAttempts = 3,
            RetryDelay = TimeSpan.FromMilliseconds(1),
        });

        await Assert.That(async () => await session.QueryAsync("2101", token))
            .Throws<OperationCanceledException>();
        await Assert.That(inner.Attempts).IsEqualTo(1);
    }

    /// <summary>Minimal IElmSession stub: fails N QueryAsync calls, then succeeds.</summary>
    private sealed class FlakySession : IElmSession
    {
        private readonly int _failuresBeforeSuccess;

        public FlakySession(int failuresBeforeSuccess) => _failuresBeforeSuccess = failuresBeforeSuccess;

        public int Attempts { get; private set; }

        public bool ThrowOce { get; init; }

        public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(4);
        public EcuCommunicationMode CurrentMode => EcuCommunicationMode.RequestResponse;
        public bool EnableDebugLogging { get; set; }
        public int MaxConsecutiveFailures { get; set; } = 3;
        public TimeSpan ProtocolDetectionTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public MonitoringEndReason LastMonitoringEndReason => MonitoringEndReason.None;

        public ValueTask<string[]> QueryAsync(string obdCommand, CancellationToken ct)
        {
            Attempts++;
            if (ThrowOce)
            {
                throw new OperationCanceledException();
            }

            if (Attempts <= _failuresBeforeSuccess)
            {
                throw new IOException("flaky");
            }

            return ValueTask.FromResult(new[] { "OK" });
        }

        public ValueTask<string[]> QueryAsync(string obdCommand, EcuContext context, CancellationToken ct) =>
            QueryAsync(obdCommand, ct);

        public ValueTask<bool> ActivateSessionAsync(EcuContext context, CancellationToken ct) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> SendKeepAliveAsync(EcuContext context, CancellationToken ct) =>
            ValueTask.FromResult(true);

        public ValueTask EnterMonitoringModeAsync(EcuContext context, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask ExitMonitoringModeAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask InitializeAndLockAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<RawCanFrame> MonitorFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask SetEcuContextAsync(EcuContext context, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }
}
