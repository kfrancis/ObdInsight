using System;
using System.Buffers.Binary;

namespace ObdInsight.SourceGeneration;

/// <summary>
/// Provides utility methods for reading individual bits and multi-bit integer values from CAN (Controller Area
/// Network) frame data represented as an 8-byte buffer.
/// </summary>
/// <remarks>All methods in this class operate on CAN frame data using little-endian byte order, as
/// commonly used in automotive and industrial CAN protocols. The methods support extracting boolean, signed, and
/// unsigned integer values from arbitrary bit positions and lengths within the frame data. This class is static and
/// cannot be instantiated.</remarks>
public static class CanBits
{
    /// <summary>
    /// Reads a single boolean bit from CAN frame data.
    /// </summary>
    /// <param name="data">8-byte CAN frame data (little-endian byte order)</param>
    /// <param name="bitPos">Bit position (0-63)</param>
    /// <returns>true if bit is set, false otherwise</returns>
    public static bool ReadBool(ReadOnlySpan<byte> data, int bitPos)
    {
        return ReadUnsigned(data, bitPos, 1) != 0;
    }

    /// <summary>
    /// Reads a signed integer value from CAN frame data.
    /// </summary>
    /// <param name="data">8-byte CAN frame data (little-endian byte order)</param>
    /// <param name="bitPos">Starting bit position (0-63)</param>
    /// <param name="bitLen">Number of bits to read (1-32)</param>
    /// <returns>Signed integer value with sign extension</returns>
    public static int ReadSigned(ReadOnlySpan<byte> data, int bitPos, int bitLen)
    {
        if (bitLen <= 0 || bitLen > 32)
            throw new ArgumentOutOfRangeException(nameof(bitLen), "Must be 1-32");

        var unsigned = ReadUnsigned(data, bitPos, bitLen);

        // Check if sign bit is set
        var signBitMask = 1u << (bitLen - 1);
        if ((unsigned & signBitMask) != 0)
        {
            // Sign extend: fill upper bits with 1s
            var signExtendMask = ~((1u << bitLen) - 1);
            return (int)(unsigned | signExtendMask);
        }

        return (int)unsigned;
    }

    /// <summary>
    /// Reads an unsigned integer value from CAN frame data.
    /// </summary>
    /// <param name="data">8-byte CAN frame data (little-endian byte order)</param>
    /// <param name="bitPos">Starting bit position (0-63)</param>
    /// <param name="bitLen">Number of bits to read (1-32)</param>
    /// <returns>Unsigned integer value</returns>
    public static uint ReadUnsigned(ReadOnlySpan<byte> data, int bitPos, int bitLen)
    {
        if (data.Length < 8)
            throw new ArgumentException("CAN data must be at least 8 bytes", nameof(data));
        if (bitPos < 0 || bitPos > 63)
            throw new ArgumentOutOfRangeException(nameof(bitPos), "Must be 0-63");
        if (bitLen <= 0 || bitLen > 32)
            throw new ArgumentOutOfRangeException(nameof(bitLen), "Must be 1-32");

        // Read 8 bytes as little-endian uint64
        var raw = BinaryPrimitives.ReadUInt64LittleEndian(data);

        // Create mask for the bits we want
        var mask = bitLen == 32 ? 0xFFFF_FFFFul : ((1ul << bitLen) - 1ul);

        // Shift and mask
        return (uint)((raw >> bitPos) & mask);
    }
}
