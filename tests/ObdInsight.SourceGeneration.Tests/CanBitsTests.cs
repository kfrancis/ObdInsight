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

        // ------------------------------------------------------------ Motorola

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        /// <summary>
        /// The case that motivated Motorola support, checked against bytes captured from a real
        /// vehicle rather than constructed to fit.
        ///
        /// 0x55B SOC is DBC big-endian, start bit 7, length 10. Two independent captures of a
        /// 2017 AZE0, months apart at different charge levels, decode to values matching what the
        /// vehicle displayed. Reading the identical bits as Intel returns 1 - which is exactly the
        /// symptom recorded in docs/FRAME_LAYOUT_AUDIT.md before this existed.
        /// </summary>
        [Test]
        [Arguments("E800AA00E380135D", 928u)]   // 2026-07-18 capture: 92.8 %
        [Arguments("F3005500E2C011B2", 972u)]   // 2026-08-31 capture: 97.2 %, pack fuller
        public async Task ReadUnsignedBe_DecodesCapturedLeafSoc(string payload, uint expected)
        {
            await Assert.That(ProdCanBits.ReadUnsignedBe(Hex(payload), 7, 10)).IsEqualTo(expected);
        }

        [Test]
        public async Task ReadUnsignedBe_IntelReadingOfSameBits_IsWrong()
        {
            // Guards the premise: if these ever agreed, the Motorola path would be pointless and
            // one of the two readers would be broken.
            var frame = Hex("E800AA00E380135D");

            await Assert.That(ProdCanBits.ReadUnsigned(frame, 7, 10)).IsNotEqualTo(928u);
        }

        /// <summary>
        /// Start bit is the signal's MSB and the run descends through the big-endian view, so a
        /// signal at bit 7 of byte 0 begins at the very left of the frame.
        /// </summary>
        [Test]
        [Arguments(7, 0)]     // byte 0, MSB -> leftmost
        [Arguments(0, 7)]     // byte 0, LSB -> 8th from the left
        [Arguments(15, 8)]    // byte 1, MSB
        [Arguments(63, 56)]   // byte 7, MSB
        public async Task MotorolaMsbIndex_MapsDbcPositionToBigEndianOffset(int bitPos, int expected)
        {
            await Assert.That(ProdCanBits.MotorolaMsbIndex(bitPos)).IsEqualTo(expected);
        }

        /// <summary>A byte-aligned big-endian byte is just that byte, whichever one it is.</summary>
        [Test]
        [Arguments(7, 0xDEu)]
        [Arguments(15, 0xADu)]
        [Arguments(23, 0xBEu)]
        [Arguments(31, 0xEFu)]
        public async Task ReadUnsignedBe_ByteAligned_ReturnsThatByte(int bitPos, uint expected)
        {
            await Assert.That(ProdCanBits.ReadUnsignedBe(Hex("DEADBEEF00000000"), bitPos, 8))
                .IsEqualTo(expected);
        }

        /// <summary>A big-endian signal crossing a byte boundary joins the halves in order.</summary>
        [Test]
        public async Task ReadUnsignedBe_CrossesByteBoundary()
        {
            // Bits 0..11 of the BE view of 0xABCD... = 0xABC.
            await Assert.That(ProdCanBits.ReadUnsignedBe(Hex("ABCD000000000000"), 7, 12))
                .IsEqualTo(0xABCu);
        }

        [Test]
        public async Task ReadSignedBe_SignExtends()
        {
            // 0xFF80 >> 4 as a 12-bit signed value = 0xFF8 = -8.
            await Assert.That(ProdCanBits.ReadSignedBe(Hex("FF80000000000000"), 7, 12)).IsEqualTo(-8);
        }

        [Test]
        public async Task ReadBoolBe_ReadsSingleBit()
        {
            var frame = Hex("8000000000000001");

            await Assert.That(ProdCanBits.ReadBoolBe(frame, 7)).IsTrue();    // byte 0 MSB
            await Assert.That(ProdCanBits.ReadBoolBe(frame, 6)).IsFalse();
            await Assert.That(ProdCanBits.ReadBoolBe(frame, 56)).IsTrue();   // byte 7 LSB
        }

        /// <summary>
        /// A Motorola signal running off the end must throw rather than silently wrap. The shift
        /// count would otherwise be masked to 6 bits and read an unrelated part of the frame.
        /// </summary>
        [Test]
        public async Task ReadUnsignedBe_RunningPastTheEnd_Throws()
        {
            // Bit 63 is byte 7's MSB, offset 56 in the BE view; 56 + 16 > 64.
            await Assert.That(() => ProdCanBits.ReadUnsignedBe(Hex("0000000000000000"), 63, 16))
                .Throws<ArgumentOutOfRangeException>();
        }
    }
}
