////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) Microsoft Corporation.  All rights reserved.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

using Microsoft.SPOT.Hardware;
using System;
using System.Collections;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Netduino.IP.LinkLayers
{
    public static class CC3100SocketNative
    {
        public const int FIONREAD = 0x4004667F;

        static public Netduino.IP.LinkLayers.CC3100 _cc3100;
        static public bool _isInitialized = false;

        static object _initializeMethodSyncObject = new object();

        static public void Initialize()
        {
            lock (_initializeMethodSyncObject)
            {
                /* initialize our CC3100 Wi-Fi chip */
                if (_isInitialized)
                    return;

                // TODO: in final implementation, we must use reflection to instantiate our particular networking driver.
                //       Type linkLayerType = Type.GetType("Netduino.IP.LinkLayers.ENC28J60, Netduino.IP.LinkLayers.ENC28J60");
                //       System.Reflection.ConstructorInfo linkLayerConstructor = linkLayerType.GetConstructor(new Type[] { typeof(SPI.SPI_module), typeof(Cpu.Pin), typeof(Cpu.Pin), typeof(Cpu.Pin), typeof(Cpu.Pin) });
                //       linkLayer = (Netduino.IP.ILinkLayer)linkLayerConstructor.Invoke(new object[] { SPI.SPI_module.SPI2 /* spiBusID */, (Cpu.Pin)0x28 /* csPinID */, 
                //          (Cpu.Pin)0x04 /* intPinID */, (Cpu.Pin)0x12 /* resetPinID */, (Cpu.Pin)0x44 /* wakeupPinID */});

                // Netduino 3 Wi-Fi SPI transport:
                //_cc3100 = new Netduino.IP.LinkLayers.CC3100(SPI.SPI_module.SPI2, (Cpu.Pin)0x28, (Cpu.Pin)0x04, (Cpu.Pin)0x12, (Cpu.Pin)0x44);

                // Netduino 3 Wi-Fi UART transport:
                _cc3100 = new Netduino.IP.LinkLayers.CC3100("COM8", (Cpu.Pin)0x04, (Cpu.Pin)0x12, (Cpu.Pin)0x44);

                /* connect our ACT LEDs (link/act and state) */
                _cc3100.SetLinkLedPinID((Cpu.Pin)0x29);
                _cc3100.SetStateLedPinID((Cpu.Pin)0x08);

                // retrieve MAC address and Wi-Fi settings from the config sector
                //NetworkInformation.Wireless80211 networkInterface = (NetworkInformation.Wireless80211)NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()[0];
                object networkInterface = Netduino.IP.Interop.NetworkInterface.GetNetworkInterface(0);

                /* TODO: retrieve Wi-Fi SSID, security type and security key from config sector */
                //string ssid = networkInterface.Ssid;
                string ssid = (string)networkInterface.GetType().GetField("Ssid").GetValue(networkInterface);
                Netduino.IP.LinkLayers.CC3100.WlanSecurityType wlanSecurityType;
                //Type.GetType("Microsoft.SPOT.Net.NetworkInformation.Wireless80211.EncryptionType", "Microsoft.SPOT.Net")
                Int32 encryption = Int32.Parse(networkInterface.GetType().GetField("Encryption").GetValue(networkInterface).ToString());
                switch (encryption /* networkInterface.Encryption */)
                {
                    case 2: /* NetworkInformation.Wireless80211.EncryptionType.WPA: */
                    case 3: /* NetworkInformation.Wireless80211.EncryptionType.WPAPSK: */
                        wlanSecurityType = Netduino.IP.LinkLayers.CC3100.WlanSecurityType.Wpa2;
                        break;
                    case 1: /* NetworkInformation.Wireless80211.EncryptionType.WEP: */
                        wlanSecurityType = Netduino.IP.LinkLayers.CC3100.WlanSecurityType.Wep;
                        break;
                    case 0: /* NetworkInformation.Wireless80211.EncryptionType.None: */
                        wlanSecurityType = Netduino.IP.LinkLayers.CC3100.WlanSecurityType.Open;
                        break;
                    default:
                        throw new NotSupportedException();
                }
                //string securityKey = networkInterface.PassPhrase;
                string securityKey = (string)networkInterface.GetType().GetField("PassPhrase").GetValue(networkInterface);

                ((Netduino.IP.ILinkLayer)_cc3100).LinkStateChanged += _cc3100_LinkStateChanged;
                _cc3100.IPv4AddressChanged += _cc3100_IPv4AddressChanged;

                //Type networkInterfaceType = Type.GetType("Microsoft.SPOT.Net.NetworkInformation.NetworkInterface, Microsoft.SPOT.Net");
                byte[] physicalAddress = (byte[])networkInterface.GetType().GetMethod("get_PhysicalAddress").Invoke(networkInterface, new object[] { });
                ((Netduino.IP.ILinkLayer)_cc3100).SetMacAddress(physicalAddress /*networkInterface.PhysicalAddress*/);
                ((Netduino.IP.ILinkLayer)_cc3100).Start();

                //DateTime currentDateTime = new DateTime(2015, 04, 16, 12, 0, 0);
                //Temp_SetCC3100DateTime(currentDateTime);

                int retVal = -1;
                bool requiresConfigReload = false;

                byte priority = 7; /* highest priority */
                string compareSsid;
                byte[] compareBssid;
                byte comparePriority;

                // make sure we are configured for the specified AP; if not, update our configuration.
                /* WARNING: there is no way to verify the AP security key here; we should store a hash of it in a file and read in that file to confirm */
                retVal = _cc3100.sl_WlanProfileGet(0, out compareSsid, out compareBssid /* ignored */, out comparePriority);
                if ((retVal != (Int32)wlanSecurityType) || (ssid != compareSsid) || (priority != comparePriority))
                {
                    _cc3100.sl_WlanProfileDel(0xFF);
                    _cc3100.sl_WlanProfileAdd(ssid, null, wlanSecurityType, securityKey, null, priority);
                    requiresConfigReload = true;
                }

                // make sure our connection policy is set to automatically connect to our AP--and to try a fast reconnect to the last-used access point at boot.
                /* TODO: fast reconnect is a problem when deleting or switching profiles; it will keep a cached version EVEN IF WE HAVE CHANGED THE SSID until it successfully
                    *       finds another AP.  We should experiment with changing the policy to 0x01, rebooting, and then changing to 0x03 and rebooting...to see if that will "take"
                    *       the new SSID while also purging the cache of old valid AP profiles */
                //byte connectionPolicy = 0x03; /* AutoConnect + Fast Reconnect: SL_CONNECTION_POLICY(1,1,0,0,0) */
                byte connectionPolicy = 0x01; /* AutoConnect: SL_CONNECTION_POLICY(1,0,0,0,0) */
                byte[] policyValues = new byte[1];
                _cc3100.sl_WLanPolicyGet(0x10 /* SL_POLICY_CONNECTION */, 0, policyValues);
                if (policyValues[0] != connectionPolicy)
                {
                    _cc3100.sl_WlanPolicySet(0x10 /* SL_POLICY_CONNECTION */, connectionPolicy, null);
                    requiresConfigReload = true;
                }

                // make sure that our IP configuration (static IP vs DHCP) is set correctly
                //bool dhcpEnabled = networkInterface.IsDhcpEnabled;
                bool dhcpEnabled = (bool)networkInterface.GetType().GetMethod("get_IsDhcpEnabled").Invoke(networkInterface, new object[] { });
                UInt32 ipAddress = 0;
                UInt32 subnetMask = 0;
                UInt32 gatewayAddress = 0;
                UInt32 dnsAddress = 0;
                if (!dhcpEnabled)
                {
                    //ipAddress = _cc3100.IPAddressFromStringBE(networkInterface.IPAddress);
                    ipAddress = _cc3100.IPAddressFromStringBE((string)networkInterface.GetType().GetMethod("get_IPAddress").Invoke(networkInterface, new object[] { }));
                    //subnetMask = _cc3100.IPAddressFromStringBE(networkInterface.SubnetMask);
                    subnetMask = _cc3100.IPAddressFromStringBE((string)networkInterface.GetType().GetMethod("get_SubnetMask").Invoke(networkInterface, new object[] { }));
                    //gatewayAddress = _cc3100.IPAddressFromStringBE(networkInterface.GatewayAddress);
                    gatewayAddress = _cc3100.IPAddressFromStringBE((string)networkInterface.GetType().GetMethod("get_GatewayAddress").Invoke(networkInterface, new object[] { }));
                    //dnsAddress = _cc3100.IPAddressFromStringBE(networkInterface.DnsAddresses.Length >= 1 ? networkInterface.DnsAddresses[0] : "0.0.0.0");
                    string[] dnsAddresses = (string[])networkInterface.GetType().GetMethod("get_DnsAddresses").Invoke(networkInterface, new object[] { });
                    dnsAddress = _cc3100.IPAddressFromStringBE(dnsAddresses.Length > 0 ? dnsAddresses[0] : "0.0.0.0");
                }
                UInt16 compareDhcpEnabled = 0;
                UInt32 compareIpAddress, compareSubnetMask, compareGatewayAddress, compareDnsAddress;
                byte[] ipConfigValues = new byte[16];
                _cc3100.sl_NetCfgGet(Netduino.IP.LinkLayers.CC3100.SL_NetCfg_ConfigID.SL_IPV4_STA_P2P_CL_GET_INFO, ref compareDhcpEnabled, ipConfigValues);
                compareIpAddress = Netduino.IP.LinkLayers.CC3100BitConverter.ToUInt32(ipConfigValues, 0);
                compareSubnetMask = Netduino.IP.LinkLayers.CC3100BitConverter.ToUInt32(ipConfigValues, 4);
                compareGatewayAddress = Netduino.IP.LinkLayers.CC3100BitConverter.ToUInt32(ipConfigValues, 8);
                compareDnsAddress = Netduino.IP.LinkLayers.CC3100BitConverter.ToUInt32(ipConfigValues, 12);
                if (dhcpEnabled && (compareDhcpEnabled == 0))
                {
                    _cc3100.SetIpv4ConfigurationAsDhcp();
                    requiresConfigReload = true;
                }
                else if (!dhcpEnabled && ((ipAddress != compareIpAddress) || (subnetMask != compareSubnetMask) || (gatewayAddress != compareGatewayAddress) || (dnsAddress != compareDnsAddress)))
                {
                    _cc3100.SetIpv4ConfigurationBE(ipAddress, subnetMask, gatewayAddress, dnsAddress);
                    requiresConfigReload = true;
                }

                if (requiresConfigReload)
                {
                    ((Netduino.IP.ILinkLayer)_cc3100).Stop();
                    ((Netduino.IP.ILinkLayer)_cc3100).Start();
                }

                _isInitialized = true;
            }
        }

        static void _cc3100_LinkStateChanged(object sender, bool state)
        {
            Type networkChangeListenerType = Type.GetType("Microsoft.SPOT.Net.NetworkInformation.NetworkChange+NetworkChangeListener, Microsoft.SPOT.Net");
            if (networkChangeListenerType != null)
            {
                // create instance of NetworkChangeListener
                System.Reflection.ConstructorInfo networkChangeListenerConstructor = networkChangeListenerType.GetConstructor(new Type[] { });
                object networkChangeListener = networkChangeListenerConstructor.Invoke(new object[] { });

                // now call the ProcessEvent function to create a NetworkEvent class.
                System.Reflection.MethodInfo processEventMethodType = networkChangeListenerType.GetMethod("ProcessEvent");
                object networkEvent = processEventMethodType.Invoke(networkChangeListener, new object[] { (UInt32)(((UInt32)(state ? 1 : 0) << 16) + ((UInt32)1 /* AvailabilityChanged*/)), (UInt32)0, DateTime.Now }); /* TODO: should this be DateTime.Now or DateTime.UtcNow? */

                // and finally call the static NetworkChange.OnNetworkChangeCallback function to raise the event.
                Type networkChangeType = Type.GetType("Microsoft.SPOT.Net.NetworkInformation.NetworkChange, Microsoft.SPOT.Net");
                if (networkChangeType != null)
                {
                    System.Reflection.MethodInfo onNetworkChangeCallbackMethod = networkChangeType.GetMethod("OnNetworkChangeCallback", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    onNetworkChangeCallbackMethod.Invoke(networkChangeType, new object[] {networkEvent});
                }
            }
        }

        static void _cc3100_IPv4AddressChanged(object sender, uint ipAddress)
        {
            Type networkChangeListenerType = Type.GetType("Microsoft.SPOT.Net.NetworkInformation.NetworkChange+NetworkChangeListener, Microsoft.SPOT.Net");
            if (networkChangeListenerType != null)
            {
                // create instance of NetworkChangeListener
                System.Reflection.ConstructorInfo networkChangeListenerConstructor = networkChangeListenerType.GetConstructor(new Type[] { });
                object networkChangeListener = networkChangeListenerConstructor.Invoke(new object[] { });

                // now call the ProcessEvent function to create a NetworkEvent class.
                System.Reflection.MethodInfo processEventMethodType = networkChangeListenerType.GetMethod("ProcessEvent");
                object networkEvent = processEventMethodType.Invoke(networkChangeListener, new object[] { (UInt32)(((UInt32)2 /* AddressChanged*/)), (UInt32)0, DateTime.Now }); /* TODO: should this be DateTime.Now or DateTime.UtcNow? */

                // and finally call the static NetworkChange.OnNetworkChangeCallback function to raise the event.
                Type networkChangeType = Type.GetType("Microsoft.SPOT.Net.NetworkInformation.NetworkChange, Microsoft.SPOT.Net");
                if (networkChangeType != null)
                {
                    System.Reflection.MethodInfo onNetworkChangeCallbackMethod = networkChangeType.GetMethod("OnNetworkChangeCallback", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    onNetworkChangeCallbackMethod.Invoke(networkChangeType, new object[] { networkEvent });
                }
            }
        }

        //private static void Temp_SetCC3100DateTime(DateTime utcDateTime)
        //{
        //    byte[] dateTimeArray = new byte[44];
        //    Int32 index = 0;
        //    /* time */
        //    Array.Copy(Netduino.IP.LinkLayers.CC3100BitConverter.GetBytes((UInt32)utcDateTime.Second), 0, dateTimeArray, index, sizeof(UInt32));
        //    index += sizeof(UInt32);
        //    Array.Copy(Netduino.IP.LinkLayers.CC3100BitConverter.GetBytes((UInt32)utcDateTime.Minute), 0, dateTimeArray, index, sizeof(UInt32));
        //    index += sizeof(UInt32);
        //    Array.Copy(Netduino.IP.LinkLayers.CC3100BitConverter.GetBytes((UInt32)utcDateTime.Hour), 0, dateTimeArray, index, sizeof(UInt32));
        //    index += sizeof(UInt32);
        //    /* date */
        //    Array.Copy(Netduino.IP.LinkLayers.CC3100BitConverter.GetBytes((UInt32)utcDateTime.Day), 0, dateTimeArray, index, sizeof(UInt32));
        //    index += sizeof(UInt32);
        //    Array.Copy(Netduino.IP.LinkLayers.CC3100BitConverter.GetBytes((UInt32)utcDateTime.Month), 0, dateTimeArray, index, sizeof(UInt32));
        //    index += sizeof(UInt32);
        //    Array.Copy(Netduino.IP.LinkLayers.CC3100BitConverter.GetBytes((UInt32)utcDateTime.Year), 0, dateTimeArray, index, sizeof(UInt32));
        //    index += sizeof(UInt32);
        //    /* skip day of week */
        //    index += sizeof(UInt32);
        //    /* skip day of year */
        //    index += sizeof(UInt32);
        //    /* skip reserved[3] */
        //    index += sizeof(UInt32);
        //    index += sizeof(UInt32);
        //    index += sizeof(UInt32);
        //    Int32 retVal = _cc3100.sl_DevSet(1 /* SL_DEVICE_GENERAL_CONFIGURATION */, 11 /* SL_DEVICE_GENERAL_CONFIGURATION_DATE_TIME */, dateTimeArray);
        //    if (retVal == 0)
        //    {
        //        Debug.Print("Set chip DateTime to: " + utcDateTime);
        //    }
        //    else
        //    {
        //        Debug.Print("Could not set chip DateTime.");
        //        return;
        //    }

        //    byte[] verifyDateTime = new byte[44];
        //    retVal = _cc3100.sl_DevGet(1, 11, verifyDateTime);
        //    if (retVal == 0)
        //    {
        //        Debug.Print("Verified written DateTime.");
        //    }
        //    else
        //    {
        //        Debug.Print("Could not verify written DateTime.");
        //        return;
        //    }
        //}

        public static int socket(int family, int type, int protocol)
        {
            if (!_isInitialized) Initialize();

            Netduino.IP.LinkLayers.CC3100.SocketAddressFamily ccSocketAddressFamily;
            Netduino.IP.LinkLayers.CC3100.SocketSocketType ccSocketSocketType;
            Netduino.IP.LinkLayers.CC3100.SocketProtocolType ccSocketProtocolType;

            switch (family)
            {
                case 2: /* InterNetwork */
                    ccSocketAddressFamily = Netduino.IP.LinkLayers.CC3100.SocketAddressFamily.IPv4;
                    break;
                case 23: /* InterNetworkV6 */
                default:
                    throw new NotSupportedException();
            }

            switch (type)
            {
                case 1: /* Stream */
                    ccSocketSocketType = Netduino.IP.LinkLayers.CC3100.SocketSocketType.Stream;
                    break;
                case 2: /* Dgram */
                    ccSocketSocketType = Netduino.IP.LinkLayers.CC3100.SocketSocketType.Dgram;
                    break;
                default:
                    throw new NotSupportedException();
            }

            switch (protocol)
            {
                case 6: /* TCP */
                    ccSocketProtocolType = Netduino.IP.LinkLayers.CC3100.SocketProtocolType.TCP;
                    break;
                case 17: /* UDP */
                    ccSocketProtocolType = Netduino.IP.LinkLayers.CC3100.SocketProtocolType.UDP;
                    break;
                default:
                    throw new NotSupportedException();
            }

            Int32 retVal = _cc3100.sl_Socket(ccSocketAddressFamily, ccSocketSocketType, ccSocketProtocolType);
            if (retVal < 0)
            {
                switch (retVal)
                {
                    case -10: // SL_ENSOCK /* The system limit on the total number of open socket, has been reached */
                        throw new System.Net.Sockets.SocketException(System.Net.Sockets.SocketError.TooManyOpenSockets);
                    default:
                        throw new CC3100SimpleLinkException(retVal); /* TODO: determine the best exception, based on retVal */
                }
            }

            return retVal;
        }

        public static void bind(int handle, byte[] address)
        {
            if (!_isInitialized) Initialize();

            UInt16 ipPort = (UInt16)(((UInt16)address[2] << 8) +
                (UInt16)address[3]);
            UInt32 ipAddress = ((UInt32)address[4] << 24) +
                ((UInt32)address[5] << 16) +
                ((UInt32)address[6] << 8) +
                (UInt32)address[7];

            /* NOTE: workaround for CC3100: all bind functions pass in 0.0.0.0 as the local address; it fails to assign a port when the actual IP address is submitted. */
            ipAddress = 0;

            Int32 retVal = _cc3100.sl_Bind(handle, Netduino.IP.LinkLayers.CC3100.SocketAddressFamily.IPv4, ipAddress, ipPort);
            if (retVal < 0)
            {
                throw new CC3100SimpleLinkException(retVal); /* TODO: determine the best exception, based on retVal */
            }
        }

        public static void connect(int handle, byte[] address, bool fThrowOnWouldBlock)
        {
            if (!_isInitialized) Initialize();

            UInt16 ipPort = (UInt16)(((UInt16)address[2] << 8) +
                (UInt16)address[3]);
            UInt32 ipAddress = ((UInt32)address[4] << 24) +
                ((UInt32)address[5] << 16) +
                ((UInt32)address[6] << 8) +
                (UInt32)address[7];

            Int32 retVal = _cc3100.sl_Connect(handle, Netduino.IP.LinkLayers.CC3100.SocketAddressFamily.IPv4, ipAddress, ipPort);
            if (retVal < 0)
            {
                switch (retVal)
                {
                    case -110: // SL_ETIMEDOUT /* Connection timed out */
                        throw new System.Net.Sockets.SocketException(System.Net.Sockets.SocketError.TimedOut);
                    case -111: // SL_ECONNREFUSED /* Connection refused */
                        throw new System.Net.Sockets.SocketException(System.Net.Sockets.SocketError.ConnectionRefused);
                    default:
                        throw new CC3100SimpleLinkException(retVal); /* TODO: determine the best exception, based on retVal */
                }
            }
        }

        public static int send(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms)
        {
            if (!_isInitialized) Initialize();

            /* TODO: enable flags */
            if (flags != 0)
                throw new ArgumentException("flags");
            /* TODO: enable send timeout; for CC3100 we may need to do this in software */
            //if (timeout_ms != System.Threading.Timeout.Infinite)
            //    throw new ArgumentOutOfRangeException("timeout_ms");

            Int32 retVal = _cc3100.sl_Send(handle, buf, offset, count, 0);
            if (retVal < 0)
            {
                throw new CC3100SimpleLinkException(retVal); /* TODO: determine the best exception, based on retVal */
            }

            return retVal; // positive value indicates # of bytes sent
        }

        public static int recv(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms)
        {
            if (!_isInitialized) Initialize();
            
            /* TODO: enable flags */
            if (flags != 0)
                throw new ArgumentException("flags");
            /* TODO: enable receive timeout */
            //if (timeout_ms != System.Threading.Timeout.Infinite)
            //    throw new ArgumentOutOfRangeException("timeout_ms");

            /* if data is buffered locally, use the local buffer; if not, then read from the CC3100's buffers */
            CC3100.CC3100SocketInfo socketInfo = _cc3100.GetSocketInfo(handle);
            lock (socketInfo.SocketReceiveBufferLockObject)
            {
                if (socketInfo.SocketReceiveBuffer != null && socketInfo.SocketReceiveBufferFirstAvailableIndex > 0)
                {
                    Int32 bytesRead = System.Math.Min(count, socketInfo.SocketReceiveBufferFirstAvailableIndex);
                    Array.Copy(socketInfo.SocketReceiveBuffer, 0, buf, offset, bytesRead);
                    Array.Copy(socketInfo.SocketReceiveBuffer, bytesRead, 
                        socketInfo.SocketReceiveBuffer, 0, socketInfo.SocketReceiveBufferFirstAvailableIndex - bytesRead);
                    socketInfo.SocketReceiveBufferFirstAvailableIndex -= bytesRead;
                    return bytesRead;
                }
            }

            /* if no data was buffered locally, read from the CC3100's buffers */
            Int32 retVal = _cc3100.sl_Recv(handle, buf, offset, count, 0);
            if (retVal < 0)
            {
                throw new CC3100SimpleLinkException(retVal); /* TODO: determine the best exception, based on retVal */
            }

            return retVal; // positive value indicates # of bytes received
        }

        public static int close(int handle)
        {
            if (!_isInitialized) Initialize();

            Int32 retVal = _cc3100.sl_Close(handle);
            if (retVal < 0)
                if (retVal < 0)
                {
                    throw new CC3100SimpleLinkException(retVal); /* TODO: determine the best exception, based on retVal */
                }

            /* NOTE: the return value of close() is not used by NETMF as of March 2015 */
            return 0; /* TODO: determine what to return for "success" */
        }

        public static void listen(int handle, int backlog)
        {
            if (!_isInitialized) Initialize();

            Int32 retVal = _cc3100.sl_Listen(handle, (Int16)backlog);
            if (retVal < 0)
            {
                throw new CC3100SimpleLinkException(retVal); /* TODO: determine the best exception, based on retVal */
            }
        }

        public static int accept(int handle)
        {
            if (!_isInitialized) Initialize();

            UInt32 ipAddress;
            UInt16 ipPort;
            Int32 retVal = _cc3100.sl_Accept(handle, Netduino.IP.LinkLayers.CC3100.SocketAddressFamily.IPv4, out ipAddress, out ipPort);
            if (retVal < 0)
            {
                throw new CC3100SimpleLinkException(retVal); /* TODO: determine the best exception, based on retVal */
            }

            return retVal; // this is our accepted connection's socketHandle
        }


        // NOTE: this overload is for compatibility with NETMF reflection
        public static object[] getaddrinfo_reflection(string name)
        {
            string canonicalName;
            byte[][] addresses;
            getaddrinfo(name, out canonicalName, out addresses);

            return new object[] {canonicalName, addresses};
        }

        //No standard non-blocking api
        public static void getaddrinfo(string name, out string canonicalName, out byte[][] addresses)
        {
             if (!_isInitialized) Initialize();

            UInt32 ipAddress;
            Int32 retVal = _cc3100.sl_NetAppDnsGetHostByName(name, Netduino.IP.LinkLayers.CC3100.SocketAddressFamily.IPv4, out ipAddress);
            if (retVal < 0)
            {
                throw new CC3100SimpleLinkException(retVal); /* TODO: determine the best exception, based on retVal */
            }

            addresses = new byte[][] { new byte[8] };
            if (SystemInfo.IsBigEndian)
            {
                addresses[0][0] = 0x00;  /* InterNetwork = 0x0002 */
                addresses[0][1] = 0x02;  /* InterNetwork = 0x0002 */
            }
            else
            {
                addresses[0][0] = 0x02;  /* InterNetwork = 0x0002 */
                addresses[0][1] = 0x00;  /* InterNetwork = 0x0002 */
            }
            // skip port address [elements 2-3]
            addresses[0][4] = (byte)((ipAddress >> 24) & 0xFF);
            addresses[0][5] = (byte)((ipAddress >> 16) & 0xFF);
            addresses[0][6] = (byte)((ipAddress >> 8) & 0xFF);
            addresses[0][7] = (byte)(ipAddress & 0xFF);

            /* CC3100 driver does not return canonical names, so use the provided hostname */
            canonicalName = name; 
        }

        public static object[] shutdown_reflection(int handle, int how)
        {
            int err;
            shutdown(handle, how, out err);
            return new object[] { err };
        }

        public static void shutdown(int handle, int how, out int err)
        {
            //if (!_isInitialized) Initialize();

            throw new NotImplementedException();

            // NOTE: shutdown does not appear to actually be implemented by NETMF.
            /* TODO: determine if the .Disconnect function of NETMF should really be calling shutdown (flush data and disconnect socket) instead */
            //_ipv4.GetSocket(handle).Close(how, out err);
            err = 0;

            // legend for 'how':
            // how == 0: stop receiving data from the socket; any further data from the remote endpoint will be dropped
            // how == 1: stop sending data to the socket (and dispose of any queued data); stream connections should not watch for ACKs of already-sent datagrams
            // how == 2: stop both sending and receivnig data

            // legend for ERR: (assumed from BSD implementation):
            // err == 0: success
            // err == -1: failure
        }

        public static int sendto(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms, byte[] address)
        {
            if (!_isInitialized) Initialize();

            /* TODO: enable flags */
            if (flags != 0)
                throw new ArgumentException("flags");
            /* TODO: enable send timeout */
            if (timeout_ms != System.Threading.Timeout.Infinite)
                throw new ArgumentOutOfRangeException("timeout_ms");

            UInt16 ipPort = (UInt16)(((UInt16)address[2] << 8) +
                (UInt16)address[3]);
            UInt32 ipAddress = ((UInt32)address[4] << 24) +
                ((UInt32)address[5] << 16) +
                ((UInt32)address[6] << 8) +
                (UInt32)address[7];

            Int32 retVal = _cc3100.sl_SendTo(handle, buf, offset, count, 0, Netduino.IP.LinkLayers.CC3100.SocketAddressFamily.IPv4, ipAddress, ipPort);
            if (retVal < 0)
            {
                throw new CC3100SimpleLinkException(retVal); /* TODO: determine the best exception, based on retVal */
            }

            return retVal; // positive value indicates # of bytes sent
        }

        public static object[] recvfrom_reflection(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms, byte[] address)
        {
            int returnValue = recvfrom(handle, buf, offset, count, flags, timeout_ms, ref address);
            return new object[] { returnValue, address };
        }

        public static int recvfrom(int handle, byte[] buf, int offset, int count, int flags, int timeout_ms, ref byte[] address)
        {
            if (!_isInitialized) Initialize();

            UInt32 ipAddress;
            UInt16 ipPort;

            if (address == null || address.Length < 8)
                throw new ArgumentException("address");

            /* TODO: enable flags */
            if (flags != 0)
                throw new ArgumentException("flags");
            /* TODO: enable send timeout */
            if (timeout_ms != System.Threading.Timeout.Infinite)
                throw new ArgumentOutOfRangeException("timeout_ms");

            Int32 retVal = _cc3100.sl_RecvFrom(handle, buf, offset, count, 0, Netduino.IP.LinkLayers.CC3100.SocketAddressFamily.IPv4, out ipAddress, out ipPort);
            if (retVal < 0)
            {
                throw new CC3100SimpleLinkException(retVal); /* TODO: determine the best exception, based on retVal */
            }

            address[2] = (byte)((ipPort >> 8) & 0xFF);
            address[3] = (byte)(ipPort & 0xFF);
            address[4] = (byte)((ipAddress >> 24) & 0xFF);
            address[5] = (byte)((ipAddress >> 16) & 0xFF);
            address[6] = (byte)((ipAddress >> 8) & 0xFF);
            address[7] = (byte)(ipAddress & 0xFF);

            return retVal; // positive value indicates # of bytes received
        }

        public static object[] getpeername_reflection(int handle)
        {
            byte[] address;
            getpeername(handle, out address);
            return new object[] { address };
        }

        public static void getpeername(int handle, out byte[] address)
        {
            if (!_isInitialized) Initialize();

            Netduino.IP.LinkLayers.CC3100.CC3100SocketInfo socketInfo = _cc3100.GetSocketInfo(handle);
            UInt32 ipAddress = socketInfo.RemoteIPAddress;
            UInt16 ipPort = socketInfo.RemoteIPPort;

            address = new byte[8];
            if (SystemInfo.IsBigEndian)
            {
                address[0] = 0x00;  /* InterNetwork = 0x0002 */
                address[1] = 0x02;  /* InterNetwork = 0x0002 */
            }
            else
            {
                address[0] = 0x02;  /* InterNetwork = 0x0002 */
                address[1] = 0x00;  /* InterNetwork = 0x0002 */
            }
            address[2] = (byte)((ipPort >> 8) & 0xFF);
            address[3] = (byte)(ipPort & 0xFF);
            address[4] = (byte)((ipAddress >> 24) & 0xFF);
            address[5] = (byte)((ipAddress >> 16) & 0xFF);
            address[6] = (byte)((ipAddress >> 8) & 0xFF);
            address[7] = (byte)(ipAddress & 0xFF);
        }

        public static object[] getsockname_reflection(int handle)
        {
            byte[] address;
            getsockname(handle, out address);
            return new object[] { address };
        }

        public static void getsockname(int handle, out byte[] address)
        {
            if (!_isInitialized) Initialize();

            UInt32 ipAddress, subnetMask, gatewayAddress, dnsAddress;
            _cc3100.GetIpv4ConfigurationBE(out ipAddress, out subnetMask, out gatewayAddress, out dnsAddress);
            //UInt16 ipPort = _ipv4.GetSocket(handle).SourceIPPort;
            UInt16 ipPort = 0;

            address = new byte[8];
            address[2] = (byte)((ipPort >> 8) & 0xFF);
            address[3] = (byte)(ipPort & 0xFF);
            address[4] = (byte)((ipAddress >> 24) & 0xFF);
            address[5] = (byte)((ipAddress >> 16) & 0xFF);
            address[6] = (byte)((ipAddress >> 8) & 0xFF);
            address[7] = (byte)(ipAddress & 0xFF);

            throw new NotImplementedException();
        }

        public static void getsockopt(int handle, int level, int optname, byte[] optval)
        {
            if (!_isInitialized) Initialize();

            switch (level)
            {
                case 0xffff: /* SocketOptionLevel.Socket */
                    {
                        /* filter for CC3100-specific optnames */
                        switch (optname)
                        {
                            case 0x001008: /* SocketOptionName.Type */
                                {
                                    Netduino.IP.LinkLayers.CC3100.CC3100SocketInfo socketInfo = _cc3100.GetSocketInfo(handle);
                                    optval[0] = (byte)(((Int32)socketInfo.SocketSocketType) & 0xFF);
                                    optval[1] = (byte)((((Int32)socketInfo.SocketSocketType) >> 8) & 0xFF);
                                    optval[2] = (byte)((((Int32)socketInfo.SocketSocketType) >> 16) & 0xFF);
                                    optval[3] = (byte)((((Int32)socketInfo.SocketSocketType) >> 24) & 0xFF);
                                }
                                break;
                            case 0x400000: /* map to SL_SO_SECMETHOD */
                                {
                                    int retVal = _cc3100.sl_GetSockOpt(handle, 1 /* SL_SOL_SOCKET */, 25 /* SL_SO_SECMETHOD */, optval);
                                    if (retVal < 0)
                                    {
                                        throw new CC3100SimpleLinkException(retVal); /* TODO: what is the correct exception? */
                                    }
                                }
                                break;
                            case 0x400002: /* trigger to upgrade to SSL/TLS (by closing an open socket and re-opening it in secure mode using the pre-specified security method/mask */
                                {
                                    // bytes 0-7: ipaddress
                                    // byte 8: sslProtocolOption
                                    // byte 9: where we return the new handle (or 0xFF on error)

                                    // close the socket
                                    _cc3100.sl_Close(handle);

                                    // re-open the socket
                                    int newHandle = 0xFF; // default to "invalid handle"
                                    int retVal = _cc3100.sl_Socket(Netduino.IP.LinkLayers.CC3100.SocketAddressFamily.IPv4, Netduino.IP.LinkLayers.CC3100.SocketSocketType.Stream, Netduino.IP.LinkLayers.CC3100.SocketProtocolType.Secure);
                                    if (retVal >= 0)
                                    {
                                        newHandle = retVal;
                                        // set SSL security protocol option(s)
                                        setsockopt(handle, 0xffff /* SocketOptionLevel.Socket */, 0x400000 /* map to SL_SO_SECMETHOD */, new byte[] { optval[8] });
                                        // parse ipAddress
                                        UInt16 ipPort = (UInt16)(((UInt16)optval[2] << 8) +
                                            (UInt16)optval[3]);
                                        UInt32 ipAddress = ((UInt32)optval[4] << 24) +
                                            ((UInt32)optval[5] << 16) +
                                            ((UInt32)optval[6] << 8) +
                                            (UInt32)optval[7];
                                        // re-open socket
                                        retVal = _cc3100.sl_Connect(newHandle, Netduino.IP.LinkLayers.CC3100.SocketAddressFamily.IPv4, ipAddress, ipPort);
                                        if (retVal >= 0 || retVal == -453 /* SL_ESECSNOVERIFY */)
                                        {
                                            optval[9] = (byte)newHandle;
                                        }
                                        else
                                        {
                                            //Debug.Print("SSL Socket Upgrade Failure; Error # " + retVal);
                                        }
                                    }
                                }
                                break;
                            default:
                                throw new NotSupportedException();
                        }
                    }
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        public static void setsockopt(int handle, int level, int optname, byte[] optval)
        {
            if (!_isInitialized) Initialize();

            switch (level)
            {
                case 0xffff: /* SocketOptionLevel.Socket */
                    {
                        /* filter for CC3100-specific optnames */
                        switch (optname)
                        {
                            case 0x1006: /* ReceiveTimeout */ /* map to SL_SO_RCVTIMEO */
                                {
                                    int retVal = _cc3100.sl_SetSockOpt(handle, 1 /* SL_SOL_SOCKET */, 20 /* SL_SO_RCVTIMEO */, optval);
                                    if (retVal < 0)
                                    {
                                        throw new CC3100SimpleLinkException(retVal); /* TODO: what is the correct exception? */
                                    }
                                }
                                break;
                            case 0x400000: /* map to SL_SO_SECMETHOD */
                                {
                                    int retVal = _cc3100.sl_SetSockOpt(handle, 1 /* SL_SOL_SOCKET */, 25 /* SL_SO_SECMETHOD */, optval);
                                    if (retVal < 0)
                                    {
                                        throw new CC3100SimpleLinkException(retVal); /* TODO: what is the correct exception? */
                                    }
                                }
                                break;
                            default:
                                break;
                        }
                    }
                    break;
                case 0x0000: /* IP */
                    {
                        switch (optname)
                        {
                            case 10: /* MulticastTimeToLive */
                                {
                                    byte ttl;
                                    if (SystemInfo.IsBigEndian)
                                        ttl = optval[3];
                                    else
                                        ttl = optval[0];
                                    int retVal = _cc3100.sl_SetSockOpt(handle, 2 /* SL_IPPROTO_IP */, 61 /* SL_IP_MULTICAST_TTL */, new byte[] { ttl });
                                    if (retVal < 0)
                                    {
                                        throw new CC3100SimpleLinkException(retVal); /* TODO: what is the correct exception? */
                                    }
                                }
                                break;
                            case 12: /* AddMembership */
                                {
                                    /* TODO: implement Socket.SetSocketOption extension method that can pass in an object.  Implement the MulticastOption class.  And then detect that class here and parse out its 
                                     *       multicastAddress and localAddress */
                                    /* TODO: make sure that the addresses are being received in the correct LE vs BE order */
                                    //byte[] multicastOptVal = new byte[8];
                                    //Array.Copy(multicastOption.Group.GetAddressBytes(), 0, multicastOptVal, 0, sizeof(UInt32));
                                    //Array.Copy(multicastOption.LocalAddress.GetAddressBytes(), 0, multicastOptVal, 4, sizeof(UInt32));
                                    int retVal = _cc3100.sl_SetSockOpt(handle, 2 /* SL_IPPROTO_IP */, 65 /* SL_IP_ADD_MEMBERSHIP */, optval);
                                    if (retVal < 0)
                                    {
                                        throw new CC3100SimpleLinkException(retVal); /* TODO: what is the correct exception? */
                                    }
                                }
                                break;
                            case 13: /* DropMembership */
                                {
                                    /* TODO: implement Socket.SetSocketOption extension method that can pass in an object.  Implement the MulticastOption class.  And then detect that class here and parse out its 
                                     *       multicastAddress and localAddress */
                                    /* TODO: make sure that the addresses are being received in the correct LE vs BE order */
                                    //byte[] multicastOptVal = new byte[8];
                                    //Array.Copy(multicastOption.Group.GetAddressBytes(), 0, multicastOptVal, 0, sizeof(UInt32));
                                    //Array.Copy(multicastOption.LocalAddress.GetAddressBytes(), 0, multicastOptVal, 4, sizeof(UInt32));
                                    int retVal = _cc3100.sl_SetSockOpt(handle, 2 /* SL_IPPROTO_IP */, 66 /* SL_IP_DROP_MEMBERSHIP */, optval);
                                    if (retVal < 0)
                                    {
                                        throw new CC3100SimpleLinkException(retVal); /* TODO: what is the correct exception? */
                                    }
                                }
                                break;
                            default:
                                break;
                        }
                    }
                    break;
                case 0x0006: /* TCP */
                    {
                        switch (optname)
                        {
                            case 1: /* NoDelay */
                                break;
                            default:
                                break;
                        }
                    }
                    break;
                case 0x0011: /* UDP */
                    break;
                default:
                    break;
            }
        }

        public static bool poll(int handle, int mode, int microSeconds)
        {
            if (!_isInitialized) Initialize();

            Int32[] socketHandles = new Int32[] { handle };
            Int32[] unusedSocketHandlesArray = new Int32[] {};
            Int32 retVal = 0;
            switch (mode)
            {
                case 0: /* SelectRead */
                    retVal = _cc3100.sl_Select(ref socketHandles, ref unusedSocketHandlesArray, microSeconds);
                    break;
                case 1: /* SelectWrite */
                    retVal = _cc3100.sl_Select(ref unusedSocketHandlesArray, ref socketHandles, microSeconds);
                    break;
                case 2: /* SelectError */
                default:
                    return false; // not supported
            }
            if (retVal < 0)
            {
                /* TODO: determine how to prevent the SL_INEXE error...or better handle it. */
                if (retVal == -8 /* SL_INEXE; socket command in execution */)
                {
                    return false;
                }
                else
                {
                    throw new CC3100SimpleLinkException(retVal); /* TODO: determine the best exception, based on retVal */
                }
            }

            return (retVal > 0 ? true : false);
        }

        public static object[] ioctl_reflection(int handle, uint cmd, uint arg)
        {
            ioctl(handle, cmd, ref arg);
            return new object[] { arg };
        }

        public static void ioctl(int handle, uint cmd, ref uint arg)
        {
            if (!_isInitialized) Initialize();

            switch (cmd)
            {
                case FIONREAD:
                    {
                        UInt32 bytesToRead = 0;
                        CC3100.CC3100SocketInfo socketInfo = _cc3100.GetSocketInfo(handle);

                        if (poll(handle, 0 /* SelectRead */, 0))
                        {
                            /* if data is available, read it into a buffer in the background and return the actual # of available bytes to read */
                            lock (socketInfo.SocketReceiveBufferLockObject)
                            {
                                Int32 RX_BUFFER_SIZE = 1500;
                                if (socketInfo.SocketReceiveBuffer == null)
                                {
                                    socketInfo.SocketReceiveBuffer = new byte[RX_BUFFER_SIZE]; // maximum rx buffer size
                                }
                                Int32 bytesRead = _cc3100.sl_Recv(handle, socketInfo.SocketReceiveBuffer, socketInfo.SocketReceiveBufferFirstAvailableIndex,
                                    RX_BUFFER_SIZE - socketInfo.SocketReceiveBufferFirstAvailableIndex, 0);
                                if (bytesRead >= 0)
                                {
                                    socketInfo.SocketReceiveBufferFirstAvailableIndex += bytesRead;
                                }
                            }
                        }

                        // whether or not we retrieved more data into the cached managed-code buffer, return the number of bytes now available.
                        lock (socketInfo.SocketReceiveBufferLockObject)
                        {
                            bytesToRead = (UInt32)socketInfo.SocketReceiveBufferFirstAvailableIndex;
                        }
                        arg = bytesToRead;
                    }
                    break;
                default:
                    {
                        throw new NotImplementedException();
                    }
            }
        }

        private static void UpgradeFirmware()
        {
            if (!_isInitialized) Initialize();
            _cc3100.UpgradeFirmware();
        }
    }
}
