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
        // Every 0x55B signal the DBC marks @0, decoded through the Motorola reader.
        //
        // Soc is unchanged at 928: it previously reached the same value by splitting the field
        // into two Intel signals and recombining them by hand, so this asserts the direct
        // mapping produces identical output.
        //
        // IrSensorWaveVoltage and SleepEnabled were NOT previously correct - both are @0 in
        // EV-can_AZE0.dbc but were declared as Intel, so they read unrelated bits.
        // SleepEnabled is the clearest evidence: Intel gave 0, which the DBC documents as
        // Reserved rather than a state the controller reports, while Motorola gives 1
        // (RefuseToSleep) - correct for a vehicle that was awake when this was captured.
        var frame = BatteryFrame_55B_AZE0.Parse(Captured("E800AA00E380135D"));

        await Assert.That(frame.Soc).IsEqualTo(928);
        await Assert.That(frame.AluAnswer).IsEqualTo(0xAA);      // 16|8@1 - Intel, unchanged
        await Assert.That(frame.IrSensorWaveVoltage).IsEqualTo(910);
        await Assert.That(frame.SleepEnabled).IsEqualTo(1);
    }

    /// <summary>
    /// A second capture five weeks later at a different charge level, so SOC is pinned by two
    /// independent observations rather than one. 972 = 97.2 %, matching the vehicle display.
    /// </summary>
    [Test]
    public async Task BatteryFrame55b_SecondCapture_SocTracksActualCharge()
    {
        var frame = BatteryFrame_55B_AZE0.Parse(Captured("F3005500E2C011B2"));

        await Assert.That(frame.Soc).IsEqualTo(972);
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

    /// <summary>
    /// Pins the byte order of 0x284's three 16-bit speed fields.
    ///
    /// The captured parked payloads read 0 for all of them, and zero is zero under either order,
    /// so they cannot show that these are big-endian. These payloads are therefore synthetic:
    /// each places a single 0x01 byte where only a Motorola read can see it.
    ///
    /// Wheel_Speed_FR is 7|16@0, so byte 0 is its high byte: 0x0100 = 256, x0.005 = 1.28 km/h.
    /// Read as Intel from bit 7 the same payload yields 0. The factors themselves stay unverified
    /// until the vehicle is driven - a stationary capture cannot confirm a scale.
    /// </summary>
    [Test]
    [Arguments("0100000000000000", 1.28, 0.0, 0.0)]   // byte 0 -> FR high byte
    [Arguments("0000010000000000", 0.0, 1.28, 0.0)]   // byte 2 -> FL high byte
    [Arguments("0000000001000000", 0.0, 0.0, 2.56)]   // byte 4 -> vehicle speed high byte, x0.01
    public async Task AbsFrame284_SpeedFields_ReadBigEndian(
        string payload, double fr, double fl, double vehicle)
    {
        var frame = AbsFrame_284_AZE0.Parse(Captured(payload));

        await Assert.That(frame.WheelSpeedFr).IsEqualTo(fr).Within(1e-9);
        await Assert.That(frame.WheelSpeedFl).IsEqualTo(fl).Within(1e-9);
        await Assert.That(frame.VehicleSpeedFromAbs).IsEqualTo(vehicle).Within(1e-9);
    }

    /// <summary>
    /// 0x260 is a 4-byte frame whose three signals are all Motorola in CAR-can_AZE0.dbc. They
    /// were declared Intel, which put every one outside its own declared range on real payloads.
    ///
    /// <c>C8127D00</c> is the most common payload across the 2026-08-31 captures, taken with the
    /// vehicle parked. PowerConsumptMotor is the physical check: ~0 kW is right for a stationary
    /// car, whereas the Intel reading claimed a constant -100 kW draw.
    ///
    /// This also exercises a 4-byte payload against Motorola signals, which is what forced
    /// GetMinimumLength to become endianness-aware - the Intel expression demanded 5 bytes for
    /// a frame the vehicle only ever sends as 4, so Parse threw on every real frame.
    /// </summary>
    [Test]
    public async Task VcmFrame260_ParkedCapture_MotorPowerDecodesWithinRange()
    {
        var frame = VcmFrame_260_AZE0.Parse(Captured("C8127D00"));

        await Assert.That(frame.PowerConsumptMotor).IsBetween(-1.0, 1.0);
        await Assert.That(frame.AvailableMotorPower).IsBetween(0, 90);
        await Assert.That(frame.MotorRegenerationPowerMax).IsBetween(0, 50);
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
    public async Task VcmFrame421_ShifterMap_DecodesAllGears_FromOneBytePayload()
    {
        // 0x421 is a 1-byte frame on the wire. The generated Parse accepts it because the
        // frame's only signal lives in byte 0 (MinimumLength = 1) — no hand-decode helper.
        // Map per OVMS: 0/1=P, 2=R, 3=N, 4=D, 7=Drive/B.
        await Assert.That(VcmFrame_421_AZE0.MinimumLength).IsEqualTo(1);

        await Assert.That(VcmFrame_421_AZE0.Parse([0x08]).DashShifterPosition).IsEqualTo(1); // Park
        await Assert.That(VcmFrame_421_AZE0.Parse([0x10]).DashShifterPosition).IsEqualTo(2); // Reverse
        await Assert.That(VcmFrame_421_AZE0.Parse([0x18]).DashShifterPosition).IsEqualTo(3); // Neutral
        await Assert.That(VcmFrame_421_AZE0.Parse([0x20]).DashShifterPosition).IsEqualTo(4); // Drive
        await Assert.That(VcmFrame_421_AZE0.Parse([0x38]).DashShifterPosition).IsEqualTo(7); // Drive/B
        // Bits outside 3-5 must not bleed in.
        await Assert.That(VcmFrame_421_AZE0.Parse([0xC7]).DashShifterPosition).IsEqualTo(0);
    }

    [Test]
    public async Task VcmFrame176_SevenBytePayload_DecodesWithoutPadding()
    {
        // 7 bytes on the wire; the CRC signal ends in byte 6, so MinimumLength is 7.
        // ASCD speed is bits 39-46: byte5 = 0x32 puts 100 (0x64) across the byte boundary.
        await Assert.That(VcmFrame_176_AZE0.MinimumLength).IsEqualTo(7);

        var frame = VcmFrame_176_AZE0.Parse([0x00, 0x00, 0x00, 0x00, 0x00, 0x32, 0x5A]);

        await Assert.That(frame.AscdSpeedRequest).IsEqualTo(100);
        await Assert.That(frame.Crc).IsEqualTo(0x5A);
    }

    [Test]
    public async Task Parse_PayloadShorterThanMinimumLength_Throws()
    {
        // A truncated payload cannot carry every signal — that is an error, not a silent
        // zero-fill. (Frames longer than declared are fine: extra bytes are ignored.)
        await Assert.That(() => { _ = VcmFrame_176_AZE0.Parse([0x00, 0x00, 0x00]); })
            .Throws<ArgumentException>();
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

    /// <summary>
    /// Pins 0x60D bits 3 and 4 to the correct doors.
    ///
    /// These were transposed until 2026-08-31 and nothing caught it: both bits decoded, and the
    /// only existing 0x60D test used the all-doors-closed payload, where the two are
    /// indistinguishable. Telling them apart requires knowing which door was physically open,
    /// which is why these payloads come from guided stimulus probes on a 2017 AZE0 rather than
    /// from a DBC or a passive capture.
    ///
    /// Captured, three repetitions each, byte 0 identical every time:
    ///   all closed       0x06 = 0000 0110
    ///   driver open      0x0E = 0000 1110   (bit 3)
    ///   passenger open   0x16 = 0001 0110   (bit 4)
    ///
    /// Bits 1-2 stay set throughout (ParkingLights) and bits 5-7 stay clear, so the difference
    /// between the two payloads is exactly the one bit under test.
    /// </summary>
    [Test]
    public async Task BcmFrame60d_DriverDoorOpen_SetsBit3Only()
    {
        var frame = BcmFrame_60D_AZE0.Parse(Captured("0E06000000000000"));

        await Assert.That(frame.DriverDoorOpen).IsTrue();
        await Assert.That(frame.PassengerDoorOpen).IsFalse();
        await Assert.That(frame.RearLeftDoorOpen).IsFalse();
        await Assert.That(frame.RearRightDoorOpen).IsFalse();
        await Assert.That(frame.TrunkOpen).IsFalse();
    }

    [Test]
    public async Task BcmFrame60d_PassengerDoorOpen_SetsBit4Only()
    {
        var frame = BcmFrame_60D_AZE0.Parse(Captured("1606000000000000"));

        await Assert.That(frame.PassengerDoorOpen).IsTrue();
        await Assert.That(frame.DriverDoorOpen).IsFalse();
        await Assert.That(frame.RearLeftDoorOpen).IsFalse();
        await Assert.That(frame.RearRightDoorOpen).IsFalse();
        await Assert.That(frame.TrunkOpen).IsFalse();
    }

    /// <summary>
    /// The confirmed-correct neighbours, pinned alongside the fix. Their being right either side
    /// of the swap is what made the swap unambiguous rather than a shifted field.
    /// </summary>
    [Test]
    public async Task BcmFrame60d_LightingAndLocks_MatchGuidedProbeBits()
    {
        // fog (bit 8) and high beam (bit 11) both on: byte 1 = 0x09 over the closed baseline.
        var lights = BcmFrame_60D_AZE0.Parse(Captured("0609000000000000"));

        await Assert.That(lights.FogLights).IsTrue();
        await Assert.That(lights.MainBeam).IsTrue();
        await Assert.That(lights.DriverDoorOpen).IsFalse();
        await Assert.That(lights.PassengerDoorOpen).IsFalse();
    }

    /// <summary>
    /// The three remaining openings, each captured with only that one open. Byte 0 walks
    /// 0x26 / 0x46 / 0x86 over the 0x06 closed baseline - one bit at a time, which is what makes
    /// each assignment unambiguous.
    /// </summary>
    [Test]
    [Arguments("2606000000000000", "rear-left")]
    [Arguments("4606000000000000", "rear-right")]
    [Arguments("8606000000000000", "hatch")]
    public async Task BcmFrame60d_EachRearOpening_SetsOnlyItsOwnBit(string payload, string which)
    {
        var frame = BcmFrame_60D_AZE0.Parse(Captured(payload));

        await Assert.That(frame.RearLeftDoorOpen).IsEqualTo(which == "rear-left");
        await Assert.That(frame.RearRightDoorOpen).IsEqualTo(which == "rear-right");
        await Assert.That(frame.TrunkOpen).IsEqualTo(which == "hatch");

        // The front doors stay shut throughout, so a future off-by-one into bits 3/4 fails here.
        await Assert.That(frame.DriverDoorOpen).IsFalse();
        await Assert.That(frame.PassengerDoorOpen).IsFalse();
    }

    [Test]
    public async Task BcmFrame60d_Locked_SetsBothDoorLockBits()
    {
        var frame = BcmFrame_60D_AZE0.Parse(Captured("0606180000000000"));

        await Assert.That(frame.DoorLockStatusOtherDoors).IsTrue();
        await Assert.That(frame.DoorLockStatusDriverDoor).IsTrue();
    }

    /// <summary>
    /// Indicator lamp feedback, captured mid-flash. Both phases are asserted because a blinking
    /// bit is only meaningful as a pair - a test pinning one phase would pass against a decoder
    /// that returned a constant.
    /// </summary>
    [Test]
    public async Task BcmFrame60d_LeftIndicatorLamp_TracksBothBlinkPhases()
    {
        var lit = BcmFrame_60D_AZE0.Parse(Captured("0026000000000000"));
        var dark = BcmFrame_60D_AZE0.Parse(Captured("0006000000000000"));

        await Assert.That(lit.LeftTurnSignalFeedback).IsTrue();
        await Assert.That(dark.LeftTurnSignalFeedback).IsFalse();

        // The right lamp is dark in both frames - only the left stalk was operated.
        await Assert.That(lit.RightTurnSignalFeedback).IsFalse();
        await Assert.That(dark.RightTurnSignalFeedback).IsFalse();
    }

    /// <summary>
    /// 0x174 byte 3 carries the shifter position: 0xAA in Park, 0x99 in Reverse. The guided probe
    /// flagged bits 24, 25, 28 and 29 as responding, and 0xAA ^ 0x99 = 0x33 - precisely those
    /// four bits. Byte 4 is a free-running counter and is deliberately not asserted.
    /// </summary>
    [Test]
    [Arguments("000000AA0A000000", 170)]
    [Arguments("0000009908000000", 153)]
    public async Task VcmFrame174_ShifterPosition_MatchesCapturedGearStates(string payload, int expected)
    {
        var frame = VcmFrame_174_AZE0.Parse(Captured(payload));

        await Assert.That(frame.ShifterPosition).IsEqualTo(expected);
    }

    /// <summary>
    /// 0x54B FanSpeed occupies bits 35-39. Captured with the fan at maximum and off:
    /// byte 4 = 0x3C vs 0x04, giving (0x3C &gt;&gt; 3) &amp; 0x1F = 7 and 0.
    ///
    /// ClimateControlStatus is asserted alongside it because byte 0 is claimed by three separate
    /// signals in the current definition with incompatible scalings; these bytes match its
    /// documented 0x10/0x11 values and nothing else.
    /// </summary>
    [Test]
    public async Task HvacFrame54b_FanSpeed_MatchesCapturedMaxAndOff()
    {
        var max = HvacFrame_54B_AZE0.Parse(Captured("104888123C000001"));
        var off = HvacFrame_54B_AZE0.Parse(Captured("1108800A04000000"));

        await Assert.That(max.FanSpeed).IsEqualTo(7);
        await Assert.That(off.FanSpeed).IsEqualTo(0);

        await Assert.That(max.ClimateControlStatus).IsEqualTo(0x10);
        await Assert.That(off.ClimateControlStatus).IsEqualTo(0x11);
    }
}
