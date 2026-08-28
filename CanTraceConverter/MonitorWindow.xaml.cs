using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CanTraceConverter.Models;
using CanTraceConverter.Services;
using Microsoft.Win32;

namespace CanTraceConverter
{
    public partial class MonitorWindow : Window
    {
        private readonly SessionConfig _config;
        private readonly CanBusService _bus = new CanBusService();
        private readonly CanMessageRepository _repository = new CanMessageRepository();
        private readonly PeriodicTransmitter _periodicTx = new PeriodicTransmitter();
        private readonly Dictionary<uint, LiveCanRow> _liveRows = new Dictionary<uint, LiveCanRow>();
        private readonly ObservableCollection<LiveCanRow> _liveCollection = new ObservableCollection<LiveCanRow>();
        private readonly DispatcherTimer _uiTimer;
        private bool _periodicRunning;
        private long _totalMessages;
        private bool _dirty;
        private bool _isClosing;
        private bool _loggingEnabled;

        public MonitorWindow(SessionConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            InitializeComponent();

            dgLive.ItemsSource = _liveCollection;
            cmbSendDlc.SelectionChanged += (_, __) => hexSendData.MaxBytes = GetSelectedDlc();
            UpdateSendPanelState();

            Title = $"CAN Monitor — {config.Title}";
            txtSessionTitle.Text = config.Title;
            txtConnectionInfo.Text =
                $"Device {config.DeviceNumber} · CH{config.Channel} · {config.BaudRateKbps} kBit/s · " +
                (config.ListenOnly ? "Listen-only" : "Normal");

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            _bus.MessageReceived += OnMessageReceived;
            _bus.StatusChanged += status => RunOnUiThread(() => txtStatus.Text = status);
            _bus.Disconnected += () => RunOnUiThread(OnDisconnected);

            Loaded += (_, __) => StartSession();
        }

        private void UpdateSendPanelState()
        {
            bool transmitAllowed = !_config.ListenOnly && _bus.IsConnected;

            gridSendControls.IsEnabled = transmitAllowed;
            txtSendDisabledNotice.Visibility = _config.ListenOnly ? Visibility.Visible : Visibility.Collapsed;

            if (!_config.ListenOnly && !_bus.IsConnected)
                txtSendDisabledNotice.Visibility = Visibility.Visible;

            txtSendDisabledNotice.Text = _config.ListenOnly
                ? "Transmit disabled — session started in listen-only mode. Start a new session with listen-only unchecked to send."
                : "Transmit disabled — not connected to CAN hardware.";
        }

        private void RunOnUiThread(Action action)
        {
            if (_isClosing || action == null)
                return;

            if (Dispatcher.CheckAccess())
                action();
            else
                Dispatcher.BeginInvoke(action);
        }

        private void StartSession()
        {
            try
            {
                if (!_bus.Connect(_config))
                {
                    ShowConnectionFailed(_bus.LastError ?? "Could not connect to USB-CAN device.");
                    return;
                }

                var header = CanMessageHelper.BuildSessionHeader(
                    _config, _bus.HardwareInfo, _bus.ChannelInfo);
                _repository.Open(_config, header);
                _loggingEnabled = true;

                txtConnectionStatus.Text = "Connected — logging";
                txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Green;
                txtLogPath.Text = Path.GetFileName(_repository.DatabasePath);
                btnReconnect.Visibility = Visibility.Collapsed;
                UpdateSendPanelState();
            }
            catch (Exception ex)
            {
                ShowConnectionFailed(ex.Message);
            }
        }

        private void ShowConnectionFailed(string message)
        {
            _loggingEnabled = false;
            txtConnectionStatus.Text = "Not connected";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            txtStatus.Text = message;
            btnReconnect.Visibility = Visibility.Visible;
            UpdateSendPanelState();

            MessageBox.Show(
                message + Environment.NewLine + Environment.NewLine +
                "You can retry with Reconnect after installing drivers or connecting the device.",
                "Connection Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private void OnMessageReceived(CanMessageRecord record)
        {
            if (_isClosing || record == null)
                return;

            if (_loggingEnabled)
                _repository.Enqueue(record);

            System.Threading.Interlocked.Increment(ref _totalMessages);

            uint id = record.CanId;
            lock (_liveRows)
            {
                if (!_liveRows.TryGetValue(id, out var row))
                {
                    row = new LiveCanRow
                    {
                        CanId = id,
                        CanIdDisplay = record.CanIdDisplay,
                        IsExtended = record.IsExtended
                    };
                    _liveRows[id] = row;
                    RunOnUiThread(() =>
                    {
                        if (!_isClosing)
                            _liveCollection.Add(row);
                    });
                }

                row.Direction = record.Direction;
                row.TimestampMs = record.TimestampMs;
                row.TimeDisplay = CanMessageHelper.FormatTime(record.TimestampMs);
                row.Dlc = record.Dlc;
                row.Data = record.DataDisplay;
                row.MessageCount++;
            }
            _dirty = true;
        }

        private void UiTimer_Tick(object sender, EventArgs e)
        {
            if (_isClosing)
                return;

            if (_dirty)
            {
                _dirty = false;
                try { dgLive.Items.Refresh(); } catch { /* ignore during shutdown */ }
            }

            long saved = _loggingEnabled ? _repository.TotalWritten : 0;
            txtLiveStats.Text = $"{_liveCollection.Count} IDs · {_totalMessages:N0} total · {saved:N0} saved";
        }

        private void OnDisconnected()
        {
            _loggingEnabled = false;
            txtConnectionStatus.Text = "Disconnected";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            btnReconnect.Visibility = Visibility.Visible;
            UpdateSendPanelState();
            if (_periodicRunning)
                StopPeriodic();
        }

        private void BtnSendOnce_Click(object sender, RoutedEventArgs e)
        {
            if (!_bus.IsConnected)
            {
                MessageBox.Show("Not connected to CAN hardware.", "TX Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParseSendInput(out uint id, out byte dlc, out byte[] data, out bool ext))
                return;

            if (!_bus.SendOnce(id, dlc, data, ext, false))
                MessageBox.Show("Failed to send message.", "TX Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void BtnPeriodic_Click(object sender, RoutedEventArgs e)
        {
            if (_periodicRunning)
            {
                StopPeriodic();
                return;
            }

            if (!_bus.IsConnected)
            {
                MessageBox.Show("Not connected to CAN hardware.", "TX Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParseSendInput(out uint id, out byte dlc, out byte[] data, out bool ext))
                return;

            if (!int.TryParse(txtInterval.Text, out int interval) || interval < 1)
            {
                MessageBox.Show("Interval must be at least 1 ms.", "Invalid interval",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _periodicTx.Start(_bus, interval, id, dlc, data, ext);
            _periodicRunning = true;
            btnPeriodic.Content = "Stop Periodic";
        }

        private void StopPeriodic()
        {
            _periodicTx.Stop();
            _bus.StopPeriodicFirmware();
            _periodicRunning = false;
            btnPeriodic.Content = "Periodic TX";
        }

        private byte GetSelectedDlc()
        {
            if (cmbSendDlc.SelectedItem is System.Windows.Controls.ComboBoxItem item &&
                byte.TryParse(item.Tag?.ToString(), out byte dlc))
                return dlc;
            return 8;
        }

        private bool TryParseSendInput(out uint id, out byte dlc, out byte[] data, out bool extended)
        {
            id = 0;
            dlc = GetSelectedDlc();
            data = new byte[8];
            extended = chkExtended.IsChecked == true;

            string idText = txtSendId.Text?.Trim().Replace("h", "").Replace("0x", "");
            if (!uint.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id))
            {
                MessageBox.Show("Invalid CAN ID.", "Input error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!hexSendData.TryGetBytes(dlc, out data, out string dataError))
            {
                MessageBox.Show(dataError, "Data error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (!_repository.IsOpen)
            {
                MessageBox.Show("No session data is available yet. Connect to the CAN bus first.",
                    "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                _repository.Flush();
                var win = new AnalysisWindow(_repository.DatabasePath, AnalysisSource.Database) { Owner = this };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open analysis:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (!_repository.IsOpen)
            {
                MessageBox.Show("No session data is available yet. Connect to the CAN bus first.",
                    "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                _repository.Flush();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not flush session data:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dialog = new ExportDialog { Owner = this };
            if (dialog.ShowDialog() != true)
                return;

            var dlg = new SaveFileDialog
            {
                Filter = "Text file|*.txt|All files|*.*",
                FileName = $"{_config.Title}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                DefaultExt = "txt"
            };
            if (dlg.ShowDialog() != true)
                return;

            try
            {
                _repository.ExportToText(dlg.FileName, dialog.TimeFromMs, dialog.TimeToMs);
                MessageBox.Show($"Exported to:\n{dlg.FileName}", "Export complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnOpenLogDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Directory.Exists(_config.LogDirectory))
                    Process.Start("explorer.exe", _config.LogDirectory);
                else
                    MessageBox.Show("Log directory does not exist yet.", "Not Found",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not open folder:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _bus.Disconnect();
                StartSession();
            }
            catch (Exception ex)
            {
                ShowConnectionFailed(ex.Message);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _isClosing = true;
            _bus.MessageReceived -= OnMessageReceived;
            _uiTimer.Stop();
            StopPeriodic();
            try { _bus.Dispose(); } catch { /* ignore */ }
            try { _repository.Dispose(); } catch { /* ignore */ }
            try { _periodicTx.Dispose(); } catch { /* ignore */ }
        }
    }
}
