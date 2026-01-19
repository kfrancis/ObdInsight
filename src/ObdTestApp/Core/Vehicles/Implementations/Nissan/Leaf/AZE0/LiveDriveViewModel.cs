using ObdInsight.EvTestDrive.Core.Models;
using ObdInsight.EvTestDrive.Core.Services;
using ObdInsight.EvTestDrive.Services;
using ObdInsight.Obd.Bluetooth;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Timers;

namespace ObdInsight.EvTestDrive.ViewModels;

[QueryProperty(nameof(SessionId), "sessionId")]
public partial class LiveDriveViewModel : BaseViewModel, IDisposable
{
    // UDS Request/Response IDs for detailed queries (pre/post check only)
    private const int BMS_RXID = 0x7BB;

    private const int BMS_TXID = 0x79B;
    private const int CanFrameFlushIntervalMs = 5000;
    private const int ConnectionCheckIntervalMs = 5000;

    // 5 seconds
    private const int MaxRetries = 3;

    private const string ReportRoute = "//Report";
    private const int TelemetryIntervalMs = 2000;
    private const int VCM_RXID = 0x79A;
    private const int VCM_TXID = 0x797;
    // 2 seconds - UI update interval
    // Flush CAN frames every 5 seconds

    // CAN frame buffer for ATMA monitoring
    private readonly ConcurrentDictionary<int, (DateTime timestamp, byte[] data)> _canFrameBuffer = new();

    private readonly IConnectedDeviceService _connectedDeviceService;
    private readonly System.Timers.Timer _connectionCheckTimer;
    private readonly ConcurrentQueue<(DateTime timestamp, int canId, byte[] data)> _rawFrameQueue = new();
    private readonly System.Timers.Timer _telemetryTimer;
    private readonly ITestDriveService _testDriveService;

    [ObservableProperty]
    private decimal _batteryTemperature;

    [ObservableProperty]
    private bool _canMarkEvent;

    [ObservableProperty]
    private bool _canStartDrive;

    [ObservableProperty]
    private bool _canStopDrive;

    [ObservableProperty]
    private decimal? _cellVoltageVariance;

    [ObservableProperty]
    private string _connectionHealth = "Checking...";

    [ObservableProperty]
    private string _connectionStatus = "Checking connection...";

    private int _consecutiveErrors;

    [ObservableProperty]
    private decimal _current;

    [ObservableProperty]
    private TestDrivePhase _currentPhase = TestDrivePhase.PreCheck;

    [ObservableProperty]
    private decimal _dischargePowerLimitKw;

    private bool _disposed;

    // Drive statistics
    [ObservableProperty]
    private TimeSpan _driveDuration;

    [ObservableProperty]
    private bool _ecoModeActive;

    [ObservableProperty]
    private string _gpsStatus = "Waiting for GPS...";

    // Connection status
    [ObservableProperty]
    private bool _isDeviceConnected;

    private bool _isMonitoring;
    private bool _isReconnecting;
    private DateTime _lastFrameFlush = DateTime.UtcNow;

    [ObservableProperty]
    private double? _latitude;

    [ObservableProperty]
    private double? _longitude;

    [ObservableProperty]
    private decimal? _maxCellVoltage;

    [ObservableProperty]
    private decimal? _minCellVoltage;

    private CancellationTokenSource? _monitorCts;

    [ObservableProperty]
    private int _motorRpm;

    [ObservableProperty]
    private decimal _motorTorque;

    [ObservableProperty]
    private string _phaseDescription = "Performing Pre-Drive Diagnostics";

    [ObservableProperty]
    private string _postCheckStatus = string.Empty;

    [ObservableProperty]
    private decimal _power;

    [ObservableProperty]
    private string _preCheckStatus = string.Empty;

    [ObservableProperty]
    private int _rawFramesCollected;

    [ObservableProperty]
    private ObservableCollection<string> _recentEvents = [];

    [ObservableProperty]
    private int _remainingGids;

    [ObservableProperty]
    private decimal? _remainingRange;

    private Guid _sessionGuid;

    [ObservableProperty]
    private string? _sessionId;

    [ObservableProperty]
    private bool _showDrivingUI;

    [ObservableProperty]
    private bool _showPostCheckUI;

    [ObservableProperty]
    private bool _showPreCheckUI;

    [ObservableProperty]
    private int _sohPercent;

    [ObservableProperty]
    private decimal _speed;

    // Real-time telemetry display
    [ObservableProperty]
    private decimal _stateOfCharge;

    private CancellationTokenSource? _telemetryCts;

    [ObservableProperty]
    private int _telemetryPointsCollected;

    [ObservableProperty]
    private decimal _voltage;

    public LiveDriveViewModel(
        ITestDriveService testDriveService,
        IConnectedDeviceService connectedDeviceService)
    {
        _testDriveService = testDriveService;
        _connectedDeviceService = connectedDeviceService;

        _telemetryTimer = new System.Timers.Timer(TelemetryIntervalMs);
        _telemetryTimer.Elapsed += OnTelemetryTimerElapsed;
        _telemetryTimer.AutoReset = true;

        _connectionCheckTimer = new System.Timers.Timer(ConnectionCheckIntervalMs);
        _connectionCheckTimer.Elapsed += OnConnectionCheckTimerElapsed;
        _connectionCheckTimer.AutoReset = true;

        Title = "Test Drive";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _telemetryTimer.Stop();
        _telemetryTimer.Dispose();
        _connectionCheckTimer.Stop();
        _connectionCheckTimer.Dispose();
        _telemetryCts?.Cancel();
        _telemetryCts?.Dispose();
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
    }

    #region ATMA CAN Monitoring

    /// <summary>
    /// Build LiveTelemetry from buffered CAN frames
    /// </summary>
    private LiveTelemetry BuildTelemetryFromCanBuffer()
    {
        var telemetry = new LiveTelemetry
        {
            Timestamp = DateTime.UtcNow
        };

        var parsedFrames = 0;
        var bufferStatus = new List<string>();

        // Parse 0x1DB (SOC, Current, Voltage) - 10ms broadcast
        if (_canFrameBuffer.TryGetValue(LeafCanParser.CAN_ID_LBC_1DB, out var frame1DB))
        {
            var parsed = LeafCanParser.Parse1DB(frame1DB.data);
            if (parsed != null)
            {
                telemetry.Current = parsed.Current;
                telemetry.Voltage = parsed.Voltage;
                telemetry.StateOfCharge = parsed.UsableSocPercent;
                telemetry.Power = LeafCanParser.CalculatePowerKw(parsed.Voltage, parsed.Current);
                parsedFrames++;
                bufferStatus.Add($"1DB(V:{parsed.Voltage:F1}V I:{parsed.Current:F1}A SOC:{parsed.UsableSocPercent}%)");
            }
        }

        // Parse 0x1DC (Power limits) - 10ms broadcast
        if (_canFrameBuffer.TryGetValue(LeafCanParser.CAN_ID_LBC_1DC, out var frame1DC))
        {
            var parsed = LeafCanParser.Parse1DC(frame1DC.data);
            if (parsed != null)
            {
                DischargePowerLimitKw = parsed.DischargePowerLimitKw;
                parsedFrames++;
                bufferStatus.Add($"1DC(Limit:{parsed.DischargePowerLimitKw:F1}kW)");
            }
        }

        // Parse 0x1DA (Motor torque, RPM) - 10ms broadcast
        if (_canFrameBuffer.TryGetValue(LeafCanParser.CAN_ID_INV_1DA, out var frame1DA))
        {
            var parsed = LeafCanParser.Parse1DA(frame1DA.data);
            if (parsed != null)
            {
                MotorRpm = parsed.MotorRpm;
                MotorTorque = parsed.EffectiveTorqueNm;
                telemetry.Speed = LeafCanParser.EstimateSpeedFromRpm(parsed.MotorRpm);

                var motorPower = LeafCanParser.CalculateMotorPowerKw(parsed.EffectiveTorqueNm, parsed.MotorRpm);
                if (Math.Abs(motorPower) > 0.1m)
                {
                    telemetry.Power = motorPower;
                }
                parsedFrames++;
                bufferStatus.Add($"1DA(RPM:{parsed.MotorRpm} Tq:{parsed.EffectiveTorqueNm:F1}Nm)");
            }
        }

        // Parse 0x5BC (GIDs, SOH, Temp) - 100ms broadcast
        if (_canFrameBuffer.TryGetValue(LeafCanParser.CAN_ID_LBC_5BC, out var frame5BC))
        {
            var parsed = LeafCanParser.Parse5BC(frame5BC.data);
            if (parsed != null)
            {
                RemainingGids = parsed.RemainingGids;
                SohPercent = parsed.SohPercent;
                var remainingKwh = LeafCanParser.GidsToKwh(parsed.RemainingGids);
                telemetry.RemainingRange = remainingKwh * 5m;
                parsedFrames++;
                bufferStatus.Add($"5BC(GIDs:{parsed.RemainingGids} SOH:{parsed.SohPercent}%)");
            }
        }

        // Parse 0x55B (Fine SOC) - 100ms broadcast
        if (_canFrameBuffer.TryGetValue(LeafCanParser.CAN_ID_LBC_55B, out var frame55B))
        {
            var parsed = LeafCanParser.Parse55B(frame55B.data);
            if (parsed != null)
            {
                telemetry.StateOfCharge = parsed.FineSocPercent;
                parsedFrames++;
                bufferStatus.Add($"55B(SOC:{parsed.FineSocPercent:F1}%)");
            }
        }

        // Parse 0x54C (Ambient temperature) - 100ms broadcast
        if (_canFrameBuffer.TryGetValue(LeafCanParser.CAN_ID_HVAC_54C, out var frame54C))
        {
            var parsed = LeafCanParser.Parse54C(frame54C.data);
            if (parsed != null)
            {
                telemetry.BatteryTemperature = parsed.AmbientTemperatureCelsius;
                telemetry.HvacActive = parsed.ClimateControlOn || parsed.AcOn;
                parsedFrames++;
                bufferStatus.Add($"54C(Temp:{parsed.AmbientTemperatureCelsius:F1}°C)");
            }
        }

        // Parse 0x5A9 (Range, ECO mode)
        if (_canFrameBuffer.TryGetValue(LeafCanParser.CAN_ID_VCM_5A9, out var frame5A9))
        {
            var parsed = LeafCanParser.Parse5A9(frame5A9.data);
            if (parsed != null)
            {
                EcoModeActive = parsed.EcoModeActive;
                if (parsed.RangeKm.HasValue)
                {
                    telemetry.RemainingRange = parsed.RangeKm.Value;
                }
                parsedFrames++;
                bufferStatus.Add($"5A9(Range:{parsed.RangeKm:F0}km ECO:{parsed.EcoModeActive})");
            }
        }

        // Add GPS data if available
        if (Latitude.HasValue && Longitude.HasValue)
        {
            telemetry.Latitude = Latitude.Value;
            telemetry.Longitude = Longitude.Value;
        }

        // Log telemetry summary every 10 cycles (20 seconds)
        if (TelemetryPointsCollected % 10 == 0 && parsedFrames > 0)
        {
            Log($"[Telemetry] Parsed {parsedFrames} frame types: {string.Join(", ", bufferStatus)}");
        }
        else if (parsedFrames == 0)
        {
            Log($"[Telemetry] WARNING: No CAN frames in buffer! Buffer has {_canFrameBuffer.Count} IDs");
        }

        return telemetry;
    }

    /// <summary>
    /// Flush queued raw CAN frames to database
    /// </summary>
    private async Task FlushRawCanFramesAsync()
    {
        var frames = new List<(DateTime timestamp, int canId, byte[] data)>();

        while (_rawFrameQueue.TryDequeue(out var frame))
        {
            frames.Add(frame);
        }

        if (frames.Count > 0)
        {
            try
            {
                await _testDriveService.RecordRawCanFramesAsync(_sessionGuid, frames);
                _lastFrameFlush = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ATMA] Failed to save raw frames: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Process a single CAN frame line from ATMA output
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "MVVMTK0034:Direct field reference to [ObservableProperty] backing field", Justification = "<Pending>")]
    private void ProcessCanFrameLine(string frameLine)
    {
        var parsed = LeafCanParser.ParseFrameLine(frameLine);
        if (!parsed.HasValue)
        {
            // Log unparseable lines for debugging
            if (!string.IsNullOrWhiteSpace(frameLine) && frameLine.Length > 3)
            {
                Log($"[ATMA] Unparseable frame: '{frameLine}'");
            }
            return;
        }

        var (canId, data) = parsed.Value;
        var timestamp = DateTime.UtcNow;

        // Log every 100th frame to avoid spam, but always log monitored IDs
        var isMonitored = LeafCanParser.MonitoredCanIds.Contains(canId);
        var frameCount = _rawFramesCollected;
        
        if (isMonitored || frameCount % 100 == 0)
        {
            var dataHex = BitConverter.ToString(data).Replace("-", " ");
            Log($"[ATMA] CAN {canId:X3}: {dataHex} {(isMonitored ? "(monitored)" : "")}");
        }

        // Only buffer frames we care about
        if (isMonitored)
        {
            _canFrameBuffer[canId] = (timestamp, data);
        }

        // Queue raw frame for storage
        _rawFrameQueue.Enqueue((timestamp, canId, data));
        Interlocked.Increment(ref _rawFramesCollected);
        
        // Log milestone every 1000 frames
        if (_rawFramesCollected % 1000 == 0)
        {
            Log($"[ATMA] Collected {_rawFramesCollected} total frames, {_canFrameBuffer.Count} unique IDs buffered");
        }
    }

    /// <summary>
    /// Start passive CAN bus monitoring using ATMA command
    /// </summary>
    private async Task StartCanMonitoringAsync(IObdTransport transport)
    {
        _monitorCts = new CancellationTokenSource();
        _isMonitoring = true;
        _canFrameBuffer.Clear();

        try
        {
            // Configure ELM327 for raw CAN monitoring
            await SendObdCommandAsync(transport, "ATH1");     // Headers on (show CAN IDs)
            await SendObdCommandAsync(transport, "ATS0");     // Spaces off (compact format)
            await SendObdCommandAsync(transport, "ATCAF0");   // CAN auto-format off
            await SendObdCommandAsync(transport, "ATL0");     // Linefeeds off
            await SendObdCommandAsync(transport, "ATE0");     // Echo off

            Log("[ATMA] Starting CAN bus monitoring...");

            // Start monitoring all CAN traffic
            await transport.WriteAsync("ATMA\r");

            // Give ATMA a moment to start
            await Task.Delay(100);

            // Background task to read incoming frames
            _ = Task.Run(async () =>
            {
                Log("[ATMA] Monitoring task started");
                var consecutiveTimeouts = 0;
                const int maxConsecutiveTimeouts = 50; // Allow 50 * 200ms = 10 seconds without data

                while (!_monitorCts!.Token.IsCancellationRequested && _isMonitoring)
                {
                    try
                    {
                        // Read lines as they come in (each CAN frame is a line)
                        // Don't pass cancellation token to ReadLineAsync - handle cancellation at loop level
                        var line = await transport.ReadLineAsync(TimeSpan.FromMilliseconds(200));

                        if (string.IsNullOrEmpty(line))
                        {
                            consecutiveTimeouts++;
                            
                            // Only log every 10 timeouts to avoid spam
                            if (consecutiveTimeouts % 10 == 0)
                            {
                                Log($"[ATMA] Waiting for data... ({consecutiveTimeouts} timeouts)");
                            }
                            
                            // If we've had too many consecutive timeouts, something might be wrong
                            if (consecutiveTimeouts >= maxConsecutiveTimeouts)
                            {
                                Log("[ATMA] WARNING: No CAN data received for 10 seconds");
                                // Don't break - keep trying, maybe car is just idling
                            }
                            
                            continue;
                        }

                        // Reset timeout counter when we get data
                        if (consecutiveTimeouts > 0)
                        {
                            Log($"[ATMA] Data received after {consecutiveTimeouts} timeouts");
                            consecutiveTimeouts = 0;
                        }

                        if (line.Contains('>'))
                        {
                            // ATMA was stopped (prompt received)
                            _isMonitoring = false;
                            Log("[ATMA] Monitoring stopped (prompt received)");
                            break;
                        }

                        ProcessCanFrameLine(line.Trim());
                    }
                    catch (TimeoutException)
                    {
                        // Normal - no data available within timeout period
                        consecutiveTimeouts++;
                        continue;
                    }
                    catch (OperationCanceledException) when (_monitorCts?.IsCancellationRequested != true)
                    {
                        // Some transports implement timeouts via TaskCanceledException
                        // (OperationCanceledException). Treat as a normal timeout.
                        consecutiveTimeouts++;
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        Log("[ATMA] Monitoring cancelled by user");
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        Log("[ATMA] Transport disposed");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ATMA] Read error: {ex.Message}");
                        Log($"[ATMA] Read error: {ex.Message}");
                        
                        // Don't exit immediately on error - try to recover
                        await Task.Delay(100);
                    }
                }

                Log("[ATMA] Monitoring task ended");
            }, _monitorCts.Token);

            Log("[ATMA] Background monitoring task launched");
        }
        catch (Exception ex)
        {
            Log($"[ATMA] Failed to start monitoring: {ex.Message}");
            _isMonitoring = false;
            throw;
        }
    }

    /// <summary>
    /// Stop CAN bus monitoring
    /// </summary>
    private async Task StopCanMonitoringAsync(IObdTransport transport)
    {
        _isMonitoring = false;
        _monitorCts?.Cancel();

        try
        {
            // Send any character to stop ATMA (space or newline)
            await transport.WriteAsync(" ");
            await Task.Delay(200);

            // Clear any pending data by draining the buffer
            transport.DrainBuffer();

            // Flush remaining CAN frames to storage
            await FlushRawCanFramesAsync();

            Log("[ATMA] Monitoring stopped, frames flushed");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ATMA] Error stopping monitor: {ex.Message}");
        }
    }

    #endregion ATMA CAN Monitoring

    #region Existing Methods (Updated)

    private static string ExtractHexData(string response)
    {
        // Remove CAN IDs, whitespace, carriage returns
        return response
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace(" ", "")
            .Replace(">", "")
            .Trim();
    }

    private static decimal ParseBatteryTemp(string response)
    {
        try
        {
            var hex = ExtractHexData(response);
            
            if (string.IsNullOrWhiteSpace(hex) || 
                hex.Contains("NODATA") || 
                hex.Contains("ERROR") ||
                hex.Length < 6)
            {
                Debug.WriteLine($"[ParseBatteryTemp] Invalid response: '{response}'");
                return 0;
            }
            
            if (!IsHex(hex.Substring(4, 2)))
            {
                Debug.WriteLine($"[ParseBatteryTemp] Non-hex data: '{hex}'");
                return 0;
            }
            
            var tempByte = Convert.ToByte(hex.Substring(4, 2), 16);
            return tempByte - 40;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ParseBatteryTemp] Error parsing '{response}': {ex.Message}");
            return 0;
        }
    }

    private static decimal ParseCurrent(string response)
    {
        try
        {
            var hex = ExtractHexData(response);
            
            if (string.IsNullOrWhiteSpace(hex) || 
                hex.Contains("NODATA") || 
                hex.Contains("ERROR") ||
                hex.Length < 8)
            {
                Debug.WriteLine($"[ParseCurrent] Invalid response: '{response}'");
                return 0;
            }
            
            if (!IsHex(hex.Substring(4, 4)))
            {
                Debug.WriteLine($"[ParseCurrent] Non-hex data: '{hex}'");
                return 0;
            }
            
            var currentHigh = Convert.ToByte(hex.Substring(4, 2), 16);
            var currentLow = Convert.ToByte(hex.Substring(6, 2), 16);
            var raw = (currentHigh << 8) | currentLow;
            
            if (raw > 32767) raw -= 65536;
            return raw / 10m;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ParseCurrent] Error parsing '{response}': {ex.Message}");
            return 0;
        }
    }

    private static List<string> ParseDtcs(string response)
    {
        var dtcs = new List<string>();
        var hex = ExtractHexData(response);

        if (hex.StartsWith("4300"))
            return dtcs;

        for (int i = 2; i + 3 < hex.Length; i += 4)
        {
            var dtcHex = hex.Substring(i, 4);
            if (dtcHex != "0000")
            {
                dtcs.Add($"DTC: {dtcHex}");
            }
        }

        return dtcs;
    }

    private static decimal ParseSoc(string response)
    {
        try
        {
            var hex = ExtractHexData(response);
            
            // Check for common error responses
            if (string.IsNullOrWhiteSpace(hex) || 
                hex.Contains("NODATA") || 
                hex.Contains("ERROR") ||
                hex.Contains("?") ||
                hex.Length < 6)
            {
                Debug.WriteLine($"[ParseSoc] Invalid response: '{response}'");
                return 0;
            }
            
            // Verify it's actually hex before parsing
            if (!IsHex(hex.Substring(4, 2)))
            {
                Debug.WriteLine($"[ParseSoc] Non-hex data: '{hex}'");
                return 0;
            }
            
            var socByte = Convert.ToByte(hex.Substring(4, 2), 16);
            return socByte / 2m;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ParseSoc] Error parsing '{response}': {ex.Message}");
            return 0;
        }
    }

    private static decimal ParseSpeed(string response)
    {
        var hex = ExtractHexData(response);
        if (hex.Length >= 4)
        {
            var speedByte = Convert.ToByte(hex.Substring(2, 2), 16);
            return speedByte;
        }
        return 0;
    }

    private static decimal ParseVoltage(string response)
    {
        try
        {
            var hex = ExtractHexData(response);
            
            if (string.IsNullOrWhiteSpace(hex) || 
                hex.Contains("NODATA") || 
                hex.Contains("ERROR") ||
                hex.Length < 8)
            {
                Debug.WriteLine($"[ParseVoltage] Invalid response: '{response}'");
                return 0;
            }
            
            if (!IsHex(hex.Substring(4, 4)))
            {
                Debug.WriteLine($"[ParseVoltage] Non-hex data: '{hex}'");
                return 0;
            }
            
            var voltageHigh = Convert.ToByte(hex.Substring(4, 2), 16);
            var voltageLow = Convert.ToByte(hex.Substring(6, 2), 16);
            return ((voltageHigh << 8) | voltageLow) / 100m;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ParseVoltage] Error parsing '{response}': {ex.Message}");
            return 0;
        }
    }

    private async Task<bool> CheckAndRepairConnectionAsync()
    {
        try
        {
            if (_isReconnecting) return false;

            if (_connectedDeviceService.Transport is not IObdTransport transport)
            {
                Debug.WriteLine("[LiveDriveVM] Transport is null");
                return false;
            }

            if (!transport.IsConnected)
            {
                Debug.WriteLine("[LiveDriveVM] Transport is disconnected, attempting reconnect");
                _isReconnecting = true;

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    ConnectionStatus = "🔄 Reconnecting...";
                    ConnectionHealth = "Reconnecting";
                });

                await Task.Delay(1000);

                if (transport.IsConnected)
                {
                    _consecutiveErrors = 0;
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        ConnectionStatus = $"✓ Reconnected to {_connectedDeviceService.DeviceName}";
                        ConnectionHealth = "Good";
                        IsDeviceConnected = true;
                    });
                    Debug.WriteLine("[LiveDriveVM] Reconnection successful");
                    return true;
                }
                else
                {
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        ConnectionStatus = "❌ Connection Lost";
                        ConnectionHealth = "Disconnected";
                        IsDeviceConnected = false;
                    });
                    Debug.WriteLine("[LiveDriveVM] Reconnection failed");
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LiveDriveVM] Connection check error: {ex.Message}");
            return false;
        }
        finally
        {
            _isReconnecting = false;
        }
    }

    /// <summary>
    /// Collect telemetry using UDS requests (for pre/post check only)
    /// </summary>
    private async Task<LiveTelemetry> CollectTelemetrySnapshotAsync(IObdTransport transport)
    {
        var telemetry = new LiveTelemetry
        {
            Timestamp = DateTime.UtcNow
        };

        try
        {
            // Configure for BMS (battery management system)
            await SendObdCommandAsync(transport, $"ATSH{BMS_TXID:X3}");
            await SendObdCommandAsync(transport, $"ATCRA{BMS_RXID:X3}");

            // Read battery SOC (State of Charge) - PID 215C
            Log("[Snapshot] Reading SOC...");
            var socResponse = await SendObdCommandAsync(transport, "215C");
            telemetry.StateOfCharge = ParseSoc(socResponse);
            Log($"[Snapshot] SOC: {telemetry.StateOfCharge}%");

            // Read battery temperature - PID 2161
            Log("[Snapshot] Reading battery temp...");
            var tempResponse = await SendObdCommandAsync(transport, "2161");
            telemetry.BatteryTemperature = ParseBatteryTemp(tempResponse);
            Log($"[Snapshot] Temp: {telemetry.BatteryTemperature}°C");

            // Read battery voltage - PID 2162
            Log("[Snapshot] Reading voltage...");
            var voltageResponse = await SendObdCommandAsync(transport, "2162");
            telemetry.Voltage = ParseVoltage(voltageResponse);
            Log($"[Snapshot] Voltage: {telemetry.Voltage}V");

            // Read battery current - PID 2163
            Log("[Snapshot] Reading current...");
            var currentResponse = await SendObdCommandAsync(transport, "2163");
            telemetry.Current = ParseCurrent(currentResponse);
            Log($"[Snapshot] Current: {telemetry.Current}A");

            // Calculate power (kW)
            telemetry.Power = (telemetry.Voltage * telemetry.Current) / 1000m;

            // Add GPS data if available
            if (Latitude.HasValue && Longitude.HasValue)
            {
                telemetry.Latitude = Latitude.Value;
                telemetry.Longitude = Longitude.Value;
            }

            // Check if we got any valid data
            if (telemetry.StateOfCharge == 0 && telemetry.Voltage == 0 && telemetry.Current == 0)
            {
                Log("[Snapshot] WARNING: All values are zero - UDS queries may not be supported or adapter not responding correctly");
                throw new InvalidOperationException(
                    "Could not read any valid data from vehicle. " +
                    "The OBD adapter may not support these Nissan Leaf PIDs, or the vehicle is not responding. " +
                    "\n\nNote: During the actual drive, data will be collected from CAN bus broadcast frames which should work correctly.");
            }

            _consecutiveErrors = 0;
        }
        catch (Exception ex)
        {
            _consecutiveErrors++;
            Debug.WriteLine($"[LiveDriveVM] Error collecting telemetry (attempt {_consecutiveErrors}): {ex.Message}");
            throw;
        }

        return telemetry;
    }

    private async Task InitializeGpsAsync()
    {
        try
        {
            var request = new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(10));
            var location = await Geolocation.Default.GetLocationAsync(request);

            if (location is not null)
            {
                Latitude = location.Latitude;
                Longitude = location.Longitude;
                GpsStatus = "✓ GPS Active";

                _ = StartGpsUpdatesAsync();
            }
            else
            {
                GpsStatus = "⚠️ GPS Unavailable";
            }
        }
        catch (Exception ex)
        {
            GpsStatus = $"❌ GPS Error: {ex.Message}";
            Debug.WriteLine($"[LiveDriveVM] GPS initialization error: {ex.Message}");
        }
    }

    private async Task LoadSessionAsync(Guid sessionGuid)
    {
        await ExecuteBusyAsync(async () =>
        {
            try
            {
                var session = await _testDriveService.GetSessionAsync(sessionGuid);
                if (session is null)
                {
                    SetError("Session not found");
                    return;
                }

                IsDeviceConnected = _connectedDeviceService.IsConnected;
                if (!IsDeviceConnected)
                {
                    ConnectionStatus = "⚠️ Device Disconnected";
                    SetError("OBD device is not connected. Please reconnect on the Setup page.");
                    return;
                }

                ConnectionStatus = $"✓ Connected to {_connectedDeviceService.DeviceName}";
                ConnectionHealth = "Good";

                _connectionCheckTimer.Start();
                _ = InitializeGpsAsync();

                CurrentPhase = TestDrivePhase.PreCheck;
                await PerformPreCheckAsync();
            }
            catch (Exception ex)
            {
                SetError($"Failed to load session: {ex.Message}");
            }
        });
    }

    [RelayCommand]
    private async Task MarkEventAsync()
    {
        if (!CanMarkEvent) return;

        var eventType = await Shell.Current.DisplayPromptAsync(
            "Mark Event",
            "Enter event description (e.g., 'Hard Acceleration', 'Highway Merge'):",
            placeholder: "Event description",
            maxLength: 100);

        if (string.IsNullOrWhiteSpace(eventType))
            return;

        try
        {
            await _testDriveService.MarkEventAsync(_sessionGuid, eventType);

            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            RecentEvents.Insert(0, $"[{timestamp}] {eventType}");

            // Keep only last 10 events
            while (RecentEvents.Count > 10)
            {
                RecentEvents.RemoveAt(RecentEvents.Count - 1);
            }

            await Shell.Current.DisplayAlertAsync("Event Marked", $"Event '{eventType}' recorded at {timestamp}", "OK");
        }
        catch (Exception ex)
        {
            SetError($"Failed to mark event: {ex.Message}");
        }
    }

    private async void OnConnectionCheckTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_disposed || CurrentPhase != TestDrivePhase.Driving)
            return;

        try
        {
            // Check if we're still receiving CAN frames
            var hasRecentFrames = _canFrameBuffer.Values.Any(f =>
                DateTime.UtcNow - f.timestamp < TimeSpan.FromSeconds(3));

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (_isMonitoring && hasRecentFrames)
                {
                    ConnectionHealth = "Good (ATMA)";
                    _consecutiveErrors = 0;
                }
                else if (_isMonitoring)
                {
                    ConnectionHealth = "No data received";
                    _consecutiveErrors++;
                }
                else
                {
                    ConnectionHealth = "Monitor stopped";
                    _consecutiveErrors++;
                }
            });

            // Periodically flush raw frames
            if (DateTime.UtcNow - _lastFrameFlush > TimeSpan.FromSeconds(CanFrameFlushIntervalMs / 1000))
            {
                await FlushRawCanFramesAsync();
            }

            // If too many errors, alert user
            if (_consecutiveErrors >= MaxRetries)
            {
                _telemetryTimer.Stop();

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var shouldContinue = await Shell.Current.DisplayAlertAsync(
                        "Connection Issues",
                        $"Having trouble receiving CAN data ({_consecutiveErrors} failed checks).\n\n" +
                        "Would you like to:\n" +
                        "• Continue (may have data gaps)\n" +
                        "• Stop drive and save data collected so far",
                        "Continue",
                        "Stop Drive");

                    if (shouldContinue)
                    {
                        _consecutiveErrors = 0;
                        _telemetryTimer.Start();
                    }
                    else
                    {
                        await StopDriveAsync();
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LiveDriveVM] Connection check error: {ex.Message}");
        }
    }

    partial void OnCurrentPhaseChanged(TestDrivePhase value)
    {
        UpdatePhaseUI(value);
    }

    partial void OnSessionIdChanged(string? value)
    {
        if (Guid.TryParse(value, out var sessionGuid))
        {
            _sessionGuid = sessionGuid;
            _ = LoadSessionAsync(sessionGuid);
        }
    }

    /// <summary>
    /// Timer callback - build telemetry from CAN buffer and update UI
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "MVVMTK0034:Direct field reference to [ObservableProperty] backing field", Justification = "<Pending>")]
    private async void OnTelemetryTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (_telemetryCts?.IsCancellationRequested == true || _disposed)
            return;

        try
        {
            // Build telemetry from buffered CAN frames (no blocking OBD requests!)
            var telemetry = BuildTelemetryFromCanBuffer();

            // Update UI on main thread
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                StateOfCharge = telemetry.StateOfCharge;
                BatteryTemperature = telemetry.BatteryTemperature;
                Voltage = telemetry.Voltage;
                Current = telemetry.Current;
                Power = telemetry.Power;
                Speed = telemetry.Speed;
                RemainingRange = telemetry.RemainingRange;
                MinCellVoltage = telemetry.MinCellVoltage;
                MaxCellVoltage = telemetry.MaxCellVoltage;
                CellVoltageVariance = telemetry.GetCellVoltageVariance();

                TelemetryPointsCollected++;
                RawFramesCollected = _rawFramesCollected;
            });

            // Record parsed telemetry to database
            await _testDriveService.RecordTelemetryAsync(_sessionGuid, telemetry);

            // Periodically flush raw frames
            if (DateTime.UtcNow - _lastFrameFlush > TimeSpan.FromSeconds(5))
            {
                await FlushRawCanFramesAsync();
            }

            _consecutiveErrors = 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LiveDriveVM] Telemetry processing error: {ex.Message}");
            _consecutiveErrors++;
        }
    }

    private async Task PerformPostCheckAsync()
    {
        ShowPreCheckUI = false;
        ShowDrivingUI = false;
        ShowPostCheckUI = true;
        PhaseDescription = "Performing Post-Drive Diagnostics";
        PostCheckStatus = "Collecting final diagnostics...";

        _connectionCheckTimer.Stop();

        try
        {
            // Flush any remaining CAN frames
            await FlushRawCanFramesAsync();

            if (!await CheckAndRepairConnectionAsync())
            {
                PostCheckStatus = "❌ Cannot connect to OBD device";
                SetError("Connection lost. Saving data collected during drive...");
                await Shell.Current.GoToAsync($"{ReportRoute}?sessionId={_sessionGuid}");
                return;
            }

            if (_connectedDeviceService.Transport is not IObdTransport transport)
            {
                PostCheckStatus = "❌ OBD transport not available";
                return;
            }

            // Configure for UDS queries (BMS)
            await SendObdCommandAsync(transport, $"ATSH{BMS_TXID:X3}");
            await SendObdCommandAsync(transport, $"ATCRA{BMS_RXID:X3}");

            PostCheckStatus = "Reading diagnostic trouble codes...";
            var dtcResponse = await SendObdCommandAsync(transport, "03");
            var dtcs = ParseDtcs(dtcResponse);

            PostCheckStatus = "Collecting final telemetry...";
            var snapshot = await CollectTelemetrySnapshotAsync(transport);

            await _testDriveService.SavePostDriveSnapshotAsync(_sessionGuid, snapshot, dtcs);

            PostCheckStatus = $"✓ Post-check complete. Found {dtcs.Count} DTC(s).";

            await Shell.Current.DisplayAlertAsync(
                "Post-Check Complete",
                $"Drive ended. Collected {TelemetryPointsCollected} telemetry points.\n" +
                $"Raw CAN frames: {RawFramesCollected}\n\n" +
                $"Final DTCs: {dtcs.Count}\n" +
                $"Battery SOC: {snapshot.StateOfCharge:F1}%\n\n" +
                $"Proceeding to analysis...",
                "OK");

            await Shell.Current.GoToAsync($"{ReportRoute}?sessionId={_sessionGuid}");
        }
        catch (ObjectDisposedException ex)
        {
            PostCheckStatus = $"❌ Connection lost during post-check";
            Debug.WriteLine($"[LiveDriveVM] Post-check disposed error: {ex.Message}");

            await Shell.Current.DisplayAlertAsync(
                "Connection Lost",
                "Lost connection to OBD device during post-check. Proceeding with data collected during drive.",
                "OK");

            await Shell.Current.GoToAsync($"{ReportRoute}?sessionId={_sessionGuid}");
        }
        catch (Exception ex)
        {
            PostCheckStatus = $"❌ Post-check failed: {ex.Message}";
            SetError($"Post-check failed: {ex.Message}");

            var proceed = await Shell.Current.DisplayAlertAsync(
                "Post-Check Failed",
                $"Failed to complete post-drive diagnostics: {ex.Message}\n\nWould you like to proceed with the data collected during the drive?",
                "Proceed",
                "Cancel");

            if (proceed)
            {
                await Shell.Current.GoToAsync($"{ReportRoute}?sessionId={_sessionGuid}");
            }
        }
    }

    private async Task PerformPreCheckAsync()
    {
        ShowPreCheckUI = true;
        ShowDrivingUI = false;
        ShowPostCheckUI = false;
        PhaseDescription = "Performing Pre-Drive Diagnostics";
        PreCheckStatus = "Checking connection...";

        try
        {
            if (!await CheckAndRepairConnectionAsync())
            {
                PreCheckStatus = "❌ Cannot connect to OBD device";
                SetError("Connection lost. Please check your OBD adapter.");
                return;
            }

            if (_connectedDeviceService.Transport is not IObdTransport transport)
            {
                PreCheckStatus = "❌ OBD transport not available";
                return;
            }

            PreCheckStatus = "Verifying OBD adapter compatibility...";

            // Try basic AT commands to verify adapter is responding
            try
            {
                var versionResponse = await SendObdCommandAsync(transport, "ATI", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2));
                Log($"[PreCheck] Adapter info: {versionResponse}");

                // Try to read protocol
                var protocolResponse = await SendObdCommandAsync(transport, "ATDPN", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2));
                Log($"[PreCheck] Protocol: {protocolResponse}");
            }
            catch (Exception ex)
            {
                Log($"[PreCheck] Basic commands failed: {ex.Message}");
            }

            // Try to collect a snapshot using UDS (optional - don't fail if it doesn't work)
            PreCheckStatus = "Attempting to read baseline data...";
            LiveTelemetry? snapshot = null;
            List<string> dtcs = new();

            try
            {
                // Configure for BMS communication (UDS queries for pre-check)
                await SendObdCommandAsync(transport, $"ATSH{BMS_TXID:X3}");
                await SendObdCommandAsync(transport, $"ATCRA{BMS_RXID:X3}");
                await SendObdCommandAsync(transport, $"ATFCSH{BMS_TXID:X3}");
                await SendObdCommandAsync(transport, "ATFCSD300000");
                await SendObdCommandAsync(transport, "ATFCSM1");

                PreCheckStatus = "Reading diagnostic trouble codes...";
                var dtcResponse = await SendObdCommandAsync(transport, "03", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3));
                dtcs = ParseDtcs(dtcResponse);
                Log($"[PreCheck] DTCs found: {dtcs.Count}");

                PreCheckStatus = "Collecting baseline telemetry...";
                snapshot = await CollectTelemetrySnapshotAsync(transport);

                if (snapshot != null)
                {
                    await _testDriveService.SavePreDriveSnapshotAsync(_sessionGuid, snapshot, dtcs);
                    PreCheckStatus = $"✓ Pre-check complete. Found {dtcs.Count} DTC(s).";
                }
            }
            catch (Exception ex)
            {
                Log($"[PreCheck] UDS snapshot failed (this is OK - will use CAN monitoring during drive): {ex.Message}");
                PreCheckStatus = "⚠️ Could not read UDS data (will use CAN monitoring during drive)";
                
                // Save empty snapshot so we can proceed
                snapshot = new LiveTelemetry { Timestamp = DateTime.UtcNow };
                await _testDriveService.SavePreDriveSnapshotAsync(_sessionGuid, snapshot, dtcs);
            }

            CanStartDrive = true;

            var message = snapshot != null && snapshot.StateOfCharge > 0
                ? $"Baseline diagnostics collected.\n\n" +
                  $"DTCs: {dtcs.Count}\n" +
                  $"Battery SOC: {snapshot.StateOfCharge:F1}%\n" +
                  $"Battery Temp: {snapshot.BatteryTemperature:F1}°C\n\n" +
                  $"Ready to start test drive.\n\n" +
                  $"During the drive, CAN bus data will be passively monitored for maximum data capture."
                : $"Connection verified.\n\n" +
                  $"Note: Could not read baseline data via UDS commands.\n" +
                  $"This is normal for some adapters.\n\n" +
                  $"During the drive, CAN bus broadcast monitoring (ATMA) will collect all data.\n\n" +
                  $"Ready to start test drive.";

            await Shell.Current.DisplayAlertAsync("Pre-Check Complete", message, "OK");
        }
        catch (Exception ex)
        {
            PreCheckStatus = $"❌ Pre-check failed: {ex.Message}";
            SetError($"Pre-check failed: {ex.Message}");
        }
    }

    private async Task<string> SendObdCommandAsync(IObdTransport transport, string cmd, TimeSpan? initialWait = null, TimeSpan? collectTime = null)
    {
        initialWait ??= TimeSpan.FromMilliseconds(300);
        collectTime ??= TimeSpan.FromSeconds(3);

        Log($"TX: {cmd}");

        if (!transport.IsConnected)
        {
            throw new InvalidOperationException("Transport disconnected");
        }

        await transport.WriteAsync(cmd + "\r");
        await Task.Delay(initialWait.Value);

        var response = new StringBuilder();
        var endTime = DateTime.UtcNow + collectTime;

        while (DateTime.UtcNow < endTime)
        {
            try
            {
                var chunk = await transport.ReadUntilAsync(">", TimeSpan.FromMilliseconds(200));
                response.Append(chunk);

                if (chunk.Contains('>'))
                    break;
            }
            catch (TimeoutException)
            {
                await Task.Delay(100);
            }
            catch (ObjectDisposedException)
            {
                throw new InvalidOperationException("Transport was disposed during communication");
            }
        }

        var result = response.ToString()
            .Replace(cmd, "")
            .Replace(">", "")
            .Trim();

        Log($"RX: {result.Replace("\r", "\\r").Replace("\n", "\\n")}");
        return result;
    }

    [RelayCommand]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CommunityToolkit.Mvvm.SourceGenerators.ObservablePropertyGenerator", "MVVMTK0034:Direct field reference to [ObservableProperty] backing field", Justification = "<Pending>")]
    private async Task StartDriveAsync()
    {
        if (!CanStartDrive) return;

        await ExecuteBusyAsync(async () =>
        {
            try
            {
                await _testDriveService.StartDriveAsync(_sessionGuid);

                CurrentPhase = TestDrivePhase.Driving;
                ShowPreCheckUI = false;
                ShowDrivingUI = true;
                ShowPostCheckUI = false;
                PhaseDescription = "Drive In Progress - Monitoring CAN Bus";

                CanStartDrive = false;
                CanStopDrive = true;
                CanMarkEvent = true;

                // Reset counters
                _rawFramesCollected = 0;
                RawFramesCollected = 0;
                _consecutiveErrors = 0;

                // Start ATMA CAN monitoring
                if (_connectedDeviceService.Transport is IObdTransport transport)
                {
                    await StartCanMonitoringAsync(transport);
                }

                // Start telemetry timer (processes buffered CAN data)
                _telemetryCts = new CancellationTokenSource();
                _telemetryTimer.Start();

                await Shell.Current.DisplayAlertAsync(
                    "Drive Started",
                    "CAN bus monitoring is now active.\n\n" +
                    "• Real-time data from broadcast frames\n" +
                    "• All raw frames stored for analysis\n" +
                    "• UI updates every 2 seconds\n\n" +
                    "Drive normally!",
                    "OK");
            }
            catch (Exception ex)
            {
                SetError($"Failed to start drive: {ex.Message}");
            }
        });
    }

    private async Task StartGpsUpdatesAsync()
    {
        try
        {
            while (CurrentPhase == TestDrivePhase.Driving && !_disposed)
            {
                var request = new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(10));
                var location = await Geolocation.Default.GetLocationAsync(request);

                if (location is not null)
                {
                    Latitude = location.Latitude;
                    Longitude = location.Longitude;
                }

                await Task.Delay(5000);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LiveDriveVM] GPS update error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StopDriveAsync()
    {
        if (!CanStopDrive) return;

        await ExecuteBusyAsync(async () =>
        {
            try
            {
                // Stop telemetry collection
                _telemetryTimer.Stop();
                _telemetryCts?.Cancel();
                _telemetryCts?.Dispose();
                _telemetryCts = null;

                // Stop CAN monitoring
                if (_connectedDeviceService.Transport is IObdTransport transport)
                {
                    await StopCanMonitoringAsync(transport);
                }

                await _testDriveService.EndDriveAsync(_sessionGuid);

                CurrentPhase = TestDrivePhase.PostCheck;
                CanStopDrive = false;
                CanMarkEvent = false;

                await PerformPostCheckAsync();
            }
            catch (Exception ex)
            {
                SetError($"Failed to stop drive: {ex.Message}");
            }
        });
    }

    private void UpdatePhaseUI(TestDrivePhase phase)
    {
        ShowPreCheckUI = phase == TestDrivePhase.PreCheck;
        ShowDrivingUI = phase == TestDrivePhase.Driving;
        ShowPostCheckUI = phase == TestDrivePhase.PostCheck;

        PhaseDescription = phase switch
        {
            TestDrivePhase.PreCheck => "Pre-Drive Diagnostics",
            TestDrivePhase.Driving => "Drive In Progress",
            TestDrivePhase.PostCheck => "Post-Drive Diagnostics",
            _ => "Unknown Phase"
        };
    }

    /// <summary>
    /// Check if a string contains only hexadecimal characters
    /// </summary>
    private static bool IsHex(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        
        foreach (char c in value)
        {
            if (!((c >= '0' && c <= '9') || 
                  (c >= 'A' && c <= 'F') || 
                  (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }
        return true;
    }
    #endregion Existing Methods (Updated)
}