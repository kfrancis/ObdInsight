using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using OdbTestApp.Tests.Fixtures;

namespace OdbTestApp.Tests.NissanLeaf.AZE0.Integration;

/// <summary>
/// Diagnostic tests for determining which Nissan Leaf AZE0 ECUs require session activation.
/// These tests compare frame acquisition with and without session activation to determine
/// the optimal configuration for each broadcast context.
///
/// IMPORTANT: These tests should be run with the vehicle in READY mode for best results.
/// </summary>
[ObdInsight.IntegrationTests.RequiresLeafHardware]
[ClassDataSource<BleSessionFixture>(Shared = SharedType.Keyed)]
public class LeafAze0SessionActivationDiagnosticsTests(BleSessionFixture bleFixture)
{
    /// <summary>
    /// Comprehensive diagnostic test that evaluates all broadcast contexts to determine
    /// which ones require session activation. Run this test to generate recommendations
    /// for RequiresSessionActivation flag.
    /// </summary>
    [Test]
    [Category("Diagnostic")]
    [Category("AZE0")]
    [Category("SessionActivation")]
    public async Task DiagnoseAllContexts_SessionActivationRequirements()
    {
        var session = bleFixture.Session;
        var testDuration = TimeSpan.FromSeconds(3);

        var contextsToTest = new[]
        {
            ("Steering", LeafAze0Contexts.SteeringBroadcast, new[] { 0x002, 0x300 }),
            ("HVAC", LeafAze0Contexts.HvacBroadcast, new[] { 0x54A, 0x54B, 0x54C, 0x54F }),
            ("MotorController", LeafAze0Contexts.InvMcBroadcast, new[] { 0x1DA, 0x55A }),
            ("ABS", LeafAze0Contexts.AbsBroadcast, new[] { 0x130, 0x245, 0x284, 0x285, 0x292, 0x354 }),
            ("Brake", LeafAze0Contexts.BrakeBroadcast, new[] { 0x1CA }),
            ("BodyControl", LeafAze0Contexts.BcmBroadcast, new[] { 0x60D, 0x625 }),
            ("VCM_EvCan", LeafAze0Contexts.VcmEvCanBroadcast, new[] { 0x11A, 0x1D4, 0x1F2, 0x284, 0x5A9 }),
            ("VCM_CarCan", LeafAze0Contexts.VcmCarCanBroadcast, new[] { 0x174, 0x176, 0x180, 0x260, 0x421, 0x50A, 0x50D, 0x510 })
        };

        Console.WriteLine("\n╔═══════════════════════════════════════════════════════════════");
        Console.WriteLine("║ SESSION ACTIVATION DIAGNOSTIC REPORT");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════════\n");

        foreach (var (name, context, expectedIds) in contextsToTest)
        {
            Console.WriteLine($"\n┌─ Testing: {name} ─────────────────────────────");
            Console.WriteLine($"│ Context: {context.Name}");
            Console.WriteLine($"│ Mode: {context.CommunicationMode}");
            Console.WriteLine($"│ Expected IDs: {string.Join(", ", expectedIds.Select(id => $"0x{id:X3}"))}");
            Console.WriteLine($"│ Current RequiresSessionActivation: {context.RequiresSessionActivation}");
            Console.WriteLine("│");

            try
            {
                // Test 1: Passive monitoring only (no session activation)
                Console.WriteLine("│ [Test 1] Passive monitoring (no session)...");
                var passiveFrames = await TestPassiveMonitoringAsync(session, context, testDuration);
                var passiveIds = passiveFrames.Select(f => f.CanId).Distinct().ToList();
                var passiveExpectedCount = expectedIds.Count(id => passiveIds.Contains(id));

                Console.WriteLine($"│   Frames received: {passiveFrames.Count}");
                Console.WriteLine($"│   Unique IDs: {string.Join(", ", passiveIds.Select(id => $"0x{id:X3}"))}");
                Console.WriteLine($"│   Expected IDs found: {passiveExpectedCount}/{expectedIds.Length}");

                // Test 2: With session activation
                Console.WriteLine("│");
                Console.WriteLine("│ [Test 2] Active monitoring (with session)...");

                // Create a test context with session activation enabled
                var activeContext = new EcuContext
                {
                    Name = context.Name + " (Session Test)",
                    TxHeader = context.TxHeader,
                    RxFilter = context.RxFilter,
                    FlowControlHeader = context.FlowControlHeader,
                    CommunicationMode = context.CommunicationMode,
                    MonitoringCommand = context.MonitoringCommand,
                    ExpectedCanIds = context.ExpectedCanIds,
                    CanFilterMask = context.CanFilterMask,
                    CanFilterPattern = context.CanFilterPattern,
                    EnableHeaders = context.EnableHeaders,
                    EnableAutoFormatting = context.EnableAutoFormatting,
                    SessionActivationCommand = "1081", // Default session with suppress
                    RequiresSessionActivation = true
                };

                var sessionActivated = await session.ActivateSessionAsync(activeContext, CancellationToken.None);
                Console.WriteLine($"│   Session activation result: {(sessionActivated ? "SUCCESS" : "FAILED")}");

                var activeFrames = await TestPassiveMonitoringAsync(session, context, testDuration);
                var activeIds = activeFrames.Select(f => f.CanId).Distinct().ToList();
                var activeExpectedCount = expectedIds.Count(id => activeIds.Contains(id));

                Console.WriteLine($"│   Frames received: {activeFrames.Count}");
                Console.WriteLine($"│   Unique IDs: {string.Join(", ", activeIds.Select(id => $"0x{id:X3}"))}");
                Console.WriteLine($"│   Expected IDs found: {activeExpectedCount}/{expectedIds.Length}");

                // Analysis
                Console.WriteLine("│");
                Console.WriteLine("│ [Analysis]");
                var passiveWorked = passiveExpectedCount == expectedIds.Length;
                var activeWorked = activeExpectedCount == expectedIds.Length;
                var improvement = activeExpectedCount - passiveExpectedCount;

                Console.WriteLine($"│   Passive complete: {passiveWorked}");
                Console.WriteLine($"│   Active complete: {activeWorked}");
                Console.WriteLine($"│   Improvement with session: {improvement} additional IDs");

                // Recommendation
                Console.WriteLine("│");
                if (passiveWorked)
                {
                    Console.WriteLine("│ ✓ RECOMMENDATION: RequiresSessionActivation = FALSE");
                    Console.WriteLine("│   All expected frames received without session activation.");
                }
                else if (activeWorked)
                {
                    Console.WriteLine("│ ⚠ RECOMMENDATION: RequiresSessionActivation = TRUE");
                    Console.WriteLine("│   Session activation required to receive all expected frames.");
                }
                else if (improvement > 0)
                {
                    Console.WriteLine("│ ⚠ RECOMMENDATION: RequiresSessionActivation = TRUE (partial)");
                    Console.WriteLine("│   Session activation improves frame acquisition but still incomplete.");
                    Console.WriteLine("│   May need different session command or vehicle must be in READY mode.");
                }
                else
                {
                    Console.WriteLine("│ ✗ WARNING: Neither approach received all expected frames.");
                    Console.WriteLine("│   Possible causes:");
                    Console.WriteLine("│   - Vehicle not in READY mode");
                    Console.WriteLine("│   - Wrong expected IDs for this variant");
                    Console.WriteLine("│   - Different session command needed");
                }

                Console.WriteLine("└────────────────────────────────────────────────");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"│ ✗ ERROR: {ex.Message}");
                Console.WriteLine("└────────────────────────────────────────────────");
            }

            // Small delay between tests to allow adapter to settle
            await Task.Delay(500);
        }

        Console.WriteLine("\n╚═══════════════════════════════════════════════════════════════\n");
    }

    /// <summary>
    /// Tests that filter state is properly reset between context switches.
    /// This validates the ResetAdapterStateAsync fix for filter pollution.
    /// </summary>
    [Test]
    [Category("Diagnostic")]
    [Category("AZE0")]
    [Category("FilterState")]
    public async Task DiagnoseFilterStateReset_BetweenContextSwitches()
    {
        var session = bleFixture.Session;

        Console.WriteLine("\n╔═══════════════════════════════════════════════════════════════");
        Console.WriteLine("║ FILTER STATE RESET DIAGNOSTIC");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════════\n");

        // Test 1: Configure for BMS (specific filter: 0x7BB)
        Console.WriteLine("│ [Test 1] Configuring for BMS query (RxFilter=7BB)...");
        var bmsContext = LeafAze0Contexts.LbcBms;
        await session.SetEcuContextAsync(bmsContext, CancellationToken.None);

        try
        {
            var bmsResponse = await session.QueryAsync("2101", CancellationToken.None);
            Console.WriteLine($"│   BMS query result: {bmsResponse.Length} response lines");
            Console.WriteLine($"│   Response: {string.Join(", ", bmsResponse.Take(2))}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"│   BMS query failed: {ex.Message}");
        }

        // Test 2: Switch to steering monitoring (different IDs: 0x002, 0x300)
        // If filter state wasn't reset, we'd still be filtering for 0x7BB
        Console.WriteLine("│");
        Console.WriteLine("│ [Test 2] Switching to Steering broadcast monitoring...");
        Console.WriteLine("│   Expected IDs: 0x002, 0x300 (NOT 0x7BB)");

        var steeringContext = LeafAze0Contexts.SteeringBroadcast;

        // If context requires session activation, do it
        if (steeringContext.RequiresSessionActivation)
        {
            var activated = await session.ActivateSessionAsync(steeringContext, CancellationToken.None);
            Console.WriteLine($"│   Session activation: {(activated ? "SUCCESS" : "FAILED")}");
        }

        await session.EnterMonitoringModeAsync(steeringContext, CancellationToken.None);
        var frames = new List<RawCanFrame>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var frame in session.MonitorFramesAsync(cts.Token))
            {
                frames.Add(frame);
                if (frames.Count >= 20) break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await session.ExitMonitoringModeAsync(CancellationToken.None);
        }

        var uniqueIds = frames.Select(f => f.CanId).Distinct().OrderBy(id => id).ToList();
        Console.WriteLine($"│   Frames received: {frames.Count}");
        Console.WriteLine($"│   Unique IDs: {string.Join(", ", uniqueIds.Select(id => $"0x{id:X3}"))}");

        // Analysis
        Console.WriteLine("│");
        Console.WriteLine("│ [Analysis]");
        var hasSteeringIds = uniqueIds.Contains(0x002) || uniqueIds.Contains(0x300);
        var hasBmsId = uniqueIds.Contains(0x7BB);

        if (hasSteeringIds && !hasBmsId)
        {
            Console.WriteLine("│ ✓ SUCCESS: Filter state properly reset");
            Console.WriteLine("│   Received expected steering IDs, no BMS ID pollution");
        }
        else if (hasBmsId)
        {
            Console.WriteLine("│ ✗ FAILURE: Filter state NOT reset!");
            Console.WriteLine("│   Still receiving BMS ID (0x7BB) after context switch");
            Console.WriteLine("│   This indicates the ATAR/ATCEA reset is not working");
        }
        else if (!hasSteeringIds)
        {
            Console.WriteLine("│ ⚠ WARNING: No steering frames received");
            Console.WriteLine("│   Filter may be reset, but steering ECU not responding");
            Console.WriteLine("│   Try with vehicle in READY mode or with session activation");
        }

        Console.WriteLine("└────────────────────────────────────────────────");
        Console.WriteLine("\n╚═══════════════════════════════════════════════════════════════\n");
    }

    /// <summary>
    /// Focused diagnostic for steering ECU session activation.
    /// Tests multiple session activation commands to find the best one.
    /// </summary>
    [Test]
    [Category("Diagnostic")]
    [Category("AZE0")]
    [Category("Steering")]
    public async Task DiagnoseSteeringEcu_SessionActivationCommands()
    {
        var session = bleFixture.Session;
        var testDuration = TimeSpan.FromSeconds(2);

        Console.WriteLine("\n╔═══════════════════════════════════════════════════════════════");
        Console.WriteLine("║ STEERING ECU SESSION ACTIVATION DIAGNOSTIC");
        Console.WriteLine("╠═══════════════════════════════════════════════════════════════\n");

        var sessionCommands = new[]
        {
            ("No Session", null as string),
            ("1001", "1001"), // Default session
            ("1081", "1081"), // Default session with suppress-positive-response
            ("1003", "1003"), // Extended diagnostic session
            ("10C0", "10C0")  // Nissan OEM session
        };

        var baseContext = LeafAze0Contexts.SteeringBroadcast;
        var expectedIds = new[] { 0x002, 0x300 };

        foreach (var (name, command) in sessionCommands)
        {
            Console.WriteLine($"│ Testing: {name}");

            var testContext = new EcuContext
            {
                Name = $"Steering ({name})",
                TxHeader = "742", // EPS ECU
                RxFilter = "",
                FlowControlHeader = "742",
                CommunicationMode = EcuCommunicationMode.ActiveMonitoring,
                SessionActivationCommand = command,
                RequiresSessionActivation = command != null,
                MonitoringCommand = "AT MA",
                ExpectedCanIds = ["002", "300"],
                EnableHeaders = true,
                EnableAutoFormatting = false
            };

            try
            {
                if (command != null)
                {
                    var activated = await session.ActivateSessionAsync(testContext, CancellationToken.None);
                    Console.WriteLine($"│   Session command: {command}");
                    Console.WriteLine($"│   Activation result: {(activated ? "SUCCESS" : "FAILED")}");
                }

                var frames = await TestPassiveMonitoringAsync(session, testContext, testDuration);
                var uniqueIds = frames.Select(f => f.CanId).Distinct().ToList();
                var expectedFound = expectedIds.Count(id => uniqueIds.Contains(id));

                Console.WriteLine($"│   Frames: {frames.Count}, IDs: {string.Join(", ", uniqueIds.Select(id => $"0x{id:X3}"))}");
                Console.WriteLine($"│   Expected found: {expectedFound}/{expectedIds.Length}");

                if (expectedFound == expectedIds.Length)
                {
                    Console.WriteLine($"│ ✓ {name} works!");
                }
                Console.WriteLine("│");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"│ ✗ Error: {ex.Message}");
                Console.WriteLine("│");
            }

            await Task.Delay(500);
        }

        Console.WriteLine("╚═══════════════════════════════════════════════════════════════\n");
    }

    /// <summary>
    /// Helper method to test passive monitoring and collect frames.
    /// </summary>
    private static async Task<List<RawCanFrame>> TestPassiveMonitoringAsync(
        IElmSession session,
        EcuContext context,
        TimeSpan duration)
    {
        var frames = new List<RawCanFrame>();

        await session.EnterMonitoringModeAsync(context, CancellationToken.None);

        using var cts = new CancellationTokenSource(duration);
        try
        {
            await foreach (var frame in session.MonitorFramesAsync(cts.Token))
            {
                frames.Add(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected timeout
        }
        finally
        {
            await session.ExitMonitoringModeAsync(CancellationToken.None);
        }

        return frames;
    }
}
