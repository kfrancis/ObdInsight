using ObdInsight.Core.Diagnostics;

namespace OdbInsights.Tests.Diagnostics;

public class BleAdapterInfoTests
{
    [Test]
    public async Task BleAdapterInfo_DefaultServices_IsEmptyList()
    {
        var info = new BleAdapterInfo
        {
            DeviceName = "Test",
            MacAddress = "00:00:00:00:00:00"
        };

        await Assert.That(info.Services).IsEmpty();
    }

    [Test]
    public async Task BleAdapterInfo_WithServices_ContainsCharacteristics()
    {
        var info = new BleAdapterInfo
        {
            DeviceName = "Veepeak",
            MacAddress = "AA:BB:CC:DD:EE:FF",
            Rssi = -65,
            Services =
            [
                new BleServiceInfo
                {
                    ServiceUuid = Guid.NewGuid(),
                    Characteristics =
                    [
                        new BleCharacteristicInfo
                        {
                            CharacteristicUuid = Guid.NewGuid(),
                            Properties = ["Read", "Write", "Notify"]
                        }
                    ]
                }
            ]
        };

        await Assert.That(info.DeviceName).IsEqualTo("Veepeak");
        await Assert.That(info.Rssi).IsEqualTo(-65);
        await Assert.That(info.Services.Count).IsEqualTo(1);
        await Assert.That(info.Services[0].Characteristics.Count).IsEqualTo(1);
        await Assert.That(info.Services[0].Characteristics[0].Properties).Contains("Notify");
    }
}

public class DiagnosticReportTests
{
    [Test]
    public async Task DiagnosticReport_DefaultCollections_AreEmpty()
    {
        var report = new DiagnosticReport
        {
            GeneratedAt = DateTime.UtcNow,
            ToolVersion = "1.0.0",
            UserVehicleInfo = CreateTestUserInfo()
        };

        await Assert.That(report.StandardPidResults).IsEmpty();
        await Assert.That(report.ExtendedPidResults).IsEmpty();
        await Assert.That(report.Errors).IsEmpty();
        await Assert.That(report.Notes).IsEmpty();
    }

    [Test]
    public async Task DiagnosticReport_WithFullData_ContainsAllSections()
    {
        var report = CreateFullTestReport();

        await Assert.That(report.BleAdapterInfo).IsNotNull();
        await Assert.That(report.ObdAdapterInfo).IsNotNull();
        await Assert.That(report.VehicleId).IsNotNull();
        await Assert.That(report.SupportedPids).IsNotNull();
        await Assert.That(report.StandardPidResults.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task DiagnosticReport_WithRequiredFields_CreatesSuccessfully()
    {
        var report = new DiagnosticReport
        {
            GeneratedAt = DateTime.UtcNow,
            ToolVersion = "1.0.0",
            UserVehicleInfo = CreateTestUserInfo()
        };

        await Assert.That(report.GeneratedAt).IsGreaterThan(DateTime.MinValue);
        await Assert.That(report.ToolVersion).IsEqualTo("1.0.0");
        await Assert.That(report.UserVehicleInfo.Make).IsEqualTo("Honda");
    }

    private static DiagnosticReport CreateFullTestReport() => new()
    {
        GeneratedAt = DateTime.UtcNow,
        ToolVersion = "1.0.0",
        UserVehicleInfo = new UserVehicleInfo
        {
            Year = 2022,
            Make = "Honda",
            Model = "CR-V",
            Trim = "EX-L",
            EngineType = "1.5L Turbo",
            TransmissionType = "CVT"
        },
        BleAdapterInfo = new BleAdapterInfo
        {
            DeviceName = "OBDII",
            MacAddress = "66:1e:87:02:c2:db",
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
                        }
                    ]
                }
            ]
        },
        ObdAdapterInfo = new ObdAdapterInfo
        {
            VersionResponse = "ELM327 v1.5",
            ProtocolDescription = "AUTO, ISO 15765-4 (CAN 11/500)"
        },
        VehicleId = new VehicleIdentification
        {
            Vin = "5J6RW2H53NA000001"
        },
        SupportedPids = new SupportedPidsInfo
        {
            Mode01Pids = ["0100", "0101", "0104", "0105", "010C", "010D"],
            Mode09Pids = ["0900", "0902"]
        },
        StandardPidResults =
        [
            new PidProbeResult
            {
                Command = "010C",
                Description = "Engine RPM",
                Success = true,
                RawResponse = "410C 1AF8",
                ResponseTime = TimeSpan.FromMilliseconds(45)
            }
        ]
    };

    private static UserVehicleInfo CreateTestUserInfo() => new()
    {
        Year = 2022,
        Make = "Honda",
        Model = "CR-V"
    };
}

public class ObdAdapterInfoTests
{
    [Test]
    public async Task ObdAdapterInfo_WithResponses_ContainsAllData()
    {
        var info = new ObdAdapterInfo
        {
            ResetResponse = "ELM327 v1.5",
            VersionResponse = "ELM327 v1.5",
            VoltageResponse = "12.4V",
            ProtocolDescription = "ISO 15765-4 CAN",
            ProtocolNumber = "6",
            RawAtResponses = new Dictionary<string, string>
            {
                ["ATZ"] = "ELM327 v1.5",
                ["ATI"] = "ELM327 v1.5",
                ["ATRV"] = "12.4V"
            }
        };

        await Assert.That(info.VersionResponse).IsEqualTo("ELM327 v1.5");
        await Assert.That(info.VoltageResponse).IsEqualTo("12.4V");
        await Assert.That(info.RawAtResponses.Count).IsEqualTo(3);
    }
}

public class PidProbeResultTests
{
    [Test]
    public async Task PidProbeResult_FailedProbe_ContainsError()
    {
        var result = new PidProbeResult
        {
            Command = "015B",
            Description = "Hybrid battery remaining life",
            Success = false,
            Error = "NO DATA",
            ResponseTime = TimeSpan.FromMilliseconds(1000)
        };

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsEqualTo("NO DATA");
        await Assert.That(result.RawResponse).IsNull();
    }

    [Test]
    public async Task PidProbeResult_SuccessfulProbe_ContainsResponse()
    {
        var result = new PidProbeResult
        {
            Command = "010C",
            Description = "Engine RPM",
            Success = true,
            RawResponse = "410C 1AF8",
            ParsedValue = "1726",
            ResponseTime = TimeSpan.FromMilliseconds(45)
        };

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RawResponse).IsEqualTo("410C 1AF8");
        await Assert.That(result.Error).IsNull();
        await Assert.That(result.ResponseTime.TotalMilliseconds).IsEqualTo(45);
    }
}

public class SupportedPidsInfoTests
{
    [Test]
    public async Task SupportedPidsInfo_DefaultCollections_AreEmpty()
    {
        var info = new SupportedPidsInfo();

        await Assert.That(info.Mode01Pids).IsEmpty();
        await Assert.That(info.Mode09Pids).IsEmpty();
    }

    [Test]
    public async Task SupportedPidsInfo_WithPids_ContainsBothModes()
    {
        var info = new SupportedPidsInfo
        {
            Mode01Pids = ["0100", "0101", "0104", "0105", "010C", "010D", "0120"],
            Mode09Pids = ["0900", "0902", "0904"],
            RawResponses = new Dictionary<string, string>
            {
                ["0100"] = "4100BE3FA813",
                ["0900"] = "49001500000"
            }
        };

        await Assert.That(info.Mode01Pids.Count).IsEqualTo(7);
        await Assert.That(info.Mode09Pids.Count).IsEqualTo(3);
        await Assert.That(info.Mode01Pids).Contains("010C");
        await Assert.That(info.Mode09Pids).Contains("0902");
    }
}

public class UserVehicleInfoTests
{
    [Test]
    public async Task UserVehicleInfo_AllFields_ArePreserved()
    {
        var info = new UserVehicleInfo
        {
            Year = 2023,
            Make = "Nissan",
            Model = "Leaf",
            Trim = "SV Plus",
            EngineType = "Electric",
            TransmissionType = "Single-Speed",
            AdditionalNotes = "62kWh battery pack"
        };

        await Assert.That(info.Trim).IsEqualTo("SV Plus");
        await Assert.That(info.EngineType).IsEqualTo("Electric");
        await Assert.That(info.TransmissionType).IsEqualTo("Single-Speed");
        await Assert.That(info.AdditionalNotes).IsEqualTo("62kWh battery pack");
    }

    [Test]
    public async Task UserVehicleInfo_RequiredFieldsOnly_IsValid()
    {
        var info = new UserVehicleInfo
        {
            Year = 2020,
            Make = "Toyota",
            Model = "Camry"
        };

        await Assert.That(info.Year).IsEqualTo(2020);
        await Assert.That(info.Make).IsEqualTo("Toyota");
        await Assert.That(info.Model).IsEqualTo("Camry");
        await Assert.That(info.Trim).IsNull();
        await Assert.That(info.EngineType).IsNull();
    }
}

public class VehicleIdentificationTests
{
    [Test]
    public async Task VehicleIdentification_AllFieldsNullable_DefaultsToNull()
    {
        var info = new VehicleIdentification();

        await Assert.That(info.Vin).IsNull();
        await Assert.That(info.RawVinResponse).IsNull();
        await Assert.That(info.CalibrationId).IsNull();
        await Assert.That(info.EcuName).IsNull();
    }

    [Test]
    public async Task VehicleIdentification_WithVin_IsValid()
    {
        var info = new VehicleIdentification
        {
            Vin = "1N4AZ0CP5HC123456",
            RawVinResponse = "49020131 4E34415A 30435035 48433132 33343536"
        };

        await Assert.That(info.Vin).IsEqualTo("1N4AZ0CP5HC123456");
        await Assert.That(info.Vin).Length().IsEqualTo(17);
    }
}

public class DiagnosticErrorTests
{
    [Test]
    public async Task DiagnosticError_Timestamp_DefaultsToUtcNow()
    {
        var before = DateTime.UtcNow;
        var error = new DiagnosticError
        {
            Phase = "Test",
            Message = "Test error"
        };
        var after = DateTime.UtcNow;

        await Assert.That(error.Timestamp).IsGreaterThanOrEqualTo(before);
        await Assert.That(error.Timestamp).IsLessThanOrEqualTo(after);
    }

    [Test]
    public async Task DiagnosticError_WithAllFields_IsValid()
    {
        var error = new DiagnosticError
        {
            Phase = "VIN Read",
            Message = "Timeout waiting for response",
            Details = "Command: 0902, Timeout: 10s"
        };

        await Assert.That(error.Phase).IsEqualTo("VIN Read");
        await Assert.That(error.Message).IsEqualTo("Timeout waiting for response");
        await Assert.That(error.Details).IsNotNull();
    }
}