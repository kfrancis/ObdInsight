# Frame Layout Audit — hardware evidence 2026-07-18

Task: verify/fix `[CanSignal]` bit layouts against raw frames captured on a 2017 Leaf AZE0
(30 kWh, parked in READY, **charging**, ambient ~22 °C, battery near full ~96 %).
Method: for each suspect signal, hand-decode the raw hex below under the current attribute
definition, compare to physical truth, correct bitStart/length/factor/offset (watch for
Motorola→Intel transcription errors: DBC start bits 7/15/23… are MSB-of-byte in Motorola).
Fix definitions in `Frames/*.cs`, add decode unit tests with these exact bytes, run suite.

## Ground truth during capture
BMS 2101: V=392.11, I=−1.863 A (charging), Hx=34.06 %, AHR=51.67 Ah. Dash range ~179 km.
Parked: speed 0, wheel speeds 0, throttle 0, motor amps 0. Doors closed/unlocked. Climate off.

## CONFIRMED-CORRECT decodes (regression-lock these with the raw bytes)
- 292 `83C7F67FE0000001` → LeadAcidBatteryVoltage=12.70 ✓, FrictionBrakePressure=0 ✓
- 510 `55C830002E00007D` → OutsideAmbientTemperature=22.5 ✓, ChargeMode=2 ✓, ClimateOff ✓
- 5A9 `8526C01104100000` → RangeInstrumentCluster=179.2 ✓
- 390 `04000003008000C7` → ChargeStatus=2, ChargePower=0.4 (0.7 kW actual — factor plausible)
- 354 `0000000000080000` → VehicleSpeedAbs=0 ✓, EspDisabled=false ✓
- 180 `0000000000002E00` → MotorAmp=0, Throttle=0 ✓ (byte 6 = counter)
- 60D `0606000000000000` → doors closed, signals off ✓
- 174 `000000AA0E000000` → ShifterPosition=170 (in P — map value)
- 55B IrSensorWaveVoltage=769 plausible

## BROKEN — fix these
1. **AbsFrame_284_AZE0.VehicleSpeedFromAbs decodes a free-running counter.**
   Raw `00000000000034BA` → next frame `…35BB` (bytes 6–7 increment). Capability read
   61–496 km/h while parked. Wheel speeds (bytes 0–3?) correctly 0. Move/remove the
   vehicle-speed signal; bytes 6–7 are a message counter, not speed.
   ALSO: `VcmFrame_284_AZE0` (Nissan.AZE0 ns) and `AbsFrame_284_AZE0` (Leaf.AZE0.Frames ns)
   both claim CAN 0x284 — resolve the duplicate (routers pick per-namespace; capabilities
   may decode different layouts from the same bytes).
2. **BatteryFrame_55B_AZE0.Soc = 1** (expected ~960 at 0.1 %/bit near full).
   Raw `E800AA00E380135D`. Current def: bitStart 7, len 10 — Motorola start-bit 7 wrongly
   transcribed to LE. Correct SOC is likely bytes 0–1 region: 0xE8,0x00 → try Motorola
   bit7|len10 = byte0[7..0]+byte1[7..6] = 0b1110100000 = 928 → 92.8 % ✓ plausible. Fix to
   the LE-equivalent bit positions.
3. **AbsFrame_245_AZE0 torque fields**: raw `7FE8021835007FE1` → VdcTorqueDownRequest1=3720,
   MotorTorqueRequestAbs=232.5 while parked. 0x7FE/0x7FF patterns = center-offset neutral
   (≈0 after subtracting midpoint). Definitions need signed/offset handling (offset −1024
   style) or correct start bits.
4. **BatteryFrame_5BC_AZE0** multiplexed-frame issues: raw `5DC0F0648212BFFF` decoded
   Gids=384 (30 kWh max ~363), SOH=65 % (dash shows higher), RemainChargeTime=4091 (=0xFFB,
   likely "unavailable" sentinel bleeding across mux states). Review against mux flag.
5. HvacFrame_54A AmbientTempAc=61 with actual ambient 22 — doc says "ambient+41" → 63
   expected-ish; verify or document as raw.

## EV-CAN unavailable on stock adapters (architecture fact, not a bug)
Only CAR-CAN frames visible via the OBD port with the Veepeak. Absent all session:
1DB, 1DC, 1DA, 11A, 1CA, 55A, 59E (EV-CAN set). Actions:
- Prune EV-CAN IDs from `SharedBroadcastRotation` comment/expectations; document limitation.
- MotorController (1DA/55A), gear (11A), brake pressure (1CA): need UDS alternatives or
  "unavailable on stock adapter" behavior. MotorController integration tests will fail on
  data-absence until then.
- 1DB↔BMS cross-check impossible on this adapter; SOC cross-check via 55B (once fixed) or 5BC.

## Raw sample bank (from D:\results-20260718-202635\obdtest-20260718-203013.log)
284: 00000000000034BA / 00000000000035BB
285: 00000000000034BA-ish / 00000000000035BC (Unknown6/7 = same counter)
245: 7FE8021835007FE1 / 7FE8021836007FE2
292: 83C7F67FE0000001 / 83C7F67FD0000002
55B: E800AA00E380135D
5BC: 5DC0F0648212BFFF
510: 55C830002E00007D · 50A: 8552A01B80210400 · 50D: 6100800000000080
5A9: 8526C01104100000 · 390: 04000003008000C7 / 04000003008000D8
60D: 0606000000000000 · 174: 000000AA0E000000 · 180: 0000000000002E00 / ...2F00
354: 0000000000080000 / 0000000000100000
No decoder yet (candidates): 358, 3DC, 355, 35D, 351, 1D6, 280, 2DE, 239, 551
Short frames (len<8, decoders skip): 300(1B! 121 frames), 385(7), 625(6), 1CB(7), 1CC(4),
1D5(5), 176(7), 260(4), 215(6), 216(2), 6F6(3) — 625 is BCM headlights: consider supporting
non-8-byte frames in Parse/router (design decision).
