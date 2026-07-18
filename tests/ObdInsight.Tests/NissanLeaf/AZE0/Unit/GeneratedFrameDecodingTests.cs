using ObdInsight.Core.Vehicles.Implementations.Nissan.AZE0;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace OdbTestApp.Tests.NissanLeaf.AZE0.Unit;

/// <summary>
/// Regression tests for source-generated CAN frame decoders, exercising the actual
/// generated production code path (not a test-side re-implementation).
/// Guards against the signed-signal decode defect where negative raw values
/// (regen/charge current, negative torque) decoded as huge positive numbers.
/// </summary>
public class GeneratedFrameDecodingTests
{
    /// <summary>Builds an 8-byte little-endian CAN frame from a raw 64-bit value.</summary>
    private static byte[] Frame(ulong raw) => BitConverter.GetBytes(raw);

    /// <summary>Encodes a signed value as two's complement in <paramref name="bitLen"/> bits at <paramref name="bitPos"/>.</summary>
    private static ulong SignedField(long value, int bitPos, int bitLen)
        => (((ulong)value) & ((1ul << bitLen) - 1)) << bitPos;

    [Test]
    public async Task BatteryFrame1db_NegativeCurrent_DecodesAsCharge()
    {
        // Current: bit 13, 11 bits, signed, Factor 0.5 A/bit.
        // Raw -200 => -100.0 A (charging). The defect decoded this as ~2.1e9 A.
        var data = Frame(SignedField(-200, 13, 11));

        var frame = BatteryFrame_1DB_AZE0.Parse(data);

        await Assert.That(frame.Current).IsEqualTo(-100.0);
    }

    [Test]
    public async Task BatteryFrame1db_PositiveCurrent_DecodesAsDischarge()
    {
        // Raw 100 => 50.0 A discharge.
        var data = Frame(SignedField(100, 13, 11));

        var frame = BatteryFrame_1DB_AZE0.Parse(data);

        await Assert.That(frame.Current).IsEqualTo(50.0);
    }

    [Test]
    public async Task BatteryFrame1db_VoltageAndCurrent_DecodeTogether()
    {
        // Voltage: bit 30, 10 bits, unsigned, Factor 0.5 V/bit. Raw 720 => 360.0 V.
        // Current: raw -32 => -16.0 A.
        var data = Frame(SignedField(-32, 13, 11) | (720ul << 30));

        var frame = BatteryFrame_1DB_AZE0.Parse(data);

        await Assert.That(frame.Voltage).IsEqualTo(360.0);
        await Assert.That(frame.Current).IsEqualTo(-16.0);
    }

    [Test]
    public async Task InverterFrame1da_NegativeTorqueAndReverseRpm_DecodeSigned()
    {
        // Effective torque: bit 18, 11 bits, signed, Factor 0.5 Nm/bit. Raw -80 => -40.0 Nm (regen).
        // Motor RPM: bit 39, 15 bits, signed, unscaled. Raw -1500 => reverse.
        var data = Frame(SignedField(-80, 18, 11) | SignedField(-1500, 39, 15));

        var frame = InvMcFrame_1DA_AZE0.Parse(data);

        await Assert.That(frame.EffectiveTorque).IsEqualTo(-40.0);
        await Assert.That(frame.OutputRevolution).IsEqualTo(-1500);
    }
}
