# ObdTestApp Architecture

## Overview
The codebase has been refactored to organize functionality under the `/Core` directory with a clean, maintainable architecture.

## Directory Structure

```
src/ObdTestApp/
├── Core/
│   ├── Application/           # Application services and orchestration
│   │   ├── DeviceScanService.cs      # BLE device scanning and selection
│   │   └── SessionRetryService.cs    # Session retry logic with automatic recovery
│   │
│   ├── Communication/         # Communication layer
│   │   ├── Bluetooth/         # BLE-specific implementations
│   │   │   ├── BleScanner.cs         # BLE device discovery
│   │   │   └── DevicePreferences.cs  # Device favorites and history
│   │   │
│   │   └── Elm327/            # ELM327 adapter communication
│   │       ├── BleElmTransport.cs    # BLE transport for ELM327
│   │       ├── BtElmTransport.cs     # Bluetooth Classic transport
│   │       ├── ElmFramer.cs          # Frame-level ELM327 communication
│   │       ├── ElmSession.cs         # Session management
│   │       └── IElmTransport.cs      # Transport abstraction
│   │
│   ├── Protocols/             # Communication protocols
│   │   ├── EcuContext.cs             # ECU configuration (headers, filters)
│   │   ├── EcuCommunicationMode.cs   # Query vs Monitoring modes
│   │   ├── ElmParsing.cs             # ELM327 response parsing
│   │   ├── IsoTpParser.cs            # ISO-TP protocol parsing
│   │   └── RawCanFrame.cs            # CAN frame representation
│   │
│   ├── UI/                    # User interface components
│   │   ├── ConsoleHelpers.cs         # Console output utilities
│   │   └── DeviceRenderer.cs         # Device table and panel rendering
│   │
│   └── Vehicles/              # Vehicle-specific implementations
│       ├── VehicleCapabilities.cs    # Generic vehicle capability interfaces
│       ├── VehicleProfile.cs         # Vehicle profile abstraction
│       └── Implementations/   # Concrete vehicle implementations
│           ├── Honda/
│           │   └── CRV/
│           │       └── HondaCrv.cs
│           └── Nissan/
│               └── Leaf/
│                   ├── NissanLeaf.cs
│                   └── AZE0/         # Generation 2 (2016-2017) specific
│                       ├── Capabilities/  # Feature implementations (BMS, Charger, etc.)
│                       ├── Frames/        # CAN frame definitions
│                       ├── LeafAze0CommandSet.cs
│                       └── LeafAze0Contexts.cs
│
└── Program.cs                 # Application entry point

```

## Key Components

### Application Layer (`Core/Application`)
- **DeviceScanService**: Handles BLE device discovery, filtering, and user selection
- **SessionRetryService**: Manages automatic retry with exponential backoff for failed connections

### Communication Layer (`Core/Communication`)

#### Bluetooth (`Core/Communication/Bluetooth`)
- **BleScanner**: Windows BLE device discovery using WinRT APIs
- **DevicePreferences**: Persists favorite and recently-used devices

#### ELM327 (`Core/Communication/Elm327`)
- **IElmTransport**: Abstract transport interface for ELM327 communication
- **BleElmTransport**: BLE Low Energy transport implementation
- **BtElmTransport**: Bluetooth Classic transport implementation
- **ElmFramer**: Low-level framing and command/response handling
- **ElmSession**: High-level session management with protocol detection and locking

### Protocols Layer (`Core/Protocols`)
- **IsoTpParser**: ISO 15765-2 (ISO-TP) multi-frame message reassembly
- **ElmParsing**: ELM327-specific response parsing utilities
- **EcuContext**: Encapsulates CAN headers, filters, and flow control for specific ECUs
- **RawCanFrame**: Represents parsed CAN frames from monitoring mode

### UI Layer (`Core/UI`)
- **ConsoleHelpers**: Safe console output with markup escaping
- **DeviceRenderer**: Renders tables and panels for device selection and statistics

### Vehicles Layer (`Core/Vehicles`)
- **Vehicle Profiles**: Define make, model, variants, and capabilities
- **Capabilities**: Generic interfaces (BMS, Charger, HVAC, Motor Controller, etc.)
- **Implementations**: Vehicle-specific command sets and frame parsing

## Design Patterns

### Repository Pattern
- `DevicePreferences` acts as a repository for device configuration

### Strategy Pattern
- `IElmTransport` allows switching between BLE and Bluetooth Classic
- `VehicleProfile` enables vehicle-specific communication strategies

### Service Layer
- Application services (`DeviceScanService`, `SessionRetryService`) orchestrate complex workflows

### Factory Pattern
- Vehicle profiles create appropriate command sets based on variant ID

## Data Flow

```
User Input
    ↓
DeviceScanService
    ↓
BleScanner → DevicePreferences
    ↓
SessionRetryService
    ↓
BleElmTransport → ElmFramer → ElmSession
    ↓
EcuContext (configuration)
    ↓
Vehicle Commands (NissanLeaf, HondaCrv, etc.)
    ↓
IsoTpParser → Response Data
    ↓
DeviceRenderer → Console Output
```

## Testing Strategy

The architecture supports unit testing through:
- **Dependency Injection**: Services accept dependencies through constructors
- **Interfaces**: `IElmTransport`, `IElmSession` enable mocking
- **Separation of Concerns**: UI, business logic, and data access are isolated

## Future Enhancements

### Planned Improvements
1. **Dependency Injection Container**: Use Microsoft.Extensions.DependencyInjection
2. **Configuration Management**: Move hardcoded values to appsettings.json
3. **Logging Abstraction**: Replace direct Serilog calls with ILogger<T>
4. **More Vehicle Implementations**: Add support for additional makes/models
5. **Plugin Architecture**: Load vehicle profiles dynamically at runtime

### Extensibility Points
- Add new transports by implementing `IElmTransport`
- Add new vehicles by implementing `VehicleProfile`
- Add new capabilities by extending `VehicleCapabilities` interfaces
- Add new UI renderers by creating additional renderer classes

## Migration Notes

### Namespace Changes
All code moved to `ObdTestApp.Core.*` namespaces:
- `ObdTestApp` → `ObdTestApp.Core.Communication.Bluetooth`
- `ObdTestApp` → `ObdTestApp.Core.Communication.Elm327`
- `ObdTestApp` → `ObdTestApp.Core.Protocols`
- `ObdTestApp.Vehicles.*` → `ObdTestApp.Core.Vehicles.Implementations.*`

### Breaking Changes
- `DevicePreferences` is now `public` (was `internal`)
- Parsing methods moved to `IsoTpParser` static class
- UI rendering methods moved to `DeviceRenderer` static class
