//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Reflection.PortableExecutable;
//using System.Text;
//using System.Threading.Tasks;

//namespace ObdInsight.Core
//{
//    public class BluetoothLeTransport : IObdTransport
//    {
//        private IDevice? _device;
//        private ICharacteristic? _writeCharacteristic;
//        private ICharacteristic? _notifyCharacteristic;
//        private readonly StringBuilder _buffer = new();

//        // BLE-specific: Nordic UART Service (common for OBD dongles)
//        // UUID: 6E400001-B5A3-F393-E0A9-E50E24DCCA9E
//        // TX: 6E400002-B5A3-F393-E0A9-E50E24DCCA9E
//        // RX: 6E400003-B5A3-F393-E0A9-E50E24DCCA9E
//    }
//}
