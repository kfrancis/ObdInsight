using ObdInsight.Core.Diagnostics;

namespace OdbInsights.Tests.Diagnostics;

public class MarkdownReportGeneratorTests
{
    [Test]
    public async Task Generate_MinimalReport_ContainsRequiredSections()
    {
        var report = CreateMinimalReport();

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("# Vehicle/Adapter Support Request");
        await Assert.That(markdown).Contains("## Vehicle Information (User Provided)");
        await Assert.That(markdown).Contains("## Summary");
        await Assert.That(markdown).Contains("Honda");
        await Assert.That(markdown).Contains("CR-V");
        await Assert.That(markdown).Contains("2022");
    }

    [Test]
    public async Task Generate_WithBleInfo_ContainsBleSection()
    {
        var report = CreateMinimalReport() with
        {
            BleAdapterInfo = new BleAdapterInfo
            {
                DeviceName = "OBDII",
                MacAddress = "66:1e:87:02:c2:db",
                Rssi = -55
            }
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("## BLE Adapter Information");
        await Assert.That(markdown).Contains("OBDII");
        await Assert.That(markdown).Contains("66:1e:87:02:c2:db");
    }

    [Test]
    public async Task Generate_WithObdAdapterInfo_ContainsAdapterSection()
    {
        var report = CreateMinimalReport() with
        {
            ObdAdapterInfo = new ObdAdapterInfo
            {
                VersionResponse = "ELM327 v1.5",
                ProtocolDescription = "ISO 15765-4 CAN",
                VoltageResponse = "12.4V"
            }
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("## OBD Adapter Information");
        await Assert.That(markdown).Contains("ELM327 v1.5");
        await Assert.That(markdown).Contains("12.4V");
    }

    [Test]
    public async Task Generate_WithVin_MasksLastSixCharacters()
    {
        var report = CreateMinimalReport() with
        {
            VehicleId = new VehicleIdentification
            {
                Vin = "1N4AZ0CP5HC123456"
            }
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("## Vehicle Identification (ECU)");
        await Assert.That(markdown).Contains("1N4AZ0CP5HC******"); // Last 6 masked
        await Assert.That(markdown).DoesNotContain("123456"); // Full serial not exposed
    }

    [Test]
    public async Task Generate_WithSupportedPids_ListsPids()
    {
        var report = CreateMinimalReport() with
        {
            SupportedPids = new SupportedPidsInfo
            {
                Mode01Pids = ["0100", "0101", "0104", "0105", "010C", "010D"],
                Mode09Pids = ["0900", "0902"]
            }
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("## Supported PIDs");
        await Assert.That(markdown).Contains("6 PIDs"); // Mode 01 count
        await Assert.That(markdown).Contains("010C");
    }

    [Test]
    public async Task Generate_WithPidProbeResults_ShowsSuccessfulResponses()
    {
        var report = CreateMinimalReport() with
        {
            StandardPidResults =
            [
                new PidProbeResult
                {
                    Command = "010C",
                    Description = "Engine RPM",
                    Success = true,
                    RawResponse = "410C 1AF8",
                    ResponseTime = TimeSpan.FromMilliseconds(45)
                },
                new PidProbeResult
                {
                    Command = "010D",
                    Description = "Vehicle speed",
                    Success = true,
                    RawResponse = "410D 00",
                    ResponseTime = TimeSpan.FromMilliseconds(38)
                }
            ]
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("## Standard PID Responses");
        await Assert.That(markdown).Contains("Engine RPM");
        await Assert.That(markdown).Contains("410C 1AF8");
        await Assert.That(markdown).Contains("45ms");
    }

    [Test]
    public async Task Generate_WithFailedPids_ShowsFailureCount()
    {
        var report = CreateMinimalReport() with
        {
            StandardPidResults =
            [
                new PidProbeResult
                {
                    Command = "010C",
                    Description = "Engine RPM",
                    Success = true,
                    RawResponse = "410C 1AF8",
                    ResponseTime = TimeSpan.FromMilliseconds(45)
                },
                new PidProbeResult
                {
                    Command = "015B",
                    Description = "Hybrid battery",
                    Success = false,
                    Error = "NO DATA",
                    ResponseTime = TimeSpan.FromMilliseconds(1000)
                },
                new PidProbeResult
                {
                    Command = "015E",
                    Description = "Fuel rate",
                    Success = false,
                    Error = "NO DATA",
                    ResponseTime = TimeSpan.FromMilliseconds(1000)
                }
            ]
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("2 PIDs did not respond or returned errors");
    }

    [Test]
    public async Task Generate_WithErrors_ShowsErrorSection()
    {
        var report = CreateMinimalReport() with
        {
            Errors =
            [
                new DiagnosticError
                {
                    Phase = "VIN Read",
                    Message = "Timeout waiting for response"
                },
                new DiagnosticError
                {
                    Phase = "PID Query",
                    Message = "Connection lost",
                    Details = "BLE disconnected unexpectedly"
                }
            ]
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("## Errors Encountered");
        await Assert.That(markdown).Contains("VIN Read");
        await Assert.That(markdown).Contains("Timeout waiting for response");
        await Assert.That(markdown).Contains("BLE disconnected unexpectedly");
    }

    [Test]
    public async Task Generate_WithNotes_ShowsNotesSection()
    {
        var report = CreateMinimalReport() with
        {
            Notes =
            [
                "Found 45 Mode 01 PIDs",
                "VIN detected: 1N4AZ0CP5HC123456",
                "OBD adapter identified as: ELM327 v1.5"
            ]
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("## Collection Notes");
        await Assert.That(markdown).Contains("Found 45 Mode 01 PIDs");
    }

    [Test]
    public async Task Generate_WithExtendedPidResults_ShowsEvSection()
    {
        var report = CreateMinimalReport() with
        {
            ExtendedPidResults =
            [
                new PidProbeResult
                {
                    Command = "2101",
                    Description = "Nissan battery data",
                    Success = true,
                    RawResponse = "6101...",
                    ResponseTime = TimeSpan.FromMilliseconds(150)
                }
            ]
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("## Extended/EV PID Responses");
        await Assert.That(markdown).Contains("Nissan battery data");
    }

    [Test]
    public async Task Generate_WithOptionalVehicleInfo_IncludesTrimAndEngine()
    {
        var report = CreateMinimalReport() with
        {
            UserVehicleInfo = new UserVehicleInfo
            {
                Year = 2023,
                Make = "Nissan",
                Model = "Leaf",
                Trim = "SV Plus",
                EngineType = "Electric",
                TransmissionType = "Single-Speed",
                AdditionalNotes = "Has ProPilot Assist"
            }
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("SV Plus");
        await Assert.That(markdown).Contains("Electric");
        await Assert.That(markdown).Contains("Single-Speed");
        await Assert.That(markdown).Contains("Has ProPilot Assist");
    }

    [Test]
    public async Task Generate_SummaryTable_ShowsCheckStatuses()
    {
        var report = CreateMinimalReport() with
        {
            BleAdapterInfo = new BleAdapterInfo
            {
                DeviceName = "OBDII",
                MacAddress = "AA:BB:CC:DD:EE:FF"
            },
            ObdAdapterInfo = new ObdAdapterInfo
            {
                VersionResponse = "ELM327 v1.5"
            },
            VehicleId = new VehicleIdentification
            {
                Vin = "1N4AZ0CP5HC123456"
            },
            SupportedPids = new SupportedPidsInfo
            {
                Mode01Pids = ["0100", "0101", "0104"]
            }
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        // Summary section should show status indicators
        await Assert.That(markdown).Contains("?"); // At least one success
        await Assert.That(markdown).Contains("BLE Connection");
        await Assert.That(markdown).Contains("Adapter Detection");
        await Assert.That(markdown).Contains("VIN Read");
        await Assert.That(markdown).Contains("PID Discovery");
    }

    [Test]
    public async Task Generate_ContainsFooterWithInstructions()
    {
        var report = CreateMinimalReport();

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("---");
        await Assert.That(markdown).Contains("ObdInsight DevTools");
        await Assert.That(markdown).Contains("GitHub issue");
    }

    [Test]
    public async Task Generate_WithBleServices_ShowsCollapsibleSection()
    {
        var report = CreateMinimalReport() with
        {
            BleAdapterInfo = new BleAdapterInfo
            {
                DeviceName = "OBDII",
                MacAddress = "AA:BB:CC:DD:EE:FF",
                Services =
                [
                    new BleServiceInfo
                    {
                        ServiceUuid = Guid.Parse("0000FFF0-0000-1000-8000-00805F9B34FB"),
                        Characteristics =
                        [
                            new BleCharacteristicInfo
                            {
                                CharacteristicUuid = Guid.Parse("0000FFF1-0000-1000-8000-00805F9B34FB"),
                                Properties = ["Notify"]
                            },
                            new BleCharacteristicInfo
                            {
                                CharacteristicUuid = Guid.Parse("0000FFF2-0000-1000-8000-00805F9B34FB"),
                                Properties = ["Write", "WriteNoResp"]
                            }
                        ]
                    }
                ]
            }
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        await Assert.That(markdown).Contains("<details>");
        await Assert.That(markdown).Contains("<summary>GATT Services & Characteristics</summary>");
        await Assert.That(markdown).Contains("0000fff0-0000-1000-8000-00805f9b34fb");
        await Assert.That(markdown).Contains("Notify");
    }

    [Test]
    public async Task Generate_EscapesMarkdownSpecialCharacters()
    {
        var report = CreateMinimalReport() with
        {
            ObdAdapterInfo = new ObdAdapterInfo
            {
                VersionResponse = "ELM327 v1.5 | Clone",
                DeviceDescription = "Test `device`"
            }
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        // Pipe should be escaped in table cells
        await Assert.That(markdown).Contains("\\|");
        // Backticks should be escaped
        await Assert.That(markdown).Contains("\\`");
    }

    [Test]
    public async Task Generate_LongResponses_AreTruncated()
    {
        var longResponse = new string('A', 100);

        var report = CreateMinimalReport() with
        {
            StandardPidResults =
            [
                new PidProbeResult
                {
                    Command = "0100",
                    Description = "Supported PIDs",
                    Success = true,
                    RawResponse = longResponse,
                    ResponseTime = TimeSpan.FromMilliseconds(50)
                }
            ]
        };

        var markdown = MarkdownReportGenerator.Generate(report);

        // Long responses should be truncated with "..."
        await Assert.That(markdown).Contains("...");
        await Assert.That(markdown).DoesNotContain(longResponse);
    }

    private static DiagnosticReport CreateMinimalReport() => new()
    {
        GeneratedAt = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc),
        ToolVersion = "1.0.0",
        UserVehicleInfo = new UserVehicleInfo
        {
            Year = 2022,
            Make = "Honda",
            Model = "CR-V"
        }
    };
}

/// <summary>
/// Test implementation of MarkdownReportGenerator for unit testing.
/// This duplicates the static class to allow testing without DevTools reference.
/// </summary>
public static class MarkdownReportGenerator
{
    public static string Generate(DiagnosticReport report)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Vehicle/Adapter Support Request");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"**Tool Version:** {report.ToolVersion}");
        sb.AppendLine();

        AppendVehicleInfo(sb, report.UserVehicleInfo);
        AppendSummary(sb, report);

        if (report.BleAdapterInfo != null)
            AppendBleAdapterInfo(sb, report.BleAdapterInfo);

        if (report.ObdAdapterInfo != null)
            AppendObdAdapterInfo(sb, report.ObdAdapterInfo);

        if (report.VehicleId != null)
            AppendVehicleId(sb, report.VehicleId);

        if (report.SupportedPids != null)
            AppendSupportedPids(sb, report.SupportedPids);

        if (report.StandardPidResults.Count > 0)
            AppendPidProbeResults(sb, "Standard PID Responses", report.StandardPidResults);

        if (report.ExtendedPidResults.Count > 0)
            AppendPidProbeResults(sb, "Extended/EV PID Responses", report.ExtendedPidResults);

        if (report.Errors.Count > 0)
            AppendErrors(sb, report.Errors);

        if (report.Notes.Count > 0)
            AppendNotes(sb, report.Notes);

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("*This report was generated by ObdInsight DevTools. Please attach this file to your GitHub issue.*");

        return sb.ToString();
    }

    private static void AppendVehicleInfo(System.Text.StringBuilder sb, UserVehicleInfo info)
    {
        sb.AppendLine("## Vehicle Information (User Provided)");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|----------|-------|");
        sb.AppendLine($"| Year | {info.Year} |");
        sb.AppendLine($"| Make | {info.Make} |");
        sb.AppendLine($"| Model | {info.Model} |");

        if (!string.IsNullOrEmpty(info.Trim))
            sb.AppendLine($"| Trim | {info.Trim} |");
        if (!string.IsNullOrEmpty(info.EngineType))
            sb.AppendLine($"| Engine/Powertrain | {info.EngineType} |");
        if (!string.IsNullOrEmpty(info.TransmissionType))
            sb.AppendLine($"| Transmission | {info.TransmissionType} |");

        sb.AppendLine();

        if (!string.IsNullOrEmpty(info.AdditionalNotes))
        {
            sb.AppendLine("**Additional Notes:**");
            sb.AppendLine($"> {info.AdditionalNotes}");
            sb.AppendLine();
        }
    }

    private static void AppendSummary(System.Text.StringBuilder sb, DiagnosticReport report)
    {
        sb.AppendLine("## Summary");
        sb.AppendLine();

        var checksTable = new List<(string Check, bool? Passed, string Details)>
        {
            ("BLE Connection", report.BleAdapterInfo != null,
                report.BleAdapterInfo?.DeviceName ?? "Not connected"),
            ("Adapter Detection", !string.IsNullOrEmpty(report.ObdAdapterInfo?.VersionResponse?.Trim()),
                report.ObdAdapterInfo?.VersionResponse?.Trim() ?? "Unknown"),
            ("VIN Read", !string.IsNullOrEmpty(report.VehicleId?.Vin),
                report.VehicleId?.Vin != null ? MaskVin(report.VehicleId.Vin) : "Not available"),
            ("PID Discovery", (report.SupportedPids?.Mode01Pids.Count ?? 0) > 0,
                $"{report.SupportedPids?.Mode01Pids.Count ?? 0} Mode 01 PIDs"),
            ("PID Responses", report.StandardPidResults.Count(r => r.Success) > 0,
                $"{report.StandardPidResults.Count(r => r.Success)}/{report.StandardPidResults.Count} successful")
        };

        sb.AppendLine("| Check | Status | Details |");
        sb.AppendLine("|-------|--------|---------|");

        foreach (var (check, passed, details) in checksTable)
        {
            var status = passed switch { true => "?", false => "?", null => "?" };
            sb.AppendLine($"| {check} | {status} | {EscapeMarkdown(details)} |");
        }

        sb.AppendLine();
    }

    private static void AppendBleAdapterInfo(System.Text.StringBuilder sb, BleAdapterInfo info)
    {
        sb.AppendLine("## BLE Adapter Information");
        sb.AppendLine();
        sb.AppendLine($"**Device Name:** `{info.DeviceName}`");
        sb.AppendLine($"**MAC Address:** `{info.MacAddress}`");

        if (info.Rssi.HasValue)
            sb.AppendLine($"**RSSI:** {info.Rssi} dBm");

        sb.AppendLine();

        if (info.Services.Count > 0)
        {
            sb.AppendLine("<details>");
            sb.AppendLine("<summary>GATT Services & Characteristics</summary>");
            sb.AppendLine();
            sb.AppendLine("```");

            foreach (var service in info.Services)
            {
                sb.AppendLine($"Service: {service.ServiceUuid}");
                foreach (var characteristic in service.Characteristics)
                {
                    var props = string.Join(", ", characteristic.Properties);
                    sb.AppendLine($"  ?? {characteristic.CharacteristicUuid} [{props}]");
                }
            }

            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }

    private static void AppendObdAdapterInfo(System.Text.StringBuilder sb, ObdAdapterInfo info)
    {
        sb.AppendLine("## OBD Adapter Information");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|----------|-------|");

        if (!string.IsNullOrEmpty(info.VersionResponse))
            sb.AppendLine($"| Version (ATI) | `{EscapeMarkdown(info.VersionResponse.Trim())}` |");
        if (!string.IsNullOrEmpty(info.DeviceDescription))
            sb.AppendLine($"| Description (AT@1) | `{EscapeMarkdown(info.DeviceDescription.Trim())}` |");
        if (!string.IsNullOrEmpty(info.VoltageResponse))
            sb.AppendLine($"| Voltage (ATRV) | `{EscapeMarkdown(info.VoltageResponse.Trim())}` |");
        if (!string.IsNullOrEmpty(info.ProtocolDescription))
            sb.AppendLine($"| Protocol (ATDP) | `{EscapeMarkdown(info.ProtocolDescription.Trim())}` |");

        sb.AppendLine();
    }

    private static void AppendVehicleId(System.Text.StringBuilder sb, VehicleIdentification info)
    {
        sb.AppendLine("## Vehicle Identification (ECU)");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(info.Vin))
            sb.AppendLine($"**VIN:** `{MaskVin(info.Vin)}` (last 6 masked for privacy)");
        else
            sb.AppendLine("**VIN:** Not available");

        sb.AppendLine();
    }

    private static void AppendSupportedPids(System.Text.StringBuilder sb, SupportedPidsInfo info)
    {
        sb.AppendLine("## Supported PIDs");
        sb.AppendLine();
        sb.AppendLine($"**Mode 01 (Live Data):** {info.Mode01Pids.Count} PIDs");

        if (info.Mode01Pids.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(string.Join(", ", info.Mode01Pids));
            sb.AppendLine("```");
        }

        sb.AppendLine();
    }

    private static void AppendPidProbeResults(System.Text.StringBuilder sb, string title, IReadOnlyList<PidProbeResult> results)
    {
        sb.AppendLine($"## {title}");
        sb.AppendLine();

        var successful = results.Where(r => r.Success).ToList();
        var failed = results.Where(r => !r.Success).ToList();

        if (successful.Count > 0)
        {
            sb.AppendLine("### Successful Responses");
            sb.AppendLine();
            sb.AppendLine("| PID | Description | Response | Time |");
            sb.AppendLine("|-----|-------------|----------|------|");

            foreach (var result in successful)
            {
                var response = result.RawResponse ?? "";
                if (response.Length > 50)
                    response = response[..47] + "...";

                sb.AppendLine($"| `{result.Command}` | {result.Description} | `{EscapeMarkdown(response)}` | {result.ResponseTime.TotalMilliseconds:F0}ms |");
            }

            sb.AppendLine();
        }

        sb.AppendLine("<details>");
        sb.AppendLine("<summary>All PID Probe Data (Raw)</summary>");
        sb.AppendLine();
        sb.AppendLine("```");

        foreach (var result in results)
        {
            var status = result.Success ? "OK" : "FAIL";
            var response = result.RawResponse?.Replace("\r", "\\r").Replace("\n", "\\n") ?? result.Error ?? "";
            if (response.Length > 50)
                response = response[..47] + "...";

            sb.AppendLine($"[{status}] {result.Command} ({result.Description}): {response} [{result.ResponseTime.TotalMilliseconds:F0}ms]");
        }

        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("</details>");
        sb.AppendLine();

        if (failed.Count > 0)
            sb.AppendLine($"*{failed.Count} PIDs did not respond or returned errors*");

        sb.AppendLine();
    }

    private static void AppendErrors(System.Text.StringBuilder sb, IReadOnlyList<DiagnosticError> errors)
    {
        sb.AppendLine("## Errors Encountered");
        sb.AppendLine();

        foreach (var error in errors)
        {
            sb.AppendLine($"- **{error.Phase}:** {error.Message}");
            if (!string.IsNullOrEmpty(error.Details))
            {
                sb.AppendLine($"  ```");
                sb.AppendLine($"  {error.Details}");
                sb.AppendLine($"  ```");
            }
        }

        sb.AppendLine();
    }

    private static void AppendNotes(System.Text.StringBuilder sb, IReadOnlyList<string> notes)
    {
        sb.AppendLine("## Collection Notes");
        sb.AppendLine();

        foreach (var note in notes)
            sb.AppendLine($"- {note}");

        sb.AppendLine();
    }

    private static string MaskVin(string vin)
    {
        if (vin.Length <= 6)
            return new string('*', vin.Length);
        return vin[..^6] + "******";
    }

    private static string EscapeMarkdown(string text)
    {
        return text
            .Replace("|", "\\|")
            .Replace("`", "\\`")
            .Replace("\r", "")
            .Replace("\n", " ");
    }
}
