# ObdInsight ELM327 Communication Architecture Refactoring

## Executive Summary

Analysis of a working OBD application's ELM327 log reveals that ObdInsight's current approach to data acquisition is fundamentally flawed. The working app uses **targeted request/response polling** as the primary mechanism, not passive monitoring. This document provides a comprehensive refactoring plan.

---

## Problem Statement

### Current ObdInsight Approach (Problematic)

1. **Over-reliance on `AT MA` (Monitor All)** for broadcast frame acquisition
2. **No filter state reset** between ECU context switches - `ATCRA` filters persist and block frames
3. **No session activation** for modules that may require wake-up or OEM session entry
4. **No keep-alive mechanism** for modules that sleep
5. **Conflation of data types** - doesn't distinguish between true broadcasts, wake-dependent broadcasts, and polled data

### Working App Approach (Observed from Log Analysis)

The working app uses this sequence for each ECU:

```
1. ATAR          - Reset address-related filtering state
2. ATCEA         - Disable extended addressing (baseline for 11-bit ISO-TP)
3. ATSH xxx      - Set transmit CAN ID
4. ATCRA xxx     - Set receive filter (Response ID = Request ID + 0x20 for Nissan)
5. ATFCSH xxx    - Set flow control header
6. ATFCSM 1      - Enable flow control mode
7. ATST hh       - Set timeout (aggressive values like 08 = 32ms)
8. Send request  - UDS/ISO-TP diagnostic request
```

**Key Insight**: Even for "broadcast" data, the working app often sends diagnostic requests rather than passively monitoring.

---

## Files to Modify

### Core Communication Layer
- `src/ObdTestApp.Core/Communication/Elm327/ElmSession.cs` - Main session management
- `src/ObdTestApp.Core/Communication/Elm327/ElmFramer.cs` - Frame-level communication
- `src/ObdTestApp.Core/Protocols/EcuContext.cs` - ECU configuration model
- `src/ObdTestApp.Core/Protocols/EcuCommunicationMode.cs` - Communication mode enum

### Leaf-Specific Contexts
- `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/LeafAze0Contexts.cs` - ECU context definitions

### Capability Implementations (all use monitoring incorrectly)
- `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/Capabilities/LeafAze0Steering.cs`
- `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/Capabilities/LeafAze0Hvac.cs`
- `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/Capabilities/LeafAze0MotorController.cs`
- `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/Capabilities/LeafAze0Charger.cs`
- `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/Capabilities/LeafAze0Abs.cs`
- `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/Capabilities/LeafAze0BodyControl.cs`
- `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/Capabilities/LeafAze0Brake.cs`
- `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/Capabilities/LeafAze0Vcm.cs`
- `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/Capabilities/LeafAze0VcmEvCan.cs`
- `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/Capabilities/LeafAze0VcmCarCan.cs`

---

## Phase 1: Fix ELM327 State Management

### Task 1.1: Add Filter Reset to ElmSession

**File**: `src/ObdTestApp.Core/Communication/Elm327/ElmSession.cs`

Add a new method for resetting adapter state and call it before any reconfiguration:

```csharp
/// <summary>
/// Resets ELM327 filter and addressing state to known baseline.
/// Must be called before reconfiguring for a different ECU.
/// </summary>
private async ValueTask ResetAdapterStateAsync(CancellationToken ct)
{
    Log("Resetting adapter state (ATAR, ATCEA, ATAR)");
    
    // Clear any receive address filter
    await _framer.SendAndReadFrameAsync("AT AR", CommandTimeout, ct);
    
    // Disable extended addressing (baseline for 11-bit ISO-TP)
    await _framer.SendAndReadFrameAsync("AT CEA", CommandTimeout, ct);
    
    // Reset address-related filtering state
    // Note: Some adapters use ATAR differently - this clears CRA filters
    await _framer.SendAndReadFrameAsync("AT AR", CommandTimeout, ct);
}
```

**Modify `SetEcuContextAsync`** to call `ResetAdapterStateAsync` before configuration:

```csharp
public async ValueTask SetEcuContextAsync(EcuContext context, CancellationToken ct)
{
    // ... existing validation ...
    
    await _gate.WaitAsync(ct);
    try
    {
        // Always reset state before reconfiguring (even if same context name)
        // This prevents filter pollution from previous operations
        await ResetAdapterStateAsync(ct);
        
        Log($"Configuring ECU context: {context.Name}");
        
        // ... rest of existing configuration ...
    }
    finally { _gate.Release(); }
}
```

### Task 1.2: Add Timeout Configuration to EcuContext

**File**: `src/ObdTestApp.Core/Protocols/EcuContext.cs`

Add property for adapter timeout:

```csharp
/// <summary>
/// Timeout value for ATST command in units of 4ms.
/// Default 32 = 128ms. Working app uses aggressive values like 8 = 32ms for probing.
/// </summary>
public int AdapterTimeoutUnits { get; init; } = 32;
```

**File**: `src/ObdTestApp.Core/Communication/Elm327/ElmSession.cs`

Apply timeout in `SetEcuContextAsync`:

```csharp
// Set adapter timeout if specified
if (context.AdapterTimeoutUnits > 0)
{
    await _framer.SendAndReadFrameAsync($"AT ST {context.AdapterTimeoutUnits:X2}", CommandTimeout, ct);
}
```

### Task 1.3: Fix EnterMonitoringModeAsync State Reset

**File**: `src/ObdTestApp.Core/Communication/Elm327/ElmSession.cs`

Modify `EnterMonitoringModeAsync` to properly reset state:

```csharp
public async ValueTask EnterMonitoringModeAsync(EcuContext context, CancellationToken ct)
{
    // ... existing validation ...
    
    await _gate.WaitAsync(ct);
    try
    {
        if (_currentMode == EcuCommunicationMode.PassiveMonitoring)
        {
            Log("Already in monitoring mode - exiting first");
            await ExitMonitoringModeInternalAsync(ct);
        }

        Log($"Entering monitoring mode: {context.Name}");

        // CRITICAL: Reset adapter state before monitoring configuration
        await ResetAdapterStateAsync(ct);

        // Configure headers/formatting
        await _framer.SendAndReadFrameAsync($"AT H{(context.EnableHeaders ? "1" : "0")}", CommandTimeout, ct);
        await _framer.SendAndReadFrameAsync($"AT CAF{(context.EnableAutoFormatting ? "1" : "0")}", CommandTimeout, ct);

        // ... rest of existing implementation ...
    }
    finally { _gate.Release(); }
}
```

---

## Phase 2: Expand EcuContext for Session Control

### Task 2.1: Add Session and Keep-Alive Properties

**File**: `src/ObdTestApp.Core/Protocols/EcuContext.cs`

Add these properties to the `EcuContext` class:

```csharp
/// <summary>
/// Session activation command (e.g., "10C0" for Nissan OEM session, "1081" for default+suppress).
/// Sent before diagnostic queries or monitoring if module requires session activation.
/// </summary>
/// <remarks>
/// Common values:
/// - "1001" = Default session
/// - "1081" = Default session with suppress-positive-response bit
/// - "10C0" = Nissan OEM-specific session
/// - "1003" = Extended diagnostic session
/// </remarks>
public string? SessionActivationCommand { get; init; }

/// <summary>
/// Keep-alive command to prevent module sleep during extended monitoring.
/// Typically a TesterPresent command ("3E00" or "3E80").
/// </summary>
public string? KeepAliveCommand { get; init; }

/// <summary>
/// Keep-alive interval in milliseconds. Default 2000ms (2 seconds).
/// Most ECUs require keep-alive within 5 seconds to prevent sleep.
/// </summary>
public int KeepAliveIntervalMs { get; init; } = 2000;

/// <summary>
/// Whether this ECU requires session activation before data is available.
/// If true, session will be activated before monitoring or first query.
/// </summary>
public bool RequiresSessionActivation { get; init; }
```

### Task 2.2: Expand EcuCommunicationMode Enum

**File**: `src/ObdTestApp.Core/Protocols/EcuCommunicationMode.cs`

Replace the existing enum:

```csharp
namespace ObdTestApp.Core.Protocols;

/// <summary>
/// Defines the communication mode for interacting with an ECU.
/// These modes determine how data is acquired from the vehicle.
/// </summary>
public enum EcuCommunicationMode
{
    /// <summary>
    /// Active request/response mode using UDS/ISO-TP diagnostic requests.
    /// Send query, receive response, adapter returns to prompt.
    /// Used for: Mode 21/22 queries (BMS, Charger VIN, etc.)
    /// </summary>
    RequestResponse,

    /// <summary>
    /// Passive monitoring of unsolicited broadcast frames.
    /// Uses AT MA (Monitor All) or AT MR (Monitor Receiver).
    /// Used for: True broadcast frames that appear without any requests (e.g., 0x1DB battery status)
    /// WARNING: Not all "broadcast" frames are truly unsolicited - some require session/wake.
    /// </summary>
    PassiveMonitoring,
    
    /// <summary>
    /// Active broadcast monitoring - sends session activation or keep-alive,
    /// then monitors for broadcast responses.
    /// Used for: Wake-dependent broadcast frames (modules that sleep until activated)
    /// </summary>
    ActiveMonitoring,
    
    /// <summary>
    /// Filtered single-ID monitoring using AT MR xxx.
    /// More reliable than AT MA for specific frames, prevents buffer overflow.
    /// </summary>
    FilteredMonitoring
}
```

---

## Phase 3: Add Session Activation to ElmSession

### Task 3.1: Implement Session Activation Method

**File**: `src/ObdTestApp.Core/Communication/Elm327/ElmSession.cs`

Add new method:

```csharp
/// <summary>
/// Activates a diagnostic session with the specified ECU.
/// Required for some ECUs before they will respond to queries or broadcast data.
/// </summary>
/// <param name="context">The ECU context with session configuration.</param>
/// <param name="ct">Cancellation token.</param>
/// <returns>True if session was activated (or no activation required), false if activation failed.</returns>
public async ValueTask<bool> ActivateSessionAsync(EcuContext context, CancellationToken ct)
{
    if (string.IsNullOrEmpty(context.SessionActivationCommand))
    {
        Log($"No session activation required for {context.Name}");
        return true;
    }

    await _gate.WaitAsync(ct);
    try
    {
        Log($"Activating session for {context.Name}: {context.SessionActivationCommand}");
        
        // Ensure ECU context is configured
        if (_activeContext?.Name != context.Name)
        {
            await ResetAdapterStateAsync(ct);
            await ConfigureEcuContextInternalAsync(context, ct);
        }
        
        // Send session activation command
        var response = await SendAndNormalizeAsync(context.SessionActivationCommand, ct);
        
        // Interpret response
        // Positive response: 50 xx (session activated)
        // Negative response: 7F 10 xx (still useful as proof-of-life)
        // No response: May be expected for suppress-positive-response (0x81)
        
        var hasPositiveResponse = response.Any(line => 
            line.Contains("50", StringComparison.OrdinalIgnoreCase));
        var hasNegativeResponse = response.Any(line => 
            line.Contains("7F", StringComparison.OrdinalIgnoreCase));
        var isSuppressPositive = context.SessionActivationCommand.EndsWith("81", StringComparison.OrdinalIgnoreCase) ||
                                  context.SessionActivationCommand.EndsWith("C0", StringComparison.OrdinalIgnoreCase);
        
        if (hasPositiveResponse)
        {
            Log($"Session activated successfully for {context.Name}");
            return true;
        }
        else if (hasNegativeResponse)
        {
            // Negative response still indicates ECU is alive and communicating
            Log($"Session activation received negative response for {context.Name} (ECU is responsive)");
            return true;
        }
        else if (isSuppressPositive && !response.Any(ElmParsing.LooksLikeAdapterError))
        {
            // Suppress-positive-response bit set - no response is expected
            Log($"Session activation sent (suppress-positive-response) for {context.Name}");
            return true;
        }
        else
        {
            Log($"Session activation failed for {context.Name}: {string.Join(", ", response)}");
            return false;
        }
    }
    finally { _gate.Release(); }
}

/// <summary>
/// Internal method to configure ECU context without acquiring gate (caller must hold gate).
/// </summary>
private async ValueTask ConfigureEcuContextInternalAsync(EcuContext context, CancellationToken ct)
{
    // Configure headers and formatting
    await _framer.SendAndReadFrameAsync($"AT H{(context.EnableHeaders ? "1" : "0")}", CommandTimeout, ct);
    await _framer.SendAndReadFrameAsync($"AT CAF{(context.EnableAutoFormatting ? "1" : "0")}", CommandTimeout, ct);

    // Set CAN headers
    if (!string.IsNullOrEmpty(context.TxHeader) && context.TxHeader != "000")
        await _framer.SendAndReadFrameAsync($"AT SH {context.TxHeader}", CommandTimeout, ct);
    if (!string.IsNullOrEmpty(context.RxFilter) && context.RxFilter != "000")
        await _framer.SendAndReadFrameAsync($"AT CRA {context.RxFilter}", CommandTimeout, ct);

    // Configure ISO-TP flow control
    if (!string.IsNullOrEmpty(context.FlowControlHeader))
        await _framer.SendAndReadFrameAsync($"AT FC SH {context.FlowControlHeader}", CommandTimeout, ct);
    if (!string.IsNullOrEmpty(context.FlowControlData))
        await _framer.SendAndReadFrameAsync($"AT FC SD {context.FlowControlData}", CommandTimeout, ct);
    if (!string.IsNullOrEmpty(context.FlowControlMode))
        await _framer.SendAndReadFrameAsync($"AT FC SM {context.FlowControlMode}", CommandTimeout, ct);

    // Set adapter timeout if specified
    if (context.AdapterTimeoutUnits > 0)
        await _framer.SendAndReadFrameAsync($"AT ST {context.AdapterTimeoutUnits:X2}", CommandTimeout, ct);

    _activeContext = context;
    Log($"ECU context '{context.Name}' configured");
}
```

---

## Phase 4: Update LeafAze0Contexts

### Task 4.1: Audit and Update Context Definitions

**File**: `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/LeafAze0Contexts.cs`

Review each broadcast context and update based on actual behavior:

```csharp
/// <summary>
/// Steering broadcast frames - MAY REQUIRE SESSION ACTIVATION.
/// Frames 0x002 (10ms) and 0x300 (20ms) may only appear when steering ECU is awake.
/// </summary>
public static EcuContext SteeringBroadcast => new()
{
    Name = "STEERING Broadcast (0x002, 0x300)",
    TxHeader = "742",             // EPS ECU TX address for session activation
    RxFilter = "",                // Clear for monitoring
    FlowControlHeader = "742",
    CommunicationMode = EcuCommunicationMode.ActiveMonitoring,
    
    // Session activation to wake EPS module
    SessionActivationCommand = "1081",  // Default session with suppress-positive-response
    RequiresSessionActivation = true,
    
    // Keep-alive to prevent sleep during monitoring
    KeepAliveCommand = "3E80",    // TesterPresent with suppress-positive-response
    KeepAliveIntervalMs = 2000,
    
    MonitoringCommand = "AT MA",
    ExpectedCanIds = ["002", "300"],
    
    EnableHeaders = true,
    EnableAutoFormatting = false  // CAF0 required for proper frame parsing
};

/// <summary>
/// HVAC broadcast frames - likely true broadcast, vehicle must be ON/READY.
/// </summary>
public static EcuContext HvacBroadcast => new()
{
    Name = "HVAC Broadcast (0x54A-0x54F)",
    TxHeader = "744",             // HVAC ECU address
    RxFilter = "",
    FlowControlHeader = "744",
    CommunicationMode = EcuCommunicationMode.PassiveMonitoring, // True broadcast when vehicle ON
    
    // Use filtered monitoring to prevent buffer overflow
    MonitoringCommand = "AT MA",
    CanFilterMask = "FF0",
    CanFilterPattern = "540",
    
    ExpectedCanIds = ["54A", "54B", "54C", "54F"],
    
    EnableHeaders = true,
    EnableAutoFormatting = false
};

/// <summary>
/// Motor/Inverter broadcast frames - true broadcast when vehicle in READY mode.
/// 0x1DA broadcasts at 10ms, 0x55A at 100ms.
/// </summary>
public static EcuContext InvMcBroadcast => new()
{
    Name = "INVmc Broadcast (0x1DA, 0x55A)",
    TxHeader = "784",
    RxFilter = "",
    FlowControlHeader = "784",
    CommunicationMode = EcuCommunicationMode.PassiveMonitoring,
    
    MonitoringCommand = "AT MA",
    
    // Consider using filtered monitoring for reliability
    // CanFilterMask = "7FF",
    // CanFilterPattern = "1DA",
    
    ExpectedCanIds = ["1DA", "55A"],
    
    EnableHeaders = true,
    EnableAutoFormatting = false
};
```

---

## Phase 5: Update Capability Implementations

### Task 5.1: Add Fallback Strategy Pattern

Create a base pattern for capabilities that need multiple acquisition strategies:

```csharp
/// <summary>
/// Attempts to acquire data using multiple strategies in order of preference.
/// </summary>
protected async ValueTask<T?> TryAcquireWithFallbackAsync<T>(
    Func<CancellationToken, ValueTask<T?>> primaryStrategy,
    Func<CancellationToken, ValueTask<T?>>? fallbackStrategy,
    CancellationToken ct) where T : class
{
    // Strategy 1: Primary approach
    var result = await primaryStrategy(ct);
    if (result != null) return result;
    
    // Strategy 2: Fallback if available
    if (fallbackStrategy != null)
    {
        result = await fallbackStrategy(ct);
        if (result != null) return result;
    }
    
    return null;
}
```

### Task 5.2: Update LeafAze0Steering with Session Activation

**File**: `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/Capabilities/LeafAze0Steering.cs`

```csharp
public async ValueTask<SteeringStatus> GetStatusAsync(CancellationToken ct = default)
{
    var timeout = TimeSpan.FromMilliseconds(300);
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeoutCts.CancelAfter(timeout);

    // If context requires session activation, do it first
    if (_context.RequiresSessionActivation)
    {
        var sessionActivated = await _session.ActivateSessionAsync(_context, ct);
        if (!sessionActivated)
        {
            Log($"[Steering] Session activation failed for {_context.Name}");
            // Continue anyway - may still get data
        }
    }

    await _session.EnterMonitoringModeAsync(_context, ct);

    SteeringFrame_002_AZE0? frame002 = null;
    SteeringFrame_300_AZE0? frame300 = null;

    try
    {
        await foreach (var frame in _session.MonitorFramesAsync(timeoutCts.Token))
        {
            // ... existing parsing logic ...
        }
    }
    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
    {
        // Timeout - return whatever data we collected
    }
    finally
    {
        await _session.ExitMonitoringModeAsync(ct);
    }

    // ... existing status building logic ...
}
```

---

## Phase 6: Testing Strategy

### Task 6.1: Create Diagnostic Test Method

Add to `ElmSession` for testing different acquisition approaches:

```csharp
/// <summary>
/// Diagnostic method to test frame acquisition for a given context.
/// Returns raw frames received and timing information.
/// </summary>
public async ValueTask<DiagnosticResult> DiagnoseContextAsync(
    EcuContext context, 
    TimeSpan duration,
    CancellationToken ct)
{
    var result = new DiagnosticResult
    {
        ContextName = context.Name,
        StartTime = DateTime.UtcNow
    };
    
    try
    {
        // Test 1: Passive monitoring without session
        result.PassiveMonitoringFrames = await TestPassiveMonitoringAsync(context, duration, ct);
        
        // Test 2: With session activation
        if (!string.IsNullOrEmpty(context.SessionActivationCommand))
        {
            await ActivateSessionAsync(context, ct);
            result.ActiveMonitoringFrames = await TestPassiveMonitoringAsync(context, duration, ct);
        }
        
        result.Success = result.PassiveMonitoringFrames.Count > 0 || 
                         result.ActiveMonitoringFrames.Count > 0;
    }
    catch (Exception ex)
    {
        result.Error = ex.Message;
    }
    
    result.EndTime = DateTime.UtcNow;
    return result;
}

public class DiagnosticResult
{
    public string ContextName { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public List<RawCanFrame> PassiveMonitoringFrames { get; set; } = new();
    public List<RawCanFrame> ActiveMonitoringFrames { get; set; } = new();
    public bool Success { get; set; }
    public string? Error { get; set; }
}
```

---

## Implementation Order

Execute these tasks in order:

1. **Phase 1.1**: Add `ResetAdapterStateAsync` to `ElmSession`
2. **Phase 1.2**: Add `AdapterTimeoutUnits` to `EcuContext`
3. **Phase 1.3**: Update `EnterMonitoringModeAsync` to reset state
4. **Phase 2.1**: Add session/keep-alive properties to `EcuContext`
5. **Phase 2.2**: Expand `EcuCommunicationMode` enum
6. **Phase 3.1**: Add `ActivateSessionAsync` to `ElmSession`
7. **Phase 4.1**: Update `LeafAze0Contexts` with session configuration
8. **Phase 5.2**: Update `LeafAze0Steering` as reference implementation
9. **Phase 5.x**: Update remaining capabilities following same pattern
10. **Phase 6.1**: Add diagnostic testing method

---

## Validation Checklist

After implementation, verify:

- [ ] Filter state is reset before each ECU context switch
- [ ] Session activation command is sent when `RequiresSessionActivation` is true
- [ ] Steering frames (0x002, 0x300) are reliably received
- [ ] HVAC frames (0x54A-0x54F) are reliably received  
- [ ] Motor controller frames (0x1DA, 0x55A) are reliably received
- [ ] No `BUFFER FULL` errors during normal operation
- [ ] Graceful degradation when session activation fails
- [ ] Proper cleanup when exiting monitoring mode

---

## Open Questions (Requires Physical Testing)

1. Do steering frames 0x002/0x300 appear without any session activation?
2. Which Nissan Leaf modules respond to `10 C0` vs `10 81`?
3. What's the minimum keep-alive interval to prevent EPS sleep?
4. Does the specific ELM327 adapter properly clear filters with `AT AR`?
5. Are there Nissan-specific diagnostic services beyond standard UDS?
