//HintName: BatteryFrame.g.cs
#nullable enable
using System;
namespace TestNamespace
{
    /// <summary>
    /// CAN Frame decoder for ID 0x1DB
    /// </summary>
    partial class BatteryFrame : global::ObdInsight.Core.Protocols.ICanFrame<BatteryFrame>
    {
        /// <summary>
        /// The CAN ID this frame decodes (0x1DB).
        /// </summary>
        public static int FrameCanId => 0x1DB;
        /// <summary>
        /// The shortest payload this frame can be decoded from (5 byte(s)) - the
        /// highest byte any of its signals touches. Frames on the wire are often shorter
        /// than 8 bytes; anything at least this long decodes.
        /// </summary>
        public static int MinimumLength => 5;
        /// <summary>
        /// Parses a CAN frame with ID 0x1DB from raw payload data.
        /// </summary>
        /// <param name="data">CAN frame data, at least 5 byte(s), little-endian byte order. Bytes past the last signal are ignored.</param>
        /// <returns>Parsed BatteryFrame instance</returns>
        /// <exception cref="ArgumentException">Thrown if data is shorter than 5 byte(s)</exception>
        public static BatteryFrame Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length < 5)
                throw new ArgumentException($"CAN frame data must be at least 5 byte(s), got {data.Length}", nameof(data));
            return new BatteryFrame
            {
                Voltage = (double)(CanBits.ReadUnsigned(data, 30, 10) * 0.5)
            };
        }
        /// <summary>
        /// Signal at bit 30, length 10
        /// </summary>
        /// <remarks>Unit: V</remarks>
        public partial double Voltage { get => __Voltage; init => __Voltage = value; }
        private double __Voltage;
    }
}
