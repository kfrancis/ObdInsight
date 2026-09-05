namespace ObdInsight.Core.Communication.Elm327;

/// <summary>
/// The adapter's response boundary is no longer trustworthy. Dispose the owning connection
/// and create a fresh transport/framer/session graph. A possibly delivered command must not be replayed.
/// </summary>
public sealed class ElmSessionInvalidatedException(string message, Exception? innerException = null)
    : IOException(message, innerException);

/// <summary>A prompt-terminated response rejected by the query validator; framing remains synchronized.</summary>
public sealed class ElmQueryRejectedException(string command)
    : IOException($"ELM query '{command}' returned no usable response. The command was not retried.")
{
    public string Command { get; } = command;
}
