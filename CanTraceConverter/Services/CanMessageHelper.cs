using System;
using System.Text;
using CanTraceConverter.Models;
using UcanDotNET;
using static UcanDotNET.USBcanServer;

namespace CanTraceConverter.Services
{
    public static class CanMessageHelper
    {
        /// <summary>
        /// Maps a hardware message to a record.
        /// </summary>
        /// <param name="timestampScaleMs">Milliseconds per hardware tick, resolved once when
        /// the channel was opened (1.0 normally, 0.1 for a G4 high-resolution timer). Passing
        /// it in avoids re-reading a channel-info struct that another thread may be rewriting.</param>
        /// <param name="unwrappedTicks">Tick count already corrected for 32-bit wrap-around.</param>
        public static CanMessageRecord FromHardwareMessage(
            tCanMsgStruct msg,
            double timestampScaleMs,
            long unwrappedTicks,
            DateTime wallTime)
        {
            bool isTx = (msg.m_bFF & (byte)eUcanMsgFrameFormat.USBCAN_MSG_FF_ECHO) != 0;
            bool isExtended = (msg.m_bFF & (byte)eUcanMsgFrameFormat.USBCAN_MSG_FF_EXT) != 0;
            bool isRtr = (msg.m_bFF & (byte)eUcanMsgFrameFormat.USBCAN_MSG_FF_RTR) != 0;

            var data = new byte[8];
            if (msg.m_bData != null)
                Array.Copy(msg.m_bData, data, Math.Min(8, msg.m_bData.Length));

            return new CanMessageRecord
            {
                Direction = isTx ? "TX" : "RX",
                TimestampMs = unwrappedTicks * timestampScaleMs,
                RawHardwareTime = unwrappedTicks,
                WallTime = wallTime,
                CanId = (uint)msg.m_dwID,
                IsExtended = isExtended,
                IsRtr = isRtr,
                Dlc = msg.m_bDLC,
                Data = data
            };
        }

        public static CanMessage ToDisplayMessage(CanMessageRecord record)
        {
            return new CanMessage
            {
                Direction = record.Direction,
                TimeValue = record.TimestampMs,
                Time = FormatTime(record.TimestampMs),
                CanIdValue = record.CanId,
                CanId = record.CanIdDisplay,
                Dlc = record.Dlc.ToString(),
                Data = record.DataDisplay,
                DataBytes = record.Data,
                RawLine = record.ToRawLine(),
                IsExtended = record.IsExtended,
                IsRtr = record.IsRtr,
                WallTime = record.WallTime
            };
        }

        public static string FormatTime(double timestampMs)
        {
            if (Math.Abs(timestampMs - Math.Round(timestampMs)) > 0.01)
                return $"{timestampMs:F1}";

            return $"{timestampMs:F0}";
        }

        public static tCanMsgStruct ToHardwareMessage(
            uint canId,
            byte dlc,
            byte[] data,
            bool extended,
            bool rtr)
        {
            byte ff = 0;
            if (extended)
                ff |= (byte)eUcanMsgFrameFormat.USBCAN_MSG_FF_EXT;
            if (rtr)
                ff |= (byte)eUcanMsgFrameFormat.USBCAN_MSG_FF_RTR;

            var msg = tCanMsgStruct.CreateInstance((int)canId, ff);
            msg.m_bDLC = dlc;
            if (data != null)
            {
                for (int i = 0; i < Math.Min(8, data.Length); i++)
                    msg.m_bData[i] = data[i];
            }
            return msg;
        }

        public static int MapBaudRateKbps(int kbps, tUcanHardwareInfoEx hwInfo)
        {
            if (CheckIs_G4(hwInfo))
                return MapG4BaudEx(kbps);

            return MapStandardBaudEx(kbps);
        }

        private static int MapG4BaudEx(int kbps)
        {
            switch (kbps)
            {
                case 1000: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_G4_1MBit;
                case 800: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_G4_800kBit;
                case 500: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_G4_500kBit;
                case 250: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_G4_250kBit;
                case 125: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_G4_125kBit;
                case 100: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_G4_100kBit;
                case 50: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_G4_50kBit;
                case 20: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_G4_20kBit;
                case 10: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_G4_10kBit;
                default: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_G4_500kBit;
            }
        }

        private static int MapStandardBaudEx(int kbps)
        {
            switch (kbps)
            {
                case 1000: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_1MBit;
                case 800: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_800kBit;
                case 500: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_500kBit;
                case 250: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_250kBit;
                case 125: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_125kBit;
                case 100: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_100kBit;
                case 50: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_50kBit;
                case 20: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_20kBit;
                case 10: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_10kBit;
                default: return (int)eUcanBaudrateEx.USBCAN_BAUDEX_500kBit;
            }
        }

        public static string BuildSessionHeader(SessionConfig config, tUcanHardwareInfoEx hw, tUcanChannelInfo ch)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Session:   {config.Title}");
            sb.AppendLine($"Device:    {config.DeviceNumber}  Channel: {config.Channel}");
            sb.AppendLine($"Baudrate:  {config.BaudRateKbps} kBit/s");
            sb.AppendLine($"Mode:      {(config.ListenOnly ? "Listen-only" : "Normal")}");
            sb.AppendLine($"Hardware:  {string.Format(new Helpers.UcanHwInfoFormatProvider(), "{0}", hw)}");
            sb.AppendLine($"Channel:   {string.Format(new Helpers.UcanChannelInfoFormatProvider(), "{0}", ch)}");
            sb.AppendLine($"Started:   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            return sb.ToString();
        }
    }

    public class CanMessageRecord
    {
        public long Id { get; set; }
        public string Direction { get; set; }
        public double TimestampMs { get; set; }

        /// <summary>Device tick counter, unwrapped to 64 bits so it stays monotonic.</summary>
        public long RawHardwareTime { get; set; }
        public DateTime WallTime { get; set; }
        public uint CanId { get; set; }
        public bool IsExtended { get; set; }
        public bool IsRtr { get; set; }
        public byte Dlc { get; set; }
        public byte[] Data { get; set; } = new byte[8];

        public string CanIdDisplay =>
            IsExtended ? $"{CanId:X8}h" : $"{CanId,3:X3}h".Trim();

        public string DataDisplay
        {
            get
            {
                if (IsRtr)
                    return "Remote Request";

                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++)
                {
                    if (i < Dlc)
                        sb.Append($"{Data[i]:X2} ");
                    else
                        sb.Append("-- ");
                }
                return sb.ToString().TrimEnd();
            }
        }

        public string ToRawLine()
        {
            string time = CanMessageHelper.FormatTime(TimestampMs);
            return $"{Direction}: {time}  {CanIdDisplay} ({Dlc}) {DataDisplay}";
        }
    }
}
