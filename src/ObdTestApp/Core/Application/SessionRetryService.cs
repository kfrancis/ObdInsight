using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ObdTestApp.Core.Communication.Bluetooth;
using Serilog;
using Spectre.Console;

namespace ObdTestApp.Core.Application;

/// <summary>
/// Manages retry logic for ELM327 sessions
/// </summary>
public class SessionRetryService
{
    private const int MaxFailures = 5;

    /// <summary>
    /// Runs an ELM327 session with automatic retry on connection failure
    /// </summary>
    public async Task RunWithRetryAsync(
        BleDeviceInfo selectedDevice,
        DevicePreferences preferences,
        Func<BleDeviceInfo, CancellationToken, Task> sessionFunc,
        CancellationToken ct)
    {
        var failureCount = 0;
        var currentDevice = selectedDevice;

        while (!ct.IsCancellationRequested && failureCount < MaxFailures)
        {
            try
            {
                await sessionFunc(currentDevice, ct);

                // If we get here, session ended normally
                break;
            }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                failureCount++;
                Log.Warning(ex, "Connection failure #{FailureCount}/{MaxFailures} - {Message}", failureCount, MaxFailures, ex.Message);
                AnsiConsole.MarkupLine($"[red]Connection failure #{failureCount}/{MaxFailures}:[/] {ex.Message.EscapeMarkup()}");

                if (failureCount < MaxFailures)
                {
                    var retryDelay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failureCount)));
                    Log.Information("Retrying in {RetryDelay} seconds (attempt {NextAttempt})", retryDelay.TotalSeconds, failureCount + 1);
                    AnsiConsole.MarkupLine($"[yellow]Retrying in {retryDelay.TotalSeconds:F0}s...[/]");

                    await Task.Delay(retryDelay, ct);
                    Log.Information("Starting retry attempt {Attempt}", failureCount + 1);
                    AnsiConsole.MarkupLine($"[cyan]Retry attempt {failureCount + 1}...[/]");
                }
                else
                {
                    Log.Error("Max retry attempts ({MaxFailures}) reached. Prompting for rescan.", MaxFailures);
                    AnsiConsole.MarkupLine($"[red]Max retry attempts ({MaxFailures}) reached. Giving up.[/]");

                    // Ask if user wants to rescan
                    if (AnsiConsole.Confirm("Scan for devices again?", defaultValue: true))
                    {
                        Log.Information("User requested rescan");
                        var scanService = new DeviceScanService(TimeSpan.FromSeconds(10));
                        var newDevice = await scanService.ScanAndSelectDeviceAsync(preferences, ct);
                        if (newDevice != null)
                        {
                            Log.Information("New device selected: {DeviceName} ({Address})", newDevice.Name, newDevice.Address);
                            currentDevice = newDevice;
                            failureCount = 0; // Reset counter for new device
                            continue;
                        }
                    }
                    else
                    {
                        Log.Information("User declined rescan");
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Error(ex, "Unexpected error during session: {Message}", ex.Message);
                AnsiConsole.MarkupLine($"[red]Unexpected error:[/] {ex.Message.EscapeMarkup()}");
                throw;
            }
        }
    }
}
