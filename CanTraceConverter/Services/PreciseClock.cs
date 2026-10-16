using System;
using System.Diagnostics;

namespace CanTraceConverter.Services
{
    /// <summary>
    /// Sub-millisecond UTC clock. <see cref="DateTime.UtcNow"/> on .NET Framework only advances
    /// with the system timer tick (1–15.6 ms), which is far coarser than the CAN hardware
    /// timestamps it is compared with.
    /// </summary>
    internal static class PreciseClock
    {
        /// <summary>Re-sync with the system clock when they disagree by more than this,
        /// so NTP adjustments and Stopwatch drift do not accumulate over a long session.</summary>
        private static readonly long ResyncThresholdTicks = TimeSpan.FromMilliseconds(20).Ticks;

        private static readonly object Sync = new object();
        private static readonly double StopwatchToDateTimeTicks = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;
        private static long _baseUtcTicks = DateTime.UtcNow.Ticks;
        private static long _baseStopwatch = Stopwatch.GetTimestamp();

        public static long UtcNowTicks
        {
            get
            {
                lock (Sync)
                {
                    long elapsed = Stopwatch.GetTimestamp() - _baseStopwatch;
                    long precise = _baseUtcTicks + (long)(elapsed * StopwatchToDateTimeTicks);

                    long system = DateTime.UtcNow.Ticks;
                    if (Math.Abs(system - precise) > ResyncThresholdTicks)
                    {
                        _baseUtcTicks = system;
                        _baseStopwatch = Stopwatch.GetTimestamp();
                        precise = system;
                    }

                    return precise;
                }
            }
        }
    }
}
