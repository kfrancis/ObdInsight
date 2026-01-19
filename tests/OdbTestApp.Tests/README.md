# ObdTestApp TUnit Tests

This test project contains unit and integration tests for the ObdTestApp application, specifically focused on Nissan Leaf vehicle testing and BMS (Battery Management System) data parsing.

## Test Organization

### NissanLeaf/
Tests specific to the Nissan Leaf AZE0 (2016-2017 30kWh) vehicle:

#### LeafBmsParsingHelpers.cs
- Shared parsing utilities extracted from `LeafAze0Bms`
- Golden sample data captured from real Leaf: `66:1E:87:02:C2:DB`
- ISO-TP frame parsing and reassembly logic
- BMS Group 01 parsing for voltage, current, SOH (Hx), and capacity

#### LeafIsoTpParsingTests.cs
**Unit tests** - No BLE required
- Tests ISO-TP frame parsing from ELM327 responses
- Validates frame count, frame types, and payload reassembly
- Uses golden sample data captured from real vehicle

#### LeafBmsGroup01Tests.cs
**Unit tests** - No BLE required:
- `LeafBmsGroup01ParsingTests`: Validates parsing of BMS Group 01 data
  - Voltage extraction (361.78V expected)
  - Current extraction (0.229A expected)
  - Hx/SOH extraction (35.44% expected)
  - Capacity extraction (52.58 Ah expected)
  - SOC validation (null for 24/30kWh Leaf)

**Integration tests** - Requires BLE:
- `LeafBmsGroup01IntegrationTests`: Tests against real vehicle
  - All tests marked with `[Skip]` by default
  - Remove skip attribute when testing with physical adapter
  - Validates data is in reasonable ranges

#### LeafBmsGroup02Tests.cs
**Integration tests** - Requires BLE:
- `LeafBmsGroup02IntegrationTests`: Tests cell voltage queries
  - Validates ISO-TP frames and headers
  - Parses 96 cell pair voltages
  - Checks voltage ranges (2500-4500mV)
  - Validates cell balance (delta < 500mV)

#### LeafChargerTests.cs
**Unit tests** - No BLE required:
- `LeafChargerVinParsingTests`: Validates VIN parsing logic
  - Tests ISO-TP frame parsing from VIN query
  - Validates 17-character VIN format
  - Tests handling of invalid/null responses
  - Uses golden sample: `1N4BZ0CP3HC310408`

**Integration tests** - Requires BLE:
- `LeafChargerIntegrationTests`: Tests VIN query against real vehicle
  - Validates Mode 21 PID 81 response format
  - Checks VIN is 17 characters
  - Verifies manufacturer code (1N4 for USA, JN1 for Japan)
  - Validates VIN character set (no I, O, Q)

### Fixtures/

#### BleSessionFixture.cs
- TUnit fixture for managing real BLE connections
- Connects to OBD adapter via Bluetooth Low Energy
- Initializes ELM327 session with protocol detection
- Shared across multiple test classes using `SharedType.Keyed`
- Configure device address via environment variable:
  ```bash
  $env:LEAF_BLE_ADDRESS = "66:1E:87:02:C2:DB"
  ```

## Running Tests

### Run all tests
```bash
dotnet test
```

### Run only unit tests (no BLE required)
```bash
dotnet test --filter "FullyQualifiedName~ParsingTests"
```

### Run integration tests (requires BLE adapter)
1. Set environment variable for your device (optional, defaults to 66:1E:87:02:C2:DB):
   ```bash
   $env:LEAF_BLE_ADDRESS = "YOUR:MAC:ADDRESS"
   ```
2. Connect your Nissan Leaf OBD adapter
3. Run tests:
   ```bash
   dotnet test --filter "FullyQualifiedName~IntegrationTests"
   ```

## Test Data

### Golden Samples
The golden sample data in `LeafBmsParsingHelpers.GoldenGroup01Lines` was captured from:
- **Vehicle**: Nissan Leaf AZE0-2 (2016-2017 30kWh)
- **Device**: 66:1E:87:02:C2:DB
- **Date**: 2026-01-18
- **Command**: Mode 21, PID 01 (BMS Group 01)
- **Format**: ISO-TP multi-frame response (43 bytes total)

Expected values:
- Voltage: 361.78V
- Current: 0.229A
- Hx (SOH): 35.44%
- Capacity: 52.58 Ah
- SOC: null (not available in Group 01 for 24/30kWh Leaf)

## Architecture Notes

### Why Separate Unit and Integration Tests?
- **Unit tests** validate parsing logic using golden samples
  - Fast, reliable, no hardware dependencies
  - Can run in CI/CD pipelines
  - Cover edge cases and data formats

- **Integration tests** validate real BLE communication
  - Require physical hardware
  - Test actual ELM327 adapter behavior
  - Verify data ranges and vehicle responses
  - Marked with `[Skip]` by default

### TUnit Fixtures
- `BleSessionFixture` implements `IAsyncInitializer` and `IAsyncDisposable`
- Fixture is shared using `[ClassDataSource<BleSessionFixture>(Shared = SharedType.Keyed)]`
- This means all tests using the fixture share one BLE connection
- Connection is established once and cleaned up after all tests complete

## References

- OVMS Nissan Leaf implementation: `vehicle_nissanleaf.cpp`
- TUnit documentation: https://tunit.dev/docs/intro
- ISO-TP specification: ISO 15765-2
