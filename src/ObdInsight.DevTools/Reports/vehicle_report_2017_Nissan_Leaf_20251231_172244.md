# Vehicle/Adapter Support Request

**Generated:** 2025-12-31 22:22:44 UTC
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
| Adapter Detection | ? |  |
| VIN Read | ? | Not available |
| PID Discovery | ? | 0 Mode 01 PIDs |
| PID Responses | ? | 0/46 successful |
| EV/Hybrid Indicators | ? | 0 EV PIDs responded |

## BLE Adapter Information

**Device Name:** `VEEPEAK`
**MAC Address:** `00000000-0000-0000-0000-661e8702c2db`

## OBD Adapter Information

| Property | Value |
|----------|-------|

<details>
<summary>Raw AT Command Responses</summary>

```
ATZ: 
ATI: 
AT@1: 
AT@2: 
ATRV: 
ATDP: 
ATDPN: 
```

</details>

## Vehicle Identification (ECU)

**VIN:** Not available

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

<details>
<summary>All PID Probe Data (Raw)</summary>

```
[FAIL] 015B (Hybrid battery pack remaining life): Transport not connected [0ms]
[FAIL] 015E (Engine fuel rate (0 = EV)): Transport not connected [0ms]
[FAIL] 2101 (Manufacturer-specific battery data (Nissan)): Transport not connected [0ms]
[FAIL] 220101 (Manufacturer-specific battery data (GM/Kia)): Transport not connected [0ms]
[FAIL] 7E421C0 (Tesla-specific probe): Transport not connected [0ms]
```

</details>

*5 PIDs did not respond or returned errors*

## Collection Notes

- Connected to VEEPEAK
- OBD adapter identified as: 
- Found 0 Mode 01 PIDs and 0 Mode 09 PIDs supported
- Standard PID probe complete: 0/46 successful
- Extended PID probe complete: 0/5 successful

---

*This report was generated by ObdInsight. Please attach this file to your GitHub issue.*
