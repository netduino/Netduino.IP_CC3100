using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using System;
using System.IO.Ports;

namespace Netduino.IP.LinkLayers
{
    class CC3100UartTransport : ICC3100Transport 
    {
        bool _isDisposed = false;

        // our SerialPort instance
        SerialPort _serialPort = null;
        // our SerialPort lock object (NOTE: we lock this whenever we use our UART or shared UART-related buffers)
        object _serialPortLockObject = new object();

        //InterruptPort _interruptPin = null;

        // our Write function needs a lock object so that its callers are queued.
        object _writeFunctionLockObject = new object();

        public event CC3100DataReceivedEventHandler DataReceived;

        public CC3100UartTransport(string portName, int baudRate, Parity parity, int dataBits, StopBits stopBits, Cpu.Pin intPinID)
        {
            // create our SerialPort object
            _serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits);
            _serialPort.Handshake = Handshake.RequestToSend;
            _serialPort.Open();

            // wire up our interrupt
            //_interruptPin = new InterruptPort(intPinID, true, Port.ResistorMode.PullDown, Port.InterruptMode.InterruptEdgeHigh);
            //_interruptPin.OnInterrupt += _interruptPin_OnInterrupt;
            //_interruptPin.EnableInterrupt();

            // start processing incoming data.
            _serialPort.DataReceived += _serialPort_DataReceived;
        }

        void _serialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            DataReceived(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _isDisposed = true;

            //_interruptPin.Dispose();

            _serialPort.Dispose();
        }

        public int BytesToRead
        {
            get
            {
                lock (_serialPortLockObject)
                {
                    try
                    {
                        return _serialPort.BytesToRead;
                    }
                    catch (ArgumentException)
                    {
                        // workaround for NETMF bug
                        return 1;
                    }
                }
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            lock (_serialPortLockObject)
            {
                return _serialPort.Read(buffer, offset, count);
            }
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            lock (_serialPortLockObject)
            {
                _serialPort.Write(buffer, offset, count);
            }
        }

    }
}
