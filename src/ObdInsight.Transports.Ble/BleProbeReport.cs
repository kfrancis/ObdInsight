namespace ObdInsight.Transports.Ble;

public enum BleProbeStage
{
    Connecting,
    DiscoveringServices,
    ResolvingProfile,
    BindingCharacteristics,
    SubscribingNotifications,
    Completed
}

public enum BleProbeFailureKind
{
    Cancelled,
    ConnectionFailed,
    ServiceDiscoveryFailed,
    NoCompatibleProfile,
    CharacteristicBindingFailed,
    NotificationSubscriptionFailed,
    Unknown
}

/// <summary>
/// Sanitized result of one BLE transport-open probe. It deliberately excludes the
/// device address/identifier, advertisement manufacturer payloads, and stack traces.
/// </summary>
public sealed record BleProbeReport(
    BleProbeStage Stage,
    IReadOnlyList<GattServiceInfo> Services,
    ResolvedBleProfile? ResolvedProfile,
    BleProbeFailureKind? FailureKind,
    string? FailureMessage);
