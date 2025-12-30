// Backward compatibility - re-export types from new namespaces
// New code should use:
// - ObdInsight.Core.Transports for IObdTransport
// - ObdInsight.Core.Transports.Ble for BLE-related types  
// - ObdInsight.Core.Adapters for IObdAdapter, ObdCommand, ObdResponse

global using ObdInsight.Core.Transports;
global using ObdInsight.Core.Transports.Ble;
global using ObdInsight.Core.Adapters;

// Type aliases for backward compatibility in the ObdInsight.Core namespace
namespace ObdInsight.Core
{
    // These type aliases allow existing code using ObdInsight.Core.IObdTransport etc. to continue working
    // New code should reference the types from their proper namespaces directly
}