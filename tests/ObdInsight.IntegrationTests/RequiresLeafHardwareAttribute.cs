using OdbTestApp.Tests.Fixtures;

namespace ObdInsight.IntegrationTests;

/// <summary>
///     Conditionally skips hardware integration tests unless <c>LEAF_BLE_ADDRESS</c> is set.
///     The integration suite needs a real Nissan Leaf plus a BLE OBD adapter; on machines
///     without them (developer laptops, CI) the tests skip instead of failing during
///     fixture initialization. Setting the environment variable to the adapter's MAC both
///     opts in and tells <see cref="OdbTestApp.Tests.Fixtures.BleSessionFixture" /> which device to use.
/// </summary>
public sealed class RequiresLeafHardwareAttribute : SkipAttribute
{
    public RequiresLeafHardwareAttribute()
        : base(
            "Requires a Nissan Leaf + BLE OBD adapter. Set LEAF_BLE_ADDRESS to the adapter MAC to enable hardware tests.")
    {
    }

    public override Task<bool> ShouldSkip(TestRegisteredContext context)
        => Task.FromResult(!BleSessionFixture.HardwareRequested);
}
