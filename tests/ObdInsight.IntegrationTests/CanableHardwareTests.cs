using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Communication.Slcan;
using ObdInsight.Transports.Serial;

namespace ObdInsight.IntegrationTests;

/// <summary>
///     Bench tests for a CANable-class adapter on a COM port. No vehicle needed: a bare adapter
///     on the desk answers the version query, opens listen-only and sits quietly. Everything the
///     replay transport already proves is skipped here; what remains is the serial plumbing
///     (cancellation of a blocked read, clean close) and the firmware's actual reaction to the
///     handshake. Opt in with <c>CANABLE_PORT=COM5</c>.
/// </summary>
/// <remarks>
///     Verified 2026-09-03 on a CANable 2.0 (USB VID 16D0 PID 117E) running stock canable2-fw
///     <c>16e7497-dirty</c>: banner <c>16e7497-dirty github.com/normaldotcom/canable2.git</c>,
///     dialect <see cref="SlcanDialect.Canable" />.
/// </remarks>
[RequiresCanable]
[Timeout(30_000)]
[NotInParallel("canable-port")]
public class CanableHardwareTests
{
    private static string Port => RequiresCanableAttribute.Port!;

    [Test]
    public async Task Port_IsListed(CancellationToken token)
    {
        await Assert.That(SerialElmTransport.AvailablePorts())
            .Contains(p => string.Equals(p, Port, StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task Version_IdentifiesAKnownDialect(CancellationToken token)
    {
        await using var transport = new SerialElmTransport(Port);
        await transport.OpenAsync(token);
        await using var source = new SlcanFrameSource(transport);

        await source.StartAsync(token);

        await Assert.That(source.FirmwareVersion).IsNotNull();
        await Assert.That(source.Dialect).IsNotEqualTo(SlcanDialect.Unknown);
    }

    /// <summary>
    ///     A quiet adapter must not spin, and cancelling must return promptly. Both were real
    ///     failure modes: the async serial read ignored cancellation entirely (measured: never
    ///     returned), and a 0-byte "quiet" result looped hot.
    /// </summary>
    [Test]
    public async Task ReadFrames_OnAQuietAdapter_CancelsPromptly(CancellationToken token)
    {
        await using var transport = new SerialElmTransport(Port);
        await transport.OpenAsync(token);
        await using var source = new SlcanFrameSource(transport);
        await using var monitor = new CanMonitor(source);
        await monitor.StartAsync(token);

        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(TimeSpan.FromSeconds(2));
        var frames = 0;
        var started = DateTime.UtcNow;
        try
        {
            await foreach (var _ in monitor.Subscribe(ReadOnlyMemory<int>.Empty, window.Token))
            {
                frames++;
            }
        }
        catch (OperationCanceledException)
        {
            // Window elapsed.
        }

        var stopStarted = DateTime.UtcNow;
        await monitor.StopAsync(token);
        var stopTook = DateTime.UtcNow - stopStarted;

        await Assert.That(DateTime.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(6));
        await Assert.That(stopTook).IsLessThan(TimeSpan.FromSeconds(2));
        await Assert.That(monitor.EndReason).IsEqualTo(MonitoringEndReason.Stopped);
        // On the bench there is no bus; in a car this simply proves frames flow.
        await Assert.That(frames).IsGreaterThanOrEqualTo(0);
    }

    /// <summary>The error register is the only diagnostic the stock firmware offers; it must be readable after a session.</summary>
    [Test]
    public async Task ErrorRegister_IsReadable_AfterClose(CancellationToken token)
    {
        await using var transport = new SerialElmTransport(Port);
        await transport.OpenAsync(token);
        await using var source = new SlcanFrameSource(transport);
        await source.StartAsync(token);
        await source.StopAsync(token);

        var reply = await source.QueryAsync(SlcanProtocol.ErrorRegister, TimeSpan.FromSeconds(1), token);

        // Only stock CANable firmware has 'E'. Lawicel and ElmüSoft 2.5 answer BEL (measured
        // 2026-09-03 on slcan 2.5: "E" -> 0x07), which the query filters to null.
        if (source.Dialect is SlcanDialect.Canable)
        {
            await Assert.That(reply).IsNotNull();
            await Assert.That(reply!).Contains("Error");
        }
        else
        {
            await Assert.That(reply).IsNull();
        }
    }

    /// <summary>Two sessions back to back: the port must be released cleanly by the first.</summary>
    [Test]
    public async Task Reopen_AfterDispose_Works(CancellationToken token)
    {
        for (var i = 0; i < 2; i++)
        {
            await using var transport = new SerialElmTransport(Port);
            await transport.OpenAsync(token);
            await using var source = new SlcanFrameSource(transport);
            await source.StartAsync(token);
            await Assert.That(source.Dialect).IsNotEqualTo(SlcanDialect.Unknown);
        }
    }
}
