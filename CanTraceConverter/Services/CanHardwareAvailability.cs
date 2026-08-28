using System;
using System.IO;
using UcanDotNET;

namespace CanTraceConverter.Services
{
    public static class CanHardwareAvailability
    {
        public static bool TryProbeDriver(out string message)
        {
            message = null;
            try
            {
                _ = new USBcanServer();
                return true;
            }
            catch (DllNotFoundException)
            {
                message = "USBCAN32.DLL was not found. Install the Systec USB-CAN device driver.";
                return false;
            }
            catch (BadImageFormatException)
            {
                message = "Driver/library architecture mismatch. Run the x86 build of this application.";
                return false;
            }
            catch (TypeInitializationException ex) when (ex.InnerException != null)
            {
                message = FormatInner(ex.InnerException);
                return false;
            }
            catch (Exception ex)
            {
                message = FormatInner(ex);
                return false;
            }
        }

        private static string FormatInner(Exception ex) =>
            ex is DllNotFoundException
                ? "USBCAN32.DLL was not found. Install the Systec USB-CAN device driver."
                : $"CAN hardware library unavailable: {ex.Message}";
    }
}
