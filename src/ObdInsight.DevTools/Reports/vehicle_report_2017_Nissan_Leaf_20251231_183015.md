# Vehicle/Adapter Support Request

**Generated:** 2025-12-31 23:30:15 UTC
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
| BLE Connection | ? | Veepeak BLE+ |
| Adapter Detection | ? | Unknown |
| Working Protocols | ? | No protocols responded |
| VIN Read | ? | Not available |
| PID Discovery | ? | 0 Mode 01 PIDs |
| Standard PID Responses | ? | 0/46 successful |
| Extended PID Responses | ? | 0/10 successful |
| EV CAN Responses | ? | 0 CAN address(es) responded |
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

**Device Name:** `Veepeak BLE+`
**MAC Address:** `66:1e:87:02:c2:db`

## OBD Adapter Information

| Property | Value |
|----------|-------|

<details>
<summary>Raw AT Command Responses</summary>

```
ATZ: 
```

</details>

## Protocol Probe Results

**No standard OBD-II protocols responded with data.**

## Vehicle Identification (ECU)

**VIN:** Not available via standard OBD-II

## Supported PIDs

**Mode 01 (Live Data):** 0 PIDs

**Mode 09 (Vehicle Info):** 0 PIDs

<details>
<summary>Raw Supported PIDs Responses</summary>

```
0100: 
0900: 
```

</details>

## Standard PID Responses

*No PIDs in this category responded with data.*

<details>
<summary>All PID Probe Data (Raw)</summary>

```
[FAIL] 0100 (Supported PIDs [01-20]): Transport not connected [0ms]
[FAIL] 0101 (Monitor status since DTCs cleared): Transport not connected [0ms]
[FAIL] 0103 (Fuel system status): Transport not connected [0ms]
[FAIL] 0104 (Calculated engine load): Transport not connected [0ms]
[FAIL] 0105 (Engine coolant temperature): Transport not connected [0ms]
[FAIL] 0106 (Short term fuel trim—Bank 1): Transport not connected [0ms]
[FAIL] 0107 (Long term fuel trim—Bank 1): Transport not connected [0ms]
[FAIL] 010A (Fuel pressure): Transport not connected [0ms]
[FAIL] 010B (Intake manifold absolute pressure): Transport not connected [0ms]
[FAIL] 010C (Engine speed (RPM)): Transport not connected [0ms]
[FAIL] 010D (Vehicle speed): Transport not connected [0ms]
[FAIL] 010E (Timing advance): Transport not connected [0ms]
[FAIL] 010F (Intake air temperature): Transport not connected [0ms]
[FAIL] 0110 (Mass air flow sensor): Transport not connected [0ms]
[FAIL] 0111 (Throttle position): Transport not connected [0ms]
[FAIL] 0113 (Oxygen sensors present (2 banks)): Transport not connected [0ms]
[FAIL] 011C (OBD standards this vehicle conforms to): Transport not connected [0ms]
[FAIL] 011F (Run time since engine start): Transport not connected [0ms]
[FAIL] 0120 (Supported PIDs [21-40]): Transport not connected [0ms]
[FAIL] 0121 (Distance traveled with MIL on): Transport not connected [0ms]
[FAIL] 012F (Fuel tank level input): Transport not connected [0ms]
[FAIL] 0131 (Distance traveled since codes cleared): Transport not connected [0ms]
[FAIL] 0133 (Absolute Barometric Pressure): Transport not connected [0ms]
[FAIL] 0140 (Supported PIDs [41-60]): Transport not connected [0ms]
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

## EV CAN Address Probes

*No EV-specific CAN addresses responded. Vehicle may require different addressing.*

<details>
<summary>All CAN Probe Data</summary>

```
[NO DATA] ATSP6:  [0ms]
[NO DATA] ATH1:  [0ms]
[NO DATA] ATCAF1:  [0ms]
[NO DATA] ATCFC1:  [0ms]
[NO DATA] 1001:  [0ms]
[NO DATA] 1003:  [0ms]
--- Header: 79B ---
[NO DATA] ATFCSH79B:  [0ms]
[NO DATA] ATFCSD300000:  [0ms]
[NO DATA] ATFCSM1:  [0ms]
[NO DATA] 2101:  [0ms]
[NO DATA] 2102:  [0ms]
[NO DATA] 2104:  [0ms]
[NO DATA] 2106:  [0ms]
--- Header: 797 ---
[NO DATA] ATFCSH797:  [0ms]
[NO DATA] 2181:  [0ms]
[NO DATA] 221203:  [0ms]
[NO DATA] 221205:  [0ms]
--- Header: 7E4 ---
[NO DATA] ATFCSH7E4:  [0ms]
[NO DATA] 2101:  [0ms]
--- Header: 7DF ---
[NO DATA] ATH0:  [0ms]
[NO DATA] ATCAF0:  [0ms]
```

</details>

## Errors Encountered

- **Adapter Info:** Transport disconnected during ATZ
- **Protocol Probe:** Transport disconnected during ATSP0

## Collection Notes

- Connected to Veepeak BLE+ (66:1e:87:02:c2:db)
- OBD adapter identified as: Unknown
- Protocol probe complete: 0/1 protocols responded
- Found 0 Mode 01 PIDs and 0 Mode 09 PIDs supported
- Standard PID probe complete: 0/46 successful
- Extended PID probe complete: 0/10 successful
- EV CAN probe complete: 0 data responses

---

*This report was generated by ObdInsight. Please attach this file to your GitHub issue.*
