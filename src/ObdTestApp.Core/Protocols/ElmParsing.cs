using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace ObdTestApp.Core.Protocols
{
    /// <summary>
    /// Provides utility methods for parsing and interpreting ELM327 OBD-II adapter responses.
    /// </summary>
    /// <remarks>This static class contains methods for identifying adapter error messages, normalizing
    /// response frames, and parsing Mode 01 OBD-II responses. The methods are designed to assist in processing raw text
    /// data received from ELM327-compatible devices.</remarks>
    public static class ElmParsing
    {
        /// <summary>
        /// Determines whether the specified line of text appears to represent an adapter error message.
        /// </summary>
        /// <remarks>This method checks for specific keywords and patterns commonly found in adapter error
        /// messages, such as "NO DATA", "UNABLE", "STOPPED", or "ERROR". The comparison is case-insensitive.</remarks>
        /// <param name="line">The line of text to evaluate for error indicators. Cannot be null.</param>
        /// <returns>true if the line matches common adapter error patterns; otherwise, false.</returns>
        public static bool LooksLikeAdapterError(string line)
        {
            var s = line.Trim();

            // "SEARCHING..." is NOT an error - it means the adapter is actively trying protocols
            if (s.Contains("SEARCHING", StringComparison.OrdinalIgnoreCase))
                return false;

            return s == "?" || s.Contains("NO DATA", StringComparison.OrdinalIgnoreCase)
                || s.Contains("UNABLE", StringComparison.OrdinalIgnoreCase)
                || s.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)
                || s.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Splits the specified text into an array of trimmed, non-empty lines, normalizing line endings and removing
        /// null characters.
        /// </summary>
        /// <remarks>This method removes all null characters from the input and treats both carriage
        /// return (\r) and line feed (\n) as line delimiters. Empty or whitespace-only lines are excluded from the
        /// result.</remarks>
        /// <param name="frame">The text to be split into lines. May contain carriage return (CR), line feed (LF), or both as line
        /// separators, as well as null characters.</param>
        /// <returns>An array of strings, each representing a trimmed, non-empty line from the input text. The array is empty if
        /// no valid lines are found.</returns>
        public static string[] NormalizeLines(string frame)
        {
            // Typical frame contains CR/LF line endings, plus possible echoes.
            var cleaned = frame.Replace("\0", "");
            var lines = cleaned
            .Split(["\r", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
            return lines;
        }

        /// <summary>
        /// Attempts to parse a Mode 01 OBD-II response line for a specified parameter ID (PID).
        /// </summary>
        /// <remarks>This method does not throw exceptions for invalid input. It returns false if the
        /// input line does not match the expected format or the PID does not match. The comparison of the PID is
        /// case-insensitive for the hexadecimal representation.</remarks>
        /// <param name="line">The response line to parse, expected in the format "41 <pid> <data...>" with hexadecimal values separated by
        /// spaces.</param>
        /// <param name="pid">The parameter ID (PID) to match, specified as a byte value.</param>
        /// <param name="data">When this method returns, contains the parsed data bytes if parsing succeeds; otherwise, an empty array.</param>
        /// <returns>true if the response line is successfully parsed and the PID matches; otherwise, false.</returns>
        public static bool TryParseMode01Response(string line, byte pid, out byte[] data)
        {
            // Expect: 41 <pid> <data...>
            data = [];
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            if (!parts[0].Equals("41", StringComparison.OrdinalIgnoreCase)) return false;
            if (!byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var gotPid))
                if (gotPid != pid) return false;
            var bytes = new byte[parts.Length - 2];
            for (var i = 2; i < parts.Length; i++)
            {
                if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i - 2]))
                    return false;
            }
            data = bytes;
            return true;
        }
    }
}
