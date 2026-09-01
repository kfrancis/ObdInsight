using System.Diagnostics.CodeAnalysis;

namespace ObdInsight.Simulation;

/// <summary>
///     Golden ELM327 response lines captured from a real Nissan Leaf AZE0.
///     Data only — parsing belongs to production code (LeafBmsDiagnostics, IsoTpParser).
/// </summary>
public static class LeafGoldenData
{
    /// <summary>
    ///     BMS Group 01 response (Mode 21 PID 01, TX 79B / RX 7BB).
    ///     Captured 2026-01-18. 30kWh-format payload: 43 bytes, header [61 01].
    /// </summary>
    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    public static readonly string[] GoldenGroup01Lines =
    [
        "7BB102B6101000000EB", // FF: len=43, [61 01 00 00 00 EB]
        "7BB21028AFFFFFD5AFF", // CF1: [02 8A FF FF FD 5A FF]
        "7BB22FFFFFF07F220AC", // CF2: [FF FF FF 07 F2 20 AC]
        "7BB238D52386C039201", // CF3: [8D 52 38 6C 03 92 01]
        "7BB244E0DD80006658A", // CF4: [4E 0D D8 00 06 65 8A]
        "7BB25000805C1800005", // CF5: [00 08 05 C1 80 00 05]
        "7BB260000FFFFFFFFFF" // CF6: [00 00 FF...]
    ];

    /// <summary>
    ///     BMS Group 02 response (96 cell-pair voltages, TX 79B / RX 7BB).
    ///     Captured 2025-12-06 on the same 30kWh AZE0 (third-party app log).
    ///     Payload: 198 bytes, header [61 02] + 96×u16 mV (3899-3911 range) + 4 trailing bytes
    ///     (two u16s ≈ pack voltage ×0.01: 374.82 / 374.00 V — semantics unconfirmed).
    /// </summary>
    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    public static readonly string[] GoldenGroup02Lines =
    [
        "7BB10C661020F3D0F42",
        "7BB210F420F3F0F3E0F",
        "7BB223E0F420F3F0F3B",
        "7BB230F3E0F3E0F3F0F",
        "7BB243F0F3F0F420F3B",
        "7BB250F3F0F470F420F",
        "7BB26400F3F0F3F0F42",
        "7BB270F3B0F440F420F",
        "7BB28420F440F420F42",
        "7BB290F420F3F0F420F",
        "7BB2A470F420F3F0F42",
        "7BB2B0F420F420F3F0F",
        "7BB2C3F0F3F0F420F40",
        "7BB2D0F420F420F420F",
        "7BB2E3F0F420F3F0F3E",
        "7BB2F0F3F0F3E0F3D0F",
        "7BB203D0F3B0F420F42",
        "7BB210F420F420F420F",
        "7BB22420F420F440F44",
        "7BB230F3F0F470F3B0F",
        "7BB24420F420F420F44",
        "7BB250F3F0F420F420F",
        "7BB263F0F3F0F420F42",
        "7BB270F3F0F3F0F420F",
        "7BB283E0F440F3F0F42",
        "7BB290F420F3B0F3F0F",
        "7BB2A420F3E0F3B0F3E",
        "7BB2B0F3E0F3E0F4292",
        "7BB2C6A9218FFFFFFFF"
    ];

    /// <summary>
    ///     BMS Group 04 response (pack temperatures, TX 79B / RX 7BB).
    ///     Captured 2025-12-06 on the same 30kWh AZE0 (third-party app log, winter).
    ///     Payload: 16 bytes, header [61 04]; sensors: ADC 691→2°C, 686→3°C, absent (FFFF),
    ///     697→2°C, fifth integer reading 2°C.
    /// </summary>
    public static readonly string[] GoldenGroup04Lines =
    [
        "7BB1010610402B30202", // FF: len=16, [61 04 02 B3 02 02]
        "7BB21AE03FFFFFF02B9", // CF1: [AE 03 FF FF FF 02 B9]
        "7BB22020200FFFFFFFF" // CF2: [02 02 00 FF...]
    ];

    /// <summary>
    ///     BMS Group 06 response (cell shunt/balancing bits, TX 79B / RX 7BB).
    ///     Captured 2025-12-06 on the same 30kWh AZE0 (third-party app log).
    ///     Payload: 26 bytes, header [61 06] + 24 shunt bytes (4 cells per byte, order 8421).
    /// </summary>
    public static readonly string[] GoldenGroup06Lines =
    [
        "7BB101A61060F0E0E0E", // FF: len=26, [61 06 0F 0E 0E 0E]
        "7BB210F0A070F0F0F0E", // CF1: [0F 0A 07 0F 0F 0F 0E]
        "7BB220F0E0E0F0F0E0F", // CF2: [0F 0E 0E 0F 0F 0E 0F]
        "7BB23060E06060E0FFF" // CF3: [06 0E 06 06 0E 0F FF]
    ];

    /// <summary>
    ///     Charger/IDENT VIN response (Mode 21 PID 81, TX 797 / RX 79A).
    ///     Fake/generated VIN: 1N4AZ0CP7HC000001. Payload: 21 bytes, header [61 81].
    /// </summary>
    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    public static readonly string[] GoldenVinLines =
    [
        "79A10156181314E3441", // FF: len=0x15 (21), [61 81] + "1N4A"
        "79A215A304350374843", // CF1: "Z0CP7HC"
        "79A2230303030303100", // CF2: "308656" + 00 padding
        "79A230000000000000" // CF3: padding
    ];

    /// <summary>Joins golden lines into a raw ELM327 frame ending with the prompt.</summary>
    public static string AsElmResponse(this string[] lines) => string.Join("\r", lines) + "\r\r>";
}
