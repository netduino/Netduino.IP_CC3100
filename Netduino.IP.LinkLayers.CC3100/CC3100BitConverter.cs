using System;
using Microsoft.SPOT;

namespace Netduino.IP.LinkLayers
{
    static public class CC3100BitConverter
    {
        /* TODO: review this function for efficiency */
        static public Int32 ToInt32(byte[] value, int startIndex)
        {
            UInt32 tempValue = (UInt32)(
                value[startIndex]
                + (((UInt32)value[startIndex + 1]) << 8)
                + (((UInt32)value[startIndex + 2]) << 16)
                + (((UInt32)value[startIndex + 3]) << 24)
                );

            if ((tempValue & 0x80000000) == 0)
            {
                // this value is positive
                return (Int32)tempValue;
            }
            else
            {
                // this value is negative
                return (Int32)(-1 * (1 + (Int32)(~tempValue)));
            }
        }

        /* TODO: review this function for efficiency */
        static public Int16 ToInt16(byte[] value, int startIndex)
        {
            UInt16 tempValue = (UInt16)(
                value[startIndex]
                + (((UInt16)value[startIndex + 1]) << 8)
                );

            if ((tempValue & 0x8000) == 0)
            {
                // this value is positive
                return (Int16)tempValue;
            }
            else
            {
                // this value is negative
                return (Int16)(-1 * (1 + (Int16)(~tempValue)));
            }
        }

        static public UInt32 ToUInt32(byte[] value, int startIndex)
        {
            return (UInt32)(
                value[startIndex]
                + (((UInt32)value[startIndex + 1]) << 8)
                + (((UInt32)value[startIndex + 2]) << 16)
                + (((UInt32)value[startIndex + 3]) << 24)
                );
        }

        static public UInt16 ToUInt16(byte[] value, int startIndex)
        {
            return (UInt16)(
                value[startIndex]
                + (((UInt16)value[startIndex + 1]) << 8)
                );
        }

        static public byte[] GetBytes(UInt16 value)
        {
            return new byte[2] {
                (byte)(value & 0xFF),
                (byte)((value >> 8) & 0xFF)
                };
        }

        static public byte[] GetBytes(UInt32 value)
        {
            return new byte[4] {
                (byte)(value & 0xFF),
                (byte)((value >> 8) & 0xFF),
                (byte)((value >> 16) & 0xFF),
                (byte)((value >> 24) & 0xFF)
                };
        }
    }
}
