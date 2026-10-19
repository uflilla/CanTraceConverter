using System;
using UcanDotNET;

namespace CanTraceConverter.Services
{
    /// <summary>
    /// A <see cref="USBcanServer"/> that exposes its native handle, so the handle can be released
    /// after a fatal disconnect.
    /// </summary>
    /// <remarks>
    /// On a fatal disconnect (module unplugged while initialised) the wrapper clears its own
    /// "initialised" flags and zeroes its handle without calling UcanDeinitHardware, so a later
    /// Shutdown or Dispose never releases it either. The driver can then keep the module's
    /// device number in use, and initialising the module again after it is plugged back in
    /// fails until the application restarts.
    /// </remarks>
    internal sealed class TrackedUcanServer : USBcanServer
    {
        /// <summary>The native handle; valid only after a successful <c>InitHardware</c>.</summary>
        public byte NativeHandle => m_UcanHandle;

        /// <summary>Calls the driver's UcanDeinitHardware for a handle the wrapper has abandoned.</summary>
        public static int DeinitNativeHandle(byte handle) => UcanDeinitHardware(handle);
    }

    /// <summary>
    /// Serialises the parts of the USB-CAN wrapper that are not thread-safe.
    /// </summary>
    /// <remarks>
    /// <para>The wrapper keeps process-wide state with no locking: a plug-and-play reference
    /// count changed by every constructor and <c>Dispose</c> (at zero it unregisters the
    /// driver's connect/disconnect notifications), and static arrays used by
    /// <c>EnumerateHardware</c>. Servers are created and disposed on the UI thread, the receive
    /// thread (after a disconnect), teardown threads and the device-scan thread. Unserialised,
    /// the count could reach zero while a module was still open, after which an unplug was no
    /// longer reported, and two scans could overwrite each other's results.</para>
    /// <para>Every construction, <c>InitHardware</c>, <c>Dispose</c> and <c>EnumerateHardware</c>
    /// runs under <see cref="Lock"/>. It is taken after <c>UsbCanHardwareManager.Gate</c> and
    /// after a module's I/O lock, never before them, and no other lock is taken while holding it.</para>
    /// </remarks>
    internal static class UcanServerAccess
    {
        public static readonly object Lock = new object();

        public static TrackedUcanServer Create()
        {
            lock (Lock)
                return new TrackedUcanServer();
        }

        public static void Dispose(USBcanServer server)
        {
            if (server == null)
                return;

            lock (Lock)
            {
                try { server.Dispose(); } catch { /* ignore */ }
            }
        }
    }
}
