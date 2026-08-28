using System;

namespace CanTraceConverter.Models
{
    public class CanMessage
    {
        public string Direction { get; set; }
        public string Time { get; set; }
        public double TimeValue { get; set; }
        public string CanId { get; set; }
        public uint CanIdValue { get; set; }
        public string Dlc { get; set; }
        public string Data { get; set; }
        public byte[] DataBytes { get; set; }
        public string RawLine { get; set; }
        public bool IsExtended { get; set; }
        public bool IsRtr { get; set; }
        public DateTime? WallTime { get; set; }
    }

    public class CanMessageSummary
    {
        public string CanId { get; set; }
        public int Count { get; set; }
        public string Direction { get; set; }
        public string Dlc { get; set; }
        public string FirstTime { get; set; }
        public string LastTime { get; set; }
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
