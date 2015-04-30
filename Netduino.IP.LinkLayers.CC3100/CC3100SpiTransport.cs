using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using System;
using System.Threading;

namespace Netduino.IP.LinkLayers
{
    class CC3100SpiTransport : ICC3100Transport 
    {
        /* NOTES for CC3100 SPI transport
         * 
         * The CC3100 SPI transport is unique in that it treats SPI much like UART.  Bytes received via the MISO pin are just like UART_RX bytes and can arrive during
         * any arbitrary SPI WriteRead transaction.  
         * 
         * This SPI transport is written with the assumption that there are no set rules as to when the data can start arriving.
         * 
         * This SPI transport eliminates duplicate SyncWord blocks and also removes dummy batches of 0x00 and 0xFF bytes (and any other pre-syncword garbage data)
         * 
         * When the CC3100 asserts its INT pin, this class sets its ReadResponsePendingStage to Stage1.  Stage1 is designed to ALWAYS request DEFAULT_BYTES_TO_REQUEST
         * whenever no data is arriving and the INT pin has been asserted.  The PendingStage then moves to Stage2 which continues requesting data until valid bytes are
         * received--at which point the regular logic takes over.  This is the only reasonably-assured way that we can reliably know we received our INT-trigger data.
         * 
         * Our SPI transport does read the LENGTH value out of incoming frames to determine how many more bytes need to be read--and then requests those bytes automatically.
         */

        /* KNOWN ISSUES
         * 
         * 1. If we receive a frame with a valid syncword but an invalid length value, we could potentially wait forever for that frame to arrive; this would also cause us to
         *    never raise the DataReceived event.  This issue would only be cleared up when enough new frames were received to meet the "len" value.  This of course should not
         *    be an issue if we do not have data corruption.
         *    
         * 2. Due to a bug in the NETMF runtime, _interruptPin_OnInterrupt may take an extra ~900ms to fire the first time (if no other InterruptPort events have fired already).
         *    Our startup time may thus be ~1,000ms instead of ~100ms.  There is no good workaround; this must be fixed in the NETMF runtime.
         */

        bool _isDisposed = false;

        // our SPI bus instance
        SPI _spi = null;
        // our SPI bus lock object (NOTE: we lock this whenever we use our SPI bus or shared bus-related buffers)
        object _spiLockObject = new object();

        OutputPort _chipSelectPin = null;
        const bool _chipSelectActiveLevel = false;
        InterruptPort _interruptPin = null;

        /* TODO: identify the largest data block size allowed by CC3100--and modify this buffer size accordingly (to be at least as big as two full blocks--and 3x */
        const int CC3100_DATA_BLOCK_MAX_SIZE = 1500 + SYNC_WORD_SIZE + 4; // we are guessing here on the maximum size of a CC3100 data block; 4 bytes refers to OPCODE and LENGTH size
        const int INCOMING_BUFFER_SIZE = CC3100_DATA_BLOCK_MAX_SIZE * 2; // total size of incoming data buffer
        byte[] _incomingDataBuffer = new byte[INCOMING_BUFFER_SIZE]; // incoming data buffer
        int _incomingDataBufferFirstAvailableIndex = 0; // the position in the incoming bytes buffer to begin filling with data
        int _incomingDataBufferEndOfLastCompleteFrame = 0; // the position in the incoming bytes buffer where our currently-being-retrieved frame begins
        object _incomingDataBufferLockObject = new object();

        const int DEFAULT_BYTES_TO_REQUEST = 24; // number of bytes to request via SPI if we do not know how many more bytes are in our current frame.

        // buffer used for dummy SPI writes (i.e. when we need to pass in SOME write buffer to Spi.WriteRead but we are realistically only reading data)
        byte[] _emptyBuffer = new byte[0];

        bool _isCurrentlyReading = false;
        // we have a master bus lock object to make sure we are only reading or writing at the same time (and also to queue up caller write requests)
        object _masterBusLockObject = new object();

        // our _notifyDataReceived WaitHandle; this is set whenever we need to raise a DataReceived event.
        AutoResetEvent _notifyDataReceivedWaitHandle = new AutoResetEvent(false);
        // our _notifyDataReceived thread; this runs while our connection is open.
        Thread _notifyDataReceivedThread = null;
        public event CC3100DataReceivedEventHandler DataReceived;

        const int SYNC_WORD_SIZE = 4;
		//byte[] _syncWordWrite = new byte[SYNC_WORD_SIZE] { 0x43, 0x21, 0x34, 0x12 }; /* use network byte order */
		byte[] _syncWordWrite = new byte[SYNC_WORD_SIZE] { 0x21, 0x43, 0x34, 0x12 }; /* use the same byte order for 'default UART' and SPI */
		//byte[] _syncWordRead = new byte[SYNC_WORD_SIZE] { 0x87, 0x65, 0x78, 0x56 }; /* use network byte order */
		byte[] _syncWordRead = new byte[SYNC_WORD_SIZE] { 0x65, 0x87, 0x78, 0x56 };	/* use the same byte order for 'default UART' and SPI */
		//byte[] _syncWordIncoming = new byte[SYNC_WORD_SIZE] { 0xAB, 0xCD, 0xDC, 0xBC }; /* use network byte order */
		byte[] _syncWordIncoming = new byte[SYNC_WORD_SIZE] { 0xBC, 0xDC, 0xCD, 0xAB };	 /* use the same byte order for 'default UART' and SPI */
		//const UInt32 _syncWordIncomingMask = 0xFCFFFFFF; /* use network byte order */ // reversed byte order (due to 32-bit network byte order to LE conversion)
		const UInt32 _syncWordIncomingMask = 0xFFFFFFFC; /* use the same byte order for 'default UART' and SPI */ // reversed byte order (due to 32-bit network byte order to LE conversion)
		const byte _syncWordIncomingMaskFirstByte = (byte)(_syncWordIncomingMask & 0xFF); // lowest 8 bits of word mask
        const int OPCODE_FIELD_SIZE = 2;
        const int LENGTH_FIELD_SIZE = 2;

        bool _isFirstReadRequestAfterInit = false;

        public CC3100SpiTransport(SPI.SPI_module spiBusID, Cpu.Pin csPinID, Cpu.Pin intPinID)
        {
            // create our chip select pin and SPI bus objects
            _chipSelectPin = new OutputPort(csPinID, !_chipSelectActiveLevel);
            _spi = new SPI(new SPI.Configuration(Cpu.Pin.GPIO_NONE, false, 0, 0, false, true, 20000, spiBusID));

            // wire up our interrupt
            _interruptPin = new InterruptPort(intPinID, true, Port.ResistorMode.PullDown, Port.InterruptMode.InterruptEdgeBoth);
            _interruptPin.OnInterrupt += _interruptPin_OnInterrupt;
            _interruptPin.EnableInterrupt();

            // start our ProcessIncomingData thread.
            _notifyDataReceivedThread = new Thread(NotifyDataReceived);
            _notifyDataReceivedThread.Start();
        }

        public void Dispose()
        {
            _isDisposed = true;

            _interruptPin.Dispose();
            _spi.Dispose();
            _chipSelectPin.Dispose();

            // abort our data received event-raising thread
            _notifyDataReceivedWaitHandle.Set();
        }

        public int BytesToRead
        {
            get
            {
                lock (_incomingDataBufferLockObject)
                {
                    return _incomingDataBufferEndOfLastCompleteFrame;
                }
            }
        }

        // NOTE: Read is the ONLY function allowed to remove valid data from the incoming data buffer
        public int Read(byte[] buffer, int offset, int count)
        {
            // validate that our buffer is large enough for the requested amount of data
            if (buffer.Length < offset + count)
                throw new ArgumentException();

            int bytesToCopy = 0;
            lock (_incomingDataBufferLockObject)
            {
                bytesToCopy = System.Math.Min(count, _incomingDataBufferEndOfLastCompleteFrame);
                Array.Copy(_incomingDataBuffer, 0, buffer, offset, bytesToCopy);
                Array.Copy(_incomingDataBuffer, bytesToCopy, _incomingDataBuffer, 0, _incomingDataBufferFirstAvailableIndex - bytesToCopy);
                _incomingDataBufferFirstAvailableIndex -= bytesToCopy;
                _incomingDataBufferEndOfLastCompleteFrame -= bytesToCopy;
            }

            return bytesToCopy;
        }

        // NOTE: this function has a large function-specific lock around it so that Write calls are processed sequentially.
        public void Write(byte[] buffer, int offset, int count)
        {
            // lock the master bus lock to ensure that no read operations are executing; this also has the effect of queueing up writes gracefully.
            Monitor.Enter(_masterBusLockObject);
            try
            {
                // write the data
                lock (_spiLockObject)
                {
                    _chipSelectPin.Write(_chipSelectActiveLevel);
                    _spi.WriteRead(buffer, offset, count, null, 0, 0, 0);
                    _chipSelectPin.Write(!_chipSelectActiveLevel);
                }
            }
            finally
            {
                // write operation complete; release the master bus lock.
                Monitor.Exit(_masterBusLockObject);
            }
        }

        internal void SetIsFirstReadCommandAfterInit()
        {
            _isFirstReadRequestAfterInit = true;
        }

        void _interruptPin_OnInterrupt(uint pin, uint level, DateTime time)
        {
            if (_isDisposed)
                return;

            if (_isFirstReadRequestAfterInit)
            {
                // wait for SPI to stabilize after first command is sent
                Thread.Sleep(10);
                _isFirstReadRequestAfterInit = false;
            }

            if (level > 0) // high interrupt
            {
                if (_isCurrentlyReading) return; /* ignore GPIO glitching and duplicate interrupt cycles */
                _isCurrentlyReading = true;
                // lock the master bus lock until the read is complete
                Monitor.Enter(_masterBusLockObject);
                // write the data
                lock (_spiLockObject)
                {
                    _chipSelectPin.Write(_chipSelectActiveLevel);
                    _spi.WriteRead(_syncWordRead, 0, _syncWordRead.Length, null, 0, 0, 0);
                    _chipSelectPin.Write(!_chipSelectActiveLevel);
                }

            }
            else // low interrupt
            {
                if (!_isCurrentlyReading) return; /* ignore GPIO glitching and duplicate interrupt cycles */
                RetrieveBytes();
            }
        }

        void RetrieveBytes()
        {
            try
            {
                lock (_incomingDataBufferLockObject)
                {
                    /* if there is not enough room to retrieve the data, wait to request more data. */
                    /* if (INCOMING_BUFFER_SIZE - _incomingDataBufferFirstAvailableIndex < count)
                        return 0; */

                    // WARNING: nested locks can be tricky/dangerous and can lead to deadlock; we are only locking the incoming data buffer here, briefly, so we can reliably append data to it.

                    UInt32 syncWord = BitConverter.ToUInt32(_syncWordIncoming, 0);
                    Int32 bytesToRead = DEFAULT_BYTES_TO_REQUEST;
                    while (bytesToRead > 0)
                    {
                        // read the data
                        lock (_spiLockObject)
                        {
                            _chipSelectPin.Write(_chipSelectActiveLevel);
                            _spi.WriteRead(_emptyBuffer, 0, 0, _incomingDataBuffer, _incomingDataBufferFirstAvailableIndex, bytesToRead, 0);
                            _chipSelectPin.Write(!_chipSelectActiveLevel);
                        }
                        _incomingDataBufferFirstAvailableIndex += bytesToRead;

                        // after reading data, we should always move our _incomingDataBufferEndOfLastCompleteFrame pointer to the end of the last complete frame
                        bool foundSyncWord = false;
                        while(true)
                        {
                            foundSyncWord = ((_incomingDataBufferFirstAvailableIndex - _incomingDataBufferEndOfLastCompleteFrame) >= SYNC_WORD_SIZE) &&
                                ((BitConverter.ToUInt32(_incomingDataBuffer, _incomingDataBufferEndOfLastCompleteFrame) & _syncWordIncomingMask) == syncWord);

                            if (foundSyncWord)
                            {
                                if (_incomingDataBufferFirstAvailableIndex >= _incomingDataBufferEndOfLastCompleteFrame + SYNC_WORD_SIZE + OPCODE_FIELD_SIZE + LENGTH_FIELD_SIZE)
                                {
                                    // retrieve length; if we have enough data for this frame in our buffer, move the _incomingDataBufferEndOfLastCompleteFrame pointer to next SOF
                                    UInt16 length = CC3100BitConverter.ToUInt16(_incomingDataBuffer, _incomingDataBufferEndOfLastCompleteFrame + SYNC_WORD_SIZE + OPCODE_FIELD_SIZE);

                                    if (_incomingDataBufferEndOfLastCompleteFrame + SYNC_WORD_SIZE + OPCODE_FIELD_SIZE + LENGTH_FIELD_SIZE + length <= _incomingDataBufferFirstAvailableIndex)
                                    {
                                        // advance to the next frame
                                        _incomingDataBufferEndOfLastCompleteFrame += SYNC_WORD_SIZE + OPCODE_FIELD_SIZE + LENGTH_FIELD_SIZE + length;

                                        /* special case: if we are advancing to the end of the frame--but there is empty data from our current too-large read request--then remove that data */
                                        if (_incomingDataBufferFirstAvailableIndex - _incomingDataBufferEndOfLastCompleteFrame < bytesToRead)
                                        {
                                            _incomingDataBufferFirstAvailableIndex = _incomingDataBufferEndOfLastCompleteFrame;
                                        }
                                        continue;
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }
                            }
                            else // if (!foundSyncWord)
                            {
                                break;
                            }
                        }

                        bytesToRead = 0;
                        // if we found a sync word, determine how many more bytes of data we need to request.
                        if (foundSyncWord)
                        {
                            if (_incomingDataBufferFirstAvailableIndex >= _incomingDataBufferEndOfLastCompleteFrame + SYNC_WORD_SIZE + OPCODE_FIELD_SIZE + LENGTH_FIELD_SIZE)
                            {
                                // we have enough data to know our frame's length; see if we need to read more data.
                                UInt16 length = CC3100BitConverter.ToUInt16(_incomingDataBuffer, _incomingDataBufferEndOfLastCompleteFrame + SYNC_WORD_SIZE + OPCODE_FIELD_SIZE);
                                bytesToRead = SYNC_WORD_SIZE + OPCODE_FIELD_SIZE + LENGTH_FIELD_SIZE + length
                                    - _incomingDataBufferFirstAvailableIndex - _incomingDataBufferEndOfLastCompleteFrame;

                                // in the rare case that our length is corrupted (measured here as "larger than our entire buffer"), clear this frame.
                                if (bytesToRead + (_incomingDataBufferFirstAvailableIndex) > INCOMING_BUFFER_SIZE - _incomingDataBufferEndOfLastCompleteFrame)
                                {
                                    foundSyncWord = false;
                                    bytesToRead = 0;
                                    _incomingDataBufferFirstAvailableIndex = _incomingDataBufferEndOfLastCompleteFrame;
                                }
                            }
                            else
                            {
                                // we do not have enough bytes to know our frame's length; we need to read more data.
                                bytesToRead = DEFAULT_BYTES_TO_REQUEST;
                            }
                        }

                        // in any circumstance, we cannot read more bytes than we have room to read.
                        bytesToRead = System.Math.Min(bytesToRead, INCOMING_BUFFER_SIZE - _incomingDataBufferFirstAvailableIndex);

                        // bytesToRead should always be a multiple of 32-bits.
                        bytesToRead += ((bytesToRead % 4) > 0) ? 4 - (bytesToRead % 4) : 0;
                    }

                    // after reading data, we should raise the DataReceived event.
                    _notifyDataReceivedWaitHandle.Set();
                }
            }
            finally
            {
                // read is complete; unlock the master bus lock
                _isCurrentlyReading = false;
                Monitor.Exit(_masterBusLockObject);
            }
        }

       // this function is responsible for raising the DataAvailable event
        void NotifyDataReceived()
        {
            while (!_isDisposed)
            {
                _notifyDataReceivedWaitHandle.WaitOne();

                if (_incomingDataBufferEndOfLastCompleteFrame > 0)
                {
                    if (DataReceived != null)
                        DataReceived(this, EventArgs.Empty);
                }
            }
        }
    }
}
