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
