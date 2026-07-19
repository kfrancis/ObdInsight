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
    // ------------------------------------------------------------------------------------
    // EV-CAN frame layouts (1DB/1DC/1DA/5C0): no hardware captures exist — these frames are
    // invisible on stock ELM327 adapters (see CLAUDE.md gotcha). Byte encodings below are
    // hand-computed from the OVMS vehicle_nissanleaf.cpp reference decoders (repo root),
    // which read EV-CAN directly. Guards both the signed-decode defect (AUDIT.md C1) and
    // the Motorola-transcription defect (AUDIT.md §7 addendum 2).
    // ------------------------------------------------------------------------------------

    [Test]
    public async Task BatteryFrame1db_NegativeCurrent_DecodesAsCharge()
    {
        // Current = 11-bit two's complement (byte0 + byte1[7..5]), 0.5 A/bit.
        // Raw -200 = 0x738 => byte0 0xE7, byte1 0x00 => -100.0 A.
        var frame = BatteryFrame_1DB_AZE0.Parse(Captured("E700000000000000"));

        await Assert.That(frame.Current).IsEqualTo(-100.0);
    }

    [Test]
    public async Task BatteryFrame1db_PositiveCurrent_DecodesAsDischarge()
    {
        // Raw 100 = 0x064 => byte0 0x0C, byte1 0x80 => 50.0 A.
        var frame = BatteryFrame_1DB_AZE0.Parse(Captured("0C80000000000000"));

        await Assert.That(frame.Current).IsEqualTo(50.0);
    }

    [Test]
    public async Task BatteryFrame1db_VoltageAndCurrent_DecodeTogether()
    {
        // Voltage = 10-bit (byte2 + byte3[7..6]), 0.5 V/bit: raw 720 => byte2 0xB4, byte3 0x00 => 360.0 V.
        // Current raw -32 = 0x7E0 => byte0 0xFC, byte1 0x00 => -16.0 A.
        var frame = BatteryFrame_1DB_AZE0.Parse(Captured("FC00B40000000000"));

        await Assert.That(frame.Voltage).IsEqualTo(360.0);
        await Assert.That(frame.Current).IsEqualTo(-16.0);
    }

    [Test]
    public async Task BatteryFrame1dc_PowerLimits_DecodePerOvmsLayout()
    {
        // Discharge = (byte0<<2 | byte1>>6)/4: 0x6E,0x19 => 440 => 110.0 kW.
        // Charge = ((byte1&0x3F)<<4 | byte2>>4)/4: 0x19,0x02 => 400 => 100.0 kW.
        //   (10-bit field per the DBC; OVMS's <<2 is inconsistent with its own
        //   neighboring 10-bit fields and would overlap the low nibble.)
        // Charger max = ((byte2&0x0F)<<6 | byte3>>2)*0.1 - 10: 0x02,0x98 => 166 => 6.6 kW.
        var frame = BatteryFrame_1DC_AZE0.Parse(Captured("6E19029800000000"));

        await Assert.That(frame.DischargePowerLimit).IsEqualTo(110.0);
        await Assert.That(frame.ChargePowerLimit).IsEqualTo(100.0);
        await Assert.That(frame.MaxPowerForCharger).IsEqualTo(6.6).Within(1e-9);
    }

    [Test]
    public async Task InverterFrame1da_NegativeTorqueAndReverseRpm_DecodeSigned()
    {
        // Torque = 11-bit two's complement (byte2[2..0] + byte3), 0.5 Nm/bit:
        //   raw -80 = 0x7B0 => byte2 0x07, byte3 0xB0 => -40.0 Nm (regen).
        // RPM = 15-bit two's complement (byte4[6..0] + byte5) / 2:
        //   raw -3000 = 0x7448 => byte4 0x74, byte5 0x48 => -1500 rpm (reverse).
        var frame = InvMcFrame_1DA_AZE0.Parse(Captured("000007B074480000"));

        await Assert.That(frame.EffectiveTorque).IsEqualTo(-40.0);
        await Assert.That(frame.OutputRevolution).IsEqualTo(-1500);
    }

    [Test]
    public async Task BatteryFrame5c0_PackTemperature_FullByteHalfDegree()
    {
        // Temp = byte2 * 0.5 - 40 (OVMS: d[2]/2 - 40); the pre-fix (17,7) layout halved twice.
        // byte0 0x80 => HistoricalDataSwitchFlag = 2 (average); byte2 0x5A (90) => 5.0 °C.
        var frame = BatteryFrame_5C0_AZE0.Parse(Captured("80005A0000000000"));

        await Assert.That(frame.HistoricalDataSwitchFlag).IsEqualTo(2);
        await Assert.That(frame.HistDataTemperatureAvg).IsEqualTo(5.0);
    }

    // ------------------------------------------------------------------------------------
    // Hardware-capture regression tests. Raw bytes below are verbatim from a 2017 Leaf AZE0
    // (30 kWh, parked in READY, charging, ambient ~22 °C, pack ~96%) captured 2026-07-18 —
    // see docs/FRAME_LAYOUT_AUDIT.md. Expected values are the physically-verified decodes.
    // ------------------------------------------------------------------------------------

    /// <summary>Builds an 8-byte CAN frame from captured hex (byte 0 first).</summary>
    private static byte[] Captured(string hex) => Convert.FromHexString(hex);

    [Test]
    public async Task AbsFrame284_ParkedCapture_SpeedsZeroAndCountersExposed()
    {
        // Bytes 6-7 are free-running counters (next frame ...35BB). The pre-audit layout
        // decoded them as 61-496 km/h vehicle speed while stationary.
        var frame = AbsFrame_284_AZE0.Parse(Captured("00000000000034BA"));

        await Assert.That(frame.WheelSpeedFr).IsEqualTo(0.0);
        await Assert.That(frame.WheelSpeedFl).IsEqualTo(0.0);
        await Assert.That(frame.VehicleSpeedFromAbs).IsEqualTo(0.0);
        await Assert.That(frame.MessageCounter1).IsEqualTo(0x34);
        await Assert.That(frame.MessageCounter2).IsEqualTo(0xBA);

        var next = AbsFrame_284_AZE0.Parse(Captured("00000000000035BB"));
        await Assert.That(next.VehicleSpeedFromAbs).IsEqualTo(0.0);
        await Assert.That(next.MessageCounter1).IsEqualTo(0x35);
        await Assert.That(next.MessageCounter2).IsEqualTo(0xBB);
    }

    [Test]
    public async Task AbsFrame285_ParkedCapture_RearSpeedsZero()
    {
        var frame = AbsFrame_285_AZE0.Parse(Captured("00000000000035BC"));

        await Assert.That(frame.WheelSpeedRr).IsEqualTo(0.0);
        await Assert.That(frame.WheelSpeedRl).IsEqualTo(0.0);
        await Assert.That(frame.MessageCounter1).IsEqualTo(0x35);
        await Assert.That(frame.MessageCounter2).IsEqualTo(0xBC);
    }

    [Test]
    public async Task BatteryFrame55b_NearFullCapture_SocDecodesTenthsOfPercent()
    {
        // Motorola 10-bit SOC (byte0 + byte1[7..6]): E8 00 => 928 = 92.8% (pack ~96% full).
        // The pre-audit Intel transcription decoded 1.
        var frame = BatteryFrame_55B_AZE0.Parse(Captured("E800AA00E380135D"));

        await Assert.That(frame.Soc).IsEqualTo(928);
        await Assert.That(frame.AluAnswer).IsEqualTo(0xAA);
        await Assert.That(frame.IrSensorWaveVoltage).IsEqualTo(769);
    }

    [Test]
    public async Task AbsFrame245_ParkedCapture_TorquesNearZero()
    {
        // 0x7FE/0x802 raw around the 0x800 center => ±1.0 Nm ≈ neutral while parked.
        // The pre-audit layout decoded VdcTorqueDownRequest1=3720 Nm, MotorTorque=232.5 Nm.
        var frame = AbsFrame_245_AZE0.Parse(Captured("7FE8021835007FE1"));

        await Assert.That(frame.VdcTorqueDownRequest1).IsEqualTo(-1.0);
        await Assert.That(frame.MotorTorqueRequestAbs).IsEqualTo(1.0);
        await Assert.That(frame.VdcTorqueDownRequest2).IsEqualTo(-1.0);
        await Assert.That(frame.TorqueDownRequestType).IsEqualTo(0);
        await Assert.That(frame.Unknown7).IsEqualTo(1);

        var next = AbsFrame_245_AZE0.Parse(Captured("7FE8021836007FE2"));
        await Assert.That(next.Unknown4).IsEqualTo(0x36);
        await Assert.That(next.Unknown7).IsEqualTo(2);
    }

    [Test]
    public async Task BatteryFrame5bc_ChargingCapture_GidsAndChargeTimeSentinel()
    {
        // GIDS 5D C0 => 375 (pre-audit decoded 384). MaxGids (byte5 bit4) is the gids mux
        // selector (confirmed vs OVMS): set here, so 375 = maximum gids = 30.0 kWh pack
        // capacity, not remaining. Charge time BF FF => 0x1FFF unavailable sentinel
        // (pre-audit decoded 4091 from a 12-bit misread).
        var frame = BatteryFrame_5BC_AZE0.Parse(Captured("5DC0F0648212BFFF"));

        await Assert.That(frame.RemainCapacityGids).IsEqualTo(375);
        await Assert.That(frame.GidsValid).IsTrue();
        await Assert.That(frame.MaxGids).IsTrue();
        await Assert.That(frame.RemainChargeTime).IsEqualTo(0x1FFF);
        await Assert.That(frame.RemainChargeTimeAvailable).IsFalse();
        await Assert.That(frame.CapacityDeteriorationRate).IsEqualTo(65);
    }

    [Test]
    public async Task AbsFrame292_Capture_LeadAcidVoltageAndBrakePressure()
    {
        var frame = AbsFrame_292_AZE0.Parse(Captured("83C7F67FE0000001"));

        await Assert.That(frame.LeadAcidBatteryVoltage).IsEqualTo(12.7).Within(1e-9);
        await Assert.That(frame.FrictionBrakePressure).IsEqualTo(0);
    }

    [Test]
    public async Task VcmFrame510_Capture_AmbientTempAndChargeMode()
    {
        var frame = VcmFrame_510_AZE0.Parse(Captured("55C830002E00007D"));

        await Assert.That(frame.OutsideAmbientTemperature).IsEqualTo(22.5);
        await Assert.That(frame.ChargeMode).IsEqualTo(2);
        await Assert.That(frame.ClimateControlActive).IsFalse();
    }

    [Test]
    public async Task VcmFrame5a9_ChargingCapture_RangeDecodesKm()
    {
        var frame = VcmFrame_5A9_AZE0.Parse(Captured("8526C01104100000"));

        await Assert.That(frame.RangeInstrumentCluster).IsEqualTo(179.2).Within(1e-9);
    }

    [Test]
    public async Task AbsFrame354_ParkedCapture_SpeedZeroEspEnabled()
    {
        var frame = AbsFrame_354_AZE0.Parse(Captured("0000000000080000"));

        await Assert.That(frame.VehicleSpeedAbs).IsEqualTo(0);
        await Assert.That(frame.EspDisabled).IsFalse();
    }

    [Test]
    public async Task VcmFrame180_ParkedCapture_MotorAmpAndThrottleZero()
    {
        // Byte 6 (0x2E) is a counter; must not bleed into MotorAmp/Throttle.
        var frame = VcmFrame_180_AZE0.Parse(Captured("0000000000002E00"));

        await Assert.That(frame.MotorAmp).IsEqualTo(0);
        await Assert.That(frame.ThrottlePosition).IsEqualTo(0.0);
    }

    [Test]
    public async Task VcmFrame5b3_AppLogCapture_SohDecodes66Percent()
    {
        // Captured 2025-12-06 by a third-party app on the same car (see AUDIT.md addendum 2).
        // OVMS layout: SOH = byte1 >> 1. 0x84 >> 1 = 66%.
        var frame = VcmFrame_5B3_AZE0.Parse(Captured("5084FFFB20B5A18A"));

        await Assert.That(frame.Soh).IsEqualTo(66);
        await Assert.That(frame.SohValid).IsTrue();
    }

    [Test]
    public async Task VcmFrame421_ShifterMap_DecodesAllGears()
    {
        // 0x421 is a 1-byte frame — decoded from raw byte 0, not the generated 8-byte Parse.
        // Map per OVMS: 0/1=P, 2=R, 3=N, 4=D, 7=Drive/B.
        await Assert.That(VcmFrame_421_AZE0.ShifterPositionFromByte0(0x08)).IsEqualTo(1); // Park
        await Assert.That(VcmFrame_421_AZE0.ShifterPositionFromByte0(0x10)).IsEqualTo(2); // Reverse
        await Assert.That(VcmFrame_421_AZE0.ShifterPositionFromByte0(0x18)).IsEqualTo(3); // Neutral
        await Assert.That(VcmFrame_421_AZE0.ShifterPositionFromByte0(0x20)).IsEqualTo(4); // Drive
        await Assert.That(VcmFrame_421_AZE0.ShifterPositionFromByte0(0x38)).IsEqualTo(7); // Drive/B
        // Bits outside 3-5 must not bleed in.
        await Assert.That(VcmFrame_421_AZE0.ShifterPositionFromByte0(0xC7)).IsEqualTo(0);
    }

    [Test]
    public async Task BcmFrame60d_ParkedCapture_DoorsClosedSignalsOffReady()
    {
        var frame = BcmFrame_60D_AZE0.Parse(Captured("0606000000000000"));

        await Assert.That(frame.DriverDoorOpen).IsFalse();
        await Assert.That(frame.PassengerDoorOpen).IsFalse();
        await Assert.That(frame.RearLeftDoorOpen).IsFalse();
        await Assert.That(frame.RearRightDoorOpen).IsFalse();
        await Assert.That(frame.TrunkOpen).IsFalse();
        await Assert.That(frame.LeftTurnSignalFeedback).IsFalse();
        await Assert.That(frame.RightTurnSignalFeedback).IsFalse();
        await Assert.That(frame.VehicleState).IsEqualTo(3); // ON/Ready (parked in READY)
    }
}
