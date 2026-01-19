//HintName: BatteryFrame.g.cs
#nullable enable
using System;
namespace Vehicles.Nissan.Leaf
{
    /// <summary>
    /// CAN Frame decoder for ID 0x1DB
    /// </summary>
    partial class BatteryFrame
    {
        /// <summary>
        /// Parses a CAN frame with ID 0x1DB from raw 8-byte data.
        /// </summary>
        /// <param name="data">8-byte CAN frame data (little-endian byte order)</param>
        /// <returns>Parsed BatteryFrame instance</returns>
        /// <exception cref="ArgumentException">Thrown if data length is not 8 bytes</exception>
        public static BatteryFrame Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length != 8)
                throw new ArgumentException($"CAN frame data must be exactly 8 bytes, got {data.Length}", nameof(data));
            return new BatteryFrame
            {
                Current = (double)(CanBits.ReadUnsigned(data, 0, 16) * 0.5)
            };
        }
        /// <summary>
        /// Signal at bit 0, length 16
        /// </summary>
        /// <remarks>Unit: A</remarks>
        public partial double Current { get => __Current; init => __Current = value; }
        private double __Current;
    }
}
