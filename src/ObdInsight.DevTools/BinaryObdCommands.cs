using System.Text;

namespace ObdInsight.DevTools;

/// <summary>
///     Binary OBD command helper - minimal stub for DevTools.
/// </summary>
public static class BinaryObdCommands
{
    public static bool TryInterpretAsAscii(byte[] data, out string ascii)
    {
        try
        {
            ascii = Encoding.ASCII.GetString(data);
            return ascii.All(c => !char.IsControl(c) || c == '\r' || c == '\n');
        }
        catch
        {
            ascii = string.Empty;
            return false;
        }
    }
}
