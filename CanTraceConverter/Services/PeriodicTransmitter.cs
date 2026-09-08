using System;
using System.Threading;
using CanTraceConverter.Models;

namespace CanTraceConverter.Services
{
    /// <summary>
    /// Software periodic transmitter using high-resolution timing (~1 ms accuracy).
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
                Name = "PeriodicCanTx"
            };
            _thread.Start();
        }

        private void TransmitLoop()
        {
            // Request 1 ms timer resolution from Windows multimedia timer API.
            NativeMethods.timeBeginPeriod(1);
            string fault = null;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long nextTick = 0;

                while (_running)
                {
                    var bus = _bus;
                    if (bus == null || !bus.CanTransmit)
                    {
                        if (bus != null && bus.IsConnected && bus.BusStatus.BlocksTransmit)
                            fault = "Transmission stopped: " + bus.BusStatus.Message;
                        break;
                    }

                    long elapsed = sw.ElapsedMilliseconds;
                    if (elapsed >= nextTick)
                    {
                        bus.SendOnce(_canId, _dlc, _data, _extended, false);
                        nextTick += _intervalMs;

                        // If a slow USB write (or a scheduling stall) pushed us past a whole
                        // interval, resynchronise instead of firing a catch-up burst onto
                        // the bus.
                        if (nextTick <= elapsed)
                            nextTick = elapsed + _intervalMs;
                    }
                    else
                    {
                        long remaining = nextTick - elapsed;
                        Thread.Sleep(remaining > 1 ? 1 : (int)remaining);
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

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        internal static extern uint timeBeginPeriod(uint uPeriod);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        internal static extern uint timeEndPeriod(uint uPeriod);
    }
}
