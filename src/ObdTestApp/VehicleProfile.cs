using System.Buffers.Binary;
using System.Globalization;

namespace ObdTestApp;

public enum GearPosition : byte
{
    Unknown = 0,
    Park = 1,
    Reverse = 2,
    Neutral = 3,
    Drive = 4,
    Eco = 7
}

public interface IVehicleCommandSet
{
    IReadOnlyCollection<Type> Capabilities { get; }

    bool TryGet<T>(out T capability) where T : class, IVehicleCapability;
}

public interface IVehicleProfile
{
    string Make { get; }
    string Model { get; }
    IReadOnlyList<VehicleVariant> Variants { get; }

    IVehicleCommandSet GetCommands(VehicleVariantId variantId, IElmSession session);
}

public readonly record struct VehicleVariantId(string Value);

public sealed record VehicleVariant(
    VehicleVariantId Id,
    string DisplayName,
    int YearFrom,
    int? YearTo,
    string PlatformCode,   // "ZE0", "AZE0-2", "ZE1", "RE4", "RM4", etc. Use whatever makes sense per OEM.
    IReadOnlyDictionary<string, object?> Attributes
)
{
    public T? Get<T>(string key) => Attributes.TryGetValue(key, out var v) && v is T t ? t : default;
    public object? Get(string key) => Attributes.TryGetValue(key, out var v) ? v : null;
}

public static class VariantAttr
{
    // EV-ish attributes (Leaf)
    public const string PackKwh = "pack.kwh";
    public const string Motor = "motor";
    public const string Chemistry = "battery.chemistry";
    public const string MaxChargeVolts = "battery.maxChargeVolts";

    // ICE/hybrid-ish attributes (CR-V)
    public const string Engine = "engine";
    public const string DisplacementL = "engine.displacementL";
    public const string Induction = "engine.induction"; // NA / Turbo
    public const string Transmission = "transmission";
    public const string Drivetrain = "drivetrain";
    public const string Hybrid = "powertrain.hybrid";
}

public abstract class VehicleCommandSet : IVehicleCommandSet
{
    private readonly Dictionary<Type, IVehicleCapability> _caps = new();

    public IReadOnlyCollection<Type> Capabilities => _caps.Keys.ToArray();

    public bool TryGet<T>(out T capability) where T : class, IVehicleCapability
    {
        if (_caps.TryGetValue(typeof(T), out var cap) && cap is T t) { capability = t; return true; }
        capability = default!;
        return false;
    }

    protected void Add<T>(T cap) where T : class, IVehicleCapability => _caps[typeof(T)] = cap;
}

public abstract class VehicleProfile : IVehicleProfile
{
    public abstract string Make { get; }
    public abstract string Model { get; }
    public abstract IReadOnlyList<VehicleVariant> Variants { get; }

    public abstract IVehicleCommandSet GetCommands(VehicleVariantId variantId, IElmSession session);
}

public sealed class VehicleSession
{
    private readonly IVehicleCommandSet _commands;

    public VehicleSession(IVehicleCommandSet commands) => _commands = commands;

    public bool Supports<T>() where T : class, IVehicleCapability => _commands.TryGet<T>(out _);

    public bool TryGet<T>(out T cap) where T : class, IVehicleCapability => _commands.TryGet(out cap);
}

internal static class Hex
{
    /// <summary>
    /// Parses a string containing hex bytes into a byte array.
    /// Accepts formats like:
    /// - "04 62 11 56 01 00 00 00"
    /// - "0x79A 04 62 11 56 01 00 00 00"
    /// - "0462115601000000"
    /// Non-hex tokens are ignored; "0x" prefixes are allowed.
    /// </summary>
    public static byte[] ParseBytes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Fast path: strip to hex digits only.
        var compact = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (compact.Length >= 2 && compact.Length % 2 == 0 && compact.Length <= 64)
        {
            var bytes = new byte[compact.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(compact.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }

        // Token path...
        var tokens = text.Split(new[] { ' ', '\t', '\r', '\n', ',', ';', '|', '-' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<byte>(16);

        foreach (var tok0 in tokens)
        {
            var tok = tok0.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? tok0[2..] : tok0;
            if (tok.Length == 0) continue;

            // treat only 1–2 hex chars as a byte
            if (tok.Length > 2) continue;
            if (!tok.All(Uri.IsHexDigit)) continue;

            list.Add(byte.Parse(tok, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        return list.ToArray();
    }

    /// <summary>
    /// If the payload is ISO-TP single frame "N ..." (e.g. 04 62 11 56 01 ...),
    /// returns the ISO-TP payload portion (the next N bytes).
    /// If it doesn't look like ISO-TP, returns the original span.
    /// </summary>
    public static ReadOnlySpan<byte> TryExtractIsoTpPayload(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return bytes;

        // ISO-TP SF: high nibble 0, low nibble = payload length
        var pci = bytes[0];
        if ((pci & 0xF0) != 0x00) return bytes;

        var len = pci & 0x0F;
        if (len == 0) return bytes;
        if (bytes.Length < 1 + len) return bytes;

        return bytes.Slice(1, len);
    }
}

/// <summary>
/// Provides utility methods and constants for constructing and parsing Unified Diagnostic Services (UDS)
/// ReadDataByIdentifier (RDBI) requests and responses.
/// </summary>
/// <remarks>This class contains helpers for working with UDS protocol messages, specifically for building request
/// payloads and extracting data from positive responses for the ReadDataByIdentifier service (service ID 0x22). It is
/// intended for use in automotive diagnostic applications that communicate using UDS over CAN or similar transport
/// layers.</remarks>
internal static class Uds
{
    public const byte ReadDataByIdentifier = 0x22;
    public const byte ReadDataByIdentifier_PositiveResponse = 0x62;

    /// <summary>
    /// Builds a UDS ReadDataByIdentifier request payload for a 2-byte DID.
    /// </summary>
    /// <remarks>
    /// UDS request format:
    ///   [0] = 0x22 (ReadDataByIdentifier)
    ///   [1] = DID high byte
    ///   [2] = DID low byte
    ///
    /// Example for DID 0x1156:
    ///   22 11 56
    ///
    /// ISO-TP framing (e.g., "03 22 11 56 00 00 00 00") is transport-layer,
    /// and should be handled by the ELM/session layer when possible.
    /// </remarks>
    public static byte[] BuildReadDidPayload(ushort did) =>
        [(byte)ReadDataByIdentifier, (byte)(did >> 8), (byte)did];

    /// <summary>
    /// Validates and extracts the data portion from a positive UDS ReadDataByIdentifier response.
    /// </summary>
    /// <remarks>
    /// UDS positive response format for RDBI:
    ///   [0] = 0x62 (0x22 + 0x40)
    ///   [1] = DID high byte
    ///   [2] = DID low byte
    ///   [3..] = DID data bytes
    /// </remarks>
    public static ReadOnlySpan<byte> ParseReadDidResponse(ReadOnlySpan<byte> response, ushort expectedDid)
    {
        if (response.Length < 4)
            throw new InvalidOperationException($"UDS RDBI response too short. Length={response.Length}");

        if (response[0] != ReadDataByIdentifier_PositiveResponse)
            throw new InvalidOperationException($"Unexpected UDS response SID: 0x{response[0]:X2} (expected 0x62).");

        var did = (ushort)((response[1] << 8) | response[2]);
        if (did != expectedDid)
            throw new InvalidOperationException($"Unexpected DID in response: 0x{did:X4} (expected 0x{expectedDid:X4}).");

        return response[3..];
    }
}

public sealed class HvacStatus
{
    public bool ClimateControlOn { get; init; }
    public bool AcOn { get; init; }
    public bool RearDefrostOn { get; init; }

    public double? InteriorIntakeTempC { get; init; }
    public double? OutsideAmbientTempC { get; init; }
    public double? EvaporatorTempC { get; init; }

    public int? FanSpeed { get; init; }          // 0-15 raw nibble (you can map later)
    public double? FanVoltageV { get; init; }

    public int? AcPowerWatts { get; init; }      // 50 W / bit
    public int? HeaterPowerWatts { get; init; }  // 300 W / bit

    public byte? SetpointRaw { get; init; }      // From 0x54A byte4 (unknown mapping)
}

public readonly record struct BrakeStatus(bool BrakePressed, bool AbsActive);

internal static class CanBits
{
    public static uint ReadUnsigned(ReadOnlySpan<byte> data, int bitPos, int bitLen)
    {
        if ((uint)bitPos > 63 || bitLen is <= 0 or > 32) throw new ArgumentOutOfRangeException();
        if (data.Length < 8) throw new ArgumentException("Expected 8 bytes of CAN data.", nameof(data));
        var raw = BinaryPrimitives.ReadUInt64LittleEndian(data);
        var mask = bitLen == 32 ? 0xFFFF_FFFFul : ((1ul << bitLen) - 1ul);
        return (uint)((raw >> bitPos) & mask);
    }

    public static bool ReadBool(ReadOnlySpan<byte> data, int bitPos) =>
        ReadUnsigned(data, bitPos, 1) != 0;
}
