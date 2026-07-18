using ProdCanBits = ObdInsight.SourceGeneration.CanBits;

namespace ObdInsight.SourceGeneration.Tests
{
    /// <summary>
    /// Bit-level tests for the production <see cref="ObdInsight.SourceGeneration.CanBits"/> helper.
    /// The generated per-namespace CanBits copy must behave identically; that path is covered by
    /// the frame-decoding regression tests in ObdInsight.Tests and the generator snapshot tests.
    /// </summary>
    public class CanBitsTests
    {
        /// <summary>Builds an 8-byte little-endian CAN frame from a raw 64-bit value.</summary>
        private static byte[] Frame(ulong raw) => BitConverter.GetBytes(raw);

        /// <summary>Encodes a signed value as two's complement in <paramref name="bitLen"/> bits at <paramref name="bitPos"/>.</summary>
        private static byte[] FrameWithSigned(long value, int bitPos, int bitLen)
        {
            var mask = bitLen == 64 ? ulong.MaxValue : (1ul << bitLen) - 1;
            var encoded = (ulong)value & mask;
            return Frame(encoded << bitPos);
        }

        [Test]
        [Arguments(0, 2)]
        [Arguments(5, 11)]
        [Arguments(13, 11)]
        [Arguments(3, 12)]
        [Arguments(7, 15)]
        [Arguments(0, 16)]
        [Arguments(16, 16)]
        [Arguments(1, 31)]
        [Arguments(0, 32)]
        [Arguments(32, 32)]
        public async Task ReadSigned_NegativeValues_SignExtend(int bitPos, int bitLen)
        {
            var minusOne = FrameWithSigned(-1, bitPos, bitLen);
            var mostNegative = -(1L << (bitLen - 1));
            var mostNegativeFrame = FrameWithSigned(mostNegative, bitPos, bitLen);

            await Assert.That(ProdCanBits.ReadSigned(minusOne, bitPos, bitLen)).IsEqualTo(-1);
            await Assert.That(ProdCanBits.ReadSigned(mostNegativeFrame, bitPos, bitLen)).IsEqualTo((int)mostNegative);
        }

        [Test]
        [Arguments(0, 2)]
        [Arguments(13, 11)]
        [Arguments(0, 16)]
        [Arguments(1, 31)]
        [Arguments(0, 32)]
        public async Task ReadSigned_PositiveValues_Unchanged(int bitPos, int bitLen)
        {
            var mostPositive = (1L << (bitLen - 1)) - 1;
            var frame = FrameWithSigned(mostPositive, bitPos, bitLen);

            await Assert.That(ProdCanBits.ReadSigned(frame, bitPos, bitLen)).IsEqualTo((int)mostPositive);
            await Assert.That(ProdCanBits.ReadSigned(Frame(0), bitPos, bitLen)).IsEqualTo(0);
        }

        [Test]
        public async Task ReadSigned_LeafBatteryCurrentShape_DecodesNegative()
        {
            // Nissan Leaf 0x1DB battery current: bit 13, 11 bits, signed, Factor 0.5.
            // -200 raw (-100.0 A after scaling) must come back negative.
            var frame = FrameWithSigned(-200, 13, 11);

            var raw = ProdCanBits.ReadSigned(frame, 13, 11);

            await Assert.That(raw).IsEqualTo(-200);
            await Assert.That(raw * 0.5).IsEqualTo(-100.0);
        }

        [Test]
        [Arguments(0, 1, 1ul, 1u)]
        [Arguments(32, 10, 0ul, 0u)]
        [Arguments(30, 10, 720ul, 720u)]
        [Arguments(0, 32, 0xFFFF_FFFFul, 0xFFFF_FFFFu)]
        [Arguments(32, 32, 0xFFFF_FFFFul, 0xFFFF_FFFFu)]
        public async Task ReadUnsigned_ExtractsRawBits(int bitPos, int bitLen, ulong encoded, uint expected)
        {
            var frame = Frame(encoded << bitPos);

            await Assert.That(ProdCanBits.ReadUnsigned(frame, bitPos, bitLen)).IsEqualTo(expected);
        }

        [Test]
        public async Task ReadUnsigned_IgnoresNeighboringBits()
        {
            // All bits set except the 10-bit window at bit 30.
            var frame = Frame(~(((1ul << 10) - 1) << 30));

            await Assert.That(ProdCanBits.ReadUnsigned(frame, 30, 10)).IsEqualTo(0u);
        }

        [Test]
        public async Task ReadBool_ReadsSingleBit()
        {
            var frame = Frame(1ul << 29);

            await Assert.That(ProdCanBits.ReadBool(frame, 29)).IsTrue();
            await Assert.That(ProdCanBits.ReadBool(frame, 28)).IsFalse();
            await Assert.That(ProdCanBits.ReadBool(frame, 30)).IsFalse();
        }
    }
}
