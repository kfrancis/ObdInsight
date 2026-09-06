using System.Text.Json;
using System.Text.Json.Serialization;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Communication.Slcan;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Telemetry;

namespace ObdInsight.Application;

// This is an application evidence format, not a new library public contract. Never serialize
// detection, snapshots, exceptions or raw adapter output directly: they can contain identifiers.
internal sealed record SmokeEvidence(string Stage)
{
    public int SchemaVersion => 1;
    public DateTimeOffset WrittenUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? Detail { get; init; }
    public long? Generation { get; init; }
    public bool? VinRead { get; init; }
    public DateTimeOffset? TimestampUtc { get; init; }
    public SmokeMeasurement[]? Measurements { get; init; }
    public Dictionary<int, long>? FrameCounts { get; init; }
    public long? Count { get; init; }
    public string? StoredDtcStatus { get; init; }
    public string? PendingDtcStatus { get; init; }
}

internal sealed record SmokeMeasurement(TelemetrySignal Signal, TelemetryValue Value);

[JsonSourceGenerationOptions(UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SmokeEvidence))]
internal partial class SmokeJsonContext : JsonSerializerContext;

internal sealed class SmokeOutputException(Exception inner) : Exception("Evidence output failed.", inner);

internal sealed class HardwareSmokeRunner(TextWriter output)
{
    private long _nonemptySamples;
    private string _stage = "open";
    private readonly SmokeConnectionLogger _connectionLogger = new();
    private static TelemetrySessionOptions TelemetryOptions => new() { CacheReadTimeout = TimeSpan.FromSeconds(5) };

    private async Task WriteAsync(SmokeEvidence evidence)
    {
        try
        {
            while (_connectionLogger.TryRead(out var diagnostic))
                await output.WriteLineAsync(JsonSerializer.Serialize(diagnostic!, SmokeJsonContext.Default.SmokeEvidence));
            await output.WriteLineAsync(JsonSerializer.Serialize(evidence, SmokeJsonContext.Default.SmokeEvidence));
            await output.FlushAsync();
        }
        catch (Exception ex) { throw new SmokeOutputException(ex); }
    }

    public async Task<int> RunAsync(SmokeOptions options, Func<IElmTransport> factory, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.Timeout);
        try
        {
            await WriteAsync(new("start") { Detail = options.Mode });
            if (options.Mode == "slcan") await SlcanAsync(options, factory, deadline.Token);
            else await ElmAsync(options, factory, deadline.Token);
            await WriteAsync(new("shutdown-complete")); // All owned resources already joined.
            var hasEvidence = _nonemptySamples > 0;
            await WriteAsync(new("result") { Detail = hasEvidence ? "evidence-collected" : "no-measurements", Count = _nonemptySamples });
            return hasEvidence ? 0 : 2;
        }
        catch (Exception ex)
        {
            await WriteAsync(new("failed") { Detail = $"{_stage}:{(ct.IsCancellationRequested ? "cancelled" : deadline.IsCancellationRequested ? "deadline" : ex.GetType().Name)}" });
            return 1;
        }
    }

    private async Task SnapshotAsync(ITelemetrySession telemetry, string stage, CancellationToken ct)
    {
        _stage = stage;
        var snapshot = await telemetry.GetSnapshotAsync(ct);
        await WriteAsync(new(stage)
        {
            Generation = snapshot.ConnectionGeneration, TimestampUtc = snapshot.TimestampUtc,
            VinRead = snapshot.Vin is not null,
            StoredDtcStatus = snapshot.DiagnosticTroubleCodes?.Stored.Status.ToString(),
            PendingDtcStatus = snapshot.DiagnosticTroubleCodes?.Pending.Status.ToString(),
            Measurements = snapshot.Measurements.Select(p => new SmokeMeasurement(p.Key, p.Value)).ToArray()
        });
    }

    private async Task RecordAsync(ITelemetrySession telemetry, CancellationToken ct)
    {
        _stage = "record";
        await telemetry.StartAsync(ct);
        await foreach (var batch in telemetry.Batches(ct))
        {
            _nonemptySamples += batch.Samples.Count(s => !s.Value.IsEmpty);
            await WriteAsync(new("batch")
            {
                Generation = batch.ConnectionGeneration, TimestampUtc = batch.TimestampUtc,
                Measurements = batch.Samples.Select(s => new SmokeMeasurement(s.Signal, s.Value)).ToArray()
            });
        }
        ct.ThrowIfCancellationRequested();
        throw new IOException("Telemetry ended before the recording window.");
    }

    private async Task ElmAsync(SmokeOptions options, Func<IElmTransport> factory, CancellationToken ct)
    {
        await using var owner = new VehicleConnection(factory, [new NissanLeaf()], telemetryOptions: TelemetryOptions, logger: _connectionLogger);
        var generation = await owner.OpenAsync(ct);
        await ReadyAsync(generation);
        await SnapshotAsync(generation.Telemetry, "pre-snapshot", ct); // Never replay a failed snapshot.
        _stage = "start-telemetry";
        await generation.Telemetry.StartAsync(ct);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(options.Duration); // Fixed window includes reconnect time.
        try
        {
            while (true)
            {
                try { await RecordAsync(generation.Telemetry, window.Token); }
                catch (Exception ex) when (!window.IsCancellationRequested && generation.Ended.IsCompleted &&
                    ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                {
                    await WriteAsync(new("generation-ended") { Generation = generation.Number });
                    _stage = "reconnect";
                    generation = await owner.WaitForReadyAsync(generation.Number, window.Token);
                    await ReadyAsync(generation); // Fresh stream, no retained capabilities or cached values.
                }
            }
        }
        catch (OperationCanceledException) when (window.IsCancellationRequested && !ct.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();
        _stage = "stop";
        await generation.Telemetry.StopAsync();
        if (generation.Ended.IsCompleted) throw new IOException("No live generation for post-snapshot.");
        await SnapshotAsync(generation.Telemetry, "post-snapshot", ct);
        _stage = "dispose";
    }

    private Task ReadyAsync(VehicleConnectionGeneration generation) => WriteAsync(new("generation-ready")
    {
        Generation = generation.Number, Detail = generation.Detection.Status.ToString(),
        VinRead = generation.Detection.Vin is not null
    });

    private async Task SlcanAsync(SmokeOptions options, Func<IElmTransport> factory, CancellationToken ct)
    {
        await using var transport = factory();
        await transport.OpenAsync(ct);
        await using var source = new SlcanFrameSource(transport, SlcanProtocol.BitrateCommand(options.Bitrate), listenOnly: true);
        await using var commands = new LeafAze0CommandSet(source);
        var monitor = commands.Monitor;
        await monitor.StartAsync(ct);
        await WriteAsync(new("slcan-ready") { Detail = $"{source.Dialect};configured-Leaf-AZE0;VIN/active-UDS/reconnect-unsupported" });
        await using var telemetry = TelemetrySession.Create(commands, options: TelemetryOptions);
        using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var counts = new Dictionary<int, long>();
        var capture = CaptureAsync();
        try
        {
            await SnapshotAsync(telemetry, "pre-snapshot", ct);
            _stage = "start-telemetry";
            await telemetry.StartAsync(ct);
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(options.Duration);
            var recording = RecordAsync(telemetry, window.Token);
            try
            {
                if (await Task.WhenAny(recording, capture) == capture)
                {
                    await capture;
                    throw new IOException("Raw CAN stream ended unexpectedly.");
                }
                await recording;
            }
            catch (OperationCanceledException) when (window.IsCancellationRequested && !ct.IsCancellationRequested) { }
            finally
            {
                await window.CancelAsync();
                try { await recording; }
                catch (OperationCanceledException) when (window.IsCancellationRequested) { }
            }
            ct.ThrowIfCancellationRequested();
            await telemetry.StopAsync();
            await SnapshotAsync(telemetry, "post-snapshot", ct);
        }
        finally
        {
            await captureCancellation.CancelAsync();
            try { await capture; }
            catch (OperationCanceledException) when (captureCancellation.IsCancellationRequested) { }
        }
        _stage = "stop";
        await monitor.StopAsync(CancellationToken.None);
        await WriteAsync(new("frame-coverage") { FrameCounts = counts, Detail = "Observed subscriber counts, not lossless wire counts; payloads omitted." });
        await WriteAsync(new("slcan-stop") { Detail = $"{source.LastEndReason};CAN-FD-skipped={source.CanFdFrameCount};non-frame-lines={source.NonFrameLineCount}" });
        _stage = "dispose";

        async Task CaptureAsync()
        {
            await foreach (var frame in monitor.Subscribe(ReadOnlyMemory<int>.Empty, captureCancellation.Token))
                counts[frame.CanId] = counts.GetValueOrDefault(frame.CanId) + 1;
            captureCancellation.Token.ThrowIfCancellationRequested();
            throw new IOException("Raw CAN stream ended before capture was stopped.");
        }
    }
}
