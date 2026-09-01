//HintName: HistoryFrame.g.cs
#nullable enable
using System;
namespace TestNamespace
{
    /// <summary>
    /// CAN Frame decoder for ID 0x5C0
    /// </summary>
    partial class HistoryFrame
    {
        /// <summary>
        /// The shortest payload this frame can be decoded from (6 byte(s)) - the
        /// highest byte any of its signals touches. Frames on the wire are often shorter
        /// than 8 bytes; anything at least this long decodes.
        /// </summary>
        public static int MinimumLength => 6;
        /// <summary>
        /// Parses a CAN frame with ID 0x5C0 from raw payload data.
        /// </summary>
        /// <param name="data">CAN frame data, at least 6 byte(s), little-endian byte order. Bytes past the last signal are ignored.</param>
        /// <returns>Parsed HistoryFrame instance</returns>
        /// <exception cref="ArgumentException">Thrown if data is shorter than 6 byte(s)</exception>
        public static HistoryFrame Parse(ReadOnlySpan<byte> data)
        {
            if (data.Length < 6)
                throw new ArgumentException($"CAN frame data must be at least 6 byte(s), got {data.Length}", nameof(data));
            // Multiplexor: signals tagged with a MuxValue exist only when this matches.
            var __mux = (int)(CanBits.ReadUnsigned(data, 6, 2));
            return new HistoryFrame
            {
                HistoricalDataSwitchFlag = (int)(CanBits.ReadUnsigned(data, 6, 2)),
                TemperatureMax = __mux == 1 ? (double?)((double)(CanBits.ReadUnsigned(data, 17, 7) + -40)) : null,
                TemperatureMin = __mux == 3 ? (double?)((double)(CanBits.ReadUnsigned(data, 17, 7) + -40)) : null,
                CellVoltageAvg = __mux == 2 ? (int?)((int)((CanBits.ReadUnsigned(data, 42, 6) * 40) + 1900)) : null,
                AlwaysPresent = (int)(CanBits.ReadUnsigned(data, 24, 8))
            };
        }
        /// <summary>
        /// Selects which history variant this frame carries
        /// </summary>
        public partial int HistoricalDataSwitchFlag { get => __HistoricalDataSwitchFlag; init => __HistoricalDataSwitchFlag = value; }
        private int __HistoricalDataSwitchFlag;
        /// <summary>
        /// Highest recorded pack temperature
        /// </summary>
        /// <remarks>Unit: degC</remarks>
        public partial double? TemperatureMax { get => __TemperatureMax; init => __TemperatureMax = value; }
        private double? __TemperatureMax;
        /// <summary>
        /// Lowest recorded pack temperature
        /// </summary>
        /// <remarks>Unit: degC</remarks>
        public partial double? TemperatureMin { get => __TemperatureMin; init => __TemperatureMin = value; }
        private double? __TemperatureMin;
        /// <summary>
        /// Average recorded cell voltage
        /// </summary>
        /// <remarks>Unit: mV</remarks>
        public partial int? CellVoltageAvg { get => __CellVoltageAvg; init => __CellVoltageAvg = value; }
        private int? __CellVoltageAvg;
        /// <summary>
        /// Present in every frame regardless of the selector
        /// </summary>
        public partial int AlwaysPresent { get => __AlwaysPresent; init => __AlwaysPresent = value; }
        private int __AlwaysPresent;
    }
}
