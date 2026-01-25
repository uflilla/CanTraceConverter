using System;

namespace CanTraceConverter.Helpers
{
    /// <summary>
    /// UcanChannelInfoFormatProvider is a FormatProvider class for converting the tUcanChannelInfo structure to a string.
    /// </summary>
    public class UcanChannelInfoFormatProvider : IFormatProvider, ICustomFormatter
    {
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
            UcanDotNET.USBcanServer.tUcanChannelInfo CanChannelInfo;
            string str = "N/A";

            if (argToBeFormatted != null)
            {
                try
                {
                    if (argToBeFormatted is UcanDotNET.USBcanServer.tUcanChannelInfo)
                    {
                        CanChannelInfo = (UcanDotNET.USBcanServer.tUcanChannelInfo)argToBeFormatted;

                        if (CanChannelInfo.m_fCanIsInit)
                        {
                            str = String.Format("Mode {0:X2}h BTR0/1 {1:X2}{2:X2}h BdrEx {3:X8}h AMR/ACR {4:X8}h/{5:X8}h",
                                CanChannelInfo.m_bMode, CanChannelInfo.m_bBTR0, CanChannelInfo.m_bBTR1, CanChannelInfo.m_dwBaudrate,
                                CanChannelInfo.m_dwAMR, CanChannelInfo.m_dwACR);
                        }
                        else
                        {
                            str = "off";
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(String.Format(
                        "The argument \"{0}\" cannot be " +
                        "converted to UcanDotNET.USBcanServer.tUcanChannelInfo class.",
                        argToBeFormatted), ex);
                }
            }

            return str;
        }

        #endregion
    }
}