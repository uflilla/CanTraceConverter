using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using CanTraceConverter.Helpers;
using static UcanDotNET.USBcanServer;

namespace CanTraceConverter.ViewModels
{
    public class TraceConverter
    {
        public event Action<int> ProgressUpdated;
        public event Action<string> StatusUpdated;

        private readonly Dispatcher _dispatcher;

        public TraceConverter(Dispatcher dispatcher = null)
        {
            _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        }

        /// <summary>
        /// Converts a CAN trace file and returns the formatted output as a string.
        /// </summary>
        /// <returns>
        /// Tuple containing:
        /// - result: Output string with header and all messages
        /// - messageCount: Total number of CAN messages processed
        /// - errorCode: 0 on success, error code otherwise
        /// - header: Header information separately (for display purposes)
        /// </returns>
        public async Task<(string result, int messageCount, int errorCode, string header)> ConvertAsync(
            string inputPath,
            bool useDifferentialTime)
        {
            int errorCode = 0;
            int count = 0;
            string headerText = string.Empty;
            var sb = new StringBuilder(1024 * 1024); // Pre-allocate 1MB

            try
            {
                if (!File.Exists(inputPath))
                {
                    RaiseStatus("Input file does not exist.");
                    return (string.Empty, 0, 0x1000, string.Empty);
                }

                RaiseStatus("Reading binary trace file...");

                using var reader = new UcanTraceBinaryReader(File.OpenRead(inputPath));

                // ── Read header ────────────────────────────────────────
                uint signature = reader.ReadUInt32();
                if (signature != 0x5543414E)
                {
                    RaiseStatus("Invalid binary trace file (wrong signature).");
                    return (string.Empty, 0, 0x1002, string.Empty);
                }

                uint offset = reader.ReadUInt32();
                uint version = reader.ReadUInt32();
                var sysTime = reader.ReadSystemTime();
                var osVer = reader.ReadOsVersion();
                var hwInfo = reader.ReadUcanHwInfo();
                var ch0 = reader.ReadUcanChannelInfo();
                var ch1 = reader.ReadUcanChannelInfo();

                var activeChannel = ch0.m_fCanIsInit ? ch0 : ch1;

                headerText = BuildHeader(inputPath, version, sysTime, osVer, hwInfo, ch0, ch1);

                // Append header to output
                sb.AppendLine(headerText);
                sb.AppendLine(UcanMsgFormatProvider.GetMessageHeader());

                RaiseStatus("Converting messages...");

                // ── Read messages ──────────────────────────────────────
                tCanMsgStruct prevMsg = tCanMsgStruct.CreateInstance(0, 0);
                var formatter = new UcanMsgFormatProvider(hwInfo, activeChannel);

                while (true)
                {
                    try
                    {
                        var msg = reader.ReadUcanMessage();
                        count++;

                        if (useDifferentialTime && count > 1)
                        {
                            msg.m_dwTime -= prevMsg.m_dwTime;
                        }
                        prevMsg = msg;

                        string line = string.Format(formatter, "{0}", msg);
                        sb.AppendLine(line);

                        // Update progress every 500 messages
                        if (count % 500 == 0)
                        {
                            RaiseProgress(count);
                            await Task.Delay(1); // Small yield for UI responsiveness
                        }
                    }
                    catch (EndOfStreamException)
                    {
                        break;
                    }
                }

                // Append footer
                sb.AppendLine();
                sb.AppendLine($"Total number of CAN messages: {count}");

                RaiseProgress(count);
                RaiseStatus($"Conversion complete: {count:N0} messages processed.");
            }
            catch (Exception ex)
            {
                RaiseStatus($"Conversion failed: {ex.Message}");
                errorCode = 0x7FFF;
            }

            return (sb.ToString(), count, errorCode, headerText);
        }

        private string BuildHeader(string path, uint ver, SYSTEMTIME st, OSVERSIONINFO os,
            tUcanHardwareInfoEx hw, tUcanChannelInfo ch0, tUcanChannelInfo ch1)
        {
            var sb = new StringBuilder(512);
            sb.AppendLine($"InputFile: {path}");
            sb.AppendLine($"Version:   {string.Format(new VersionFormatProvider(), "PeakUcan.dll {0}", ver)}");
            sb.AppendLine($"Date/Time: {string.Format(new SystemTimeFormatProvider(), "{0}", st)}");
            sb.AppendLine($"OS:        {string.Format(new OsVerisonFormatProvider(), "{0}", os)}");
            sb.AppendLine($"Hardware:  {string.Format(new UcanHwInfoFormatProvider(), "{0}", hw)}");
            sb.AppendLine($"Channel0:  {string.Format(new UcanChannelInfoFormatProvider(), "{0}", ch0)}");
            sb.AppendLine($"Channel1:  {string.Format(new UcanChannelInfoFormatProvider(), "{0}", ch1)}");
            return sb.ToString();
        }

        // Safe UI updates
        private void RaiseStatus(string msg) =>
            _dispatcher?.Invoke(() => StatusUpdated?.Invoke(msg));

        private void RaiseProgress(int cnt) =>
            _dispatcher?.Invoke(() => ProgressUpdated?.Invoke(cnt));
    }
}
