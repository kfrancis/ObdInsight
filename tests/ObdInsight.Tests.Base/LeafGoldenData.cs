using System.Diagnostics.CodeAnalysis;

namespace ObdInsight.Tests.Base;

/// <summary>
/// Golden ELM327 response lines captured from a real Nissan Leaf AZE0.
/// Data only — parsing belongs to production code (LeafBmsDiagnostics, IsoTpParser).
/// </summary>
public static class LeafGoldenData
{
    /// <summary>
    /// BMS Group 01 response (Mode 21 PID 01, TX 79B / RX 7BB).
    /// Captured 2026-01-18. 30kWh-format payload: 43 bytes, header [61 01].
    /// </summary>
    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    public static readonly string[] GoldenGroup01Lines =
    [
        "7BB102B6101000000EB",  // FF: len=43, [61 01 00 00 00 EB]
        "7BB21028AFFFFFD5AFF",  // CF1: [02 8A FF FF FD 5A FF]
        "7BB22FFFFFF07F220AC",  // CF2: [FF FF FF 07 F2 20 AC]
        "7BB238D52386C039201",  // CF3: [8D 52 38 6C 03 92 01]
        "7BB244E0DD80006658A",  // CF4: [4E 0D D8 00 06 65 8A]
        "7BB25000805C1800005",  // CF5: [00 08 05 C1 80 00 05]
        "7BB260000FFFFFFFFFF",  // CF6: [00 00 FF...]
    ];

    /// <summary>
    /// Charger/IDENT VIN response (Mode 21 PID 81, TX 797 / RX 79A).
    /// Fake/generated VIN: 1N4AZ0CP7HC308656. Payload: 21 bytes, header [61 81].
    /// </summary>
    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    public static readonly string[] GoldenVinLines =
    [
        "79A10156181314E3441",  // FF: len=0x15 (21), [61 81] + "1N4A"
        "79A215A304350374843",  // CF1: "Z0CP7HC"
        "79A2233303836353600",  // CF2: "308656" + 00 padding
        "79A230000000000000",   // CF3: padding
    ];

    /// <summary>Joins golden lines into a raw ELM327 frame ending with the prompt.</summary>
    public static string AsElmResponse(this string[] lines) => string.Join("\r", lines) + "\r\r>";
}
