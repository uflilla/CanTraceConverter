using System;

namespace CanTraceConverter.Helpers
{
    /// <summary>
    /// SystemTimeFormatProvider is a FormatProvider class for converting the SYSTEMTIME to a string.
    /// </summary>
    public class SystemTimeFormatProvider : IFormatProvider, ICustomFormatter
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
            SYSTEMTIME SystemTime;
            string str = "N/A";

            if (argToBeFormatted != null)
            {
                try
                {
                    if (argToBeFormatted is SYSTEMTIME)
                    {
                        SystemTime = (SYSTEMTIME)argToBeFormatted;

                        str = String.Format("{0:D2}.{1:D2}.{2:D4}/{3:D2}:{4:D2}:{5:D2}",
                            SystemTime.wDay, SystemTime.wMonth, SystemTime.wYear,
                            SystemTime.wHour, SystemTime.wMinute, SystemTime.wSecond);
                    }
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(String.Format(
                        "The argument \"{0}\" cannot be " +
                        "converted to SYSTEMTIME class.",
                        argToBeFormatted), ex);
                }
            }

            return str;
        }

        #endregion
    }
}