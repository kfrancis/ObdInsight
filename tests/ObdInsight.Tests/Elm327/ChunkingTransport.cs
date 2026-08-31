using ObdInsight.Core.Communication.Elm327;

namespace OdbTestApp.Tests.Elm327;

/// <summary>
/// Serves a fixed byte stream to <see cref="ElmFramer"/> in caller-specified chunk sizes,
/// modelling a transport (BLE notifications, serial reads) whose read boundaries fall at
/// arbitrary points in the stream — including mid-frame and mid-prompt.
/// </summary>
/// <remarks>
/// A read never returns more than the caller's buffer allows; a chunk larger than the buffer
/// is served across successive reads. Chunk sizes cycle once the list is exhausted. Once the stream is exhausted the transport blocks until
/// cancelled, matching a real adapter that has simply gone quiet.
/// </remarks>
internal sealed class ChunkingTransport : IElmTransport
{
    private readonly int[] _chunkSizes;
    private readonly byte[] _stream;
    private int _chunkIndex;
    private int _position;
    private int _remainingInChunk;

    public ChunkingTransport(byte[] stream, int[] chunkSizes)
    {
        _stream = stream;
        _chunkSizes = chunkSizes;
    }

    public List<byte[]> Written { get; } = [];

    public bool IsOpen => true;

    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask OpenAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_position >= _stream.Length || buffer.Length == 0)
        {
            // Stream exhausted: stay quiet until the framer's own timeout fires.
            await Task.Delay(Timeout.Infinite, ct);
        }

        if (_remainingInChunk == 0)
        {
            _remainingInChunk = _chunkSizes[_chunkIndex % _chunkSizes.Length];
            _chunkIndex++;
        }

        var n = Math.Min(Math.Min(_remainingInChunk, buffer.Length), _stream.Length - _position);
        _stream.AsSpan(_position, n).CopyTo(buffer.Span);
        _position += n;
        _remainingInChunk -= n;
        return n;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Written.Add(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public void ClearBuffer()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
