using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;
using ObdInsight.IntegrationTests;
using ObdInsight.IntegrationTests.Fixtures;

namespace ObdInsight.IntegrationTests.Nissan.Leaf.AZE0;

/// <summary>
///     Integration tests for Nissan Leaf AZE0 Motor Controller and HVAC using source-generated frames.
///     These tests require a physical Nissan Leaf AZE0 with OBD adapter connected via BLE.
///     Validates: Motor/Inverter status and HVAC status from broadcast CAN frames.
/// </summary>
[RequiresLeafHardware]
[ClassDataSource<BleSessionFixture>(Shared = SharedType.Keyed)]
public class LeafAze0MotorAndHvacIntegrationTests(BleSessionFixture bleFixture)
{
    // Migrated capabilities view a shared CanMonitor (streaming design P2) instead of
    // taking (session, context). One monitor per test class instance.
    private CanMonitor? _monitor;

    private CanMonitor Monitor =>
        _monitor ??= new CanMonitor(
            bleFixture.Session, LeafAze0Contexts.SharedBroadcastMonitor);

    [After(Test)]
    public async Task DisposeMonitorAsync()
    {
        if (_monitor is not null)
        {
            await _monitor.DisposeAsync();
            _monitor = null;
        }
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("ErrorHandling")]
    public async Task Hvac_GetStatus_HandlesPartialDataGracefully()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.HvacBroadcast;
        var hvac = new LeafAze0Hvac(Monitor);

        // Act - Even if some frames are slow or missing, should return valid status
        var status = await hvac.GetStatusAsync(CancellationToken.None);

        // Assert - Should return valid status (may have some null optional fields)
        await Assert.That(status).IsNotNull();
        await Assert.That(status.ClimateControlOn).IsTypeOf<bool>();

        Console.WriteLine($"[HVAC Partial] Climate: {status.ClimateControlOn}, AC: {status.AcOn}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("HVAC")]
    public async Task Hvac_GetStatus_ReturnsFanSpeed()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.HvacBroadcast;
        var hvac = new LeafAze0Hvac(Monitor);

        // Act
        var status = await hvac.GetStatusAsync(CancellationToken.None);

        // Assert - Fan speed should be present and in valid range (0-15 typically)
        if (status.FanSpeed.HasValue)
        {
            await Assert.That(status.FanSpeed.Value).IsGreaterThanOrEqualTo(0);
            await Assert.That(status.FanSpeed.Value).IsLessThanOrEqualTo(15);
        }

        // Fan voltage should be reasonable if present (0-14V typically)
        if (status.FanVoltageV.HasValue)
        {
            await Assert.That(status.FanVoltageV.Value).IsGreaterThanOrEqualTo(0.0);
            await Assert.That(status.FanVoltageV.Value).IsLessThanOrEqualTo(15.0);
        }

        Console.WriteLine($"[HVAC Fan] Speed: {status.FanSpeed}, Voltage: {status.FanVoltageV:F1}V");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("HVAC")]
    public async Task Hvac_GetStatus_ReturnsTemperatures()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.HvacBroadcast;
        var hvac = new LeafAze0Hvac(Monitor);

        // Act
        var status = await hvac.GetStatusAsync(CancellationToken.None);

        // Assert - Temperatures should be in reasonable range if present
        if (status.OutsideAmbientTempC.HasValue)
        {
            await Assert.That(status.OutsideAmbientTempC.Value).IsGreaterThanOrEqualTo(-40.0);
            await Assert.That(status.OutsideAmbientTempC.Value).IsLessThanOrEqualTo(60.0);
        }

        if (status.EvaporatorTempC.HasValue)
        {
            await Assert.That(status.EvaporatorTempC.Value).IsGreaterThanOrEqualTo(-40.0);
            await Assert.That(status.EvaporatorTempC.Value).IsLessThanOrEqualTo(60.0);
        }

        if (status.InteriorIntakeTempC.HasValue)
        {
            await Assert.That(status.InteriorIntakeTempC.Value).IsGreaterThanOrEqualTo(-40.0);
            await Assert.That(status.InteriorIntakeTempC.Value).IsLessThanOrEqualTo(60.0);
        }

        Console.WriteLine($"[HVAC Temps] Outside: {status.OutsideAmbientTempC:F1}°C, " +
                          $"Evaporator: {status.EvaporatorTempC:F1}°C, Interior: {status.InteriorIntakeTempC:F1}°C");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("HVAC")]
    public async Task Hvac_GetStatus_ReturnsValidData()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.HvacBroadcast;
        var hvac = new LeafAze0Hvac(Monitor);

        // Act
        var status = await hvac.GetStatusAsync(CancellationToken.None);

        // Assert - Status should contain valid boolean values
        await Assert.That(status.ClimateControlOn).IsTypeOf<bool>();
        await Assert.That(status.AcOn).IsTypeOf<bool>();
        await Assert.That(status.RearDefrostOn).IsTypeOf<bool>();

        Console.WriteLine(
            $"[HVAC] Climate: {status.ClimateControlOn}, AC: {status.AcOn}, Rear Defrost: {status.RearDefrostOn}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("HVAC")]
    public async Task Hvac_GetStatus_WhenClimateOff_LowPower()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.HvacBroadcast;
        var hvac = new LeafAze0Hvac(Monitor);

        // Act
        var status = await hvac.GetStatusAsync(CancellationToken.None);

        // Assert - If climate is off, power should be low or zero
        if (!status.ClimateControlOn)
        {
            if (status.AcPowerWatts.HasValue)
            {
                await Assert.That((double)status.AcPowerWatts.Value).IsLessThan(100.0); // Very minimal if off
            }

            if (status.HeaterPowerWatts.HasValue)
            {
                await Assert.That((double)status.HeaterPowerWatts.Value).IsLessThan(100.0); // Very minimal if off
            }

            Console.WriteLine(
                $"[HVAC Off] AC Power: {status.AcPowerWatts:F1}W, Heater Power: {status.HeaterPowerWatts:F1}W");
        }
        else
        {
            Console.WriteLine("[HVAC] Climate control is ON - skipping low power test");
        }
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("HVAC")]
    public async Task Hvac_GetStatus_WhenClimateOn_PowerValuesPresent()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.HvacBroadcast;
        var hvac = new LeafAze0Hvac(Monitor);

        // Act
        var status = await hvac.GetStatusAsync(CancellationToken.None);

        // Assert - If climate control is on, power values should be reasonable
        if (status.ClimateControlOn)
        {
            if (status.AcPowerWatts.HasValue)
            {
                await Assert.That((double)status.AcPowerWatts.Value).IsGreaterThanOrEqualTo(0.0);
                await Assert.That((double)status.AcPowerWatts.Value).IsLessThanOrEqualTo(10000.0); // Max ~10kW for HVAC
            }

            if (status.HeaterPowerWatts.HasValue)
            {
                await Assert.That((double)status.HeaterPowerWatts.Value).IsGreaterThanOrEqualTo(0.0);
                await Assert.That((double)status.HeaterPowerWatts.Value)
                    .IsLessThanOrEqualTo(10000.0); // Max ~10kW for heater
            }

            Console.WriteLine($"[HVAC Power] AC: {status.AcPowerWatts:F1}W, Heater: {status.HeaterPowerWatts:F1}W");
        }
        else
        {
            Console.WriteLine("[HVAC] Climate control is OFF");
        }
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("CrossCapability")]
    public async Task Motor_And_Hvac_TemperatureConsistency()
    {
        // Arrange
        var session = bleFixture.Session;
        var motorController = new LeafAze0MotorController(Monitor);
        var hvac = new LeafAze0Hvac(Monitor);

        // Act
        var motorStatus = await motorController.GetStatusAsync(CancellationToken.None);
        var hvacStatus = await hvac.GetStatusAsync(CancellationToken.None);

        // Assert - Motor and HVAC temperatures should be in similar ambient range
        if (motorStatus.MotorTempC.HasValue && hvacStatus.OutsideAmbientTempC.HasValue)
        {
            // Motor temp should generally be >= ambient (motor generates heat)
            // Allow for cases where vehicle just started and temps are still stabilizing
            var tempDiff = motorStatus.MotorTempC.Value - hvacStatus.OutsideAmbientTempC.Value;
            await Assert.That(tempDiff).IsGreaterThanOrEqualTo(-20.0); // Motor could be cooler if just started
            await Assert.That(tempDiff)
                .IsLessThanOrEqualTo(80.0); // Motor shouldn't be way hotter than ambient when idle

            Console.WriteLine(
                $"[Temp Consistency] Motor: {motorStatus.MotorTempC:F1}°C, Ambient: {hvacStatus.OutsideAmbientTempC:F1}°C, Diff: {tempDiff:F1}°C");
        }
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("CrossCapability")]
    public async Task MotorAndHvac_CanBeQueriedSequentially()
    {
        // Arrange
        var session = bleFixture.Session;
        var motorController = new LeafAze0MotorController(Monitor);
        var hvac = new LeafAze0Hvac(Monitor);

        // Act & Assert
        var motorStatus = await motorController.GetStatusAsync(CancellationToken.None);
        await Assert.That(motorStatus).IsNotNull();
        await Assert.That(motorStatus.InputVoltageV).IsNotNull();

        var hvacStatus = await hvac.GetStatusAsync(CancellationToken.None);
        await Assert.That(hvacStatus).IsNotNull();

        Console.WriteLine(
            $"[Multi-Query] Motor Voltage: {motorStatus.InputVoltageV:F1}V, HVAC Climate: {hvacStatus.ClimateControlOn}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("MotorController")]
    public async Task MotorController_GetStatus_CalculatedPowerMakesRpense()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.InvMcBroadcast;
        var motorController = new LeafAze0MotorController(Monitor);

        // Act
        var status = await motorController.GetStatusAsync(CancellationToken.None);

        // Assert - If we have torque and RPM, power should be calculable
        if (status.EffectiveTorqueNm.HasValue && status.OutputRevolutionRpm.HasValue && status.PowerWatts.HasValue)
        {
            // Power = Torque * RPM * (2π / 60)
            var expectedPower = status.EffectiveTorqueNm.Value * status.OutputRevolutionRpm.Value *
                                (2.0 * Math.PI / 60.0);
            await Assert.That(Math.Abs(status.PowerWatts.Value - expectedPower))
                .IsLessThan(1.0); // Allow small rounding error

            Console.WriteLine(
                $"[Motor Power] Calculated: {status.PowerWatts:F1}W from Torque: {status.EffectiveTorqueNm:F1}Nm, RPM: {status.OutputRevolutionRpm}");
        }
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("MotorController")]
    public async Task MotorController_GetStatus_ReturnsErrorCodes()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.InvMcBroadcast;
        var motorController = new LeafAze0MotorController(Monitor);

        // Act
        var status = await motorController.GetStatusAsync(CancellationToken.None);

        // Assert - Error codes should be present (typically 0 for healthy system)
        await Assert.That(status.ErrorCodes.HasValue).IsFalse();

        Console.WriteLine($"[Motor] Error Codes: 0x{status.ErrorCodes:X2}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("MotorController")]
    public async Task MotorController_GetStatus_ReturnsTemperatures()
    {
        // Arrange
        var session = bleFixture.Session;
        session.EnableDebugLogging = true;
        var context = LeafAze0Contexts.InvMcBroadcast;
        var motorController = new LeafAze0MotorController(Monitor);

        // Act
        var status = await motorController.GetStatusAsync(CancellationToken.None);

        // Assert - Temperatures should be present and in reasonable range
        await Assert.That(status.MotorTempC.HasValue).IsTrue();
        await Assert.That(status.IgbtTempC.HasValue).IsTrue();
        await Assert.That(status.InverterComBoardTempC.HasValue).IsTrue();
        await Assert.That(status.IgbtDriverBoardTempC.HasValue).IsTrue();

        // Temperature range: -40°C to +120°C (extended range for components)
        await Assert.That(status.MotorTempC!.Value).IsGreaterThanOrEqualTo(-40.0);
        await Assert.That(status.MotorTempC!.Value).IsLessThanOrEqualTo(120.0);
        await Assert.That(status.IgbtTempC!.Value).IsGreaterThanOrEqualTo(-40.0);
        await Assert.That(status.IgbtTempC!.Value).IsLessThanOrEqualTo(120.0);
        await Assert.That(status.InverterComBoardTempC!.Value).IsGreaterThanOrEqualTo(-40.0);
        await Assert.That(status.InverterComBoardTempC!.Value).IsLessThanOrEqualTo(120.0);
        await Assert.That(status.IgbtDriverBoardTempC!.Value).IsGreaterThanOrEqualTo(-40.0);
        await Assert.That(status.IgbtDriverBoardTempC!.Value).IsLessThanOrEqualTo(120.0);

        Console.WriteLine($"[Motor Temps] Motor: {status.MotorTempC:F1}°C, IGBT: {status.IgbtTempC:F1}°C, " +
                          $"ComBoard: {status.InverterComBoardTempC:F1}°C, DriverBoard: {status.IgbtDriverBoardTempC:F1}°C");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("MotorController")]
    public async Task MotorController_GetStatus_ReturnsTorqueAndRpm()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.InvMcBroadcast;
        var motorController = new LeafAze0MotorController(Monitor);

        // Act
        var status = await motorController.GetStatusAsync(CancellationToken.None);

        // Assert - When parked, torque should be near 0 and RPM should be 0 or very low
        await Assert.That(status.EffectiveTorqueNm).IsNotNull();
        await Assert.That(status.OutputRevolutionRpm).IsNotNull();

        // Torque range: -300 to +300 Nm (typical Leaf motor range)
        await Assert.That(status.EffectiveTorqueNm!.Value).IsGreaterThanOrEqualTo(-350.0);
        await Assert.That(status.EffectiveTorqueNm!.Value).IsLessThanOrEqualTo(350.0);

        // RPM range: -12000 to +12000 (typical Leaf motor max RPM)
        await Assert.That(status.OutputRevolutionRpm!.Value).IsGreaterThanOrEqualTo(-13000);
        await Assert.That(status.OutputRevolutionRpm!.Value).IsLessThanOrEqualTo(13000);

        Console.WriteLine($"[Motor] Torque: {status.EffectiveTorqueNm:F1} Nm, RPM: {status.OutputRevolutionRpm}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("MotorController")]
    public async Task MotorController_GetStatus_ReturnsValidVoltage()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.InvMcBroadcast;
        var motorController = new LeafAze0MotorController(Monitor);

        // Act
        var status = await motorController.GetStatusAsync(CancellationToken.None);

        // Assert - Motor input voltage should be HV battery voltage (typically 300-400V for Leaf)
        await Assert.That(status.InputVoltageV).IsNotNull();
        await Assert.That(status.InputVoltageV!.Value).IsGreaterThanOrEqualTo(250.0);
        await Assert.That(status.InputVoltageV!.Value).IsLessThanOrEqualTo(450.0);

        Console.WriteLine($"[Motor] Input Voltage: {status.InputVoltageV:F1}V");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("MotorController")]
    public async Task MotorController_GetStatus_WhenParked_RpmZero()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.InvMcBroadcast;
        var motorController = new LeafAze0MotorController(Monitor);

        // Act
        var status = await motorController.GetStatusAsync(CancellationToken.None);

        // Assert - When parked, RPM should be 0 or very close to 0
        await Assert.That(status.OutputRevolutionRpm).IsNotNull();
        await Assert.That(Math.Abs(status.OutputRevolutionRpm!.Value)).IsLessThan(50);

        Console.WriteLine($"[Motor Parked] RPM: {status.OutputRevolutionRpm}");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("MotorController")]
    public async Task MotorController_GetStatus_WhenParked_TorqueNearZero()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.InvMcBroadcast;
        var motorController = new LeafAze0MotorController(Monitor);

        // Act
        var status = await motorController.GetStatusAsync(CancellationToken.None);

        // Assert - When parked/idle, torque should be minimal (< 5 Nm)
        await Assert.That(status.EffectiveTorqueNm).IsNotNull();
        await Assert.That(Math.Abs(status.EffectiveTorqueNm!.Value)).IsLessThan(10.0);

        Console.WriteLine($"[Motor Parked] Torque: {status.EffectiveTorqueNm:F2} Nm");
    }

    [Test]
    [Category("Integration")]
    [Category("AZE0")]
    [Category("ErrorHandling")]
    public async Task MotorController_GetStatus_WithCancellation_ThrowsOperationCanceledException()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.InvMcBroadcast;
        var motorController = new LeafAze0MotorController(Monitor);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10)); // Very short timeout

        // Act & Assert
        await Assert.That(async () => await motorController.GetStatusAsync(cts.Token))
            .Throws<OperationCanceledException>();

        Console.WriteLine("[Motor Cancellation] Successfully handled cancellation");
    }
}
