using System.Runtime.CompilerServices;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles;

namespace ObdInsight.Tests.NissanLeaf.AZE0.Unit;

public class DtcEvidenceTests
{
    [Test]
    [Arguments("7E8054300")]
    [Arguments("7E8024300F")]
    [Arguments("7E80443020143")]
    [Arguments("7E80443000001")]
    [Arguments("7E80443010000")]
    [Arguments("7E8024700")]
    [Arguments("7E8037F0311")]
    [Arguments("7E81008430301430196")]
    [Arguments("7E8210A800000000000")]
    [Arguments("7E80243GG")]
    [Arguments("130024300")]
    [Arguments("unrecognized adapter reply")]
    public async Task InvalidReply_CannotMeanClean(string line)
    {
        var result = await Read(line);
        await Assert.That(result.Stored.Status).IsEqualTo(DtcReadStatus.InvalidResponse);
        await Assert.That(result.Stored.Codes).IsNull();
        await Assert.That(result.Pending.Status).IsEqualTo(DtcReadStatus.Succeeded);
    }

    [Test]
    [Arguments("7E8220A800000000000")]
    [Arguments("7E9210A800000000000")]
    public async Task BadSequenceOrWrongResponder_IsNotReassembled(string continuation)
    {
        var result = await Read("7E81008430301430196", continuation);
        await Assert.That(result.Stored.Status).IsEqualTo(DtcReadStatus.InvalidResponse);
        await Assert.That(result.Stored.Codes).IsNull();
    }

    [Test]
    public async Task PartialRead_RetainsEvidenceWithoutSuccessfulAggregate()
    {
        var result = await Read("7E80443010143", "7E9054300");
        await Assert.That(result.Stored.Status).IsEqualTo(DtcReadStatus.Partial);
        await Assert.That(result.Stored.Codes).IsNull();
        await Assert.That(result.Stored.Responders.Single(r => r.CanId == 0x7E8).Codes!).IsEquivalentTo(["P0143"]);
        await Assert.That(result.Stored.Responders.Single(r => r.CanId == 0x7E9).Codes).IsNull();
    }

    [Test]
    public async Task InterleavedResponders_KeepTheirOwnPayloadsAndDeduplicateCodes()
    {
        var result = await Read("7E81008430301430196", "7E90443010143", "7E8210A800000000000");
        await Assert.That(result.Stored.Status).IsEqualTo(DtcReadStatus.Succeeded);
        await Assert.That(result.Stored.Codes!).IsEquivalentTo(["P0143", "P0196", "P0A80"]);
        await Assert.That(result.Stored.Responders.Single(r => r.CanId == 0x7E9).Codes!).IsEquivalentTo(["P0143"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MixedNoDataAndValidReply_IsPartialRegardlessOfOrdering(bool reverse)
    {
        var lines = new[] { "NO DATA", "7E8024300" };
        var result = await Read(reverse ? lines.Reverse().ToArray() : lines);
        await Assert.That(result.Stored.Status).IsEqualTo(DtcReadStatus.Partial);
        await Assert.That(result.Stored.Codes).IsNull();
    }

    [Test]
    public async Task DuplicateFrame_DoesNotOverwriteInvalidEvidence()
    {
        var result = await Read("7E8024300", "7E8024300");
        await Assert.That(result.Stored.Status).IsEqualTo(DtcReadStatus.InvalidResponse);
        await Assert.That(result.Stored.Codes).IsNull();
    }

    [Test]
    public async Task ConsecutiveSequence_WrapsAtSixteen()
    {
        // 60 codes require 17 CFs, crossing sequence F -> 0 -> 1.
        var payload = new List<byte> { 0x43, 60 };
        for (var i = 0; i < 60; i++) payload.AddRange([0x01, 0x43]);
        var lines = new List<string> { "7E8107A" + Convert.ToHexString(payload.Take(6).ToArray()) };
        var sequence = 1;
        for (var offset = 6; offset < payload.Count; offset += 7)
        {
            var bytes = new byte[8];
            bytes[0] = (byte)(0x20 | sequence);
            payload.Skip(offset).Take(7).ToArray().CopyTo(bytes, 1);
            lines.Add("7E8" + Convert.ToHexString(bytes));
            sequence = (sequence + 1) & 15;
        }
        var result = await Read(lines.ToArray());
        await Assert.That(result.Stored.Status).IsEqualTo(DtcReadStatus.Succeeded);
        await Assert.That(result.Stored.Codes!).IsEquivalentTo(["P0143"]);
    }

    [Test]
    [Arguments("")]
    [Arguments("NO DATA")]
    public async Task NoResponse_HasNoCleanCodeList(string line)
    {
        var result = await Read(line);
        await Assert.That(result.Stored.Status).IsEqualTo(DtcReadStatus.NoData);
        await Assert.That(result.Stored.Codes).IsNull();
        await Assert.That(result.Stored.Responders).IsEmpty();
    }

    [Test]
    public async Task FailureInStoredMode_DoesNotErasePendingEvidence()
    {
        var session = new QuerySession((mode, _) => mode == "03"
            ? throw new IOException("disconnected") : ["7E80447010143"]);
        var result = await new ObdDtcReader(session, ObdDtcReader.FunctionalContext).GetDtcsAsync();
        await Assert.That(result.Stored.Status).IsEqualTo(DtcReadStatus.QueryFailed);
        await Assert.That(result.Stored.Codes).IsNull();
        await Assert.That(result.Pending.Codes!).IsEquivalentTo(["P0143"]);
    }

    [Test]
    public async Task InternalDeadline_IsNotCallerCancellation()
    {
        var session = new QuerySession((_, _) => throw new TimeoutException());
        var result = await new ObdDtcReader(session, ObdDtcReader.FunctionalContext).GetDtcsAsync();
        await Assert.That(result.Stored.Status).IsEqualTo(DtcReadStatus.Timeout);
        await Assert.That(result.Pending.Status).IsEqualTo(DtcReadStatus.Timeout);
        await Assert.That(session.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task UnexpectedCancellation_IsNotReclassifiedAsTimeout()
    {
        var session = new QuerySession((_, _) => throw new OperationCanceledException());
        var reader = new ObdDtcReader(session, ObdDtcReader.FunctionalContext);
        await Assert.That(async () => await reader.GetDtcsAsync()).Throws<OperationCanceledException>();
        await Assert.That(session.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task CallerCancellation_PreventsPendingQuery()
    {
        using var cts = new CancellationTokenSource();
        var session = new QuerySession((_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return [];
        });
        var reader = new ObdDtcReader(session, ObdDtcReader.FunctionalContext);
        await Assert.That(async () => await reader.GetDtcsAsync(cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(session.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task AlreadyCanceled_DoesNotTouchSession()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var session = new QuerySession((_, _) => []);
        var reader = new ObdDtcReader(session, ObdDtcReader.FunctionalContext);
        await Assert.That(async () => await reader.GetDtcsAsync(cts.Token)).Throws<OperationCanceledException>();
        await Assert.That(session.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task ProgrammingErrors_AreNotDiagnosticOutcomes()
    {
        var session = new QuerySession((_, _) => throw new InvalidOperationException("wrong mode"));
        var reader = new ObdDtcReader(session, ObdDtcReader.FunctionalContext);
        await Assert.That(async () => await reader.GetDtcsAsync()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Outcome_DefensivelyCopiesEvidence()
    {
        var codes = new List<string> { "P0143" };
        var responders = new List<DtcResponderResult> { new(0x7E8, codes) };
        var result = DtcModeResult.FromResponses(responders);
        codes.Clear();
        responders.Clear();
        await Assert.That(result.Codes!).IsEquivalentTo(["P0143"]);
        await Assert.That(result.Responders[0].Codes!).IsEquivalentTo(["P0143"]);
        await Assert.That(() => DtcModeResult.Failed(DtcReadStatus.Succeeded)).Throws<ArgumentOutOfRangeException>();
    }

    private static ValueTask<DtcReadResult> Read(params string[] lines) =>
        new ObdDtcReader(new QuerySession((mode, _) => mode == "03" ? lines : ["7E8024700"]),
            ObdDtcReader.FunctionalContext).GetDtcsAsync();

    // Tests the public session seam, without timings or transport retries obscuring outcomes.
    private sealed class QuerySession(Func<string, CancellationToken, string[]> query) : IElmSession
    {
        public int Calls { get; private set; }
        public TimeSpan CommandTimeout { get; set; }
        public EcuCommunicationMode CurrentMode => EcuCommunicationMode.RequestResponse;
        public bool EnableDebugLogging { get; set; }
        public TimeSpan ProtocolDetectionTimeout { get; set; }
        public MonitoringEndReason LastMonitoringEndReason => MonitoringEndReason.None;
        public ValueTask<string[]> QueryAsync(string command, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(query(command, ct));
        }
        public ValueTask<string[]> QueryAsync(string command, EcuContext context, CancellationToken ct) => QueryAsync(command, ct);
        public ValueTask<bool> ActivateSessionAsync(EcuContext context, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask<bool> SendKeepAliveAsync(EcuContext context, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask EnterMonitoringModeAsync(EcuContext context, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask ExitMonitoringModeAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask InitializeAndLockAsync(CancellationToken ct) => throw new NotSupportedException();
        public ValueTask SetEcuContextAsync(EcuContext context, CancellationToken ct) => throw new NotSupportedException();
        public async IAsyncEnumerable<RawCanFrame> MonitorFramesAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
