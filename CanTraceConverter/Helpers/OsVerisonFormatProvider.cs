using System;

namespace CanTraceConverter.Helpers
{
    /// <summary>
    /// OsVerisonFormatProvider is a FormatProvider class for converting the OSVERSIONINFO to a string.
    /// </summary>
    public class OsVerisonFormatProvider : IFormatProvider, ICustomFormatter
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
            OSVERSIONINFO OsVersion;
            string str = "N/A";

            if (argToBeFormatted != null)
            {
                try
                {
                    if (argToBeFormatted is OSVERSIONINFO)
                    {
                        OsVersion = (OSVERSIONINFO)argToBeFormatted;

                        str = String.Format("V{0}.{1}.{2} Platform {3} '{4}'",
                            OsVersion.dwMajorVersion, OsVersion.dwMinorVersion, OsVersion.dwBuildNumber,
                            OsVersion.dwPlatformId, OsVersion.strCSDVersion);
                    }
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(String.Format(
                        "The argument \"{0}\" cannot be " +
                        "converted to OSVERSIONINFO class.",
                        argToBeFormatted), ex);
                }
            }

            return str;
        }

        #endregion
    }
}