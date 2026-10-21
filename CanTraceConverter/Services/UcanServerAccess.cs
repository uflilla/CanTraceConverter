using System;
using System.Runtime.InteropServices;
using System.Threading;
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

        /// <summary>
        /// Does what the wrapper's <c>Shutdown</c> does, without its error-code arithmetic.
        /// </summary>
        /// <remarks>
        /// <c>Shutdown</c> adds each driver return code into a <c>Byte</c> with overflow checking.
        /// Once the module is unplugged, deinitialising both channels returns two error codes
        /// whose sum exceeds 255, and it throws before it reaches UcanDeinitHardware. The driver
        /// then keeps the module marked in use, and initialising it again after it is plugged
        /// back in fails until the application exits.
        /// </remarks>
        public void ShutdownIgnoringErrors(byte channel, bool shutDownHardware)
        {
            // The same monitor the wrapper's own Shutdown takes.
            lock (this)
            {
                bool all = channel == (byte)eUcanChannel.USBCAN_CHANNEL_ALL || shutDownHardware;

                if (m_fCh0IsInitialized && (all || channel == (byte)eUcanChannel.USBCAN_CHANNEL_CH0))
                {
                    UcanDeinitCanEx(m_UcanHandle, (byte)eUcanChannel.USBCAN_CHANNEL_CH0);
                    m_fCh0IsInitialized = false;
                }

                if (m_fCh1IsInitialized && (all || channel == (byte)eUcanChannel.USBCAN_CHANNEL_CH1))
                {
                    UcanDeinitCanEx(m_UcanHandle, (byte)eUcanChannel.USBCAN_CHANNEL_CH1);
                    m_fCh1IsInitialized = false;
                }

                if (m_fHwIsInitialized && shutDownHardware)
                {
                    // The wrapper waits here so a pending callback handler cannot call ReadCanMsg
                    // on a released handle.
                    Thread.Sleep(200);

                    // The wrapper documents an occasional NullReferenceException from this call.
                    try { UcanDeinitHardware(m_UcanHandle); }
                    catch (NullReferenceException) { /* ignore */ }

                    m_fHwIsInitialized = false;
                    m_UcanHandle = 0;
                }
            }
        }

        /// <summary>
        /// Calls the driver's UcanDeinitHardware for a handle the wrapper has abandoned.
        /// </summary>
        /// <remarks>
        /// The wrapper freed its callback GCHandle on the fatal disconnect. If the driver then
        /// raises USBCAN_EVENT_DEINITHW for this handle, the wrapper frees it again, and that
        /// throws on the driver's thread in the middle of the call. Re-pinning the callback
        /// first gives that second free something to release.
        /// </remarks>
        /// <param name="callbackRaised">Whether the driver raised USBCAN_EVENT_DEINITHW.</param>
        public int DeinitAbandonedHandle(byte handle, out bool callbackRaised)
        {
            if (!m_gchUcanCallback.IsAllocated && m_pfnUcanCallback != null)
                m_gchUcanCallback = GCHandle.Alloc(m_pfnUcanCallback, GCHandleType.Normal);

            int result = UcanDeinitHardware(handle);

            callbackRaised = !m_gchUcanCallback.IsAllocated;
            if (!callbackRaised)
                m_gchUcanCallback.Free();

            return result;
        }
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
