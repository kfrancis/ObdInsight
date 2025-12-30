using ObdInsight.Core.Transports.Tracing;

namespace OdbInsights.Tests.Transports;

/// <summary>
/// Tests for the transport tracing and replay infrastructure.
/// </summary>
public class TransportTracerTests
{
    [Test]
    public async Task TransportTracer_DoubleStart_ThrowsException()
    {
        // Arrange
        using var tracer = new TransportTracer();
        tracer.StartRecording();

        // Act & Assert
        await Assert.That(() => tracer.StartRecording())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TransportTracer_EntryRecordedEvent_IsFired()
    {
        // Arrange
        using var tracer = new TransportTracer();
        var recordedEntries = new List<TraceEntry>();
        tracer.EntryRecorded += (_, e) => recordedEntries.Add(e);
        tracer.StartRecording();

        // Act
        tracer.RecordTx("ATZ\r");
        tracer.RecordRx("OK\r\n>");
        tracer.StopRecording();

        // Assert
        await Assert.That(recordedEntries).Count().IsEqualTo(2);
        await Assert.That(recordedEntries[0].Direction).IsEqualTo(TraceDirection.Tx);
        await Assert.That(recordedEntries[1].Direction).IsEqualTo(TraceDirection.Rx);
    }

    [Test]
    public async Task TransportTracer_NotRecording_DoesNotAddEntries()
    {
        // Arrange
        using var tracer = new TransportTracer();
        // Note: Not starting recording

        // Act
        tracer.RecordTx("ATZ\r");
        tracer.RecordRx("OK\r\n>");

        // Assert
        await Assert.That(tracer.CurrentSession).IsNull();
    }

    [Test]
    public async Task TransportTracer_RecordsEntries_InOrder()
    {
        // Arrange
        using var tracer = new TransportTracer();
        tracer.StartRecording();

        // Act
        tracer.RecordTx("ATZ\r");
        await Task.Delay(10); // Small delay for timing difference
        tracer.RecordRx("ELM327 v1.5\r\n>");
        tracer.RecordTx("ATE0\r");
        tracer.RecordRx("OK\r\n>");

        var session = tracer.StopRecording();

        // Assert
        await Assert.That(session.Entries).Count().IsEqualTo(4);
        await Assert.That(session.Entries[0].Direction).IsEqualTo(TraceDirection.Tx);
        await Assert.That(session.Entries[0].Payload).IsEqualTo("ATZ\r");
        await Assert.That(session.Entries[1].Direction).IsEqualTo(TraceDirection.Rx);
        await Assert.That(session.Entries[2].Direction).IsEqualTo(TraceDirection.Tx);
        await Assert.That(session.Entries[3].Direction).IsEqualTo(TraceDirection.Rx);
    }

    [Test]
    public async Task TransportTracer_SequenceNumbers_AreMonotonic()
    {
        // Arrange
        using var tracer = new TransportTracer();
        tracer.StartRecording();

        // Act
        for (int i = 0; i < 10; i++)
        {
            tracer.RecordTx($"CMD{i}\r");
            tracer.RecordRx($"RSP{i}\r\n>");
        }

        var session = tracer.StopRecording();

        // Assert
        for (int i = 0; i < session.Entries.Count; i++)
        {
            await Assert.That(session.Entries[i].SequenceNumber).IsEqualTo(i);
        }
    }

    [Test]
    public async Task TransportTracer_StopWithoutStart_ThrowsException()
    {
        // Arrange
        using var tracer = new TransportTracer();

        // Act & Assert
        await Assert.That(() => tracer.StopRecording())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task TransportTracer_UpdateMetadata_ModifiesSession()
    {
        // Arrange
        using var tracer = new TransportTracer();
        tracer.StartRecording(new TraceSessionMetadata
        {
            StartedAt = DateTimeOffset.UtcNow
        });

        // Act
        tracer.UpdateMetadata(m => m with
        {
            Protocol = "ISO 15765-4 CAN",
            AdapterVersion = "ELM327 v1.5"
        });

        var session = tracer.StopRecording();

        // Assert
        await Assert.That(session.Metadata.Protocol).IsEqualTo("ISO 15765-4 CAN");
        await Assert.That(session.Metadata.AdapterVersion).IsEqualTo("ELM327 v1.5");
    }
}