# Vehicle/Adapter Support Request

**Generated:** 2026-01-01 00:01:53 UTC
**Tool Version:** 1.0.0.0

## Vehicle Information (User Provided)

| Property | Value |
|----------|-------|
| Year | 2017 |
| Make | Nissan |
| Model | Leaf |
| Engine/Powertrain | Electric (BEV) |
| Transmission | Single-Speed (EV) |

## Summary

| Check | Status | Details |
|-------|--------|---------|
| BLE Connection | ? | VEEPEAK |
| Adapter Detection | ? | ELM327 v2.2 |
| Working Protocols | ? | No protocols responded |
| VIN Read | ? | Not available |
| PID Discovery | ? | 0 Mode 01 PIDs |
| Standard PID Responses | ? | 0/46 successful |
| Extended PID Responses | ? | 0/10 successful |
| EV/Hybrid Data | ? | No EV data (may need proprietary protocol) |

## Diagnostic Analysis

### Findings

- **No standard OBD-II protocols responded** - Vehicle may use proprietary protocol
- **No standard PIDs responded** - Expected for pure EVs which often don't implement OBD-II Mode 01
- **VIN not available** - Vehicle doesn't expose VIN via standard OBD-II Mode 09

### Recommendations

- This is an **Electric Vehicle** which typically requires manufacturer-specific protocols
- **Nissan Leaf/EV:** Try using CAN header 0x79B for battery data (ATSH79B then 2101-2104)
- The Leaf uses ISO 15765-4 CAN but with non-standard addressing

### For GitHub Issue

When submitting this report, please include:
1. Any additional observations about how your vehicle behaves
2. Whether any aftermarket apps (LeafSpy, Torque, etc.) work with your vehicle
3. What data you're most interested in (battery %, range, charging status, etc.)

## BLE Adapter Information

**Device Name:** `VEEPEAK`
**MAC Address:** `00000000-0000-0000-0000-661e8702c2db`

## OBD Adapter Information

| Property | Value |
|----------|-------|
| Version (ATI) | `ELM327 v2.2` |
| Description (AT@1) | `OBDII to RS232 Interpreter` |
| Voltage (ATRV) | `12.0V` |
| Protocol (ATDP) | `ISO 15765-4 (CAN 11/500)` |
| Protocol # (ATDPN) | `6` |

<details>
<summary>Raw AT Command Responses</summary>

```
ATZ: ELM327 v2.2
ATI: ELM327 v2.2
AT@1: OBDII to RS232 Interpreter
AT@2: ????????????
ATRV: 12.0V
ATDP: ISO 15765-4 (CAN 11/500)
ATDPN: 6
```

</details>

## Vehicle Identification (ECU)

**VIN:** Not available via standard OBD-II

<details>
<summary>Raw VIN Response</summary>

```
NO DATA
```

</details>

## Supported PIDs

**Mode 01 (Live Data):** 0 PIDs

**Mode 09 (Vehicle Info):** 0 PIDs

<details>
<summary>Raw Supported PIDs Responses</summary>

```
0100: NO DATA
0900: NO DATA
```

</details>

## Standard PID Responses

*No PIDs in this category responded with data.*

<details>
<summary>All PID Probe Data (Raw)</summary>

```
[FAIL] 0100 (Supported PIDs [01-20]): NO DATA [445ms]
[FAIL] 0101 (Monitor status since DTCs cleared): NO DATA [510ms]
[FAIL] 0103 (Fuel system status): NO DATA [434ms]
[FAIL] 0104 (Calculated engine load): NO DATA [419ms]
[FAIL] 0105 (Engine coolant temperature): NO DATA [466ms]
[FAIL] 0106 (Short term fuel trim—Bank 1): NO DATA [434ms]
[FAIL] 0107 (Long term fuel trim—Bank 1): NO DATA [432ms]
[FAIL] 010A (Fuel pressure): NO DATA [435ms]
[FAIL] 010B (Intake manifold absolute pressure): NO DATA [419ms]
[FAIL] 010C (Engine speed (RPM)): NO DATA [420ms]
[FAIL] 010D (Vehicle speed): NO DATA [418ms]
[FAIL] 010E (Timing advance): NO DATA [419ms]
[FAIL] 010F (Intake air temperature): NO DATA [418ms]
[FAIL] 0110 (Mass air flow sensor): NO DATA [466ms]
[FAIL] 0111 (Throttle position): NO DATA [451ms]
[FAIL] 0113 (Oxygen sensors present (2 banks)): NO DATA [434ms]
[FAIL] 011C (OBD standards this vehicle conforms to): NO DATA [419ms]
[FAIL] 011F (Run time since engine start): NO DATA [420ms]
[FAIL] 0120 (Supported PIDs [21-40]): NO DATA [418ms]
[FAIL] 0121 (Distance traveled with MIL on): NO DATA [418ms]
[FAIL] 012F (Fuel tank level input): NO DATA [435ms]
[FAIL] 0131 (Distance traveled since codes cleared): NO DATA [419ms]
[FAIL] 0133 (Absolute Barometric Pressure): NO DATA [421ms]
[FAIL] 0140 (Supported PIDs [41-60]): Command failed: The operation was canceled. [5738ms]
[FAIL] 0142 (Control module voltage): Transport not connected [0ms]
[FAIL] 0145 (Relative throttle position): Transport not connected [0ms]
[FAIL] 0146 (Ambient air temperature): Transport not connected [0ms]
[FAIL] 0149 (Accelerator pedal position D): Transport not connected [0ms]
[FAIL] 014A (Accelerator pedal position E): Transport not connected [0ms]
[FAIL] 014C (Commanded throttle actuator): Transport not connected [0ms]
[FAIL] 0151 (Fuel Type): Transport not connected [0ms]
[FAIL] 015B (Hybrid battery pack remaining life): Transport not connected [0ms]
[FAIL] 015C (Engine oil temperature): Transport not connected [0ms]
[FAIL] 015E (Engine fuel rate): Transport not connected [0ms]
[FAIL] 0160 (Supported PIDs [61-80]): Transport not connected [0ms]
[FAIL] 0161 (Driver's demand engine - percent torque): Transport not connected [0ms]
[FAIL] 0162 (Actual engine - percent torque): Transport not connected [0ms]
[FAIL] 0163 (Engine reference torque): Transport not connected [0ms]
[FAIL] 0166 (Mass air flow sensor B): Transport not connected [0ms]
[FAIL] 0167 (Engine coolant temperature sensor 2): Transport not connected [0ms]
[FAIL] 0900 (Supported PIDs [01-20]): Transport not connected [0ms]
[FAIL] 0902 (Vehicle Identification Number (VIN)): Transport not connected [0ms]
[FAIL] 0904 (Calibration ID): Transport not connected [0ms]
[FAIL] 0906 (Calibration Verification Numbers): Transport not connected [0ms]
[FAIL] 090A (ECU name): Transport not connected [0ms]
[FAIL] 090B (In-use performance tracking): Transport not connected [0ms]
```

</details>

*46 PIDs did not respond or returned errors*

## Extended/EV PID Responses

*No PIDs in this category responded with data.*

<details>
<summary>All PID Probe Data (Raw)</summary>

```
[FAIL] 015B (Hybrid battery pack remaining life): Transport not connected [0ms]
[FAIL] 015E (Engine fuel rate (0 = EV)): Transport not connected [0ms]
[FAIL] 2101 (Manufacturer-specific battery data (Nissan/Hyundai)): Transport not connected [0ms]
[FAIL] 2102 (Manufacturer-specific battery data 2): Transport not connected [0ms]
[FAIL] 2103 (Manufacturer-specific battery data 3): Transport not connected [0ms]
[FAIL] 2104 (Manufacturer-specific battery data 4): Transport not connected [0ms]
[FAIL] 2105 (Manufacturer-specific battery data 5): Transport not connected [0ms]
[FAIL] 220101 (Manufacturer-specific battery data (GM/Kia)): Transport not connected [0ms]
[FAIL] 220102 (Manufacturer-specific battery data (GM/Kia) 2): Transport not connected [0ms]
[FAIL] 220105 (Manufacturer-specific battery data (GM/Kia) 3): Transport not connected [0ms]
```

</details>

*10 PIDs did not respond or returned errors*

## Collection Notes

- Connected to VEEPEAK
- OBD adapter identified as: ELM327 v2.2
- Found 0 Mode 01 PIDs and 0 Mode 09 PIDs supported
- Standard PID probe complete: 0/46 successful
- Extended PID probe complete: 0/10 successful

---

*This report was generated by ObdInsight. Please attach this file to your GitHub issue.*
