using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf;
using ObdInsight.IntegrationTests;
using ObdInsight.Transports.WindowsBle;
using TUnit.Core.Interfaces;

namespace OdbTestApp.Tests.Fixtures;

/// <summary>
///     TUnit fixture for managing a real BLE connection to a Nissan Leaf OBD adapter.
///     This fixture establishes and maintains a connection for the lifetime of tests that require it.
/// </summary>
public class BleSessionFixture : IAsyncInitializer, IAsyncDisposable
{
    private ElmFramer? _framer;
    private ElmSession? _session;
    private BleElmTransport? _transport;

    /// <summary>
    ///     The MAC address of the BLE device to connect to. No baked-in default (audit
    ///     M3.5 scrub) — hardware tests only run when the LEAF_BLE_ADDRESS environment
    ///     variable is set, and that value is applied during initialization.
    /// </summary>
    public string DeviceAddress { get; private set; } = "";

    /// <summary>
    ///     Gets the active ELM session for sending commands.
    /// </summary>
    public IElmSession Session => _session ?? throw new InvalidOperationException("Session not initialized");

    /// <summary>
    ///     Gets the underlying BLE transport.
    /// </summary>
    public BleElmTransport Transport => _transport ?? throw new InvalidOperationException("Transport not initialized");

    /// <summary>
    ///     Indicates whether the BLE connection is established and ready.
    /// </summary>
    public bool IsConnected => _transport?.IsOpen == true;

    /// <summary>
    ///     True when LEAF_BLE_ADDRESS is set — the opt-in for hardware tests. When false the
    ///     fixture skips BLE initialization entirely; tests are skipped by
    ///     <see cref="RequiresLeafHardwareAttribute" /> before they would touch the session.
    /// </summary>
    public static bool HardwareRequested =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LEAF_BLE_ADDRESS"));

    public async ValueTask DisposeAsync()
    {
        Console.WriteLine("[BleSessionFixture] Disposing BLE session");

        if (_transport != null)
        {
            await _transport.DisposeAsync();
            _transport = null;
        }

        _session = null;
        _framer = null;
    }

    public async Task InitializeAsync()
    {
        if (!HardwareRequested)
        {
            Console.WriteLine(
                "[BleSessionFixture] LEAF_BLE_ADDRESS not set - skipping BLE initialization (hardware tests will be skipped)");
            return;
        }

        var envAddress = Environment.GetEnvironmentVariable("LEAF_BLE_ADDRESS");
        if (!string.IsNullOrWhiteSpace(envAddress))
        {
            DeviceAddress = envAddress;
        }

        Console.WriteLine($"[BleSessionFixture] Initializing BLE connection to {DeviceAddress}");

        try
        {
            _transport = new BleElmTransport(DeviceAddress) { EnableDebugLogging = true };

            await _transport.OpenAsync(CancellationToken.None);

            _framer = new ElmFramer(_transport) { EnableDebugLogging = true };
            _session = new ElmSession(_framer, new LeafBmsWakeupStrategy())
            {
                EnableDebugLogging = true,
                CommandTimeout = TimeSpan.FromSeconds(5),
                ProtocolDetectionTimeout = TimeSpan.FromSeconds(30)
            };

            await _session.InitializeAndLockAsync(CancellationToken.None);

            Console.WriteLine("[BleSessionFixture] BLE session initialized successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BleSessionFixture] Failed to initialize: {ex.Message}");
            await DisposeAsync();
            throw;
        }
    }
}
