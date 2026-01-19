# Nissan Leaf AZE0 Integration Tests

This directory contains comprehensive integration tests for the Nissan Leaf AZE0 platform implementation using source-generated CAN frames.

## Overview

These tests validate the AZE0 capability implementations against a real Nissan Leaf AZE0 vehicle with an OBD adapter connected via BLE. The tests exercise all passive monitoring capabilities that use source-generated frame parsers.

## Test Files

### LeafAze0PassiveMonitoringIntegrationTests.cs

Tests for passive CAN bus monitoring capabilities:

#### Steering Tests
- `Steering_GetStatus_ReturnsValidData` - Validates steering angle and torque ranges
- `Steering_GetStatus_WhenIdle_HasLowTorque` - Verifies low torque when idle

#### Brake Tests
- `Brake_GetStatus_ReturnsValidData` - Validates brake status flags
- `Brake_GetStatus_WhenNotPressed_ReturnsFalse` - Verifies brake not pressed when idle

#### ABS Tests
- `Abs_GetStatus_ReturnsValidWheelSpeeds` - Validates all 4 wheel speeds
- `Abs_GetStatus_ReturnsVehicleSpeed` - Validates vehicle speed from ABS
- `Abs_GetStatus_ReturnsLeadAcidBatteryVoltage` - Validates 12V battery voltage
- `Abs_GetStatus_WhenParked_WheelSpeedsAreZero` - Verifies zero speeds when parked

#### Body Control Tests
- `BodyControl_GetStatus_ReturnsValidData` - Validates door locks, lights, hazards
- `BodyControl_GetStatus_WhenIdle_HazardsOff` - Verifies hazards off when idle

#### VCM Tests
- `Vcm_GetStatus_ReturnsValidData` - Validates climate control status
- `Vcm_GetStatus_ReturnsAmbientTemperature` - Validates outside temperature
- `Vcm_GetStatus_ReturnsPowerConsumptionMetrics` - Validates power consumption values
- `Vcm_GetStatus_ReturnsEcoIndicators` - Validates eco indicator and eco tree values

#### Cross-Capability Tests
- `MultipleCapabilities_CanBeQueriedSequentially` - Tests querying multiple capabilities
- `Abs_And_Brake_StatusConsistency` - Validates consistency between ABS and Brake data

#### Error Handling Tests
- `Steering_GetStatus_WithCancellation_ThrowsOperationCanceledException` - Tests cancellation
- `Brake_GetStatus_HandlesNoFrameGracefully` - Tests missing frame handling

### LeafAze0MotorAndHvacIntegrationTests.cs

Tests for motor controller and HVAC capabilities:

#### Motor Controller Tests
- `MotorController_GetStatus_ReturnsValidVoltage` - Validates HV input voltage (250-450V)
- `MotorController_GetStatus_ReturnsTorqueAndRpm` - Validates torque and RPM ranges
- `MotorController_GetStatus_WhenParked_TorqueNearZero` - Verifies minimal torque when parked
- `MotorController_GetStatus_WhenParked_RpmZero` - Verifies RPM is zero when parked
- `MotorController_GetStatus_ReturnsTemperatures` - Validates motor and inverter temps
- `MotorController_GetStatus_ReturnsErrorCodes` - Validates error code reporting
- `MotorController_GetStatus_CalculatedPowerMakesRpense` - Validates power calculation

#### HVAC Tests
- `Hvac_GetStatus_ReturnsValidData` - Validates climate control flags
- `Hvac_GetStatus_ReturnsTemperatures` - Validates temperature sensors
- `Hvac_GetStatus_ReturnsFanSpeed` - Validates fan speed and voltage
- `Hvac_GetStatus_WhenClimateOn_PowerValuesPresent` - Validates AC/heater power when on
- `Hvac_GetStatus_WhenClimateOff_LowPower` - Validates low power when off

#### Cross-Capability Tests
- `MotorAndHvac_CanBeQueriedSequentially` - Tests querying both capabilities
- `Motor_And_Hvac_TemperatureConsistency` - Validates temperature correlation

#### Error Handling Tests
- `MotorController_GetStatus_WithCancellation_ThrowsOperationCanceledException` - Tests cancellation
- `Hvac_GetStatus_HandlesPartialDataGracefully` - Tests partial frame handling

## Prerequisites

### Hardware
- Nissan Leaf AZE0 (2018-2023 model years: 40kWh or 62kWh battery)
- ELM327 compatible OBD adapter with Bluetooth LE support
- Windows PC with Bluetooth LE capability

### Software
- .NET 9.0
- TUnit test framework
- BleSessionFixture configured with correct device MAC address

## Configuration

Set the BLE device MAC address via environment variable:
```bash
set LEAF_BLE_ADDRESS=66:1E:87:02:C2:DB
```

Or modify the default in `BleSessionFixture.cs`.

## Running Tests

### Run all AZE0 integration tests:
```bash
dotnet test --filter "Category=AZE0&Category=Integration"
```

### Run specific capability tests:
```bash
dotnet test --filter "Category=Steering"
dotnet test --filter "Category=Brake"
dotnet test --filter "Category=ABS"
dotnet test --filter "Category=MotorController"
dotnet test --filter "Category=HVAC"
```

### Run error handling tests:
```bash
dotnet test --filter "Category=ErrorHandling"
```

## Test Categories

Tests are organized with the following categories:
- `Integration` - Requires physical hardware
- `AZE0` - Specific to AZE0 platform
- `Steering` - Steering capability tests
- `Brake` - Brake capability tests
- `ABS` - ABS capability tests
- `BodyControl` - Body control capability tests
- `VCM` - Vehicle Control Module tests
- `MotorController` - Motor/inverter tests
- `HVAC` - Climate control tests
- `CrossCapability` - Multi-capability integration tests
- `ErrorHandling` - Error and timeout handling tests

## Expected Behavior

All tests should pass when:
- Vehicle is parked with ignition ON
- OBD adapter is connected and powered
- No DTCs (Diagnostic Trouble Codes) are active
- Climate control can be in any state (tests adapt)

## Validated Implementations

These tests validate the following implementations:
- `LeafAze0Steering` - Frame 0x002, 0x300
- `LeafAze0Brake` - Frame 0x1CA
- `LeafAze0Abs` - Frames 0x130, 0x245, 0x284, 0x285, 0x292, 0x354
- `LeafAze0BodyControl` - Frames 0x60D, 0x625
- `LeafAze0VcmCarCan` - Frame 0x510
- `LeafAze0MotorController` - Frames 0x1DA, 0x55A
- `LeafAze0Hvac` - Frames 0x54A, 0x54B, 0x54C, 0x54F

All implementations use source-generated frame parsers from `CanFrameRouter`.

## InternalsVisibleTo Configuration

The `ObdTestApp.csproj` includes:
```xml
<ItemGroup>
    <InternalsVisibleTo Include="OdbTestApp.Tests" />
</ItemGroup>
```

This allows the test project to access internal capability implementation classes while keeping them internal to the main application.

## Notes

- Tests use `BleSessionFixture` with shared lifecycle for efficient test execution
- All tests include Console.WriteLine output for debugging
- Tests validate both happy path and error conditions
- Range checks are generous to account for various vehicle states
- Temperature checks account for ambient conditions
