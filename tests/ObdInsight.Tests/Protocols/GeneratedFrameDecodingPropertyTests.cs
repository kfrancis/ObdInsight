using System.Reflection;
using CsCheck;
using ObdInsight.Core.Protocols;
using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.Tests.Protocols;

/// <summary>
///     Property-based tests (CsCheck) covering every source-generated CAN frame decoder in Core at
///     once: for random payloads, each <c>[CanSignal]</c> property must equal what the signal's own
///     declared bit layout says it should be.
///     This is differential testing, not a second parser: the oracle below is the DBC decode rule
///     (little-endian raw bits, sign-extended when signed, then <c>raw * Factor + Offset</c>) applied
///     to metadata read off the attribute at runtime, while the value under test comes from generated
///     code. The two agree only if the generator implements that rule, so a regression in bit
///     extraction, sign extension or scaling fails here for every affected signal rather than only
///     where someone wrote a hand-computed example.
/// </summary>
[Timeout(120_000)]
public class GeneratedFrameDecodingPropertyTests
{
    private const int Iterations = 200;

    /// <summary>Matches the generator's own threshold for "this signal needs scaling".</summary>
    private const double ScalingTolerance = 1e-12;

    /// <summary>Every generated frame decoder in Core, paired with its signals. Built once.</summary>
    private static readonly FrameUnderTest[] Frames = DiscoverFrames();

    /// <summary>Full-length CAN payloads.</summary>
    private static readonly Gen<byte[]> PayloadGen = Gen.Byte.Array[8, 8];

    [Test]
    public async Task DiscoveryFoundTheFrames(CancellationToken _)
    {
        // Guards the reflection below: a rename that silently matched nothing would make every
        // other test in this class vacuous.
        await Assert.That(Frames.Length).IsGreaterThan(20);
        await Assert.That(Frames.Sum(f => f.Signals.Length)).IsGreaterThan(200);
    }

    [Test]
    public async Task EverySignal_DecodesToItsDeclaredBitLayout(CancellationToken _)
    {
        PayloadGen.Sample(payload =>
            {
                foreach (var frame in Frames)
                {
                    var decoded = frame.Decode(payload);
                    foreach (var signal in frame.Signals)
                    {
                        if (!Matches(frame, signal, payload, decoded))
                        {
                            return false;
                        }
                    }
                }

                return true;
            },
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ShortPayloads_DecodeLikeTheirZeroExtendedForm(CancellationToken _)
    {
        // Frames on the wire are often shorter than 8 bytes. The generated reader zero-extends
        // them, so a truncated payload must decode exactly like the same bytes padded with zeros.
        PayloadGen.Select(Gen.Int[1, 8]).Sample(t =>
            {
                var (payload, length) = t;
                var truncated = payload.Take(length).ToArray();
                var padded = truncated.Concat(new byte[8 - length]).ToArray();

                foreach (var frame in Frames.Where(f => f.MinimumLength <= length))
                {
                    var fromTruncated = frame.Decode(truncated);
                    var fromPadded = frame.Decode(padded);
                    foreach (var signal in frame.Signals)
                    {
                        if (!Equals(signal.Property.GetValue(fromTruncated), signal.Property.GetValue(fromPadded)))
                        {
                            return false;
                        }
                    }
                }

                return true;
            },
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task PayloadsShorterThanMinimumLength_AreRejected(CancellationToken _)
    {
        PayloadGen.Select(Gen.Int[0, 7]).Sample(t =>
            {
                var (payload, length) = t;
                foreach (var frame in Frames.Where(f => length < f.MinimumLength))
                {
                    try
                    {
                        frame.Decode(payload.Take(length).ToArray());
                        return false;
                    }
                    catch (ArgumentException)
                    {
                        // Expected: the frame documents this as its contract.
                    }
                }

                return true;
            },
            iter: Iterations);

        await Task.CompletedTask;
    }

    /// <summary>Compares one generated property value against the DBC decode of the same bits.</summary>
    private static bool Matches(FrameUnderTest frame, SignalUnderTest signal, byte[] payload, object decoded)
    {
        var actual = signal.Property.GetValue(decoded);
        var attribute = signal.Attribute;
        var unsigned = attribute.ByteOrder == CanByteOrder.Motorola
            ? ReadUnsignedMotorola(payload, attribute.BitStart, attribute.BitLength)
            : ReadUnsigned(payload, attribute.BitStart, attribute.BitLength);

        // A multiplexed signal exists only in frames whose selector chooses its variant. When it
        // does not, the only correct answer is null - and asserting that is the point, since the
        // bug this replaced was three variants of a group all decoding the same bits and
        // returning the same number regardless of the selector.
        if (attribute.MuxValue != CanSignalAttribute.NotMultiplexed)
        {
            var multiplexor = frame.Signals.FirstOrDefault(s => s.Attribute.IsMultiplexor);
            if (multiplexor is null)
            {
                return false; // generator should have rejected this frame outright
            }

            var muxAttribute = multiplexor.Attribute;
            var selector = muxAttribute.ByteOrder == CanByteOrder.Motorola
                ? ReadUnsignedMotorola(payload, muxAttribute.BitStart, muxAttribute.BitLength)
                : ReadUnsigned(payload, muxAttribute.BitStart, muxAttribute.BitLength);

            if (selector != attribute.MuxValue)
            {
                return actual is null;
            }
        }

        // Nullable properties box to their underlying type once populated, so the comparisons
        // below work unchanged for an active multiplexed signal.
        if (Nullable.GetUnderlyingType(signal.Property.PropertyType) is { } underlying)
        {
            return MatchesValue(actual, underlying, attribute, unsigned);
        }

        return MatchesValue(actual, signal.Property.PropertyType, attribute, unsigned);
    }

    private static bool MatchesValue(object? actual, Type type, CanSignalAttribute attribute, uint unsigned)
    {
        if (type == typeof(bool))
        {
            return Equals(actual, unsigned != 0);
        }

        double raw = attribute.IsSigned ? SignExtend(unsigned, attribute.BitLength) : unsigned;
        var needsScaling =
            Math.Abs(attribute.Factor - 1.0) > ScalingTolerance ||
            Math.Abs(attribute.Offset) > ScalingTolerance;

        if (type == typeof(double))
        {
            var expected = needsScaling ? raw * attribute.Factor + attribute.Offset : raw;
            return actual is double d && Math.Abs(d - expected) <= 1e-9 * Math.Max(1.0, Math.Abs(expected));
        }

        if (type == typeof(int))
        {
            // Unscaled signals are cast straight from the raw integer, so a 32-bit unsigned signal
            // wraps rather than saturating; scaled ones go through double and truncate.
            var expected = needsScaling
                ? (int)(raw * attribute.Factor + attribute.Offset)
                : attribute.IsSigned
                    ? SignExtend(unsigned, attribute.BitLength)
                    : unchecked((int)unsigned);

            return Equals(actual, expected);
        }

        throw new NotSupportedException(
            $"Signal type {type.Name} is unhandled; extend this oracle.");
    }

    /// <summary>Reads a little-endian bit field, zero-extending payloads shorter than 8 bytes.</summary>
    private static uint ReadUnsigned(byte[] payload, int bitStart, int bitLength)
    {
        ulong raw = 0;
        for (var i = 0; i < payload.Length && i < 8; i++)
        {
            raw |= (ulong)payload[i] << (i * 8);
        }

        var mask = bitLength == 32 ? 0xFFFF_FFFFul : (1ul << bitLength) - 1ul;
        return (uint)((raw >> bitStart) & mask);
    }

    /// <summary>
    ///     Reads a Motorola (DBC <c>@0</c>) bit field by walking the bits the way the format
    ///     describes, rather than by the shift-and-mask the production reader uses.
    /// </summary>
    /// <remarks>
    ///     The start bit is the signal's most significant bit. Each subsequent bit is the next one
    ///     down within the byte; on falling below bit 0 the walk continues at bit 7 of the following
    ///     byte. Written literally, one bit at a time, so this oracle is an independent statement of
    ///     the rule - agreeing with the production reader is then evidence its
    ///     <c>64 - (msbIndex + bitLen)</c> shift is the same thing, not merely evidence that two
    ///     copies of the same arithmetic agree.
    /// </remarks>
    private static uint ReadUnsignedMotorola(byte[] payload, int bitStart, int bitLength)
    {
        uint value = 0;
        var byteIndex = bitStart / 8;
        var bitInByte = bitStart % 8;

        for (var i = 0; i < bitLength; i++)
        {
            var bit = byteIndex < payload.Length && byteIndex < 8
                ? (payload[byteIndex] >> bitInByte) & 1
                : 0; // short payloads zero-extend, matching the generated reader

            value = (value << 1) | (uint)bit;

            if (--bitInByte < 0)
            {
                bitInByte = 7;
                byteIndex++;
            }
        }

        return value;
    }

    private static int SignExtend(uint value, int bitLength)
    {
        var signBit = 1u << (bitLength - 1);
        if ((value & signBit) == 0)
        {
            return (int)value;
        }

        return (int)(value | (bitLength == 32 ? 0u : ~((1u << bitLength) - 1)));
    }

    /// <summary>
    ///     Finds every <c>[CanFrame]</c> type in Core and binds a delegate to its generated
    ///     <c>Parse</c>. Reflection cannot invoke a <c>ReadOnlySpan&lt;byte&gt;</c> parameter
    ///     directly, so calls route through <see cref="DecodeVia{T}" />.
    /// </summary>
    private static FrameUnderTest[] DiscoverFrames()
    {
        var decodeVia = typeof(GeneratedFrameDecodingPropertyTests)
            .GetMethod(nameof(DecodeVia), BindingFlags.NonPublic | BindingFlags.Static)!;

        return typeof(ICanFrame<>).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttribute<CanFrameAttribute>() is not null)
            .Where(t => t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICanFrame<>)))
            .Select(t => new FrameUnderTest(
                t,
                decodeVia.MakeGenericMethod(t).CreateDelegate<Func<byte[], object>>(),
                (int)t.GetProperty("MinimumLength")!.GetValue(null)!,
                t.GetProperties()
                    .Select(p => (Property: p, Attribute: p.GetCustomAttribute<CanSignalAttribute>()))
                    .Where(p => p.Attribute is { IncludeInGeneration: true })
                    .Select(p => new SignalUnderTest(p.Property, p.Attribute!))
                    .ToArray()))
            .Where(f => f.Signals.Length > 0)
            .ToArray();
    }

    private static object DecodeVia<T>(byte[] payload) where T : ICanFrame<T> => T.Parse(payload)!;

    private sealed record FrameUnderTest(
        Type Type,
        Func<byte[], object> Decode,
        int MinimumLength,
        SignalUnderTest[] Signals);

    private sealed record SignalUnderTest(PropertyInfo Property, CanSignalAttribute Attribute);
}
