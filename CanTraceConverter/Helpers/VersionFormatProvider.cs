using System;

namespace CanTraceConverter.Helpers
{
    /// <summary>
    /// VersionFormatProvider is a FormatProvider class for converting the verion code to a string.
    /// </summary>
    public class VersionFormatProvider : IFormatProvider, ICustomFormatter
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
            long lVersion = 0;
            int iMajor;
            int iMinor;
            int iRelease;
            string str;

            if (argToBeFormatted != null)
            {
                try
                {
                    if (argToBeFormatted is Int32)
                    {
                        lVersion = (long)(int)argToBeFormatted;
                    }
                    else if (argToBeFormatted is UInt32)
                    {
                        lVersion = (long)(uint)argToBeFormatted;
                    }
                    else if (argToBeFormatted is Int64)
                    {
                        lVersion = (long)argToBeFormatted;
                    }
                    else if (argToBeFormatted is UInt64)
                    {
                        lVersion = (long)(ulong)argToBeFormatted;
                    }
                }
                catch (Exception ex)
                {
                    throw new ArgumentException(String.Format(
                        "The argument \"{0}\" cannot be " +
                        "converted to an integer value.",
                        argToBeFormatted), ex);
                }
            }

            if ((lVersion != 0) && (lVersion != -1))
            {
                iMajor = (int)(lVersion & 0xFF);
                iMinor = (int)((lVersion & 0xFF00) >> 8);
                iRelease = (int)((lVersion & 0xFFFF0000) >> 16);

                str = String.Format("V{0}.{1:D2}r{2:D4}", iMajor, iMinor, iRelease);
            }
            else
            {
                str = "N/A";
            }
            return str;
        }

        #endregion
    }
}