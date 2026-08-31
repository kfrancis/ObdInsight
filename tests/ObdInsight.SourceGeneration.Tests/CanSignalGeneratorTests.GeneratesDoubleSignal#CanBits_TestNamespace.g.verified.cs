//HintName: CanBits_TestNamespace.g.cs
#nullable enable
using System;
using System.Buffers.Binary;
namespace TestNamespace
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
