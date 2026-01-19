//HintName: HvacFrame.g.cs
#nullable enable
using System;
namespace TestNamespace
{
    /// <summary>
    /// HVAC status frame
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
                EvaporatorTemp = (double)(CanBits.ReadUnsigned(data, 0, 8) * 0.25),
                RearDefrostOn = CanBits.ReadBool(data, 9),
                ClimateControlOn = CanBits.ReadBool(data, 10),
                AcOn = CanBits.ReadBool(data, 11),
                FanVoltage = (double)(CanBits.ReadUnsigned(data, 40, 8) * 0.05),
                OutsideTemp = (double)((CanBits.ReadUnsigned(data, 48, 8) * 0.5) + -40)
            };
        }
        /// <summary>
        /// Evaporator temperature
        /// </summary>
        /// <remarks>Unit: °C</remarks>
        public partial double EvaporatorTemp { get => __EvaporatorTemp; init => __EvaporatorTemp = value; }
        private double __EvaporatorTemp;
        /// <summary>
        /// Rear defrost active
        /// </summary>
        public partial bool RearDefrostOn { get => __RearDefrostOn; init => __RearDefrostOn = value; }
        private bool __RearDefrostOn;
        /// <summary>
        /// Climate control enabled
        /// </summary>
        public partial bool ClimateControlOn { get => __ClimateControlOn; init => __ClimateControlOn = value; }
        private bool __ClimateControlOn;
        /// <summary>
        /// A/C compressor active
        /// </summary>
        public partial bool AcOn { get => __AcOn; init => __AcOn = value; }
        private bool __AcOn;
        /// <summary>
        /// Fan voltage
        /// </summary>
        /// <remarks>Unit: V</remarks>
        public partial double FanVoltage { get => __FanVoltage; init => __FanVoltage = value; }
        private double __FanVoltage;
        /// <summary>
        /// Outside ambient temperature
        /// </summary>
        /// <remarks>Unit: °C</remarks>
        public partial double OutsideTemp { get => __OutsideTemp; init => __OutsideTemp = value; }
        private double __OutsideTemp;
    }
}
