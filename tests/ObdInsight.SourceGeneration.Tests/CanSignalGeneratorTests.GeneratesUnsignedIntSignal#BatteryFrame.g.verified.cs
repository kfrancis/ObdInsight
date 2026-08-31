//HintName: BatteryFrame.g.cs
#nullable enable
using System;
namespace TestNamespace
{
    /// <summary>
    /// CAN Frame decoder for ID 0x1DB
    /// </summary>
    partial class BatteryFrame
    {
        /// <summary>
        /// The shortest payload this frame can be decoded from (6 byte(s)) - the
        /// highest byte any of its signals touches. Frames on the wire are often shorter
        /// than 8 bytes; anything at least this long decodes.
        /// </summary>
        public static int MinimumLength => 6;
        /// <summary>
        /// Parses a CAN frame with ID 0x1DB from raw payload data.
        /// </summary>
        /// <param name="data">CAN frame data, at least 6 byte(s), little-endian byte order. Bytes past the last signal are ignored.</param>
        /// <returns>Parsed BatteryFrame instance</returns>
        /// <exception cref="ArgumentException">Thrown if data is shorter than 6 byte(s)</exception>
        public static BatteryFrame Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length < 6)
                throw new ArgumentException($"CAN frame data must be at least 6 byte(s), got {data.Length}", nameof(data));
            return new BatteryFrame
            {
                AvailableCapacity = (int)(CanBits.ReadUnsigned(data, 32, 10))
            };
        }
        /// <summary>
        /// Available capacity
        /// </summary>
        /// <remarks>Unit: Gids</remarks>
        public partial int AvailableCapacity { get => __AvailableCapacity; init => __AvailableCapacity = value; }
        private int __AvailableCapacity;
    }
}
