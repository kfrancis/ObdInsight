# ObdInsight

A modern, open-source OBD-II diagnostic application for Android and iOS built with .NET MAUI. 

ObdInsight addresses the poor user experience of existing OBD apps by providing a clean, responsive interface backed by a robust, testable architecture. The pluggable driver system makes it easy to add support for new Bluetooth and WiFi OBD dongles without modifying the core application.

## Why ObdInsight?

- **Better UX**: Designed mobile-first with modern UI patterns
- **Extensible**: Plugin-based driver architecture for easy dongle support
- **Testable**: Drivers can be developed and tested independently of the mobile app
- **Cross-platform**: Single codebase for Android and iOS using .NET MAUI
- **Open**: Community contributions welcome, especially for dongle drivers

## Architecture

ObdInsight separates concerns into testable layers:
- **ObdInsight.Core**: Transport abstractions, driver interfaces, and protocol handling
- **ObdInsight.Drivers**: Built-in support for common dongles (ELM327, etc.)
- **ObdInsight.DevTools**: Windows development tool for BLE debugging and generating vehicle support reports
- **ObdInsight**: The mobile application

Drivers implement a simple interface and can be tested without mobile hardware using mock transports or serial connections.

## Requesting New Vehicle or Adapter Support

If your vehicle or OBD adapter isn't supported, you can help us add support by generating a diagnostic report. This report collects essential information about your vehicle's OBD communication that developers need to create a vehicle profile.

### Prerequisites

- Windows PC with Bluetooth Low Energy support
- OBD-II Bluetooth adapter (ELM327-compatible)
- Vehicle with OBD-II port (1996+ for US vehicles)
- .NET 9 SDK installed

### Generating a Diagnostic Report

1. **Build and run the DevTools**
   ```bash
   cd src/ObdInsight.DevTools
   dotnet run
   ```

2. **Select "Generate Vehicle Support Report"** from the menu

3. **Enter your vehicle information**
   - Year, Make, Model, Trim
   - Engine/Powertrain type (Gasoline, Diesel, Hybrid, Electric, etc.)
   - Transmission type

4. **Enter your OBD adapter's MAC address**
   - If you don't know it, use "Scan for BLE devices" first to find it

5. **Select your BLE adapter profile**
   - Veepeak BLE+ is the default
   - Choose "Auto-detect" if unsure

6. **Start the vehicle** (ignition on or engine running)

7. **Wait for the diagnostic collection to complete**
   - The tool will probe your vehicle's supported PIDs
   - It collects VIN, adapter info, and protocol details
   - EV-specific PIDs are also tested

8. **A markdown report file will be generated** in the current directory

### Submitting Your Report

1. Open a new issue at: https://github.com/kfrancis/ObdInsight/issues/new

2. Use the title format: `Vehicle Support: [Year] [Make] [Model]`
   - Example: `Vehicle Support: 2023 Hyundai Ioniq 6`

3. Copy the entire contents of the generated markdown file into the issue body

4. Add any additional observations:
   - Did certain features work/not work?
   - Any unusual behavior noticed?
   - Other OBD apps that work with your vehicle (for reference)

### What the Report Contains

The diagnostic report includes:

| Section | Description |
|---------|-------------|
| Vehicle Information | Your provided year, make, model, and powertrain details |
| BLE Adapter Info | GATT services and characteristics discovered |
| OBD Adapter Info | ELM327 version, voltage, protocol detection |
| Vehicle Identification | VIN (partially masked for privacy), ECU name, calibration ID |
| Supported PIDs | List of all PIDs your vehicle responds to |
| PID Responses | Raw responses from standard and EV-specific PIDs |
| Errors | Any communication issues encountered |

> **Privacy Note**: The VIN's last 6 characters are automatically masked in the report. The VIN prefix helps identify vehicle make/model/year for profile matching.

### For Adapter Support Requests

If you have an OBD adapter that isn't connecting properly:

1. Run "Scan for BLE devices" to verify the adapter is discoverable
2. Run "Discover device services" to see the GATT service/characteristic UUIDs
3. Include this information in your GitHub issue with the title: `Adapter Support: [Adapter Name]`

## Contributing

Contributions are welcome! Areas where help is especially appreciated:

- **Vehicle Profiles**: Add support for new vehicles by implementing `IVehicleProfile`
- **OBD Adapters**: Add support for new BLE/WiFi adapters
- **Testing**: Generate diagnostic reports for vehicles you own
- **Documentation**: Improve guides and API documentation

See the [Contributing Guide](CONTRIBUTING.md) for more details.