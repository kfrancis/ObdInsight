using ObdInsight.Core.Vehicles;
using ObdInsight.Drivers.Vehicles;

namespace OdbInsights.Tests.Vehicles;

public class VinInfoTests
{
    [Test]
    [Arguments("1N4AZ0CP5HC123456", "Nissan (USA)", "USA", 2017)]
    [Arguments("JN1TBNT30Z0000001", "Nissan (Japan)", "Japan", null)]
    [Arguments("5YJSA1E26HF123456", "Tesla", "USA", 2017)]
    [Arguments("1G1FW6S08H4123456", "Chevrolet", "USA", 2017)]
    [Arguments("WVWZZZ3CZE0123456", "Volkswagen", "Germany", 2014)]
    public async Task Parse_ValidVin_ReturnsCorrectInfo(
        string vin,
        string expectedManufacturer,
        string expectedCountry,
        int? expectedYear)
    {
        var info = VinInfo.Parse(vin);

        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Vin).IsEqualTo(vin.ToUpperInvariant());
        await Assert.That(info.Manufacturer).IsEqualTo(expectedManufacturer);
        await Assert.That(info.Country).IsEqualTo(expectedCountry);

        if (expectedYear.HasValue)
        {
            await Assert.That(info.ModelYear).IsEqualTo(expectedYear);
        }
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("TOOSHORT")]
    [Arguments("123456789012345678")] // 18 chars - too long
    public async Task Parse_InvalidVin_ReturnsNull(string? vin)
    {
        var info = VinInfo.Parse(vin);

        await Assert.That(info).IsNull();
    }

    [Test]
    public async Task Parse_ExtractsWmiVdsVis()
    {
        var info = VinInfo.Parse("1N4AZ0CP5HC123456");

        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Wmi).IsEqualTo("1N4"); // World Manufacturer Identifier
        await Assert.That(info.Vds).IsEqualTo("AZ0CP5"); // Vehicle Descriptor Section
        await Assert.That(info.Vis).IsEqualTo("HC123456"); // Vehicle Identifier Section
    }
}

public class NissanLeafProfileTests
{
    private readonly NissanLeafProfile _profile = new();

    [Test]
    [Arguments("1N4AZ0CP5HC123456")] // US-built Leaf
    [Arguments("JN1AZEV30U0123456")] // Japan-built Leaf (Gen 1 - ZE0)
    public async Task MatchesVin_ValidLeafVin_ReturnsTrue(string vin)
    {
        var matches = _profile.MatchesVin(vin);

        await Assert.That(matches).IsTrue();
    }

    [Test]
    [Arguments("5YJSA1E26HF123456")] // Tesla
    [Arguments("1G1FW6S08H4123456")] // Chevrolet Bolt
    [Arguments("1HGBH41JXMN123456")] // Honda Accord
    [Arguments("WVWZZZ3CZE0123456")] // VW
    public async Task MatchesVin_NonLeafVin_ReturnsFalse(string vin)
    {
        var matches = _profile.MatchesVin(vin);

        await Assert.That(matches).IsFalse();
    }

    [Test]
    public async Task Profile_HasCorrectMetadata()
    {
        await Assert.That(_profile.Name).IsEqualTo("Nissan Leaf");
        await Assert.That(_profile.Manufacturer).IsEqualTo("Nissan");
        await Assert.That(_profile.Model).IsEqualTo("Leaf");
        await Assert.That(_profile.IsElectric).IsTrue();
        await Assert.That(_profile.Protocol).IsEqualTo(VehicleProtocol.NissanCarCan);
    }

    [Test]
    public async Task Profile_SupportsEvCategories()
    {
        await Assert.That(_profile.SupportedCategories.Contains(VehicleDataCategory.Battery)).IsTrue();
        await Assert.That(_profile.SupportedCategories.Contains(VehicleDataCategory.Charging)).IsTrue();
        await Assert.That(_profile.SupportedCategories.Contains(VehicleDataCategory.Range)).IsTrue();
    }

    [Test]
    public async Task Profile_DoesNotSupportFuelCategory()
    {
        await Assert.That(_profile.SupportedCategories.Contains(VehicleDataCategory.Fuel)).IsFalse();
    }

    [Test]
    public async Task GetCommand_BatterySoc_ReturnsCommand()
    {
        var command = _profile.GetCommand(VehicleDataPoint.BatteryStateOfCharge);

        await Assert.That(command).IsNotNull();
        await Assert.That(command!.Command).IsEqualTo("022101");
    }

    [Test]
    public async Task GetCommand_FuelLevel_ReturnsNull()
    {
        // Leaf doesn't support fuel level
        var command = _profile.GetCommand(VehicleDataPoint.FuelLevel);

        await Assert.That(command).IsNull();
    }

    [Test]
    public async Task GetInitializationCommands_ReturnsLeafSpecificCommands()
    {
        var commands = _profile.GetInitializationCommands();

        await Assert.That(commands.Count).IsGreaterThan(0);

        // Should include CAN protocol setup
        await Assert.That(commands.Any(c => c.Command.Contains("ATSP"))).IsTrue();

        // Should include header setup for BMS
        await Assert.That(commands.Any(c => c.Command.Contains("ATSH79B"))).IsTrue();
    }
}

public class StandardObdVehicleProfileTests
{
    private readonly StandardObdVehicleProfile _profile = new();

    [Test]
    public async Task MatchesVin_AnyVin_ReturnsFalse()
    {
        // Generic profile doesn't match specific VINs
        var matches = _profile.MatchesVin("1N4AZ0CP5HC123456");

        await Assert.That(matches).IsFalse();
    }

    [Test]
    public async Task Profile_HasCorrectMetadata()
    {
        await Assert.That(_profile.Name).IsEqualTo("Standard OBD-II Vehicle");
        await Assert.That(_profile.Manufacturer).IsEqualTo("Generic");
        await Assert.That(_profile.IsElectric).IsFalse();
        await Assert.That(_profile.Protocol).IsEqualTo(VehicleProtocol.StandardObd2);
    }

    [Test]
    public async Task Profile_SupportsStandardCategories()
    {
        await Assert.That(_profile.SupportedCategories.Contains(VehicleDataCategory.Engine)).IsTrue();
        await Assert.That(_profile.SupportedCategories.Contains(VehicleDataCategory.Diagnostics)).IsTrue();
        await Assert.That(_profile.SupportedCategories.Contains(VehicleDataCategory.Fuel)).IsTrue();
    }

    [Test]
    public async Task GetCommand_Rpm_ReturnsStandardPid()
    {
        var command = _profile.GetCommand(VehicleDataPoint.Rpm);

        await Assert.That(command).IsNotNull();
        await Assert.That(command!.Command).IsEqualTo("010C");
    }

    [Test]
    public async Task GetCommand_BatterySoc_ReturnsNull()
    {
        // Standard profile doesn't support EV-specific data
        var command = _profile.GetCommand(VehicleDataPoint.BatteryStateOfCharge);

        await Assert.That(command).IsNull();
    }

    [Test]
    public async Task DecodeResponse_Rpm_DecodesCorrectly()
    {
        // RPM formula: ((A * 256) + B) / 4
        // 0x1A 0xF8 = 6904 / 4 = 1726 RPM
        var bytes = new byte[] { 0x1A, 0xF8 };

        var result = _profile.DecodeResponse(VehicleDataPoint.Rpm, bytes);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Value).IsEqualTo(1726.0);
        await Assert.That(result.Unit).IsEqualTo("rpm");
    }

    [Test]
    public async Task DecodeResponse_CoolantTemp_DecodesCorrectly()
    {
        // Coolant temp formula: A - 40
        // 0x5A = 90 - 40 = 50°C
        var bytes = new byte[] { 0x5A };

        var result = _profile.DecodeResponse(VehicleDataPoint.CoolantTemp, bytes);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Value).IsEqualTo(50);
        await Assert.That(result.Unit).IsEqualTo("°C");
    }
}