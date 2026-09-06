using Microsoft.Extensions.Logging;
using ObdInsight.Application;

namespace ObdInsight.Tests.Telemetry;

public class SmokeConnectionLoggerTests
{
    [Test]
    public async Task FailureEvidence_DoesNotFormatOrLeakArbitraryMessages()
    {
        var logger = new SmokeConnectionLogger();
        logger.Log(LogLevel.Warning, new EventId(4104),
            new Dictionary<string, object?> { ["Phase"] = "transport-open", ["Vin"] = "SECRET-VIN" },
            new IOException("Device not reachable. SECRET-ADDRESS", new IOException("SECRET-PAYLOAD")),
            (_, _) => throw new InvalidOperationException("Formatter must not be called"));
        await Assert.That(logger.TryRead(out var item)).IsTrue();
        await Assert.That(item!.Detail).Contains("transport-open");
        await Assert.That(item.Detail).Contains("device-unreachable");
        await Assert.That(item.Detail).Contains("0x");
        await Assert.That(item.Detail).DoesNotContain("SECRET");
    }

    [Test]
    public async Task UnknownEventsAndUntrustedPhasesAreNotEmitted()
    {
        var logger = new SmokeConnectionLogger();
        logger.LogInformation("SECRET");
        await Assert.That(logger.TryRead(out _)).IsFalse();
        logger.LogWarning(new EventId(4104), "Failure {Phase}", "SECRET");
        await Assert.That(logger.TryRead(out var item)).IsTrue();
        await Assert.That(item!.Detail).IsEqualTo("attempt-failed");
    }

    [Test]
    public async Task UndrainedDiagnosticsAreBounded()
    {
        var logger = new SmokeConnectionLogger();
        for (var i = 0; i < 1000; i++) logger.LogDebug(new EventId(4100), "Opening");
        var count = 0;
        while (logger.TryRead(out _)) count++;
        await Assert.That(count).IsEqualTo(128);
    }
}
