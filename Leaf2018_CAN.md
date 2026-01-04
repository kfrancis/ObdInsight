# Nissan Leaf 2018 – CAN / UDS Signal Reference

**Source:** Nissan Leaf 2018 CAN Documentation (March 12, 2021)

---

## ECU IDs

| ECU | Query ID | Response ID |
|-----|----------|-------------|
| Vehicle Control Module (VCM) | `0x797` | `0x79A` |
| Body Control Module (BCM) | `0x743` | `0x763` |
| Anti-lock Braking System (ABS) | `0x740` | `0x760` |
| Li-ion Battery Controller (LBC) | `0x79B` | `0x7BB` |
| Traction Motor Inverter (INV/MC) | `0x784` | `0x78C` |
| Meter | `0x745` | `0x765` |
| HVAC | `0x744` | `0x764` |

---

## Vehicle Control Module (VCM)

### Power Software
- **Type:** Boolean  
- **Query:** `0x797 03 22 13 04`  
- **Answer:** `0x79A 05 62 13 04 80 FE 00 00`

```
PowerSW = (data[4] & 0x80) == 0x80
```

### Gear Position
- **Values:** `1=Park, 2=Reverse, 3=Neutral, 4=Drive, 7=Eco`

```
Gear = data[4]
```

### 12V Battery Voltage (V)
```
Voltage = data[4] * 0.08
```

### 12V Battery Current (A)
```
raw = (data[4] << 8) | data[5]
if raw & 0x8000:
    raw |= -65536
Current = raw / 256
```

### Ambient Temperature (°C)
```
TempF = (data[4] * 0.9) - 40.9
TempC = (TempF - 32) * 5 / 9
```

---

## Body Control Module (BCM)

### Odometer (km)
```
Odometer = (data[4] << 16) | (data[5] << 8) | data[6]
```

### Tire Pressure (kPa)
```
TP = data[4] * 0.068947576 * 100 / 4
```

---

## Li-Ion Battery Controller (LBC)

### State of Charge (SOC %)
```
SOC = ((data24[7] << 16) | (data25[1] << 8) | data25[2]) / 10000
```

### State of Health (SOH %)
```
SOH = ((data[6] << 8) | data[7]) / 100
```

---

## ABS

```
SteeringAngle = signed16(data[4], data[5]) / 10
BrakePressure = (data[4] << 8) | data[5]
AccelPedal    = data[4]
```

---

## HVAC

### Fan Speed
```
FanSpeed = data[1] - 131
```
