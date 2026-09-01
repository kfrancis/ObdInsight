using System;
using System.Buffers.Binary;

namespace ObdInsight.SourceGeneration;

/// <summary>
///     Provides utility methods for reading individual bits and multi-bit integer values from CAN (Controller Area
///     Network) frame data represented as an 8-byte buffer.
/// </summary>
/// <remarks>
///     All methods in this class operate on CAN frame data using little-endian byte order, as
///     commonly used in automotive and industrial CAN protocols. The methods support extracting boolean, signed, and
///     unsigned integer values from arbitrary bit positions and lengths within the frame data. This class is static and
///     cannot be instantiated.
/// </remarks>
public static class CanBits
{
    /// <summary>
    ///     Reads a single boolean bit from CAN frame data.
    /// </summary>
    /// <param name="data">8-byte CAN frame data (little-endian byte order)</param>
    /// <param name="bitPos">The bit position (0-63)</param>
    /// <returns>true if bit is set, false otherwise</returns>
    public static bool ReadBool(ReadOnlySpan<byte> data, int bitPos)
    {
        return ReadUnsigned(data, bitPos, 1) != 0;
    }

    /// <summary>
    ///     Reads a signed integer value from CAN frame data.
    /// </summary>
    /// <param name="data">8-byte CAN frame data (little-endian byte order)</param>
    /// <param name="bitPos">Starting bit position (0-63)</param>
    /// <param name="bitLen">Number of bits to read (1-32)</param>
    /// <returns>Signed integer value with sign extension</returns>
    public static int ReadSigned(ReadOnlySpan<byte> data, int bitPos, int bitLen)
    {
        if (bitLen is <= 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitLen), "Must be 1-32");
        }

        var unsigned = ReadUnsigned(data, bitPos, bitLen);

        // Check if sign bit is set
        var signBitMask = 1u << (bitLen - 1);
        if ((unsigned & signBitMask) != 0)
        {
            // Sign extend: fill upper bits with 1s.
            // At 32 bits there are no upper bits — the (int) reinterpretation is already
            // two's complement, and (1u << 32) would wrap to 1 via C# shift-count masking.
            var signExtendMask = bitLen == 32 ? 0u : ~((1u << bitLen) - 1);
            return (int)(unsigned | signExtendMask);
        }

        return (int)unsigned;
    }

    /// <summary>
    ///     Reads a single boolean bit using Motorola (big-endian) numbering.
    /// </summary>
    /// <param name="data">8-byte CAN frame data</param>
    /// <param name="bitPos">DBC bit position (0-63)</param>
    public static bool ReadBoolBe(ReadOnlySpan<byte> data, int bitPos)
    {
        return ReadUnsignedBe(data, bitPos, 1) != 0;
    }

    /// <summary>
    ///     Reads a signed integer using Motorola (big-endian) numbering, with sign extension.
    /// </summary>
    /// <param name="data">8-byte CAN frame data</param>
    /// <param name="bitPos">DBC bit position of the signal's MOST significant bit (0-63)</param>
    /// <param name="bitLen">Number of bits to read (1-32)</param>
    public static int ReadSignedBe(ReadOnlySpan<byte> data, int bitPos, int bitLen)
    {
        if (bitLen is <= 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitLen), "Must be 1-32");
        }

        var unsigned = ReadUnsignedBe(data, bitPos, bitLen);

        var signBitMask = 1u << (bitLen - 1);
        if ((unsigned & signBitMask) != 0)
        {
            // At 32 bits there are no upper bits to fill, and (1u << 32) would wrap to 1 via
            // C#'s shift-count masking - the (int) reinterpretation is already two's complement.
            var signExtendMask = bitLen == 32 ? 0u : ~((1u << bitLen) - 1);
            return (int)(unsigned | signExtendMask);
        }

        return (int)unsigned;
    }

    /// <summary>
    ///     Reads an unsigned integer using Motorola (big-endian) numbering, as DBC files express
    ///     it with <c>@0</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         DBC numbers bits the same way for both orders - bit <c>N</c> is byte <c>N/8</c>,
    ///         bit <c>N%8</c>, bit 7 being that byte's MSB. Under Motorola the start bit is the
    ///         signal's MSB and the signal continues by descending within the byte, carrying on at
    ///         bit 7 of the next byte.
    ///     </para>
    ///     <para>
    ///         Reading the payload as a big-endian ulong makes such a signal a contiguous run, so
    ///         the whole thing reduces to one shift and mask. With <c>byte = bitPos / 8</c> and
    ///         <c>bit = bitPos % 8</c>, the MSB sits <c>byte * 8 + (7 - bit)</c> places from the
    ///         left, so the right-shift is <c>64 - (msbIndex + bitLen)</c>.
    ///     </para>
    ///     <para>
    ///         Worked example, hardware-verified: 0x55B SOC is DBC big-endian start 7, length 10.
    ///         byte 0, bit 7 gives msbIndex 0 and a shift of 54. Against captured bytes
    ///         <c>E800AA00E380135D</c> that yields <c>0b1110100000</c> = 928 = 92.8 % - the value
    ///         the vehicle actually showed. Decoding the same bits as Intel returns 1.
    ///     </para>
    /// </remarks>
    /// <param name="data">8-byte CAN frame data</param>
    /// <param name="bitPos">DBC bit position of the signal's MOST significant bit (0-63)</param>
    /// <param name="bitLen">Number of bits to read (1-32)</param>
    public static uint ReadUnsignedBe(ReadOnlySpan<byte> data, int bitPos, int bitLen)
    {
        if (data.Length < 8)
        {
            throw new ArgumentException("CAN data must be at least 8 bytes", nameof(data));
        }

        if (bitPos is < 0 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(bitPos), "Must be 0-63");
        }

        if (bitLen is <= 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitLen), "Must be 1-32");
        }

        var msbIndex = MotorolaMsbIndex(bitPos);
        if (msbIndex + bitLen > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bitLen),
                $"Motorola signal starting at bit {bitPos} with length {bitLen} runs past the end of the payload.");
        }

        var raw = BinaryPrimitives.ReadUInt64BigEndian(data);
        var mask = bitLen == 32 ? 0xFFFF_FFFFul : (1ul << bitLen) - 1ul;

        return (uint)((raw >> (64 - (msbIndex + bitLen))) & mask);
    }

    /// <summary>
    ///     Distance from the left edge of the big-endian 64-bit view to the bit DBC calls
    ///     <paramref name="bitPos" />. Shared by the readers and by the generator's bounds check,
    ///     so both agree on what fits.
    /// </summary>
    public static int MotorolaMsbIndex(int bitPos)
    {
        return (bitPos / 8 * 8) + (7 - (bitPos % 8));
    }

    /// <summary>
    ///     Reads an unsigned integer value from CAN frame data.
    /// </summary>
    /// <param name="data">8-byte CAN frame data (little-endian byte order)</param>
    /// <param name="bitPos">Starting bit position (0-63)</param>
    /// <param name="bitLen">Number of bits to read (1-32)</param>
    /// <returns>Unsigned integer value</returns>
    public static uint ReadUnsigned(ReadOnlySpan<byte> data, int bitPos, int bitLen)
    {
        if (data.Length < 8)
        {
            throw new ArgumentException("CAN data must be at least 8 bytes", nameof(data));
        }

        if (bitPos is < 0 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(bitPos), "Must be 0-63");
        }

        if (bitLen is <= 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitLen), "Must be 1-32");
        }

        // Read 8 bytes as little-endian uint64
        var raw = BinaryPrimitives.ReadUInt64LittleEndian(data);

        // Create mask for the bits we want
        var mask = bitLen == 32 ? 0xFFFF_FFFFul : (1ul << bitLen) - 1ul;

        // Shift and mask
        return (uint)((raw >> bitPos) & mask);
    }
}
