using System.Text;

namespace ObdInsight.DevTools;

/// <summary>
/// Extension methods for BLE transports to provide convenient string-based API.
/// </summary>
public static class BleTransportExtensions
{
    /// <summary>
    /// Write a string to the transport (encodes as ASCII).
    /// </summary>
    public static async Task WriteAsync(this WindowsBleTransport transport, string data, CancellationToken ct = default)
    {
        var bytes = Encoding.ASCII.GetBytes(data);
        await ((ObdInsight.Core.Communication.Elm327.IElmTransport)transport).WriteAsync(bytes, ct);
    }

    /// <summary>
    /// Read from transport until a delimiter is found or timeout occurs.
    /// </summary>
    public static async Task<string> ReadUntilAsync(this WindowsBleTransport transport, string delimiter, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var buffer = new byte[4096];
        var sb = new StringBuilder();

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var bytesRead = await ((ObdInsight.Core.Communication.Elm327.IElmTransport)transport).ReadAsync(buffer, cts.Token);
                if (bytesRead == 0)
                    break;

                var chunk = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                sb.Append(chunk);

                if (sb.ToString().Contains(delimiter))
                    break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout occurred
        }

        return sb.ToString();
    }

    /// <summary>
    /// Overload without cancellation token.
    /// </summary>
    public static Task<string> ReadUntilAsync(this WindowsBleTransport transport, string delimiter, TimeSpan timeout)
    {
        return ReadUntilAsync(transport, delimiter, timeout, CancellationToken.None);
    }

    /// <summary>
    /// Drain the receive buffer by clearing it.
    /// </summary>
    public static void DrainBuffer(this WindowsBleTransport transport)
    {
        ((ObdInsight.Core.Communication.Elm327.IElmTransport)transport).ClearBuffer();
    }
}
