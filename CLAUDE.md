# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

ObdInsight is a cross-platform OBD-II diagnostic application for Android and iOS built with .NET MAUI. It features a pluggable architecture separating transport (BLE/WiFi), adapter (ELM327/STN), and vehicle-specific logic.

**Key Goals:**
- Clean mobile-first UX superior to existing OBD apps
- Pluggable driver system for easy extensibility
- Testable architecture with replay/mock capabilities
- Cross-platform single codebase

## Build and Test Commands

### Building
```bash
# Restore dependencies
dotnet restore

# Build entire solution
dotnet build

# Build for Android
dotnet build -t:Run -f net9.0-android

# Build for iOS (macOS only)
dotnet build -t:Run -f net9.0-ios

# Clean and rebuild (Windows PowerShell)
./clean-rebuild.ps1

# Build DevTools (Windows only)
cd src/ObdInsight.DevTools
dotnet run
```

### Testing
```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/ObdInsights.Tests/

# Run compliance tests for adapters
dotnet test tests/ObdInsight.AdapterComplianceTests/

# Run with coverage
dotnet test /p:CollectCoverage=true
```

### DevTools Commands
DevTools (Windows-only) provides:
- BLE device scanning and service discovery
- OBD adapter diagnostics (protocol detection, voltage, VIN)
- Vehicle support report generation (for adding new vehicle profiles)
- Recording/replay transport session capture
- Binary protocol testing (Veepeak)

## Architecture

### Layered Architecture (Bottom-Up)

**1. Transport Layer** (`ObdInsight.Core.Transports`)
- **Purpose**: Low-level byte transfer (BLE, WiFi, Serial)
- **Key Interface**: `IObdTransport` - connection, read/write primitives
- **Important**: Transports are protocol-agnostic - they don't understand OBD commands
- **BLE-Specific**: `IBleTransport`, `BleTransportBase` - handles BLE connection, chunking, buffering
- **Device Profiles**: `BleDeviceProfile` - GATT service/characteristic UUIDs for different adapters (Veepeak, OBDLink, Nordic UART)

**2. Adapter Layer** (`ObdInsight.Core.Adapters`)
- **Purpose**: OBD protocol handling (ELM327, STN1110)
- **Key Interface**: `IObdAdapter` - initialization, command/response framing, error handling
- **Important**: Adapters are vehicle-agnostic - they only send commands and parse responses
- **Types**: `ObdCommand` (request), `ObdResponse` (result)
- **Example**: `Elm327Adapter` handles AT commands, response parsing, multi-frame messages

**3. Vehicle Profile Layer** (`ObdInsight.Core.Vehicles`)
- **Purpose**: Vehicle-specific PID interpretation and data decoding
- **Key Interface**: `IVehicleProfile` - custom PIDs, decoders, VIN matching, initialization commands
- **Important**: Profiles define *what* to request and *how* to decode it for specific vehicles
- **Enums**: `VehicleDataPoint` (what data), `VehicleDataCategory` (grouping), `VehicleProtocol` (communication style)
- **Built-in Profiles**: `ObdInsight.Drivers.Vehicles/` - NissanLeafProfile, ChevroletBoltProfile

**4. Application Layer** (`ObdInsight` MAUI app)
- **ViewModels**: MainViewModel, DevicesViewModel, VehicleViewModel
- **Services**: Dependency injection in `MauiProgram.cs`
- **Navigation**: Shell-based routing with `INavigationService`

### Key Design Patterns

**Separation of Concerns:**
```
Transport: "Write bytes, read bytes"
    ↓
Adapter: "Send '010C\r', get '41 0C 1A F8'"
    ↓
Vehicle Profile: "Decode [0x1A, 0xF8] as RPM = 1726"
    ↓
ViewModel: "Display '1726 RPM'"
```

**Testing Architecture:**
- `ReplayTransport` - deterministic testing by replaying recorded sessions
- `RecordingTransportDecorator` - wraps any transport to capture all I/O
- `MockTransport` - simple mock for unit tests
- `TransportTrace` - serializable session format (JSON)

**Binary vs ASCII Protocols:**
- Standard: ASCII ELM327 commands (e.g., "010C\r")
- Binary: Veepeak proprietary framing (`IBinaryBleTransport`, `BinaryObdCommands`)
- Binary is faster but adapter-specific

## Common Development Tasks

### Adding a New Vehicle Profile

1. Create class in `src/ObdInsight.Drivers/Vehicles/` implementing `IVehicleProfile`
2. Define:
   - VIN prefixes for auto-detection
   - Custom PIDs with decoders
   - Supported data categories
3. Register in `VehicleProfileRegistry.cs`
4. Generate diagnostic report using DevTools for real vehicle data

**Example Pattern:**
```csharp
public class MyVehicleProfile : IVehicleProfile
{
    public string Name => "2023 MyBrand Model";
    public IReadOnlyList<string> VinPrefixes => ["ABC", "XYZ"];

    public IReadOnlyList<VehiclePid> CustomPids =>
    [
        new VehiclePid("Battery SOC", "220100", VehicleDataPoint.BatteryStateOfCharge, "%")
        {
            Decoder = bytes => bytes[0] / 2.55,
            ExpectedHeader = "7BB"
        }
    ];
}
```

### Adding a New BLE Adapter Profile

1. Discover GATT services using DevTools ("Discover device services")
2. Create `BleDeviceProfile` in `BleTypes.cs` with service/characteristic UUIDs
3. Add to `BleDeviceProfile.AllProfiles` for auto-detection
4. Test with DevTools before integrating into app

**Critical UUIDs:**
- Service UUID - main GATT service for OBD
- Write Characteristic - where commands are sent
- Notify Characteristic - where responses arrive
- WriteWithResponse - true for reliability, false for speed

### Adding a New Adapter Protocol

1. Implement `IObdAdapter` in `src/ObdInsight.Core/Adapters/YourAdapter/`
2. Handle initialization sequence (AT commands, etc.)
3. Implement command framing and response parsing
4. Register in `AdapterRegistry.cs`
5. Create compliance tests extending base classes in `ObdInsight.AdapterComplianceTests/`

### Recording and Replaying Sessions

**Recording:**
```csharp
var recorder = new RecordingTransportDecorator(realTransport);
// ... perform operations ...
var trace = recorder.GetTrace();
await File.WriteAllTextAsync("session.json", JsonSerializer.Serialize(trace));
```

**Replaying:**
```csharp
var trace = JsonSerializer.Deserialize<TransportTrace>(json);
using var transport = new ReplayTransport(trace, new ReplayOptions { MatchingMode = ReplayMatchingMode.Exact });
```

### Generating Vehicle Diagnostic Reports

Use DevTools to generate markdown reports for requesting vehicle support:

1. Run `dotnet run` in `src/ObdInsight.DevTools`
2. Select "Generate Vehicle Support Report"
3. Enter vehicle details and OBD adapter MAC address
4. Report includes: VIN, supported PIDs, protocol, EV-specific data
5. Submit as GitHub issue for new vehicle profile

## Code Standards

### Naming Conventions
- **PascalCase**: Classes, methods, properties, public fields
- **camelCase**: Local variables, parameters
- **_camelCase**: Private fields (underscore prefix)
- **SCREAMING_SNAKE_CASE**: Constants

### File Organization
- **Core**: Interfaces and base implementations (transport, adapter, vehicle abstractions)
- **Drivers**: Concrete implementations (specific vehicles, adapter registry)
- **Platform-Specific**: Separate implementations (Windows*, Plugin* for mobile)
- **DevTools**: Windows-only diagnostic/development utilities

### Async Patterns
- All I/O operations are async
- Use `CancellationToken` parameters
- Prefer `Task<bool>` for success/failure over exceptions for expected failures
- Use `ObdResponse.Ok()` / `ObdResponse.Fail()` pattern

### Dependency Injection
- Services registered in `MauiProgram.cs`
- ViewModels are injected into Pages
- Use interfaces for testability (`IObdAdapter`, `IVehicleProfile`, `IObdTransport`)

## Important Implementation Notes

### BLE Transport Considerations
- **MTU Limits**: Max 20 bytes per write (configurable in `BleDeviceProfile`)
- **Write Chunking**: `BleTransportBase` handles automatic chunking
- **Buffering**: All received data goes through thread-safe receive buffer
- **Notifications**: Must subscribe to CCCD for notify characteristic
- **Binary vs ASCII**: Different profiles for same physical adapter

### ELM327 Protocol Specifics
- All commands terminated with `\r` (carriage return)
- Responses end with `>` prompt
- Multi-frame responses: `0:` `1:` `2:` prefixes
- Error responses: `NO DATA`, `UNABLE TO CONNECT`, `?`
- Initialization: `ATZ` (reset), `ATE0` (echo off), `ATL0` (line feeds off), `ATSP0` (auto protocol)

### Vehicle Profile Best Practices
- Use VIN prefixes (WMI codes) for auto-detection
- Define `ExpectedHeader` for CAN responses to validate correct ECU
- Multi-frame responses: set `ExpectedFrames` in `VehiclePid`
- Custom timeouts: Some PIDs (e.g., VIN) need longer timeouts
- EV-specific: Use `VehicleProtocol.NissanCarCan`, `VehicleProtocol.GmEv`, etc.

### Testing Best Practices
- Use `ReplayTransport` for deterministic adapter tests
- Capture real sessions with `RecordingTransportDecorator`
- Extend compliance test bases for new adapters
- Test vehicle profiles with real diagnostic reports
- Mock transport for unit testing decoder logic

## Platform-Specific Implementations

### Windows (DevTools)
- `WindowsBleTransport` - uses Windows.Devices.Bluetooth
- `WindowsBleScanner` - BLE discovery
- `WindowsBinaryBleTransport` - binary protocol support
- Full GATT service/characteristic enumeration

### Mobile (MAUI)
- `PluginBleTransportFactory` - uses Plugin.BLE for cross-platform
- Platform-agnostic through abstraction layers
- No direct Windows.Devices.Bluetooth dependency

## Key Files Reference

### Core Interfaces
- `src/ObdInsight.Core/IObdTransport.cs` - Transport abstraction
- `src/ObdInsight.Core/Adapters/IObdAdapter.cs` - Adapter abstraction
- `src/ObdInsight.Core/Vehicles/IVehicleProfile.cs` - Vehicle profile abstraction

### Implementations
- `src/ObdInsight.Core/Adapters/Elm327/Elm327Adapter.cs` - ELM327 protocol
- `src/ObdInsight.Core/Transports/Ble/BleTransportBase.cs` - BLE base implementation
- `src/ObdInsight.Drivers/Vehicles/NissanLeafProfile.cs` - Example vehicle profile

### Testing
- `src/ObdInsight.Core/Transports/Tracing/ReplayTransport.cs` - Replay testing
- `tests/ObdInsight.AdapterComplianceTests/` - Adapter compliance suite

### Configuration
- `src/ObdInsight/MauiProgram.cs` - DI container setup
- `src/ObdInsight.Drivers/Adapters/AdapterRegistry.cs` - Adapter factory
- `src/ObdInsight.Drivers/VehicleProfileRegistry.cs` - Vehicle profile factory

## External Resources

- **EV CAN Signal Glossary**: `EV-CAN_signal_glossary.json` - comprehensive EV PID database
- **Nissan Leaf Notes**: `Leaf_Battery_SoC_SoH_notes.md` - battery SOC/SOH details
- **Icon Guide**: `ICON_UPDATE_GUIDE.md` - SVG icon workflow
- **Vehicle C++ Reference**: `vehicle_nissanleaf.cpp` - reference implementation

## Troubleshooting

### BLE Connection Issues
1. Check device advertises expected service UUID (use DevTools scan)
2. Verify correct `BleDeviceProfile` selected
3. Enable notifications on notify characteristic
4. Check MTU size if writes fail (reduce `MaxWriteSize`)

### Adapter Initialization Failures
1. Verify transport is connected before calling `InitializeAsync`
2. Check ELM327 command sequence (ATZ, ATE0, ATL0)
3. Use longer timeout for slow adapters
4. Replay captured session to isolate issue

### Vehicle Detection Problems
1. Verify VIN prefixes match actual vehicle VIN
2. Check if vehicle uses non-standard protocol (set `VehicleProtocol`)
3. Generate diagnostic report to see supported PIDs
4. Some vehicles need custom initialization commands
