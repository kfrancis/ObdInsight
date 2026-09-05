using System.Text;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Simulation;

namespace ObdInsight.Tests.Elm327;

[Timeout(10_000)]
public class ElmFramerOutcomeTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Eof_NeverReturnsPartialOrSpins(bool command, bool partial, CancellationToken ct)
    {
        var transport = new EndingTransport(partial ? "7E8" : "", eof: true);
        var framer = new ElmFramer(transport);
        await Assert.That(async () => await Read(framer, command, TimeSpan.FromSeconds(2), ct))
            .Throws<EndOfStreamException>();
        await Assert.That(transport.Reads).IsEqualTo(partial ? 2 : 1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Deadline_NeverReturnsPartialOrCancellation(bool command, bool partial, CancellationToken ct)
    {
        var framer = new ElmFramer(new EndingTransport(partial ? "7E8" : "", eof: false));
        await Assert.That(async () => await Read(framer, command, TimeSpan.FromMilliseconds(40), ct))
            .Throws<TimeoutException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CallerCancellation_PreservesCallerToken(bool command, CancellationToken ct)
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var transport = new EndingTransport("7E8", eof: false);
        var pending = Read(new ElmFramer(transport), command, Timeout.InfiniteTimeSpan, caller.Token).AsTask();
        await transport.Waiting.Task.WaitAsync(ct);
        caller.Cancel();
        try { await pending; throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException ex)
        {
            await Assert.That(ex.CancellationToken).IsEqualTo(caller.Token);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Deadline_CoversWriteAndFlush(bool flush, CancellationToken ct)
    {
        var transport = new EndingTransport("", false) { BlockWrite = !flush, BlockFlush = flush };
        await Assert.That(async () => await new ElmFramer(transport)
            .SendAndReadFrameAsync("ATI", TimeSpan.FromMilliseconds(40), ct)).Throws<TimeoutException>();
        await Assert.That(transport.Reads).IsEqualTo(0);
    }

    private static ValueTask<string> Read(ElmFramer framer, bool command, TimeSpan timeout, CancellationToken ct) =>
        command ? framer.SendAndReadFrameAsync("ATI", timeout, ct) : framer.ReadUntilAsync("\r", timeout, ct);

    [Test]
    public async Task SuppressedKeepAlive_StillRequiresAdapterPrompt(CancellationToken ct)
    {
        await using var transport = new ReplayElmTransport();
        transport.Expect("3E80", "");
        var session = new ElmSession(new ElmFramer(transport)) { CommandTimeout = TimeSpan.FromMilliseconds(40) };
        var context = new EcuContext { Name = "test", TxHeader = "7E0", KeepAliveCommand = "3E80" };
        await Assert.That(async () => await session.SendKeepAliveAsync(context, ct)).Throws<TimeoutException>();
        await Assert.That(session.Failure).IsNotNull();
    }

    private sealed class EndingTransport(string initial, bool eof) : IElmTransport
    {
        private byte[] _initial = Encoding.ASCII.GetBytes(initial);
        public int Reads { get; private set; }
        public bool BlockWrite { get; init; }
        public bool BlockFlush { get; init; }
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsOpen => true;
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            Reads++;
            if (_initial.Length > 0)
            {
                var count = _initial.Length;
                _initial.CopyTo(buffer);
                _initial = [];
                return count;
            }
            Waiting.TrySetResult();
            if (!eof) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
            BlockWrite ? new(Task.Delay(Timeout.InfiniteTimeSpan, ct)) : ValueTask.CompletedTask;
        public ValueTask FlushAsync(CancellationToken ct) =>
            BlockFlush ? new(Task.Delay(Timeout.InfiniteTimeSpan, ct)) : ValueTask.CompletedTask;
        public ValueTask OpenAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void ClearBuffer() { }
    }
}
