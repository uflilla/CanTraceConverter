using System;
using System.Diagnostics;
using System.Threading;
using UcanView.Models;

namespace UcanView.Services
{
    /// <summary>
    /// Software periodic transmitter. The schedule itself is sub-millisecond; what reaches
    /// the wire still carries USB transfer jitter (full-speed USB frames are 1 ms).
    /// </summary>
    public sealed class PeriodicTransmitter : IDisposable
    {
        private Thread _thread;
        private volatile bool _running;
        private CanBusService _bus;
        private uint _canId;
        private byte _dlc;
        private byte[] _data;
        private bool _extended;
        private int _intervalMs;

        public bool IsRunning => _running;

        /// <summary>Raised on the transmit thread when transmission stops unexpectedly.</summary>
        public event Action<string> Faulted;

        public void Start(
            CanBusService bus,
            int intervalMs,
            uint canId,
            byte dlc,
            byte[] data,
            bool extended)
        {
            Stop();
            _bus = bus;
            _intervalMs = Math.Max(1, intervalMs);
            _canId = canId;
            _dlc = dlc;
            _data = data ?? new byte[8];
            _extended = extended;
            _running = true;
            _thread = new Thread(TransmitLoop)
            {
                IsBackground = true,
                Name = "PeriodicCanTx",
                Priority = ThreadPriority.Highest
            };
            _thread.Start();
        }

        private void TransmitLoop()
        {
            // Request 1 ms timer resolution from Windows multimedia timer API.
            NativeMethods.timeBeginPeriod(1);
            string fault = null;
            var timer = HighResolutionWait.TryCreate();
            try
            {
                // Scheduled in Stopwatch ticks, not whole milliseconds: the old integer-ms
                // clock plus Sleep(1) granularity put up to ~2 ms of jitter on every frame.
                long frequency = Stopwatch.Frequency;
                long intervalTicks = Math.Max(1, (long)Math.Round(_intervalMs * frequency / 1000.0));
                long spinWindow = frequency / 2000; // final 0.5 ms is spun for accuracy
                long maxWaitTicks = frequency / 20;
                var sw = Stopwatch.StartNew();
                long nextDue = 0;

                while (_running)
                {
                    var bus = _bus;
                    if (bus == null || !bus.CanTransmit)
                    {
                        if (bus != null && bus.IsConnected && bus.BusStatus.BlocksTransmit)
                            fault = "Transmission stopped: " + bus.BusStatus.Message;
                        break;
                    }

                    long now = sw.ElapsedTicks;
                    long remaining = nextDue - now;
                    if (remaining <= 0)
                    {
                        bus.SendOnce(_canId, _dlc, _data, _extended, false);
                        nextDue += intervalTicks;

                        // If a slow USB write (or a scheduling stall) pushed us past a whole
                        // interval, resynchronise instead of firing a catch-up burst onto
                        // the bus.
                        now = sw.ElapsedTicks;
                        if (nextDue <= now)
                            nextDue = now + intervalTicks;
                    }
                    else if (remaining > spinWindow)
                    {
                        // Capped so a stop request on a long interval is still seen promptly.
                        long sleepTicks = Math.Min(remaining - spinWindow, maxWaitTicks);
                        if (timer != null)
                            timer.Wait(sleepTicks * TimeSpan.TicksPerSecond / frequency);
                        else
                            Thread.Sleep(1);
                    }
                    else
                    {
                        Thread.SpinWait(20);
                    }
                }
            }
            catch (Exception ex)
            {
                // A background thread exception would otherwise terminate the process.
                fault = "Cyclic transmit stopped: " + ex.Message;
                AppLog.Warn(ex, "Periodic transmitter failed.");
            }
            finally
            {
                _running = false;
                timer?.Dispose();
                NativeMethods.timeEndPeriod(1);

                if (fault != null)
                {
                    try { Faulted?.Invoke(fault); } catch { /* ignore subscriber errors */ }
                }
            }
        }

        /// <summary>
        /// Asks the transmit thread to stop without waiting for it. Lets a caller signal many
        /// transmitters and then join them once, instead of paying a timeout per transmitter.
        /// </summary>
        public void RequestStop()
        {
            _running = false;
            // Drop bus reference first so an in-flight loop iteration cannot TX.
            _bus = null;
        }

        public void Stop()
        {
            RequestStop();

            var thread = _thread;
            _thread = null;
            if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
            {
                try
                {
                    if (!thread.Join(1000))
                        AppLog.Warn("Periodic transmitter did not stop within 1 s.");
                }
                catch { /* ignore */ }
            }
        }

        public void Dispose()
        {
            Faulted = null;
            Stop();
        }
    }

    /// <summary>
    /// Waitable timer created with CREATE_WAITABLE_TIMER_HIGH_RESOLUTION (Windows 10 1803+),
    /// which wakes within a fraction of a millisecond instead of on the next scheduler tick.
    /// </summary>
    internal sealed class HighResolutionWait : IDisposable
    {
        private IntPtr _handle;

        private HighResolutionWait(IntPtr handle) => _handle = handle;

        public static HighResolutionWait TryCreate()
        {
            try
            {
                IntPtr handle = NativeMethods.CreateWaitableTimerExW(
                    IntPtr.Zero, null, NativeMethods.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, NativeMethods.TIMER_ALL_ACCESS);
                return handle == IntPtr.Zero ? null : new HighResolutionWait(handle);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Blocks for the given number of 100 ns units.</summary>
        public void Wait(long duration100ns)
        {
            if (duration100ns <= 0 || _handle == IntPtr.Zero)
                return;

            long due = -duration100ns; // negative = relative
            if (NativeMethods.SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                NativeMethods.WaitForSingleObject(_handle, NativeMethods.INFINITE);
            else
                Thread.Sleep(1);
        }

        public void Dispose()
        {
            var handle = _handle;
            _handle = IntPtr.Zero;
            if (handle != IntPtr.Zero)
                NativeMethods.CloseHandle(handle);
        }
    }

    internal static class NativeMethods
    {
        internal const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        internal const uint TIMER_ALL_ACCESS = 0x001F0003;
        internal const uint INFINITE = 0xFFFFFFFF;

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        internal static extern uint timeBeginPeriod(uint uPeriod);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        internal static extern uint timeEndPeriod(uint uPeriod);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        internal static extern IntPtr CreateWaitableTimerExW(IntPtr lpTimerAttributes, string lpTimerName, uint dwFlags, uint dwDesiredAccess);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool SetWaitableTimer(IntPtr hTimer, ref long pDueTime, int lPeriod,
            IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool fResume);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);
    }
}
