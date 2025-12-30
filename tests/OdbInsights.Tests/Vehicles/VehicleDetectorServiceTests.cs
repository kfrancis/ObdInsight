using ObdInsight.Core.Adapters;
using ObdInsight.Core.Vehicles;
using ObdInsight.Drivers;

namespace OdbInsights.Tests.Vehicles;

public class VehicleDetectorServiceTests
{
    private readonly VehicleDetectorService _detector;

    public VehicleDetectorServiceTests()
    {
        _detector = new VehicleDetectorService();
        // Register all profiles from Drivers package
        VehicleProfileRegistry.RegisterAllProfiles(_detector);
    }

    [Test]
    public async Task RegisteredProfiles_ContainsBuiltInProfiles()
    {
        await Assert.That(_detector.RegisteredProfiles.Count).IsGreaterThan(0);

        // Should have Nissan Leaf registered from Drivers package
        await Assert.That(_detector.RegisteredProfiles.Any(p => p.Name == "Nissan Leaf")).IsTrue();
    }

    [Test]
    [Arguments("1N4AZ0CP5HC123456", "Nissan Leaf")]
    public async Task DetectFromVin_KnownVehicle_ReturnsCorrectProfile(string vin, string expectedName)
    {
        var profile = _detector.DetectFromVin(vin);

        await Assert.That(profile).IsNotNull();
        await Assert.That(profile!.Name).IsEqualTo(expectedName);
    }

    [Test]
    public async Task DetectFromVin_UnknownVehicle_ReturnsNull()
    {
        // Random unknown VIN
        var profile = _detector.DetectFromVin("WDBRF61J21F123456"); // Mercedes

        await Assert.That(profile).IsNull();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("SHORT")]
    public async Task DetectFromVin_InvalidVin_ReturnsNull(string? vin)
    {
        var profile = _detector.DetectFromVin(vin!);

        await Assert.That(profile).IsNull();
    }

    [Test]
    public async Task RegisterProfile_AddsNewProfile()
    {
        var detector = new VehicleDetectorService();
        var customProfile = new TestVehicleProfile("Test Vehicle", "TestMfg");
        var initialCount = detector.RegisteredProfiles.Count;

        detector.RegisterProfile(customProfile);

        await Assert.That(detector.RegisteredProfiles.Count).IsEqualTo(initialCount + 1);
        await Assert.That(detector.RegisteredProfiles.Contains(customProfile)).IsTrue();
    }

    [Test]
    public async Task RegisterProfile_DuplicateProfile_DoesNotAddAgain()
    {
        var detector = new VehicleDetectorService();
        var customProfile = new TestVehicleProfile("Unique Test", "UniqueMfg");

        detector.RegisterProfile(customProfile);
        var countAfterFirst = detector.RegisteredProfiles.Count;

        detector.RegisterProfile(customProfile);
        var countAfterSecond = detector.RegisteredProfiles.Count;

        await Assert.That(countAfterSecond).IsEqualTo(countAfterFirst);
    }

    [Test]
    public async Task RegisterProfile_NullProfile_ThrowsArgumentNullException()
    {
        var detector = new VehicleDetectorService();
        await Assert.That(() => detector.RegisterProfile(null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Simple test profile for testing registration
    /// </summary>
    private class TestVehicleProfile : IVehicleProfile
    {
        public TestVehicleProfile(string name, string manufacturer)
        {
            Name = name;
            Manufacturer = manufacturer;
        }

        public string Name { get; }
        public string Manufacturer { get; }
        public string Model => "Test";
        public Range<int> SupportedYears => new(2020, 2025);
        public VehicleProtocol Protocol => VehicleProtocol.StandardObd2;
        public bool IsElectric => false;
        public IReadOnlyList<string> VinPrefixes => [];
        public IReadOnlyList<VehiclePid> CustomPids => [];
        public IReadOnlySet<VehicleDataCategory> SupportedCategories => new HashSet<VehicleDataCategory>();

        public ObdCommand? GetCommand(VehicleDataPoint dataPoint) => null;

        public VehicleDataResult DecodeResponse(VehicleDataPoint dataPoint, byte[] responseBytes) =>
            VehicleDataResult.Fail(dataPoint, "Test profile");

        public bool MatchesVin(string vin) => false;

        public IReadOnlyList<ObdCommand> GetInitializationCommands() => [];
    }
}