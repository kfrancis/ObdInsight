using System.Diagnostics;
using System.Text;
using ObdInsight.Core.Communication.Elm327;

namespace ObdInsight.Simulation;

/// <summary>
/// A simulated 30 kWh Leaf AZE0 behind a simulated ELM327 adapter (roadmap B2):
/// answers the init/protocol sequence, BMS/VIN UDS queries (with state-accurate
/// ISO-TP payloads — SOC at the AZE0 offset, cells, temps, shunts), and streams
/// CAR-CAN broadcast frames (speed 0x284, HVAC 0x54x, VCM 0x510/0x5A9, gear 0x421,
/// SOH 0x5B3) whose values evolve along a <see cref="LeafDriveProfile"/>.
///
/// Limitations (deliberate): AT CM/CF hardware filters are ignored — all broadcast
/// frames stream in every monitoring window (the monitor's demux doesn't care);
/// EV-CAN broadcast IDs are absent, exactly like a stock adapter on the real car.
/// </summary>
public sealed class SimulatedLeafAze0Transport : IElmTransport
{
    public const string SimulatedVin = "1N4AZ0CP7HC308656";

    private readonly LeafDriveProfile _profile;
    private readonly object _gate = new();
    private readonly Queue<byte> _rx = new();
    private readonly SemaphoreSlim _dataSignal = new(0);
    private readonly StringBuilder _commandBuffer = new();
    private readonly Stopwatch _clock = new();

    private bool _monitoring;
    private string _header = "7DF";
    private long _lastBurstTicks;
    private ushort _frameCounter;

    public SimulatedLeafAze0Transport(LeafDriveProfile? profile = null, double timeScale = 1.0)
    {
        _profile = profile ?? LeafDriveProfile.DefaultTestDrive;
        TimeScale = timeScale;
    }

    /// <summary>
    /// Simulated-seconds per wall-clock second (default 1). Raise to compress a
    /// 30-minute drive into seconds for tests.
    /// </summary>
    public double TimeScale { get; }

    /// <summary>Minimum wall-clock spacing between broadcast bursts while monitoring.</summary>
    public TimeSpan FrameInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Current simulated elapsed drive time.</summary>
    public TimeSpan SimulatedElapsed => _clock.Elapsed * TimeScale;

    public bool IsOpen { get; private set; }

    public ValueTask OpenAsync(CancellationToken ct)
    {
        IsOpen = true;
        _clock.Start();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The session stack doesn't reliably call <see cref="OpenAsync"/> — the drive
    /// clock also starts on first adapter traffic so simulated time always advances.
    /// </summary>
    private void EnsureClockRunning()
    {
        if (!_clock.IsRunning)
        {
            _clock.Start();
        }
    }

    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public void ClearBuffer()
    {
        lock (_gate)
        {
            _rx.Clear();
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        EnsureClockRunning();
        var text = Encoding.ASCII.GetString(data.Span);
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n')
            {
                var command = _commandBuffer.ToString().Trim();
                _commandBuffer.Clear();
                if (command.Length > 0)
                {
                    HandleCommand(command);
                }
                else if (_monitoring)
                {
                    // A bare CR aborts monitoring, like a real ELM327.
                    StopMonitoring();
                }
            }
            else
            {
                _commandBuffer.Append(ch);
            }
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_rx.Count > 0)
                {
                    var n = 0;
                    while (n < buffer.Length && _rx.Count > 0)
                    {
                        buffer.Span[n++] = _rx.Dequeue();
                    }

                    return n;
                }
            }

            if (_monitoring)
            {
                var sinceLast = Environment.TickCount64 - _lastBurstTicks;
                var interval = (long)FrameInterval.TotalMilliseconds;
                if (sinceLast < interval)
                {
                    await Task.Delay((int)(interval - sinceLast), ct);
                }

                if (_monitoring) // may have been aborted while pacing
                {
                    EmitBroadcastBurst();
                    _lastBurstTicks = Environment.TickCount64;
                }

                continue;
            }

            await _dataSignal.WaitAsync(ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        _dataSignal.Dispose();
        return ValueTask.CompletedTask;
    }

    private void HandleCommand(string command)
    {
        var normalized = command.Replace(" ", "").ToUpperInvariant();

        if (_monitoring)
        {
            // Any received command aborts monitoring first.
            StopMonitoring();
        }

        if (normalized.StartsWith("AT", StringComparison.Ordinal))
        {
            HandleAtCommand(normalized);
            return;
        }

        Respond(BuildUdsResponse(normalized));
    }

    private void HandleAtCommand(string normalized)
    {
        switch (normalized)
        {
            case "ATZ":
                Respond("ELM327 v2.1");
                return;
            case "ATMA":
                _monitoring = true;
                _lastBurstTicks = 0;
                return; // monitoring streams without a prompt
            case "ATDPN":
                Respond("6");
                return;
        }

        if (normalized.StartsWith("ATSH", StringComparison.Ordinal))
        {
            _header = normalized["ATSH".Length..];
        }

        // CRA/CM/CF/FC and everything else: acknowledged, filters intentionally ignored.
        Respond("OK");
    }

    private string BuildUdsResponse(string request)
    {
        var state = _profile.StateAt(SimulatedElapsed);

        // Broadcast OBD probe (protocol detection): any functional 0100 gets a standard
        // supported-PIDs reply so the session locks CAN 500k without the EV wakeup path.
        if (request == "0100")
        {
            return "7E8064100BE3FA813";
        }

        // Functional DTC reads (Mode 03 stored / 07 pending): healthy car, zero codes.
        if (request == "03")
        {
            return "7E8024300";
        }

        if (request == "07")
        {
            return "7E8024700";
        }

        return _header switch
        {
            "79B" => request switch
            {
                "2101" => IsoTp("7BB", BuildGroup01Payload(state)),
                "2102" => IsoTp("7BB", BuildGroup02Payload(state)),
                "2104" => IsoTp("7BB", BuildGroup04Payload(state)),
                "2106" => IsoTp("7BB", BuildGroup06Payload()),
                _ => "NO DATA",
            },
            "797" => request == "2181" ? IsoTp("79A", BuildVinPayload()) : "NO DATA",
            _ => "NO DATA",
        };
    }

    // ---- UDS payload builders (payloads include the 61 xx response header) ----

    private static byte[] BuildGroup01Payload(LeafSimulationState s)
    {
        // 30 kWh layout: 41 data bytes after [61 01] (total 43 = 0x2B).
        var data = new byte[41];
        WriteInt32BE(data, 0, (int)Math.Round(s.PackCurrentAmps * 1024.0));
        WriteUInt16BE(data, 18, (ushort)Math.Round(s.PackVoltage * 100.0));
        WriteUInt16BE(data, 26, (ushort)Math.Round(s.HxPercent * 100.0));
        WriteUInt24BE(data, 29, (uint)Math.Round(s.SocPercent * 10000.0));
        WriteUInt24BE(data, 33, (uint)Math.Round(s.CapacityAh * 10000.0));
        return Payload(0x01, data);
    }

    private static byte[] BuildGroup02Payload(LeafSimulationState s)
    {
        // 96 × u16 mV + two trailing u16 (pack-voltage-like, semantics unconfirmed).
        var data = new byte[96 * 2 + 4];
        for (var i = 0; i < 96; i++)
        {
            WriteUInt16BE(data, i * 2, (ushort)s.CellVoltagesMv[i]);
        }

        var packish = (ushort)Math.Round(s.PackVoltage * 100.0);
        WriteUInt16BE(data, 192, packish);
        WriteUInt16BE(data, 194, packish);
        return Payload(0x02, data);
    }

    private static byte[] BuildGroup04Payload(LeafSimulationState s)
    {
        // Four [u16 thermistor ADC][u8 integer °C] slots (slot 3 absent on AZE0) + a
        // fifth integer °C byte. ADC inverse of −0.102 × (ADC − 710).
        var data = new byte[14];
        var adc = (ushort)Math.Round(710.0 - s.PackTempC / 0.102);
        var intC = (byte)Math.Round(s.PackTempC);
        WriteUInt16BE(data, 0, adc);
        data[2] = intC;
        WriteUInt16BE(data, 3, adc);
        data[5] = intC;
        WriteUInt16BE(data, 6, (ushort)(adc - 5)); // slight sensor spread
        data[8] = intC;
        WriteUInt16BE(data, 9, 0xFFFF);            // absent slot, like the real car
        data[11] = 0;
        data[12] = intC;
        return Payload(0x04, data);
    }

    private static byte[] BuildGroup06Payload()
    {
        // 24 shunt bytes, wire bits all set = "not balancing" in the OVMS convention.
        var data = new byte[24];
        Array.Fill(data, (byte)0xFF);
        return Payload(0x06, data);
    }

    private static byte[] BuildVinPayload()
    {
        var data = new byte[19];
        Encoding.ASCII.GetBytes(SimulatedVin, data);
        return Payload(0x81, data);
    }

    private static byte[] Payload(byte pid, byte[] data)
    {
        var payload = new byte[data.Length + 2];
        payload[0] = 0x61;
        payload[1] = pid;
        data.CopyTo(payload, 2);
        return payload;
    }

    /// <summary>Formats a payload as ELM-style ISO-TP lines (FF + CFs, headers-on, no spaces).</summary>
    private static string IsoTp(string rxHeader, byte[] payload)
    {
        var sb = new StringBuilder();
        var first = Math.Min(6, payload.Length);
        sb.Append(rxHeader).Append("10").Append(payload.Length.ToString("X2"));
        AppendHex(sb, payload.AsSpan(0, first));
        sb.Append('\r');

        var offset = first;
        var seq = 1;
        while (offset < payload.Length)
        {
            var take = Math.Min(7, payload.Length - offset);
            sb.Append(rxHeader).Append('2').Append((seq & 0xF).ToString("X1"));
            AppendHex(sb, payload.AsSpan(offset, take));
            sb.Append('\r');
            offset += take;
            seq++;
        }

        return sb.ToString().TrimEnd('\r');
    }

    // ---- Broadcast frames ----

    private void EmitBroadcastBurst()
    {
        var s = _profile.StateAt(SimulatedElapsed);
        _frameCounter++;

        var speedRaw = (ushort)Math.Round(s.SpeedKmh * 100.0);
        var rangeRaw = (uint)Math.Round(s.RangeKm / 0.2);
        var ambientRaw = (byte)Math.Round((s.AmbientTempC + 40.0) * 2.0);
        var intakeRaw = (byte)Math.Round((s.CabinTempC + 14.0) * 2.0);
        var sohByte = (byte)((int)Math.Round(s.HxPercent) << 1);

        var frames = new List<byte[]>
        {
            Frame(0x130, 0, 0, 0, 0, 0, 0, 0, 0),
            // 0x284: speed at bytes 4-5 (BE, ×0.01), free-running counter bytes 6-7.
            Frame(0x284, 0, 0, 0, 0, (byte)(speedRaw >> 8), (byte)speedRaw,
                (byte)(_frameCounter >> 8), (byte)_frameCounter),
            Frame(0x285, 0, 0, 0, 0, 0, 0, 0, 0),
            Frame(0x354, 0, 0, 0, 0, 0, 0x08, 0, 0),
            Frame(0x54A, 0, 0, 0, 0, 0, 0, 0, 0),
            // 0x54B: fan speed bits 35-39.
            Frame(0x54B, 0, 0, 0, 0, (byte)((s.HvacOn ? 2 : 0) << 3), 0, 0, 0),
            // 0x54C: ClimateControlOn bit 10 / AcOn bit 11; ambient byte 6 (×0.5 − 40).
            Frame(0x54C, 0, (byte)(s.HvacOn ? 0x0C : 0x00), 0, 0, 0, 0, ambientRaw, 0),
            // 0x54F: interior intake temp byte 0 (×0.5 − 14).
            Frame(0x54F, intakeRaw, 0, 0, 0, 0, 0, 0, 0),
            Frame(0x510, 0, 0, 0, 0, 0, 0, 0, 0),
            // 0x5A9: range bits 15-26 (raw ×0.2 km; 0xFFF = charging sentinel).
            Frame(0x5A9, 0,
                (byte)((rangeRaw & 0x01) << 7),
                (byte)((rangeRaw >> 1) & 0xFF),
                (byte)((rangeRaw >> 9) & 0x07), 0, 0, 0, 0),
            // 0x421: shifter D (byte 0 bits 3-5 = 4).
            Frame(0x421, 0x20),
            // 0x5B3: SOH % at byte1 >> 1.
            Frame(0x5B3, 0, sohByte, 0, 0, 0, 0, 0, 0),
        };

        lock (_gate)
        {
            foreach (var frame in frames)
            {
                var line = new StringBuilder();
                line.Append(((frame[0] << 8) | frame[1]).ToString("X3"));
                foreach (var b in frame.AsSpan(2))
                {
                    line.Append(' ').Append(b.ToString("X2"));
                }

                line.Append('\r');
                foreach (var b in Encoding.ASCII.GetBytes(line.ToString()))
                {
                    _rx.Enqueue(b);
                }
            }
        }

        _dataSignal.Release();
    }

    private static byte[] Frame(int canId, params byte[] data)
    {
        var frame = new byte[2 + data.Length];
        frame[0] = (byte)(canId >> 8);
        frame[1] = (byte)canId;
        data.CopyTo(frame, 2);
        return frame;
    }

    private void StopMonitoring()
    {
        _monitoring = false;
        Respond("STOPPED");
    }

    private void Respond(string body)
    {
        lock (_gate)
        {
            foreach (var b in Encoding.ASCII.GetBytes(body + "\r\r>"))
            {
                _rx.Enqueue(b);
            }
        }

        _dataSignal.Release();
    }

    private static void AppendHex(StringBuilder sb, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("X2"));
        }
    }

    private static void WriteUInt16BE(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static void WriteUInt24BE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 16);
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)value;
    }

    private static void WriteInt32BE(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
