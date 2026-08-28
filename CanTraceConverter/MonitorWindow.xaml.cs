using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        private readonly TransmitListManager _transmitManager = new TransmitListManager();
        private readonly Dictionary<uint, LiveCanRow> _liveRows = new Dictionary<uint, LiveCanRow>();
        private readonly ObservableCollection<LiveCanRow> _liveCollection = new ObservableCollection<LiveCanRow>();
        private readonly ObservableCollection<CanTransmitRow> _transmitRows = new ObservableCollection<CanTransmitRow>();
        private readonly DispatcherTimer _uiTimer;
        private long _totalMessages;
        private bool _dirty;
        private bool _isClosing;
        private bool _loggingEnabled;

        public MonitorWindow(SessionConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            InitializeComponent();

            dgLive.ItemsSource = _liveCollection;
            dgTransmit.ItemsSource = _transmitRows;
            _transmitRows.CollectionChanged += TransmitRows_CollectionChanged;
            _transmitManager.AttachBus(_bus);
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

        private void TransmitRows_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems == null)
                return;

            foreach (CanTransmitRow row in e.NewItems)
                row.PropertyChanged += TransmitRow_PropertyChanged;
        }

        private void TransmitRow_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is CanTransmitRow row &&
                e.PropertyName == nameof(CanTransmitRow.CycleEnabled) &&
                !row.CycleEnabled &&
                row.IsCyclicRunning)
            {
                _transmitManager.StopCyclic(row);
            }
        }

        private void DgTransmit_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindVisualParent<Button>(e.OriginalSource as DependencyObject) != null)
                return;

            var cell = FindVisualParent<DataGridCell>(e.OriginalSource as DependencyObject);
            if (cell == null || cell.IsReadOnly || cell.Column is DataGridTemplateColumn)
                return;

            if (!cell.IsFocused)
                cell.Focus();

            if (!cell.IsEditing)
                dgTransmit.BeginEdit(e);
        }

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T match)
                    return match;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
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
                AppLog.Error(ex, "Monitor session start failed.");
                ShowConnectionFailed(ex.Message);
            }
        }

        private void ShowConnectionFailed(string message)
        {
            _loggingEnabled = false;
            AppLog.Warn("Monitor connection failed: {0}", message);
            txtConnectionStatus.Text = "Not connected";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Red;
            txtStatus.Text = message;
            btnReconnect.Visibility = Visibility.Visible;
            UpdateSendPanelState();
            StopAllCyclicInternal();

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
            AppLog.Warn("CAN monitor disconnected from hardware.");
            txtConnectionStatus.Text = "Disconnected";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            btnReconnect.Visibility = Visibility.Visible;
            UpdateSendPanelState();
            StopAllCyclicInternal();
        }

        private void StopAllCyclicInternal()
        {
            _transmitManager.StopAll();
            foreach (var row in _transmitRows)
                row.IsCyclicRunning = false;
        }

        private void ShowTransmitError(string error)
        {
            if (!string.IsNullOrEmpty(error))
            {
                AppLog.Warn("Transmit error: {0}", error);
                MessageBox.Show(error, "Transmit", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private CanTransmitRow GetRowFromSender(object sender)
        {
            if (sender is Button button && button.DataContext is CanTransmitRow row)
                return row;
            return null;
        }

        private void BtnRowSend_Click(object sender, RoutedEventArgs e)
        {
            var row = GetRowFromSender(sender);
            if (row == null)
                return;

            CommitTransmitGridEdits();

            if (row.CycleEnabled)
            {
                if (row.IsCyclicRunning)
                {
                    _transmitManager.StopCyclic(row);
                    return;
                }

                if (!_transmitManager.TryStartCyclic(row, out string cyclicError))
                    ShowTransmitError(cyclicError);
                return;
            }

            if (!_transmitManager.TrySendOnce(row, out string error))
                ShowTransmitError(error);
        }

        private void BtnTxAdd_Click(object sender, RoutedEventArgs e)
        {
            _transmitRows.Add(new CanTransmitRow());
        }

        private void BtnTxRemove_Click(object sender, RoutedEventArgs e)
        {
            if (dgTransmit.SelectedItem is CanTransmitRow row)
            {
                row.PropertyChanged -= TransmitRow_PropertyChanged;
                _transmitManager.StopCyclic(row);
                _transmitRows.Remove(row);
            }
        }

        private void BtnTxDuplicate_Click(object sender, RoutedEventArgs e)
        {
            if (dgTransmit.SelectedItem is CanTransmitRow selected)
                _transmitRows.Add(selected.Clone());
            else if (_transmitRows.Count > 0)
                _transmitRows.Add(_transmitRows[_transmitRows.Count - 1].Clone());
        }

        private void BtnTxSendAllOnce_Click(object sender, RoutedEventArgs e)
        {
            CommitTransmitGridEdits();

            var onceRows = _transmitRows.Where(r => !r.CycleEnabled).ToList();
            if (onceRows.Count == 0)
            {
                MessageBox.Show("No rows with Cycle unchecked. Uncheck Cycle on rows you want to send once, or use Start All Cyclic.",
                    "Send All Once", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int sent = 0;
            var errors = new List<string>();
            foreach (var row in onceRows)
            {
                if (_transmitManager.TrySendOnce(row, out string error))
                    sent++;
                else if (!string.IsNullOrEmpty(error))
                    errors.Add($"ID {row.CanIdHex}: {error}");
            }

            if (errors.Count > 0)
            {
                MessageBox.Show(
                    $"Sent {sent} of {onceRows.Count} message(s)." + Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine, errors.Take(5)) +
                    (errors.Count > 5 ? Environment.NewLine + "..." : string.Empty),
                    "Send All Once",
                    MessageBoxButton.OK,
                    errors.Count == onceRows.Count ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            else if (sent > 0)
            {
                txtStatus.Text = $"Sent {sent} message(s) once.";
            }
        }

        private void BtnTxStartAllCyclic_Click(object sender, RoutedEventArgs e)
        {
            CommitTransmitGridEdits();

            int started = 0;
            var errors = new List<string>();
            foreach (var row in _transmitRows.Where(r => r.CycleEnabled))
            {
                if (row.IsCyclicRunning)
                {
                    started++;
                    continue;
                }

                if (_transmitManager.TryStartCyclic(row, out string error))
                    started++;
                else if (!string.IsNullOrEmpty(error))
                    errors.Add($"ID {row.CanIdHex}: {error}");
            }

            if (_transmitRows.All(r => !r.CycleEnabled))
            {
                MessageBox.Show("No rows have Cycle checked. Enable Cycle on rows to include them in Start All Cyclic.",
                    "Start All Cyclic", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (errors.Count > 0)
            {
                MessageBox.Show(
                    $"Started {started} cyclic transmitter(s)." + Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine, errors.Take(5)),
                    "Start All Cyclic",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                txtStatus.Text = $"Cyclic TX running on {started} message(s).";
            }
        }

        private void BtnTxStopAllCyclic_Click(object sender, RoutedEventArgs e)
        {
            StopAllCyclicInternal();
            txtStatus.Text = "All cyclic transmitters stopped.";
        }

        private void CommitTransmitGridEdits()
        {
            dgTransmit.CommitEdit(DataGridEditingUnit.Cell, true);
            dgTransmit.CommitEdit(DataGridEditingUnit.Row, true);
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
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not flush session data:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AnalysisWindow.Open(this, _repository.DatabasePath, AnalysisSource.Database);
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
                StopAllCyclicInternal();
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
            StopAllCyclicInternal();
            try { _bus.Dispose(); } catch { /* ignore */ }
            try { _repository.Dispose(); } catch { /* ignore */ }
            try { _transmitManager.Dispose(); } catch { /* ignore */ }
        }
    }
}
