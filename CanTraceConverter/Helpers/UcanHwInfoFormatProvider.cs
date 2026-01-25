using System;

namespace CanTraceConverter.Helpers
{
    /// <summary>
    /// UcanHwInfoFormatProvider is a FormatProvider class for converting the tUcanHardwareInfoEx structure to a string.
    /// </summary>
    public class UcanHwInfoFormatProvider : IFormatProvider, ICustomFormatter
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
            UcanDotNET.USBcanServer.tUcanHardwareInfoEx UcanHwInfo;
            string str = "N/A";

            if (argToBeFormatted != null)
            {
                try
                {
                    if (argToBeFormatted is UcanDotNET.USBcanServer.tUcanHardwareInfoEx)
                    {
                        UcanHwInfo = (UcanDotNET.USBcanServer.tUcanHardwareInfoEx)argToBeFormatted;

                        str = String.Format("ProductID {0:X8}h SerialNr {1} DeviceNr {2} Firmware {3}",
                            UcanHwInfo.m_dwProductCode, UcanHwInfo.m_dwSerialNr, UcanHwInfo.m_bDeviceNr,
                            String.Format(new VersionFormatProvider(), "{0}", UcanHwInfo.m_dwFwVersionEx));
                    }
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(String.Format(
                        "The argument \"{0}\" cannot be " +
                        "converted to UcanDotNET.USBcanServer.tUcanHardwareInfoEx class.",
                        argToBeFormatted), ex);
                }
            }

            return str;
        }

        #endregion
    }
}