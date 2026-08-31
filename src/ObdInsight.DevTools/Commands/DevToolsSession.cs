using ObdInsight.Core.Communication.Elm327;
using Spectre.Console;

namespace ObdInsight.DevTools.Commands;

/// <summary>
/// Manages the current device session state across all DevTools commands.
/// This allows connecting once and using the connection across multiple operations.
/// </summary>
public sealed class DevToolsSession : IAsyncDisposable
{
    private WindowsBleTransport? _transport;
    private Elm327Adapter? _adapter;
    private WindowsBinaryBleTransport? _binaryTransport;
    
    // Track whether we're inside a Status operation to suppress logging
    private volatile bool _suppressLogging;

    /// <summary>
    /// Device history for favorites/recent devices.
    /// </summary>
    public DeviceHistory DeviceHistory { get; } = DeviceHistory.Load();

    /// <summary>
    /// The currently selected device address (MAC address).
    /// </summary>
    public string? DeviceAddress { get; private set; }

    /// <summary>
    /// The friendly name of the connected device.
    /// </summary>
    public string? DeviceName { get; private set; }

    /// <summary>
    /// The BLE profile being used for the connection.
    /// </summary>
    public BleDeviceProfile? Profile { get; private set; }

    /// <summary>
    /// Whether we have an active ASCII/ELM327 transport connection.
    /// </summary>
    public bool IsConnected => _transport?.IsConnected == true;

    /// <summary>
    /// Whether we have an active binary transport connection.
    /// </summary>
    public bool IsBinaryConnected => _binaryTransport?.IsConnected == true;

    /// <summary>
    /// The active ASCII transport (for ELM327 communication).
    /// </summary>
    public WindowsBleTransport? Transport => _transport;

    /// <summary>
    /// The active ELM327 adapter.
    /// </summary>
    public Elm327Adapter? Adapter => _adapter;

    /// <summary>
    /// The active binary transport (for direct CAN communication).
    /// </summary>
    public WindowsBinaryBleTransport? BinaryTransport => _binaryTransport;

    /// <summary>
    /// Event logging for BLE traffic. When true, logs are shown in real-time.
    /// When false, logs are suppressed (useful during Status operations).
    /// </summary>
    public bool EnableTrafficLogging { get; set; } = true;

    /// <summary>
    /// Temporarily suppresses BLE/ELM traffic logging without tearing down the connection.
    /// High-volume commands (raw CAN capture) set this so per-chunk RX logging does not
    /// flood the console or corrupt a live-rendered display.
    /// </summary>
    public bool SuppressTrafficLogging
    {
        get => _suppressLogging;
        set => _suppressLogging = value;
    }

    /// <summary>
    /// Set the target device without connecting.
    /// </summary>
    public void SetDevice(string address, string? name = null, BleDeviceProfile? profile = null)
    {
        DeviceAddress = address;
        DeviceName = name ?? address;
        Profile = profile ?? BleDeviceProfile.VeepeakBle;
    }

    /// <summary>
    /// Connect to the current device using ASCII/ELM327 protocol.
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(DeviceAddress))
        {
            AnsiConsole.MarkupLine("[red]No device selected. Use 'Scan for devices' or 'Set device address' first.[/]");
            return false;
        }

        // Disconnect any existing connection
        await DisconnectAsync();

        Profile ??= BleDeviceProfile.VeepeakBle;
        _transport = new WindowsBleTransport(Profile);

        if (EnableTrafficLogging)
        {
            _transport.DataSent += OnDataSent;
            _transport.DataReceived += OnDataReceived;
        }

        // Suppress logging during status operation to prevent display corruption
        _suppressLogging = true;
        bool connected;
        try
        {
            connected = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Connecting to {DeviceName} ({DeviceAddress})...", async ctx =>
                {
                    return await _transport.ConnectAsync(DeviceAddress, ct);
                });
        }
        finally
        {
            _suppressLogging = false;
        }

        if (!connected)
        {
            AnsiConsole.MarkupLine("[red]Failed to connect![/]");
            _transport.Dispose();
            _transport = null;
            return false;
        }

        // Save to device history on successful connection
        DeviceHistory.AddOrUpdate(DeviceAddress, DeviceName, Profile.Name);

        AnsiConsole.MarkupLine($"[green]?[/] Connected to [cyan]{DeviceName}[/]");
        return true;
    }

    /// <summary>
    /// Connect and initialize the ELM327 adapter.
    /// </summary>
    public async Task<bool> ConnectAndInitializeAdapterAsync(bool minimalInit = false, CancellationToken ct = default)
    {
        if (!await ConnectAsync(ct))
            return false;

        _adapter = new Elm327Adapter();
        
        if (EnableTrafficLogging)
        {
            _adapter.Log += OnAdapterLog;
        }

        if (minimalInit)
        {
            // Minimal init - skip protocol search (useful for EVs)
            AnsiConsole.MarkupLine("[grey]Using minimal initialization (skipping protocol search)...[/]");
            
            if (!await MinimalAdapterInitAsync(ct))
            {
                AnsiConsole.MarkupLine("[yellow]Minimal initialization had issues[/]");
                return false;
            }
            
            _adapter.SetTransport(_transport!, markAsInitialized: true);
            AnsiConsole.MarkupLine("[green]?[/] Adapter initialized (minimal mode)");
        }
        else
        {
            // Suppress logging during status operation
            _suppressLogging = true;
            bool initialized;
            try
            {
                initialized = await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Initializing ELM327 adapter...", async ctx =>
                    {
                        return await _adapter.InitializeAsync(_transport!, ct);
                    });
            }
            finally
            {
                _suppressLogging = false;
            }

            if (!initialized)
            {
                AnsiConsole.MarkupLine("[yellow]Adapter initialization completed with warnings[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[green]?[/] Adapter ready!");
            }
        }

        return true;
    }

    /// <summary>
    /// Connect using binary protocol (service 6287).
    /// </summary>
    public async Task<bool> ConnectBinaryAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(DeviceAddress))
        {
            AnsiConsole.MarkupLine("[red]No device selected.[/]");
            return false;
        }

        // Disconnect any existing binary connection
        if (_binaryTransport != null)
        {
            await _binaryTransport.DisconnectAsync();
            await _binaryTransport.DisposeAsync();
            _binaryTransport = null;
        }

        var binaryProfile = BleDeviceProfile.VeepeakBinary;
        _binaryTransport = new WindowsBinaryBleTransport(binaryProfile);

        // Suppress logging during status operation
        _suppressLogging = true;
        bool connected;
        try
        {
            connected = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Connecting to {DeviceName} (binary mode)...", async ctx =>
                {
                    return await _binaryTransport.ConnectAsync(DeviceAddress, ct);
                });
        }
        finally
        {
            _suppressLogging = false;
        }

        if (!connected)
        {
            AnsiConsole.MarkupLine("[red]Failed to connect to binary service![/]");
            await _binaryTransport.DisposeAsync();
            _binaryTransport = null;
            return false;
        }

        AnsiConsole.MarkupLine($"[green]?[/] Connected to binary service on [cyan]{DeviceName}[/]");
        return true;
    }

    /// <summary>
    /// Disconnect all active connections.
    /// </summary>
    public async Task DisconnectAsync()
    {
        if (_transport != null)
        {
            // Unsubscribe from events
            _transport.DataSent -= OnDataSent;
            _transport.DataReceived -= OnDataReceived;
            
            try { await _transport.DisconnectAsync(); } catch { }
            _transport.Dispose();
            _transport = null;
        }

        if (_binaryTransport != null)
        {
            try { await _binaryTransport.DisconnectAsync(); } catch { }
            await _binaryTransport.DisposeAsync();
            _binaryTransport = null;
        }

        if (_adapter != null)
        {
            _adapter.Log -= OnAdapterLog;
            _adapter = null;
        }

        AnsiConsole.MarkupLine("[grey]Disconnected[/]");
    }

    /// <summary>
    /// Ensure we have a valid connection, reconnecting if necessary.
    /// </summary>
    public async Task<bool> EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (_transport?.IsConnected == true)
        {
            // Validate the connection is actually usable
            if (await ValidateConnectionAsync())
                return true;
        }

        AnsiConsole.MarkupLine("[yellow]Connection lost. Reconnecting...[/]");
        return await ConnectAndInitializeAdapterAsync(minimalInit: true, ct);
    }

    /// <summary>
    /// Get the current connection status display string.
    /// </summary>
    public string GetStatusDisplay()
    {
        if (string.IsNullOrEmpty(DeviceAddress))
            return "[grey]No device selected[/]";

        if (IsConnected)
            return $"[green]Connected:[/] {DeviceName} ({DeviceAddress})";

        if (IsBinaryConnected)
            return $"[cyan]Binary mode:[/] {DeviceName} ({DeviceAddress})";

        return $"[yellow]Selected:[/] {DeviceName} ({DeviceAddress}) [grey](not connected)[/]";
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    private async Task<bool> ValidateConnectionAsync()
    {
        if (_transport == null || !_transport.IsConnected)
            return false;

        try
        {
            _transport.DrainBuffer();
            await _transport.WriteAsync("ATI\r");
            var response = await _transport.ReadUntilAsync(">", TimeSpan.FromSeconds(4));
            return !string.IsNullOrWhiteSpace(response) && 
                   response.Contains("ELM", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> MinimalAdapterInitAsync(CancellationToken ct)
    {
        if (_transport == null || !_transport.IsConnected)
            return false;

        async Task<(bool Success, string Response)> SendAsync(string cmd, TimeSpan timeout)
        {
            try
            {
                _transport.DrainBuffer();
                await _transport.WriteAsync(cmd + "\r", ct);
                var response = await _transport.ReadUntilAsync(">", timeout, ct);
                response = response.Replace(cmd, "").Replace(">", "").Replace("\r", "").Trim();
                var success = !string.IsNullOrWhiteSpace(response) && 
                             !response.Contains("?") &&
                             !response.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
                return (success, response);
            }
            catch
            {
                return (false, "");
            }
        }

        // Reset
        var (atzOk, _) = await SendAsync("ATZ", TimeSpan.FromSeconds(5));
        await Task.Delay(500, ct);

        // Check we can communicate
        var (atiOk, atiResp) = await SendAsync("ATI", TimeSpan.FromSeconds(3));
        if (!atiOk) return false;

        // Basic setup
        foreach (var cmd in new[] { "ATE0", "ATL0", "ATS0", "ATH0" })
        {
            var (ok, _) = await SendAsync(cmd, TimeSpan.FromSeconds(2));
            if (!ok) return false;
            await Task.Delay(100, ct);
        }

        // Set protocol
        await SendAsync("ATSP6", TimeSpan.FromSeconds(3));
        return true;
    }

    private void OnDataSent(object? sender, string data)
    {
        if (_suppressLogging) return;
        LogTraffic("TX", data);
    }

    private void OnDataReceived(object? sender, string data)
    {
        if (_suppressLogging) return;
        LogTraffic("RX", data);
    }

    private void OnAdapterLog(object? sender, Elm327LogEventArgs e)
    {
        if (_suppressLogging) return;
        LogAdapter(e);
    }

    private static void LogTraffic(string direction, string data)
    {
        var escaped = data.Replace("\r", "\\r").Replace("\n", "\\n").Replace(">", ">");
        var color = direction == "TX" ? "blue" : "green";
        AnsiConsole.MarkupLine($"[grey]BLE[/] [{color}]{direction}[/]: [white]{escaped.EscapeMarkup()}[/]");
    }

    private static void LogAdapter(Elm327LogEventArgs e)
    {
        var color = e.Level switch
        {
            Elm327LogLevel.Debug => "grey",
            Elm327LogLevel.Info => "cyan",
            Elm327LogLevel.Warning => "yellow",
            Elm327LogLevel.Error => "red",
            _ => "white"
        };
        AnsiConsole.MarkupLine($"[grey]ELM[/] [{color}]{e.Level}[/]: {e.Message.EscapeMarkup()}");
    }
}
