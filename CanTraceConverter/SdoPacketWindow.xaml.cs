using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CanTraceConverter.Models;
using CanTraceConverter.Services;
using Microsoft.Win32;

namespace CanTraceConverter
{
    /// <summary>
    /// Reassembled SDO packets — who sent what to whom, and when — kept apart from the
    /// frame-level analysis window: frame filters would cut packets apart, and a packet
    /// needs its own filters (sender, receiver, object, status, payload).
    /// </summary>
    public partial class SdoPacketWindow : Window
    {
        private const string StatusAll = "All";
        private const string StatusNotComplete = "Not complete";

        private enum SourceKind
        {
            /// <summary>Session database still being written by a monitor window.</summary>
            LiveDatabase,
            Database,
            TextFile,
            BinaryTrace,

            /// <summary>Two captures of the same adapter's channels, merged by timestamp.</summary>
            Merged,
            Messages
        }

        private readonly SourceKind _kind;
        private readonly string _path;

        /// <summary>The second capture of a merged source.</summary>
        private readonly string _mergePath;
        private readonly string _label;
        private readonly IReadOnlyList<CanMessage> _messages;
        private byte _standardClientNode;

        /// <summary>Bytes of a packet's payload shown in the dump; Save Payload writes all of it.</summary>
        private const int MaxDumpBytes = 64 * 1024;

        /// <summary>Above this many new packets, the list is told once rather than per packet.</summary>
        private const int BulkAddThreshold = 100;

        private readonly PacketCollection _packets = new PacketCollection();
        private readonly ICollectionView _view;
        private readonly DispatcherTimer _filterTimer;
        private readonly DispatcherTimer _pollTimer;
        private PacketFilter _filter = PacketFilter.None;
        private bool _readsStoredPackets;
        private long _lastPacketId;
        private bool _polling;
        private bool _closed;

        private SdoPacketWindow(SourceKind kind, string path, string label, IReadOnlyList<CanMessage> messages,
            byte standardClientNode, string mergePath = null)
        {
            _kind = kind;
            _path = path;
            _mergePath = mergePath;
            _label = label;
            _messages = messages;
            _standardClientNode = standardClientNode;

            InitializeComponent();

            _view = CollectionViewSource.GetDefaultView(_packets);
            _view.Filter = item => _filter.Matches((SdoTransfer)item);
            dgPackets.ItemsSource = _view;

            _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _filterTimer.Tick += (_, __) =>
            {
                _filterTimer.Stop();
                ApplyFilters();
            };

            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _pollTimer.Tick += async (_, __) => await PollAsync();

            bool live = kind == SourceKind.LiveDatabase;
            badgeLive.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            chkFollow.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            txtSource.Text = label;
            Title = "SDO Packet Trace — " + (path == null ? label
                : mergePath == null ? Path.GetFileName(path)
                : Path.GetFileName(path) + " + " + Path.GetFileName(mergePath));

            Loaded += async (_, __) => await LoadAsync();
        }

        // ------------------------------------------------------------------ opening

        /// <summary>Follows a monitor session's database as packets are stored.</summary>
        public static SdoPacketWindow ShowLive(Window owner, string databasePath, string sessionTitle)
        {
            var window = new SdoPacketWindow(SourceKind.LiveDatabase, databasePath,
                $"Live session “{sessionTitle}” — {databasePath}", null, 0);
            Show(owner, window);
            return window;
        }

        /// <summary>A session database (.db), converted trace text (.txt) or binary Systec trace (.ucantrc).</summary>
        public static void OpenFile(Window owner, string path)
        {
            SourceKind kind;
            if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                kind = SourceKind.Database;
            else if (path.EndsWith(".ucantrc", StringComparison.OrdinalIgnoreCase))
                kind = SourceKind.BinaryTrace;
            else
                kind = SourceKind.TextFile;

            Show(owner, new SdoPacketWindow(kind, path, path, null, 0));
        }

        /// <summary>
        /// Two captures merged by timestamp: the two channels of one adapter, as binary traces
        /// or monitor session databases. Their clocks must be the same adapter's.
        /// </summary>
        public static void OpenMerged(Window owner, string firstPath, string secondPath)
        {
            Show(owner, new SdoPacketWindow(SourceKind.Merged, firstPath,
                $"{firstPath}  +  {secondPath}", null, 0, secondPath));
        }

        /// <summary>Messages already loaded elsewhere, e.g. a trace open in the analysis window.</summary>
        public static void OpenMessages(Window owner, IReadOnlyList<CanMessage> messages, byte standardClientNode, string label)
        {
            Show(owner, new SdoPacketWindow(SourceKind.Messages, null, label, messages, standardClientNode));
        }

        private static void Show(Window owner, SdoPacketWindow window)
        {
            try
            {
                if (owner != null)
                    window.Owner = owner;
                window.Show();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Could not open the SDO packet window.");
                MessageBox.Show($"Could not open SDO packets:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ------------------------------------------------------------------ loading

        private async Task LoadAsync()
        {
            btnRefresh.IsEnabled = false;
            txtStatus.Text = "Loading SDO packets…";
            try
            {
                switch (_kind)
                {
                    case SourceKind.LiveDatabase:
                    case SourceKind.Database:
                        await LoadDatabaseAsync();
                        break;

                    case SourceKind.TextFile:
                        await ReassembleAsync(CapturedFrameSource.Open(_path), "reassembled from trace text");
                        break;

                    case SourceKind.BinaryTrace:
                        // Streamed straight from the file. Converting it to text first held the
                        // whole trace in memory several times over, which a large trace did not
                        // survive in this 32-bit process.
                        await ReassembleAsync(CapturedFrameSource.Open(_path), "reassembled from binary trace");
                        break;

                    case SourceKind.Merged:
                        await LoadMergedAsync();
                        break;

                    default:
                    {
                        var messages = _messages;
                        await ReassembleAsync((tracker, transfers) =>
                        {
                            if (messages == null)
                                return 0;
                            foreach (var msg in messages)
                                tracker.Process(msg, transfers);
                            return messages.Count;
                        }, "reassembled from the loaded trace");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Loading SDO packets failed.");
                txtStatus.Text = "Could not load SDO packets: " + ex.Message;
                MessageBox.Show($"Could not load SDO packets:\n{ex.Message}", "Load Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (!_closed)
                    btnRefresh.IsEnabled = true;
            }
        }

        private async Task LoadDatabaseAsync()
        {
            _readsStoredPackets = await Task.Run(() => SdoPacketStore.HasPacketTables(_path));
            if (_readsStoredPackets)
            {
                var stored = await Task.Run(() => SdoPacketStore.ReadTransfers(_path, _lastPacketId));
                Append(stored);
                UpdateStatus("stored packets");

                if (_kind == SourceKind.LiveDatabase && !_closed)
                    _pollTimer.Start();
                return;
            }

            // Recorded before packets were stored: rebuild them from the raw frames.
            byte client = await Task.Run(() => SdoPacketStore.ReadStandardClientNodeId(_path));
            _standardClientNode = client;
            await ReassembleAsync(CapturedFrameSource.Open(_path),
                "reassembled from raw frames (database has no stored packets)");
        }

        private async Task LoadMergedAsync()
        {
            string first = _path, second = _mergePath;

            if (first.EndsWith(".ucantrc", StringComparison.OrdinalIgnoreCase) &&
                second.EndsWith(".ucantrc", StringComparison.OrdinalIgnoreCase))
            {
                var (serialA, serialB) = await Task.Run(() =>
                    (CapturedFrameSource.ReadTraceSerialNumber(first), CapturedFrameSource.ReadTraceSerialNumber(second)));
                if (serialA != serialB &&
                    MessageBox.Show(this,
                        $"These traces come from different adapters (serial {serialA} and {serialB}), so their " +
                        "timestamps are not on one clock and the merged order may be wrong.\n\nMerge anyway?",
                        "Different adapters", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    txtStatus.Text = "Merge cancelled.";
                    return;
                }
            }

            // Packets stored by a monitor session cover only its own channel, so a session
            // database is reassembled from its raw frames here.
            if (first.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                _standardClientNode = await Task.Run(() => SdoPacketStore.ReadStandardClientNodeId(first));

            var rangeA = new CaptureRange();
            var rangeB = new CaptureRange();
            await ReassembleAsync(
                CapturedFrameSource.Merge(rangeA.Measure(CapturedFrameSource.Open(first)), rangeB.Measure(CapturedFrameSource.Open(second))),
                "reassembled from two merged captures");

            if (!_closed && rangeA.Frames > 0 && rangeB.Frames > 0 && !rangeA.Overlaps(rangeB))
                UpdateStatus(null, "the two captures do not overlap in time — check they are channels of the same adapter, recorded together");
        }

        /// <summary>The time span of a capture, measured as it streams by.</summary>
        private sealed class CaptureRange
        {
            public long Frames;
            public double FirstMs;
            public double LastMs;

            public IEnumerable<CapturedFrame> Measure(IEnumerable<CapturedFrame> frames)
            {
                foreach (var f in frames)
                {
                    if (Frames++ == 0)
                        FirstMs = f.TimestampMs;
                    LastMs = f.TimestampMs;
                    yield return f;
                }
            }

            public bool Overlaps(CaptureRange other) => FirstMs <= other.LastMs && other.FirstMs <= LastMs;
        }

        private Task ReassembleAsync(IEnumerable<CapturedFrame> frames, string origin) =>
            ReassembleAsync((tracker, transfers) =>
            {
                long count = 0;
                foreach (var f in frames)
                {
                    count++;
                    tracker.Process(f, transfers);
                }
                return count;
            }, origin);

        /// <summary>
        /// Feeds a source through an SDO tracker on a background thread. Frames are kept
        /// undecoded and messages are streamed, so a trace of millions of frames fits in memory.
        /// </summary>
        /// <param name="feed">Processes every CAN frame of the source; returns the frame count.</param>
        private async Task ReassembleAsync(Func<SdoTracker, List<SdoTransfer>, long> feed, string origin)
        {
            txtStatus.Text = "Reassembling SDO packets…";
            byte client = _standardClientNode;
            var (transfers, frameCount) = await Task.Run(() =>
            {
                var tracker = new SdoTracker(client, retainFrames: false);
                var completed = new List<SdoTransfer>();
                long frames = feed(tracker, completed);
                tracker.FinishReassembly(completed);
                return (completed, frames);
            });
            if (_closed)
                return;

            // One reset, not a change notification per packet: a long trace has tens of
            // thousands of packets, and adding them one by one through the filtered view
            // and grid froze the window.
            _packets.ReplaceAll(transfers);
            UpdateStatus(origin + $" ({frameCount:N0} CAN frames)");
        }

        private async Task PollAsync()
        {
            if (_polling || _closed || !_readsStoredPackets)
                return;

            _polling = true;
            try
            {
                long after = _lastPacketId;
                var fresh = await Task.Run(() => SdoPacketStore.ReadTransfers(_path, after));
                if (_closed)
                    return;

                if (fresh.Count > 0)
                {
                    Append(fresh);
                    UpdateStatus("stored packets");
                }
                txtLive.Text = "● LIVE";
            }
            catch (Exception ex)
            {
                // The session may be closing; keep trying rather than failing the window.
                txtLive.Text = "● LIVE (retrying)";
                AppLog.Warn(ex, "Polling SDO packets failed.");
            }
            finally
            {
                _polling = false;
            }
        }

        private void Append(List<SdoTransfer> transfers)
        {
            if (transfers.Count == 0)
                return;

            // A first load can be tens of thousands of packets; live polls add a few, added
            // singly so the grid keeps its selection and scroll position.
            if (transfers.Count > BulkAddThreshold)
            {
                _packets.AddRange(transfers);
            }
            else
            {
                foreach (var t in transfers)
                    _packets.Add(t);
            }

            foreach (var t in transfers)
            {
                if (t.DatabaseId > _lastPacketId)
                    _lastPacketId = t.DatabaseId;
            }

            if (chkFollow.IsChecked == true && _kind == SourceKind.LiveDatabase)
            {
                var newest = transfers.LastOrDefault(t => _filter.Matches(t));
                if (newest != null)
                    dgPackets.ScrollIntoView(newest);
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_readsStoredPackets)
            {
                await PollAsync();
                return;
            }

            _packets.Clear();
            await LoadAsync();
        }

        // ------------------------------------------------------------------ filters

        private void OnFilterChanged(object sender, EventArgs e)
        {
            if (_filterTimer == null)
                return;

            _filterTimer.Stop();
            _filterTimer.Start();
        }

        private void ApplyFilters()
        {
            _filter = PacketFilter.Parse(
                txtSender.Text, txtReceiver.Text, txtIndex.Text,
                (cmbStatus.SelectedItem as ComboBoxItem)?.Content?.ToString(),
                txtFrom.Text, txtTo.Text, txtPayload.Text,
                out string problem);

            _view.Refresh();
            UpdateStatus(null, problem);
        }

        private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
        {
            txtSender.Clear();
            txtReceiver.Clear();
            txtIndex.Clear();
            txtFrom.Clear();
            txtTo.Clear();
            txtPayload.Clear();
            cmbStatus.SelectedIndex = 0;
            _filterTimer.Stop();
            ApplyFilters();
        }

        private string _origin = string.Empty;

        private void UpdateStatus(string origin, string problem = null)
        {
            if (origin != null)
                _origin = origin;

            int shown = _view.Cast<object>().Count();
            int complete = _packets.Count(p => p.Status == "Complete");
            var text = $"{_packets.Count:N0} SDO packets · {shown:N0} shown · {complete:N0} complete · {_packets.Count - complete:N0} not complete — {_origin}";
            if (!string.IsNullOrEmpty(problem))
                text += "   ⚠ " + problem;
            txtStatus.Text = text;
        }

        // ------------------------------------------------------------------ selection

        private async void DgPackets_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var t = dgPackets.SelectedItem as SdoTransfer;
            btnSavePayload.IsEnabled = t != null;
            if (t == null)
            {
                txtSummary.Text = "Select a packet to see its full payload and the frames it was sent in.";
                txtPayloadDump.Clear();
                dgFrames.ItemsSource = null;
                txtFramesHeader.Text = "Frames";
                return;
            }

            txtSummary.Text = DescribePacket(t);
            if (t.PayloadSize == 0)
                txtPayloadDump.Text = "(no payload bytes captured)";
            else if (t.PayloadSize > MaxDumpBytes)
                txtPayloadDump.Text = t.FormatPayloadDump(MaxDumpBytes) + Environment.NewLine +
                    $"… first {MaxDumpBytes:N0} of {t.PayloadSize:N0} bytes shown — use Save Payload for all of it.";
            else
                txtPayloadDump.Text = t.PayloadDump;

            if (t.Frames.Count == 0 && t.FrameCount > 0 && t.DatabaseId == 0)
            {
                // Reassembled in memory with its frames kept undecoded: decode them now, and
                // only for display, so browsing packets does not grow the memory held.
                var decoded = SdoTracker.DecodeFrames(t);
                dgFrames.ItemsSource = decoded;
                txtFramesHeader.Text = $"Frames ({decoded.Count})";
                return;
            }

            if (t.Frames.Count == 0 && t.DatabaseId > 0 && _path != null)
            {
                txtFramesHeader.Text = "Frames (loading…)";
                dgFrames.ItemsSource = null;
                try
                {
                    var frames = await Task.Run(() => SdoPacketStore.ReadFrames(_path, t.DatabaseId));
                    if (t.Frames.Count == 0)
                        t.Frames.AddRange(frames);
                }
                catch (Exception ex)
                {
                    AppLog.Warn(ex, "Could not load SDO frames for packet {0}.", t.DatabaseId);
                }

                if (!ReferenceEquals(dgPackets.SelectedItem, t))
                    return;
            }

            dgFrames.ItemsSource = null;
            dgFrames.ItemsSource = t.Frames;
            txtFramesHeader.Text = $"Frames ({t.Frames.Count})";
        }

        private static string DescribePacket(SdoTransfer t)
        {
            var sb = new StringBuilder();
            sb.Append($"{t.TransferType} ({t.Mode}) · node {t.SenderDisplay} → node {t.ReceiverDisplay} · SDO #{t.SdoNumber} · object {t.IndexDisplay} sub {t.SubIndexDisplay}");
            sb.AppendLine(t.Scheme == SdoAddressingScheme.Extended ? " · 29-bit IDs" : " · 11-bit IDs");
            sb.Append($"{t.StartTimeDisplay} – {CanMessageHelper.FormatTime(t.EndTimeMs)} ms ({t.DurationDisplay} ms)");
            if (t.StartWallTime.HasValue)
                sb.Append($" · {t.StartWallTimeDisplay}");
            sb.AppendLine();
            sb.Append($"Status: {t.Status} · {t.PayloadSize} bytes");
            if (t.DeclaredSize.HasValue)
                sb.Append($" (declared {t.DeclaredSize.Value})");
            if (t.AbortCode.HasValue)
                sb.Append($" · abort {t.AbortCode.Value:X8}h {SdoTracker.DescribeAbortCode(t.AbortCode.Value)}");
            if (t.Issues.Count > 0)
                sb.AppendLine().Append("Issues: ").Append(t.IssuesDisplay);
            return sb.ToString();
        }

        // ------------------------------------------------------------------ export

        private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
        {
            var packets = _view.Cast<SdoTransfer>().ToList();
            if (packets.Count == 0)
            {
                MessageBox.Show("No packets match the current filters.", "Export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "CSV file|*.csv|All files|*.*",
                Title = "Export SDO Packets",
                DefaultExt = "csv",
                AddExtension = true,
                FileName = $"sdo_packets_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };
            if (dlg.ShowDialog(this) != true)
                return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("StartMs,EndMs,StartWallTime,Type,Mode,SenderNode,ReceiverNode,SdoNumber,Index,SubIndex,DeclaredSize,PayloadSize,Status,AbortCode,Frames,Issues,PayloadHex");
                foreach (var t in packets)
                {
                    sb.Append(Csv(t.StartTimeDisplay)).Append(',')
                      .Append(Csv(CanMessageHelper.FormatTime(t.EndTimeMs))).Append(',')
                      .Append(Csv(t.StartWallTime?.ToString("o") ?? string.Empty)).Append(',')
                      .Append(Csv(t.TransferType)).Append(',')
                      .Append(Csv(t.Mode)).Append(',')
                      .Append(t.SenderNodeId).Append(',')
                      .Append(t.ReceiverNodeId).Append(',')
                      .Append(t.SdoNumber).Append(',')
                      .Append(t.Index.HasValue ? t.Index.Value.ToString("X4") : string.Empty).Append(',')
                      .Append(t.SubIndex.HasValue ? t.SubIndex.Value.ToString("X2") : string.Empty).Append(',')
                      .Append(t.DeclaredSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                      .Append(t.PayloadSize).Append(',')
                      .Append(Csv(t.Status)).Append(',')
                      .Append(t.AbortCode.HasValue ? t.AbortCode.Value.ToString("X8") : string.Empty).Append(',')
                      .Append(t.FrameCount).Append(',')
                      .Append(Csv(t.IssuesDisplay)).Append(',')
                      .AppendLine(Csv(t.PayloadHex));
                }

                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                txtStatus.Text = $"Exported {packets.Count:N0} packets to {Path.GetFileName(dlg.FileName)}.";
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "SDO packet export failed.");
                MessageBox.Show($"Export failed:\n{ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSavePayload_Click(object sender, RoutedEventArgs e)
        {
            if (!(dgPackets.SelectedItem is SdoTransfer t))
                return;

            var dlg = new SaveFileDialog
            {
                Filter = "Binary file|*.bin|All files|*.*",
                Title = "Save Packet Payload",
                DefaultExt = "bin",
                AddExtension = true,
                FileName = $"sdo_{t.SenderNodeId}_to_{t.ReceiverNodeId}_{t.IndexDisplay.TrimEnd('h')}_{t.StartTimeMs:F0}ms.bin"
            };
            if (dlg.ShowDialog(this) != true)
                return;

            try
            {
                File.WriteAllBytes(dlg.FileName, t.Payload);
                txtStatus.Text = $"Saved {t.PayloadSize} bytes to {Path.GetFileName(dlg.FileName)}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save payload:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void Window_Closed(object sender, EventArgs e)
        {
            _closed = true;
            _pollTimer.Stop();
            _filterTimer.Stop();
        }

        // ------------------------------------------------------------------ packet list

        /// <summary>An observable list that can take many packets with a single notification.</summary>
        private sealed class PacketCollection : ObservableCollection<SdoTransfer>
        {
            public void ReplaceAll(IEnumerable<SdoTransfer> packets)
            {
                CheckReentrancy();
                Items.Clear();
                AddItems(packets);
            }

            public void AddRange(IEnumerable<SdoTransfer> packets)
            {
                CheckReentrancy();
                AddItems(packets);
            }

            private void AddItems(IEnumerable<SdoTransfer> packets)
            {
                foreach (var p in packets)
                    Items.Add(p);

                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                    System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
            }
        }

        // ------------------------------------------------------------------ filter model

        private sealed class PacketFilter
        {
            public static readonly PacketFilter None = new PacketFilter();

            private byte? _sender;
            private byte? _receiver;
            private ushort? _index;
            private string _status;
            private double? _from;
            private double? _to;
            private string _payloadHex;

            public bool Matches(SdoTransfer t)
            {
                if (_sender.HasValue && t.SenderNodeId != _sender.Value)
                    return false;
                if (_receiver.HasValue && t.ReceiverNodeId != _receiver.Value)
                    return false;
                if (_index.HasValue && t.Index != _index.Value)
                    return false;
                if (_from.HasValue && t.StartTimeMs < _from.Value)
                    return false;
                if (_to.HasValue && t.StartTimeMs > _to.Value)
                    return false;

                if (_status != null)
                {
                    if (_status == StatusNotComplete ? t.Status == "Complete" : t.Status != _status)
                        return false;
                }

                if (_payloadHex != null && !ContainsBytes(t.SearchHex, _payloadHex))
                    return false;

                return true;
            }

            /// <summary>Hex match on a byte boundary, so "4F 00" never matches across nibbles.</summary>
            private static bool ContainsBytes(string haystack, string needle)
            {
                int at = haystack.IndexOf(needle, StringComparison.Ordinal);
                while (at >= 0)
                {
                    if (at % 2 == 0)
                        return true;
                    at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal);
                }
                return false;
            }

            public static PacketFilter Parse(string sender, string receiver, string index, string status,
                string from, string to, string payload, out string problem)
            {
                var problems = new List<string>();
                var f = new PacketFilter();

                if (!string.IsNullOrWhiteSpace(sender))
                {
                    if (TryParseNode(sender, out byte node)) f._sender = node;
                    else problems.Add("sender node ignored (0–255, decimal or hex)");
                }

                if (!string.IsNullOrWhiteSpace(receiver))
                {
                    if (TryParseNode(receiver, out byte node)) f._receiver = node;
                    else problems.Add("receiver node ignored (0–255, decimal or hex)");
                }

                if (!string.IsNullOrWhiteSpace(index))
                {
                    var text = index.Trim().TrimEnd('h', 'H');
                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        text = text.Substring(2);
                    if (ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort value)) f._index = value;
                    else problems.Add("index ignored (hex, e.g. 6304)");
                }

                if (!string.IsNullOrEmpty(status) && status != StatusAll)
                    f._status = status;

                if (!string.IsNullOrWhiteSpace(from))
                {
                    if (TryParseMs(from, out double value)) f._from = value;
                    else problems.Add("'from' time ignored");
                }

                if (!string.IsNullOrWhiteSpace(to))
                {
                    if (TryParseMs(to, out double value)) f._to = value;
                    else problems.Add("'to' time ignored");
                }

                if (!string.IsNullOrWhiteSpace(payload))
                {
                    var hex = new string(payload.Where(c => !char.IsWhiteSpace(c) && c != ',' && c != '-').ToArray())
                        .Replace("0x", string.Empty).Replace("0X", string.Empty).ToUpperInvariant();
                    if (hex.Length % 2 == 0 && hex.All(Uri.IsHexDigit)) f._payloadHex = hex;
                    else problems.Add("payload filter ignored (whole hex bytes, e.g. 08 04 C1)");
                }

                problem = problems.Count > 0 ? string.Join("; ", problems) : null;
                return f;
            }

            private static bool TryParseNode(string text, out byte node)
            {
                node = 0;
                string trimmed = text.Trim();
                bool hex = false;
                if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed.Substring(2);
                    hex = true;
                }
                else if (trimmed.EndsWith("h", StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed.Substring(0, trimmed.Length - 1);
                    hex = true;
                }

                return hex
                    ? byte.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out node)
                    : byte.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out node);
            }

            private static bool TryParseMs(string text, out double value) =>
                double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
