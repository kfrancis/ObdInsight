using Spectre.Console;

namespace ObdTestApp.Core.UI;

/// <summary>
/// Helper methods for console output and rendering
/// </summary>
public static class ConsoleHelpers
{
    /// <summary>
    /// Safely writes text to Spectre.Console by escaping markup characters.
    /// </summary>
    public static void SafeWrite(string text)
    {
        AnsiConsole.Write(text.EscapeMarkup());
    }

    /// <summary>
    /// Safely writes a line to Spectre.Console by escaping markup characters.
    /// </summary>
    public static void SafeWriteLine(string text)
    {
        AnsiConsole.WriteLine(text.EscapeMarkup());
    }

    /// <summary>
    /// Gets the appropriate color for RSSI signal strength
    /// </summary>
    /// <param name="rssi">RSSI value in dBm</param>
    /// <returns>Color name for Spectre.Console markup</returns>
    public static string GetRssiColor(int rssi) => rssi switch
    {
        > -50 => "green",
        > -70 => "yellow",
        _ => "red"
    };
}
