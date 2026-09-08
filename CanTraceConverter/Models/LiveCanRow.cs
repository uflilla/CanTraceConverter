namespace CanTraceConverter.Models
{
    /// <summary>
    /// One row of the live "latest value per ID" grid.
    /// </summary>
    /// <remarks>
    /// Deliberately does not implement <see cref="System.ComponentModel.INotifyPropertyChanged"/>.
    /// These fields are written by the CAN receive thread, and WPF marshals every bound
    /// cross-thread property change onto the dispatcher — seven per received message, which
    /// put dispatcher work directly on the timing-critical reader. The monitor window refreshes
    /// the grid on a 200 ms timer instead, which is also what the user can actually read.
    /// </remarks>
    public class LiveCanRow
    {
        public uint CanId { get; set; }
        public string CanIdDisplay { get; set; }
        public bool IsExtended { get; set; }
        public string Direction { get; set; }
        public double TimestampMs { get; set; }
        public string TimeDisplay { get; set; }
        public string PeriodDisplay { get; set; } = "-";
        public byte Dlc { get; set; }
        public string Data { get; set; }
        public int MessageCount { get; set; }
    }
}
