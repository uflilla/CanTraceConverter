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
            NativeMethods.TimeBeginPeriod(1);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long nextTick = 0;

                while (_running && _bus != null && _bus.IsConnected)
                {
                    long elapsed = sw.ElapsedMilliseconds;
                    if (elapsed >= nextTick)
                    {
                        _bus.SendOnce(_canId, _dlc, _data, _extended, false);
                        nextTick += _intervalMs;
                    }
                    else
                    {
                        int sleep = (int)Math.Min(1, nextTick - elapsed);
                        if (sleep > 0)
                            Thread.Sleep(sleep);
                    }
                }
            }
            finally
            {
                NativeMethods.TimeEndPeriod(1);
            }
        }

        public void Stop()
        {
            _running = false;
            _thread?.Join(500);
            _thread = null;
        }

        public void Dispose() => Stop();
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        internal static extern uint TimeBeginPeriod(uint uPeriod);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        internal static extern uint TimeEndPeriod(uint uPeriod);
    }
}
