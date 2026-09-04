using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Communication.Slcan;
using ObdInsight.Core.Protocols;
using ObdInsight.Simulation;

namespace ObdInsight.Tests.Elm327;

/// <summary>
///     Drives <see cref="SlcanFrameSource" /> end to end over the replay transport, so the whole path
///     from device bytes to <see cref="RawCanFrame" /> is exercised without a CANable attached.
///     This is the abstraction that lets a raw CAN interface feed the same consumers an ELM327 does.
///     The parts worth pinning are the ones a real device will break: frames split across reads,
///     adapter chatter interleaved with data, and the listen-only default.
/// </summary>
[Timeout(30_000)]
public class SlcanFrameSourceTests
{
    /// <summary>Banner captured from a CANable 2.0 on COM5, 2026-09-03 (stock canable2-fw).</summary>
    private const string CanableBanner = "16e7497-dirty github.com/normaldotcom/canable2.git";

    /// <summary>
    ///     The replay transport auto-answers AT commands but not SLCAN verbs, so the handshake is
    ///     scripted here as a Lawicel device: a bare CR meaning "accepted", and a classic
    ///     <c>V1013</c> banner so dialect detection lands on Lawicel and the open command is <c>L</c>.
    /// </summary>
    private static ReplayElmTransport Transport(string versionBanner = "V1013")
    {
        var transport = new ReplayElmTransport();
        foreach (var command in new[] { "C", "S6", "S5", "S4", "S8", "L", "O", "M0", "M1" })
        {
            transport.AutoRespond(command, "\r");
        }

        transport.AutoRespond("V", versionBanner.Length == 0 ? "" : versionBanner + "\r");

        return transport;
    }

    private static void Feed(ReplayElmTransport transport, string text) =>
        transport.EnqueueIncoming(text);

    private static async Task<List<RawCanFrame>> ReadAsync(
        SlcanFrameSource source, int expected, CancellationToken ct)
    {
        var frames = new List<RawCanFrame>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await foreach (var frame in source.ReadFramesAsync(window.Token))
            {
                frames.Add(frame);
                if (frames.Count >= expected)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Fall through: the assertions report what did arrive, which is more useful than a
            // timeout exception hiding a partial result.
        }

        return frames;
    }

    [Test]
    public async Task Start_OpensListenOnlyByDefault(CancellationToken token)
    {
        var transport = Transport();
        await using var source = new SlcanFrameSource(transport);

        await source.StartAsync(token);

        // Close first (the device may be open from a previous process), probe the firmware,
        // then bitrate, then open.
        await Assert.That(transport.SentCommands).IsEquivalentTo(new[] { "C", "V", "S6", "L" });
        await Assert.That(source.Dialect).IsEqualTo(SlcanDialect.Lawicel);
        await Assert.That(source.FirmwareVersion).IsEqualTo("V1013");
    }

    /// <summary>
    ///     The finding that motivated dialect detection: CANable firmware has no <c>L</c>. Sent
    ///     <c>L</c>, it stays closed and never emits a frame; listen-only there is <c>M1</c> before
    ///     <c>O</c>. The banner is the real one from the bench device.
    /// </summary>
    [Test]
    public async Task Start_OnCanableFirmware_UsesSilentModeThenOpen(CancellationToken token)
    {
        var transport = Transport(CanableBanner);
        await using var source = new SlcanFrameSource(transport);

        await source.StartAsync(token);

        await Assert.That(transport.SentCommands).IsEquivalentTo(new[] { "C", "V", "S6", "M1", "O" });
        await Assert.That(transport.SentCommands).DoesNotContain("L");
        await Assert.That(source.Dialect).IsEqualTo(SlcanDialect.Canable);
        await Assert.That(source.FirmwareVersion).IsEqualTo(CanableBanner);
    }

    [Test]
    public async Task Start_OnCanableFirmware_NormalModeIsExplicit(CancellationToken token)
    {
        var transport = Transport(CanableBanner);
        await using var source = new SlcanFrameSource(transport, listenOnly: false);

        await source.StartAsync(token);

        await Assert.That(transport.SentCommands).IsEquivalentTo(new[] { "C", "V", "S6", "M0", "O" });
    }

    /// <summary>A caller that already knows the firmware skips the probe entirely.</summary>
    [Test]
    public async Task Start_WithExplicitDialect_DoesNotProbe(CancellationToken token)
    {
        var transport = Transport();
        await using var source = new SlcanFrameSource(transport, dialect: SlcanDialect.Canable);

        await source.StartAsync(token);

        await Assert.That(transport.SentCommands).DoesNotContain("V");
        await Assert.That(transport.SentCommands).IsEquivalentTo(new[] { "C", "S6", "M1", "O" });
        await Assert.That(source.FirmwareVersion).IsNull();
    }

    /// <summary>
    ///     A device that stays silent on <c>V</c> gets the Lawicel sequence: <c>L</c> is the one
    ///     listen-only request that cannot open anything in normal mode by accident.
    /// </summary>
    [Test]
    public async Task Start_WhenDeviceStaysSilent_FallsBackToLawicelListenOnly(CancellationToken token)
    {
        var transport = Transport(versionBanner: "");
        await using var source = new SlcanFrameSource(transport) { ProbeTimeout = TimeSpan.FromMilliseconds(100) };

        await source.StartAsync(token);

        await Assert.That(source.Dialect).IsEqualTo(SlcanDialect.Unknown);
        await Assert.That(source.FirmwareVersion).IsNull();
        await Assert.That(transport.SentCommands).IsEquivalentTo(new[] { "C", "V", "S6", "L" });
    }

    /// <summary>
    ///     Bytes that arrive in the same read as the banner belong to the frame stream, not to the
    ///     probe. Dropping them would lose the first frames after every open.
    /// </summary>
    [Test]
    public async Task Start_LinesArrivingWithTheBanner_AreNotLost(CancellationToken token)
    {
        var transport = Transport();
        transport.AutoRespond("V", CanableBanner + "\rt1DB80102030405060708\r");
        await using var source = new SlcanFrameSource(transport);
        await source.StartAsync(token);

        var frames = await ReadAsync(source, 1, token);

        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(frames[0].CanId).IsEqualTo(0x1DB);
    }

    /// <summary>
    ///     An unplugged adapter must end the stream, not spin it. The replay transport's failure
    ///     injection stands in for the serial port throwing on a vanished device.
    /// </summary>
    [Test]
    public async Task ReadFrames_EndsWithTransportError_WhenTheLinkDies(CancellationToken token)
    {
        var transport = Transport();
        await using var source = new SlcanFrameSource(transport);
        await source.StartAsync(token);

        Feed(transport, "t1DB80102030405060708\r");
        var frames = new List<RawCanFrame>();
        var reader = Task.Run(async () =>
        {
            await foreach (var frame in source.ReadFramesAsync(token))
            {
                frames.Add(frame);
                transport.SimulateConnectionLost();
            }
        }, token);

        await reader.WaitAsync(TimeSpan.FromSeconds(5), token);

        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(source.LastEndReason).IsEqualTo(MonitoringEndReason.TransportError);
    }

    /// <summary>CAN FD with bit-rate switch (<c>b</c>/<c>B</c>) is what a CANable 2.0 emits on a real FD bus.</summary>
    [Test]
    public async Task ReadFrames_CountsBrsFramesAsCanFd(CancellationToken token)
    {
        var transport = Transport(CanableBanner);
        await using var source = new SlcanFrameSource(transport);
        await source.StartAsync(token);

        Feed(transport, "b1DB80102030405060708\rB18DAF11080102030405060708\r");

        var frames = await ReadAsync(source, 2, token);

        await Assert.That(frames).Count().IsEqualTo(2);
        await Assert.That(source.CanFdFrameCount).IsEqualTo(2);
    }

    /// <summary>The error register is the only diagnostic a stock CANable offers; it must be readable.</summary>
    [Test]
    public async Task Query_ReturnsTheDeviceReply(CancellationToken token)
    {
        var transport = Transport(CanableBanner);
        transport.AutoRespond("E", "CANable Error Register: 0\r");
        await using var source = new SlcanFrameSource(transport);

        var reply = await source.QueryAsync(SlcanProtocol.ErrorRegister, TimeSpan.FromSeconds(1), token);

        await Assert.That(reply).IsEqualTo("CANable Error Register: 0");
    }

    /// <summary>
    ///     Opening for transmission has to be asked for. On a powertrain bus the difference is a
    ///     safety property, not a preference, so the default must be the safe one.
    /// </summary>
    [Test]
    public async Task Start_OpensNormalOnlyWhenExplicitlyRequested(CancellationToken token)
    {
        var transport = Transport();
        await using var source = new SlcanFrameSource(transport, listenOnly: false);

        await source.StartAsync(token);

        await Assert.That(transport.SentCommands).Contains("O");
        await Assert.That(transport.SentCommands).DoesNotContain("L");
    }

    [Test]
    public async Task ReadFrames_ParsesStandardFrames(CancellationToken token)
    {
        var transport = Transport();
        await using var source = new SlcanFrameSource(transport);
        await source.StartAsync(token);

        Feed(transport, "t1DB80102030405060708\rt3558AABBCCDDEEFF0011\r");

        var frames = await ReadAsync(source, 2, token);

        await Assert.That(frames).Count().IsEqualTo(2);
        await Assert.That(frames[0].CanId).IsEqualTo(0x1DB);
        await Assert.That(Convert.ToHexString(frames[0].Data.ToArray())).IsEqualTo("0102030405060708");
        await Assert.That(frames[1].CanId).IsEqualTo(0x355);
    }

    /// <summary>
    ///     A real device splits lines across reads whenever the buffer boundary lands mid-frame. The
    ///     carry-over buffer is the only thing preventing that from silently dropping frames, so it
    ///     is worth pinning at the nastiest split: one byte at a time.
    /// </summary>
    [Test]
    public async Task ReadFrames_ReassemblesFrameSplitAcrossReads(CancellationToken token)
    {
        var transport = Transport();
        await using var source = new SlcanFrameSource(transport);
        await source.StartAsync(token);

        foreach (var c in "t1DB80102030405060708\r")
        {
            Feed(transport, c.ToString());
        }

        var frames = await ReadAsync(source, 1, token);

        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(frames[0].CanId).IsEqualTo(0x1DB);
        await Assert.That(Convert.ToHexString(frames[0].Data.ToArray())).IsEqualTo("0102030405060708");
    }

    /// <summary>
    ///     Adapter chatter is interleaved with frames in practice - version banners on open, bell
    ///     characters on error, acknowledgements after a transmit. A capture loop has to run straight
    ///     through them, so they are counted rather than treated as failures.
    /// </summary>
    [Test]
    public async Task ReadFrames_SkipsNonFrameLinesAndCountsThem(CancellationToken token)
    {
        var transport = Transport();
        await using var source = new SlcanFrameSource(transport);
        await source.StartAsync(token);

        Feed(transport, "V1013\r\a\rt1DB80102030405060708\rZ\r");

        var frames = await ReadAsync(source, 1, token);

        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(frames[0].CanId).IsEqualTo(0x1DB);
        await Assert.That(source.NonFrameLineCount).IsGreaterThanOrEqualTo(2);
    }

    /// <summary>
    ///     An FD frame on a bus believed to be classic CAN is worth surfacing: it is the evidence
    ///     that a vehicle needs FD-capable hardware, and silently dropping it would hide that.
    /// </summary>
    [Test]
    public async Task ReadFrames_CountsCanFdFramesSeparately(CancellationToken token)
    {
        var transport = Transport();
        await using var source = new SlcanFrameSource(transport);
        await source.StartAsync(token);

        Feed(transport, "d1DB80102030405060708\r");

        var frames = await ReadAsync(source, 1, token);

        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(source.CanFdFrameCount).IsEqualTo(1);
    }

    [Test]
    public async Task ReadFrames_ParsesExtendedIds(CancellationToken token)
    {
        var transport = Transport();
        await using var source = new SlcanFrameSource(transport);
        await source.StartAsync(token);

        Feed(transport, "T18DAF11084AABBCCDDEEFF0011\r");

        var frames = await ReadAsync(source, 1, token);

        await Assert.That(frames).Count().IsEqualTo(1);
        await Assert.That(frames[0].CanId).IsEqualTo(0x18DAF110);
    }

    [Test]
    public async Task Stop_ClosesTheChannel(CancellationToken token)
    {
        var transport = Transport();
        var source = new SlcanFrameSource(transport);
        await source.StartAsync(token);

        await source.StopAsync(token);

        // Close on open plus close on stop.
        await Assert.That(transport.SentCommands.Count(c => c == "C")).IsEqualTo(2);
    }

    [Test]
    public async Task Start_IsIdempotent(CancellationToken token)
    {
        var transport = Transport();
        await using var source = new SlcanFrameSource(transport);

        await source.StartAsync(token);
        await source.StartAsync(token);

        await Assert.That(transport.SentCommands.Count(c => c == "L")).IsEqualTo(1);
    }
}
