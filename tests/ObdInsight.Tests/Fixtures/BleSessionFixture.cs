using ObdInsight;
using ObdInsight.Core.Communication.Elm327;
using TUnit.Core.Interfaces;

namespace OdbTestApp.Tests.Fixtures;

/// <summary>
/// TUnit fixture for managing a real BLE connection to a Nissan Leaf OBD adapter.
/// This fixture establishes and maintains a connection for the lifetime of tests that require it.
/// </summary>
public class BleSessionFixture : IAsyncInitializer, IAsyncDisposable
{
    private BleElmTransport? _transport;
    private ElmSession? _session;
    private ElmFramer? _framer;

    /// <summary>
    /// The MAC address of the BLE device to connect to.
    /// Default: 66:1E:87:02:C2:DB (captured in golden samples)
    /// Set via environment variable: LEAF_BLE_ADDRESS
    /// </summary>
    public string DeviceAddress { get; private set; } = "66:1E:87:02:C2:DB";

    /// <summary>
    /// Gets the active ELM session for sending commands.
    /// </summary>
    public IElmSession Session => _session ?? throw new InvalidOperationException("Session not initialized");

    /// <summary>
    /// Gets the underlying BLE transport.
    /// </summary>
    public BleElmTransport Transport => _transport ?? throw new InvalidOperationException("Transport not initialized");

    /// <summary>
    /// Indicates whether the BLE connection is established and ready.
    /// </summary>
    public bool IsConnected => _transport?.IsOpen == true;

    public async Task InitializeAsync()
    {
        // Allow override via environment variable
        var envAddress = Environment.GetEnvironmentVariable("LEAF_BLE_ADDRESS");
        if (!string.IsNullOrWhiteSpace(envAddress))
        {
            DeviceAddress = envAddress;
        }

        Console.WriteLine($"[BleSessionFixture] Initializing BLE connection to {DeviceAddress}");

        try
        {
            _transport = new BleElmTransport(DeviceAddress)
            {
                EnableDebugLogging = true
            };

            await _transport.OpenAsync(CancellationToken.None);

            _framer = new ElmFramer(_transport)
            {
                EnableDebugLogging = true
            };
            _session = new ElmSession(_framer, new ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.LeafBmsWakeupStrategy())
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
}
