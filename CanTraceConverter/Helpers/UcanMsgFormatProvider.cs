using System;
using System.Globalization;
using System.Text;

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
                        bool highRes = UcanDotNET.USBcanServer.CheckIs_G4(this.m_UcanHwInfo) &&
                            ((this.m_CanChannelInfo.m_bMode & (byte)UcanDotNET.USBcanServer.tUcanMode.kUcanModeHighResTimer) != 0);

                        var sb = new StringBuilder(64);
                        AppendMessage(sb, CanMsg, highRes);
                        str = sb.ToString();
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

        private const string HexDigits = "0123456789ABCDEF";

        /// <summary>
        /// Appends one message line in the trace text format. A converted trace has millions of
        /// lines, so this avoids the per-field string.Format and concatenation allocations.
        /// </summary>
        /// <param name="highResTimer">Timestamps are 0.1 ms ticks of a G4 high-resolution timer.</param>
        public static void AppendMessage(StringBuilder sb, UcanDotNET.USBcanServer.tCanMsgStruct msg, bool highResTimer)
        {
            byte ff = msg.m_bFF;
            sb.Append((ff & (byte)UcanDotNET.USBcanServer.eUcanMsgFrameFormat.USBCAN_MSG_FF_ECHO) != 0 ? "TX: " : "RX: ");

            // The device counter is an unsigned 32-bit value that the wrapper types
            // as int; formatted signed it printed negative times past 2^31 ticks.
            uint time = unchecked((uint)msg.m_dwTime);
            if (highResTimer)
            {
                AppendDecimal(sb, time / 10, 8);
                sb.Append('.');
                sb.Append((char)('0' + time % 10));
                sb.Append(' ');
            }
            else
            {
                AppendDecimal(sb, time, 9);
                sb.Append("  ");
            }

            uint id = unchecked((uint)msg.m_dwID);
            if ((ff & (byte)UcanDotNET.USBcanServer.eUcanMsgFrameFormat.USBCAN_MSG_FF_EXT) != 0)
            {
                AppendHex(sb, id, 8);
            }
            else
            {
                sb.Append("     ");
                AppendHex(sb, id, 3);
            }
            sb.Append("h (");
            sb.Append(msg.m_bDLC);
            sb.Append(") ");

            if ((ff & (byte)UcanDotNET.USBcanServer.eUcanMsgFrameFormat.USBCAN_MSG_FF_RTR) != 0)
            {
                sb.Append("Remote Request");
                return;
            }

            for (int i = 0; i < 8; i++)
            {
                if (i < msg.m_bDLC)
                {
                    byte b = msg.m_bData[i];
                    sb.Append(HexDigits[b >> 4]).Append(HexDigits[b & 0xF]).Append(' ');
                }
                else
                {
                    sb.Append("-- ");
                }
            }
        }

        /// <summary>Zero-padded to at least <paramref name="minDigits"/>, like "D9".</summary>
        private static void AppendDecimal(StringBuilder sb, uint value, int minDigits)
        {
            var digits = value.ToString(CultureInfo.InvariantCulture);
            if (digits.Length < minDigits)
                sb.Append('0', minDigits - digits.Length);
            sb.Append(digits);
        }

        /// <summary>Upper-case hex, at least <paramref name="minDigits"/> wide, like "X8".</summary>
        private static void AppendHex(StringBuilder sb, uint value, int minDigits)
        {
            int digits = 1;
            for (uint v = value >> 4; v != 0; v >>= 4)
                digits++;
            if (digits < minDigits)
                sb.Append('0', minDigits - digits);
            for (int shift = (digits - 1) * 4; shift >= 0; shift -= 4)
                sb.Append(HexDigits[(int)((value >> shift) & 0xF)]);
        }
    }
}