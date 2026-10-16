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
using System.Threading.Tasks;
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
        private readonly Dictionary<LiveRowKey, LiveCanRow> _liveRows = new Dictionary<LiveRowKey, LiveCanRow>();
        private readonly ObservableCollection<LiveCanRow> _liveCollection = new ObservableCollection<LiveCanRow>();
        private readonly ObservableCollection<CanTransmitRow> _transmitRows = new ObservableCollection<CanTransmitRow>();
        private readonly DispatcherTimer _uiTimer;
        private long _totalMessages;
        private volatile bool _dirty;
        private volatile bool _isClosing;
        private volatile bool _loggingEnabled;
        private bool _teardownComplete;
        private CanBusStatus _busStatus = CanBusStatus.Ok;
        private SdoPacketWindow _sdoPacketWindow;

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
            _bus.StatusChanged += OnBusStatusText;
            _bus.BusStatusChanged += OnBusStatusChanged;
            _bus.Disconnected += OnBusDisconnected;
            _transmitManager.CyclicFaulted += OnCyclicFaulted;

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
            bool busOff = _busStatus.BlocksTransmit;
            bool transmitAllowed = !_config.ListenOnly && _bus.IsConnected && !busOff;

            gridSendControls.IsEnabled = transmitAllowed;

            if (_config.ListenOnly)
            {
                txtSendDisabledNotice.Text =
                    "Transmit disabled — session started in listen-only mode. Start a new session with listen-only unchecked to send.";
                txtSendDisabledNotice.Visibility = Visibility.Visible;
            }
            else if (!_bus.IsConnected)
            {
                txtSendDisabledNotice.Text = "Transmit disabled — not connected to CAN hardware.";
                txtSendDisabledNotice.Visibility = Visibility.Visible;
            }
            else if (busOff)
            {
                // The controller has left the bus: writes would be queued but never sent.
                txtSendDisabledNotice.Text = "Transmit disabled — controller is bus off. Use Reset CAN Controller to rejoin the bus.";
                txtSendDisabledNotice.Visibility = Visibility.Visible;
            }
            else
            {
                txtSendDisabledNotice.Visibility = Visibility.Collapsed;
            }

            btnResetController.Visibility = busOff && _bus.IsConnected ? Visibility.Visible : Visibility.Collapsed;
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

                // The channel was (re)opened, so its timestamp base restarted.
                ResetLivePeriodStatistics(resetCounts: false);

                var header = CanMessageHelper.BuildSessionHeader(
                    _config, _bus.HardwareInfo, _bus.ChannelInfo);
                _repository.Open(_config, header);
                _loggingEnabled = true;

                txtConnectionStatus.Text = "Connected — logging";
                txtConnectionStatus.Foreground = System.Windows.Media.Brushes.Green;
                txtLogPath.Text = Path.GetFileName(_repository.DatabasePath);
                btnReconnect.Visibility = Visibility.Collapsed;
                _busStatus = CanBusStatus.Ok;
                txtBusError.Text = string.Empty;
                txtBusError.Visibility = Visibility.Collapsed;
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
            _busStatus = CanBusStatus.Ok;
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

            // TX echoes and received frames with the same identifier are different traffic:
            // sharing a row made DIR flicker and mixed two unrelated cadences into PERIOD.
            // Standard 123h and extended 00000123h are different identifiers too.
            var key = new LiveRowKey(record.CanId, record.IsExtended, record.Direction == "TX");

            lock (_liveRows)
            {
                if (!_liveRows.TryGetValue(key, out var row))
                {
                    row = new LiveCanRow
                    {
                        CanId = record.CanId,
                        CanIdDisplay = record.CanIdDisplay,
                        IsExtended = record.IsExtended
                    };
                    _liveRows[key] = row;
                    RunOnUiThread(() =>
                    {
                        if (!_isClosing)
                            _liveCollection.Add(row);
                    });
                }

                row.Direction = record.Direction;
                UpdatePeriodStatistics(row, record.TimestampMs);
                row.TimeDisplay = CanMessageHelper.FormatTime(row.TimestampMs);
                row.Dlc = record.Dlc;
                row.Data = record.DataDisplay;
                row.MessageCount++;
            }
            _dirty = true;
        }

        /// <summary>
        /// Cycle time from consecutive hardware timestamps of the same identifier and
        /// direction. Receive thread, under the <see cref="_liveRows"/> lock.
        /// </summary>
        private static void UpdatePeriodStatistics(LiveCanRow row, double timestampMs)
        {
            if (!row.HasPeriodBaseline)
            {
                row.HasPeriodBaseline = true;
                row.PeriodStartMs = timestampMs;
                row.TimestampMs = timestampMs;
                return;
            }

            double periodMs = timestampMs - row.TimestampMs;
            if (periodMs < 0)
            {
                // A frame stamped before its predecessor (echo ordering): it says nothing
                // about this identifier's cadence, and must not reset the baseline.
                return;
            }

            row.PeriodSamples++;
            if (row.PeriodSamples == 1 || periodMs < row.MinPeriodMs)
                row.MinPeriodMs = periodMs;
            if (row.PeriodSamples == 1 || periodMs > row.MaxPeriodMs)
                row.MaxPeriodMs = periodMs;

            row.TimestampMs = timestampMs;

            // Mean over the whole run, (last - first) / intervals: exact, and immune to the
            // rounding a running sum of deltas would accumulate.
            double avgMs = (timestampMs - row.PeriodStartMs) / row.PeriodSamples;

            row.PeriodDisplay = CanMessageHelper.FormatTime(periodMs);
            row.AvgPeriodDisplay = avgMs.ToString("F2");
            row.MinPeriodDisplay = CanMessageHelper.FormatTime(row.MinPeriodMs);
            row.MaxPeriodDisplay = CanMessageHelper.FormatTime(row.MaxPeriodMs);
        }

        /// <summary>
        /// Starts period measurement afresh. Needed whenever the hardware timestamp base may
        /// have changed (reconnect, controller reset), otherwise the first period spans the gap.
        /// </summary>
        private void ResetLivePeriodStatistics(bool resetCounts)
        {
            lock (_liveRows)
            {
                foreach (var row in _liveRows.Values)
                {
                    row.ResetPeriodStatistics();
                    if (resetCounts)
                        row.MessageCount = 0;
                }
            }
            _dirty = true;
        }

        private void BtnResetLiveStats_Click(object sender, RoutedEventArgs e)
        {
            ResetLivePeriodStatistics(resetCounts: true);
            txtStatus.Text = "Live period statistics and counters reset.";
        }

        private void OnBusStatusText(string status)
        {
            RunOnUiThread(() =>
            {
                if (!_isClosing)
                    txtStatus.Text = status;
            });
        }

        private void OnBusDisconnected()
        {
            RunOnUiThread(OnDisconnected);
        }

        private void OnBusStatusChanged(CanBusStatus status)
        {
            RunOnUiThread(() =>
            {
                if (_isClosing)
                    return;

                var previous = _busStatus;
                _busStatus = status ?? CanBusStatus.Ok;

                if (!_busStatus.IsError)
                {
                    txtBusError.Text = string.Empty;
                    txtBusError.Visibility = Visibility.Collapsed;
                }
                else
                {
                    txtBusError.Text = "CAN Error: " + _busStatus.Message;
                    txtBusError.Visibility = Visibility.Visible;
                }

                // Entering bus-off stops cyclic TX: the frames would never reach the wire.
                if (_busStatus.BlocksTransmit && !previous.BlocksTransmit)
                {
                    AppLog.Warn("Bus off on device {0} CH{1}; stopping cyclic transmit.",
                        _config.DeviceNumber, _config.Channel);
                    StopAllCyclicInternal();
                }

                UpdateSendPanelState();
            });
        }

        private void OnCyclicFaulted(Guid rowId, string reason)
        {
            RunOnUiThread(() =>
            {
                if (_isClosing)
                    return;

                foreach (var row in _transmitRows)
                {
                    if (row.Id == rowId)
                    {
                        row.IsCyclicRunning = false;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(reason))
                    txtStatus.Text = reason;
            });
        }

        private void BtnResetController_Click(object sender, RoutedEventArgs e)
        {
            if (!_bus.ResetController(out string error))
            {
                MessageBox.Show(error ?? "Could not reset the CAN controller.", "Reset",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _busStatus = CanBusStatus.Ok;
            txtBusError.Text = string.Empty;
            txtBusError.Visibility = Visibility.Collapsed;
            ResetLivePeriodStatistics(resetCounts: false);
            UpdateSendPanelState();
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

            // A 64-bit read is not atomic in this 32-bit process.
            long total = System.Threading.Interlocked.Read(ref _totalMessages);
            long saved = _repository.IsOpen ? _repository.TotalWritten : 0;
            long transfers = _repository.IsOpen ? _repository.TotalTransfersWritten : 0;
            long dropped = _repository.TotalDropped;
            txtLiveStats.Text = dropped > 0
                ? $"{_liveCollection.Count} rows · {total:N0} total · {saved:N0} saved · {transfers:N0} SDO packets · {dropped:N0} DROPPED"
                : $"{_liveCollection.Count} rows · {total:N0} total · {saved:N0} saved · {transfers:N0} SDO packets";

            string lastTransfer = _repository.LastTransferSummary;
            txtLiveStats.ToolTip = lastTransfer == null
                ? $"{_repository.TotalSdoWritten:N0} SDO frames saved; no complete SDO packet yet."
                : $"{_repository.TotalSdoWritten:N0} SDO frames saved.\nLast SDO packet: {lastTransfer}";
        }

        private void OnDisconnected()
        {
            _loggingEnabled = false;
            _busStatus = CanBusStatus.Ok;
            AppLog.Warn("CAN monitor disconnected from hardware.");
            txtConnectionStatus.Text = "Disconnected";
            txtConnectionStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            btnReconnect.Visibility = Visibility.Visible;
            txtBusError.Text = string.Empty;
            txtBusError.Visibility = Visibility.Collapsed;
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

        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (!_repository.IsOpen)
            {
                MessageBox.Show("No session data is available yet. Connect to the CAN bus first.",
                    "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                await Task.Run(() => _repository.Flush());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not flush session data:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            AnalysisWindow.Open(this, _repository.DatabasePath, AnalysisSource.Database);
        }

        private void BtnSdoPackets_Click(object sender, RoutedEventArgs e)
        {
            if (!_repository.IsOpen)
            {
                MessageBox.Show("No session data is available yet. Connect to the CAN bus first.",
                    "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // One live packet view per session; it closes with this window.
            if (_sdoPacketWindow != null && _sdoPacketWindow.IsLoaded)
            {
                if (_sdoPacketWindow.WindowState == WindowState.Minimized)
                    _sdoPacketWindow.WindowState = WindowState.Normal;
                _sdoPacketWindow.Activate();
                return;
            }

            _sdoPacketWindow = SdoPacketWindow.ShowLive(this, _repository.DatabasePath, _config.Title);
        }

        private async void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (!_repository.IsOpen)
            {
                MessageBox.Show("No session data is available yet. Connect to the CAN bus first.",
                    "No Data", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                await Task.Run(() => _repository.Flush());
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

            string target = dlg.FileName;
            double? from_ = dialog.TimeFromMs;
            double? to = dialog.TimeToMs;

            btnExport.IsEnabled = false;
            txtStatus.Text = "Exporting…";
            try
            {
                // Streams row by row, so a long session can take a while — keep the UI alive.
                await Task.Run(() => _repository.ExportToText(target, from_, to));
                txtStatus.Text = $"Exported to {Path.GetFileName(target)}.";
                MessageBox.Show($"Exported to:\n{target}", "Export complete",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Session export failed.");
                txtStatus.Text = "Export failed.";
                MessageBox.Show($"Export failed:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (!_isClosing)
                    btnExport.IsEnabled = true;
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

        private async void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            btnReconnect.IsEnabled = false;
            txtStatus.Text = "Reconnecting…";
            try
            {
                // Ensure no TX continues across reconnect. Releasing the lease joins the
                // receive thread and waits on the driver, so it does not belong on the UI thread.
                StopAllCyclicInternal();
                await Task.Run(() => _bus.Disconnect());
                StartSession();
            }
            catch (Exception ex)
            {
                ShowConnectionFailed(ex.Message);
            }
            finally
            {
                if (!_isClosing)
                    btnReconnect.IsEnabled = true;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_teardownComplete)
                return;

            // Releasing the session means joining transmit threads, a 200 ms driver shutdown
            // and a SQLite drain. Doing that inline froze the UI for as long as it took, so
            // the close is deferred until a background teardown finishes.
            e.Cancel = true;

            if (_isClosing)
                return;

            _isClosing = true;
            _loggingEnabled = false;

            try { _uiTimer.Stop(); } catch { /* ignore */ }

            // Detach callbacks first so nothing arrives mid-teardown.
            try { _bus.MessageReceived -= OnMessageReceived; } catch { /* ignore */ }
            try { _bus.BusStatusChanged -= OnBusStatusChanged; } catch { /* ignore */ }
            try { _bus.StatusChanged -= OnBusStatusText; } catch { /* ignore */ }
            try { _bus.Disconnected -= OnBusDisconnected; } catch { /* ignore */ }
            try { _transmitManager.CyclicFaulted -= OnCyclicFaulted; } catch { /* ignore */ }
            try { _transmitRows.CollectionChanged -= TransmitRows_CollectionChanged; } catch { /* ignore */ }
            foreach (var row in _transmitRows)
            {
                try { row.PropertyChanged -= TransmitRow_PropertyChanged; } catch { /* ignore */ }
            }

            txtConnectionStatus.Text = "Closing session…";
            txtStatus.Text = "Stopping transmit, releasing hardware and flushing the log…";
            IsEnabled = false;

            Task.Run((Action)TeardownSession).ContinueWith(_ =>
            {
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        _teardownComplete = true;
                        Close();
                    }));
                }
                catch (Exception ex)
                {
                    // The dispatcher is already gone — the app is shutting down anyway, and
                    // App.OnExit releases the hardware.
                    AppLog.Warn(ex, "Could not complete the deferred window close.");
                }
            });
        }

        /// <summary>Runs on a background thread; must never throw.</summary>
        private void TeardownSession()
        {
            // 1) Stop all software cyclic TX threads before touching the bus. Only the
            //    manager is touched here — the bound row collection belongs to the UI thread.
            try { _transmitManager.StopAll(); } catch (Exception ex) { AppLog.Warn(ex, "Stopping cyclic TX failed."); }

            // 2) Disconnect CAN: stops firmware cyclic + releases channel/hardware lease.
            try { _bus.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Releasing the CAN lease failed."); }

            // 3) Flush and close SQLite so no writer thread remains.
            try { _repository.Flush(); } catch (Exception ex) { AppLog.Warn(ex, "Final session flush failed."); }
            try { _repository.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Closing the session database failed."); }

            try { _transmitManager.Dispose(); } catch { /* ignore */ }

            AppLog.Info("Monitor window closed; CAN session resources released.");
        }

        /// <summary>Identifies a live row. TX echoes are tracked separately from RX frames.</summary>
        private readonly struct LiveRowKey : IEquatable<LiveRowKey>
        {
            private readonly uint _canId;
            private readonly bool _isExtended;
            private readonly bool _isTx;

            public LiveRowKey(uint canId, bool isExtended, bool isTx)
            {
                _canId = canId;
                _isExtended = isExtended;
                _isTx = isTx;
            }

            public bool Equals(LiveRowKey other) =>
                _canId == other._canId && _isExtended == other._isExtended && _isTx == other._isTx;
            public override bool Equals(object obj) => obj is LiveRowKey other && Equals(other);
            public override int GetHashCode() =>
                unchecked((int)_canId * 4 + (_isExtended ? 2 : 0) + (_isTx ? 1 : 0));
        }
    }
}
