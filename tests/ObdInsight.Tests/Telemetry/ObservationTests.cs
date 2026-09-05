using System.Text;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;
using ObdInsight.Telemetry;
using ObdInsight.Telemetry.Providers;

namespace ObdInsight.Tests.Telemetry;

[Timeout(30_000)]
public class ObservationTests
{
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _utc = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => _utc;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan amount) { _utc += amount; _ticks += amount.Ticks; }
        public void RewindUtc(TimeSpan amount) => _utc -= amount;
    }

    [Test]
    public async Task CachedSpeed_RetainsReceipt_AndSnapshotCannotPromoteStaleData(CancellationToken ct)
    {
        var clock = new Clock();
        await using var transport = new ReplayElmTransport();
        transport.AutoRespond("ATMA", "");
        await using var commands = new LeafAze0CommandSet(new ElmSession(new ElmFramer(transport), timeProvider: clock));
        commands.Monitor.FilterRotation = [];
        await commands.Monitor.StartAsync(ct);
        transport.EnqueueIncoming("130 00 00 00 00 00 00 00 00\r284 00 00 00 00 0A 00 76 FC\r285 00 00 00 00 00 00 00 00\r354 00 00 00 00 00 00 00 00\r");
        while (!commands.Monitor.TryGetLatest(0x354, out _)) await Task.Delay(5, ct);
        commands.TryGet<IAntilockBrakingSystem>(out var abs);
        await using var telemetry = new TelemetrySession([new SpeedTelemetryProvider(abs)],
            new TelemetrySubscription(new Dictionary<TelemetrySignal, CadenceTier> { [TelemetrySignal.VehicleSpeed] = CadenceTier.High }),
            new TelemetrySessionOptions { MaxObservationAge = TimeSpan.FromSeconds(1) }, timeProvider: clock, connectionGeneration: 42);
        var initial = await telemetry.GetSnapshotAsync(ct);
        var observed = initial.Measurements[TelemetrySignal.VehicleSpeed].Observation;
        await Assert.That(initial.VehicleSpeedKmh).IsEqualTo(25.6m);
        await Assert.That(observed.CanId).IsEqualTo(0x284);
        await Assert.That(observed.ObservedAtUtc).IsEqualTo(clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(2));
        var post = await telemetry.GetSnapshotAsync(ct);
        var stale = post.Measurements[TelemetrySignal.VehicleSpeed];
        await Assert.That(post.VehicleSpeedKmh).IsNull();
        await Assert.That(stale.Scalar).IsEqualTo(25.6m); // evidence retained, not destroyed
        await Assert.That(stale.Observation).IsEqualTo(observed);
        await Assert.That(stale.Freshness).IsEqualTo(ObservationFreshness.Stale);
        await Assert.That(post.ConnectionGeneration).IsEqualTo(42);
        await telemetry.StartAsync(ct);
        await using var reader = telemetry.Stream(Signals.VehicleSpeed, ct).GetAsyncEnumerator(ct);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Observation).IsEqualTo(observed);
        await Assert.That(reader.Current.TimestampUtc).IsEqualTo(clock.GetUtcNow());
        await Assert.That(reader.Current.Freshness).IsEqualTo(ObservationFreshness.Stale);
        await Assert.That(reader.Current.ConnectionGeneration).IsEqualTo(42);
        await Assert.That(telemetry.Availability[TelemetrySignal.VehicleSpeed]).IsEqualTo(SignalAvailability.Stale);
    }

    [Test]
    public async Task ElectricalAndTemperatureReplies_KeepTheirSeparateCompletionTimes(CancellationToken ct)
    {
        var clock = new Clock(); var start = clock.GetUtcNow();
        await using var transport = new AdvancingTransport(clock);
        await using var commands = new LeafAze0CommandSet(new ElmSession(new ElmFramer(transport), timeProvider: clock));
        commands.TryGet<IBatteryManagementSystem>(out var bms);
        var status = await bms.GetStatusAsync(ct);
        await Assert.That(status.SocObservation.ObservedAtUtc).IsEqualTo(start.AddSeconds(1));
        await Assert.That(status.TemperatureObservation.ObservedAtUtc).IsEqualTo(start.AddSeconds(4));
        await Assert.That(status.SocObservation.Query).IsEqualTo("2101");
        await Assert.That(status.TemperatureObservation.Query).IsEqualTo("2104");
        await Assert.That(status.PowerObservation.ObservedAtUtc).IsEqualTo(start.AddSeconds(1));
        await Assert.That(status.PowerObservation.IsDerived).IsTrue();
    }

    [Test]
    public async Task UnknownAcquisition_IsNotStampedAsCurrent(CancellationToken ct)
    {
        var clock = new Clock();
        await using var telemetry = new TelemetrySession([new Provider(() => new(50m))], timeProvider: clock);
        var snapshot = await telemetry.GetSnapshotAsync(ct);
        var value = snapshot.Measurements[TelemetrySignal.StateOfCharge];
        await Assert.That(snapshot.SocPercent).IsNull();
        await Assert.That(value.Scalar).IsEqualTo(50m);
        await Assert.That(value.Observation.ObservedAtUtc).IsNull();
        await Assert.That(value.Freshness).IsEqualTo(ObservationFreshness.Unknown);
    }

    [Test]
    public async Task MissingTimeoutAndUnsupported_AreNotTheSameOutcome(CancellationToken ct)
    {
        await using var missing = new TelemetrySession([new Provider(() => TelemetryValue.Empty)]);
        var snapshot = await missing.GetSnapshotAsync(ct);
        await Assert.That(snapshot.Measurements[TelemetrySignal.StateOfCharge].Observation.Quality).IsEqualTo(ObservationQuality.Missing);
        await Assert.That(snapshot.Measurements[TelemetrySignal.Odometer].Observation.Quality).IsEqualTo(ObservationQuality.Unsupported);
        await using var timedOut = new TelemetrySession([new Provider(() => throw new TimeoutException())]);
        var failed = await timedOut.GetSnapshotAsync(ct);
        await Assert.That(failed.Measurements[TelemetrySignal.StateOfCharge].Observation.Quality).IsEqualTo(ObservationQuality.TimedOut);
        await timedOut.StartAsync(ct);
        await Assert.That(timedOut.Availability[TelemetrySignal.StateOfCharge]).IsEqualTo(SignalAvailability.Unknown);
    }

    [Test]
    public async Task InvalidValue_PreservesSourceEvidence_AndNeverBecomesAvailable(CancellationToken ct)
    {
        var clock = new Clock();
        var observation = ObservationMetadata.Capture(clock, ObservationSource.DiagnosticQuery, query: "2101");
        await using var telemetry = new TelemetrySession([new Provider(() => new TelemetryValue(500m).WithObservation(observation))], timeProvider: clock);
        var snapshot = await telemetry.GetSnapshotAsync(ct);
        var invalid = snapshot.Measurements[TelemetrySignal.StateOfCharge];
        await Assert.That(invalid.Scalar).IsNull();
        await Assert.That(invalid.Observation.ObservedAtUtc).IsEqualTo(observation.ObservedAtUtc);
        await Assert.That(invalid.Observation.Quality).IsEqualTo(ObservationQuality.Invalid);
        await telemetry.StartAsync(ct);
        await Assert.That(telemetry.Availability[TelemetrySignal.StateOfCharge]).IsEqualTo(SignalAvailability.Unknown);
    }

    [Test]
    public async Task BackwardWallClock_DoesNotMakeAnOldObservationFresh(CancellationToken ct)
    {
        var clock = new Clock();
        var observation = ObservationMetadata.Capture(clock, ObservationSource.CanBroadcast, 0x284);
        clock.Advance(TimeSpan.FromMinutes(2)); clock.RewindUtc(TimeSpan.FromSeconds(119));
        await using var telemetry = new TelemetrySession([new Provider(() => new TelemetryValue(50m).WithObservation(observation))], timeProvider: clock);
        var snapshot = await telemetry.GetSnapshotAsync(ct);
        await Assert.That(snapshot.SocPercent).IsNull();
        await Assert.That(snapshot.Measurements[TelemetrySignal.StateOfCharge].Age).IsEqualTo(TimeSpan.FromMinutes(2));
    }

    [Test]
    public async Task PublishedVector_IsFrozen_EvenWithoutRangeValidation(CancellationToken ct)
    {
        var clock = new Clock();
        decimal?[] cells = [3.9m, null, 4m];
        var observation = ObservationMetadata.Capture(clock, ObservationSource.DiagnosticQuery, query: "2102");
        await using var telemetry = new TelemetrySession([new Provider(
            () => new TelemetryValue(Vector: cells).WithObservation(observation), TelemetrySignal.CellVoltages)],
            options: new TelemetrySessionOptions { ValidateRanges = false }, timeProvider: clock);
        var snapshot = await telemetry.GetSnapshotAsync(ct);
        cells[0] = 0m;
        var value = snapshot.Measurements[TelemetrySignal.CellVoltages];
        await Assert.That(value.Vector![0]).IsEqualTo(3.9m);
        await Assert.That(value.Observation.Quality).IsEqualTo(ObservationQuality.Partial);
    }

    [Test]
    [Arguments("2101")]
    [Arguments("2104")]
    [Arguments("2102")]
    public async Task LeafTimeout_KeepsOutcomeWithoutFabricatingReceipt(string query, CancellationToken ct)
    {
        var clock = new Clock();
        await using var transport = new AdvancingTransport(clock, query);
        await using var commands = new LeafAze0CommandSet(new ElmSession(new ElmFramer(transport), timeProvider: clock));
        commands.TryGet<IBatteryManagementSystem>(out var bms);
        ObservationMetadata observation;
        if (query == "2102") observation = (await bms.GetCellVoltagesAsync(ct))!.Observation;
        else
        {
            var status = await bms.GetStatusAsync(ct);
            observation = query == "2101" ? status.SocObservation : status.TemperatureObservation;
        }
        await Assert.That(observation.Query).IsEqualTo(query);
        await Assert.That(observation.Quality).IsEqualTo(ObservationQuality.TimedOut);
        await Assert.That(observation.ObservedAtUtc).IsNull();
    }

    [Test]
    public async Task LeafProgrammingError_IsNotMissingData(CancellationToken ct)
    {
        await using var transport = new ReplayElmTransport(); // unscripted diagnostic is a test/programming error
        await using var commands = new LeafAze0CommandSet(new ElmSession(new ElmFramer(transport)));
        commands.TryGet<IBatteryManagementSystem>(out var bms);
        await Assert.That(async () => await bms.GetStatusAsync(ct)).Throws<InvalidOperationException>();
    }

    private sealed class Provider(Func<TelemetryValue> read, TelemetrySignal signal = TelemetrySignal.StateOfCharge) : ITelemetryProvider
    {
        public IReadOnlyCollection<TelemetrySignal> Signals => [signal];
        public bool IsCacheOnly => false;
        public ValueTask<IReadOnlyDictionary<TelemetrySignal, TelemetryValue>> ReadAsync(IReadOnlySet<TelemetrySignal> requested, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyDictionary<TelemetrySignal, TelemetryValue>>(new Dictionary<TelemetrySignal, TelemetryValue> { [signal] = read() });
    }

    private sealed class AdvancingTransport(Clock clock, string? timeoutQuery = null) : IElmTransport
    {
        private readonly ReplayElmTransport _inner = Create();
        private static ReplayElmTransport Create()
        {
            var replay = new ReplayElmTransport();
            replay.AutoRespond("2101", LeafGoldenData.GoldenGroup01Lines.AsElmResponse());
            replay.AutoRespond("2104", LeafGoldenData.GoldenGroup04Lines.AsElmResponse());
            return replay;
        }
        public bool IsOpen => true;
        public ValueTask OpenAsync(CancellationToken ct) => _inner.OpenAsync(ct);
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            var command = Encoding.ASCII.GetString(data.Span).Trim();
            if (command == timeoutQuery) throw new TimeoutException();
            if (command == "2101") clock.Advance(TimeSpan.FromSeconds(1));
            if (command == "2104") clock.Advance(TimeSpan.FromSeconds(3));
            return _inner.WriteAsync(data, ct);
        }
        public ValueTask FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public void ClearBuffer() => _inner.ClearBuffer();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
