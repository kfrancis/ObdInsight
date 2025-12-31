# Contributing to ObdInsight

Thank you for your interest in contributing to ObdInsight! This document provides guidelines and instructions for contributing to the project.

## Table of Contents

- [Code of Conduct](#code-of-conduct)
- [How Can I Contribute?](#how-can-i-contribute)
- [Development Setup](#development-setup)
- [Coding Standards](#coding-standards)
- [Pull Request Process](#pull-request-process)
- [Adding Vehicle Support](#adding-vehicle-support)
- [Adding Adapter Support](#adding-adapter-support)

## Code of Conduct

This project adheres to a code of conduct that all contributors are expected to follow. Please be respectful, inclusive, and considerate in all interactions.

### Our Standards

- **Be respectful**: Treat everyone with respect and kindness
- **Be inclusive**: Welcome diverse perspectives and experiences
- **Be constructive**: Provide helpful feedback and suggestions
- **Be patient**: Remember that we're all learning and growing

## How Can I Contribute?

### Reporting Bugs

Before creating a bug report, please check existing issues to avoid duplicates. When creating a bug report, include:

- **Clear title**: Describe the issue concisely
- **Steps to reproduce**: Detailed steps to reproduce the problem
- **Expected behavior**: What you expected to happen
- **Actual behavior**: What actually happened
- **Environment**: Device, OS version, .NET version, OBD adapter model
- **Logs**: Relevant error messages or logs

### Suggesting Enhancements

Enhancement suggestions are welcome! Please include:

- **Clear description**: What feature or improvement you're suggesting
- **Use case**: Why this would be useful
- **Examples**: How it might work or look
- **Alternatives**: Other approaches you've considered

### Contributing Code

1. **Check existing issues**: Look for issues labeled `good first issue` or `help wanted`
2. **Discuss major changes**: Open an issue first to discuss significant changes
3. **Follow coding standards**: Adhere to the project's coding conventions
4. **Write tests**: Include tests for new features or bug fixes
5. **Update documentation**: Update relevant documentation

## Development Setup

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- IDE: [Visual Studio 2022](https://visualstudio.microsoft.com/), [Visual Studio Code](https://code.visualstudio.com/), or [JetBrains Rider](https://www.jetbrains.com/rider/)
- For mobile development:
  - **Android**: Android SDK (API 21+), Android emulator or physical device
  - **iOS**: macOS with Xcode 14+, iOS simulator or physical device
- For DevTools: Windows 10/11 with Bluetooth Low Energy support

### Setting Up Your Development Environment

```bash
# Clone your fork
git clone https://github.com/YOUR-USERNAME/ObdInsight.git
cd ObdInsight

# Add upstream remote
git remote add upstream https://github.com/kfrancis/ObdInsight.git

# Create a new branch
git checkout -b feature/your-feature-name

# Restore dependencies
dotnet restore

# Build the solution
dotnet build
```

### Running Tests

```bash
# Run all tests
dotnet test

# Run tests for a specific project
dotnet test tests/ObdInsight.Core.Tests/

# Run tests with coverage
dotnet test /p:CollectCoverage=true
```

## Coding Standards

### C# Style Guidelines

- Follow [C# Coding Conventions](https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Use meaningful variable and method names
- Write clear, concise comments for complex logic
- Keep methods focused and small
- Use async/await for asynchronous operations
- Prefer dependency injection over static classes

### Code Formatting

- **Indentation**: 4 spaces (no tabs)
- **Line length**: Aim for 120 characters maximum
- **Braces**: Use Allman style (braces on new line)
- **Naming conventions**:
  - PascalCase for classes, methods, properties
  - camelCase for local variables and parameters
  - _camelCase for private fields (with underscore prefix)
  - SCREAMING_SNAKE_CASE for constants

### Example

```csharp
public class VehicleService : IVehicleService
{
    private readonly IObdAdapter _adapter;
    private readonly ILogger<VehicleService> _logger;

    public VehicleService(IObdAdapter adapter, ILogger<VehicleService> logger)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<VehicleData> GetVehicleDataAsync(string pid)
    {
        try
        {
            var response = await _adapter.SendCommandAsync(pid);
            return ParseResponse(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get vehicle data for PID {Pid}", pid);
            throw;
        }
    }

    private VehicleData ParseResponse(string response)
    {
        // Parse logic here
        return new VehicleData();
    }
}
```

## Pull Request Process

### Before Submitting

1. **Update your fork**: Sync with the upstream repository
   ```bash
   git fetch upstream
   git merge upstream/main
   ```

2. **Run tests**: Ensure all tests pass
   ```bash
   dotnet test
   ```

3. **Build successfully**: Ensure the project builds without errors
   ```bash
   dotnet build
   ```

4. **Update documentation**: Update README or other docs if needed

### Submitting Your Pull Request

1. **Push your changes**:
   ```bash
   git push origin feature/your-feature-name
   ```

2. **Create Pull Request**: Go to GitHub and create a new Pull Request

3. **Fill out the template**: Provide a clear description of your changes

4. **Link related issues**: Reference any related issues (e.g., "Fixes #123")

### Pull Request Guidelines

- **One feature per PR**: Keep pull requests focused on a single feature or fix
- **Clear title**: Use a descriptive title that summarizes the changes
- **Description**: Explain what changes were made and why
- **Tests**: Include tests for new functionality
- **Documentation**: Update relevant documentation
- **Commits**: Keep commits atomic and well-described

### Review Process

- A maintainer will review your PR
- Address any feedback or requested changes
- Once approved, a maintainer will merge your PR

## Adding Vehicle Support

To add support for a new vehicle:

### 1. Create Vehicle Profile Class

Create a new class in `src/ObdInsight.Drivers/Vehicles/` implementing `IVehicleProfile`:

```csharp
using ObdInsight.Core.Vehicles;

namespace ObdInsight.Drivers.Vehicles;

public class MyVehicleProfile : IVehicleProfile
{
    public string Name => "My Vehicle Name";
    public string Manufacturer => "Vehicle Manufacturer";
    public bool IsElectric => false; // or true for EVs
    public IReadOnlyList<string> VinPrefixes => new[] { "ABC", "XYZ" };
    
    public IReadOnlyList<VehiclePid> CustomPids => new[]
    {
        new VehiclePid(
            name: "Custom Data Point",
            pid: "220100", // Custom PID code
            dataPoint: VehicleDataPoint.Custom,
            unit: "units")
        {
            Decoder = bytes => DecodeCustomData(bytes)
        }
    };
    
    private static double DecodeCustomData(byte[] bytes)
    {
        // Implement decoding logic
        return bytes[0] / 2.55;
    }
    
    // Implement other required interface members
}
```

### 2. Register the Profile

Add your profile to the registry in `src/ObdInsight.Drivers/Vehicles/VehicleProfileRegistry.cs`:

```csharp
profiles.Add(new MyVehicleProfile());
```

### 3. Test Your Profile

- Generate a diagnostic report using ObdInsight.DevTools
- Test with a real vehicle if possible
- Add unit tests for decoder logic

### 4. Submit Pull Request

Include in your PR:
- The vehicle profile class
- Registry update
- Tests (if applicable)
- Diagnostic report from your vehicle (if available)

## Adding Adapter Support

To add support for a new OBD adapter:

### 1. Implement Adapter Class

Create a new class in `src/ObdInsight.Core/Adapters/` implementing `IObdAdapter`:

```csharp
using ObdInsight.Core;

namespace ObdInsight.Core.Adapters;

public class MyObdAdapter : IObdAdapter
{
    private readonly IObdTransport _transport;
    
    public MyObdAdapter(IObdTransport transport)
    {
        _transport = transport;
    }
    
    public async Task InitializeAsync()
    {
        // Initialization sequence for your adapter
    }
    
    public async Task<string> SendCommandAsync(string command)
    {
        // Command/response handling
    }
    
    // Implement other required interface members
}
```

### 2. Create Device Profile (for BLE adapters)

If your adapter uses Bluetooth Low Energy, create a device profile:

```csharp
public class MyBleDeviceProfile : BleDeviceProfile
{
    public override string Name => "My Adapter Name";
    public override Guid ServiceUuid => new Guid("your-service-uuid");
    public override Guid CharacteristicUuid => new Guid("your-characteristic-uuid");
    
    // Implement required members
}
```

### 3. Register the Adapter

Add your adapter to the registry in `src/ObdInsight.Drivers/Adapters/AdapterRegistry.cs`.

### 4. Test Your Adapter

- Test with DevTools on Windows
- Test with a real vehicle
- Verify communication with various vehicles

### 5. Submit Pull Request

Include in your PR:
- Adapter implementation
- Device profile (if BLE)
- Registry update
- Documentation on how to use the adapter
- Test results or diagnostic reports

## Questions?

If you have questions or need help:

- Open an issue on GitHub
- Start a discussion in GitHub Discussions
- Check existing documentation and issues

Thank you for contributing to ObdInsight!
