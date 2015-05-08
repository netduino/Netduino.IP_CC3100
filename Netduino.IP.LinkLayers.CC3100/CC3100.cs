// Netduino.IP Stack
// Copyright (c) 2015 Secret Labs LLC. All rights reserved.
// Licensed under the Apache 2.0 License

using Microsoft.SPOT;
using Microsoft.SPOT.Hardware;
using System;
using System.IO.Ports;
using System.Threading;

namespace Netduino.IP.LinkLayers
{
    public class CC3100 : Netduino.IP.ILinkLayer
    {
        ICC3100Transport _cc3100Transport = null;
 
        public class CC3100SocketInfo
        {
            public UInt32 RemoteIPAddress;
            public UInt16 RemoteIPPort;
            public Int32 SocketHandle;
            public SocketAddressFamily SocketAddressFamily;
            public SocketSocketType SocketSocketType;
            // the following are used to buffer RX data
            public byte[] SocketReceiveBuffer = null;
            public Int32 SocketReceiveBufferFirstAvailableIndex = 0;
            public object SocketReceiveBufferLockObject = new object();

            public CC3100SocketInfo(Int32 socketHandle, SocketAddressFamily addressFamily, SocketSocketType socketType)
            {
                this.SocketHandle = socketHandle;
                this.SocketAddressFamily = addressFamily;
                this.SocketSocketType = socketType;
                this.RemoteIPAddress = 0; // default
                this.RemoteIPPort = 0; // default
            }
        }
        System.Collections.ArrayList _cc3100SocketList = new System.Collections.ArrayList();
        object _cc3100SocketListLockObject = new object();

        enum CC3100TransportTypes
        {
            Spi,
            Uart
        }
        CC3100TransportTypes _cc3100TransportType = CC3100TransportTypes.Spi;

        // SPI transport settings
        SPI.SPI_module _spiBusID = SPI.SPI_module.SPI1;
        Cpu.Pin _spiCsPinID = Cpu.Pin.GPIO_NONE;
        Cpu.Pin _spiIntPinID = Cpu.Pin.GPIO_NONE;

        // UART transport settings
        string _uartPortName = String.Empty;

        OutputPort _resetPin = null;
        Cpu.Pin _resetPinID = Cpu.Pin.GPIO_NONE;
        OutputPort _hibernatePin = null;
        Cpu.Pin _hibernatePinId = Cpu.Pin.GPIO_NONE;

        const int MAC_ADDRESS_SIZE = 6;
        byte[] _macAddress = null;

        bool _isInitialized = false;
        bool _isSimplelinkStarted = false;

        /* TODO: the CC3100 appears to cycle its syncword's last two bits between data blocks; if so, consider using that as a sequence number if that is a benefit to us,
         *       for reliability or performance reasons, etc. */
        const int SYNC_WORD_SIZE_TX = 4;
//        const int SYNC_WORD_SIZE_TX = 8;
        //byte[] _syncWordWrite = new byte[SYNC_WORD_SIZE_TX] { 0x43, 0x21, 0x34, 0x12 }; /* use network byte order */
		byte[] _syncWordWrite = new byte[SYNC_WORD_SIZE_TX] { 0x21, 0x43, 0x34, 0x12 }; /* use the same byte order for 'default UART' and SPI */
//        byte[] _syncWordWrite = new byte[SYNC_WORD_SIZE_TX] { 0xFF, 0xEE, 0xDD, 0xBB, 0x21, 0x43, 0x34, 0x12 }; /* use the same byte order for 'default UART' and SPI */
        //byte[] _syncWordRead = new byte[SYNC_WORD_SIZE_TX] { 0x87, 0x65, 0x78, 0x56 }; /* use network byte order */
		byte[] _syncWordRead = new byte[SYNC_WORD_SIZE_TX] { 0x65, 0x87, 0x78, 0x56 };	/* use the same byte order for 'default UART' and SPI */
//        byte[] _syncWordRead = new byte[SYNC_WORD_SIZE_TX] { 0xFF, 0xEE, 0xDD, 0xBB, 0x65, 0x87, 0x78, 0x56 };	/* use the same byte order for 'default UART' and SPI */
        //byte[] _syncWordIncoming = new byte[SYNC_WORD_SIZE_RX] { 0xAB, 0xCD, 0xDC, 0xBC }; /* use network byte order */
        //byte[] _syncWordIncoming = new byte[SYNC_WORD_SIZE_RX] { 0xBC, 0xDC, 0xCD, 0xAB };	 /* use the same byte order for 'default UART' and SPI */
        //const UInt32 _syncWordIncomingMask = 0xFCFFFFFF; /* use network byte order */ // reversed byte order (due to 32-bit network byte order to LE conversion)
		//const UInt32 _syncWordIncomingMask = 0xFFFFFFFC; /* use the same byte order for 'default UART' and SPI */ // reversed byte order (due to 32-bit network byte order to LE conversion)
		const byte _syncWordIncomingMaskFirstByte = (byte)(_syncWordIncomingMask & 0xFF); // lowest 8 bits of word mask
		const int OPCODE_FIELD_SIZE = 2;
		const int LENGTH_FIELD_SIZE = 2;
        const int FLOW_CONTROL_FIELDS_SIZE = 4;
        //
        /* NOTE: from our testing, the incoming sync word can vary in the last THREE bits, rather than the last TWO bits as specified by TI's documentation.  This was
         *       noted in testing the UART interface where the syncWord ends with 0xBA rather than 0xBC. The following is a fix for this issue. */
        // NOTE: it may be that the sync word ends in b10xx for UART and b11xx for SPI; we should ask TI for clarification
        const int SYNC_WORD_SIZE_RX = 4;
        byte[] _syncWordIncoming = new byte[SYNC_WORD_SIZE_RX] { 0xB8, 0xDC, 0xCD, 0xAB };	 /* use the same byte order for 'default UART' and SPI */
        const UInt32 _syncWordIncomingMask = 0xFFFFFFF8; /* use the same byte order for 'default UART' and SPI */ // reversed byte order (due to 32-bit network byte order to LE conversion)

		/* TODO: identify the largest data block size allowed by CC3100--and modify this buffer size accordingly (to be at least as big as two full blocks--and 3x */
		const int CC3100_DATA_BLOCK_MAX_SIZE = 1500 + SYNC_WORD_SIZE_TX + 4; // we are guessing here on the maximum size of a CC3100 data block; 4 bytes refers to OPCODE and LENGTH size
		const int INCOMING_BUFFER_SIZE = CC3100_DATA_BLOCK_MAX_SIZE * 2; // total size of incoming data buffer
		byte[] _incomingDataBuffer = new byte[INCOMING_BUFFER_SIZE]; // incoming data buffer
		int _incomingDataBufferFirstAvailableIndex = 0; // number of bytes used in incoming data buffer
        object _incomingDataBufferLockObject = null;

        /* we can have up to 32 concurrent requests queued on the CC3100; we can set this to 1 for a simple "single action at a time" action window */
        const int MAX_CONCURRENT_ACTIONS = 11; /* one for each of 10 sockets...plus 1 for our actions */
        const int MAX_PENDING_RESPONSES = MAX_CONCURRENT_ACTIONS * 2; /* one for a response to each concurrent action, doubled in case each action has an async response */

        object _sl_NetAppDnsGetHostByName_SynchronizationLockObject = new object();
        object _sl_Select_SynchronizationLockObject = new object();

        AutoResetEvent _callFunctionSynchronizationEvent;

        class PendingResponse
        {
            public CC3100Opcode OpCode;
            public AutoResetEvent WaitHandle = new AutoResetEvent(false);
            public object ResponseData = null;
            public Int32 SocketHandle = -1; /* this is used on async responses which are socket-specific; -1 means "do not match socketHandle on response" */

            internal void SetResponse(object responseData)
            {
                this.ResponseData = responseData;
                this.WaitHandle.Set();
            }
        }
        PendingResponse[] _pendingResponses = null;
        int _pendingResponsesCount = 0;
        object _pendingResponsesLockObject = null;

		event PacketReceivedEventHandler OnPacketReceived;
        event LinkStateChangedEventHandler OnLinkStateChanged;
        bool _lastLinkState = false;

        /* TODO: move this event to the sockets/IP layer class */
        public delegate void IPv4AddressChangedEventHandler(object sender, UInt32 ipAddress);
        event IPv4AddressChangedEventHandler OnIPv4AddressChanged;

        /* TODO: move the IP address variable and logic to a higher level of the IP stack */
        public UInt32 _cachedIpv4Address = 0;
        public UInt32 _cachedGatewayAddress = 0;
        public UInt32 _cachedSubnetMask = 0;
        public UInt32 _cachedDnsAddress = 0;
        public bool __cachedIpv4ConfigurationIsDirty = true;
        public object _cachedIpv4ConfigurationLockObject = new object();

        bool _isFirstCommandSent = false;

        // CC3100 opcodes
        enum CC3100Opcode : ushort
        {
            None                       = 0x0000,
            Device_InitComplete = 0x0008,
            //Device_Abort_Command       = 0x000C,
            Device_Stop_Command = 0x8473,
            Device_Stop_Response = 0x0473,
            Device_Stop_Async_Response = 0x0073,
            Device_DeviceAsyncDummy_Event = 0x0063,
            //
            Device_DeviceGet_Command = 0x8466,
            Device_DeviceGet_Response = 0x0466,
            Device_DeviceSet_Command = 0x84B7,
            Device_DeviceSet_Response = 0x04B7,
            //
            Device_SetUartMode_Command = 0x846B,
            Device_SetUartMode_Response = 0x046B,
            //
            Device_NetCfg_Get_Command = 0x8433,
            Device_NetCfg_Get_Response = 0x0433,
            Device_NetCfg_Set_Command  = 0x8432,
            Device_NetCfg_Set_Response = 0x0432,
            //
            NetApp_DnsGetHostByName_Command = 0x9C20,
            NetApp_DnsGetHostByName_Response= 0x1C20,
            NetApp_DnsGetHostByName_AsyncResponse = 0x1820,
            NetApp_IPv4IPAcquired_Event = 0x1825,
            NetApp_Stop_Command      = 0x9C61,
            NetApp_Stop_Response     = 0x1C61,
            //
            NvMem_FileClose_Command  = 0xA43D,
            NvMem_FileClose_Response = 0x243D,
            NvMem_FileDel_Command    = 0xA443,
            NvMem_FileDel_Response   = 0x2443,
            NvMem_FileOpen_Command   = 0xA43C,
            NvMem_FileOpen_Response  = 0x243C,
            NvMem_FileRead_Command   = 0xA440,
            NvMem_FileRead_Response  = 0x2440,
            NvMem_FileWrite_Command  = 0xA441,
            NvMem_FileWrite_Response = 0x2441,
            //
            Socket_Accept_Command    = 0x9403,
            Socket_Accept_Response   = 0x1403,
            Socket_Accept_IPv4_AsyncResponse = 0x1003,
            Socket_Async_Event       = 0x100F,
            Socket_Bind_IPv4_Command = 0x9404,
            //Socket_Bind_IPv6_Command   = 0x9604,
            Socket_Bind_Response       = 0x1404,
            Socket_Connect_IPv4_Command= 0x9406,
            //Socket_Connect_IPv6_Command= 0x9606,
            Socket_Connect_Response    = 0x1406,
            Socket_Connect_AsyncResponse=0x1006,
            Socket_Close_Command       = 0x9402,
            Socket_Close_Response      = 0x1402,
            Socket_GetSockOpt_Command  = 0x9409,
            Socket_GetSockOpt_Response = 0x1409,
            Socket_Listen_Command      = 0x9405,
            Socket_Listen_Response     = 0x1405,
            Socket_Recv_Command        = 0x940A,
            Socket_Recv_AsyncResponse  = 0x100A,
            Socket_RecvFrom_Command    = 0x940B,
            Socket_RecvFrom_IPv4_AsyncResponse=0x100B,
            //Socket_RecvFrom_IPv6_AsyncResponse=0x120B,
            Socket_Select_Command      = 0x9407,
            Socket_Select_Response     = 0x1407,
            Socket_Select_AsyncResponse= 0x1007,
            Socket_Send_Command        = 0x940C,
            Socket_SendTo_IPv4_Command = 0x940D,
            //Socket_SendTo_IPv6_Command = 0x960D,
            Socket_SetSockOpt_Command  = 0x9408,
            Socket_SetSockOpt_Response = 0x1408,
            Socket_Socket_Command      = 0x9401,
            Socket_Socket_Response     = 0x1401,
            Socket_TxFailed_EVent      = 0x100E,
            //
            Wlan_Connect_Event         = 0x0880,
            Wlan_Disconnect_Event      = 0x0881,
            Wlan_Policy_Get_Command    = 0x8C87,
            Wlan_Policy_Get_Response   = 0x0C87,
            Wlan_Policy_Set_Command    = 0x8C86,
            Wlan_Policy_Set_Response   = 0x0C86,
            Wlan_Profile_Add_Command   = 0x8C83,
            Wlan_Profile_Add_Response  = 0x0C83,
            Wlan_Profile_Del_Command   = 0x8C85,
            Wlan_Profile_Del_Response  = 0x0C85,
            Wlan_Profile_Get_Command   = 0x8C84,
            Wlan_Profile_Get_Response  = 0x0C84,
        }

        enum CC3100Role : short 
        {
            ROLE_UNKNOWN_ERR = -1,
            ROLE_STA = 0,
            ROLE_STA_ERR = -1,
            ROLE_AP = 2,
            ROLE_AP_ERR = -2,
            ROLE_P2P = 3,
            ROLE_P2P_ERR = -3,
            //ROLE_UNKNOWN_ERR = -1
        }

        public enum SL_NetCfg_ConfigID : byte
        {
            SL_MAC_ADDRESS_SET = 1,
            SL_MAC_ADDRESS_GET = 2,
            SL_IPV4_STA_P2P_CL_GET_INFO = 3,
            SL_IPV4_STA_P2P_CL_DHCP_ENABLE = 4,
            SL_IPV4_STA_P2P_CL_STATIC_ENABLE = 5,
            //SL_IPV4_AP_P2P_GO_GET_INFO = 6,
            //SL_IPV4_AP_P2P_GO_STATIC_ENABLE = 7,
            //SL_SET_HOST_RX_AGGR = 8,
        }

        public enum sl_NetAppOptions : uint 
        {
            HttpServer = 1,
            DhcpServer = 2,
            MDNS = 4,
//            DnsServer = 8,
//            DeviceConfig = 16,
        }

        public enum _sl_FsMode : byte
        {
            OpenRead = 0,
            OpenWrite = 1,
            OpenCreate = 2,
            OpenWriteCreateIfNotExist = 3,
        }

        [Flags]
        public enum _sl_FsAccessFlags : byte
        {
            Commit = 0x01,
            Secure = 0x02,
            NoSignatureTest = 0x04,
            Static = 0x08,
            Vendor = 0x10,
            Write = 0x20,
            Read = 0x40,
        }

        public enum WlanSecurityType : byte
        {
            Open = 0,
            Wep = 1,
            //Wpa = 2, /* deprecated */
            Wpa2 = 2,
            //WpsPbc = 3,
            //WpsPin = 4,
            //WpaEnt = 5,
            //P2pPbc = 6,
            //P2pPinKeypad = 7,
            //P2pPinDisplay = 8,
            //P2pPinAuto = 9, /* not yet supported */
        }

        public enum SocketAddressFamily : byte
        {
            IPv4 = 2,
            //IPv6 = 3,
            //IPv6Eui48 = 9,
            //RF = 6,
            //Packet = 17,
        }

        public enum SocketSocketType : byte
        {
            Stream = 1,
            Dgram = 2,
            //Raw = 3,
            //TcpRawProtocol = 6,
            //UdpRawProtocol = 17,
            //RawProtocol = 255,
        }

        public enum SocketProtocolType : byte
        {
            //IP = 0,
            //ICMP = 1,
            //IGMP = 2,
            //GGP = 3,
            TCP = 6,
            //PUP = 12,
            UDP = 17,
            //IDP = 22,
            //ND = 77,
            Secure = 100,
            //Raw = 255,
        }

        OutputPort _ledLink = null;
        OutputPort _ledState = null;

        /* TODO: should all of our link layers implement IDisposable, so that we can free up the interrupt, reset and other pins when we're done with the NIC? */
        public CC3100(SPI.SPI_module spiBusID, Cpu.Pin csPinID, Cpu.Pin intPinID, Cpu.Pin resetPinID, Cpu.Pin hibernatePinID)
        {
            // initialize variables
            InitializeVariables();

            // save the SPI and /INT configuration values for Start()
            _spiBusID = spiBusID;
            _spiCsPinID = csPinID;
            _spiIntPinID = intPinID;
            // set our transport type to SPI.
            _cc3100TransportType = CC3100TransportTypes.Spi;

            // save our reset pin ID (which we will use to control the reset pin a bit later on)
            _resetPinID = resetPinID;

            // save our hibernate pin ID (which we will use to control the hibernate pin a bit later on)
            _hibernatePinId = hibernatePinID;

            // we are not initialized; we will initialize when we are started. 
            _isInitialized = false;
        }

        public CC3100(string portName, Cpu.Pin intPinID, Cpu.Pin resetPinID, Cpu.Pin hibernatePinID)
        {
            // initialize variables
            InitializeVariables();

            // save the SerialPort configuration values for Start()
            _uartPortName = portName;
            _spiIntPinID = intPinID;
            // set our transport type to UART.
            _cc3100TransportType = CC3100TransportTypes.Uart;

            // save our reset pin ID (which we will use to control the reset pin a bit later on)
            _resetPinID = resetPinID;

            // save our hibernate pin ID (which we will use to control the hibernate pin a bit later on)
            _hibernatePinId = hibernatePinID;

            // we are not initialized; we will initialize when we are started. 
            _isInitialized = false;
        }

        void InitializeVariables()
        {
            _macAddress = new byte[MAC_ADDRESS_SIZE] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

            _incomingDataBufferLockObject = new object();

            _pendingResponses = new PendingResponse[MAX_PENDING_RESPONSES];
            _pendingResponsesLockObject = new object();

        }

		bool ILinkLayer.GetLinkState()
        {
            return _lastLinkState;
        }

        byte[] ILinkLayer.GetMacAddress()
        {
            return _macAddress;
        }

        void ILinkLayer.SendFrame(int numBuffers, byte[][] buffer, int[] index, int[] count, long timeoutInMachineTicks)
        {
            throw new NotImplementedException();
        }

        void ILinkLayer.SetMacAddress(byte[] macAddress)
        {
            if (macAddress == null || macAddress.Length != MAC_ADDRESS_SIZE)
                throw new ArgumentException();

            // write over our MAC address with the new MAC address
            Array.Copy(macAddress, _macAddress, MAC_ADDRESS_SIZE);

            if (_isInitialized)
            {
                throw new NotSupportedException();
                /* TODO: if we're already started, re-initialize our MAC address and any other settings in the network chip */
            }
        }

        void ILinkLayer.Start()
        {
            Initialize();
        }

        void ILinkLayer.Stop()
        {
            if (_isSimplelinkStarted)
                sl_Stop(10);

            // we are now un-initializing
            _isInitialized = false;

            // dispose of our SPI transport
            _cc3100Transport.Dispose();

            // power down our network chip
            EnterPowerDownMode();
        }

        public void SetLinkLedPinID(Cpu.Pin pinID)
        {
            if (_isInitialized)
            {
                throw new NotSupportedException();
                /* TODO: if we're already started, configure our link LED */
            }

            if (_ledLink != null)
                _ledLink.Dispose();

            _ledLink = new OutputPort(pinID, _lastLinkState);
        }

        public void SetStateLedPinID(Cpu.Pin pinID)
        {
            if (_isInitialized)
            {
                throw new NotSupportedException();
                /* TODO: if we're already started, configure out link LED */
            }

            if (_ledState != null)
                _ledState.Dispose();

            _ledState = new OutputPort(pinID, !_lastLinkState);
        }

        // this function restarts and initializes our network chip
        void Initialize()
        {
            _isInitialized = false;

            // dispose of any pre-existing transport
            if (_cc3100Transport != null)
            {
                _cc3100Transport.Dispose();
                _cc3100Transport = null;
            }

            // create our CC3100 transport
            switch (_cc3100TransportType)
            {
                case CC3100TransportTypes.Spi:
                    _cc3100Transport = new CC3100SpiTransport(_spiBusID, _spiCsPinID, _spiIntPinID);
                    break;
                case CC3100TransportTypes.Uart:
                    _cc3100Transport = new CC3100UartTransport(_uartPortName, 115200, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One, _spiIntPinID);
                    break;
            }

            /* TODO: consider implementing an in-line asynchronous callback which watches for the role and after ~5000ms times out with an error */
            const Int32 startMillisecondsTimeout = 5000;
            CC3100Role cc3100Role = sl_Start(startMillisecondsTimeout);
            if (cc3100Role == CC3100Role.ROLE_UNKNOWN_ERR)
            {
                // could not connect.  /* TODO: should we have an exception for this? */
                return;
            }
            else if (cc3100Role != CC3100Role.ROLE_STA)
            {
                // if the radio is not currently in STATION mode, then switch it to STATION mode, disconnect and re-initialize.

                if (cc3100Role == CC3100Role.ROLE_AP)
                {
                    /* if the device is in AP mode, we need to wait for it to acquire an IP before switching roles. */
                    ////// *** TI CODE ***
                    /* TODO: while(!IS_IP_ACQUIRED(g_Status)); */
                }

                // switch to STATION role and restart
                ////// *** TI CODE ***
                //////retVal = sl_WlanSetMode(ROLE_STA);
                //////ASSERT_ON_ERROR(retVal);

                // reset CC3100 for role change to take effect
                /* TODO: consider implementing an in-line asynchronous callback which watches for the role and after ~2000ms times out with an error */
                sl_Stop(100);
                cc3100Role = sl_Start(startMillisecondsTimeout);
                if (cc3100Role != CC3100Role.ROLE_STA)
                {
                    /* CC3100 could not be switched to station mode; determine a better exception for this */
                    throw new CC3100SimpleLinkException((Int32)cc3100Role);
                }
            }

            //sl_UartSetMode(115200, true);
            //sl_WlanProfileDel(0xFF);
            //sl_WlanPolicySet(0x10 /* SL_POLICY_CONNECTION */, 0x00, null);

            //// get version
            //int versionLoop = 0;
            //while (true)
            //{
            //    byte[] versionBuffer = new byte[44];
            //    Int32 verSuccess = sl_DevGet(1 /*SL_DEVICE_GENERAL_CONFIGURATION*/, 12 /*SL_DEVICE_GENERAL_VERSION*/, versionBuffer);
            //    if (verSuccess < 0)
            //    {
            //        Debug.Print("Could not retrieve version information.");
            //    }
            //    else
            //    {
            //        Debug.Print("version loop #: " + versionLoop);
            //    }
            //    versionLoop++;
            //}

            // disable our integrated web server, dhcp server (if enabled) and mdns feature
            sl_NetAppStop(sl_NetAppOptions.HttpServer | sl_NetAppOptions.MDNS | sl_NetAppOptions.DhcpServer);

            /* TODO: set our MAC address; if this has changed, we will need to reboot the CC3100 */
            byte[] storedMacAddress = new byte[MAC_ADDRESS_SIZE];
            //SL_NetCfgGet(SL_NetCfg_ConfigID.SL_MAC_ADDRESS_GET, 0, (byte)MAC_ADDRESS_SIZE, storedMacAddress);
            UInt16 configOption = 0;
            sl_NetCfgGet(SL_NetCfg_ConfigID.SL_MAC_ADDRESS_GET, ref configOption, storedMacAddress);
            bool macAddressIsEqual = true;
            for (int i = 0; i < MAC_ADDRESS_SIZE; i++)
            {
                if (_macAddress[i] != storedMacAddress[i])
                    macAddressIsEqual = false;
            }
            if (!macAddressIsEqual)
            {
                // store new MAC address
                sl_NetCfgSet(SL_NetCfg_ConfigID.SL_MAC_ADDRESS_SET, 0, _macAddress);

                // we must restart the CC3100 for the new MAC address to take effect
                /* TODO: consider implementing an in-line asynchronous callback which watches for the role and after ~2000ms times out with an error */
                sl_Stop(100);
                sl_Start(startMillisecondsTimeout);
            }

            /* TODO: any other necessary configuration goes here */

            /* TODO: disable CC3100's internal HTTP server */

            // we are now initialized
            _isInitialized = true;
        }

        #region SimpleLink Error Codes
        public enum CC3100ErrorCode : int 
        {
            //SL_POOL_IS_EMPTY = -2000,
            SL_ESMALLBUF     = -2001,
        }
        #endregion /* SimpleLink Error Codes */

        #region SimpleLink Device API
        void sl_DeviceDisable()
        {
            try { _cc3100Transport.DataReceived -= _cc3100Transport_DataReceived; } catch { }

            // hardware-reset our network chip
            if (_resetPinID != Cpu.Pin.GPIO_NONE)
            {
                if (_resetPin == null)
                {
                    _resetPin = new OutputPort(_resetPinID, false);
                }
                else
                {
                    _resetPin.Write(false);
                }
            }
            /* NOTE: hibernate pin actually powers down our chip; the reset pin may technically be redundant here */
            if (_hibernatePin == null)
            {
                _hibernatePin = new OutputPort(_hibernatePinId, false);
            }
            else
            {
                _hibernatePin.Write(false);
            }
        }

        void sl_EnableDevice()
        {
            // sleep for at least 10ms
            //System.Threading.Thread.Sleep(10); // 10000us (10ms) is the minimum allowable reset time
            System.Threading.Thread.Sleep(250); // 10000us (10ms) is the minimum allowable reset time

            _cc3100Transport.DataReceived += _cc3100Transport_DataReceived;

            // take our hardware chip out of reset
            _hibernatePin.Write(true);
            if (_resetPin != null)
            {
                _resetPin.Write(true);
            }
        }

        /* NOTE: if millisecondsTimeout is set to Timeout.Infinite, this function will block until a connection is established. */
        /* RETURNS: current role */
        CC3100Role sl_Start(Int32 millisecondsTimeout)
        {
            if (_isSimplelinkStarted)
            {
                return CC3100Role.ROLE_UNKNOWN_ERR;
            }

            // disable our device 
            sl_DeviceDisable();

            // initialize all of our non-transport locks, queues, variables, etc.
            _callFunctionSynchronizationEvent = new AutoResetEvent(true);
            //_txSynchronizationEvent = new AutoResetEvent(true or false?);
            //_txLockEvent = new AutoResetEvent(true or false?);
            ClearPendingResponses();

            // queue up an InitComplete pending response
            PendingResponse initCompleteResponse = AddPendingResponse(CC3100Opcode.Device_InitComplete);

            // enable our device
            sl_EnableDevice();

            // attempt to connect to network chip for millisecondsTimeout
            // NOTE: in our initial tests, boot time was consistently ~83ms.  But under debugger controler, when no event has been fired before, up to ~1000ms of additional time may be required.
            //Int64 startTicks = Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks;
            if (!initCompleteResponse.WaitHandle.WaitOne(millisecondsTimeout, false))
            {
                ClearPendingResponses();
                sl_DeviceDisable();
                return CC3100Role.ROLE_UNKNOWN_ERR;
            }
            //Debug.Print("startup time: " + ((Utility.GetMachineTime().Ticks - startTicks) / System.TimeSpan.TicksPerMillisecond).ToString());
            CC3100Role cc3100Role = ConvertInitCompleteStatusToRole((UInt32)initCompleteResponse.ResponseData);

            _isSimplelinkStarted = true;

            return cc3100Role;
        }

        /* NOTE: millisecondsTimeout of -1 means "wait forever"; it is equal to System.Threading.Timeout.Infinite 
                 millisecondsTimeout of  0 means "hibernate immediately". */
        Int32 sl_Stop(Int32 millisecondsTimeout)
        {
            if (millisecondsTimeout < -1)
                throw new ArgumentOutOfRangeException();

            // in case are aborting an sl_Start, trigger its "InitComplete" event here.
            PendingResponse initCompleteResponse = RemovePendingResponse(CC3100Opcode.Device_InitComplete);
            if (initCompleteResponse != null)
            {
                initCompleteResponse.SetResponse((UInt32)0 /* ROLE_UNKNOWN_ERR */); 
            }

            // send stop function
            Int32 retVal = sl_StopDevice(millisecondsTimeout);

            _isSimplelinkStarted = false;

            /* shut down all of our non-transport locks, queues, variables, etc. */
            ClearPendingResponses();
            if (_callFunctionSynchronizationEvent != null)
                _callFunctionSynchronizationEvent.Set();
            //if (_txSynchronizationEvent != null)
                //_txSynchronizationEvent.Set();
            //if (_txLockEvent != null)
                //_txLockEvent.Set();


            // disable our device 
            sl_DeviceDisable();

            // clear all connections in our connection list
            lock (_cc3100SocketListLockObject)
            {
                _cc3100SocketList.Clear();
            }

            return retVal;
        }

        CC3100Role ConvertInitCompleteStatusToRole(UInt32 status)
        {
            switch (status & 0x07)
            {
                case 1:
                    return CC3100Role.ROLE_STA;
                case 2:
                    return CC3100Role.ROLE_STA_ERR;
                case 3:
                    return CC3100Role.ROLE_AP;
                case 4:
                    return CC3100Role.ROLE_AP_ERR;
                case 5:
                    return CC3100Role.ROLE_P2P;
                case 6:
                    return CC3100Role.ROLE_P2P_ERR;
                case 0:
                case 7:
                default:
                    return CC3100Role.ROLE_UNKNOWN_ERR;
            }
        }

        public Int32 sl_StopDevice(Int32 millisecondsTimeout)
        {
            if ((millisecondsTimeout > UInt16.MaxValue) || (millisecondsTimeout < -1))
                throw new ArgumentOutOfRangeException();

            // calculate our timeout moment (in ticks)
            Int64 timeoutInMachineTicks = (millisecondsTimeout == -1 ? Int64.MaxValue : Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks + (TimeSpan.TicksPerMillisecond * millisecondsTimeout));
            Int32 currentMillisecondsTimeout;

            Int32 index = 0;
            Int32 retVal = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            // timeout
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)millisecondsTimeout), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);

            // payload (32-bit aligned)

            // register callback function
            CC3100Opcode asyncResponseOpCode = CC3100Opcode.Device_Stop_Async_Response;
            PendingResponse asyncPendingResponse = AddPendingResponse(asyncResponseOpCode);

            // call function
            currentMillisecondsTimeout = (Int32)((timeoutInMachineTicks != Int64.MaxValue) ? (System.Math.Max(0, timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks)) / System.TimeSpan.TicksPerMillisecond : Timeout.Infinite);
            byte[] responseBytes = CallFunction(CC3100Opcode.Device_Stop_Command, CC3100Opcode.Device_Stop_Response, descriptors, null, currentMillisecondsTimeout);

            if (responseBytes != null)
            {
                // response contains immediate status; async response will contain final status
                index = 0;
                retVal = CC3100BitConverter.ToInt16(responseBytes, index);
            }
            else
            {
                retVal = -1;
            }

            if (retVal == 0) // success
            {
                // now wait for async response to confirm stop
                currentMillisecondsTimeout = (Int32)((timeoutInMachineTicks != Int64.MaxValue) ? (System.Math.Max(0, timeoutInMachineTicks - Microsoft.SPOT.Hardware.Utility.GetMachineTime().Ticks)) / System.TimeSpan.TicksPerMillisecond : Timeout.Infinite);
                asyncPendingResponse.WaitHandle.WaitOne(currentMillisecondsTimeout, false);
                byte[] asyncResponseBytes = (byte[])asyncPendingResponse.ResponseData;

                index = 0;
                retVal = CC3100BitConverter.ToInt16(asyncResponseBytes, index);
            }
            else
            {
                // async response will not be called
                RemovePendingResponse(asyncResponseOpCode);
            }

            return retVal;
        }

        public Int32 sl_DevGet(byte deviceGetId, byte option, byte[] values)
        {
            Int32 index = 0;
            Int32 status = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[8];
            index = 0;
            // skip the 16-bit status
                Array.Copy(CC3100BitConverter.GetBytes((UInt16)0xCCCC), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)deviceGetId), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)option), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);
            // skip the 16-bit configLen
                Array.Copy(CC3100BitConverter.GetBytes((UInt16)0xCCCC), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);

            // payload (32-bit aligned)

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Device_DeviceGet_Command, CC3100Opcode.Device_DeviceGet_Response, descriptors, null, Timeout.Infinite);

            if (responseBytes == null)
            {
                status = -1; /* TODO: is there an appropriate error response for "timeout"? */
            }
            else
            {
                // response
                index = 0;
                status = CC3100BitConverter.ToInt16(responseBytes, index);
                index += sizeof(Int16);
                // skip the 16-bit deviceGetId
                index += sizeof(UInt16);
                // skip the 16-bit option
                index += sizeof(UInt16);
                UInt16 configLen = CC3100BitConverter.ToUInt16(responseBytes, index);
                index += sizeof(UInt16);
            }

            if (status == 0) // success
            {
                if (responseBytes.Length - index > values.Length)
                    return (Int32)CC3100ErrorCode.SL_ESMALLBUF;

                Array.Copy(responseBytes, index, values, 0, responseBytes.Length - index);
            }
            return status;
        }

        public Int32 sl_DevSet(byte deviceSetId, byte option, byte[] values)
        {
            Int32 index = 0;
            Int32 status = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[8];
            index = 0;
            // skip the 16-bit status
            index += sizeof(UInt16);
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)deviceSetId), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)option), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)values.Length), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);

            // payload (32-bit aligned)
            byte[] payload = new byte[RoundUpSizeToNearest32BitBoundary(values.Length)];
            index = 0;
            Array.Copy(values, 0, payload, index, values.Length);

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Device_DeviceSet_Command, CC3100Opcode.Device_DeviceSet_Response, descriptors, payload, Timeout.Infinite);

            if (responseBytes == null)
            {
                status = -1; /* TODO: is there an appropriate error response for "timeout"? */
            }
            else
            {
                // response
                index = 0;
                status = CC3100BitConverter.ToInt16(responseBytes, index);
            }
            return status;
        }

        #endregion /* SimpleLink Device API */

        /* TDOO: this function is conceptual and mostly untested. */
        /* NOTE: baud rate can be up to 711 kbps */
        public Int16 sl_UartSetMode(Int32 baudRate, bool flowControlEnable)
        {
            //throw new NotImplementedException();

            Int32 index = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[8];
            index = 0;
            Array.Copy(CC3100BitConverter.GetBytes((UInt32)baudRate), 0, descriptors, index, sizeof(UInt32));
            index += sizeof(UInt32);
            descriptors[index] = flowControlEnable ? (byte)1 : (byte)0;
            index++;

            // payload (32-bit aligned)

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Device_SetUartMode_Command, CC3100Opcode.Device_SetUartMode_Response, descriptors, null, Timeout.Infinite);

            // no data in the command response

            /* NOTE: only send the "magic word" if the function call suceeded */
            _cc3100Transport.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0, 4);
            _cc3100Transport.Write(new byte[] { 0x55, 0xAA, 0x55, 0xAA }, 0, 4);
            
            /* TODO: wait for magic word response before proceeding; return error or abort or throw exception if magic word is not returned. */

            return 0; /* TODO: return RetVal from the response */

        }

        #region SimpleLink NetConfig API

        public Int32 sl_NetCfgGet(SL_NetCfg_ConfigID configID, ref UInt16 configOption, byte[] values)
        {
            Int32 index = 0;
            Int32 status = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[8];
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)0xcccc), 0, descriptors, index, sizeof(UInt16));       /* status: 0xcccc indicates "uninitialized UInt16" */
            index += sizeof(UInt16);
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)configID), 0, descriptors, index, sizeof(UInt16));     /* configID */
            index += sizeof(UInt16);
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)configOption), 0, descriptors, index, sizeof(UInt16)); /* configOption */
            index += sizeof(UInt16);
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)values.Length), 0, descriptors, index, sizeof(UInt16));    /* configLen */

            // payload (32-bit aligned)

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Device_NetCfg_Get_Command, CC3100Opcode.Device_NetCfg_Get_Response, descriptors, null, Timeout.Infinite);

            if (responseBytes == null)
            {
                status = -1; /* TODO: is there an appropriate error response for "timeout"? */
            }
            else
            {
                /* NOTE: responseBytes is 8 bytes of descriptors followed by our actual 32bit-aligned payload */
                index = 0;
                status = CC3100BitConverter.ToInt16(responseBytes, index);
                index += sizeof(Int16);
                // skip the 16-bit deviceGetId
                index += sizeof(UInt16);
                configOption = CC3100BitConverter.ToUInt16(responseBytes, index);
                index += sizeof(UInt16);
                UInt16 configLen = CC3100BitConverter.ToUInt16(responseBytes, index);
                index += sizeof(UInt16);

                if ((responseBytes.Length - index > values.Length) 
                    && !((configID == SL_NetCfg_ConfigID.SL_MAC_ADDRESS_GET) && responseBytes.Length > 8) /* patch for errata */
                    )
                    return (Int32)CC3100ErrorCode.SL_ESMALLBUF;

                Array.Copy(responseBytes, index, values, 0, System.Math.Min(responseBytes.Length - index, values.Length));
            }

            return status;
        }

        public Int32 sl_NetCfgSet(SL_NetCfg_ConfigID configID, byte configOption, byte[] values)
        {
            Int32 index = 0;
            Int32 status = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[8];
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)0xcccc), 0, descriptors, index, sizeof(UInt16));       /* status: 0xcccc indicates "uninitialized UInt16" */
            index += sizeof(UInt16);
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)configID), 0, descriptors, index, sizeof(UInt16));     /* configID */
            index += sizeof(UInt16);
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)configOption), 0, descriptors, index, sizeof(UInt16)); /* configOption */
            index += sizeof(UInt16);
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)values.Length), 0, descriptors, index, sizeof(UInt16));    /* configLen */

            // payload (32-bit aligned)
            byte[] payload = new byte[RoundUpSizeToNearest32BitBoundary(values.Length)];
            index = 0;
            Array.Copy(values, 0, payload, index, values.Length);

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Device_NetCfg_Set_Command, CC3100Opcode.Device_NetCfg_Set_Response, descriptors, payload, Timeout.Infinite);

            // no data in the command response
            if (responseBytes == null)
            {
                status = -1; /* TODO: is there an appropriate error response for "timeout"? */
            }
            else
            {
                index = 0;
                status = CC3100BitConverter.ToInt16(responseBytes, index);
            }

            return status;
        }

        #endregion /* SimpleLink NetConfig API */

        int RoundUpSizeToNearest32BitBoundary(int length)
        {
            return (int)System.Math.Ceiling(length / 4.0) * 4;
        }

        UInt32 _sl_GetFsMode(_sl_FsMode accessMode, byte sizeGran, byte size, byte flags)
        {
            return (UInt32)(((flags & 0xFF) << 16) |
                (((byte)accessMode & 0x0F) << 12) |
                ((sizeGran & 0x0F) << 8) |
                ((size & 0xFF) << 0));
        }

        public UInt32 _sl_GetCreateFsMode(UInt32 maxSizeInBytes, _sl_FsAccessFlags accessFlags)
        {
            // find the gran size and # of grans to support a file length size of maxSizeInBytes.
            Int32 index;
            UInt32[] granOptions = { 256, 1024, 4096, 16384, 65536 }; /* 5 options for gran size */
            for (index = 0; index < granOptions.Length; index++)
            {
                if (granOptions[index] * 255 >= maxSizeInBytes)
                    break;
            }
            byte granNum = (byte)(maxSizeInBytes / granOptions[index]);
            // if maxSize requires an extra partial gran, add one more now.
            if (maxSizeInBytes % granOptions[index] != 0)
                granNum++;

            return _sl_GetFsMode(_sl_FsMode.OpenWriteCreateIfNotExist, (byte)index, granNum, (byte)accessFlags);
        }

        public UInt32 _sl_FsModeOpenRead()
        {
            return _sl_GetFsMode(_sl_FsMode.OpenRead, 0, 0, 0);
        }

        public UInt32 _sl_FsModeOpenWrite()
        {
            return _sl_GetFsMode(_sl_FsMode.OpenWrite, 0, 0, 0);
        }

        public Int32 sl_FsClose(Int32 fileHandle, string certificateFileName, byte[] signature)
        {
            /* TODO: add support for certificateFileName */

            Int32 index = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[12];
            index = 0;
            Array.Copy(CC3100BitConverter.GetBytes((UInt32)fileHandle), 0, descriptors, index, sizeof(UInt32));
            index += sizeof(UInt32);
            Array.Copy(CC3100BitConverter.GetBytes((UInt32)certificateFileName.Length), 0, descriptors, index, sizeof(UInt32));
            index += sizeof(UInt32);
            if (signature != null)
            {
                Array.Copy(CC3100BitConverter.GetBytes((UInt32)signature.Length), 0, descriptors, index, sizeof(UInt32));
            }
            index += sizeof(UInt32);

            // payload (32-bit aligned)
            byte[] payload = new byte[((signature != null) ? RoundUpSizeToNearest32BitBoundary(signature.Length) : 0)];
            index = 0;
            if (signature != null)
            {
                Array.Copy(signature, 0, payload, index, signature.Length);
            }

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.NvMem_FileClose_Command, CC3100Opcode.NvMem_FileClose_Response, descriptors, payload, Timeout.Infinite);

            if (responseBytes == null)
                return -1;

            // response contains status
            index = 0;
            return (Int32)CC3100BitConverter.ToInt16(responseBytes, index); // statusOrLen
        }

        public Int32 sl_FsDel(string fileName, UInt32 token)
        {
            Int32 index = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            Array.Copy(CC3100BitConverter.GetBytes(token), 0, descriptors, index, sizeof(UInt32));
            index += sizeof(UInt32);

            // payload (32-bit aligned)
            byte[] payload = new byte[RoundUpSizeToNearest32BitBoundary(fileName.Length)];
            index = 0;
            Array.Copy(System.Text.Encoding.UTF8.GetBytes(fileName), 0, payload, index, fileName.Length);

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.NvMem_FileDel_Command, CC3100Opcode.NvMem_FileDel_Response, descriptors, payload, Timeout.Infinite);

            if (responseBytes == null)
                return -1;

            // response contains status
            index = 0;
            return (Int32)CC3100BitConverter.ToInt16(responseBytes, index); // statusOrLen
        }
        
        public Int32 sl_FsOpen(string fileName, UInt32 accessModeAndMaxSize, ref UInt32 token, out Int32 fileHandle)
        {
            Int32 index = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[8];
            index = 0;
            Array.Copy(CC3100BitConverter.GetBytes(accessModeAndMaxSize), 0, descriptors, index, sizeof(UInt32));
            index += sizeof(UInt32);
            Array.Copy(CC3100BitConverter.GetBytes(token), 0, descriptors, index, sizeof(UInt32));
            index += sizeof(UInt32);

            // payload (32-bit aligned)
            byte[] payload = new byte[RoundUpSizeToNearest32BitBoundary(fileName.Length)];
            index = 0;
            Array.Copy(System.Text.Encoding.UTF8.GetBytes(fileName), 0, payload, index, fileName.Length);

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.NvMem_FileOpen_Command, CC3100Opcode.NvMem_FileOpen_Response, descriptors, payload, Timeout.Infinite);

            if (responseBytes == null)
            {
                fileHandle = -1; // ERROR
                return -1; /* TODO: is there a better error to return here? */
            }

            // response contains status
            index = 0;
            fileHandle = (Int32)CC3100BitConverter.ToInt32(responseBytes, index);
            index += sizeof(UInt32);
            token = CC3100BitConverter.ToUInt32(responseBytes, index);
            index += sizeof(UInt32);

            if (fileHandle < 0)
            {
                return fileHandle;
            }

            return 0; // success
        }

        public Int32 sl_FsRead(Int32 fileHandle, UInt32 offset, byte[] buffer)
        {
            int descriptorsIndex = 0;
            int responseIndex = 0;
            int chunkLen = 0;
            int maxChunkLen = 1460;
            int bytesRead = 0;

            Int32 statusOrLen = 0;

            int bufferIndex = 0;
            while (bufferIndex < buffer.Length)
            {
                if (buffer.Length - bufferIndex < maxChunkLen)
                    chunkLen = buffer.Length - bufferIndex;
                else
                    chunkLen = maxChunkLen;

                // descriptors (32-bit aligned)
                byte[] descriptors = new byte[12];
                descriptorsIndex = 0;
                Array.Copy(CC3100BitConverter.GetBytes((UInt32)fileHandle), 0, descriptors, descriptorsIndex, sizeof(UInt32));
                descriptorsIndex += sizeof(UInt32);
                Array.Copy(CC3100BitConverter.GetBytes((UInt32)(offset + bufferIndex)), 0, descriptors, descriptorsIndex, sizeof(UInt32));
                descriptorsIndex += sizeof(UInt32);
                Array.Copy(CC3100BitConverter.GetBytes((UInt16)chunkLen), 0, descriptors, descriptorsIndex, sizeof(UInt16));
                descriptorsIndex += sizeof(UInt16);
                // skip the 16-bit padding
                descriptorsIndex += sizeof(UInt16);

                // payload (32-bit aligned)

                // call function
                byte[] responseBytes = CallFunction(CC3100Opcode.NvMem_FileRead_Command, CC3100Opcode.NvMem_FileRead_Response, descriptors, null, Timeout.Infinite);

                if (responseBytes == null)
                {
                    statusOrLen = -1; /* TODO: is there an appropriate error response for "timeout"? */
                }
                else
                {
                    // response
                    responseIndex = 0;
                    statusOrLen = CC3100BitConverter.ToInt16(responseBytes, responseIndex);
                }

                if (statusOrLen < 0)
                {
                    if (bytesRead > 0)
                        return bytesRead;
                    else
                        return statusOrLen;
                }

                responseIndex += sizeof(Int16);
                // skip UInt16 padding
                responseIndex += sizeof(UInt16);
                // copy buffer
                Array.Copy(responseBytes, responseIndex, buffer, bufferIndex, chunkLen);

                bytesRead += statusOrLen;
                bufferIndex += chunkLen;
            }

            // response contains status
            return bytesRead;
        }

        public Int32 sl_FsWrite(Int32 fileHandle, UInt32 offset, byte[] buffer)
        {
            int descriptorsIndex = 0;
            int payloadIndex = 0;
            int chunkLen = 0;
            int maxChunkLen = 1460;
            int bytesWritten = 0;

            Int32 statusOrLen = 0;

            int bufferIndex = 0;
            while (bufferIndex < buffer.Length)
            {
                if (buffer.Length - bufferIndex < maxChunkLen)
                    chunkLen = buffer.Length - bufferIndex;
                else
                    chunkLen = maxChunkLen;

                // descriptors (32-bit aligned)
                byte[] descriptors = new byte[12];
                descriptorsIndex = 0;
                Array.Copy(CC3100BitConverter.GetBytes((UInt32)fileHandle), 0, descriptors, descriptorsIndex, sizeof(UInt32));
                descriptorsIndex += sizeof(UInt32);
                Array.Copy(CC3100BitConverter.GetBytes((UInt32)(offset + bufferIndex)), 0, descriptors, descriptorsIndex, sizeof(UInt32));
                descriptorsIndex += sizeof(UInt32);
                Array.Copy(CC3100BitConverter.GetBytes((UInt16)chunkLen), 0, descriptors, descriptorsIndex, sizeof(UInt16));
                descriptorsIndex += sizeof(UInt16);
                // skip the 16-bit padding
                descriptorsIndex += sizeof(UInt16);

                // payload (32-bit aligned)
                byte[] payload = new byte[RoundUpSizeToNearest32BitBoundary(chunkLen)];
                payloadIndex = 0;
                Array.Copy(buffer, bufferIndex, payload, payloadIndex, chunkLen);

                // call function
                byte[] responseBytes = CallFunction(CC3100Opcode.NvMem_FileWrite_Command, CC3100Opcode.NvMem_FileWrite_Response, descriptors, payload, Timeout.Infinite);

                if (responseBytes == null)
                {
                    statusOrLen = -1; /* TODO: is there an appropriate error response for "timeout"? */
                }
                else
                {
                    // response
                    statusOrLen = CC3100BitConverter.ToInt16(responseBytes, 0);
                }

                if (statusOrLen < 0)
                {
                    if (bytesWritten > 0)
                        return bytesWritten;
                    else
                        return statusOrLen;
                }

                bytesWritten += statusOrLen;
                bufferIndex += chunkLen;
            }

            // response contains status
            return bytesWritten;
        }

        #region SimpleLink NetApp API

        public Int32 sl_NetAppDnsGetHostByName(string hostName, SocketAddressFamily addressFamily, out UInt32 ipAddress)
        {
            /* NOTE: this function cannot be called by multiple threads at the same time; we use a function-specific lock to ensure thread safety */
            lock (_sl_NetAppDnsGetHostByName_SynchronizationLockObject)
            {
                Int32 index = 0;
                Int32 status = 0;

                // descriptors (32-bit aligned)
                byte[] descriptors = new byte[4];
                index = 0;
                Array.Copy(CC3100BitConverter.GetBytes((UInt16)hostName.Length), 0, descriptors, index, sizeof(UInt16));
                index += sizeof(UInt16);
                descriptors[index] = (byte)addressFamily;
                index++;
                //descriptors[index] = 0x00; // dummy byte
                index++;

                // payload (32-bit aligned)
                byte[] payload = new byte[RoundUpSizeToNearest32BitBoundary(hostName.Length)];
                index = 0;
                Array.Copy(System.Text.Encoding.UTF8.GetBytes(hostName), 0, payload, index, hostName.Length);
                index += hostName.Length;
                /* NOTE: remaining bytes are empty bytes */

                // register callback function
                CC3100Opcode asyncResponseOpCode = CC3100Opcode.NetApp_DnsGetHostByName_AsyncResponse;
                PendingResponse asyncPendingResponse = AddPendingResponse(asyncResponseOpCode);

                // call function
                byte[] responseBytes = CallFunction(CC3100Opcode.NetApp_DnsGetHostByName_Command, CC3100Opcode.NetApp_DnsGetHostByName_Response, descriptors, payload, Timeout.Infinite);

                // response contains immediate status; async response will contain final status and ip address
                if (responseBytes == null)
                {
                    status = -1; /* TODO: is there an appropriate error response for "timeout"? */
                }
                else
                {
                    // response
                    index = 0;
                    status = CC3100BitConverter.ToInt16(responseBytes, index);
                    index += sizeof(Int16);
                }
                if (status == 0) // success
                {
                    // now wait for async response with IP address
                    asyncPendingResponse.WaitHandle.WaitOne();
                    byte[] asyncResponseBytes = (byte[])asyncPendingResponse.ResponseData;

                    index = 0;
                    status = CC3100BitConverter.ToInt16(asyncResponseBytes, index);
                    ipAddress = 0; // set to default
                    switch (status)
                    {
                        case 0: // success
                            {
                                index += 4;
                                ipAddress = CC3100BitConverter.ToUInt32(asyncResponseBytes, index);
                            }
                            break;
                        case -161: // SL_NET_APP_DNS_NO_SERVER /* /* No DNS server was specified */
                            {
                                if (!_lastLinkState)
                                    throw new System.Net.Sockets.SocketException(System.Net.Sockets.SocketError.NetworkDown);
                                else
                                    throw new System.Net.Sockets.SocketException(System.Net.Sockets.SocketError.HostNotFound);
                            }
                        default:
                            throw new System.Net.Sockets.SocketException(System.Net.Sockets.SocketError.HostNotFound);
                    }
                }
                else
                {
                    // async response will not be called
                    RemovePendingResponse(asyncResponseOpCode);
                    ipAddress = 0;
                }
                return status;
            }
        }

        public Int32 sl_NetAppStop(sl_NetAppOptions appBitMap)
        {
            Int32 status = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            Int32 descriptorsIndex = 0;
            Array.Copy(CC3100BitConverter.GetBytes((UInt32)appBitMap), 0, descriptors, descriptorsIndex, sizeof(UInt32));
            descriptorsIndex += sizeof(UInt32);

            // payload (32-bit aligned)

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.NetApp_Stop_Command, CC3100Opcode.NetApp_Stop_Response, descriptors, null, Timeout.Infinite);

            if (responseBytes == null)
            {
                status = -1; /* TODO: is there an appropriate error response for "timeout"? */
            }
            else
            {
                // response
                status = CC3100BitConverter.ToInt16(responseBytes, 0);
            }

            return status;
        }


        #endregion /* SimpleLink NetApp API */

        UInt16 _sl_TruncatePayloadByProtocol(Int32 socketHandle, UInt16 length)
        {
            UInt16 maxLength;
            switch (socketHandle >> 4)
            {
                case 0: // UDPv4
                    maxLength = 1472;
                    break;
                case 1: // TCPv4
                    maxLength = 1460;
                    break;
                //case 2: // UDPv6
                //    maxLength = 1452;
                //    break;
                //case 3: // TCPv6
                //    maxLength = 1440;
                //    break;
                case 4: // TCPv4 (secure)
                    maxLength = 1386;
                    break;
                case 5: // UDPv4 (secure)
                    maxLength = 1386;
                    break;
                //case 6: // TCPv6 (secure)
                //    maxLength = 1396;
                //    break;
                //case 7: // UDPv6 (secure)
                //    maxLength = 1396;
                //    break;
                //case 8: // RAW Transmission
                //    maxLength = 1476;
                //    break;
                //case 9: // RAW Packet
                //    maxLength = 1514;
                //    break;
                //case 10: // RAW IPv4
                //    maxLength = 1480;
                //    break;
                //case 11: // RAW IPv6
                //    maxLength = 1480;
                //    break;
                default:
                    maxLength = 1440;
                    break;
            }

            return (maxLength < length ? maxLength : length);
        }

        public Int32 sl_Accept(Int32 socketHandle, SocketAddressFamily addressFamily, out UInt32 ipAddress, out UInt16 ipPort)
        {
            Int32 index = 0;
            Int32 status = 0;

            // default out var settings
            ipAddress = 0;
            ipPort = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            descriptors[index] = (byte)socketHandle;
            index++;
            descriptors[index] = (byte)((byte)addressFamily << 4); /* family */
            index++;

            // payload (32-bit aligned)

            // register callback function
            /* TODO: make sure we are only waiting for async response on THIS socketHandle; currently we are trapping ANY AsyncResponse. */
            CC3100Opcode asyncResponseOpCode = CC3100Opcode.Socket_Accept_IPv4_AsyncResponse;
            PendingResponse asyncPendingResponse = AddPendingResponse(asyncResponseOpCode, socketHandle);

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Socket_Accept_Command, CC3100Opcode.Socket_Accept_Response, descriptors, null, Timeout.Infinite);

            if (responseBytes == null)
            {
                status = -1; // error
            }
            else
            {
                // response contains immediate status; async response will contain final status
                index = 0;
                status = CC3100BitConverter.ToInt16(responseBytes, index);
                index += sizeof(Int16);
                byte compareSocketHandle = responseBytes[index];

                if (compareSocketHandle != socketHandle)
                {
                    status = -1; // error
                }
            }

            if (status == 0) // success
            {
                // now wait for async response with IP address
                asyncPendingResponse.WaitHandle.WaitOne();
                byte[] asyncResponseBytes = (byte[])asyncPendingResponse.ResponseData;

                index = 0;
                status = CC3100BitConverter.ToInt16(asyncResponseBytes, index);
                index += sizeof(Int16);

                if (status >= 0)
                {
                    byte compareAsyncSocketHandle = asyncResponseBytes[index];
                    index++;

                    if (compareAsyncSocketHandle != socketHandle)
                    {
                        status = -1; // error
                    }
                    else
                    {
                        // process response data
                        // skip addressFamily;
                        index++;
                        ipPort = (UInt16)(
                            ((UInt16)asyncResponseBytes[index] << 8) + 
                            asyncResponseBytes[index + 1]
                            ); // port uses network byte order
                        index += sizeof(UInt16);
                        // skip padding
                        index += sizeof(UInt16);
                        ipAddress = (UInt32)(
                            ((UInt32)asyncResponseBytes[index] << 24) +
                            ((UInt32)asyncResponseBytes[index + 1] << 16) +
                            ((UInt32)asyncResponseBytes[index + 2] << 8) +
                            asyncResponseBytes[index + 3]
                            ); // ip address uses network byte order
                        index += sizeof(UInt32);

                        // add our new socket to our socket list.
                        lock (_cc3100SocketListLockObject)
                        {
                            _cc3100SocketList.Add(new CC3100SocketInfo(status, addressFamily, SocketSocketType.Stream /* default socket type */));
                        }
                    }
                }
            }
            else
            {
                // async response will not be called
                RemovePendingResponse(asyncResponseOpCode);
            }
            return status;
        }

        public Int32 sl_Bind(Int32 socketHandle, SocketAddressFamily addressFamily, UInt32 ipAddress, UInt16 ipPort)
        {
            Int32 index = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[12];
            index = 0;
            //Array.Copy(CC3100BitConverter.GetBytes(lenOrPadding), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);
            descriptors[index] = (byte)socketHandle;
            index++;
            descriptors[index] = (byte)((byte)addressFamily << 4);
            index++;
            descriptors[index] = (byte)((ipPort >> 8) & 0xFF); // port high byte (uses network byte order)
            index++;
            descriptors[index] = (byte)(ipPort & 0xFF); // port low byte ( usesnetwork byte order)
            index++;
            descriptors[index] = (byte)((ipAddress >> 24) & 0xFF); // IP address high byte (uses network byte order)
            index++;
            descriptors[index] = (byte)((ipAddress >> 16) & 0xFF); // IP address continued
            index++;
            descriptors[index] = (byte)((ipAddress >> 8) & 0xFF); // IP address continued
            index++;
            descriptors[index] = (byte)(ipAddress & 0xFF); // IP address low byte (uses network byte order)
            index++;

            // payload (32-bit aligned)

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Socket_Bind_IPv4_Command, CC3100Opcode.Socket_Bind_Response, descriptors, null, Timeout.Infinite);

            if (responseBytes == null)
                return -1;

            // response contains status
            index = 0;
            return CC3100BitConverter.ToInt16(responseBytes, index); /* statusOrLen */
        }

        public Int32 sl_Close(Int32 socketHandle)
        {
            Int32 index = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            descriptors[index] = (byte)socketHandle;
            index++;

            // payload (32-bit aligned)

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Socket_Close_Command, CC3100Opcode.Socket_Close_Response, descriptors, null, Timeout.Infinite);

            // remove this socket from our connection list
            lock (_cc3100SocketListLockObject)
            {
                foreach (CC3100SocketInfo connectionInfo in _cc3100SocketList)
                {
                    if (connectionInfo.SocketHandle == socketHandle)
                    {
                        _cc3100SocketList.Remove(connectionInfo);
                        break;
                    }
                }
            }

            if (responseBytes == null)
                return -1;

            // response contains status
            index = 0;
            return CC3100BitConverter.ToInt16(responseBytes, index); /* statusOrLen */
        }

        public Int32 sl_Connect(Int32 socketHandle, SocketAddressFamily addressFamily, UInt32 ipAddress, UInt16 ipPort)
        {
            Int32 index = 0;
            Int32 status = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[12];
            index = 0;
            //Array.Copy(CC3100BitConverter.GetBytes((UInt16)lenOrPadding), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);
            descriptors[index] = (byte)socketHandle;
            index++;
            descriptors[index] = (byte)((byte)addressFamily << 4); /* family */
            index++;
            descriptors[index] = (byte)((ipPort >> 8) & 0xFF); // port high byte (uses network byte order)
            index++;
            descriptors[index] = (byte)(ipPort & 0xFF); // port low byte ( usesnetwork byte order)
            index++;
            //Array.Copy(CC3100BitConverter.GetBytes(paddingOrAddr), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);
            descriptors[index] = (byte)((ipAddress >> 24) & 0xFF); // IP address high byte (uses network byte order)
            index++;
            descriptors[index] = (byte)((ipAddress >> 16) & 0xFF); // IP address continued
            index++;
            descriptors[index] = (byte)((ipAddress >> 8) & 0xFF); // IP address continued
            index++;
            descriptors[index] = (byte)(ipAddress & 0xFF); // IP address low byte (uses network byte order)
            index++;

            // payload (32-bit aligned)

            // register callback function
            /* TODO: make sure we are only waiting for async response on THIS socketHandle; currently we are trapping ANY AsyncResponse. */
            CC3100Opcode asyncResponseOpCode = CC3100Opcode.Socket_Connect_AsyncResponse;
            PendingResponse asyncPendingResponse = AddPendingResponse(asyncResponseOpCode, socketHandle);

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Socket_Connect_IPv4_Command, CC3100Opcode.Socket_Connect_Response, descriptors, null, Timeout.Infinite);

            if (responseBytes == null)
            {
                status = -1; // error
            }
            else
            {
                // response contains immediate status; async response will contain final status
                index = 0;
                status = CC3100BitConverter.ToInt16(responseBytes, index);
            }

            if (status == 0) // success
            {
                // now wait for async response with IP address
                asyncPendingResponse.WaitHandle.WaitOne();
                byte[] asyncResponseBytes = (byte[])asyncPendingResponse.ResponseData;

                index = 0;
                status = CC3100BitConverter.ToInt16(asyncResponseBytes, index);

                // update our socket list to show our remote ip address/port
                lock (_cc3100SocketListLockObject)
                {
                    for (int iSocketInfo = 0; iSocketInfo < _cc3100SocketList.Count; iSocketInfo++)
                    {
                        CC3100SocketInfo socketInfo = ((CC3100SocketInfo)_cc3100SocketList[iSocketInfo]);
                        if (socketInfo.SocketHandle == socketHandle)
                        {
                            socketInfo.RemoteIPAddress = ipAddress;
                            socketInfo.RemoteIPPort = ipPort;
                        }
                    }
                }
            }
            else
            {
                // async response will not be called
                RemovePendingResponse(asyncResponseOpCode);
                ipAddress = 0;
            }
            return status;
        }

        public Int32 sl_Listen(Int32 socketHandle, Int16 backlog)
        {
            Int32 index = 0;
            Int32 statusOrLen = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            descriptors[index] = (byte)socketHandle;
            index++;
            descriptors[index] = (byte)backlog;
            //index++;
            //descriptors[index] = 0x00; // padding
            //index++;
            //descriptors[index] = 0x00; // padding
            //index++;

            // payload (32-bit aligned)

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Socket_Listen_Command, CC3100Opcode.Socket_Listen_Response, descriptors, null, Timeout.Infinite);

            if (responseBytes == null)
            {
                statusOrLen = -1; // error
            }
            else
            {
                // response contains status or socket ID
                index = 0;
                statusOrLen = CC3100BitConverter.ToInt16(responseBytes, index);
                index += sizeof(Int16);
            }

            return statusOrLen;
        }

        public Int32 sl_Recv(Int32 socketHandle, byte[] buffer, Int32 offset, Int32 count, Int16 flags)
        {
            if (offset + count > buffer.Length)
                throw new ArgumentException();

			if (count > 16000)
				count = 16000; // 16000 bytes is maximum
			if (count < 1)
				return 0;      // 1 byte is minimum

            Int32 index = 0;
            Int32 statusOrLen = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)count), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);
            descriptors[index] = (byte)socketHandle;
            index++;
            descriptors[index] = (byte)(flags & 0x0F); /* flags */
            index++;

            // payload (32-bit aligned)

            // register callback function
            /* TODO: make sure we are only waiting for async response on THIS socketHandle; currently we are trapping ANY AsyncResponse. */
            CC3100Opcode asyncResponseOpCode = CC3100Opcode.Socket_Recv_AsyncResponse;
            PendingResponse asyncPendingResponse = AddPendingResponse(asyncResponseOpCode, socketHandle);

            // call function
            CallFunction(CC3100Opcode.Socket_Recv_Command, CC3100Opcode.None, descriptors, null, Timeout.Infinite);

            // now wait for async response with IP address
            asyncPendingResponse.WaitHandle.WaitOne();
            byte[] asyncResponseBytes = (byte[])asyncPendingResponse.ResponseData;

            // async response contains length, sender information and actual data.
            index = 0;
            statusOrLen = CC3100BitConverter.ToInt16(asyncResponseBytes, index);
            index += sizeof(Int16);

            if (statusOrLen >= 0)
            {
                //socketHandle = responseBytes[index];
                index++;
                // skip the 8-bit padding
                index++;

                // copy data to buffer
                /* TODO: if our response is bigger than our buffer, we need to be returning some sort of error or status, correct? */
                Array.Copy(asyncResponseBytes, index, buffer, offset, System.Math.Min(statusOrLen, count));
            }

            return statusOrLen;
        }

        public Int32 sl_RecvFrom(Int32 socketHandle, byte[] buffer, Int32 offset, Int32 count, Int16 flags, SocketAddressFamily addressFamily, out UInt32 ipAddress, out UInt16 ipPort)
        {
            if (offset + count > buffer.Length)
                throw new ArgumentException();

            Int32 index = 0;
            Int32 statusOrLen = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            Array.Copy(CC3100BitConverter.GetBytes((UInt16)count), 0, descriptors, index, sizeof(UInt16));
            index += sizeof(UInt16);
            descriptors[index] = (byte)socketHandle;
            index++;
            descriptors[index] = (byte)(((byte)addressFamily << 4) | (byte)(flags & 0x0F)); ; /* familyAndFlags */
            index++;

            // payload (32-bit aligned)

            // register callback function
            /* TODO: make sure we are only waiting for async response on THIS socketHandle; currently we are trapping ANY AsyncResponse. */
            CC3100Opcode asyncResponseOpCode = CC3100Opcode.Socket_RecvFrom_IPv4_AsyncResponse;
            PendingResponse asyncPendingResponse = AddPendingResponse(asyncResponseOpCode, socketHandle);

            // call function
            CallFunction(CC3100Opcode.Socket_RecvFrom_Command, CC3100Opcode.None, descriptors, null, Timeout.Infinite);

            // now wait for async response with IP address
            asyncPendingResponse.WaitHandle.WaitOne();
            byte[] asyncResponseBytes = (byte[])asyncPendingResponse.ResponseData;

            // async response contains length, sender information and actual data.
            if (asyncResponseBytes == null)
            {
                statusOrLen = -1; // error
            }
            else
            {
                // response contains length, sender information and actual data.
                index = 0;
                statusOrLen = CC3100BitConverter.ToInt16(asyncResponseBytes, index);
                index += sizeof(Int16);
            }

            if (statusOrLen >= 0)
            {
                //socketHandle = responseBytes[index];
                index++;
                //SocketAddressFamily senderAddressFamily = (SocketAddressFamily)responseBytes[index];
                index++;
                ipPort = (UInt16)(((UInt16)asyncResponseBytes[index] << 8) | asyncResponseBytes[index + 1]);
                index += sizeof(Int16);
                //UInt16 paddingOrAddr = CC3100BitConverter.ToUInt16(responseBytes, index);
                index += sizeof(UInt16);
                ipAddress = (UInt32)(
                    ((UInt32)asyncResponseBytes[index] << 24) |
                    ((UInt32)asyncResponseBytes[index + 1] << 16) |
                    ((UInt32)asyncResponseBytes[index + 2] << 8) |
                    (UInt32)asyncResponseBytes[index + 3]
                    );
                index += sizeof(UInt32);

                // copy data to buffer
                Array.Copy(asyncResponseBytes, index, buffer, offset, System.Math.Min(asyncResponseBytes.Length - index, count));
            }
            else
            {
                ipAddress = 0;
                ipPort = 0;
            }

            return statusOrLen;
        }

        public Int32 sl_Select(ref Int32[] readSocketHandles, ref Int32[] writeSocketHandles, Int32 timeoutInMicroseconds)
        {
            /* NOTE: this function cannot be called by multiple threads at the same time; we use a function-specific lock to ensure thread safety */
            lock (_sl_Select_SynchronizationLockObject)
            {
                Int32 index = 0;
                Int32 status = 0;

                // find highest socket handle
                Int32 highestSocketHandlePlusOne = 1;
                UInt16 readSocketMask = 0;
                UInt16 writeSocketMask = 0;
                if (readSocketHandles != null)
                {
                    for (int i = 0; i < readSocketHandles.Length; i++)
                    {
                        if ((readSocketHandles[i] & 0x0F) > highestSocketHandlePlusOne)
                            highestSocketHandlePlusOne = (readSocketHandles[i] & 0x0F) + 1;
                        // add the readSocketHandle to our bitmask
                        readSocketMask |= (UInt16)(1 << (readSocketHandles[i] & 0x0F));
                    }
                }
                if (writeSocketHandles != null)
                {
                    for (int i = 0; i < writeSocketHandles.Length; i++)
                    {
                        if ((writeSocketHandles[i] & 0x0F) > highestSocketHandlePlusOne)
                            highestSocketHandlePlusOne = (writeSocketHandles[i] & 0x0F) + 1;
                        // add the readSocketHandle to our bitmask
                        writeSocketMask |= (UInt16)(1 << (writeSocketHandles[i] & 0x0F));
                    }
                }
                //UInt32 totalTimeoutMilliseconds = (UInt32)(timeout.Ticks / System.TimeSpan.TicksPerMillisecond);
                Int32 totalTimeoutMilliseconds = timeoutInMicroseconds / 1000;
                UInt16 timeoutSeconds = (UInt16)(totalTimeoutMilliseconds / 1000);
                UInt16 timeoutMilliseconds = (UInt16)(totalTimeoutMilliseconds % 1000);

                // descriptors (32-bit aligned)
                byte[] descriptors = new byte[12];
                index = 0;
                descriptors[index] = (byte)highestSocketHandlePlusOne;
                index++;
                descriptors[index] = (byte)(readSocketHandles != null ? readSocketHandles.Length : 0);
                index++;
                descriptors[index] = (byte)(writeSocketHandles != null ? writeSocketHandles.Length : 0);
                index++;
                // skip padding byte
                index++;
                Array.Copy(CC3100BitConverter.GetBytes(readSocketMask), 0, descriptors, index, sizeof(UInt16));
                index += sizeof(UInt16);
                Array.Copy(CC3100BitConverter.GetBytes(writeSocketMask), 0, descriptors, index, sizeof(UInt16));
                index += sizeof(UInt16);
                Array.Copy(CC3100BitConverter.GetBytes(timeoutMilliseconds), 0, descriptors, index, sizeof(UInt16));
                index += sizeof(UInt16);
                Array.Copy(CC3100BitConverter.GetBytes(timeoutSeconds), 0, descriptors, index, sizeof(UInt16));
                index += sizeof(UInt16);

                // payload (32-bit aligned)

                // register callback function
                CC3100Opcode asyncResponseOpCode = CC3100Opcode.Socket_Select_AsyncResponse;
                PendingResponse asyncPendingResponse = AddPendingResponse(asyncResponseOpCode);

                // call function
                byte[] responseBytes = CallFunction(CC3100Opcode.Socket_Select_Command, CC3100Opcode.Socket_Select_Response, descriptors, null, Timeout.Infinite);

                if (responseBytes == null)
                {
                    status = -1; // error
                }
                else
                {
                    // response contains immediate status; async response will contain final status
                    index = 0;
                    status = CC3100BitConverter.ToInt16(responseBytes, index);
                }

                if (status == 0) // success
                {
                    // now wait for async response with IP address
                    asyncPendingResponse.WaitHandle.WaitOne();
                    byte[] asyncResponseBytes = (byte[])asyncPendingResponse.ResponseData;

                    index = 0;
                    status = CC3100BitConverter.ToInt16(asyncResponseBytes, index);

                    /* TODO: sometimes we are getting shortened 4-byte reponses instead of all 8 bytes; investigate. */
                    if (status >= 0 && asyncResponseBytes.Length >= 8)
                    {
                        index += sizeof(Int16);
                        byte readSocketHandleCount = asyncResponseBytes[index];
                        index++;
                        byte writeSocketHandleCount = asyncResponseBytes[index];
                        index++;
                        readSocketMask = CC3100BitConverter.ToUInt16(asyncResponseBytes, index);
                        index += sizeof(UInt16);
                        writeSocketMask = CC3100BitConverter.ToUInt16(asyncResponseBytes, index);
                        index += sizeof(UInt16);

                        Int32[] returnReadSocketHandles = new Int32[readSocketHandleCount];
                        Int32 iReturnReadSocketHandles = 0;
                        if (readSocketHandles != null)
                        {
                            for (int i = 0; i < readSocketHandles.Length; i++)
                            {
                                if ((readSocketMask & (1 << (readSocketHandles[i] & 0x0F))) > 0)
                                {
                                    returnReadSocketHandles[iReturnReadSocketHandles] = readSocketHandles[i];
                                    iReturnReadSocketHandles++;
                                }
                            }
                        }
                        readSocketHandles = returnReadSocketHandles;

                        Int32[] returnWriteSocketHandles = new Int32[writeSocketHandleCount];
                        Int32 iReturnWriteSocketHandles = 0;
                        if (writeSocketHandles != null)
                        {
                            for (int i = 0; i < writeSocketHandles.Length; i++)
                            {
                                if ((writeSocketMask & (1 << (writeSocketHandles[i] & 0x0F))) > 0)
                                {
                                    returnWriteSocketHandles[iReturnWriteSocketHandles] = writeSocketHandles[i];
                                    iReturnWriteSocketHandles++;
                                }
                            }
                        }
                        writeSocketHandles = returnWriteSocketHandles;
                    }
                }
                else
                {
                    // async response will not be called
                    RemovePendingResponse(asyncResponseOpCode);
                }
                return status;
            }
        }

        /* TODO NOTE: Send does _not_ have a command response on CC3100, so in theory we could overflow our transmission medium by sending more data faster than it can
 *            handle at its current TX speed (for instance 802.11b @ <11mbps with our SPI transmission at 20mbps); contact TI or find a way to reliably know if we
 *            are overflowing our TX buffer; before we contact them, double-check the STATUS flags in dummy messages...that is probably the flow control method */
        public Int32 sl_Send(Int32 socketHandle, byte[] buffer, Int32 offset, Int32 count, Int16 flags)
        {
            if (offset + count > buffer.Length)
                throw new ArgumentException();

            Int32 index = 0;

            UInt16 chunkLen = _sl_TruncatePayloadByProtocol(socketHandle, (UInt16)count);

            // send data in chunks, if the total buffer is larger than the maximum frame size.
            for (int i = offset; i < offset + count; i += chunkLen)
            {
                UInt16 currentChunkLen = (offset + count - i < chunkLen) ? (UInt16)(offset + count - i) : chunkLen;

                // descriptors (32-bit aligned)
                byte[] descriptors = new byte[4];
                index = 0;
                Array.Copy(CC3100BitConverter.GetBytes(currentChunkLen), 0, descriptors, index, sizeof(UInt16));
                index += sizeof(UInt16);
                descriptors[index] = (byte)socketHandle;
                index++;
                descriptors[index] = (byte)(flags & 0x0F); /* flags */
                index++;

                // payload (32-bit aligned)
                byte[] payload = new byte[RoundUpSizeToNearest32BitBoundary(currentChunkLen)];
                index = 0;
                Array.Copy(buffer, i, payload, index, currentChunkLen);
                index += currentChunkLen;

                // call function
                /* TODO: create a way to get an "error" response back from call function, in case we're out of buffers, etc. */
                CallFunction(CC3100Opcode.Socket_Send_Command, CC3100Opcode.None, descriptors, payload, Timeout.Infinite);
            }

            /* TODO: if we received an error, return it instead of the "bytesWritten" count */
            return (Int16)count;
        }

        /* TODO NOTE: SendTo does _not_ have a command response on CC3100, so in theory we could overflow our transmission medium by sending more data faster than it can
         *            handle at its current TX speed (for instance 802.11b @ <11mbps with our SPI transmission at 20mbps); contact TI or find a way to reliably know if we
         *            are overflowing our TX buffer; before we contact them, double-check the STATUS flags in dummy messages...that is probably the flow control method */
        public Int32 sl_SendTo(Int32 socketHandle, byte[] buffer, Int32 offset, Int32 count, Int16 flags, SocketAddressFamily addressFamily, UInt32 ipAddress, UInt16 ipPort)
        {
            if (offset + count > buffer.Length)
                throw new ArgumentException();

            Int32 index = 0;

            UInt16 chunkLen = _sl_TruncatePayloadByProtocol(socketHandle, (UInt16)count);

            // send data in chunks, if the total buffer is larger than the maximum frame size.
            for (int i = offset; i < offset + count; i += chunkLen)
            {
                UInt16 currentChunkLen = (buffer.Length - i < chunkLen) ? (UInt16)(buffer.Length - i) : chunkLen;

                // descriptors (32-bit aligned)
                byte[] descriptors = new byte[12];
                index = 0;
                Array.Copy(CC3100BitConverter.GetBytes(currentChunkLen), 0, descriptors, index, sizeof(UInt16));
                index += sizeof(UInt16);
                descriptors[index] = (byte)socketHandle;
                index++;
                descriptors[index] = (byte)(((byte)addressFamily << 4) | (byte)(flags & 0x0F)); ; /* familyAndFlags */
                index++;
                descriptors[index] = (byte)((ipPort >> 8) & 0xFF); // port high byte (uses network byte order)
                index++;
                descriptors[index] = (byte)(ipPort & 0xFF); // port low byte ( usesnetwork byte order)
                index++;
                //Array.Copy(CC3100BitConverter.GetBytes(paddingOrAddr), 0, descriptors, index, sizeof(UInt16));
                index += sizeof(UInt16);
                descriptors[index] = (byte)((ipAddress >> 24) & 0xFF); // IP address high byte (uses network byte order)
                index++;
                descriptors[index] = (byte)((ipAddress >> 16) & 0xFF); // IP address continued
                index++;
                descriptors[index] = (byte)((ipAddress >> 8) & 0xFF); // IP address continued
                index++;
                descriptors[index] = (byte)(ipAddress & 0xFF); // IP address low byte (uses network byte order)
                index++;

                // payload (32-bit aligned)
                byte[] payload = new byte[RoundUpSizeToNearest32BitBoundary(currentChunkLen)];
                index = 0;
                Array.Copy(buffer, i, payload, index, currentChunkLen);
                index += currentChunkLen;

                // call function
                /* TODO: create a way to get an "error" response back from call function, in case we're out of buffers, etc. */
                CallFunction(CC3100Opcode.Socket_SendTo_IPv4_Command, CC3100Opcode.None, descriptors, payload, Timeout.Infinite);
            }

            /* TODO: if we received an error, return it instead of the buffer length */
            return (Int16)buffer.Length;
        }

        public Int32 sl_GetSockOpt(Int32 socketHandle, Int32 level, UInt16 name, byte[] values)
        {
            Int32 index = 0;
            Int32 statusOrLen = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            descriptors[index] = (byte)socketHandle;
            index++;
            descriptors[index] = (byte)level;
            index++;
            descriptors[index] = (byte)name;
            index++;
            descriptors[index] = (values != null ? (byte)values.Length : (byte)0);
            index++;

            // payload (32-bit aligned)

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Socket_GetSockOpt_Command, CC3100Opcode.Socket_GetSockOpt_Response, descriptors, null, Timeout.Infinite);

            if (responseBytes == null)
            {
                statusOrLen = -1; // error
            }
            else
            {
                /* NOTE: responseBytes is 4 bytes of descriptors followed by our actual 32bit-aligned payload */
                index = 0;
                statusOrLen = CC3100BitConverter.ToInt16(responseBytes, index);
                index += sizeof(Int16);
                // skip the 8-bit socketHandle
                index++;
                UInt16 valuesLength = responseBytes[index];
                index++;

                if (valuesLength > values.Length)
                    return (Int32)CC3100ErrorCode.SL_ESMALLBUF;

                Array.Copy(responseBytes, index, values, 0, valuesLength);
            }

            return statusOrLen;
        }

        public Int32 sl_SetSockOpt(Int32 socketHandle, Int32 level, UInt16 name, byte[] values)
        {
            Int32 index = 0;
            Int32 statusOrLen = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            descriptors[index] = (byte)socketHandle;
            index++;
            descriptors[index] = (byte)level;
            index++;
            descriptors[index] = (byte)name;
            index++;
            descriptors[index] = (values != null ? (byte)values.Length : (byte)0);
            index++;

            // payload (32-bit aligned)
            byte[] payload = null;
            if (values != null)
            {
                payload = new byte[RoundUpSizeToNearest32BitBoundary(values.Length)];
                index = 0;
                Array.Copy(values, 0, payload, index, values.Length);
            }

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Socket_SetSockOpt_Command, CC3100Opcode.Socket_SetSockOpt_Response, descriptors, payload, Timeout.Infinite);

            if (responseBytes == null)
            {
                statusOrLen = -1; // error
            }
            else
            {
                // response contains status or socket ID
                index = 0;
                statusOrLen = CC3100BitConverter.ToInt16(responseBytes, index);
                index += sizeof(Int16);
            }

            return statusOrLen;
        }

        public Int32 sl_Socket(SocketAddressFamily addressFamily, SocketSocketType socketType, SocketProtocolType protocolType)
        {
            Int32 index = 0;
            Int32 statusOrLen = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            descriptors[0] = (byte)addressFamily;
            descriptors[1] = (byte)socketType;
            descriptors[2] = (byte)protocolType;
            descriptors[3] = 0x00; // padding

            // payload (32-bit aligned)

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Socket_Socket_Command, CC3100Opcode.Socket_Socket_Response, descriptors, null, Timeout.Infinite);

            if (responseBytes == null)
            {
                statusOrLen = -1; // error
            }
            else
            {
                // response contains status or socket ID
                index = 0;
                statusOrLen = CC3100BitConverter.ToInt16(responseBytes, index);
                index += sizeof(Int16);
            }

            // negative status value indicates error
            if (statusOrLen < 0)
            {
                return statusOrLen;
            }
            else
            {
                byte socketHandle = responseBytes[index];

                // add our new socket to our socket list.
                lock (_cc3100SocketListLockObject)
                {
                    _cc3100SocketList.Add(new CC3100SocketInfo(socketHandle, addressFamily, socketType));
                }

                return socketHandle;
            }
        }

        #region SimpleLink WLAN API

        public Int32 sl_WLanPolicyGet(byte policyType, byte policy, byte[] values)
        {
            Int32 index = 0;
            Int32 status = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            descriptors[index] = policyType;
            index++;
            /* skip padding */
            index++;
            descriptors[index] = policy;
            index++;
            //skip policyLen
            index++;

            // payload (32-bit aligned)

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Wlan_Policy_Get_Command, CC3100Opcode.Wlan_Policy_Get_Response, descriptors, null, Timeout.Infinite);

            if (responseBytes == null)
            {
                status = -1; /* TODO: is there an appropriate error response for "timeout"? */
            }
            else
            {
                /* NOTE: responseBytes is 4 bytes of descriptors followed by our actual 32bit-aligned payload */
                index = 0;
                // skip policyType
                index++;
                // skip padding
                index++;
                byte responsePolicy = responseBytes[index];
                index++;
                Int32 policyLength = responseBytes[index];
                index++;

                if (policyLength > values.Length)
                    return (Int32)CC3100ErrorCode.SL_ESMALLBUF;

                if (policyLength > 0)
                {
                    Array.Copy(responseBytes, index, values, 0, policyLength);
                }
                else
                {
                    if (values.Length < 1)
                        return (Int32)CC3100ErrorCode.SL_ESMALLBUF;

                    values[0] = responsePolicy;
                }

                status = 0; // success
            }

            return status;
        }

        public Int32 sl_WlanPolicySet(byte policyType, byte policy, byte[] values)
        {
            Int32 index = 0;
            Int32 status = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            descriptors[index] = policyType;
            index++;
            // padding
            index++;
            descriptors[index] = policy;
            index++;

            // payload (32-bit aligned)
            byte[] payload = null;
            if (values != null)
            {
                payload = new byte[RoundUpSizeToNearest32BitBoundary(values.Length)];
                index = 0;
                Array.Copy(values, 0, payload, index, values.Length);
            }

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Wlan_Policy_Set_Command, CC3100Opcode.Wlan_Policy_Set_Response, descriptors, payload, Timeout.Infinite);

            if (responseBytes == null)
            {
                status = -1; /* TODO: is there an appropriate error response for "timeout"? */
            }
            else
            {
                // response
                index = 0;
                status = CC3100BitConverter.ToInt16(responseBytes, index);
                index += sizeof(Int16);
            }

            return status;
        }

        //bool SL_WlanProfileAdd(string ssid, byte[] bssid, SL_SecParams secParams, SL_SecParamsExt extSecParmas, UInt32 priority, UInt32 options)
        public Int32 sl_WlanProfileAdd(string ssid, byte[] bssid, WlanSecurityType securityType, string securityKey, object unimplementedEnterpriseSecurityParams, byte priority)
        {
            Int32 index = 0;
            Int32 status = 0;

            // descriptors (32-bit aligned)

            // payload (32-bit aligned)
            const int BASE_PAYLOAD_LENGTH = 11;
            byte[] payload = new byte[RoundUpSizeToNearest32BitBoundary(BASE_PAYLOAD_LENGTH + ssid.Length + (securityKey != null ? securityKey.Length : 0))];
            index = 0;
            payload[index] = (byte)securityType;
            index++;
            payload[index] = (byte)ssid.Length;
            index++;
            payload[index] = priority;
            index++;
            if (bssid != null)
            {
                Array.Copy(bssid, 0, payload, index, System.Math.Min(MAC_ADDRESS_SIZE, bssid.Length));
            }
            index += MAC_ADDRESS_SIZE;
            payload[index] = (byte)securityKey.Length;
            index++;
            //payLoad[index] = wepKeyID /* not implemented */
            index++;
            Array.Copy(System.Text.Encoding.UTF8.GetBytes(ssid), 0, payload, index, ssid.Length);
            index += ssid.Length;
            if (securityKey != null)
            {
                Array.Copy(System.Text.Encoding.UTF8.GetBytes(securityKey), 0, payload, index, securityKey.Length);
            }

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Wlan_Profile_Add_Command, CC3100Opcode.Wlan_Profile_Add_Response, null, payload, Timeout.Infinite);

            if (responseBytes == null)
            {
                status = -1; /* TODO: is there an appropriate error response for "timeout"? */
            }
            else
            {
                // response
                index = 0;
                status = CC3100BitConverter.ToInt16(responseBytes, index);
                index += sizeof(Int16);
            }

            return status; /* returns error or the index # of the newly-added profile */
        }

        public Int32 sl_WlanProfileDel(byte profileIndex)
        {
            Int32 index = 0;
            Int32 status = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            descriptors[index] = profileIndex;
            index++;
            descriptors[index] = 0xCC;
            index++;
            descriptors[index] = 0xCC;
            index++;
            descriptors[index] = 0xCC;
            index++;

            // payload (32-bit aligned)

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Wlan_Profile_Del_Command, CC3100Opcode.Wlan_Profile_Del_Response, descriptors, null, Timeout.Infinite);

            if (responseBytes == null)
            {
                status = -1; /* TODO: is there an appropriate error response for "timeout"? */
            }
            else
            {
                // response
                index = 0;
                status = CC3100BitConverter.ToInt16(responseBytes, index);
                index += sizeof(Int16);
            }

            return status;
        }

        /* TODO: we am working on this function, completing it; we run into errors when I send it--for unknown reasons. */
        public Int32 sl_WlanProfileGet(byte profileIndex, out string name, out byte[] bssid, out byte priority)
        {
            Int32 index = 0;
            Int32 status = 0;

            // default out values
            name = "";
            bssid = null;
            priority = 0;

            // descriptors (32-bit aligned)
            byte[] descriptors = new byte[4];
            index = 0;
            descriptors[index] = profileIndex;
            index++;
            descriptors[index] = 0xCC;
            index++;
            descriptors[index] = 0xCC;
            index++;
            descriptors[index] = 0xCC;
            index++;

            // payload (32-bit aligned)

            // call function
            byte[] responseBytes = CallFunction(CC3100Opcode.Wlan_Profile_Get_Command, CC3100Opcode.Wlan_Profile_Get_Response, descriptors, null, Timeout.Infinite);

            if (responseBytes == null)
            {
                status = -1; /* TODO: is there an appropriate error response for "timeout"? */
            }
            else
            {
                index = 0;
                byte securityType = responseBytes[index];
                index++;
                int nameLength = responseBytes[index];
                index++;
                priority = responseBytes[index];
                index++;
                bssid = new byte[MAC_ADDRESS_SIZE];
                Array.Copy(responseBytes, index, bssid, 0, MAC_ADDRESS_SIZE);
                index += MAC_ADDRESS_SIZE;
                /* skip 11 bytes:
                 * byte    passwordLen [empty, SimpleLink does NOT return passwords.]
                 * byte    wepKey
                 * --
                 * byte    userLen
                 * byte    anonUserLen
                 * byte    certIndex
                 * UInt16  padding
                 * UInt32  eapBitmask
                 */
                index += 11;
                if (responseBytes.Length >= index + nameLength)
                {
                    name = new string(System.Text.Encoding.UTF8.GetChars(responseBytes, index, nameLength));
                }
                else
                {
                    name = string.Empty;
                }

                status = securityType;
            }

            return status;
        }

        #endregion /* SimpleLink WLAN API */

        public CC3100SocketInfo GetSocketInfo(Int32 socketHandle)
        {
            // retrieve our socket connection info
            lock (_cc3100SocketListLockObject)
            {
                foreach (CC3100SocketInfo socketInfo in _cc3100SocketList)
                {
                    if (socketInfo.SocketHandle == socketHandle)
                    {
                        return socketInfo;
                    }
                }
            }

            // if we did not find socket info for this socketHandle, throw an exception
            throw new ArgumentException();
        }

        /* NOTE: millisecondsTimeout only applies to the command response; our function call waits indefinitely for previous commands to complete before its own execution */
        byte[] CallFunction(CC3100Opcode functionOpCode, CC3100Opcode responseOpCode, byte[] descriptors, byte[] payload, Int32 millisecondsTimeout)
        {
            // if we are using SPI transport, wait at least 10 ms after init is complete before proceeding (so that SPI can stabilize before we start issuing commands */
            if (!_isFirstCommandSent && _cc3100TransportType == CC3100TransportTypes.Spi)
            {
                ((CC3100SpiTransport)_cc3100Transport).SetIsFirstReadCommandAfterInit();
                _isFirstCommandSent = true;
            }

            if (!_isSimplelinkStarted) return null; /* TODO: return "SimpleLink not started" or other fatal error */

            // NOTE: we make sure that only one function call can be outstanding simultaneously
            AutoResetEvent synchronizationLockEvent = _callFunctionSynchronizationEvent; // make a local copy, in case sl_Stop/sl_Start is called during function call.
            synchronizationLockEvent.WaitOne();
            /* CRITICAL NOTE: no other code can come between this synchronization lock and the try block; the try block's finally section will release the lock */
            try
            {
                int length = ((descriptors != null) ? descriptors.Length : 0) + ((payload != null) ? payload.Length : 0);
                byte[] buffer = new byte[SYNC_WORD_SIZE_TX + OPCODE_FIELD_SIZE + LENGTH_FIELD_SIZE + length];
                Int32 index = 0;

                PendingResponse pendingResponse = null;
                if (responseOpCode != CC3100Opcode.None)
                    pendingResponse = AddPendingResponse(responseOpCode);

                Array.Copy(_syncWordWrite, 0, buffer, index, SYNC_WORD_SIZE_TX);
                index += SYNC_WORD_SIZE_TX;
                Array.Copy(CC3100BitConverter.GetBytes((UInt16)functionOpCode), 0, buffer, index, OPCODE_FIELD_SIZE);
                index += OPCODE_FIELD_SIZE;
                Array.Copy(CC3100BitConverter.GetBytes((UInt16)length), 0, buffer, index, LENGTH_FIELD_SIZE);
                index += LENGTH_FIELD_SIZE;
                if (descriptors != null)
                {
                    Array.Copy(descriptors, 0, buffer, index, descriptors.Length);
                    index += descriptors.Length;
                }
                if (payload != null)
                {
                    Array.Copy(payload, 0, buffer, index, payload.Length);
                    index += payload.Length;
                }
                _cc3100Transport.Write(buffer, 0, buffer.Length);

                if (responseOpCode != CC3100Opcode.None)
                {
                    // wait for response
                    if (pendingResponse.WaitHandle.WaitOne(millisecondsTimeout, false))
                    {
                        return (byte[])pendingResponse.ResponseData;
                    }
                    else
                    {
                        RemovePendingResponse(responseOpCode);
                        return null;
                    }
                }
                else
                {
                    return null;
                }
            }
            finally
            {
                synchronizationLockEvent.Set();
            }
        }

        void ClearPendingResponses()
        {
            lock (_pendingResponsesLockObject)
            {
                for (int i = 0; i < MAX_PENDING_RESPONSES; i++)
                {
                    _pendingResponses[i] = null;
                }
                _pendingResponsesCount = 0;
            }
        }

        PendingResponse AddPendingResponse(CC3100Opcode opCode, Int32 socketHandle = -1)
        {
            int index;
            lock (_pendingResponsesLockObject)
            {
                if (_pendingResponsesCount >= MAX_PENDING_RESPONSES)
                {
                    // no room for any more pending responses
                    throw new CC3100MaximumConcurrentActionsExceededException();
                }

                index = _pendingResponsesCount;
                if (_pendingResponses[index] == null)
                {
                    // allocate the pending response entry, if it has not been allocated before.
                    _pendingResponses[index] = new PendingResponse();
                }
                _pendingResponses[index].OpCode = opCode;
                _pendingResponses[index].ResponseData = null;
                _pendingResponses[index].SocketHandle = socketHandle;
                _pendingResponsesCount++;

                return _pendingResponses[index];
            }
        }

        PendingResponse RemovePendingResponse(CC3100Opcode opCode, Int32 socketHandle = -1)
        {
            PendingResponse response = null;
            lock (_pendingResponsesLockObject)
            {
                for (int i = 0; i < _pendingResponsesCount; i++)
                {
                    if (_pendingResponses[i] != null && _pendingResponses[i].OpCode == opCode && _pendingResponses[i].SocketHandle == socketHandle)
                    {
                        response = _pendingResponses[i];
                        // technically this shouldn't be necessary, but clear out the entry we just removed.
                        _pendingResponses[i] = null;

                        // remove the response
                        Array.Copy(_pendingResponses, i + 1, _pendingResponses, i, _pendingResponsesCount - i - 1);
                        _pendingResponsesCount--;

                        // clear the final entry
                        _pendingResponses[_pendingResponsesCount] = null;
                        break;
                    }
                }
            }

            return response;
        }

        // NOTE: _cc3100Transport_DataReceived is the only function allowed to add or remove data within the _incomingDataBuffer.
		void _cc3100Transport_DataReceived(object sender, EventArgs e)
        {
			/* TODO: do we need "while(_cc3100Transport.BytesToRead > 0)" loop here? so we don't lose data? */
			UInt32 syncWordIncoming = BitConverter.ToUInt32(_syncWordIncoming, 0);

			// read data until our data buffer is empty, since our event will not necessarily be called again if we can currently only fit _some_ data.
			while (_cc3100Transport.BytesToRead > 0)
			{
				// we have data; process it...
				lock (_incomingDataBufferLockObject)
				{
					int bytesRead = _cc3100Transport.Read(_incomingDataBuffer, _incomingDataBufferFirstAvailableIndex, System.Math.Min(_cc3100Transport.BytesToRead, INCOMING_BUFFER_SIZE - _incomingDataBufferFirstAvailableIndex));
					_incomingDataBufferFirstAvailableIndex += bytesRead;

					Int32 index = 0;
                    int startOfFrameIndex = 0;
					while (index < _incomingDataBufferFirstAvailableIndex - SYNC_WORD_SIZE_RX)
					{
						// if a sync word is found at this index, try to process its data
						if ((BitConverter.ToUInt32(_incomingDataBuffer, index) & _syncWordIncomingMask) == syncWordIncoming)
						{
                            startOfFrameIndex = index;
							index += SYNC_WORD_SIZE_RX; // move forward to the end of the SyncWord.
							if (index < _incomingDataBufferFirstAvailableIndex - OPCODE_FIELD_SIZE - LENGTH_FIELD_SIZE)
							{
								// if there is enough data received for our opcode and length, read them to determine if the entire data segment is available.
                                CC3100Opcode opCode = (CC3100Opcode)CC3100BitConverter.ToUInt16(_incomingDataBuffer, index);
								index += OPCODE_FIELD_SIZE;	// move forward to the end of the opcode field
                                UInt16 length = CC3100BitConverter.ToUInt16(_incomingDataBuffer, index);
								// move forward to the end of the length field
								index += LENGTH_FIELD_SIZE;

								/* sanity check: if length makes data larger than our entire buffer size, clear our buffer and discontinue processing of the current buffer. */
								if (index + length > INCOMING_BUFFER_SIZE)
								{
									index = 0;
									_incomingDataBufferFirstAvailableIndex = 0;
									break;
								}

								if (index <= _incomingDataBufferFirstAvailableIndex - length)
								{
									// entire frame has been received; process it now.

                                    // NOTE: the InitComplete event's FlowControl info is not valid data.
                                    if (opCode != CC3100Opcode.Device_InitComplete && length >= FLOW_CONTROL_FIELDS_SIZE)
                                    {
                                        // first four bytes: flow control data
                                        /* TODO: process four bytes of flow control data */
                                        //TxPoolCnt = _incomingDataBuffer[index + 0];
                                        //DevStatus = 0x00 = _incomingDataBuffer[index + 1];
                                        //SocketTXFailure = 0x00 = _incomingDataBuffer[index + 2]
                                        //SockeNonBlocking = 0x00 = _incomingDataBuffer[index + 3];
                                    }
                                    if (length >= FLOW_CONTROL_FIELDS_SIZE)
                                    {
                                        index += FLOW_CONTROL_FIELDS_SIZE;
                                        length -= FLOW_CONTROL_FIELDS_SIZE;
                                    }
                                    switch (opCode)
                                    {
                                        case CC3100Opcode.Wlan_Connect_Event:
                                            {
                                                _lastLinkState = true;
                                                if (_ledState != null) _ledState.Write(false);
                                                if (_ledLink != null) _ledLink.Write(true);
                                                if (OnLinkStateChanged != null)
                                                    OnLinkStateChanged(this, _lastLinkState);
                                            }
                                            break;
                                        case CC3100Opcode.Wlan_Disconnect_Event:
                                            {
                                                _lastLinkState = false;
                                                if (_ledLink != null) _ledLink.Write(false);
                                                if (_ledState != null) _ledState.Write(true);
                                                if (OnLinkStateChanged != null)
                                                    OnLinkStateChanged(this, _lastLinkState);
                                            }
                                            break;
                                        case CC3100Opcode.NetApp_IPv4IPAcquired_Event:
                                            {
                                                lock (_cachedIpv4ConfigurationLockObject)
                                                {
                                                    _cachedIpv4Address = CC3100BitConverter.ToUInt32(_incomingDataBuffer, index);
                                                    __cachedIpv4ConfigurationIsDirty = true;
                                                }
                                                //_cachedGatewayAddress = CC3100BitConverter.ToUInt32(_incomingDataBuffer, index);
                                                //_cachedDnsAddress = CC3100BitConverter.ToUInt32(_incomingDataBuffer, index);

                                                if (OnIPv4AddressChanged != null)
                                                    OnIPv4AddressChanged(this, _cachedIpv4Address);
                                            }
                                            break;
                                        case CC3100Opcode.Socket_Accept_IPv4_AsyncResponse:
                                        case CC3100Opcode.Socket_Connect_AsyncResponse:
                                        case CC3100Opcode.Socket_Recv_AsyncResponse:
                                        case CC3100Opcode.Socket_RecvFrom_IPv4_AsyncResponse:
                                            Int32 socketHandle = _incomingDataBuffer[index + 2];
        									SaveIncomingFrame(opCode, _incomingDataBuffer, index, length, socketHandle);
                                            break;
                                        default:
        									SaveIncomingFrame(opCode, _incomingDataBuffer, index, length);
                                            break;
                                    }
                                    index += length;

                                    // remove this frame from _incomingDataBuffer.
                                    Array.Copy(_incomingDataBuffer, index, _incomingDataBuffer, 0, _incomingDataBufferFirstAvailableIndex - index);
                                    _incomingDataBufferFirstAvailableIndex -= index;
                                    index = 0;
								}
								else
								{
									// entire frame has not been received; jump to end of index to abort data processing.
									index = _incomingDataBufferFirstAvailableIndex;
								}
							}
						}
						else
						{
							// sync word is invalid; remove first byte of buffer...and then continue the loop without incrementing our index.
							Array.Copy(_incomingDataBuffer, 1, _incomingDataBuffer, 0, _incomingDataBufferFirstAvailableIndex - 1);
							_incomingDataBufferFirstAvailableIndex--;
						}
					}
				}
			}
        }

        public UInt32 GetIPv4ConfigurationLE_IPAddress()
        {
            UInt32 ipAddressBE, subnetMaskBE, gatewayAddressBE, dnsAddressBE;
            GetIpv4ConfigurationBE(out ipAddressBE, out subnetMaskBE, out gatewayAddressBE, out dnsAddressBE);
            return
                (((ipAddressBE >> 0) & 0xFF) << 24) +
                (((ipAddressBE >> 8) & 0xFF) << 16) +
                (((ipAddressBE >> 16) & 0xFF) << 8) +
                (((ipAddressBE >> 24) & 0xFF) << 0);
        }

        public UInt32 GetIPv4ConfigurationLE_SubnetMask()
        {
            UInt32 ipAddressBE, subnetMaskBE, gatewayAddressBE, dnsAddressBE;
            GetIpv4ConfigurationBE(out ipAddressBE, out subnetMaskBE, out gatewayAddressBE, out dnsAddressBE);
            return
                (((subnetMaskBE >> 0) & 0xFF) << 24) +
                (((subnetMaskBE >> 8) & 0xFF) << 16) +
                (((subnetMaskBE >> 16) & 0xFF) << 8) +
                (((subnetMaskBE >> 24) & 0xFF) << 0);
        }

        public UInt32 GetIPv4ConfigurationLE_GatewayAddress()
        {
            UInt32 ipAddressBE, subnetMaskBE, gatewayAddressBE, dnsAddressBE;
            GetIpv4ConfigurationBE(out ipAddressBE, out subnetMaskBE, out gatewayAddressBE, out dnsAddressBE);
            return
                (((gatewayAddressBE >> 0) & 0xFF) << 24) +
                (((gatewayAddressBE >> 8) & 0xFF) << 16) +
                (((gatewayAddressBE >> 16) & 0xFF) << 8) +
                (((gatewayAddressBE >> 24) & 0xFF) << 0);
        }

        public UInt32 GetIPv4ConfigurationLE_DnsAddress()
        {
            UInt32 ipAddressBE, subnetMaskBE, gatewayAddressBE, dnsAddressBE;
            GetIpv4ConfigurationBE(out ipAddressBE, out subnetMaskBE, out gatewayAddressBE, out dnsAddressBE);
            return
                (((dnsAddressBE >> 0) & 0xFF) << 24) +
                (((dnsAddressBE >> 8) & 0xFF) << 16) +
                (((dnsAddressBE >> 16) & 0xFF) << 8) +
                (((dnsAddressBE >> 24) & 0xFF) << 0);
        }

        //public void GetIpv4ConfigurationLE(out UInt32 ipAddress, out UInt32 subnetMask, out UInt32 gatewayAddress, out UInt32 dnsAddress)
        //{
        //    UInt32 ipAddressBE, subnetMaskBE, gatewayAddressBE, dnsAddressBE;
        //    GetIpv4ConfigurationBE(out ipAddressBE, out subnetMaskBE, out gatewayAddressBE, out dnsAddressBE);
        //    ipAddress =
        //        (((ipAddressBE >> 0) & 0xFF) << 24) +
        //        (((ipAddressBE >> 8) & 0xFF) << 16) +
        //        (((ipAddressBE >> 16) & 0xFF) << 8) +
        //        (((ipAddressBE >> 24) & 0xFF) << 0);
        //    subnetMask =
        //        (((subnetMaskBE >>  0) & 0xFF) << 24) +
        //        (((subnetMaskBE >>  8) & 0xFF) << 16) +
        //        (((subnetMaskBE >> 16) & 0xFF) << 8) +
        //        (((subnetMaskBE >> 24) & 0xFF) << 0);
        //    gatewayAddress = 
        //        (((gatewayAddressBE >>  0 )& 0xFF) << 24) +
        //        (((gatewayAddressBE >>  8) & 0xFF) << 16) +
        //        (((gatewayAddressBE >> 16) & 0xFF) <<  8) +
        //        (((gatewayAddressBE >> 24) & 0xFF) <<  0);
        //    dnsAddress = 
        //        (((dnsAddressBE >>  0) & 0xFF) << 24) +
        //        (((dnsAddressBE >>  8) & 0xFF) << 16) +
        //        (((dnsAddressBE >> 16) & 0xFF) <<  8) +
        //        (((dnsAddressBE >> 24) & 0xFF) <<  0);
        //}

        internal void GetIpv4ConfigurationBE(out UInt32 ipAddress, out UInt32 subnetMask, out UInt32 gatewayAddress, out UInt32 dnsAddress)
        {
            if (__cachedIpv4ConfigurationIsDirty)
            {
                UInt16 dhcpIsOn = 0;
                byte[] values = new byte[16];
                /* ERRATA fix: retry sl_NetCfgGet up to 3 times, 100ms apart, to work around an unknown CC3100 error code (-21991) */
                Int32 retVal = sl_NetCfgGet(SL_NetCfg_ConfigID.SL_IPV4_STA_P2P_CL_GET_INFO, ref dhcpIsOn, values);
                if (retVal < 0)
                    throw new CC3100SimpleLinkException(retVal); /* TODO: determine best exception to use for "could not communicate with CC3100 module" */

                // parse response
                lock (_cachedIpv4ConfigurationLockObject)
                {
                    _cachedIpv4Address = CC3100BitConverter.ToUInt32(values, 0);
                    _cachedSubnetMask = CC3100BitConverter.ToUInt32(values, 4);
                    _cachedGatewayAddress = CC3100BitConverter.ToUInt32(values, 8);
                    _cachedDnsAddress = CC3100BitConverter.ToUInt32(values, 12); 
                    __cachedIpv4ConfigurationIsDirty = false;
                }
            }
            ipAddress = _cachedIpv4Address;
            subnetMask = _cachedSubnetMask;
            gatewayAddress = _cachedGatewayAddress;
            dnsAddress = _cachedDnsAddress;
        }

        public void SetIpv4ConfigurationLE(UInt32 ipAddress, UInt32 subnetMask, UInt32 gatewayAddress, UInt32 dnsAddress)
        {
            UInt32 ipAddressBE, subnetMaskBE, gatewayAddressBE, dnsAddressBE;
            ipAddressBE =
                (((ipAddress >> 0) & 0xFF) << 24) +
                (((ipAddress >> 8) & 0xFF) << 16) +
                (((ipAddress >> 16) & 0xFF) << 8) +
                (((ipAddress >> 24) & 0xFF) << 0);
            subnetMaskBE =
                (((subnetMask >> 0) & 0xFF) << 24) +
                (((subnetMask >> 8) & 0xFF) << 16) +
                (((subnetMask >> 16) & 0xFF) << 8) +
                (((subnetMask >> 24) & 0xFF) << 0);
            gatewayAddressBE =
                (((gatewayAddress >> 0) & 0xFF) << 24) +
                (((gatewayAddress >> 8) & 0xFF) << 16) +
                (((gatewayAddress >> 16) & 0xFF) << 8) +
                (((gatewayAddress >> 24) & 0xFF) << 0);
            dnsAddressBE =
                (((dnsAddress >> 0) & 0xFF) << 24) +
                (((dnsAddress >> 8) & 0xFF) << 16) +
                (((dnsAddress >> 16) & 0xFF) << 8) +
                (((dnsAddress >> 24) & 0xFF) << 0);

            SetIpv4ConfigurationBE(ipAddressBE, subnetMaskBE, gatewayAddressBE, dnsAddressBE);
        }

        public void SetIpv4ConfigurationBE(UInt32 ipAddress, UInt32 subnetMask, UInt32 gatewayAddress, UInt32 dnsAddress)
        {
            byte[] values = new byte[16];
            values[0] = (byte)((ipAddress >> 0) & 0xFF);
            values[1] = (byte)((ipAddress >> 8) & 0xFF);
            values[2] = (byte)((ipAddress >> 16) & 0xFF);
            values[3] = (byte)((ipAddress >> 24) & 0xFF);
            values[4] = (byte)((subnetMask >> 0) & 0xFF);
            values[5] = (byte)((subnetMask >> 8) & 0xFF);
            values[6] = (byte)((subnetMask >> 16) & 0xFF);
            values[7] = (byte)((subnetMask >> 24) & 0xFF);
            values[8] = (byte)((gatewayAddress >> 0) & 0xFF);
            values[9] = (byte)((gatewayAddress >> 8) & 0xFF);
            values[10] = (byte)((gatewayAddress >> 16) & 0xFF);
            values[11] = (byte)((gatewayAddress >> 24) & 0xFF);
            values[12] = (byte)((dnsAddress >> 0) & 0xFF);
            values[13] = (byte)((dnsAddress >> 8) & 0xFF);
            values[14] = (byte)((dnsAddress >> 16) & 0xFF);
            values[15] = (byte)((dnsAddress >> 24) & 0xFF);
            sl_NetCfgSet(Netduino.IP.LinkLayers.CC3100.SL_NetCfg_ConfigID.SL_IPV4_STA_P2P_CL_STATIC_ENABLE, 1, values);
        }

        public void SetIpv4ConfigurationAsDhcp()
        {
            sl_NetCfgSet(Netduino.IP.LinkLayers.CC3100.SL_NetCfg_ConfigID.SL_IPV4_STA_P2P_CL_DHCP_ENABLE, 1, new byte[] { 0x01 });
        }

        public uint IPAddressFromStringBE(string ipAddress)
        {
            if (ipAddress == null)
                throw new ArgumentNullException();

            ulong ipAddressValue = 0;
            int lastIndex = 0;
            int shiftIndex = 24;
            ulong mask = 0x00000000FF000000;
            ulong octet = 0L;
            int length = ipAddress.Length;

            for (int i = 0; i < length; ++i)
            {
                // Parse to '.' or end of IP address
                if (ipAddress[i] == '.' || i == length - 1)
                    // If the IP starts with a '.'
                    // or a segment is longer than 3 characters or shiftIndex > last bit position throw.
                    if (i == 0 || i - lastIndex > 3 || shiftIndex > 24)
                    {
                        throw new ArgumentException();
                    }
                    else
                    {
                        i = i == length - 1 ? ++i : i;
                        octet = (ulong)(ConvertStringToInt32(ipAddress.Substring(lastIndex, i - lastIndex)) & 0x00000000000000FF);
                        ipAddressValue = ipAddressValue + (ulong)((octet << shiftIndex) & mask);
                        lastIndex = i + 1;
                        shiftIndex = shiftIndex - 8;
                        mask = (mask >> 8);
                    }
            }

            return (uint)ipAddressValue;
        }

        public static int ConvertStringToInt32(string value)
        {
            char[] num = value.ToCharArray();
            int result = 0;

            bool isNegative = false;
            int signIndex = 0;

            if (num[0] == '-')
            {
                isNegative = true;
                signIndex = 1;
            }
            else if (num[0] == '+')
            {
                signIndex = 1;
            }

            int exp = 1;
            for (int i = num.Length - 1; i >= signIndex; i--)
            {
                if (num[i] < '0' || num[i] > '9')
                {
                    throw new ArgumentException();
                }

                result += ((num[i] - '0') * exp);
                exp *= 10;
            }

            return (isNegative) ? (-1 * result) : result;
        }

		/* *** Event Clases (left-shifted 10 bits in the opcode?) ***
         * SL_EVENT_CLASS_WLAN = 2 <_- SL_WLAN_CONNECT_EVENT, SL_WLAN_DISCONNECT_EVENT
         * SL_EVENT_CLASS_NETAPP = 4 <-- SL_NETAPP_IPV4_ACQUIRED
         * 
         * *** SL_EVENT_CLASS_WLAN connection user events ***
         * SL_WLAN_CONNECT_EVENT = 1
         *   - ssid_name
         *   - ssid_len
         *   - bssid
         *   - go_peer_device_name
         *   - go_peer_device_name_len
         * SL_WLAN_DISCONNECT_EVENT = 2 <-- we should also call the IPv4Changed event (and set our IP address to 0)
         *   - ssid_name
         *   - ssid_len
         *   - reason_code
         * *** SL_EVENT_CLASS_BSD user events ***
         * SL_SOCKET_TX_FAILED_EVENT = 1
         *   - socketHandle
         *   - status
         * SL_SOCKET_ASYNC_EVENT = 2
         *   - socketHandle
         *   - type: SSL_ACCEPT or RX_FRAGMENTATION_TOO_BIG or OTHER_SIDE_CLOSE_SSL_DATA_NOT_ENCRYPTED
         *   - val
         * *** SL_EVENT_CLASS_NETAPP user events ***
         * SL_NETAPP_IPV4_IPACQUIRED_EVENT = 1
         *   - ip
         *   - gateway
         *   - dns
         *   
         * SL_OPCODE_WLAN_WLANASYNCCONNECTEDRESPONSE = 0x0880
         *   maps to SL_WLAN_CONNECT_EVENT
         * SL_OPCODE_WLAN_WLANASYNCDISCONNECTEDRESPONSE = 0x0881
         *   maps to SL_WLAN_DISCONNECT_EVENT
         * SL_OPCODE_NETAPP_IPACQUIRED = 0x1825
         *   maps to SL_NETAPP_IPV4_IPACQUIRED_EVENT
         * SL_OPCODE_SOCKET_TXFAILEDASYNCRESPONSE = 0x100E
         *   maps to SL_SOCKET_TX_FAILED_EVENT
         * SL_OPCODE_SOCKET_SOCKETASYNCEVENT = 0x100F
         *   maps to SL_SOCKET_ASYNC_EVENT
         *   
         * Classes of response: // note: these may not be set in the incoming data bytes
         * RECV_RESP_CLASS = 0
         * CMD_RESP_CLASS = 1
         * ASYNC_EVT_CLASS = 2
         * DUMMY_MSG_CLASS = 3
         */

		void SaveIncomingFrame(CC3100Opcode opCode, byte[] buffer, int offset, int length, Int32 socketHandle = -1)
		{
            PendingResponse response = RemovePendingResponse(opCode, socketHandle);
            if (response == null)
                return; // nothing to process

            // process incoming frame
            switch (opCode)
            {
                case CC3100Opcode.Device_InitComplete:
                    {
                        if (length >= 4)
                        {
                            response.ResponseData = CC3100BitConverter.ToUInt32(buffer, offset);
                        }
                    }
                    break;
                default:
                    {
                        response.ResponseData = new byte[length];
                        Array.Copy(buffer, offset, (byte[])response.ResponseData, 0, length);
                    }
                    break;
            }

            // trigger the pending response's WaitHandle so that the caller knows the action is complete.
            response.WaitHandle.Set();
		}

		void EnterPowerDownMode()
        {
            /* TODO: (if necessary) turn off packet reception */
			/* TODO: (if necessary) wait for any in-progress incoming frames to complete reception */
			/* TODO: (if necessary) wait for any in-progress outgoing frame to complete transmission */
			// put the chip into hibernate mode
			_hibernatePin.Write(false);

            try { _ledLink.Write(false); } 
            catch { }
            finally { /*_ledLink.Dispose();*/ }

            try { _ledState.Write(false); }
            catch { }
            finally { /*_ledState.Dispose();*/ }
        }

        event LinkStateChangedEventHandler ILinkLayer.LinkStateChanged
        {
            add { OnLinkStateChanged += value; }
            remove { OnLinkStateChanged -= value; }
        }

        event PacketReceivedEventHandler ILinkLayer.PacketReceived
        {
            add { OnPacketReceived += value; }
            remove { OnPacketReceived -= value; }
        }

        public event IPv4AddressChangedEventHandler IPv4AddressChanged
        {
            add { OnIPv4AddressChanged += value; }
            remove { OnIPv4AddressChanged -= value; }
        }

        internal void UpgradeFirmware()
        {
            /* GET VERSION START */
            //_u32               ChipId;
            //_u32               FwVersion[4];
            //_u8                PhyVersion[4];
            //_u32               NwpVersion[4];
            //_u16               RomVersion;
            //_u16               Padding;
            byte[] versionBuffer = new byte[44];
            Int32 retVal = sl_DevGet(1 /*SL_DEVICE_GENERAL_CONFIGURATION*/, 12 /*SL_DEVICE_GENERAL_VERSION*/, versionBuffer);
            if (retVal < 0)
            {
                Debug.Print("Could not retrieve version information.");
            }
            Int32 bufferIndex = 0;
            UInt32 chipId = CC3100BitConverter.ToUInt32(versionBuffer, bufferIndex);
            bufferIndex += sizeof(UInt32);
            UInt32[] fwVersion = new UInt32[4];
            for (int i = 0; i < fwVersion.Length; i++)
            {
                fwVersion[i] = CC3100BitConverter.ToUInt32(versionBuffer, bufferIndex);
                bufferIndex += sizeof(UInt32);
            }
            byte[] phyVersion = new byte[4];
            for (int i = 0; i < phyVersion.Length; i++)
            {
                phyVersion[i] = versionBuffer[bufferIndex];
                bufferIndex++;
            }
            UInt32[] nwpVersion = new UInt32[4];
            for (int i = 0; i < nwpVersion.Length; i++)
            {
                nwpVersion[i] = CC3100BitConverter.ToUInt32(versionBuffer, bufferIndex);
                bufferIndex += sizeof(UInt32);
            }
            UInt32 romVersion = CC3100BitConverter.ToUInt16(versionBuffer, bufferIndex);
            //UInt32 padding = CC3100BitConverter.ToUInt16(versionBuffer, bufferIndex);

            Debug.Print("*** Version info before upgrade ***\r\n");
            Debug.Print("ChipID: 0x" + chipId.ToString("X"));
            Debug.Print("fwVersion: " + fwVersion[0] + "." + fwVersion[1] + "." + fwVersion[2] + "." + fwVersion[3]);
            Debug.Print("phyVersion: " + phyVersion[0] + "." + phyVersion[1] + "." + phyVersion[2] + "." + phyVersion[3]);
            Debug.Print("nwpVersion: " + nwpVersion[0] + "." + nwpVersion[1] + "." + nwpVersion[2] + "." + nwpVersion[3]);
            Debug.Print("romVersion: " + romVersion.ToString());
            Debug.Print("");
            /* GET VERSION FINISH */

            //Debug.Print("Deleting all Wi-Fi associations.");
            //sl_WlanProfileDel(0xFF);
            //sl_WlanPolicySet(0x10 /* SL_POLICY_CONNECTION */, 0x00, null);
            //sl_Stop(1000);
            //sl_Start(-1);

            /* FIRMWARE UPGRADE BEGIN */

            // create the service pack file.
            Debug.Print("Creating service pack file.");
            UInt32 servicePackFileToken = 0;
            Int32 servicePackFileHandle;
            retVal = sl_FsOpen("/sys/servicepack.ucf", _sl_GetCreateFsMode(128 * 1024, CC3100._sl_FsAccessFlags.Secure | CC3100._sl_FsAccessFlags.Commit | CC3100._sl_FsAccessFlags.Write), ref servicePackFileToken, out servicePackFileHandle);
            if (retVal < 0)
            {
                Debug.Print("Cannot create service pack file.");
                return;
            }

            // write the service pack.
            Int32 remainingLen = CC3100ServicePack.ServicePackImage.Length;
            Int32 movingOffset = 0;
            Int32 MAX_CHUNK_LEN = 1024;
            Int32 chunkLen = (Int32)System.Math.Min(MAX_CHUNK_LEN, remainingLen);
            movingOffset = 0;

            /* Flashing is done in 1024 bytes chunks because of a bug resolved in later patches */

            do
            {
                Debug.Print("Writing service pack at offset: " + movingOffset.ToString());
                byte[] currentChunk = new byte[chunkLen];
                Array.Copy(CC3100ServicePack.ServicePackImage, movingOffset, currentChunk, 0, chunkLen);
                retVal = sl_FsWrite(servicePackFileHandle, (UInt32)movingOffset, currentChunk);
                if (retVal < 0)
                {
                    /* cannot program ServicePack file */
                    Debug.Print("Failure writing service pack.");
                    return;
                }

                remainingLen -= chunkLen;
                movingOffset += chunkLen;
                chunkLen = (Int32)System.Math.Min(MAX_CHUNK_LEN, remainingLen);
            } while (chunkLen > 0);


            /* close the servicepack file */
            Debug.Print("Closing service pack file.");
            retVal = sl_FsClose(servicePackFileHandle, "", CC3100ServicePack.ServicePackImageSig);
            if (retVal < 0)
            {
                /* cannot close Service Pack file */
                Debug.Print("Could not close service pack.");
                return;
            }
            Debug.Print("Service pack successfully written.");

            /* FIRMWARE UPGRADE END */

            sl_Stop(100);
            sl_Start(System.Threading.Timeout.Infinite);

            Array.Clear(versionBuffer, 0, versionBuffer.Length);
            retVal = sl_DevGet(1 /*SL_DEVICE_GENERAL_CONFIGURATION*/, 12 /*SL_DEVICE_GENERAL_VERSION*/, versionBuffer);
            if (retVal < 0)
            {
                Debug.Print("Could not retrieve version information.");
            }
            bufferIndex = 0;
            chipId = CC3100BitConverter.ToUInt32(versionBuffer, bufferIndex);
            bufferIndex += sizeof(UInt32);
            fwVersion = new UInt32[4];
            for (int i = 0; i < fwVersion.Length; i++)
            {
                fwVersion[i] = CC3100BitConverter.ToUInt32(versionBuffer, bufferIndex);
                bufferIndex += sizeof(UInt32);
            }
            phyVersion = new byte[4];
            for (int i = 0; i < phyVersion.Length; i++)
            {
                phyVersion[i] = versionBuffer[bufferIndex];
                bufferIndex++;
            }
            nwpVersion = new UInt32[4];
            for (int i = 0; i < nwpVersion.Length; i++)
            {
                nwpVersion[i] = CC3100BitConverter.ToUInt32(versionBuffer, bufferIndex);
                bufferIndex += sizeof(UInt32);
            }
            romVersion = CC3100BitConverter.ToUInt16(versionBuffer, bufferIndex);
            //UInt32 padding = CC3100BitConverter.ToUInt16(versionBuffer, bufferIndex);

            Debug.Print("*** Version info after upgrade ***\r\n");
            Debug.Print("ChipID: 0x" + chipId.ToString("X"));
            Debug.Print("fwVersion: " + fwVersion[0] + "." + fwVersion[1] + "." + fwVersion[2] + "." + fwVersion[3]);
            Debug.Print("phyVersion: " + phyVersion[0] + "." + phyVersion[1] + "." + phyVersion[2] + "." + phyVersion[3]);
            Debug.Print("nwpVersion: " + nwpVersion[0] + "." + nwpVersion[1] + "." + nwpVersion[2] + "." + nwpVersion[3]);
            Debug.Print("romVersion: " + romVersion.ToString());
            Debug.Print("");
            /* GET VERSION FINISH */

        }
    }
}
