using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace QuickBlueToothLE
{

    public enum SensorState { INIT, QUERY_DEVICES, CONNECTED, DISCONNECTED };


    class Program
    {
        private static DeviceInformation device = null;
        private static  string CYCLING_SPEED_AND_CADENCE_SERVICE_ID = "1816";
        private static SensorState sensorState = SensorState.INIT;
        private static DeviceWatcher deviceWatcher = null;
        private static GattSession gattSession = null;
        private static Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private static IPAddress broadcast = IPAddress.Parse("127.0.0.1");
        private static IPEndPoint ep = new IPEndPoint(broadcast, 3300);
        private static UInt16 previousCumulativeCrankRevolutions = 0;
        private static UInt16 previousLastCrankEventTime = 0;
        private static UInt16 RPM = 0;


        static async Task Main(string[] args)
        {


            while (true){

                switch (sensorState)
                {
                    case SensorState.INIT:
                        {

                            // Query for extra properties you want returned
                            string[] requestedProperties = { "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected" };
                            deviceWatcher = DeviceInformation.CreateWatcher(BluetoothLEDevice.GetDeviceSelectorFromPairingState(false), requestedProperties, DeviceInformationKind.AssociationEndpoint);
                            // Register event handlers before starting the watcher.
                            // Added, Updated and Removed are required to get all nearby devices
                            deviceWatcher.Added += DeviceWatcher_Added;
                            deviceWatcher.Updated += DeviceWatcher_Updated;
                            deviceWatcher.Removed += DeviceWatcher_Removed;
                            // EnumerationCompleted and Stopped are optional to implement.
                            deviceWatcher.EnumerationCompleted += DeviceWatcher_EnumerationCompleted;
                            deviceWatcher.Stopped += DeviceWatcher_Stopped;
                            // Start the watcher.
                            deviceWatcher.Start();
                            sensorState = SensorState.QUERY_DEVICES;
                            Console.WriteLine("From init: waiting to detect cadence sensor Advertising");
                            break;
                        }
                    case SensorState.QUERY_DEVICES:
                        {
                            if(device != null)
                            {

                                BluetoothLEDevice bluetoothLeDevice = await BluetoothLEDevice.FromIdAsync(device.Id);
                                Console.WriteLine($"BLE.ID: {bluetoothLeDevice.BluetoothDeviceId.Id}");
                                Console.WriteLine($"BLE.ADDRESS: {bluetoothLeDevice.BluetoothAddress.ToString()}");
                                gattSession = await GattSession.FromDeviceIdAsync(bluetoothLeDevice.BluetoothDeviceId);
                                gattSession.SessionStatusChanged += SessionStatus_Changed;
                                //Console.WriteLine("Attempting to pair with device");
                                GattDeviceServicesResult result = await bluetoothLeDevice.GetGattServicesAsync();
                                Console.WriteLine($"Gatt Comm Status: {result.Status}");

                                if (result.Status == GattCommunicationStatus.Success)
                                {
                                    Console.WriteLine("Pairing succeeded");
                                    var services = result.Services;
                                    foreach (var service in services)
                                    {
                                        //Console.WriteLine(service.Uuid.ToString());


                                        if (service.Uuid.ToString("N").Substring(4, 4) == CYCLING_SPEED_AND_CADENCE_SERVICE_ID)
                                        {
                                            Console.WriteLine("Cycling speed and cadence services found");
                                            GattCharacteristicsResult charactiristicResult = await service.GetCharacteristicsAsync();

                                            if (charactiristicResult.Status == GattCommunicationStatus.Success)
                                            {
                                                var characteristics = charactiristicResult.Characteristics;
                                                foreach (var characteristic in characteristics)
                                                {
                                                    //Console.WriteLine(characteristic);
                                                    GattCharacteristicProperties properties = characteristic.CharacteristicProperties;

                                                    if (properties.HasFlag(GattCharacteristicProperties.Notify))
                                                    {
                                                        Console.WriteLine("CSC Measurement found");
                                                        GattCommunicationStatus status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                                                        if (status == GattCommunicationStatus.Success)
                                                        {
                                                            characteristic.ValueChanged += Characteristic_ValueChanged;
                                                            sensorState = SensorState.CONNECTED;
                                                            Console.WriteLine("Press Any Key to Exit application");
                                                        }
                                                    }

                                                }
                                            }
                                        }
                                    }
                                }


                            }

                            if (Console.KeyAvailable == true)
                            {
                                Console.ReadKey(true);
                                deviceWatcher.Stop();
                                return;

                            }

                            break;
                        }
                    case SensorState.CONNECTED:
                        {

                            if(gattSession.SessionStatus != GattSessionStatus.Active)
                            {
                                sensorState = SensorState.QUERY_DEVICES;
                                Console.WriteLine("From connected: waiting to detect cadence device Advertising");
                            }

                            if(Console.KeyAvailable == true)
                            {
                                Console.ReadKey(true);
                                deviceWatcher.Stop();
                                return;

                            }
                                break;
                        }
                    default:
                        {
                            Console.WriteLine("SensorState ERROR");
                            break;
                        }
    
                }


                Thread.Sleep(250);
            }
        }


        private static void SessionStatus_Changed(GattSession sender, GattSessionStatusChangedEventArgs args)
        {
            Console.WriteLine($"GattSession Event: {args.Status}");
        }

        private static void Characteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte[] bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);

            //Console.WriteLine(BitConverter.ToString(bytes));
            var cumulativeCrankRevolutions = BitConverter.ToUInt16(bytes, 1);
            //Console.WriteLine($"Cumulative Crank Revolutions: {cumulativeCrankRevolutions}");
            var lastCrankEventTime = BitConverter.ToUInt16(bytes, 3);
            //Console.WriteLine($"Last Crank Event Time: {lastCrankEventTime}");
            Console.WriteLine($"Cumulative Revs:{cumulativeCrankRevolutions} - Last Crank Event Time: {lastCrankEventTime}");

            var deltaRevolutions = 0;

            if( cumulativeCrankRevolutions < previousCumulativeCrankRevolutions)
            {

                deltaRevolutions = 65536 - previousCumulativeCrankRevolutions + cumulativeCrankRevolutions;

            }
            else
            {
                deltaRevolutions = cumulativeCrankRevolutions - previousCumulativeCrankRevolutions;
            }

            previousCumulativeCrankRevolutions = cumulativeCrankRevolutions;

            var deltaTime = 0;

            if (lastCrankEventTime < previousLastCrankEventTime)
            {

                deltaTime = 65536 - previousLastCrankEventTime + lastCrankEventTime;

            }
            else
            {
                deltaTime = lastCrankEventTime - previousLastCrankEventTime;
            }

            previousLastCrankEventTime = lastCrankEventTime;

            RPM = 0;
            if (deltaTime != 0) RPM =  (UInt16) ((UInt32)(60 * deltaRevolutions * 1024) / (UInt32)deltaTime);
            else RPM = 0;
            Console.WriteLine($"RPM:{RPM}");

            //s.SendTo(bytes, ep);

            s.SendTo(BitConverter.GetBytes(RPM), ep);
        }

        private static void DeviceWatcher_Stopped(DeviceWatcher sender, object args)
        {
            Console.WriteLine("Device Stopped");
            //throw new NotImplementedException();
        }

        private static void DeviceWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            //Console.WriteLine("Device Enumeration Completed");
            //throw new NotImplementedException();
        }

        private static void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            //Console.WriteLine("Device Removed");
            //throw new NotImplementedException();
        }

        private static void DeviceWatcher_Updated(DeviceWatcher sender, DeviceInformationUpdate args)
        {
            //Console.WriteLine("Device Updated");
            //throw new NotImplementedException();
        }

        private static void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation args)
        {
            //Console.WriteLine(args.Name);

            if (args.Name == "Bryton Cadence")
            {
                device = args;
                Console.WriteLine("Bryton Cadence detected");
                var deviceID = device.Id;
                Console.WriteLine($"Device id: {deviceID}");
            }
            //throw new NotImplementedException();
        }
    }
}
