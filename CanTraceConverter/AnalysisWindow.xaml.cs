using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CanTraceConverter.Models;
using CanTraceConverter.Services;

namespace CanTraceConverter
{
    public enum AnalysisSource
    {
        ConvertedText,
        TextFile,
        Database
    }

    public partial class AnalysisWindow : Window
    {
        private readonly DispatcherTimer _filterTimer;
        private List<CanMessage> _allMessages = new List<CanMessage>();
        private List<CanMessage> _filteredMessages = new List<CanMessage>();
        private Dictionary<string, List<CanMessage>> _messagesByCanId = new Dictionary<string, List<CanMessage>>();
        private CanTraceLoadResult _loadResult;
        private string _sourceInput;
        private AnalysisSource _sourceKind;
        private string _currentViewMode = "All Messages";
        private bool _isLoading;
        private bool _suppressFilterEvents;

        public AnalysisWindow(string source, AnalysisSource kind = AnalysisSource.ConvertedText)
        {
            // XAML CheckBox/ComboBox defaults fire Checked/SelectionChanged during
            // InitializeComponent. Build the timer and suppress those events first.
            _sourceInput = source;
            _sourceKind = kind;
            _isLoading = true;
            _suppressFilterEvents = true;

            _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _filterTimer.Tick += FilterTimer_Tick;

            InitializeComponent();
            Closed += AnalysisWindow_Closed;
            LoadData(source, kind);
        }

        /// <summary>
        /// Opens the analysis window from any caller (converter, launcher, monitor).
        /// Construction-time XAML events are handled inside the constructor.
        /// </summary>
        public static void Open(Window owner, string source, AnalysisSource kind = AnalysisSource.ConvertedText)
        {
            try
            {
                var window = new AnalysisWindow(source, kind);
                if (owner != null)
                    window.Owner = owner;
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Could not open analysis window.");
                MessageBox.Show($"Could not open analysis:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AnalysisWindow_Closed(object sender, EventArgs e)
        {
            _filterTimer.Stop();
            _filterTimer.Tick -= FilterTimer_Tick;
        }

        private void LoadData(string source, AnalysisSource kind)
        {
            try
            {
                _isLoading = true;
                _suppressFilterEvents = true;

                var loaderKind = MapSourceKind(kind);
                _loadResult = CanTraceLoader.Load(source, loaderKind);
                _allMessages = _loadResult.Messages ?? new List<CanMessage>();
                _messagesByCanId = CanTraceAnalyzer.IndexByCanId(_allMessages);
                _filteredMessages = new List<CanMessage>(_allMessages);

                txtSourceInfo.Text = BuildSourceDescription();
                txtSourceBadge.Text = _loadResult.SourceLabel;
                txtFileInfo.Text = _loadResult.HeaderText ?? string.Empty;
                btnReload.Visibility = _loadResult.SourceKind == AnalysisSourceKind.Database
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                RefreshUniqueIdList();
                ApplyFiltersInternal(refreshView: false);
                RefreshCurrentView();
                UpdateStatistics();
                UpdateMessageCount();

                txtStatus.Text = $"Loaded {_allMessages.Count:N0} messages — {_loadResult.SourceLabel}";
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Failed to load analysis data.");
                MessageBox.Show($"Error loading analysis data:\n{ex.Message}", "Load Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                if (txtStatus != null)
                    txtStatus.Text = "Failed to load data";
            }
            finally
            {
                _isLoading = false;
                _suppressFilterEvents = false;
            }
        }

        private static AnalysisSourceKind MapSourceKind(AnalysisSource kind)
        {
            switch (kind)
            {
                case AnalysisSource.Database:
                    return AnalysisSourceKind.Database;
                case AnalysisSource.TextFile:
                    return AnalysisSourceKind.TextFile;
                default:
                    return AnalysisSourceKind.ConvertedText;
            }
        }

        private string BuildSourceDescription()
        {
            if (!string.IsNullOrWhiteSpace(_loadResult?.SourcePath))
                return $"{_loadResult.SourceLabel} — {_loadResult.SourcePath}";
            return _loadResult?.SourceLabel ?? "Converted trace text";
        }

        private CanTraceFilterCriteria BuildFilterCriteria()
        {
            return new CanTraceFilterCriteria
            {
                QuickSearch = txtQuickSearch?.Text,
                Direction = GetComboText(cmbDirection, "All"),
                CanIdContains = txtCanIdFilter?.Text,
                Dlc = GetComboText(cmbDlcFilter, "All"),
                TimeFrom = txtTimeFrom?.Text,
                TimeTo = txtTimeTo?.Text,
                DataPattern = txtDataPattern?.Text,
                SourceIdContains = txtSourceIdFilter?.Text,
                FunctionCodeByteIndex = cmbFunctionCodeByte?.SelectedIndex ?? 0,
                FunctionCodeHex = txtFunctionCode?.Text,
                ExtendedOnly = chkExtendedOnly?.IsChecked == true ? (bool?)true : null,
                HideRtr = chkHideRtr?.IsChecked == true
            };
        }

        private static string GetComboText(ComboBox combo, string fallback)
        {
            if (combo?.SelectedItem is ComboBoxItem item && item.Content != null)
                return item.Content.ToString();
            return fallback;
        }

        private void ApplyFiltersInternal(bool refreshView = true)
        {
            if (_isLoading || _allMessages == null)
                return;

            _filteredMessages = CanTraceAnalyzer.ApplyFilters(_allMessages, BuildFilterCriteria());
            RefreshUniqueIdList();

            if (refreshView)
                RefreshCurrentView();

            UpdateStatistics();
            UpdateMessageCount();
        }

        private void RefreshUniqueIdList()
        {
            if (lstUniqueIds == null)
                return;

            var idList = CanTraceAnalyzer.BuildCanIdList(_filteredMessages);
            lstUniqueIds.ItemsSource = idList;
        }

        private void UpdateStatistics()
        {
            if (txtStatistics == null)
                return;

            var stats = CanTraceAnalyzer.ComputeStatistics(_allMessages, _filteredMessages);
            txtStatistics.Text = stats.ToDisplayText();
        }

        private void UpdateMessageCount()
        {
            if (txtMessageCount == null)
                return;

            txtMessageCount.Text = $"{_filteredMessages.Count:N0} messages match filters (of {_allMessages.Count:N0} total)";
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            if (_suppressFilterEvents || _isLoading || _filterTimer == null)
                return;

            if (chkAutoApply?.IsChecked == true)
            {
                RestartFilterTimer();
                if (txtStatus != null)
                    txtStatus.Text = "Updating filters...";
            }
            else if (txtStatus != null)
            {
                txtStatus.Text = "Filters modified — click Apply Filters";
            }
        }

        private void FilterTimer_Tick(object sender, EventArgs e)
        {
            _filterTimer.Stop();
            ApplyFiltersInternal();
            if (txtStatus != null)
                txtStatus.Text = $"{_filteredMessages.Count:N0} messages match filters";
        }

        private void RestartFilterTimer()
        {
            _filterTimer.Stop();
            _filterTimer.Start();
        }

        private void btnApplyFilters_Click(object sender, RoutedEventArgs e)
        {
            ApplyFiltersInternal();
            txtStatus.Text = $"{_filteredMessages.Count:N0} messages match filters";
        }

        private void btnClearFilters_Click(object sender, RoutedEventArgs e)
        {
            _suppressFilterEvents = true;
            try
            {
                txtQuickSearch.Clear();
                cmbDirection.SelectedIndex = 0;
                txtCanIdFilter.Clear();
                cmbDlcFilter.SelectedIndex = 0;
                txtTimeFrom.Clear();
                txtTimeTo.Clear();
                txtDataPattern.Clear();
                txtSourceIdFilter.Clear();
                txtFunctionCode.Clear();
                cmbFunctionCodeByte.SelectedIndex = 0;
                chkExtendedOnly.IsChecked = false;
                chkHideRtr.IsChecked = false;
            }
            finally
            {
                _suppressFilterEvents = false;
            }

            ApplyFiltersInternal();
            txtStatus.Text = "All filters cleared";
        }

        private void btnClearCanId_Click(object sender, RoutedEventArgs e)
        {
            txtCanIdFilter.Clear();
        }

        private void lstUniqueIds_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Single-click highlights only; double-click applies filter.
        }

        private void lstUniqueIds_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (lstUniqueIds.SelectedItem is CanIdListItem item)
            {
                txtCanIdFilter.Text = item.CanId;
                ApplyFiltersInternal();
                txtStatus.Text = $"Filtered to CAN ID {item.CanId}";
            }
        }

        private void cmbViewMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressFilterEvents || _isLoading || cmbViewMode?.SelectedItem == null)
                return;

            _currentViewMode = GetComboText(cmbViewMode, "All Messages");
            if (panelTimelineInterval != null)
            {
                panelTimelineInterval.Visibility = _currentViewMode == "Message Timeline"
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            RefreshCurrentView();
        }

        private void txtTimelineInterval_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressFilterEvents || _isLoading || _filterTimer == null || _currentViewMode != "Message Timeline")
                return;

            if (chkAutoApply?.IsChecked == true)
                RestartFilterTimer();
            else
                RefreshCurrentView();
        }

        private void RefreshCurrentView()
        {
            if (dgMessages == null || _filteredMessages == null)
                return;

            panelMessageDetail.Visibility = Visibility.Collapsed;

            switch (_currentViewMode)
            {
                case "Latest per CAN ID":
                    ShowLatestPerId();
                    break;
                case "Unique CAN IDs":
                    ShowUniqueCanIds();
                    break;
                case "Data Changes":
                    ShowDataChanges();
                    break;
                case "Inter-message Gaps":
                    ShowGapAnalysis();
                    break;
                case "Message Timeline":
                    ShowMessageTimeline();
                    break;
                case "Frequency Analysis":
                    ShowFrequencyAnalysis();
                    break;
                default:
                    ShowAllMessages();
                    break;
            }
        }

        private void ShowAllMessages()
        {
            SetMessageColumns(
                Col("DIR", "Direction", 60),
                Col("TIME (msec)", "Time", 100),
                Col("CAN ID", "CanId", 110),
                Col("DLC", "Dlc", 50),
                Col("DATA", "Data", star: true));

            dgMessages.ItemsSource = _filteredMessages;
            txtMessageCount.Text = $"{_filteredMessages.Count:N0} messages displayed (of {_allMessages.Count:N0} total)";
        }

        private void ShowLatestPerId()
        {
            SetMessageColumns(
                Col("DIR", "Direction", 60),
                Col("TIME (msec)", "Time", 100),
                Col("CAN ID", "CanId", 110),
                Col("DLC", "Dlc", 50),
                Col("DATA", "Data", star: true));

            var latest = CanTraceAnalyzer.BuildLatestPerId(_filteredMessages);
            dgMessages.ItemsSource = latest;
            txtMessageCount.Text = $"{latest.Count:N0} CAN IDs (latest message each)";
        }

        private void ShowUniqueCanIds()
        {
            SetSummaryColumns();
            var summaries = CanTraceAnalyzer.BuildUniqueSummaries(_filteredMessages);
            dgMessages.ItemsSource = summaries;
            txtMessageCount.Text = $"{summaries.Count:N0} unique CAN IDs";
        }

        private void ShowDataChanges()
        {
            dgMessages.Columns.Clear();
            dgMessages.Columns.Add(Col("CAN ID", "CanId", 110));
            dgMessages.Columns.Add(Col("TIME", "Time", 100));
            dgMessages.Columns.Add(Col("DIR", "Direction", 60));
            dgMessages.Columns.Add(Col("PREVIOUS DATA", "PreviousData", 220));
            dgMessages.Columns.Add(Col("NEW DATA", "NewData", star: true));

            var changes = CanTraceAnalyzer.BuildDataChanges(_filteredMessages);
            dgMessages.ItemsSource = changes;
            txtMessageCount.Text = $"{changes.Count:N0} data change events";
        }

        private void ShowGapAnalysis()
        {
            dgMessages.Columns.Clear();
            dgMessages.Columns.Add(Col("CAN ID", "CanId", 110));
            dgMessages.Columns.Add(Col("DIR", "Direction", 60));
            dgMessages.Columns.Add(Col("MSGS", "MessageCount", 70));
            dgMessages.Columns.Add(Col("MIN GAP", "MinGapDisplay", 100));
            dgMessages.Columns.Add(Col("AVG GAP", "AvgGapDisplay", 100));
            dgMessages.Columns.Add(Col("MAX GAP", "MaxGapDisplay", 100));
            dgMessages.Columns.Add(Col("JITTER (σ)", "JitterDisplay", 100));
            dgMessages.Columns.Add(Col("EST. RATE", "EstHzDisplay", 100));

            var gaps = CanTraceAnalyzer.BuildGapAnalysis(_filteredMessages);
            dgMessages.ItemsSource = gaps;
            txtMessageCount.Text = $"{gaps.Count:N0} CAN ID / direction streams with timing data";
        }

        private void ShowMessageTimeline()
        {
            dgMessages.Columns.Clear();
            dgMessages.Columns.Add(Col("TIME RANGE", "TimeRange", 220));
            dgMessages.Columns.Add(Col("MESSAGES", "MessageCount", 90));
            dgMessages.Columns.Add(Col("UNIQUE IDs", "UniqueIds", 90));
            dgMessages.Columns.Add(Col("RX", "RxCount", 70));
            dgMessages.Columns.Add(Col("TX", "TxCount", 70));

            var interval = ParseTimelineInterval();
            var timeline = CanTraceAnalyzer.BuildTimeline(_filteredMessages, interval);
            dgMessages.ItemsSource = timeline;
            txtMessageCount.Text = $"{timeline.Count:N0} time buckets ({interval:N0} ms each)";
        }

        private void ShowFrequencyAnalysis()
        {
            dgMessages.Columns.Clear();
            dgMessages.Columns.Add(Col("CAN ID", "CanId", 110));
            dgMessages.Columns.Add(Col("COUNT", "TotalCount", 80));
            dgMessages.Columns.Add(Col("%", "PercentageDisplay", 80));
            dgMessages.Columns.Add(Col("FREQ (Hz)", "FrequencyDisplay", 100));
            dgMessages.Columns.Add(Col("DIR", "Direction", 70));

            var frequency = CanTraceAnalyzer.BuildFrequencyAnalysis(_filteredMessages, _filteredMessages.Count);
            dgMessages.ItemsSource = frequency;
            txtMessageCount.Text = $"{frequency.Count:N0} CAN IDs analyzed";
        }

        private byte GetSdoClientNodeId()
        {
            if (_loadResult?.SessionInfo != null &&
                _loadResult.SessionInfo.TryGetValue("sdo_client_node_id", out var text) &&
                byte.TryParse(text, out byte node) && node <= 127)
            {
                return node;
            }
            return 0;
        }

        private double ParseTimelineInterval()
        {
            if (double.TryParse(txtTimelineInterval?.Text?.Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double interval) && interval > 0)
                return interval;
            return 100;
        }

        private void SetMessageColumns(params DataGridTextColumn[] columns)
        {
            dgMessages.Columns.Clear();
            foreach (var col in columns)
                dgMessages.Columns.Add(col);
        }

        private void SetSummaryColumns()
        {
            dgMessages.Columns.Clear();
            dgMessages.Columns.Add(Col("CAN ID", "CanId", 110));
            dgMessages.Columns.Add(Col("COUNT", "Count", 70));
            dgMessages.Columns.Add(Col("DIR", "Direction", 70));
            dgMessages.Columns.Add(Col("DLC", "Dlc", 50));
            dgMessages.Columns.Add(Col("FIRST", "FirstTime", 100));
            dgMessages.Columns.Add(Col("LAST", "LastTime", 100));
            dgMessages.Columns.Add(Col("AVG INTERVAL", "AvgIntervalDisplay", 110));
        }

        private static DataGridTextColumn Col(string header, string binding, double width = double.NaN, bool star = false)
        {
            var col = new DataGridTextColumn
            {
                Header = header,
                Binding = new System.Windows.Data.Binding(binding)
            };
            col.Width = star
                ? new DataGridLength(1, DataGridLengthUnitType.Star)
                : new DataGridLength(width);
            return col;
        }

        private void dgMessages_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dgMessages.SelectedItem is CanMessage msg)
            {
                panelMessageDetail.Visibility = Visibility.Visible;
                var wall = msg.WallTime.HasValue ? msg.WallTime.Value.ToString("yyyy-MM-dd HH:mm:ss.fff") : "—";
                txtMessageDetail.Text =
                    $"{msg.Direction}  |  {msg.Time} ms  |  {msg.CanId}  |  DLC {msg.Dlc}" + Environment.NewLine +
                    $"Data: {msg.Data}" + Environment.NewLine +
                    $"Extended: {(msg.IsExtended ? "Yes" : "No")}  |  RTR: {(msg.IsRtr ? "Yes" : "No")}  |  Wall time: {wall}" + Environment.NewLine +
                    $"Raw: {msg.RawLine}";
            }
            else
            {
                panelMessageDetail.Visibility = Visibility.Collapsed;
            }
        }

        private void btnExportFiltered_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Text file|*.txt|CSV file|*.csv|All files|*.*",
                    Title = "Export Filtered Data",
                    DefaultExt = "txt",
                    AddExtension = true,
                    FileName = $"filtered_can_trace_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
                };

                if (dlg.ShowDialog() != true)
                    return;

                if (dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    ExportCsv(dlg.FileName);
                else
                    ExportText(dlg.FileName);

                txtStatus.Text = $"Exported {_filteredMessages.Count:N0} messages to {Path.GetFileName(dlg.FileName)}";
                MessageBox.Show($"Successfully exported {_filteredMessages.Count:N0} messages.",
                    "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Export failed.");
                MessageBox.Show($"Error exporting data:\n{ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportText(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(_loadResult?.HeaderText ?? string.Empty);
            sb.AppendLine("===============================================================================");
            sb.AppendLine("DIR TIME(msec) ID------- DLC DATA-------------------");
            sb.AppendLine("===============================================================================");

            foreach (var msg in _filteredMessages)
                sb.AppendLine(msg.RawLine ?? string.Empty);

            sb.AppendLine();
            sb.AppendLine($"Total number of CAN messages: {_filteredMessages.Count}");
            sb.AppendLine($"Exported on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private void ExportCsv(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Direction,TimeMs,CanId,Dlc,Data,Extended,Rtr,WallTime,RawLine");

            foreach (var msg in _filteredMessages)
            {
                sb.Append(Csv(msg.Direction));
                sb.Append(',');
                sb.Append(Csv(msg.Time));
                sb.Append(',');
                sb.Append(Csv(msg.CanId));
                sb.Append(',');
                sb.Append(Csv(msg.Dlc));
                sb.Append(',');
                sb.Append(Csv(msg.Data));
                sb.Append(',');
                sb.Append(msg.IsExtended ? "1" : "0");
                sb.Append(',');
                sb.Append(msg.IsRtr ? "1" : "0");
                sb.Append(',');
                sb.Append(Csv(msg.WallTime?.ToString("o") ?? string.Empty));
                sb.Append(',');
                sb.AppendLine(Csv(msg.RawLine));
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        private void btnReload_Click(object sender, RoutedEventArgs e)
        {
            if (_loadResult?.SourceKind != AnalysisSourceKind.Database || string.IsNullOrWhiteSpace(_loadResult.SourcePath))
                return;

            LoadData(_loadResult.SourcePath, AnalysisSource.Database);
            txtStatus.Text = "Database reloaded";
        }

        /// <summary>
        /// Packets are reassembled from the whole trace, not the filtered view: frame
        /// filters would drop segments and make intact packets look incomplete.
        /// </summary>
        private void btnSdoPackets_Click(object sender, RoutedEventArgs e)
        {
            if (_loadResult == null)
                return;

            if (_loadResult.SourceKind == AnalysisSourceKind.Database && !string.IsNullOrWhiteSpace(_loadResult.SourcePath))
                SdoPacketWindow.OpenFile(this, _loadResult.SourcePath);
            else
                SdoPacketWindow.OpenMessages(this, _allMessages, GetSdoClientNodeId(), BuildSourceDescription());
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
