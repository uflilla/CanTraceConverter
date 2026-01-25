using System;

namespace CanTraceConverter.Helpers
{
    /// <summary>
    /// UcanMsgFormatProvider is a FormatProvider class for converting the tCanMsgStruct structure to a string.
    /// </summary>
    public class UcanMsgFormatProvider : IFormatProvider, ICustomFormatter
    {
        #region Own Member
        private UcanDotNET.USBcanServer.tUcanHardwareInfoEx m_UcanHwInfo;
        private UcanDotNET.USBcanServer.tUcanChannelInfo m_CanChannelInfo;

        /// <summary>
        /// Constructor including the tUcanHardwareInfoEx and tUcanChannelInfo structures as parameter.
        /// </summary>
        /// <param name="UcanHwInfo_p">Structure holding the Hardware Information of an USB-CANmodul.</param>
        /// <param name="CanChannelInfo_p">Structure holding the CAN Information of a CAN channel of an USB-CANmodul.</param>
        public UcanMsgFormatProvider(UcanDotNET.USBcanServer.tUcanHardwareInfoEx UcanHwInfo_p,
            UcanDotNET.USBcanServer.tUcanChannelInfo CanChannelInfo_p)
            : base()
        {
            m_UcanHwInfo = UcanHwInfo_p;
            m_CanChannelInfo = CanChannelInfo_p;
        }

        /// <summary>
        /// Returns a string with the Message Table Header.
        /// </summary>
        /// <returns>
        /// string - Message Table Header.
        /// </returns>
        public static string GetMessageHeader()
        {
            string strHeader;

            strHeader = "===============================================================================\n";
            strHeader += "DIR TIME(msec) ID------- DLC DATA-------------------\n";
            //            RX: 70417421.1      321h (8) FE DC BA 98 76 54 32 10
            strHeader += "===============================================================================";

            return strHeader;
        }
        #endregion
        #region IFormatProvider Member

        /// <summary>
        /// Ruft ein Objekt ab, das Formatierungsdienste für den angegebenen Typ bereitstellt.
        /// </summary>
        /// <param name="formatType">Ein Objekt, das den Typ des abzurufenden Formatierungsobjekts angibt.</param>
        /// <returns>
        /// Die aktuelle Instanz, wenn formatType mit dem Typ der aktuellen Instanz übereinstimmt, andernfalls null.
        /// </returns>
        public object GetFormat(Type formatType)
        {
            if (typeof(ICustomFormatter).Equals(formatType))
            {
                return this;
            }
            else
            {
                return null;
            }
        }

        #endregion
        #region ICustomFormatter Member

        /// <summary>
        /// Formats the specified argument.
        /// </summary>
        /// <param name="format">The format.</param>
        /// <param name="argToBeFormatted">The arg to be formatted.</param>
        /// <param name="formatProvider">The format provider.</param>
        /// <returns></returns>
        public string Format(string format, object argToBeFormatted, IFormatProvider formatProvider)
        {
            UcanDotNET.USBcanServer.tCanMsgStruct CanMsg;
            string str = "N/A";

            if (argToBeFormatted != null)
            {
                try
                {
                    if (argToBeFormatted is UcanDotNET.USBcanServer.tCanMsgStruct)
                    {
                        CanMsg = (UcanDotNET.USBcanServer.tCanMsgStruct)argToBeFormatted;

                        str  = ((CanMsg.m_bFF & (byte)UcanDotNET.USBcanServer.eUcanMsgFrameFormat.USBCAN_MSG_FF_ECHO) != 0) ? "TX: " : "RX: ";
                        if (UcanDotNET.USBcanServer.CheckIs_G4(this.m_UcanHwInfo) &&
                            ((this.m_CanChannelInfo.m_bMode & (byte)UcanDotNET.USBcanServer.tUcanMode.kUcanModeHighResTimer) != 0))
                        {
                            str += string.Format("{0:D8}.{1:D1} ", CanMsg.m_dwTime / 10, CanMsg.m_dwTime % 10);
                        }
                        else
                        {
                            str += string.Format("{0:D9}  ", CanMsg.m_dwTime);
                        }
                        str += ((CanMsg.m_bFF & (byte)UcanDotNET.USBcanServer.eUcanMsgFrameFormat.USBCAN_MSG_FF_EXT) != 0) ?
                            string.Format ("{0:X8}h ", CanMsg.m_dwID) : string.Format ("     {0:X3}h ", CanMsg.m_dwID);
                        str += string.Format ("({0}) ", CanMsg.m_bDLC);
                        if ((CanMsg.m_bFF & (byte)UcanDotNET.USBcanServer.eUcanMsgFrameFormat.USBCAN_MSG_FF_RTR) != 0)
                        {
                            str += "Remote Request";
                        }
                        else
                        {
                            for (int i = 0; i < 8; i++)
                            {
                                if (i < CanMsg.m_bDLC)
                                {
                                    str += string.Format("{0:X2} ", CanMsg.m_bData[i]);
                                }
                                else
                                {
                                    str += "-- ";
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(String.Format(
                        "The argument \"{0}\" cannot be " +
                        "converted to UcanDotNET.USBcanServer.tCanMsgStruct class.",
                        argToBeFormatted), ex);
                }
            }

            return str;
        }

        #endregion
    }
}