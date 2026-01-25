using System;
using System.IO;

namespace CanTraceConverter.Helpers
{
    /// <summary>
    /// UcanTraceBinaryReader is a BinaryReader class with additional methods for
    /// reading elements from the binary CAN Trace File.
    /// </summary>
    public class UcanTraceBinaryReader : BinaryReader
    {
        /// <summary>
        /// New constructor for the binary reader.
        /// </summary>
        public UcanTraceBinaryReader(Stream input)
            : base(input)
        {
        }

        /// <summary>
        /// Reads the System Time from the binary file.
        /// </summary>
        /// <returns>
        /// Read SYSTEMTIME instance.
        /// </returns>
        public SYSTEMTIME ReadSystemTime()
        {
            SYSTEMTIME SystemTime = new SYSTEMTIME();

            // read all elements of the SYSTEMTIME structure
            SystemTime.wYear = ReadUInt16();
            SystemTime.wMonth = ReadUInt16();
            SystemTime.wDayOfWeek = ReadUInt16();
            SystemTime.wDay = ReadUInt16();
            SystemTime.wHour = ReadUInt16();
            SystemTime.wMinute = ReadUInt16();
            SystemTime.wSecond = ReadUInt16();
            SystemTime.wMilliseconds = ReadUInt16();

            return SystemTime;
        }

        /// <summary>
        /// Reads the OS Version Info from the binary file.
        /// </summary>
        /// <returns>
        /// Read OSVERSIONINFO instance.
        /// </returns>
        public OSVERSIONINFO ReadOsVersion()
        {
            OSVERSIONINFO OsVersion = new OSVERSIONINFO();

            // read all elements of the OSVERSIONINFO structure
            OsVersion.dwOSVersionInfoSize = ReadUInt32();
            OsVersion.dwMajorVersion = ReadUInt32();
            OsVersion.dwMinorVersion = ReadUInt32();
            OsVersion.dwBuildNumber = ReadUInt32();
            OsVersion.dwPlatformId = ReadUInt32();
            OsVersion.strCSDVersion = "";

            // read the CSD version string but do only add the characterts until the first zero
            bool fZeroFound = false;
            for (int i = 0; i < (OsVersion.dwOSVersionInfoSize - (4 * 5)); i++)
            {
                // read all the characters including the zeros to make sure the
                // file possition is correct after returning from this method.
                char Char = ReadChar();

                // check if the zero was not found any more
                if (fZeroFound == false)
                {
                    if (Char != 0)
                    {
                        OsVersion.strCSDVersion += Char;
                    }
                    else
                    {
                        fZeroFound = true;
                    }
                }
            }

            return OsVersion;
        }

        /// <summary>
        /// Reads the USBCAN Hardware Info from the binary file.
        /// </summary>
        /// <returns>
        /// Read tUcanHardwareInfoEx instance.
        /// </returns>
        public UcanDotNET.USBcanServer.tUcanHardwareInfoEx ReadUcanHwInfo()
        {
            UcanDotNET.USBcanServer.tUcanHardwareInfoEx UcanHwInfo = new UcanDotNET.USBcanServer.tUcanHardwareInfoEx();

            // read all elements of the tUcanHardwareInfoEx structure
            UcanHwInfo.m_dwSize = ReadInt32();
            UcanHwInfo.m_UcanHandle = ReadByte();
            UcanHwInfo.m_bDeviceNr = ReadByte();
            UcanHwInfo.m_dwSerialNr = ReadInt32();
            UcanHwInfo.m_dwFwVersionEx = ReadInt32();
            UcanHwInfo.m_dwProductCode = ReadInt32();

            // read the Unique-ID too if the size of the structure is big enough
            if (UcanHwInfo.m_dwSize > 0x22)
            {
                UcanHwInfo.m_dwUniqueId0 = ReadInt32();
                UcanHwInfo.m_dwUniqueId1 = ReadInt32();
                UcanHwInfo.m_dwUniqueId2 = ReadInt32();
                UcanHwInfo.m_dwUniqueId3 = ReadInt32();
                UcanHwInfo.m_dwFlags = ReadInt32();
            }

            return UcanHwInfo;
        }

        /// <summary>
        /// Reads the USBCAN Channel Info from the binary file.
        /// </summary>
        /// <returns>
        /// Read tUcanChannelInfo instance.
        /// </returns>
        public UcanDotNET.USBcanServer.tUcanChannelInfo ReadUcanChannelInfo()
        {
            UcanDotNET.USBcanServer.tUcanChannelInfo CanChannelInfo = new UcanDotNET.USBcanServer.tUcanChannelInfo();

            // read all elements of the tUcanChannelInfo structure
            CanChannelInfo.m_dwSize = ReadInt32();
            CanChannelInfo.m_bMode = ReadByte();
            CanChannelInfo.m_bBTR0 = ReadByte();
            CanChannelInfo.m_bBTR1 = ReadByte();
            CanChannelInfo.m_bOCR = ReadByte();
            CanChannelInfo.m_dwAMR = ReadInt32();
            CanChannelInfo.m_dwACR = ReadInt32();
            CanChannelInfo.m_dwBaudrate = ReadInt32();
            CanChannelInfo.m_fCanIsInit = Convert.ToBoolean (ReadInt32());
            CanChannelInfo.m_wCanStatus = ReadInt16();

            return CanChannelInfo;
        }

        /// <summary>
        /// Reads the CAN message from the binary file.
        /// </summary>
        /// <returns>
        /// Read tCanMsgStruct instance.
        /// </returns>
        public UcanDotNET.USBcanServer.tCanMsgStruct ReadUcanMessage()
        {
            UcanDotNET.USBcanServer.tCanMsgStruct CanMsg = UcanDotNET.USBcanServer.tCanMsgStruct.CreateInstance(0,0);

            // read all elements of the tCanMsgStruct structure
            CanMsg.m_dwID = ReadInt32();
            CanMsg.m_bFF = ReadByte();
            CanMsg.m_bDLC = ReadByte();
            CanMsg.m_bData[0] = ReadByte();
            CanMsg.m_bData[1] = ReadByte();
            CanMsg.m_bData[2] = ReadByte();
            CanMsg.m_bData[3] = ReadByte();
            CanMsg.m_bData[4] = ReadByte();
            CanMsg.m_bData[5] = ReadByte();
            CanMsg.m_bData[6] = ReadByte();
            CanMsg.m_bData[7] = ReadByte();
            CanMsg.m_dwTime = ReadInt32();

            return CanMsg;
        }
    }
}