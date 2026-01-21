# Task: Refactor ObdInsight ELM327 Communication

## Context

ObdInsight's ELM327 communication layer has a fundamental architectural flaw: it relies on passive CAN bus monitoring (`AT MA`) for data that actually requires active polling or session activation. Analysis of a working OBD app shows the correct pattern.

## The Core Problem

**Current broken pattern:**
```
EnterMonitoringModeAsync() → AT MA → wait for frames → often gets nothing
```

**Correct pattern (from working app):**
```
ATAR → ATCEA → ATSH xxx → ATCRA xxx → ATFCSH xxx → ATFCSM 1 → send diagnostic request
```

Key insight: The working app **always resets filter state** before reconfiguring, and uses **targeted polling** rather than passive monitoring for most data.

## Immediate Tasks

### 1. Add filter reset to ElmSession.cs

In `SetEcuContextAsync` and `EnterMonitoringModeAsync`, add this reset sequence BEFORE any configuration:

```csharp
// Reset adapter state before reconfiguration
await _framer.SendAndReadFrameAsync("AT AR", CommandTimeout, ct);   // Clear receive filter
await _framer.SendAndReadFrameAsync("AT CEA", CommandTimeout, ct);  // Disable extended addressing
```

### 2. Add session activation support to EcuContext.cs

Add these properties:

```csharp
public string? SessionActivationCommand { get; init; }  // e.g., "10C0" or "1081"
public string? KeepAliveCommand { get; init; }          // e.g., "3E80"
public int KeepAliveIntervalMs { get; init; } = 2000;
public bool RequiresSessionActivation { get; init; }
public int AdapterTimeoutUnits { get; init; } = 32;     // ATST value (units of 4ms)
```

### 3. Add ActivateSessionAsync to ElmSession.cs

New method to send session activation before monitoring/queries:

```csharp
public async ValueTask<bool> ActivateSessionAsync(EcuContext context, CancellationToken ct)
```

### 4. Update LeafAze0Contexts.cs

For `SteeringBroadcast`, add:
- `SessionActivationCommand = "1081"`
- `RequiresSessionActivation = true`
- `KeepAliveCommand = "3E80"`

### 5. Update LeafAze0Steering.cs

Before entering monitoring mode, call `ActivateSessionAsync` if the context requires it.

## Files to Modify

1. `src/ObdTestApp.Core/Communication/Elm327/ElmSession.cs`
2. `src/ObdTestApp.Core/Protocols/EcuContext.cs`
3. `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/LeafAze0Contexts.cs`
4. `src/ObdTestApp.Core/Vehicles/Vehicles/Implementations/Nissan/Leaf/AZE0/Capabilities/LeafAze0Steering.cs`

## Success Criteria

- No filter state pollution between ECU context switches
- Session activation sent when configured
- Steering frames (0x002, 0x300) should be more reliably received
- Existing BMS queries (Mode 21) continue to work

## Reference

See `/home/claude/REFACTOR_ELM327_COMMUNICATION.md` for complete architectural documentation.
