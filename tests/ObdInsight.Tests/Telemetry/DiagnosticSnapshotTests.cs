using ObdInsight.Core.Vehicles;
using ObdInsight.Telemetry;

namespace ObdInsight.Tests.Telemetry;

public class DiagnosticSnapshotTests
{
    [Test]
    public async Task AbsentCapability_IsNotAFailedRead()
    {
        await using var session = new TelemetrySession([]);
        var snapshot = await session.GetSnapshotAsync();
        await Assert.That(snapshot.DiagnosticTroubleCodes).IsNull();
    }

    [Test]
    public async Task Snapshot_PreservesIndependentOutcomesAndResponderEvidence()
    {
        var result = new DtcReadResult
        {
            Stored = DtcModeResult.FromResponses([new(0x7E8, ["P0143"]), new(0x7E9, null)]),
            Pending = DtcModeResult.FromResponses([new(0x7E8, [])])
        };
        await using var session = new TelemetrySession([], dtc: new Diagnostics(_ => result));
        var snapshot = await session.GetSnapshotAsync();
        await Assert.That(snapshot.DiagnosticTroubleCodes!.Stored.Status).IsEqualTo(DtcReadStatus.Partial);
        await Assert.That(snapshot.DiagnosticTroubleCodes.Stored.Codes).IsNull();
        await Assert.That(snapshot.DiagnosticTroubleCodes.Stored.Responders[0].Codes!).IsEquivalentTo(["P0143"]);
        await Assert.That(snapshot.DiagnosticTroubleCodes.Pending.Status).IsEqualTo(DtcReadStatus.Succeeded);
        await Assert.That(snapshot.DiagnosticTroubleCodes.Pending.Codes!).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AlternativeCapabilityOperationalFailure_IsNotAbsentCapability(bool timeout)
    {
        await using var session = new TelemetrySession([], dtc: new Diagnostics(_ =>
            throw (timeout ? new TimeoutException() : new IOException())));
        var snapshot = await session.GetSnapshotAsync();
        var expected = timeout ? DtcReadStatus.Timeout : DtcReadStatus.QueryFailed;
        await Assert.That(snapshot.DiagnosticTroubleCodes!.Stored.Status).IsEqualTo(expected);
        await Assert.That(snapshot.DiagnosticTroubleCodes.Pending.Status).IsEqualTo(expected);
        await Assert.That(snapshot.DiagnosticTroubleCodes.Stored.Codes).IsNull();
    }

    [Test]
    public async Task Cancellation_PropagatesAndReleasesSnapshotGate()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;
        await using var session = new TelemetrySession([], dtc: new Diagnostics(ct =>
        {
            if (calls++ == 0)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return new DtcReadResult
            {
                Stored = DtcModeResult.Failed(DtcReadStatus.NoData),
                Pending = DtcModeResult.Failed(DtcReadStatus.NoData)
            };
        }));
        await Assert.That(async () => await session.GetSnapshotAsync(cts.Token)).Throws<OperationCanceledException>();
        var snapshot = await session.GetSnapshotAsync();
        await Assert.That(snapshot.DiagnosticTroubleCodes!.Stored.Status).IsEqualTo(DtcReadStatus.NoData);
    }

    [Test]
    public async Task ProgrammingError_IsNotRelabeledAsMissingDiagnostics()
    {
        await using var session = new TelemetrySession([], dtc: new Diagnostics(_ => throw new InvalidOperationException()));
        await Assert.That(async () => await session.GetSnapshotAsync()).Throws<InvalidOperationException>();
    }

    private sealed class Diagnostics(Func<CancellationToken, DtcReadResult> read) : IDiagnosticTroubleCodes
    {
        public ValueTask<DtcReadResult> GetDtcsAsync(CancellationToken ct = default) => ValueTask.FromResult(read(ct));
    }
}
