# Nissan Leaf Data Parsing Improvements

## Summary of Changes

I've enhanced the ObdTestApp to parse and display Nissan Leaf data in a human-readable format, addressing both your requests:

### 1. **Improved BMS Query Data Display**

#### BMS Group 1 (2101) - Now Shows:
- **Voltage**: Battery pack voltage in volts (e.g., "389.7V")
- **Current**: Current in amps with direction indicator (charging/discharging/idle)
- **State of Charge (SOC)**: Battery charge percentage
- **Capacity**: Available battery capacity in amp-hours
- **Health (Hx)**: Battery health percentage

**Example Output:**
```
? BMS Group 1: Voltage: 389.7V, Current: 697.5A (discharging), SOC: 85.2%, Capacity: 56.47Ah, Health: 92.3%
```

#### BMS Group 2 (2102) - Now Shows:
- **Cell Count**: Number of cell pairs detected
- **Min Voltage**: Lowest cell voltage in millivolts
- **Max Voltage**: Highest cell voltage in millivolts  
- **Average Voltage**: Mean cell voltage
- **Delta**: Voltage spread (max - min) indicating balance

**Example Output:**
```
? BMS Group 2: 96 cells, Min: 3823mV, Max: 3888mV, Avg: 3855mV, Delta: 65mV
```

### 2. **CAN Frame Monitoring & Parsing**

The monitoring mode now parses broadcast CAN frames and logs detailed information to the log file. You'll now see decoded data for:

#### CAN ID 1DB - Battery Status
- Current (Amps)
- Voltage (Volts)
- Available capacity (Gids)

**Log Example:**
```
[DBG] [CAN 1DB] Battery: 45.5A, 389.7V, 234 Gids
```

#### CAN ID 55B - High-Resolution SOC
- State of charge with higher precision

**Log Example:**
```
[DBG] [CAN 55B] SOC: 85.2%
```

#### CAN ID 5BC - Capacity & Health
- Available capacity (Gids)
- State of Health (SOH) percentage

**Log Example:**
```
[DBG] [CAN 5BC] Capacity: 234 Gids, SOH: 92.5%
```

#### CAN ID 5C0 - Battery Temperatures
- Temperature readings from all 4 battery packs in Celsius

**Log Example:**
```
[DBG] [CAN 5C0] Battery Temps: 22.5°C, 23.0°C, 22.8°C, 23.2°C
```

#### CAN ID 1DA - Motor/Inverter Data
- Motor RPM
- Motor temperature

**Log Example:**
```
[DBG] [CAN 1DA] Motor: 2450 RPM, 45°C
```

## What You'll See in Future Logs

### During Monitoring Phase:
The console will continue showing frame descriptions, but the **log file** will now contain detailed parsed values for each interesting CAN frame received:

```
22:38:37.007 [DBG] [ElmSession] Monitoring mode active: Nissan Leaf HVBAT Monitor
22:38:37.112 [DBG] [CAN 1DB] Battery: 0.0A, 389.7V, 234 Gids
22:38:37.145 [DBG] [CAN 55B] SOC: 85.2%
22:38:37.178 [DBG] [CAN 5BC] Capacity: 234 Gids, SOH: 92.5%
22:38:37.201 [DBG] [CAN 5C0] Battery Temps: 22.5°C, 23.0°C, 22.8°C, 23.2°C
```

### During Query Phase:
Much more detailed information displayed on console:

```
[cyan]Querying BMS Group 1 (2101)...[/]
? BMS Group 1: Voltage: 389.7V, Current: 697.5A (discharging), SOC: 85.2%, Capacity: 56.47Ah, Health: 92.3%

[cyan]Querying BMS Group 2 (2102)...[/]
? BMS Group 2: 96 cells, Min: 3823mV, Max: 3888mV, Avg: 3855mV, Delta: 65mV
```

## Technical Details

### Parsing References
All parsing logic is based on the **Leaf2018-CAN.md** reference document in the repository, which documents:
- CAN frame formats for broadcast messages
- UDS/ISO-TP multi-frame response formats for queries
- Byte offsets and conversion formulas
- ECU-specific protocols (BMS, VCM, Charger, etc.)

### Key Improvements Made

1. **Added `BmsGroup02Data` record** - Stores parsed cell voltage statistics
2. **Enhanced `BmsGroup01Data` record** - Added voltage field
3. **Added `TryParseBmsGroup02` method** - Extracts and analyzes all 96 cell voltages
4. **Added `ParseAndLogCanFrame` method** - Decodes broadcast CAN messages
5. **Updated monitoring loop** - Calls parser for each received frame
6. **Enhanced console output** - Shows all parsed values in readable format

## Running the Enhanced Version

Next time you run:
```bash
ObdTestApp.exe --device=66:1E:87:02:C2:DB --auto
```

You'll see the improved output both on console and in the detailed log file with all the parsed CAN frame data.

## Benefits

1. **Better diagnostics**: Voltage and cell balance data helps identify battery issues
2. **Complete data visibility**: No more raw hex - everything is decoded
3. **Performance monitoring**: Real-time power flow and temperature tracking
4. **Battery health tracking**: SOC, SOH, capacity, and health metrics all visible
5. **Troubleshooting**: Detailed logs make it easier to spot problems

All changes compile successfully and are ready to use!
