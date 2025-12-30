using ObdInsight.Core.Transports.Tracing;

namespace OdbInsights.Tests.Transports;

/// <summary>
/// Tests for the JSONL transport session serializer.
/// </summary>
public class JsonLSerializerTests
{
    [Test]
    public async Task Serializer_LoadFromFile_ReturnsSession()
    {
        // Arrange
        var serializer = new JsonLTransportSessionSerializer();
        var session = CreateTestSession();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_session_{Guid.NewGuid():N}.jsonl");

        try
        {
            await serializer.SaveAsync(session, tempFile);

            // Act
            var loaded = await serializer.LoadAsync(tempFile);

            // Assert
            await Assert.That(loaded).IsNotNull();
            await Assert.That(loaded.Entries).Count().IsEqualTo(session.Entries.Count);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Test]
    public async Task Serializer_LoadMissingFile_ThrowsFileNotFound()
    {
        // Arrange
        var serializer = new JsonLTransportSessionSerializer();
        var nonExistentFile = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.jsonl");

        // Act & Assert
        await Assert.That(() => serializer.LoadAsync(nonExistentFile)!)
            .Throws<FileNotFoundException>();
    }

    [Test]
    public async Task Serializer_RoundTrip_PreservesEntryPayloads()
    {
        // Arrange
        var serializer = new JsonLTransportSessionSerializer();
        var originalSession = CreateTestSession();

        using var stream = new MemoryStream();

        // Act
        await serializer.SaveAsync(originalSession, stream);
        stream.Position = 0;
        var loadedSession = await serializer.LoadAsync(stream);

        // Assert
        for (int i = 0; i < originalSession.Entries.Count; i++)
        {
            await Assert.That(loadedSession.Entries[i].Payload)
                .IsEqualTo(originalSession.Entries[i].Payload);
            await Assert.That(loadedSession.Entries[i].Direction)
                .IsEqualTo(originalSession.Entries[i].Direction);
        }
    }

    [Test]
    public async Task Serializer_RoundTrip_PreservesSession()
    {
        // Arrange
        var serializer = new JsonLTransportSessionSerializer();
        var originalSession = CreateTestSession();

        using var stream = new MemoryStream();

        // Act
        await serializer.SaveAsync(originalSession, stream);
        stream.Position = 0;
        var loadedSession = await serializer.LoadAsync(stream);

        // Assert
        await Assert.That(loadedSession.SessionId).IsEqualTo(originalSession.SessionId);
        await Assert.That(loadedSession.Entries).Count().IsEqualTo(originalSession.Entries.Count);
        await Assert.That(loadedSession.Metadata.Protocol).IsEqualTo(originalSession.Metadata.Protocol);
    }
    [Test]
    public async Task Serializer_SaveToFile_CreatesValidFile()
    {
        // Arrange
        var serializer = new JsonLTransportSessionSerializer();
        var session = CreateTestSession();
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_session_{Guid.NewGuid():N}.jsonl");

        try
        {
            // Act
            await serializer.SaveAsync(session, tempFile);

            // Assert
            await Assert.That(File.Exists(tempFile)).IsTrue();

            var lines = await File.ReadAllLinesAsync(tempFile);
            await Assert.That(lines.Length).IsGreaterThan(1); // Header + entries

            // Verify it's valid JSONL
            foreach (var line in lines)
            {
                await Assert.That(() => System.Text.Json.JsonDocument.Parse(line))
                    .ThrowsNothing();
            }
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
    private static TransportSession CreateTestSession()
    {
        var now = DateTimeOffset.UtcNow;
        return new TransportSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            Metadata = new TraceSessionMetadata
            {
                StartedAt = now,
                EndedAt = now.AddMinutes(5),
                TransportType = "TestTransport",
                DeviceName = "Test Device",
                Protocol = "ISO 15765-4 CAN",
                AdapterVersion = "ELM327 v1.5",
                Description = "Test session"
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
