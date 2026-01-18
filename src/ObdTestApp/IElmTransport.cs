namespace ObdTestApp
{
    /// <summary>
    /// Defines an asynchronous transport interface for reading from and writing to a communication channel using byte
    /// buffers.
    /// </summary>
    /// <remarks>Implementations of this interface are responsible for managing the underlying connection
    /// state and supporting asynchronous I/O operations. The interface is designed for use in scenarios where
    /// non-blocking, high-performance data transfer is required, such as network protocols or device communication. All
    /// methods are asynchronous and support cancellation via a CancellationToken.</remarks>
    public interface IElmTransport : IAsyncDisposable
    {
        /// <summary>
        /// Gets a value indicating whether the resource is currently open.
        /// </summary>
        bool IsOpen { get; }

        /// <summary>
        /// Asynchronously clears all buffers for this stream and causes any buffered data to be written to the
        /// underlying device.
        /// </summary>
        /// <param name="ct">A cancellation token that can be used to cancel the asynchronous flush operation.</param>
        /// <returns>A value task that represents the asynchronous flush operation.</returns>
        ValueTask FlushAsync(CancellationToken ct);

        /// <summary>
        /// Asynchronously opens the connection, enabling subsequent operations that require an open state.
        /// </summary>
        /// <param name="ct">A cancellation token that can be used to cancel the asynchronous open operation.</param>
        /// <returns>A ValueTask that represents the asynchronous open operation.</returns>
        ValueTask OpenAsync(CancellationToken ct);

        /// <summary>
        /// Asynchronously reads a sequence of bytes from the current stream into the provided memory buffer.
        /// </summary>
        /// <param name="buffer">The region of memory to write the data into. The method attempts to fill this buffer with data read from the
        /// stream.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the asynchronous read operation.</param>
        /// <returns>A value task representing the asynchronous read operation. The result contains the total number of bytes
        /// read into the buffer. The result is 0 if the end of the stream has been reached.</returns>
        ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

        /// <summary>
        /// Asynchronously writes the specified sequence of bytes to the underlying target.
        /// </summary>
        /// <param name="data">The data to write. The memory region is not modified by this operation.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the write operation.</param>
        /// <returns>A value task that represents the asynchronous write operation.</returns>
        ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

        /// <summary>
        /// Clears all data from the buffer.
        /// </summary>
        void ClearBuffer();
    }
}