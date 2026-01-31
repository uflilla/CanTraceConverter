using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace CanTraceConverter
{
    public partial class AnalysisWindow : Window
    {
        private ObservableCollection<CanMessage> _allMessages;
        private ObservableCollection<CanMessage> _filteredMessages;
        private string _headerText;
        private Dictionary<string, int> _canIdFrequency;
        private Dictionary<string, List<CanMessage>> _messagesByCanId;

        public AnalysisWindow(string convertedData)
        {
            InitializeComponent();
            _allMessages = new ObservableCollection<CanMessage>();
            _filteredMessages = new ObservableCollection<CanMessage>();
            _canIdFrequency = new Dictionary<string, int>();
            _messagesByCanId = new Dictionary<string, List<CanMessage>>();

            ParseConvertedData(convertedData);
            InitializeAnalysis();
        }

        private void ParseConvertedData(string data)
        {
            try
            {
                var lines = data.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var headerLines = new List<string>();
                bool headerEnded = false;
                int messageCount = 0;

                foreach (var line in lines)
                {
                    // Collect header information
                    if (!headerEnded)
                    {
                        if (line.Contains("==="))  // Change this - just check for separator line
                        {
                            headerEnded = true;
                            continue;
                        }
                        headerLines.Add(line);
                        continue;
                    }

                    // Skip separator lines and header line
                    if (line.Contains("===") || line.Contains("DIR TIME(msec)") || string.IsNullOrWhiteSpace(line))
                        continue;

                    // Skip footer
                    if (line.StartsWith("Total number"))
                        break;

                    // Parse CAN message line
                    var message = ParseCanMessage(line);
                    if (message != null)
                    {
                        _allMessages.Add(message);
                        messageCount++;

                        // Build frequency map
                        if (_canIdFrequency.ContainsKey(message.CanId))
                            _canIdFrequency[message.CanId]++;
                        else
                            _canIdFrequency[message.CanId] = 1;

                        // Group by CAN ID
                        if (!_messagesByCanId.ContainsKey(message.CanId))
                            _messagesByCanId[message.CanId] = new List<CanMessage>();
                        _messagesByCanId[message.CanId].Add(message);
                    }
                }

                _headerText = string.Join("\n", headerLines);
                txtFileInfo.Text = _headerText;
                txtStatus.Text = $"Loaded {messageCount:N0} messages successfully";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error parsing data: {ex.Message}", "Parse Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private CanMessage ParseCanMessage(string line)
        {
            try
            {
                // Example line: RX: 012656650  0E0C0000h (1) 05 -- -- -- -- -- -- --
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                
                if (parts.Length < 4)
                    return null;

                var message = new CanMessage
                {
                    Direction = parts[0].TrimEnd(':'),
                    Time = parts[1],
                    CanId = parts[2],
                    RawLine = line
                };

                // Extract DLC from (X) pattern
                var dlcMatch = Regex.Match(line, @"\((\d+)\)");
                if (dlcMatch.Success)
                {
                    message.Dlc = dlcMatch.Groups[1].Value;
                }

                // Extract data bytes
                var dataMatch = Regex.Match(line, @"\((\d+)\)\s+(.+)$");
                if (dataMatch.Success)
                {
                    message.Data = dataMatch.Groups[2].Value.Trim();
                    message.DataBytes = ParseDataBytes(message.Data);
                }

                // Parse time as long for filtering
                if (long.TryParse(parts[1], out long timeValue))
                {
                    message.TimeValue = timeValue;
                }

                return message;
            }
            catch
            {
                return null;
            }
        }

        private byte[] ParseDataBytes(string data)
        {
            var bytes = new List<byte>();
            var parts = data.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var part in parts)
            {
                if (part == "--")
                    bytes.Add(0);
                else if (byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                    bytes.Add(b);
            }

            return bytes.ToArray();
        }

        private void InitializeAnalysis()
        {
            // Initialize filtered messages with all messages
            _filteredMessages = new ObservableCollection<CanMessage>(_allMessages);
            dgMessages.ItemsSource = _filteredMessages;

            // Populate unique CAN IDs
            var uniqueIds = _canIdFrequency.Keys.OrderBy(x => x).ToList();
            lstUniqueIds.ItemsSource = uniqueIds;

            // Update statistics
            UpdateStatistics();
            UpdateMessageCount();
        }

        private void UpdateStatistics()
        {
            var stats = new StringBuilder();
            stats.AppendLine($"Total Messages: {_allMessages.Count:N0}");
            stats.AppendLine($"Filtered: {_filteredMessages.Count:N0}");
            stats.AppendLine($"Unique CAN IDs: {_canIdFrequency.Count}");
            
            var rxCount = _filteredMessages.Count(m => m.Direction == "RX");
            var txCount = _filteredMessages.Count(m => m.Direction == "TX");
            stats.AppendLine($"RX Messages: {rxCount:N0}");
            stats.AppendLine($"TX Messages: {txCount:N0}");

            if (_filteredMessages.Count > 0)
            {
                var minTime = _filteredMessages.Min(m => m.TimeValue);
                var maxTime = _filteredMessages.Max(m => m.TimeValue);
                var duration = maxTime - minTime;
                stats.AppendLine($"Duration: {duration:N0} ms");

                if (duration > 0)
                {
                    var msgPerSec = (_filteredMessages.Count * 1000.0) / duration;
                    stats.AppendLine($"Avg Rate: {msgPerSec:F2} msg/s");
                }
            }

            // Most frequent CAN ID
            if (_canIdFrequency.Count > 0)
            {
                var mostFrequent = _canIdFrequency.OrderByDescending(x => x.Value).First();
                stats.AppendLine($"Most Frequent ID:");
                stats.AppendLine($"  {mostFrequent.Key}");
                stats.AppendLine($"  ({mostFrequent.Value:N0} msgs)");
            }

            txtStatistics.Text = stats.ToString();
        }

        private void UpdateMessageCount()
        {
            txtMessageCount.Text = $"{_filteredMessages.Count:N0} messages displayed (of {_allMessages.Count:N0} total)";
        }

        private void OnFilterChanged(object sender, EventArgs e)
        {
            if (txtStatus == null) return;  // Add this line
            // Auto-apply filters as user types (with debouncing in production)
            // For now, just update the status
            txtStatus.Text = "Filters modified - click 'Apply Filters' to update view";
        }

        private void btnApplyFilters_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var filtered = _allMessages.AsEnumerable();

                // Direction filter
                var selectedDir = (cmbDirection.SelectedItem as ComboBoxItem)?.Content.ToString();
                if (selectedDir != "All")
                {
                    filtered = filtered.Where(m => m.Direction == selectedDir);
                }

                // CAN ID filter
                if (!string.IsNullOrWhiteSpace(txtCanIdFilter.Text))
                {
                    var canIdFilter = txtCanIdFilter.Text.Trim().ToUpper();
                    filtered = filtered.Where(m => m.CanId.ToUpper().Contains(canIdFilter));
                }

                // DLC filter
                var selectedDlc = (cmbDlcFilter.SelectedItem as ComboBoxItem)?.Content.ToString();
                if (selectedDlc != "All")
                {
                    filtered = filtered.Where(m => m.Dlc == selectedDlc);
                }

                // Time range filter
                if (!string.IsNullOrWhiteSpace(txtTimeFrom.Text))
                {
                    if (long.TryParse(txtTimeFrom.Text, out long timeFrom))
                    {
                        filtered = filtered.Where(m => m.TimeValue >= timeFrom);
                    }
                }

                if (!string.IsNullOrWhiteSpace(txtTimeTo.Text))
                {
                    if (long.TryParse(txtTimeTo.Text, out long timeTo))
                    {
                        filtered = filtered.Where(m => m.TimeValue <= timeTo);
                    }
                }

                // Data pattern filter
                if (!string.IsNullOrWhiteSpace(txtDataPattern.Text))
                {
                    var pattern = txtDataPattern.Text.Trim().ToUpper();
                    filtered = filtered.Where(m => m.Data.ToUpper().Contains(pattern));
                }

                // Source/Node ID filter (extract from CAN ID)
                if (!string.IsNullOrWhiteSpace(txtSourceIdFilter.Text))
                {
                    var sourceId = txtSourceIdFilter.Text.Trim().ToUpper();
                    filtered = filtered.Where(m => ExtractSourceId(m.CanId).Contains(sourceId));
                }

                // Function code filter
                if (!string.IsNullOrWhiteSpace(txtFunctionCode.Text))
                {
                    var funcCode = txtFunctionCode.Text.Trim().ToUpper();
                    var byteIndex = cmbFunctionCodeByte.SelectedIndex;
                    
                    filtered = filtered.Where(m =>
                    {
                        if (m.DataBytes != null && m.DataBytes.Length > byteIndex)
                        {
                            var byteValue = m.DataBytes[byteIndex].ToString("X2");
                            return byteValue.Contains(funcCode);
                        }
                        return false;
                    });
                }

                // Update filtered collection
                _filteredMessages.Clear();
                foreach (var msg in filtered)
                {
                    _filteredMessages.Add(msg);
                }

                UpdateStatistics();
                UpdateMessageCount();
                txtStatus.Text = $"Filters applied - {_filteredMessages.Count:N0} messages match";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error applying filters: {ex.Message}", "Filter Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string ExtractSourceId(string canId)
        {
            // Simple extraction - take last 2 bytes as source ID
            // Customize based on your CAN protocol
            if (canId.Length >= 4)
            {
                return canId.Substring(2, 2);
            }
            return string.Empty;
        }

        private void btnClearFilters_Click(object sender, RoutedEventArgs e)
        {
            // Reset all filters
            cmbDirection.SelectedIndex = 0;
            txtCanIdFilter.Clear();
            cmbDlcFilter.SelectedIndex = 0;
            txtTimeFrom.Clear();
            txtTimeTo.Clear();
            txtDataPattern.Clear();
            txtSourceIdFilter.Clear();
            txtFunctionCode.Clear();
            cmbFunctionCodeByte.SelectedIndex = 0;

            // Reset filtered messages
            _filteredMessages.Clear();
            foreach (var msg in _allMessages)
            {
                _filteredMessages.Add(msg);
            }

            UpdateStatistics();
            UpdateMessageCount();
            txtStatus.Text = "All filters cleared";
        }

        private void btnClearCanId_Click(object sender, RoutedEventArgs e)
        {
            txtCanIdFilter.Clear();
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

                if (dlg.ShowDialog() == true)
                {
                    var sb = new StringBuilder();
                    
                    // Add header
                    sb.AppendLine(_headerText);
                    sb.AppendLine("===============================================================================");
                    sb.AppendLine("DIR TIME(msec) ID------- DLC DATA-------------------");
                    sb.AppendLine("===============================================================================");

                    // Add filtered messages
                    foreach (var msg in _filteredMessages)
                    {
                        sb.AppendLine(msg.RawLine);
                    }

                    // Add footer
                    sb.AppendLine();
                    sb.AppendLine($"Total number of CAN messages: {_filteredMessages.Count}");
                    sb.AppendLine($"Exported on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

                    File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                    
                    txtStatus.Text = $"Exported {_filteredMessages.Count:N0} messages to {dlg.FileName}";
                    MessageBox.Show($"Successfully exported {_filteredMessages.Count:N0} messages",
                        "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting data: {ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void lstUniqueIds_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstUniqueIds.SelectedItem != null)
            {
                var selectedId = lstUniqueIds.SelectedItem.ToString();
                txtCanIdFilter.Text = selectedId;
                btnApplyFilters_Click(null, null);
            }
        }

        private void cmbViewMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbViewMode.SelectedItem == null || _filteredMessages == null)  // Add null check for _filteredMessages
                return;

            var selectedMode = (cmbViewMode.SelectedItem as ComboBoxItem)?.Content.ToString();

            switch (selectedMode)
            {
                case "All Messages":
                    ShowAllMessages();
                    break;
                case "Unique CAN IDs Only":
                    ShowUniqueCanIds();
                    break;
                case "Message Timeline":
                    ShowMessageTimeline();
                    break;
                case "Frequency Analysis":
                    ShowFrequencyAnalysis();
                    break;
            }
        }

        private void ShowAllMessages()
        {
            // Reset columns to default
            dgMessages.Columns.Clear();
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "DIR", Binding = new System.Windows.Data.Binding("Direction"), Width = 60 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "TIME (msec)", Binding = new System.Windows.Data.Binding("Time"), Width = 100 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "CAN ID", Binding = new System.Windows.Data.Binding("CanId"), Width = 110 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "DLC", Binding = new System.Windows.Data.Binding("Dlc"), Width = 50 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "DATA", Binding = new System.Windows.Data.Binding("Data"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

            dgMessages.ItemsSource = _filteredMessages;
            UpdateMessageCount();
        }

        private void ShowUniqueCanIds()
        {
            var uniqueMessages = new ObservableCollection<CanMessageSummary>();
            
            foreach (var kvp in _messagesByCanId)
            {
                var messages = kvp.Value.Where(m => _filteredMessages.Contains(m)).ToList();
                if (messages.Count > 0)
                {
                    var summary = new CanMessageSummary
                    {
                        CanId = kvp.Key,
                        Count = messages.Count,
                        FirstTime = messages.Min(m => m.TimeValue).ToString(),
                        LastTime = messages.Max(m => m.TimeValue).ToString(),
                        Direction = messages.First().Direction,
                        Dlc = messages.First().Dlc
                    };
                    uniqueMessages.Add(summary);
                }
            }

            // Change DataGrid columns for summary view
            dgMessages.Columns.Clear();
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "CAN ID", Binding = new System.Windows.Data.Binding("CanId"), Width = 120 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "Count", Binding = new System.Windows.Data.Binding("Count"), Width = 80 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "DIR", Binding = new System.Windows.Data.Binding("Direction"), Width = 60 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "DLC", Binding = new System.Windows.Data.Binding("Dlc"), Width = 50 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "First Time", Binding = new System.Windows.Data.Binding("FirstTime"), Width = 120 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "Last Time", Binding = new System.Windows.Data.Binding("LastTime"), Width = 120 });

            dgMessages.ItemsSource = uniqueMessages;
            txtMessageCount.Text = $"{uniqueMessages.Count} unique CAN IDs";
        }

        private void ShowMessageTimeline()
        {
            // Group messages by time buckets (e.g., 100ms intervals)
            var timeline = new ObservableCollection<TimelineSummary>();
            
            if (_filteredMessages.Count > 0)
            {
                var minTime = _filteredMessages.Min(m => m.TimeValue);
                var maxTime = _filteredMessages.Max(m => m.TimeValue);
                var interval = 100; // 100ms buckets

                for (long time = minTime; time <= maxTime; time += interval)
                {
                    var messagesInInterval = _filteredMessages.Where(m =>
                        m.TimeValue >= time && m.TimeValue < time + interval).ToList();

                    if (messagesInInterval.Count > 0)
                    {
                        var summary = new TimelineSummary
                        {
                            TimeRange = $"{time} - {time + interval}",
                            MessageCount = messagesInInterval.Count,
                            UniqueIds = messagesInInterval.Select(m => m.CanId).Distinct().Count(),
                            RxCount = messagesInInterval.Count(m => m.Direction == "RX"),
                            TxCount = messagesInInterval.Count(m => m.Direction == "TX")
                        };
                        timeline.Add(summary);
                    }
                }
            }

            // Change DataGrid columns for timeline view
            dgMessages.Columns.Clear();
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "Time Range (ms)", Binding = new System.Windows.Data.Binding("TimeRange"), Width = 200 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "Messages", Binding = new System.Windows.Data.Binding("MessageCount"), Width = 100 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "Unique IDs", Binding = new System.Windows.Data.Binding("UniqueIds"), Width = 100 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "RX", Binding = new System.Windows.Data.Binding("RxCount"), Width = 80 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "TX", Binding = new System.Windows.Data.Binding("TxCount"), Width = 80 });

            dgMessages.ItemsSource = timeline;
            txtMessageCount.Text = $"{timeline.Count} time intervals";
        }

        private void ShowFrequencyAnalysis()
        {
            var frequency = new ObservableCollection<FrequencyAnalysis>();

            foreach (var kvp in _canIdFrequency.OrderByDescending(x => x.Value))
            {
                var messages = _messagesByCanId[kvp.Key].Where(m => _filteredMessages.Contains(m)).ToList();
                if (messages.Count > 0)
                {
                    var minTime = messages.Min(m => m.TimeValue);
                    var maxTime = messages.Max(m => m.TimeValue);
                    var duration = (maxTime - minTime) / 1000.0; // in seconds

                    var analysis = new FrequencyAnalysis
                    {
                        CanId = kvp.Key,
                        TotalCount = messages.Count,
                        Percentage = (_filteredMessages.Count > 0) ? (messages.Count * 100.0 / _filteredMessages.Count) : 0,
                        FrequencyHz = (duration > 0) ? (messages.Count / duration) : 0,
                        Direction = messages.First().Direction
                    };
                    frequency.Add(analysis);
                }
            }

            // Change DataGrid columns for frequency view
            dgMessages.Columns.Clear();
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "CAN ID", Binding = new System.Windows.Data.Binding("CanId"), Width = 120 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "Count", Binding = new System.Windows.Data.Binding("TotalCount"), Width = 100 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "Percentage", Binding = new System.Windows.Data.Binding("PercentageDisplay"), Width = 100 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "Frequency (Hz)", Binding = new System.Windows.Data.Binding("FrequencyDisplay"), Width = 120 });
            dgMessages.Columns.Add(new DataGridTextColumn { Header = "DIR", Binding = new System.Windows.Data.Binding("Direction"), Width = 60 });

            dgMessages.ItemsSource = frequency;
            txtMessageCount.Text = $"{frequency.Count} CAN IDs analyzed";
        }

        private void btnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    // Data models
    public class CanMessage
    {
        public string Direction { get; set; }
        public string Time { get; set; }
        public long TimeValue { get; set; }
        public string CanId { get; set; }
        public string Dlc { get; set; }
        public string Data { get; set; }
        public byte[] DataBytes { get; set; }
        public string RawLine { get; set; }
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
