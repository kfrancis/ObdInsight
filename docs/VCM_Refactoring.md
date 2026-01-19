# VCM Refactoring Summary

## Overview
Refactored the VCM (Vehicle Control Module) implementation to support both EV-CAN and CAR-CAN buses using a composite/facade pattern.

## Architecture

### Before
- Single `LeafAze0Vcm` class directly accessing one CAN context
- Only supported EV-CAN frames (0x11A for gear position)

### After
```
LeafAze0Vcm (public facade)
  ├─ LeafAze0VcmEvCan (internal, EV-CAN bus)
  │   └─ Handles: 0x11A, 0x1D4, 0x1F2, 0x284, 0x5A9, 0x50A-0x50C, 0x5B9, 0x603
  │
  └─ LeafAze0VcmCarCan (internal, CAR-CAN bus)
      └─ Handles: 0x174, 0x176, 0x180, 0x260, 0x421, 0x50A, 0x50D, 0x510
```

## Changes Made

### 1. Context Updates (`LeafAze0Contexts.cs`)
- Added `VcmEvCanBroadcast` - EV-CAN specific VCM frames
- Added `VcmCarCanBroadcast` - CAR-CAN specific VCM frames  
- `VcmBroadcast` is now an alias for `VcmEvCanBroadcast` (backward compatibility)

### 2. Frame Definitions (`VcmFrames.cs`)
- Added `VcmFrame_510_AZE0` - Power consumption and climate data from CAR-CAN
  - Signals: Climate control status/power, ambient temperature, power consumption (motor/AC/aux), eco indicators

### 3. Interface Updates (`VehicleCapabilities.cs`)
- Enhanced `IVcm` interface:
  - `GetGearPositionAsync()` - existing (EV-CAN 0x11A)
  - `GetStatusAsync()` - NEW (CAR-CAN 0x510)
- Added `VcmStatus` record with comprehensive VCM data

### 4. Implementation Classes

#### `LeafAze0VcmEvCan.cs` (internal)
- Handles EV-CAN operations
- Methods: `GetGearPositionAsync()`
- Accesses frame 0x11A for gear position

#### `LeafAze0VcmCarCan.cs` (internal)  
- Handles CAR-CAN operations
- Methods: `GetStatusAsync()`
- Accesses frame 0x510 for comprehensive status

#### `LeafAze0Vcm.cs` (public)
- Composite class that delegates to bus-specific helpers
- Constructor takes both `evCanContext` and `carCanContext`
- Routes method calls to appropriate helper

### 5. Registration (`LeafAze0CommandSet.cs`)
- Updated to pass both contexts:
  ```csharp
  Add<IVcm>(new LeafAze0Vcm(
      session, 
      LeafAze0Contexts.VcmEvCanBroadcast,  // EV-CAN
      LeafAze0Contexts.VcmCarCanBroadcast   // CAR-CAN
  ));
  ```

## Benefits

1. **Separation of Concerns** - Each bus has its own implementation class
2. **No Context Switching Overhead** - Each method knows exactly which bus it needs
3. **Clean API** - Users don't need to know about bus topology
4. **Testable** - Each bus-specific class can be tested independently
5. **Extensible** - Easy to add more methods for additional frames on either bus
6. **Type-Safe** - Uses generated frame parsers for compile-time safety

## Frame 0x510 Details

### CAR-CAN VCM Frame 0x510 (1296 decimal)
**Purpose**: VCM relay from A/C Auto Amp to eyebrow display and A/V unit

**Signals**:
- `ClimateControlActive` (bit 31) - boolean
- `ClimateControlPowerConsumption` (bits 25-30) - 0.25 kW/bit
- `OutsideAmbientTemperature` (byte 7) - 0.5°C/bit, offset -40
- `IntegratedPowerConsumptionMotor` (byte 0) - 0-255 raw
- `IntegratedPowerConsumptionAc` (bits 15-19) - 0-31 raw
- `IntegratedPowerConsumptionAux` (bits 23-26) - 0-15 raw
- `PowerConsumptionAux` (bits 39-43) - 0-31 raw
- `EcoIndicator` (bits 19-22) - 0-15 scale
- `EcoTree` (bits 47-51) - 0-31 growth level
- `ChargeMode` (bits 10-12) - 0-3 mode

## Usage Example

```csharp
var vcm = vehicle.Get<IVcm>();

// Get gear position (EV-CAN 0x11A)
var gear = await vcm.GetGearPositionAsync();
Console.WriteLine($"Current gear: {gear}");

// Get comprehensive status (CAR-CAN 0x510)
var status = await vcm.GetStatusAsync();
Console.WriteLine($"Outside temp: {status.OutsideAmbientTempC}°C");
Console.WriteLine($"Climate active: {status.ClimateControlActive}");
Console.WriteLine($"Climate power: {status.ClimateControlPowerKw} kW");
Console.WriteLine($"Eco tree level: {status.EcoTree}");
```

## Future Work

The architecture now supports adding more methods for additional VCM frames:

**EV-CAN frames to consider**:
- 0x1D4 - Motor torque control
- 0x1F2 - Charging control
- 0x284 - Wheel speeds
- 0x5A9 - Range estimate and warnings

**CAR-CAN frames to consider**:
- 0x174 - Shifter position relay
- 0x176 - Motor RPM relay
- 0x180 - Motor current and throttle
- 0x260 - Motor power consumption
- 0x421 - Dashboard shifter position
- 0x50D - Dashboard indicator lights

Each new method can be added to the appropriate helper class (`LeafAze0VcmEvCan` or `LeafAze0VcmCarCan`) and exposed through the `IVcm` interface.
