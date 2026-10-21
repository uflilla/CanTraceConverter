using System;
using UcanView.Services;

namespace UcanView.Models
{
    /// <summary>
    /// One CAN frame in the analysis window. A trace holds millions, so only the raw values are
    /// stored and every display string is formatted when read: with five strings and a byte
    /// array per frame, a 3.7 million frame trace needed about 1.5 GB and ran this 32-bit
    /// process out of memory.
    /// </summary>
    public sealed class CanMessage
    {
        private static readonly string[] DlcTexts =
            { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15" };

        private ulong _data;
        private long _wallTicks;
        private string _direction = "RX";

        /// <summary>RX, or TX for a frame this adapter sent.</summary>
        public string Direction
        {
            get => _direction;
            // Shared instances, not one string per frame.
            set => _direction = value == "TX" ? "TX" : value == "RX" ? "RX" : value;
        }

        public double TimeValue { get; set; }
        public uint CanIdValue { get; set; }
        public byte DlcValue { get; set; }
        public bool IsExtended { get; set; }
        public bool IsRtr { get; set; }

        public DateTime? WallTime
        {
            get => _wallTicks != 0 ? new DateTime(_wallTicks) : (DateTime?)null;
            set => _wallTicks = value?.Ticks ?? 0;
        }

        /// <summary>The 8 data bytes. Stored packed; each read returns a new array.</summary>
        public byte[] DataBytes
        {
            get => BitConverter.GetBytes(_data);
            set
            {
                var bytes = new byte[8];
                if (value != null)
                    Array.Copy(value, bytes, Math.Min(8, value.Length));
                _data = BitConverter.ToUInt64(bytes, 0);
            }
        }

        /// <summary>The data bytes packed into one value, for comparing frames without allocating.</summary>
        public ulong PackedData => _data;

        public byte DataByte(int index) => index >= 0 && index < 8 ? (byte)(_data >> (index * 8)) : (byte)0;

        public string Time => CanMessageHelper.FormatTime(TimeValue);
        public string CanId => CanTraceTextParser.FormatCanIdDisplay(CanIdValue, IsExtended);
        public string Dlc => DlcValue < DlcTexts.Length ? DlcTexts[DlcValue] : DlcValue.ToString();

        public string Data
        {
            get
            {
                if (IsRtr)
                    return "Remote Request";

                // Formatted for every frame a filter or export touches, so built directly.
                var chars = new char[23];
                for (int i = 0; i < 8; i++)
                {
                    int at = i * 3;
                    if (i > 0)
                        chars[at - 1] = ' ';
                    if (i < DlcValue)
                    {
                        byte b = DataByte(i);
                        chars[at] = HexDigits[b >> 4];
                        chars[at + 1] = HexDigits[b & 0xF];
                    }
                    else
                    {
                        chars[at] = '-';
                        chars[at + 1] = '-';
                    }
                }
                return new string(chars);
            }
        }

        private const string HexDigits = "0123456789ABCDEF";

        /// <summary>The frame as a trace text line.</summary>
        public string RawLine =>
            string.Concat(Direction, ": ", CanMessageHelper.FormatTraceTime(TimeValue), "  ", CanId, " (", Dlc, ") ", Data);

        /// <summary>Same DLC, RTR flag and data bytes.</summary>
        public bool HasSameData(CanMessage other) =>
            other != null && other.IsRtr == IsRtr && other.DlcValue == DlcValue &&
            (IsRtr || (_data & DataMask) == (other._data & DataMask));

        /// <summary>Only the bytes the DLC covers count.</summary>
        private ulong DataMask => DlcValue >= 8 ? ulong.MaxValue : (1UL << (DlcValue * 8)) - 1;
    }

    public class CanMessageSummary
    {
        public string CanId { get; set; }
        public int Count { get; set; }
        public string Direction { get; set; }
        public string Dlc { get; set; }
        public string FirstTime { get; set; }
        public string LastTime { get; set; }
        public double AvgIntervalMs { get; set; }

        public string AvgIntervalDisplay => AvgIntervalMs > 0 ? $"{Services.CanMessageHelper.FormatTime(AvgIntervalMs)} ms" : "—";
    }

    public class CanIdListItem
    {
        public string CanId { get; set; }
        public int Count { get; set; }

        public string DisplayText => $"{CanId}  ({Count:N0})";
    }

    /// <summary>A frame whose data differs from the previous frame with its identifier.</summary>
    public class DataChangeRow
    {
        private readonly CanMessage _previous;
        private readonly CanMessage _current;

        public DataChangeRow(CanMessage previous, CanMessage current)
        {
            _previous = previous;
            _current = current;
        }

        public string CanId => _current.CanId;
        public string Time => _current.Time;
        public double TimeValue => _current.TimeValue;
        public string PreviousData => _previous.Data;
        public string NewData => _current.Data;
        public string Direction => _current.Direction;
    }

    public class GapAnalysisRow
    {
        public string CanId { get; set; }
        public string Direction { get; set; }
        public int MessageCount { get; set; }
        public double MinGapMs { get; set; }
        public double MaxGapMs { get; set; }
        public double AvgGapMs { get; set; }

        /// <summary>Standard deviation of the gaps.</summary>
        public double JitterMs { get; set; }
        public double EstimatedHz { get; set; }

        public string MinGapDisplay => $"{Services.CanMessageHelper.FormatTime(MinGapMs)} ms";
        public string MaxGapDisplay => $"{Services.CanMessageHelper.FormatTime(MaxGapMs)} ms";
        public string AvgGapDisplay => $"{Services.CanMessageHelper.FormatTime(AvgGapMs)} ms";
        public string JitterDisplay => $"{JitterMs:F2} ms";
        public string EstHzDisplay => EstimatedHz > 0 ? $"{EstimatedHz:F2} Hz" : "—";
    }

    public class TimelineSummary
    {
        public string TimeRange { get; set; }
        public int MessageCount { get; set; }
        public int UniqueIds { get; set; }
        public int RxCount { get; set; }
        public int TxCount { get; set; }
    }

    public class FrequencyAnalysis
    {
        public string CanId { get; set; }
        public int TotalCount { get; set; }
        public double Percentage { get; set; }
        public double FrequencyHz { get; set; }
        public string Direction { get; set; }

        public string PercentageDisplay => $"{Percentage:F2}%";
        public string FrequencyDisplay => $"{FrequencyHz:F2}";
    }
}
