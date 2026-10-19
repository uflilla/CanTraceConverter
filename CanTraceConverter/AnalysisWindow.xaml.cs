using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
        Database,
        BinaryTrace
    }

    public partial class AnalysisWindow : Window
    {
        /// <summary>
        /// Above this many rows a grid column is not sortable: sorting millions of rows through
        /// the collection view froze the window. Messages are already in capture order.
        /// </summary>
        private const int MaxSortableRows = 200000;

        private readonly DispatcherTimer _filterTimer;
        private List<CanMessage> _allMessages = new List<CanMessage>();
        private List<CanMessage> _filteredMessages = new List<CanMessage>();
        private CanTraceLoadResult _loadResult;
        private string _sourceInput;
        private AnalysisSource _sourceKind;
        private string _currentViewMode = "All Messages";
        private bool _isLoading;
        private bool _suppressFilterEvents;
        private bool _closed;

        /// <summary>Bumped per filter / view refresh; a background result for an older one is dropped.</summary>
        private int _filterVersion;
        private int _viewVersion;

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

            // Loaded off the UI thread once the window is up: a long trace takes seconds.
            txtStatus.Text = "Loading trace…";
            Loaded += async (_, __) => await LoadDataAsync(source, kind);
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
            _closed = true;
            _filterTimer.Stop();
            _filterTimer.Tick -= FilterTimer_Tick;
        }

        private async Task LoadDataAsync(string source, AnalysisSource kind)
        {
            _isLoading = true;
            _suppressFilterEvents = true;
            var previousCursor = Cursor;
            Cursor = System.Windows.Input.Cursors.Wait;
            btnReload.IsEnabled = false;
            try
            {
                txtStatus.Text = "Loading trace…";

                // Drop the previous trace first so a reload does not hold two in memory.
                _allMessages = new List<CanMessage>();
                _filteredMessages = _allMessages;
                dgMessages.ItemsSource = null;

                var loaderKind = MapSourceKind(kind);
                var result = await Task.Run(() => CanTraceLoader.Load(source, loaderKind));
                if (_closed)
                    return;

                _loadResult = result;
                _allMessages = _loadResult.Messages ?? new List<CanMessage>();
                _filteredMessages = _allMessages;

                txtSourceInfo.Text = BuildSourceDescription();
                txtSourceBadge.Text = _loadResult.SourceLabel;
                txtFileInfo.Text = _loadResult.HeaderText ?? string.Empty;
                btnReload.Visibility = _loadResult.SourceKind == AnalysisSourceKind.Database
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                _isLoading = false;
                _suppressFilterEvents = false;
                txtStatus.Text = $"Analysing {_allMessages.Count:N0} messages…";
                await ApplyFiltersAsync();

                if (!_closed)
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
                Cursor = previousCursor;
                btnReload.IsEnabled = true;
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
                case AnalysisSource.BinaryTrace:
                    return AnalysisSourceKind.BinaryTrace;
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

        /// <summary>
        /// Filters, rebuilds the identifier list and statistics, and refreshes the view. The work
        /// runs off the UI thread: on a trace of millions of frames each pass takes seconds.
        /// </summary>
        /// <returns>False when a newer refresh superseded this one.</returns>
        private async Task<bool> ApplyFiltersAsync(bool refreshView = true)
        {
            if (_isLoading || _allMessages == null)
                return false;

            int version = ++_filterVersion;
            var all = _allMessages;
            var criteria = BuildFilterCriteria();
            bool unfiltered = IsUnfiltered(criteria);

            var (filtered, idList, stats) = await Task.Run(() =>
            {
                // No filter keeps the full list rather than copying it.
                var f = unfiltered ? all : CanTraceAnalyzer.ApplyFilters(all, criteria);
                return (f, CanTraceAnalyzer.BuildCanIdList(f), CanTraceAnalyzer.ComputeStatistics(all, f));
            });

            if (_closed || version != _filterVersion || !ReferenceEquals(all, _allMessages))
                return false;

            _filteredMessages = filtered;
            if (lstUniqueIds != null)
                lstUniqueIds.ItemsSource = idList;
            if (txtStatistics != null)
                txtStatistics.Text = stats.ToDisplayText();
            UpdateMessageCount();

            if (refreshView)
                await RefreshCurrentViewAsync();
            return true;
        }

        private static bool IsUnfiltered(CanTraceFilterCriteria c) =>
            string.IsNullOrWhiteSpace(c.QuickSearch) &&
            (string.IsNullOrWhiteSpace(c.Direction) || c.Direction == "All") &&
            string.IsNullOrWhiteSpace(c.CanIdContains) &&
            (string.IsNullOrWhiteSpace(c.Dlc) || c.Dlc == "All") &&
            string.IsNullOrWhiteSpace(c.TimeFrom) &&
            string.IsNullOrWhiteSpace(c.TimeTo) &&
            string.IsNullOrWhiteSpace(c.DataPattern) &&
            string.IsNullOrWhiteSpace(c.SourceIdContains) &&
            string.IsNullOrWhiteSpace(c.FunctionCodeHex) &&
            c.ExtendedOnly != true &&
            !c.HideRtr;

        /// <summary>Runs a refresh, reporting a failure in the status bar rather than crashing.</summary>
        private async void RunAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Analysis refresh failed.");
                if (!_closed)
                    txtStatus.Text = "Error: " + ex.Message;
            }
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
            RunAsync(async () =>
            {
                if (await ApplyFiltersAsync() && txtStatus != null && !_closed)
                    txtStatus.Text = $"{_filteredMessages.Count:N0} messages match filters";
            });
        }

        private void RestartFilterTimer()
        {
            _filterTimer.Stop();
            _filterTimer.Start();
        }

        private void btnApplyFilters_Click(object sender, RoutedEventArgs e)
        {
            txtStatus.Text = "Applying filters…";
            RunAsync(async () =>
            {
                if (await ApplyFiltersAsync() && !_closed)
                    txtStatus.Text = $"{_filteredMessages.Count:N0} messages match filters";
            });
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

            txtStatus.Text = "All filters cleared";
            RunAsync(() => ApplyFiltersAsync());
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
                txtStatus.Text = $"Filtered to CAN ID {item.CanId}";
                RunAsync(() => ApplyFiltersAsync());
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
            RunAsync(RefreshCurrentViewAsync);
        }

        private void txtTimelineInterval_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressFilterEvents || _isLoading || _filterTimer == null || _currentViewMode != "Message Timeline")
                return;

            if (chkAutoApply?.IsChecked == true)
                RestartFilterTimer();
            else
                RunAsync(RefreshCurrentViewAsync);
        }

        /// <summary>
        /// Shows the selected view of the filtered messages. Views that summarise the trace
        /// are built off the UI thread; on millions of frames they take seconds.
        /// </summary>
        private async Task RefreshCurrentViewAsync()
        {
            if (dgMessages == null || _filteredMessages == null)
                return;

            panelMessageDetail.Visibility = Visibility.Collapsed;

            int version = ++_viewVersion;
            var mode = _currentViewMode;
            var messages = _filteredMessages;
            int total = _allMessages.Count;
            double interval = ParseTimelineInterval();

            SetColumnsFor(mode);
            dgMessages.ItemsSource = null;
            if (mode != "All Messages")
                txtMessageCount.Text = "Building view…";

            var (items, countText) = await Task.Run(() => BuildView(mode, messages, total, interval));
            if (_closed || version != _viewVersion)
                return;

            dgMessages.CanUserSortColumns = items.Count <= MaxSortableRows;
            dgMessages.ItemsSource = items;
            txtMessageCount.Text = countText;
        }

        private static (IList items, string countText) BuildView(string mode, List<CanMessage> messages, int total, double interval)
        {
            switch (mode)
            {
                case "Latest per CAN ID":
                {
                    var latest = CanTraceAnalyzer.BuildLatestPerId(messages);
                    return (latest, $"{latest.Count:N0} CAN IDs (latest message each)");
                }
                case "Unique CAN IDs":
                {
                    var summaries = CanTraceAnalyzer.BuildUniqueSummaries(messages);
                    return (summaries, $"{summaries.Count:N0} unique CAN IDs");
                }
                case "Data Changes":
                {
                    var changes = CanTraceAnalyzer.BuildDataChanges(messages);
                    return (changes, $"{changes.Count:N0} data change events");
                }
                case "Inter-message Gaps":
                {
                    var gaps = CanTraceAnalyzer.BuildGapAnalysis(messages);
                    return (gaps, $"{gaps.Count:N0} CAN ID / direction streams with timing data");
                }
                case "Message Timeline":
                {
                    var timeline = CanTraceAnalyzer.BuildTimeline(messages, interval);
                    return (timeline, $"{timeline.Count:N0} time buckets ({interval:N0} ms each)");
                }
                case "Frequency Analysis":
                {
                    var frequency = CanTraceAnalyzer.BuildFrequencyAnalysis(messages, messages.Count);
                    return (frequency, $"{frequency.Count:N0} CAN IDs analyzed");
                }
                default:
                    return (messages, $"{messages.Count:N0} messages displayed (of {total:N0} total)");
            }
        }

        private void SetColumnsFor(string mode)
        {
            switch (mode)
            {
                case "Unique CAN IDs":
                    SetSummaryColumns();
                    break;

                case "Data Changes":
                    SetMessageColumns(
                        Col("CAN ID", "CanId", 110),
                        Col("TIME", "Time", 100),
                        Col("DIR", "Direction", 60),
                        Col("PREVIOUS DATA", "PreviousData", 220),
                        Col("NEW DATA", "NewData", star: true));
                    break;

                case "Inter-message Gaps":
                    SetMessageColumns(
                        Col("CAN ID", "CanId", 110),
                        Col("DIR", "Direction", 60),
                        Col("MSGS", "MessageCount", 70),
                        Col("MIN GAP", "MinGapDisplay", 100),
                        Col("AVG GAP", "AvgGapDisplay", 100),
                        Col("MAX GAP", "MaxGapDisplay", 100),
                        Col("JITTER (σ)", "JitterDisplay", 100),
                        Col("EST. RATE", "EstHzDisplay", 100));
                    break;

                case "Message Timeline":
                    SetMessageColumns(
                        Col("TIME RANGE", "TimeRange", 220),
                        Col("MESSAGES", "MessageCount", 90),
                        Col("UNIQUE IDs", "UniqueIds", 90),
                        Col("RX", "RxCount", 70),
                        Col("TX", "TxCount", 70));
                    break;

                case "Frequency Analysis":
                    SetMessageColumns(
                        Col("CAN ID", "CanId", 110),
                        Col("COUNT", "TotalCount", 80),
                        Col("%", "PercentageDisplay", 80),
                        Col("FREQ (Hz)", "FrequencyDisplay", 100),
                        Col("DIR", "Direction", 70));
                    break;

                default:
                    // All Messages and Latest per CAN ID.
                    SetMessageColumns(
                        Col("DIR", "Direction", 60),
                        Col("TIME (msec)", "Time", 100),
                        Col("CAN ID", "CanId", 110),
                        Col("DLC", "Dlc", 50),
                        Col("DATA", "Data", star: true));
                    break;
            }
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

        private async void btnExportFiltered_Click(object sender, RoutedEventArgs e)
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

                var path = dlg.FileName;
                var messages = _filteredMessages;
                var header = _loadResult?.HeaderText ?? string.Empty;
                txtStatus.Text = $"Exporting {messages.Count:N0} messages…";
                var button = sender as UIElement;
                if (button != null)
                    button.IsEnabled = false;
                try
                {
                    if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                        await Task.Run(() => ExportCsv(path, messages));
                    else
                        await Task.Run(() => ExportText(path, header, messages));
                }
                finally
                {
                    if (button != null)
                        button.IsEnabled = true;
                }

                txtStatus.Text = $"Exported {messages.Count:N0} messages to {Path.GetFileName(path)}";
                MessageBox.Show($"Successfully exported {messages.Count:N0} messages.",
                    "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Export failed.");
                MessageBox.Show($"Error exporting data:\n{ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Exports are written a line at a time; building them in memory first did not fit a
        // long trace in this 32-bit process.
        private static void ExportText(string path, string header, List<CanMessage> messages)
        {
            using (var writer = new StreamWriter(path, false, Encoding.UTF8, 1 << 16))
            {
                writer.WriteLine(header);
                writer.WriteLine("===============================================================================");
                writer.WriteLine("DIR TIME(msec) ID------- DLC DATA-------------------");
                writer.WriteLine("===============================================================================");

                foreach (var msg in messages)
                    writer.WriteLine(msg.RawLine);

                writer.WriteLine();
                writer.WriteLine($"Total number of CAN messages: {messages.Count}");
                writer.WriteLine($"Exported on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
        }

        private static void ExportCsv(string path, List<CanMessage> messages)
        {
            using (var writer = new StreamWriter(path, false, Encoding.UTF8, 1 << 16))
            {
                writer.WriteLine("Direction,TimeMs,CanId,Dlc,Data,Extended,Rtr,WallTime,RawLine");

                var sb = new StringBuilder(160);
                foreach (var msg in messages)
                {
                    sb.Clear();
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
                    sb.Append(Csv(msg.RawLine));
                    writer.WriteLine(sb);
                }
            }
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

            RunAsync(async () =>
            {
                await LoadDataAsync(_loadResult.SourcePath, AnalysisSource.Database);
                if (!_closed)
                    txtStatus.Text = "Database reloaded";
            });
        }

        /// <summary>
        /// Packets are reassembled from the whole trace, not the filtered view: frame
        /// filters would drop segments and make intact packets look incomplete.
        /// </summary>
        private void btnSdoPackets_Click(object sender, RoutedEventArgs e)
        {
            if (_loadResult == null)
                return;

            // A database or binary trace is streamed from its file rather than from the frames
            // held here.
            bool fromFile = _loadResult.SourceKind == AnalysisSourceKind.Database ||
                            _loadResult.SourceKind == AnalysisSourceKind.BinaryTrace;
            if (fromFile && !string.IsNullOrWhiteSpace(_loadResult.SourcePath))
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
