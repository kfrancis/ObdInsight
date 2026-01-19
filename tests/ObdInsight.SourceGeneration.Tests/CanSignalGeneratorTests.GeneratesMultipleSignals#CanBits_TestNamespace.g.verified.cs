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
        public static uint ReadSigned(ReadOnlySpan<byte> data, int bitPos, int bitLen)
        {
            var unsigned = ReadUnsigned(data, bitPos, bitLen);
            var signBitMask = 1u << (bitLen - 1);
            if ((unsigned & signBitMask) != 0)
            {
                var signExtendMask = ~((1u << bitLen) - 1);
                return unsigned | signExtendMask;
            }
            return unsigned;
        }
        public static uint ReadUnsigned(ReadOnlySpan<byte> data, int bitPos, int bitLen)
        {
            var raw = BinaryPrimitives.ReadUInt64LittleEndian(data);
            var mask = bitLen == 32 ? 0xFFFF_FFFFul : ((1ul << bitLen) - 1ul);
            return (uint)((raw >> bitPos) & mask);
        }
    }
}
