//HintName: TestFrame.g.cs
#nullable enable
using System;
namespace TestNamespace
{
    /// <summary>
    /// CAN Frame decoder for ID 0x54C
    /// </summary>
    partial class TestFrame
    {
        /// <summary>
        /// Parses a CAN frame with ID 0x54C from raw 8-byte data.
        /// </summary>
        /// <param name="data">8-byte CAN frame data (little-endian byte order)</param>
        /// <returns>Parsed TestFrame instance</returns>
        /// <exception cref="ArgumentException">Thrown if data length is not 8 bytes</exception>
        public static TestFrame Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length != 8)
                throw new ArgumentException($"CAN frame data must be exactly 8 bytes, got {data.Length}", nameof(data));
            return new TestFrame
            {
                OutsideTemp = (double)((CanBits.ReadUnsigned(data, 48, 8) * 0.5) + -40)
            };
        }
        /// <summary>
        /// Outside ambient temperature
        /// </summary>
        /// <remarks>Unit: °C</remarks>
        public partial double OutsideTemp { get => __OutsideTemp; init => __OutsideTemp = value; }
        private double __OutsideTemp;
    }
}
