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
        /// Parses a CAN frame with ID 0x54C from raw 8-byte data.
        /// </summary>
        /// <param name="data">8-byte CAN frame data (little-endian byte order)</param>
        /// <returns>Parsed HvacFrame instance</returns>
        /// <exception cref="ArgumentException">Thrown if data length is not 8 bytes</exception>
        public static HvacFrame Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length != 8)
                throw new ArgumentException($"CAN frame data must be exactly 8 bytes, got {data.Length}", nameof(data));
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
