using System.Text;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;

namespace ObdInsight.Tests.Elm327;

[Timeout(30_000)]
public class ElmTransactionTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Test]
    public async Task ContextAndCommand_AreOneTransaction(CancellationToken ct)
    {
        await using var transport = new ControlledTransport();
        var entered = Signal(); var release = Signal(); var armed = true;
        transport.AfterWrite = async (command, token) =>
        {
            if (command == "AT ST 20" && armed)
            {
                armed = false; entered.SetResult(); await release.Task.WaitAsync(token);
            }
        };
        var session = new ElmSession(new ElmFramer(transport));
        var a = new EcuContext { Name = "A", TxHeader = "700", AdapterTimeoutUnits = 0x20 };
        var b = new EcuContext { Name = "B", TxHeader = "701", AdapterTimeoutUnits = 0x20 };
        var first = session.QueryAsync("2101", a, ct).AsTask();
        await entered.Task.WaitAsync(ct);
        var second = session.QueryAsync("2102", b, ct).AsTask();
        release.SetResult();
        await Task.WhenAll(first, second);
        await Assert.That(transport.Requests.ToArray()).IsEquivalentTo(["700:2101", "701:2102"]);
    }

    [Test]
    public async Task CanceledQueueWait_DoesNotWriteOrInvalidate(CancellationToken ct)
    {
        await using var transport = new ControlledTransport();
        var entered = Signal(); var release = Signal();
        transport.AfterWrite = async (command, token) =>
        { if (command == "2101") { entered.SetResult(); await release.Task.WaitAsync(token); } };
        var session = new ElmSession(new ElmFramer(transport));
        var pending = session.QueryAsync("2101", ct).AsTask();
        await entered.Task.WaitAsync(ct);
        using var caller = new CancellationTokenSource();
        var waiting = session.QueryAsync("2102", caller.Token).AsTask();
        caller.Cancel();
        await Assert.That(async () => await waiting).Throws<OperationCanceledException>();
        release.SetResult(); await pending;
        await Assert.That(session.Failure).IsNull();
        await Assert.That(transport.Replay.SentCommands.Contains("2102")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InterruptedReply_RejectsLateResponseAndEverySubsequentOperation(bool cancel, CancellationToken ct)
    {
        await using var transport = new ControlledTransport();
        transport.Replay.AutoRespond("2101", "7BB 03 61"); // incomplete, prompt arrives later
        var framer = new ElmFramer(transport); var notices = 0;
        framer.Invalidated += (_, _) => notices++;
        framer.Invalidated += (_, _) => throw new Exception("subscriber failure must not hide cancellation");
        var session = new ElmSession(framer) { CommandTimeout = TimeSpan.FromMilliseconds(50) };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = session.QueryAsync("2101", caller.Token).AsTask();
        if (cancel) caller.Cancel();
        if (cancel) await Assert.That(async () => await pending).Throws<OperationCanceledException>();
        else await Assert.That(async () => await pending).Throws<TimeoutException>();
        transport.Replay.EnqueueIncoming(" 01 2A\r>7BB 03 61 02 55\r>");
        var count = transport.Replay.SentCommands.Count;
        await Assert.That(async () => await session.QueryAsync("2102", ct)).Throws<ElmSessionInvalidatedException>();
        await Assert.That(async () => await session.InitializeAndLockAsync(ct)).Throws<ElmSessionInvalidatedException>();
        await Assert.That(() => framer.ClearBuffer()).Throws<ElmSessionInvalidatedException>();
        await Assert.That(async () => await framer.WriteAsync("AT Z\r", ct)).Throws<ElmSessionInvalidatedException>();
        await Assert.That(transport.Replay.SentCommands.Count).IsEqualTo(count);
        await Assert.That(notices).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UncertainWriteOrFlush_IsNotRetriedEvenWhenAllowlisted(bool flush, CancellationToken ct)
    {
        await using var transport = new ControlledTransport();
        if (flush) transport.FlushFailure = true;
        else transport.AfterWrite = (_, _) => throw new IOException("possibly delivered");
        var inner = new ElmSession(new ElmFramer(transport));
        var retry = new RetryingElmSession(inner, new() { RetrySafeCommands = ["2E1234"] });
        await Assert.That(async () => await retry.QueryAsync("2E1234", ct)).Throws<IOException>();
        await Assert.That(transport.Replay.SentCommands.Count(c => c == "2E1234")).IsEqualTo(1);
        await Assert.That(inner.Failure).IsNotNull();
    }

    [Test]
    public async Task RejectedStateChangingCommand_IsNotImplicitlyRetried(CancellationToken ct)
    {
        await using var transport = new ControlledTransport();
        transport.Replay.AutoRespond("2E1234", "NO DATA\r>");
        var session = new RetryingElmSession(new ElmSession(new ElmFramer(transport)));
        await Assert.That(async () => await session.QueryAsync("2E1234", ct)).Throws<ElmQueryRejectedException>();
        await Assert.That(transport.Replay.SentCommands.Count(c => c == "2E1234")).IsEqualTo(1);
        await Assert.That(session.Failure).IsNull();
    }

    [Test]
    public async Task Framer_RejectsConcurrentReaderWithoutConsumingItsResponse(CancellationToken ct)
    {
        await using var transport = new ControlledTransport();
        transport.Replay.AutoRespond("2101", "");
        var framer = new ElmFramer(transport);
        var pending = framer.SendAndReadFrameAsync("2101", TimeSpan.FromSeconds(5), ct).AsTask();
        await Assert.That(async () => await framer.ReadUntilAsync("\r", TimeSpan.FromSeconds(1), ct)).Throws<InvalidOperationException>();
        transport.Replay.EnqueueIncoming("61 01\r>");
        await Assert.That(await pending).IsEqualTo("61 01\r");
        await Assert.That(framer.Failure).IsNull();
    }

    [Test]
    public async Task CanceledSuspendedQuery_DoesNotResumeInvalidMonitor(CancellationToken ct)
    {
        await using var transport = new ControlledTransport();
        transport.Replay.AutoRespond("ATMA", "");
        transport.Replay.AutoRespond("2101", "");
        var entered = Signal();
        transport.AfterWrite = (command, _) => { if (command == "2101") entered.TrySetResult(); return ValueTask.CompletedTask; };
        var session = new ElmSession(new ElmFramer(transport));
        await using var commands = new LeafAze0CommandSet(session);
        commands.Monitor.FilterRotation = [];
        await commands.Monitor.StartAsync(ct);
        commands.TryGet<IBatteryManagementSystem>(out var bms);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var query = bms.GetStatusAsync(caller.Token).AsTask();
        await entered.Task.WaitAsync(ct);
        caller.Cancel();
        await Assert.That(async () => await query).Throws<OperationCanceledException>();
        await Assert.That(commands.Monitor.EndReason).IsEqualTo(MonitoringEndReason.TransportError);
        await Assert.That(transport.Replay.SentCommands.Count(c => c == "ATMA")).IsEqualTo(1);
        await Assert.That(async () => await commands.Monitor.StartAsync(ct)).Throws<ElmSessionInvalidatedException>();
    }

    [Test]
    public async Task StopWithoutPrompt_CannotAdmitQuery(CancellationToken ct)
    {
        await using var transport = new ControlledTransport();
        transport.Replay.AutoRespond("ATMA", ""); transport.Replay.AutoRespond("", "");
        var session = new ElmSession(new ElmFramer(transport)) { CommandTimeout = TimeSpan.FromMilliseconds(50) };
        await session.EnterMonitoringModeAsync(EcuContext.NissanLeafHvbatMonitor, ct);
        await Assert.That(async () => await session.ExitMonitoringModeAsync(ct)).Throws<TimeoutException>();
        await Assert.That(async () => await session.QueryAsync("2101", ct)).Throws<ElmSessionInvalidatedException>();
        await Assert.That(transport.Replay.SentCommands.Contains("2101")).IsFalse();
    }

    [Test]
    public async Task Suspension_JoinsRotatingConfigurationBeforeIssuingStop(CancellationToken ct)
    {
        await using var transport = new ControlledTransport();
        transport.Replay.AutoRespond("ATMA", "");
        var entered = Signal(); var release = Signal(); var armed = true;
        transport.AfterWrite = async (command, token) =>
        {
            if (command == "AT H1" && armed)
            { armed = false; entered.SetResult(); await release.Task.WaitAsync(token); }
        };
        var session = new ElmSession(new ElmFramer(transport));
        await using var commands = new LeafAze0CommandSet(session);
        await commands.Monitor.StartAsync(ct); // default rotating monitor configures on its owned loop
        await entered.Task.WaitAsync(ct);
        var suspended = commands.Monitor.SuspendAsync(ct).AsTask();
        await Assert.That(suspended.IsCompleted).IsFalse();
        release.SetResult();
        await using (await suspended)
        {
            await Assert.That(session.Failure).IsNull();
            await Assert.That(session.CurrentMode).IsEqualTo(EcuCommunicationMode.RequestResponse);
            await session.QueryAsync("2101", EcuContext.NissanLeafBms, ct);
        }
        await commands.Monitor.StopAsync(ct);
        await Assert.That(session.Failure).IsNull();
    }

    [Test]
    public async Task Exit_WaitsForReaderOwnership_AndCanceledWaitDoesNotForceMode(CancellationToken ct)
    {
        await using var transport = new ControlledTransport();
        transport.Replay.AutoRespond("ATMA", "");
        var session = new ElmSession(new ElmFramer(transport));
        await session.EnterMonitoringModeAsync(EcuContext.NissanLeafHvbatMonitor, ct);
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var reader = session.MonitorFramesAsync(reading.Token).GetAsyncEnumerator(reading.Token);
        var next = reader.MoveNextAsync().AsTask();
        using var exit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stop = session.ExitMonitoringModeAsync(exit.Token).AsTask();
        exit.Cancel();
        await Assert.That(async () => await stop).Throws<OperationCanceledException>();
        await Assert.That(session.CurrentMode).IsEqualTo(EcuCommunicationMode.PassiveMonitoring);
        await Assert.That(session.Failure).IsNull();
        reading.Cancel(); await next;
        await session.ExitMonitoringModeAsync(ct);
        await Assert.That(session.CurrentMode).IsEqualTo(EcuCommunicationMode.RequestResponse);
    }

    private sealed class ControlledTransport : IElmTransport
    {
        public ReplayElmTransport Replay { get; } = new();
        public Func<string, CancellationToken, ValueTask>? AfterWrite { get; set; }
        public List<string> Requests { get; } = [];
        public bool FlushFailure { get; set; }
        private string _header = "";
        public ControlledTransport()
        {
            foreach (var command in new[] { "2101", "2102", "2E1234" }) Replay.AutoRespond(command, "61 01 2A\r>");
        }
        public bool IsOpen => true;
        public ValueTask OpenAsync(CancellationToken ct) => Replay.OpenAsync(ct);
        public ValueTask<int> ReadAsync(Memory<byte> data, CancellationToken ct) => Replay.ReadAsync(data, ct);
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            var command = Encoding.ASCII.GetString(data.Span).Trim();
            if (command.StartsWith("AT SH ")) _header = command[6..];
            if (!command.StartsWith("AT")) Requests.Add(_header + ":" + command);
            await Replay.WriteAsync(data, ct);
            if (AfterWrite is not null) await AfterWrite(command, ct);
        }
        public ValueTask FlushAsync(CancellationToken ct) => FlushFailure ? throw new IOException("uncertain flush") : Replay.FlushAsync(ct);
        public void ClearBuffer() => Replay.ClearBuffer();
        public ValueTask DisposeAsync() => Replay.DisposeAsync();
    }
}
