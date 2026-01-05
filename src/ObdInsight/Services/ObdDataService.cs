using CommunityToolkit.Mvvm.Messaging;
using ObdInsight.Controls;
using ObdInsight.Core.Transports.Ble;
using ObdInsight.Core.Vehicles;
using System.Text;

namespace ObdInsight.Services;

/// <summary>
/// Background service that passively monitors CAN broadcast frames and publishes updates via messaging.
/// Uses ELM327 ATMA (Monitor All) command to receive frames without polling.
/// </summary>
/// <remarks>
/// Key CAN frames monitored (from DBC glossary):
/// • 0x1DB: Current, Voltage, Dash SOC (10ms cycle)
/// • 0x5BC: GIDs, SOH, Charge Time (100ms cycle)
/// • 0x55B: High-resolution SOC (100ms cycle)
/// • 0x1DC: Power Limits (10ms cycle)
/// 
/// IMPORTANT: Car must be in READY mode for broadcast frames to be sent.
/// </remarks>
public sealed class ObdDataService : IDisposable
{
    private readonly IConnectedDeviceService _deviceService;
    private readonly IMessenger _messenger;
    private readonly IVehicleDataStore _vehicleDataStore;
    private readonly Dictionary<int, string> _lastFrameValues = new();

    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private bool _disposed;
    private bool _isMonitoring;

    public ObdDataService(IConnectedDeviceService deviceService, IMessenger messenger, IVehicleDataStore vehicleDataStore)
    {
        _deviceService = deviceService;
        _messenger = messenger;
        _vehicleDataStore = vehicleDataStore;

        // Start/stop monitoring when connection changes
        _deviceService.ConnectionChanged += OnConnectionChanged;
    }

    /// <summary>
    /// Whether the service is actively monitoring CAN traffic.
    /// </summary>
    public bool IsMonitoring => _isMonitoring;

    private void OnConnectionChanged(object? sender, DeviceConnectionChangedEventArgs e)
    {
        if (e.IsConnected)
        {
            StartMonitoring();
        }
        else
        {
            StopMonitoring();
        }
    }

    /// <summary>
    /// Start passive CAN monitoring.
    /// </summary>
    public void StartMonitoring()
    {
        if (_monitorTask != null) return;

        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorCanTrafficAsync(_cts.Token), _cts.Token);
    }

    /// <summary>
    /// Stop passive CAN monitoring.
    /// </summary>
    public void StopMonitoring()
    {
        _cts?.Cancel();
        _monitorTask = null;
        _isMonitoring = false;
        _messenger.Send(new MonitorStateChangedMessage(false, "Monitoring stopped"));
    }

    private async Task MonitorCanTrafficAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var transport = _deviceService.Transport;
            try
            {
                if (transport?.IsConnected != true)
                {
                    await Task.Delay(1000, ct);
                    continue;
                }

                // Configure ELM327 for passive monitoring
                await ConfigureMonitorModeAsync(transport, ct);

                _isMonitoring = true;
                _messenger.Send(new MonitorStateChangedMessage(true, "Monitoring CAN traffic"));

                // Run the monitoring loop
                await RunMonitorLoopAsync(transport, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Log error, wait and retry
                _isMonitoring = false;
                await Task.Delay(2000, ct);
            }
        }

        // Cleanup when stopping
        await RestoreAdapterAsync();
    }

    /// <summary>
    /// Configure ELM327 adapter for passive CAN monitoring.
    /// </summary>
    private async Task ConfigureMonitorModeAsync(IBleTransport transport, CancellationToken ct)
    {
        // Reset and configure adapter
        await SendCommandAsync(transport, "ATZ", TimeSpan.FromSeconds(3), ct);   // reset all
        await SendCommandAsync(transport, "ATI", TimeSpan.FromSeconds(3), ct);   // print the ELM327 firmware version ID
        await Task.Delay(500, ct);

        await SendCommandAsync(transport, "ATL1", TimeSpan.FromSeconds(2), ct);  // line feed on
        await SendCommandAsync(transport, "ATH1", TimeSpan.FromSeconds(2), ct);  // header control on
        await SendCommandAsync(transport, "ATS1", TimeSpan.FromSeconds(2), ct);  // print spaces on
        await SendCommandAsync(transport, "ATAL", TimeSpan.FromSeconds(2), ct);  // allow long messages (> 7 byte)
        await SendCommandAsync(transport, "ATSP6", TimeSpan.FromSeconds(2), ct); // set CAN protocol to ISO 15765-4 CAN(11/500kbps)

        // Reset and set up filters
        await SendCommandAsync(transport, "ATCRA", TimeSpan.FromSeconds(2), ct);     // reset can filters
        await SendCommandAsync(transport, "ATCRA 5B3", TimeSpan.FromSeconds(2), ct); // filter response with PID = 0x5B3
    }

    /// <summary>
    /// Main monitoring loop - reads CAN frames and processes them.
    /// </summary>
    private async Task RunMonitorLoopAsync(IBleTransport transport, CancellationToken ct)
    {
        // Start monitor mode
        await transport.WriteAsync("ATMA\r", ct); // Monitor All
        await Task.Delay(200, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var chunk = await transport.ReadUntilAsync("\r", TimeSpan.FromMilliseconds(100), ct);

                if (string.IsNullOrEmpty(chunk))
                    continue;

                var trimmed = chunk.Trim();

                // Handle control messages
                if (trimmed is ">" or "STOPPED" or "BUFFER FULL" or "NO DATA")
                    continue;

                // Look for hex data (CAN frames look like: 1DB8010003FF0...)
                // First 3 chars are CAN ID, rest is data
                if (trimmed.Length >= 5 && IsHexString(trimmed))
                {
                    ProcessCanFrame(trimmed);
                }
            }
            catch (TimeoutException)
            {
                // Normal timeout while waiting for frames
                continue;
            }
        }
    }

    /// <summary>
    /// Process a received CAN frame and send appropriate messages.
    /// </summary>
    private void ProcessCanFrame(string frameData)
    {
        // Parse CAN ID (first 3 hex chars)
        if (!int.TryParse(frameData[..3], System.Globalization.NumberStyles.HexNumber, null, out var canId))
            return;

        // Get data bytes (rest of the string)
        var dataHex = frameData[3..];
        if (dataHex.Length < 10) // At least 5 bytes
            return;

        var data = ParseHexString(dataHex);
        if (data.Length < 5)
            return;

        // Check for duplicate values to reduce message spam
        var frameKey = $"{canId:X3}|{dataHex}";
        if (_lastFrameValues.TryGetValue(canId, out var lastValue) && lastValue == frameKey)
            return;
        _lastFrameValues[canId] = frameKey;

        // Parse known frames and send messages
        switch (canId)
        {
            case 0x1DB when data.Length >= 7:
                var (current, voltage, soc) = Parse1DB(data);
                _messenger.Send(new BatteryStatusMessage(current, voltage, soc));
                break;

            case 0x1DC when data.Length >= 4:
                var (discharge, regen, charge) = Parse1DC(data);
                _messenger.Send(new PowerLimitsMessage(discharge, regen, charge));
                break;

            case 0x5BC when data.Length >= 6:
                var (gids, kwh, sohPct, hxPct) = Parse5BC(data);
                _messenger.Send(new GidsDataMessage(gids, kwh, sohPct, hxPct));
                break;

            case 0x55B when data.Length >= 3:
                var (socPct, socRaw) = Parse55B(data);
                _messenger.Send(new HighResSocMessage(socPct, socRaw));
                break;
        }
    }

    /// <summary>
    /// Restore ELM327 to diagnostic mode after monitoring.
    /// </summary>
    private async Task RestoreAdapterAsync()
    {
        var transport = _deviceService.Transport;
        if (transport?.IsConnected != true)
            return;

        try
        {
            // Stop ATMA - any char stops it
            await transport.WriteAsync(" \r");
            await Task.Delay(150);

            try { await transport.ReadUntilAsync(">", TimeSpan.FromSeconds(3)); } catch { }

            // Reset to diagnostic defaults
            await SendCommandSafeAsync(transport, "ATD", 150, 2000);
            await SendCommandSafeAsync(transport, "ATWS", 800, 3000);
            await SendCommandSafeAsync(transport, "ATE0");
            await SendCommandSafeAsync(transport, "ATL0");
            await SendCommandSafeAsync(transport, "ATS0");
            await SendCommandSafeAsync(transport, "ATH1");
            await SendCommandSafeAsync(transport, "ATCAF1"); // Auto-format ON
            await SendCommandSafeAsync(transport, "ATSP6");
            await SendCommandSafeAsync(transport, "ATAT2");
        }
        catch
        {
            // Best effort cleanup
        }
    }

    #region CAN Frame Parsing

    /// <summary>
    /// Parse 0x1DB frame - Current, Voltage, Dash SOC.
    /// </summary>
    private static (double Current, double Voltage, int Soc) Parse1DB(byte[] data)
    {
        // LB_Current: bits 7-17 (11 bits), signed, factor 0.5
        int currentRaw = ((data[0] & 0x7F) << 4) | ((data[1] & 0xF0) >> 4);
        if ((currentRaw & 0x400) != 0) // Sign extend
            currentRaw |= unchecked((int)0xFFFFF800);
        var currentAmps = currentRaw * 0.5;

        // LB_Total_Voltage: bits 23-32 (10 bits), factor 0.5
        int voltageRaw = ((data[2] & 0x03) << 8) | data[3];
        var voltage = voltageRaw * 0.5;

        // LB_Usable_SOC: bits 32-38 (7 bits)
        var soc = data[4] & 0x7F;

        return (currentAmps, voltage, soc);
    }

    /// <summary>
    /// Parse 0x1DC frame - Power limits.
    /// </summary>
    private static (byte DischargeLimitRaw, byte RegenLimitRaw, byte ChargeLimitRaw) Parse1DC(byte[] data)
    {
        return (data[0], data[1], data[2]);
    }

    /// <summary>
    /// Parse 0x5BC frame - GIDs, kWh, SOH, Hx.
    /// </summary>
    private static (int Gids, double Kwh, double SohPct, double HxPct) Parse5BC(byte[] data)
    {
        // LB_GIDS: first 10 bits
        int gids = ((data[0] & 0xFF) << 2) | ((data[1] & 0xC0) >> 6);

        // Calculate kWh (80 Wh per GID)
        double kwh = gids * 0.08;

        // LB_SOH: bytes 4-5 (approximate - verify with actual data)
        double sohPct = data[4];

        // LB_Hx: byte 5 (approximate - verify with actual data)
        double hxPct = data[5] * 0.5;

        return (gids, kwh, sohPct, hxPct);
    }

    /// <summary>
    /// Parse 0x55B frame - High-resolution SOC.
    /// </summary>
    private static (double SocPct, int SocRaw10Bits) Parse55B(byte[] data)
    {
        // LB_SOC: 10 bits at offset
        int socRaw10Bits = ((data[0] & 0xFF) << 2) | ((data[1] & 0xC0) >> 6);

        // Convert to percentage (0.1% resolution)
        double socPct = socRaw10Bits * 0.1;

        return (socPct, socRaw10Bits);
    }

    #endregion

    #region Utility Methods

    private static async Task<string> SendCommandAsync(IBleTransport transport, string command, TimeSpan timeout, CancellationToken ct = default)
    {
        await transport.WriteAsync(command + "\r", ct);
        return await transport.ReadUntilAsync(">", timeout, ct);
    }

    private async Task SendCommandSafeAsync(IBleTransport transport, string command, int msDelay = 120, int timeoutMs = 1500)
    {
        await transport.WriteAsync(command + "\r");
        if (msDelay > 0) await Task.Delay(msDelay);
        try { await transport.ReadUntilAsync(">", TimeSpan.FromMilliseconds(timeoutMs)); } catch { }
    }

    private static bool IsHexString(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsDigit(c) && (c < 'A' || c > 'F') && (c < 'a' || c > 'f'))
                return false;
        }
        return true;
    }

    private static byte[] ParseHexString(string hex)
    {
        if (hex.Length % 2 != 0)
            hex = hex[..^1]; // Truncate odd length

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                bytes[i] = b;
        }
        return bytes;
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopMonitoring();
        _deviceService.ConnectionChanged -= OnConnectionChanged;
        _cts?.Dispose();
    }
}
