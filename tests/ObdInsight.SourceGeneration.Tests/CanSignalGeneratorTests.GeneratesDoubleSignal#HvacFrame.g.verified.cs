//HintName: HvacFrame.g.cs
#nullable enable
using System;
namespace TestNamespace
{
    /// <summary>
    /// CAN Frame decoder for ID 0x54C
    /// </summary>
    partial class HvacFrame
    {
        /// <summary>
        /// The shortest payload this frame can be decoded from (1 byte(s)) - the
        /// highest byte any of its signals touches. Frames on the wire are often shorter
        /// than 8 bytes; anything at least this long decodes.
        /// </summary>
        public static int MinimumLength => 1;
        /// <summary>
        /// Parses a CAN frame with ID 0x54C from raw payload data.
        /// </summary>
        /// <param name="data">CAN frame data, at least 1 byte(s), little-endian byte order. Bytes past the last signal are ignored.</param>
        /// <returns>Parsed HvacFrame instance</returns>
        /// <exception cref="ArgumentException">Thrown if data is shorter than 1 byte(s)</exception>
        public static HvacFrame Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length < 1)
                throw new ArgumentException($"CAN frame data must be at least 1 byte(s), got {data.Length}", nameof(data));
            return new HvacFrame
            {
                Temperature = (double)(CanBits.ReadUnsigned(data, 0, 8) * 0.25)
            };
        }
        /// <summary>
        /// Evaporator temperature
        /// </summary>
        /// <remarks>Unit: °C</remarks>
        public partial double Temperature { get => __Temperature; init => __Temperature = value; }
        private double __Temperature;
    }
}
