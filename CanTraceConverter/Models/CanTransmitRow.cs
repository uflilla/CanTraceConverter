using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CanTraceConverter.Models
{
    public class CanTransmitRow : INotifyPropertyChanged
    {
        private string _canIdHex = "100";
        private bool _extended;
        private byte _dlc = 8;
        private string _dataHex = "00 00 00 00 00 00 00 00";
        private int _intervalMs = 100;
        private bool _cycleEnabled;
        private bool _isCyclicRunning;

        public Guid Id { get; } = Guid.NewGuid();

        public string CanIdHex
        {
            get => _canIdHex;
            set { _canIdHex = value; OnPropertyChanged(); }
        }

        public bool Extended
        {
            get => _extended;
            set { _extended = value; OnPropertyChanged(); }
        }

        public byte Dlc
        {
            get => _dlc;
            set { _dlc = value; OnPropertyChanged(); }
        }

        public string DataHex
        {
            get => _dataHex;
            set { _dataHex = value; OnPropertyChanged(); }
        }

        public int IntervalMs
        {
            get => _intervalMs;
            set { _intervalMs = value; OnPropertyChanged(); }
        }

        /// <summary>When checked, Send starts/stops periodic TX; when unchecked, Send transmits once.</summary>
        public bool CycleEnabled
        {
            get => _cycleEnabled;
            set
            {
                if (_cycleEnabled == value)
                    return;
                _cycleEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SendButtonText));
            }
        }

        public bool IsCyclicRunning
        {
            get => _isCyclicRunning;
            set
            {
                if (_isCyclicRunning == value)
                    return;
                _isCyclicRunning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SendButtonText));
            }
        }

        public string SendButtonText => CycleEnabled && IsCyclicRunning ? "Stop" : "Send";

        public CanTransmitRow Clone()
        {
            return new CanTransmitRow
            {
                CanIdHex = CanIdHex,
                Extended = Extended,
                Dlc = Dlc,
                DataHex = DataHex,
                IntervalMs = IntervalMs,
                CycleEnabled = CycleEnabled
            };
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
