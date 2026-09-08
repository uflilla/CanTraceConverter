using System;
using UcanDotNET;

namespace CanTraceConverter.Services
{
    public static class CanHardwareAvailability
    {
        private static readonly object Sync = new object();
        private static bool _probed;
        private static bool _available;
        private static string _message;

        /// <summary>
        /// Checks that the native driver can be loaded. The result is cached: constructing a
        /// <see cref="USBcanServer"/> registers a process-wide plug-and-play callback on the
        /// first instance, so probing on every connect had global side effects.
        /// </summary>
        public static bool TryProbeDriver(out string message)
        {
            lock (Sync)
            {
                if (!_probed)
                {
                    _available = Probe(out _message);
                    _probed = true;
                }

                message = _message;
                return _available;
            }
        }

        /// <summary>Forces the next probe to re-test, e.g. after the user installs drivers.</summary>
        public static void Invalidate()
        {
            lock (Sync)
            {
                _probed = false;
            }
        }

        private static bool Probe(out string message)
        {
            message = null;
            USBcanServer server = null;
            try
            {
                server = new USBcanServer();
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
            finally
            {
                // Always dispose: the wrapper only releases its plug-and-play registration
                // and static handle entry from Dispose.
                if (server != null)
                {
                    try { server.Dispose(); } catch { /* ignore */ }
                }
            }
        }

        private static string FormatInner(Exception ex) =>
            ex is DllNotFoundException
                ? "USBCAN32.DLL was not found. Install the Systec USB-CAN device driver."
                : $"CAN hardware library unavailable: {ex.Message}";
    }
}
