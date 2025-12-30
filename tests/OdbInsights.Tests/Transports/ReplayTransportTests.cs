using ObdInsight.Core.Transports.Tracing;

namespace OdbInsights.Tests.Transports;

/// <summary>
/// Tests for the replay transport - verifies deterministic playback.
/// </summary>
public class ReplayTransportTests
{
    [Test]
    public async Task ReplayTransport_ConnectAsync_ReturnsTrue()
    {
        // Arrange
        var session = CreateSimpleSession();
        using var transport = new ReplayTransport(session);

        // Act
        var result = await transport.ConnectAsync();

        // Assert
        await Assert.That(result).IsTrue();
        await Assert.That(transport.IsConnected).IsTrue();
    }

    [Test]
    public async Task ReplayTransport_DisconnectAsync_SetsDisconnected()
    {
        // Arrange
        var session = CreateSimpleSession();
        using var transport = new ReplayTransport(session);
        await transport.ConnectAsync();

        // Act
        await transport.DisconnectAsync();

        // Assert
        await Assert.That(transport.IsConnected).IsFalse();
    }

    [Test]
    public async Task ReplayTransport_WriteAndRead_PlaysBackRecordedData()
    {
        // Arrange
        var session = CreateSimpleSession();
        using var transport = new ReplayTransport(session, new ReplayOptions { MatchingMode = ReplayMatchingMode.Any });
        await transport.ConnectAsync();

        // Act
        await transport.WriteAsync("ATZ\r");
        var response = await transport.ReadUntilAsync(">", TimeSpan.FromSeconds(1));

        // Assert
        await Assert.That(response).Contains("ELM327");
    }

    [Test]
    public async Task ReplayTransport_ExactMatching_RequiresExactCommand()
    {
        // Arrange
        var session = CreateSimpleSession();
        using var transport = new ReplayTransport(session, new ReplayOptions
        {
            MatchingMode = ReplayMatchingMode.Exact,
            StrictMode = true
        });
        await transport.ConnectAsync();

        // Act - send wrong command
        var act = async () =>
        {
            await transport.WriteAsync("ATE0\r"); // Session expects "ATZ\r"
        };

        // Assert
        await Assert.That(act).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ReplayTransport_NonStrictMode_LogsMismatchAndContinues()
    {
        // Arrange
        var session = CreateSimpleSession();
        using var transport = new ReplayTransport(session, new ReplayOptions
        {
            MatchingMode = ReplayMatchingMode.Exact,
            StrictMode = false
        });
        await transport.ConnectAsync();

        // Act
        await transport.WriteAsync("WRONG_CMD\r");

        // Assert
        await Assert.That(transport.UnmatchedCommands).Contains("WRONG_CMD\r");
    }

    [Test]
    public async Task ReplayTransport_MultipleExchanges_PlaysBackInSequence()
    {
        // Arrange
        var session = CreateMultiExchangeSession();
        using var transport = new ReplayTransport(session, new ReplayOptions { MatchingMode = ReplayMatchingMode.Any });
        await transport.ConnectAsync();

        // Act
        await transport.WriteAsync("ATZ\r");
        var response1 = await transport.ReadUntilAsync(">", TimeSpan.FromSeconds(1));

        await transport.WriteAsync("0100\r");
        var response2 = await transport.ReadUntilAsync(">", TimeSpan.FromSeconds(1));

        // Assert
        await Assert.That(response1).Contains("ELM327");
        await Assert.That(response2).Contains("4100");
    }

    [Test]
    public async Task ReplayTransport_Reset_AllowsReplayAgain()
    {
        // Arrange
        var session = CreateSimpleSession();
        using var transport = new ReplayTransport(session, new ReplayOptions { MatchingMode = ReplayMatchingMode.Any });
        await transport.ConnectAsync();

        // First pass
        await transport.WriteAsync("ATZ\r");
        var response1 = await transport.ReadUntilAsync(">", TimeSpan.FromSeconds(1));

        // Act - reset
        transport.Reset();

        // Second pass
        await transport.WriteAsync("ATZ\r");
        var response2 = await transport.ReadUntilAsync(">", TimeSpan.FromSeconds(1));

        // Assert
        await Assert.That(response1).IsEqualTo(response2);
    }

    [Test]
    public async Task ReplayTransport_DataEvents_AreFired()
    {
        // Arrange
        var session = CreateSimpleSession();
        using var transport = new ReplayTransport(session, new ReplayOptions { MatchingMode = ReplayMatchingMode.Any });

        string? sentData = null;
        string? receivedData = null;
        transport.DataSent += (_, data) => sentData = data;
        transport.DataReceived += (_, data) => receivedData = data;

        await transport.ConnectAsync();

        // Act
        await transport.WriteAsync("ATZ\r");
        await transport.ReadUntilAsync(">", TimeSpan.FromSeconds(1));

        // Assert
        await Assert.That(sentData).IsEqualTo("ATZ\r");
        await Assert.That(receivedData).IsNotNull();
    }

    [Test]
    public async Task ReplayTransport_Name_IncludesSessionInfo()
    {
        // Arrange
        var session = CreateSimpleSession();
        using var transport = new ReplayTransport(session);

        // Assert
        await Assert.That(transport.Name).StartsWith("Replay:");
    }

    [Test]
    public async Task ReplayTransport_IsComplete_TrueWhenAllEntriesProcessed()
    {
        // Arrange
        var session = CreateSimpleSession();
        using var transport = new ReplayTransport(session, new ReplayOptions { MatchingMode = ReplayMatchingMode.Any });
        await transport.ConnectAsync();

        // Act
        await transport.WriteAsync("ATZ\r");
        await transport.ReadUntilAsync(">", TimeSpan.FromSeconds(1));

        // Assert
        await Assert.That(transport.IsComplete).IsTrue();
    }

    private static TransportSession CreateSimpleSession()
    {
        var now = DateTimeOffset.UtcNow;
        return new TransportSession
        {
            SessionId = "test-session",
            Metadata = new TraceSessionMetadata
            {
                StartedAt = now,
                DeviceName = "Test Adapter"
            },
            Entries =
            [
                new TraceEntry
                {
                    Timestamp = now,
                    Direction = TraceDirection.Tx,
                    Payload = "ATZ\r",
                    ElapsedTime = TimeSpan.Zero,
                    SequenceNumber = 0
                },
                new TraceEntry
                {
                    Timestamp = now.AddMilliseconds(100),
                    Direction = TraceDirection.Rx,
                    Payload = "ELM327 v1.5\r\n>",
                    ElapsedTime = TimeSpan.FromMilliseconds(100),
                    SequenceNumber = 1
                }
            ]
        };
    }

    private static TransportSession CreateMultiExchangeSession()
    {
        var now = DateTimeOffset.UtcNow;
        return new TransportSession
        {
            SessionId = "multi-exchange-session",
            Metadata = new TraceSessionMetadata
            {
                StartedAt = now,
                DeviceName = "Test Adapter"
            },
            Entries =
            [
                new TraceEntry
                {
                    Timestamp = now,
                    Direction = TraceDirection.Tx,
                    Payload = "ATZ\r",
                    ElapsedTime = TimeSpan.Zero,
                    SequenceNumber = 0
                },
                new TraceEntry
                {
                    Timestamp = now.AddMilliseconds(100),
                    Direction = TraceDirection.Rx,
                    Payload = "ELM327 v1.5\r\n>",
                    ElapsedTime = TimeSpan.FromMilliseconds(100),
                    SequenceNumber = 1
                },
                new TraceEntry
                {
                    Timestamp = now.AddMilliseconds(200),
                    Direction = TraceDirection.Tx,
                    Payload = "0100\r",
                    ElapsedTime = TimeSpan.FromMilliseconds(200),
                    SequenceNumber = 2
                },
                new TraceEntry
                {
                    Timestamp = now.AddMilliseconds(400),
                    Direction = TraceDirection.Rx,
                    Payload = "4100BE1FA813\r\n>",
                    ElapsedTime = TimeSpan.FromMilliseconds(400),
                    SequenceNumber = 3
                }
            ]
        };
    }
}
