//HintName: CanFrameRouter_TestNamespace.g.cs
#nullable enable
using System;
namespace TestNamespace
{
    /// <summary>
    /// Provides automatic routing of CAN frames to their corresponding parser methods based on CAN ID.
    /// </summary>
    public static class CanFrameRouter
    {
        /// <summary>
        /// Attempts to parse a CAN frame with ID 0x54C.
        /// </summary>
        public static bool TryParseHvacFrame(int canId, ReadOnlySpan<byte> data, out HvacFrame? result)
        {
            if (canId == 0x54C && data.Length >= HvacFrame.MinimumLength)
            {
                result = HvacFrame.Parse(data);
                return true;
            }
            result = null;
            return false;
        }
        /// <summary>
        /// Attempts to parse any registered CAN frame type based on the CAN ID.
        /// </summary>
        /// <returns>Parsed frame object, or null if the CAN ID is not recognized.</returns>
        public static object? TryParseAny(int canId, ReadOnlySpan<byte> data)
        {
            return canId switch
            {
                0x54C => data.Length >= HvacFrame.MinimumLength ? HvacFrame.Parse(data) : null,
                _ => null
            };
        }
    }
}
