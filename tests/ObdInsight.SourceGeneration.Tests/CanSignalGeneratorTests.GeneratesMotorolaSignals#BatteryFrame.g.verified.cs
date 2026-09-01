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
                Current = (double)(CanBits.ReadSignedBe(data, 7, 11) * 0.5),
                Soc = (int)(CanBits.ReadUnsignedBe(data, 7, 10)),
                RelayCutRequested = CanBits.ReadBoolBe(data, 11),
                IntelByte = (int)(CanBits.ReadUnsigned(data, 32, 8))
            };
        }
        /// <summary>
        /// Pack current, DBC big-endian
        /// </summary>
        /// <remarks>Unit: A</remarks>
        public partial double Current { get => __Current; init => __Current = value; }
        private double __Current;
        /// <summary>
        /// State of charge, DBC big-endian
        /// </summary>
        public partial int Soc { get => __Soc; init => __Soc = value; }
        private int __Soc;
        /// <summary>
        /// Relay cut request
        /// </summary>
        public partial bool RelayCutRequested { get => __RelayCutRequested; init => __RelayCutRequested = value; }
        private bool __RelayCutRequested;
        /// <summary>
        /// Left as Intel, to prove the default holds
        /// </summary>
        public partial int IntelByte { get => __IntelByte; init => __IntelByte = value; }
        private int __IntelByte;
    }
}
