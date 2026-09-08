using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CanTraceConverter.Models
{
    public class LiveCanRow : INotifyPropertyChanged
    {
        private string _direction;
        private double _timestampMs;
        private string _timeDisplay;
        private string _periodDisplay = "-";
        private byte _dlc;
        private string _data;
        private int _messageCount;

        public uint CanId { get; set; }
        public string CanIdDisplay { get; set; }
        public bool IsExtended { get; set; }

        public string Direction
        {
            get => _direction;
            set { _direction = value; OnPropertyChanged(); }
        }

        public double TimestampMs
        {
            get => _timestampMs;
            set { _timestampMs = value; OnPropertyChanged(); }
        }

        public string TimeDisplay
        {
            get => _timeDisplay;
            set { _timeDisplay = value; OnPropertyChanged(); }
        }

        public string PeriodDisplay
        {
            get => _periodDisplay;
            set { _periodDisplay = value; OnPropertyChanged(); }
        }

        public byte Dlc
        {
            get => _dlc;
            set { _dlc = value; OnPropertyChanged(); }
        }

        public string Data
        {
            get => _data;
            set { _data = value; OnPropertyChanged(); }
        }

        public int MessageCount
        {
            get => _messageCount;
            set { _messageCount = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
