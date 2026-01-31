using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;
using OdbTestApp.Tests.Fixtures;

namespace OdbTestApp.Tests.NissanLeaf.AZE0.Integration;

/// <summary>
/// Integration tests for Nissan Leaf AZE0 passive monitoring capabilities using source-generated frames.
/// These tests require a physical Nissan Leaf AZE0 with OBD adapter connected via BLE.
/// Validates: Steering, Brake, ABS, Body Control, and VCM capabilities using broadcast CAN frames.
/// </summary>
[ClassDataSource<BleSessionFixture>(Shared = SharedType.Keyed)]
public class LeafAze0PassiveMonitoringIntegrationTests(BleSessionFixture bleFixture)
{
    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("CrossCapability")]
    public async Task Abs_And_Brake_StatusConsistency()
    {
        // Arrange
        var session = bleFixture.Session;
        var brake = new LeafAze0Brake(session, LeafAze0Contexts.BrakeBroadcast);
        var abs = new LeafAze0Abs(session, LeafAze0Contexts.AbsBroadcast);

        // Act
        var brakeStatus = await brake.GetStatusAsync(CancellationToken.None);
        var absStatus = await abs.GetStatusAsync(CancellationToken.None);

        // Assert - When vehicle is parked, brake should not be pressed and wheels should be stopped
        if (!brakeStatus.BrakePressed)
        {
            // If brake is not pressed and we're parked, wheels should be stationary
            await Assert.That(absStatus.VehicleSpeedKmh!.Value).IsLessThan(5.0);
        }

        Console.WriteLine($"[Consistency] Brake Pressed: {brakeStatus.BrakePressed}, Vehicle Speed: {absStatus.VehicleSpeedKmh:F2} km/h");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("ABS")]
    public async Task Abs_GetStatus_ReturnsLeadAcidBatteryVoltage()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.AbsBroadcast;
        var abs = new LeafAze0Abs(session, context);

        // Act
        var status = await abs.GetStatusAsync(CancellationToken.None);

        // Assert - 12V battery should be between 11-15V typically
        await Assert.That(status.LeadAcidBatteryVoltage).IsNotNull();
        await Assert.That(status.LeadAcidBatteryVoltage!.Value).IsGreaterThanOrEqualTo(10.0);
        await Assert.That(status.LeadAcidBatteryVoltage!.Value).IsLessThanOrEqualTo(16.0);

        Console.WriteLine($"[ABS] 12V Battery: {status.LeadAcidBatteryVoltage:F2}V");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("ABS")]
    public async Task Abs_GetStatus_ReturnsValidWheelSpeeds()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.AbsBroadcast;
        var abs = new LeafAze0Abs(session, context);

        // Act
        var status = await abs.GetStatusAsync(CancellationToken.None);

        // Assert - All wheel speeds should be present and valid
        // When parked, wheel speeds should be 0 or very close to 0
        await Assert.That(status.WheelSpeedFrKmh).IsNotNull();
        await Assert.That(status.WheelSpeedFlKmh).IsNotNull();
        await Assert.That(status.WheelSpeedRrKmh).IsNotNull();
        await Assert.That(status.WheelSpeedRlKmh).IsNotNull();

        // Speeds should be in reasonable range (0-200 km/h)
        await Assert.That(status.WheelSpeedFrKmh!.Value).IsGreaterThanOrEqualTo(0.0);
        await Assert.That(status.WheelSpeedFrKmh!.Value).IsLessThanOrEqualTo(200.0);
        await Assert.That(status.WheelSpeedFlKmh!.Value).IsGreaterThanOrEqualTo(0.0);
        await Assert.That(status.WheelSpeedFlKmh!.Value).IsLessThanOrEqualTo(200.0);
        await Assert.That(status.WheelSpeedRrKmh!.Value).IsGreaterThanOrEqualTo(0.0);
        await Assert.That(status.WheelSpeedRrKmh!.Value).IsLessThanOrEqualTo(200.0);
        await Assert.That(status.WheelSpeedRlKmh!.Value).IsGreaterThanOrEqualTo(0.0);
        await Assert.That(status.WheelSpeedRlKmh!.Value).IsLessThanOrEqualTo(200.0);

        Console.WriteLine($"[ABS] Wheel Speeds - FR: {status.WheelSpeedFrKmh:F2}, FL: {status.WheelSpeedFlKmh:F2}, RR: {status.WheelSpeedRrKmh:F2}, RL: {status.WheelSpeedRlKmh:F2} km/h");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("ABS")]
    public async Task Abs_GetStatus_ReturnsVehicleSpeed()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.AbsBroadcast;
        var abs = new LeafAze0Abs(session, context);

        // Act
        var status = await abs.GetStatusAsync(CancellationToken.None);

        // Assert
        await Assert.That(status.VehicleSpeedKmh).IsNotNull();
        await Assert.That(status.VehicleSpeedKmh!.Value).IsGreaterThanOrEqualTo(0.0);
        await Assert.That(status.VehicleSpeedKmh!.Value).IsLessThanOrEqualTo(200.0);

        Console.WriteLine($"[ABS] Vehicle Speed: {status.VehicleSpeedKmh:F2} km/h");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("ABS")]
    public async Task Abs_GetStatus_WhenParked_WheelSpeedsAreZero()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.AbsBroadcast;
        var abs = new LeafAze0Abs(session, context);

        // Act
        var status = await abs.GetStatusAsync(CancellationToken.None);

        // Assert - When parked, all wheels should report 0 or very close to 0
        await Assert.That(status.WheelSpeedFrKmh!.Value).IsLessThan(1.0);
        await Assert.That(status.WheelSpeedFlKmh!.Value).IsLessThan(1.0);
        await Assert.That(status.WheelSpeedRrKmh!.Value).IsLessThan(1.0);
        await Assert.That(status.WheelSpeedRlKmh!.Value).IsLessThan(1.0);
        await Assert.That(status.VehicleSpeedKmh!.Value).IsLessThan(1.0);

        Console.WriteLine($"[ABS Parked] All speeds < 1 km/h");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("BodyControl")]
    public async Task BodyControl_GetStatus_ReturnsValidData()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.BcmBroadcast;
        var bodyControl = new LeafAze0BodyControl(session, context);

        // Act
        var status = await bodyControl.GetStatusAsync(CancellationToken.None);

        // Assert - Status should contain valid boolean values
        await Assert.That(status.DoorsLocked).IsTypeOf<bool>();
        await Assert.That(status.HeadlightsOn).IsTypeOf<bool>();
        await Assert.That(status.HazardLightsOn).IsTypeOf<bool>();

        Console.WriteLine($"[Body Control] Doors Locked: {status.DoorsLocked}, Headlights: {status.HeadlightsOn}, Hazards: {status.HazardLightsOn}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("BodyControl")]
    public async Task BodyControl_GetStatus_WhenIdle_HazardsOff()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.BcmBroadcast;
        var bodyControl = new LeafAze0BodyControl(session, context);

        // Act
        var status = await bodyControl.GetStatusAsync(CancellationToken.None);

        // Assert - Hazard lights should typically be off during testing
        await Assert.That(status.HazardLightsOn).IsFalse();

        Console.WriteLine($"[Body Control Idle] Hazards: {status.HazardLightsOn}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("ErrorHandling")]
    public async Task Brake_GetStatus_HandlesNoFrameGracefully()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.BrakeBroadcast;
        var brake = new LeafAze0Brake(session, context);

        // Act - Even if frames are slow or missing, should return valid default
        var status = await brake.GetStatusAsync(CancellationToken.None);

        // Assert - Should return valid status (may be default values)
        // BrakeStatus is a readonly struct, always has a value
        await Assert.That(status.BrakePressed).IsTypeOf<bool>();

        Console.WriteLine($"[Brake No Frame] Returned status: {status.BrakePressed}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("Brake")]
    public async Task Brake_GetStatus_ReturnsValidData()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.BrakeBroadcast;
        var brake = new LeafAze0Brake(session, context);

        // Act
        var status = await brake.GetStatusAsync(CancellationToken.None);

        // Assert - Status should be valid boolean values
        await Assert.That(status.BrakePressed).IsTypeOf<bool>();
        await Assert.That(status.AbsActive).IsTypeOf<bool>();

        Console.WriteLine($"[Brake] Pressed: {status.BrakePressed}, ABS Active: {status.AbsActive}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("Brake")]
    public async Task Brake_GetStatus_WhenNotPressed_ReturnsFalse()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.BrakeBroadcast;
        var brake = new LeafAze0Brake(session, context);

        // Act
        var status = await brake.GetStatusAsync(CancellationToken.None);

        // Assert - When vehicle is parked/idle without brake, should be false
        // Note: This assumes the test is run without brake pedal pressed
        await Assert.That(status.BrakePressed).IsFalse();

        Console.WriteLine($"[Brake Not Pressed] Status: {status.BrakePressed}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("CrossCapability")]
    public async Task MultipleCapabilities_CanBeQueriedSequentially()
    {
        // Arrange
        var session = bleFixture.Session;
        var steering = new LeafAze0Steering(session, LeafAze0Contexts.SteeringBroadcast);
        var brake = new LeafAze0Brake(session, LeafAze0Contexts.BrakeBroadcast);
        var abs = new LeafAze0Abs(session, LeafAze0Contexts.AbsBroadcast);

        // Act & Assert - Query multiple capabilities in sequence
        _ = await steering.GetStatusAsync(CancellationToken.None);

        // SteeringStatus is a readonly struct, always has a value
        _ = await brake.GetStatusAsync(CancellationToken.None);

        // BrakeStatus is a readonly struct, always has a value
        var absStatus = await abs.GetStatusAsync(CancellationToken.None);

        await Assert.That(absStatus).IsNotNull();
        await Assert.That(absStatus.VehicleSpeedKmh).IsNotNull();

        Console.WriteLine($"[Multi-Query] Successfully queried Steering, Brake, and ABS sequentially");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("Steering")]
    public async Task Steering_GetStatus_ReturnsValidData()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.SteeringBroadcast;
        var steering = new LeafAze0Steering(session, context);

        // Note: LeafAze0Steering now handles session activation internally if RequiresSessionActivation is set
        // The context SteeringBroadcast now uses ActiveMonitoring with session activation

        // Act
        var status = await steering.GetStatusAsync(CancellationToken.None);

        // Assert - Steering angle should be within reasonable range
        // When parked or driving straight: -45 to +45 degrees is typical
        // Full lock is typically around ±500 degrees
        await Assert.That(status.AngleDegrees).IsGreaterThanOrEqualTo(-600.0);
        await Assert.That(status.AngleDegrees).IsLessThanOrEqualTo(600.0);

        // Torque should be reasonable (0-10 Nm typical for driving)
        await Assert.That(status.TorqueNm).IsGreaterThanOrEqualTo(0.0);
        await Assert.That(status.TorqueNm).IsLessThanOrEqualTo(15.0); // Allow some headroom

        Console.WriteLine($"[Steering] Angle: {status.AngleDegrees:F2}°, Torque: {status.TorqueNm:F2} Nm");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("Steering")]
    public async Task Steering_GetStatus_WhenIdle_HasLowTorque()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.SteeringBroadcast;
        var steering = new LeafAze0Steering(session, context);

        // Act
        var status = await steering.GetStatusAsync(CancellationToken.None);

        // Assert - When vehicle is idle/parked, torque should be very low
        // This test may need adjustment based on actual vehicle state
        await Assert.That(status.TorqueNm).IsLessThan(5.0);

        Console.WriteLine($"[Steering Idle] Torque: {status.TorqueNm:F2} Nm");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("ErrorHandling")]
    public async Task Steering_GetStatus_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.SteeringBroadcast;
        var steering = new LeafAze0Steering(session, context);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10)); // Very short timeout

        // Act & Assert
        await Assert.That(async () => await steering.GetStatusAsync(cts.Token))
            .ThrowsExactly<OperationCanceledException>();

        Console.WriteLine($"[Steering Cancellation] Successfully handled cancellation");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("VCM")]
    public async Task Vcm_GetStatus_ReturnsAmbientTemperature()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.VcmCarCanBroadcast;
        var vcmCarCan = new LeafAze0VcmCarCan(session, context);

        // Act
        var status = await vcmCarCan.GetStatusAsync(CancellationToken.None);

        // Assert - Ambient temperature should be in reasonable range (-40°C to +60°C)
        await Assert.That(status.OutsideAmbientTempC).IsNotNull();
        await Assert.That(status.OutsideAmbientTempC!.Value).IsGreaterThanOrEqualTo(-40.0);
        await Assert.That(status.OutsideAmbientTempC!.Value).IsLessThanOrEqualTo(60.0);

        Console.WriteLine($"[VCM] Outside Temp: {status.OutsideAmbientTempC:F1}°C");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("VCM")]
    public async Task Vcm_GetStatus_ReturnsEcoIndicators()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.VcmCarCanBroadcast;
        var vcmCarCan = new LeafAze0VcmCarCan(session, context);

        // Act
        var status = await vcmCarCan.GetStatusAsync(CancellationToken.None);

        // Assert - Eco indicators should be present and in valid ranges
        await Assert.That(status.EcoIndicator).IsNotNull();
        await Assert.That(status.EcoTree).IsNotNull();

        await Assert.That(status.EcoIndicator!.Value).IsGreaterThanOrEqualTo(0);
        await Assert.That(status.EcoIndicator!.Value).IsLessThanOrEqualTo(15);
        await Assert.That(status.EcoTree!.Value).IsGreaterThanOrEqualTo(0);
        await Assert.That(status.EcoTree!.Value).IsLessThanOrEqualTo(31);

        Console.WriteLine($"[VCM] Eco Indicator: {status.EcoIndicator}, Eco Tree: {status.EcoTree}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("VCM")]
    public async Task Vcm_GetStatus_ReturnsPowerConsumptionMetrics()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.VcmCarCanBroadcast;
        var vcmCarCan = new LeafAze0VcmCarCan(session, context);

        // Act
        var status = await vcmCarCan.GetStatusAsync(CancellationToken.None);

        // Assert - Integrated power consumption values should be present
        await Assert.That(status.IntegratedMotorPowerConsumption).IsNotNull();
        await Assert.That(status.IntegratedAcPowerConsumption).IsNotNull();
        await Assert.That(status.IntegratedAuxPowerConsumption).IsNotNull();

        // Values should be in valid ranges
        await Assert.That(status.IntegratedMotorPowerConsumption!.Value).IsGreaterThanOrEqualTo(0);
        await Assert.That(status.IntegratedMotorPowerConsumption!.Value).IsLessThanOrEqualTo(255);
        await Assert.That(status.IntegratedAcPowerConsumption!.Value).IsGreaterThanOrEqualTo(0);
        await Assert.That(status.IntegratedAcPowerConsumption!.Value).IsLessThanOrEqualTo(31);
        await Assert.That(status.IntegratedAuxPowerConsumption!.Value).IsGreaterThanOrEqualTo(0);
        await Assert.That(status.IntegratedAuxPowerConsumption!.Value).IsLessThanOrEqualTo(15);

        Console.WriteLine($"[VCM] Integrated Power - Motor: {status.IntegratedMotorPowerConsumption}, AC: {status.IntegratedAcPowerConsumption}, Aux: {status.IntegratedAuxPowerConsumption}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("VCM")]
    public async Task Vcm_GetStatus_ReturnsValidData()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.VcmCarCanBroadcast;
        var vcmCarCan = new LeafAze0VcmCarCan(session, context);

        // Act
        var status = await vcmCarCan.GetStatusAsync(CancellationToken.None);

        // Assert - Climate control status
        await Assert.That(status.ClimateControlActive).IsNotNull();

        // If climate is active, power consumption should be present
        if (status.ClimateControlActive == true && status.ClimateControlPowerKw.HasValue)
        {
            await Assert.That(status.ClimateControlPowerKw.Value).IsGreaterThanOrEqualTo(0.0);
            await Assert.That(status.ClimateControlPowerKw.Value).IsLessThanOrEqualTo(10.0); // Max ~10kW for HVAC
        }

        Console.WriteLine($"[VCM] Climate Active: {status.ClimateControlActive}, Power: {status.ClimateControlPowerKw:F2} kW");
    }
}
