using System.Runtime.CompilerServices;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Simulation;

namespace ObdInsight.Tests.Elm327;

/// <summary>
///     Expert opt-in per-request retry policy. Owned physical recovery is protected
///     by VehicleConnectionTests, not by replaying operations on a new byte stream.
/// </summary>
[Timeout(30_000)]
public class ResilienceTests
{
    [Test]
    public async Task RetryPolicy_TransientIoException_RetriesThenSucceeds(CancellationToken token)
    {
        var inner = new FlakySession(2);
        var session = new RetryingElmSession(inner,
            new QueryRetryOptions { MaxAttempts = 3, RetryDelay = TimeSpan.FromMilliseconds(1) });

        var lines = await session.QueryAsync("2101", token);

        await Assert.That(lines).Contains("OK");
        await Assert.That(inner.Attempts).IsEqualTo(3);
    }

    [Test]
    public async Task RetryPolicy_AttemptsExhausted_Throws(CancellationToken token)
    {
        var inner = new FlakySession(99);
        var session = new RetryingElmSession(inner,
            new QueryRetryOptions { MaxAttempts = 3, RetryDelay = TimeSpan.FromMilliseconds(1) });

        await Assert.That(async () => await session.QueryAsync("2101", token))
            .Throws<IOException>();
        await Assert.That(inner.Attempts).IsEqualTo(3);
    }

    [Test]
    public async Task RetryPolicy_Cancellation_NeverRetried(CancellationToken token)
    {
        var inner = new FlakySession(0) { ThrowOce = true };
        var session = new RetryingElmSession(inner,
            new QueryRetryOptions { MaxAttempts = 3, RetryDelay = TimeSpan.FromMilliseconds(1) });

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
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask SetEcuContextAsync(EcuContext context, CancellationToken ct) =>
            ValueTask.CompletedTask;
    }
}
