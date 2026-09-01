//HintName: CanBits_Vehicles_Nissan_Leaf.g.cs
#nullable enable
using System;
using System.Buffers.Binary;
namespace Vehicles.Nissan.Leaf
{
    // Helper class for raw CAN frame bit manipulation
    static class CanBits
    {
        public static bool ReadBool(ReadOnlySpan<byte> data, int bitPos)
        {
            return ReadUnsigned(data, bitPos, 1) != 0;
        }
        public static int ReadSigned(ReadOnlySpan<byte> data, int bitPos, int bitLen)
        {
            var unsigned = ReadUnsigned(data, bitPos, bitLen);
            var signBitMask = 1u << (bitLen - 1);
            if ((unsigned & signBitMask) != 0)
            {
                // Sign extend; at 32 bits the (int) reinterpretation is already two's complement.
                var signExtendMask = bitLen == 32 ? 0u : ~((1u << bitLen) - 1);
                return (int)(unsigned | signExtendMask);
            }
            return (int)unsigned;
        }
        public static uint ReadUnsigned(ReadOnlySpan<byte> data, int bitPos, int bitLen)
        {
            var raw = ReadPayload(data);
            var mask = bitLen == 32 ? 0xFFFF_FFFFul : ((1ul << bitLen) - 1ul);
            return (uint)((raw >> bitPos) & mask);
        }
        public static bool ReadBoolBe(ReadOnlySpan<byte> data, int bitPos)
        {
            return ReadUnsignedBe(data, bitPos, 1) != 0;
        }
        public static int ReadSignedBe(ReadOnlySpan<byte> data, int bitPos, int bitLen)
        {
            var unsigned = ReadUnsignedBe(data, bitPos, bitLen);
            var signBitMask = 1u << (bitLen - 1);
            if ((unsigned & signBitMask) != 0)
            {
                var signExtendMask = bitLen == 32 ? 0u : ~((1u << bitLen) - 1);
                return (int)(unsigned | signExtendMask);
            }
            return (int)unsigned;
        }
        // DBC big-endian (@0): bitPos is the signal's MOST significant bit, and the signal
        // descends within the byte, continuing at bit 7 of the next. Read as a big-endian
        // ulong it becomes one contiguous run, so it reduces to a shift and a mask.
        public static uint ReadUnsignedBe(ReadOnlySpan<byte> data, int bitPos, int bitLen)
        {
            var raw = ReadPayloadBe(data);
            var msbIndex = ((bitPos / 8) * 8) + (7 - (bitPos % 8));
            var mask = bitLen == 32 ? 0xFFFF_FFFFul : ((1ul << bitLen) - 1ul);
            return (uint)((raw >> (64 - (msbIndex + bitLen))) & mask);
        }
        // Big-endian counterpart of ReadPayload: byte 0 is the MOST significant, so a short
        // payload zero-extends on the right rather than the left.
        private static ulong ReadPayloadBe(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 8)
            {
                return BinaryPrimitives.ReadUInt64BigEndian(data);
            }
            ulong raw = 0;
            for (var i = 0; i < data.Length; i++)
            {
                raw |= (ulong)data[i] << ((7 - i) * 8);
            }
            return raw;
        }
        // Payloads shorter than 8 bytes are zero-extended: the frame's own MinimumLength
        // guard has already established that every signal it decodes is within range.
        private static ulong ReadPayload(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 8)
            {
                return BinaryPrimitives.ReadUInt64LittleEndian(data);
            }
            ulong raw = 0;
            for (var i = 0; i < data.Length; i++)
            {
                raw |= (ulong)data[i] << (i * 8);
            }
            return raw;
        }
    }
}
