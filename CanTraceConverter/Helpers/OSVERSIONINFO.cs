using System;

namespace CanTraceConverter.Helpers
{
    /// <summary>
    /// OSVERSIONINFO structure which is used with Win32 Function GetVersionEx().
    /// </summary>
    public struct OSVERSIONINFO
    {
        public UInt32 dwOSVersionInfoSize;
        public UInt32 dwMajorVersion;
        public UInt32 dwMinorVersion;
        public UInt32 dwBuildNumber;
        public UInt32 dwPlatformId;
        public string strCSDVersion;
    }
}