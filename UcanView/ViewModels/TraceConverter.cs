using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using UcanView.Helpers;
using static UcanDotNET.USBcanServer;

namespace UcanView.ViewModels
{
    public sealed class TraceConversionResult
    {
        /// <summary>0 on success, an error code otherwise.</summary>
        public int ErrorCode { get; set; }
        public long MessageCount { get; set; }
        public string HeaderText { get; set; } = string.Empty;
    }

    public class TraceConverter
    {
        /// <summary>Messages written so far, and the total.</summary>
        public event Action<long, long> ProgressUpdated;
        public event Action<string> StatusUpdated;

        private readonly Dispatcher _dispatcher;

        public TraceConverter(Dispatcher dispatcher = null)
        {
            _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        }

        /// <summary>
        /// Reads a binary trace's header and, when <paramref name="outputPath"/> is given, writes
        /// the whole trace there as text. Lines are streamed to the file: a long trace converts
        /// to hundreds of megabytes of text, which never fit in memory in this 32-bit process.
        /// </summary>
        public Task<TraceConversionResult> ConvertAsync(string inputPath, bool useDifferentialTime, string outputPath)
        {
            // Genuinely off the UI thread. This used to run inline and yield with
            // Task.Delay(1) every 500 messages, which both stuttered the UI and added a
            // system-timer tick (~15 ms) of pure waiting per yield.
            return Task.Run(() => Convert(inputPath, useDifferentialTime, outputPath));
        }

        private TraceConversionResult Convert(string inputPath, bool useDifferentialTime, string outputPath)
        {
            var result = new TraceConversionResult();

            try
            {
                if (!File.Exists(inputPath))
                {
                    RaiseStatus("Input file does not exist.");
                    result.ErrorCode = 0x1000;
                    return result;
                }

                RaiseStatus("Reading binary trace file...");

                // ── Read header ────────────────────────────────────────
                UcanTraceFile trace;
                try
                {
                    trace = UcanTraceFile.Open(inputPath);
                }
                catch (InvalidDataException ex)
                {
                    RaiseStatus(ex.Message);
                    result.ErrorCode = 0x1002;
                    return result;
                }

                using var traceScope = trace;
                result.HeaderText = trace.BuildHeaderText(inputPath);
                result.MessageCount = trace.MessageCount;

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    RaiseStatus($"Trace read: {result.MessageCount:N0} messages.");
                    return result;
                }

                RaiseStatus("Converting messages...");
                result.MessageCount = WriteText(trace, result.HeaderText, outputPath, useDifferentialTime);
                RaiseStatus($"Conversion complete: {result.MessageCount:N0} messages processed.");
            }
            catch (Exception ex)
            {
                RaiseStatus($"Conversion failed: {ex.Message}");
                result.ErrorCode = 0x7FFF;
            }

            return result;
        }

        private long WriteText(UcanTraceFile trace, string headerText, string outputPath, bool useDifferentialTime)
        {
            long total = trace.MessageCount;
            long count = 0;
            bool highRes = trace.IsHighResTimer;
            var line = new StringBuilder(96);

            using (var writer = new StreamWriter(outputPath, false, new UTF8Encoding(true), 1 << 16))
            {
                writer.WriteLine(headerText);
                writer.WriteLine(UcanMsgFormatProvider.GetMessageHeader());

                tCanMsgStruct prevMsg = tCanMsgStruct.CreateInstance(0, 0);
                var progressClock = System.Diagnostics.Stopwatch.StartNew();
                long nextProgressMs = 0;

                while (trace.TryReadMessage(out var msg))
                {
                    count++;

                    var displayMsg = msg;
                    if (useDifferentialTime && count > 1)
                    {
                        displayMsg.m_dwTime -= prevMsg.m_dwTime;
                    }
                    prevMsg = msg; // Store original message for next calculation

                    line.Clear();
                    UcanMsgFormatProvider.AppendMessage(line, displayMsg, highRes);
                    line.AppendLine();
                    writer.Write(line);

                    // Report progress on a time budget rather than a message count, so a
                    // large trace does not pay a marshalling round-trip every 500 rows.
                    if (progressClock.ElapsedMilliseconds >= nextProgressMs)
                    {
                        nextProgressMs = progressClock.ElapsedMilliseconds + 250;
                        RaiseProgress(count, total);
                    }
                }

                // Append footer
                writer.WriteLine();
                writer.WriteLine($"Total number of CAN messages: {count}");
            }

            RaiseProgress(count, total);
            return count;
        }

        // Safe UI updates. BeginInvoke, not Invoke: the conversion thread must not block
        // waiting for the UI to render a progress number.
        private void RaiseStatus(string msg) =>
            _dispatcher?.BeginInvoke(new Action(() => StatusUpdated?.Invoke(msg)));

        private void RaiseProgress(long cnt, long total) =>
            _dispatcher?.BeginInvoke(new Action(() => ProgressUpdated?.Invoke(cnt, total)));
    }
}
