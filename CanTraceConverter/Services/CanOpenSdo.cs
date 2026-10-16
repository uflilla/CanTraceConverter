using System;
using System.Collections.Generic;
using System.Text;

namespace CanTraceConverter.Services
{
    public enum SdoDirection
    {
        /// <summary>Request from the SDO client to the server.</summary>
        ClientToServer,

        /// <summary>Response from the SDO server to the client.</summary>
        ServerToClient
    }

    /// <summary>Which COB-ID scheme identified an SDO frame.</summary>
    public enum SdoAddressingScheme
    {
        /// <summary>CiA 301 predefined connection set: 600h/580h + server node, 11-bit.</summary>
        Standard,

        /// <summary>
        /// Mission 29-bit layout: bits 28..24 function code (0Bh client→server,
        /// 0Ch server→client), 23..16 sender node, 15..8 SDO number, 7..0 destination node.
        /// </summary>
        Extended
    }

    /// <summary>Resolves sender, receiver and SDO channel from a COB-ID.</summary>
    public static class SdoAddressing
    {
        public const uint StandardClientToServerBase = 0x600;
        public const uint StandardServerToClientBase = 0x580;
        public const int ExtendedClientToServerFunctionCode = 0x0B;
        public const int ExtendedServerToClientFunctionCode = 0x0C;

        /// <param name="standardClientNodeId">Client node for 11-bit SDOs, whose COB-ID only names the server.</param>
        public static bool TryResolve(uint canId, bool isExtended, bool isRtr, byte standardClientNodeId, out SdoAddress address)
        {
            address = default;
            if (isRtr)
                return false;

            if (!isExtended)
            {
                if (canId > StandardClientToServerBase && canId <= StandardClientToServerBase + 0x7F)
                {
                    address = new SdoAddress(SdoAddressingScheme.Standard, SdoDirection.ClientToServer,
                        standardClientNodeId, (byte)(canId - StandardClientToServerBase), 0);
                    return true;
                }

                if (canId > StandardServerToClientBase && canId <= StandardServerToClientBase + 0x7F)
                {
                    address = new SdoAddress(SdoAddressingScheme.Standard, SdoDirection.ServerToClient,
                        standardClientNodeId, (byte)(canId - StandardServerToClientBase), 0);
                    return true;
                }

                return false;
            }

            int functionCode = (int)((canId >> 24) & 0x1F);
            byte source = (byte)(canId >> 16);
            byte sdoNumber = (byte)(canId >> 8);
            byte destination = (byte)canId;

            if (functionCode == ExtendedClientToServerFunctionCode)
            {
                address = new SdoAddress(SdoAddressingScheme.Extended, SdoDirection.ClientToServer, source, destination, sdoNumber);
                return true;
            }

            if (functionCode == ExtendedServerToClientFunctionCode)
            {
                address = new SdoAddress(SdoAddressingScheme.Extended, SdoDirection.ServerToClient, destination, source, sdoNumber);
                return true;
            }

            return false;
        }
    }

    public readonly struct SdoAddress
    {
        public SdoAddress(SdoAddressingScheme scheme, SdoDirection direction, byte clientNodeId, byte serverNodeId, byte sdoNumber)
        {
            Scheme = scheme;
            Direction = direction;
            ClientNodeId = clientNodeId;
            ServerNodeId = serverNodeId;
            SdoNumber = sdoNumber;
        }

        public SdoAddressingScheme Scheme { get; }
        public SdoDirection Direction { get; }
        public byte ClientNodeId { get; }
        public byte ServerNodeId { get; }
        public byte SdoNumber { get; }
        public byte SenderNodeId => Direction == SdoDirection.ClientToServer ? ClientNodeId : ServerNodeId;
        public byte ReceiverNodeId => Direction == SdoDirection.ClientToServer ? ServerNodeId : ClientNodeId;
    }

    /// <summary>One SDO CAN frame, with the full packet.</summary>
    public sealed class SdoFrame
    {
        public double TimestampMs { get; set; }
        public long RawHardwareTime { get; set; }
        public DateTime? WallTime { get; set; }

        /// <summary>RX or TX as seen by this adapter (TX = sent by this application).</summary>
        public string BusDirection { get; set; }
        public uint CobId { get; set; }
        public bool IsExtendedId { get; set; }
        public SdoAddressingScheme Scheme { get; set; }
        public SdoDirection Direction { get; set; }
        public byte ClientNodeId { get; set; }
        public byte ServerNodeId { get; set; }
        public byte SdoNumber { get; set; }
        public byte SenderNodeId { get; set; }
        public byte ReceiverNodeId { get; set; }
        public byte CommandByte { get; set; }

        /// <summary>Download, Upload, Block Download, Block Upload, Abort or Unknown.</summary>
        public string Service { get; set; }
        public string Command { get; set; }
        public ushort? Index { get; set; }
        public byte? SubIndex { get; set; }
        public uint? AbortCode { get; set; }
        public byte Dlc { get; set; }
        public byte[] Data { get; set; } = new byte[8];

        /// <summary>The reassembled transfer this frame belongs to, once known.</summary>
        public SdoTransfer Transfer { get; internal set; }

        /// <summary>Database ids, set by the session writer.</summary>
        internal long DbId { get; set; }
        internal long CanMessageId { get; set; }

        public bool IsDownload => Service == "Download" || Service == "Block Download";

        public string TimeDisplay => CanMessageHelper.FormatTime(TimestampMs);
        public string CobIdDisplay => IsExtendedId ? $"{CobId:X8}h" : $"{CobId:X3}h";
        public string DirectionDisplay => Direction == SdoDirection.ClientToServer ? "Client → Server" : "Server → Client";
        public string SenderDisplay => SdoTransfer.NodeDisplay(SenderNodeId, Scheme, Direction == SdoDirection.ClientToServer);
        public string ReceiverDisplay => SdoTransfer.NodeDisplay(ReceiverNodeId, Scheme, Direction == SdoDirection.ServerToClient);
        public string IndexDisplay => Index.HasValue ? $"{Index.Value:X4}h" : string.Empty;
        public string SubIndexDisplay => SubIndex.HasValue ? $"{SubIndex.Value:X2}h" : string.Empty;
        public string AbortDisplay => AbortCode.HasValue ? $"{AbortCode.Value:X8}h {SdoTracker.DescribeAbortCode(AbortCode.Value)}" : string.Empty;
        public string DataHex => SdoTracker.FormatBytes(Data, Dlc);
        public string WallTimeDisplay => WallTime.HasValue ? WallTime.Value.ToString("yyyy-MM-dd HH:mm:ss.ffff") : string.Empty;
    }

    /// <summary>
    /// One complete SDO transfer — the large packet a sender split into SDO frames,
    /// reassembled with who sent it to whom and when.
    /// </summary>
    public sealed class SdoTransfer
    {
        private const int MaxIssues = 20;

        internal readonly List<byte> PayloadBuffer = new List<byte>();
        private readonly List<string> _issues = new List<string>();

        /// <summary>Download (client → server data) or Upload (server → client data).</summary>
        public string TransferType { get; internal set; }

        /// <summary>Expedited, Segmented, Block or Unknown (start not captured).</summary>
        public string Mode { get; internal set; }
        public SdoAddressingScheme Scheme { get; internal set; }
        public byte ClientNodeId { get; internal set; }
        public byte ServerNodeId { get; internal set; }
        public byte SdoNumber { get; internal set; }

        /// <summary>Node the data came from.</summary>
        public byte SenderNodeId => TransferType == "Upload" ? ServerNodeId : ClientNodeId;

        /// <summary>Node the data went to.</summary>
        public byte ReceiverNodeId => TransferType == "Upload" ? ClientNodeId : ServerNodeId;

        public ushort? Index { get; internal set; }
        public byte? SubIndex { get; internal set; }
        public long? DeclaredSize { get; internal set; }
        public double StartTimeMs { get; internal set; }
        public double EndTimeMs { get; internal set; }
        public DateTime? StartWallTime { get; internal set; }
        public DateTime? EndWallTime { get; internal set; }

        /// <summary>Complete, Complete (unconfirmed), Aborted or Incomplete.</summary>
        public string Status { get; internal set; } = "In progress";
        public uint? AbortCode { get; internal set; }
        public List<SdoFrame> Frames { get; } = new List<SdoFrame>();
        public IReadOnlyList<string> Issues => _issues;

        /// <summary>All payload bytes arrived (last segment / expedited data seen).</summary>
        internal bool DataComplete { get; set; }

        internal long DbId { get; set; }

        /// <summary>Frame count stored in the database, before the frames themselves are loaded.</summary>
        internal int StoredFrameCount { get; set; }

        private string _searchHex;

        /// <summary>Row id in sdo_transfers, or 0 when reassembled in memory.</summary>
        public long DatabaseId => DbId;
        public int FrameCount => Frames.Count > 0 ? Frames.Count : StoredFrameCount;

        public byte[] Payload => PayloadBuffer.ToArray();
        public int PayloadSize => PayloadBuffer.Count;
        public double DurationMs => EndTimeMs - StartTimeMs;

        /// <summary>Payload as contiguous upper-case hex, for searching. Only valid once the
        /// transfer is finished; computed on first use.</summary>
        internal string SearchHex
        {
            get
            {
                if (_searchHex == null)
                {
                    var sb = new StringBuilder(PayloadBuffer.Count * 2);
                    foreach (var b in PayloadBuffer)
                        sb.Append(b.ToString("X2"));
                    _searchHex = sb.ToString();
                }
                return _searchHex;
            }
        }

        public string StartTimeDisplay => CanMessageHelper.FormatTime(StartTimeMs);
        public string DurationDisplay => DurationMs.ToString("F1");
        public string SenderDisplay => NodeDisplay(SenderNodeId, Scheme, TransferType != "Upload");
        public string ReceiverDisplay => NodeDisplay(ReceiverNodeId, Scheme, TransferType == "Upload");
        public string RouteDisplay => $"{SenderDisplay} → {ReceiverDisplay}";
        public string IndexDisplay => Index.HasValue ? $"{Index.Value:X4}h" : "?";
        public string SubIndexDisplay => SubIndex.HasValue ? $"{SubIndex.Value:X2}h" : "?";
        public string SizeDisplay => DeclaredSize.HasValue && DeclaredSize.Value != PayloadSize
            ? $"{PayloadSize} / {DeclaredSize.Value}"
            : PayloadSize.ToString();
        public string IssuesDisplay => string.Join("; ", _issues);
        public string StartWallTimeDisplay => StartWallTime.HasValue ? StartWallTime.Value.ToString("yyyy-MM-dd HH:mm:ss.ffff") : string.Empty;
        public string StartClockDisplay => StartWallTime.HasValue ? StartWallTime.Value.ToString("HH:mm:ss.fff") : string.Empty;

        public string PayloadPreview
        {
            get
            {
                const int previewBytes = 24;
                var text = SdoTracker.FormatBytes(PayloadBuffer, Math.Min(previewBytes, PayloadBuffer.Count));
                return PayloadBuffer.Count > previewBytes ? text + " …" : text;
            }
        }

        public string PayloadHex => SdoTracker.FormatBytes(PayloadBuffer, PayloadBuffer.Count);

        /// <summary>Offset / hex / ASCII dump, 16 bytes per line.</summary>
        public string PayloadDump
        {
            get
            {
                var sb = new StringBuilder();
                for (int offset = 0; offset < PayloadBuffer.Count; offset += 16)
                {
                    sb.Append(offset.ToString("X4")).Append("  ");
                    var ascii = new StringBuilder(16);
                    for (int i = 0; i < 16; i++)
                    {
                        int at = offset + i;
                        if (at < PayloadBuffer.Count)
                        {
                            byte b = PayloadBuffer[at];
                            sb.Append(b.ToString("X2")).Append(' ');
                            ascii.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                        }
                        else
                        {
                            sb.Append("   ");
                        }
                        if (i == 7)
                            sb.Append(' ');
                    }
                    sb.Append(' ').Append(ascii).AppendLine();
                }
                return sb.ToString().TrimEnd();
            }
        }

        internal void AddIssue(string issue)
        {
            if (_issues.Count < MaxIssues)
                _issues.Add(issue);
            else if (_issues.Count == MaxIssues)
                _issues.Add("further issues omitted");
        }

        internal static string NodeDisplay(byte node, SdoAddressingScheme scheme, bool isClient) =>
            scheme == SdoAddressingScheme.Standard && isClient ? $"{node} (client)" : node.ToString();
    }

    /// <summary>
    /// Decodes SDO frames and reassembles them into complete transfers (CiA 301 expedited,
    /// segmented and block download/upload, plus aborts) for both COB-ID schemes in
    /// <see cref="SdoAddressing"/>.
    /// </summary>
    /// <remarks>
    /// Stateful per SDO channel (client, server, SDO number). Feed frames in capture order,
    /// one instance per stream; not thread-safe. Captures can miss frames, so the tracker
    /// never assumes a clean stream: a transfer whose start was not seen, a skipped segment
    /// (toggle bit not alternating) or a size mismatch is kept and flagged, not discarded.
    /// </remarks>
    public sealed class SdoTracker
    {
        /// <summary>An open transfer with no frame for this long is closed as incomplete.</summary>
        public const double DefaultTimeoutMs = 5000;

        private readonly Dictionary<ChannelKey, Channel> _channels = new Dictionary<ChannelKey, Channel>();
        private readonly double _timeoutMs;

        public SdoTracker(byte standardClientNodeId, double timeoutMs = DefaultTimeoutMs)
        {
            StandardClientNodeId = standardClientNodeId;
            _timeoutMs = timeoutMs;
        }

        public byte StandardClientNodeId { get; }

        private enum BlockPhase
        {
            None,

            /// <summary>Client is sending download sub-block segments.</summary>
            DownloadSegments,

            /// <summary>Server is sending upload sub-block segments.</summary>
            UploadSegments
        }

        private readonly struct ChannelKey : IEquatable<ChannelKey>
        {
            private readonly SdoAddressingScheme _scheme;
            private readonly byte _client;
            private readonly byte _server;
            private readonly byte _number;

            public ChannelKey(in SdoAddress a)
            {
                _scheme = a.Scheme;
                _client = a.ClientNodeId;
                _server = a.ServerNodeId;
                _number = a.SdoNumber;
            }

            public bool Equals(ChannelKey o) =>
                _scheme == o._scheme && _client == o._client && _server == o._server && _number == o._number;
            public override bool Equals(object obj) => obj is ChannelKey o && Equals(o);
            public override int GetHashCode() => ((int)_scheme << 24) | (_client << 16) | (_server << 8) | _number;
        }

        private sealed class Channel
        {
            public BlockPhase Phase;
            public bool LastSegmentSeen;
            public SdoTransfer Active;
            public int NextToggle;

            /// <summary>Data segments captured for the active transfer.</summary>
            public int SegmentCount;

            /// <summary>Segment handshakes seen from the other side (download segment
            /// responses, upload segment requests): each pairs with one data segment.</summary>
            public int PeerSegmentCount;
            public int ToggleErrors;
            public bool StartedMidStream;
            public bool AwaitingConfirm;

            /// <summary>An abort naming another object, seen mid-transfer: it ends this transfer
            /// only if the transfer does not continue afterwards.</summary>
            public SdoFrame PendingAbort;
            public readonly SortedDictionary<int, byte[]> SubBlock = new SortedDictionary<int, byte[]>();

            public void ResetTransferState()
            {
                Active = null;
                NextToggle = 0;
                SegmentCount = 0;
                PeerSegmentCount = 0;
                ToggleErrors = 0;
                StartedMidStream = false;
                AwaitingConfirm = false;
                PendingAbort = null;
                SubBlock.Clear();
            }
        }

        public SdoFrame Process(CanMessageRecord record, List<SdoTransfer> completed)
        {
            if (record == null)
                return null;

            return Process(record.CanId, record.IsExtended, record.IsRtr, record.Dlc, record.Data,
                record.Direction, record.TimestampMs, record.RawHardwareTime, record.WallTime, completed);
        }

        /// <summary>
        /// Decodes one frame. Returns null when it is not SDO traffic. Transfers that this
        /// frame finished (or forced closed) are appended to <paramref name="completed"/>.
        /// </summary>
        public SdoFrame Process(
            uint canId, bool isExtended, bool isRtr, byte dlc, byte[] data,
            string busDirection, double timestampMs, long rawHardwareTime, DateTime? wallTime,
            List<SdoTransfer> completed)
        {
            if (!SdoAddressing.TryResolve(canId, isExtended, isRtr, StandardClientNodeId, out var address))
                return null;

            var bytes = new byte[8];
            if (data != null)
                Array.Copy(data, bytes, Math.Min(8, data.Length));

            var frame = new SdoFrame
            {
                TimestampMs = timestampMs,
                RawHardwareTime = rawHardwareTime,
                WallTime = wallTime,
                BusDirection = busDirection,
                CobId = canId,
                IsExtendedId = isExtended,
                Scheme = address.Scheme,
                Direction = address.Direction,
                ClientNodeId = address.ClientNodeId,
                ServerNodeId = address.ServerNodeId,
                SdoNumber = address.SdoNumber,
                SenderNodeId = address.SenderNodeId,
                ReceiverNodeId = address.ReceiverNodeId,
                Dlc = Math.Min(dlc, (byte)8),
                Data = bytes,
                CommandByte = bytes[0]
            };

            var key = new ChannelKey(address);
            if (!_channels.TryGetValue(key, out var channel))
            {
                channel = new Channel();
                _channels[key] = channel;
            }

            if (channel.Active != null && timestampMs - channel.Active.EndTimeMs > _timeoutMs)
            {
                Close(channel, $"no frame for {_timeoutMs / 1000:F0} s (transfer timed out)", completed);
                channel.Phase = BlockPhase.None;
            }

            if (dlc == 0)
            {
                frame.Service = "Unknown";
                frame.Command = "Empty SDO frame (DLC 0)";
                Track(channel, frame);
                return frame;
            }

            if (address.Direction == SdoDirection.ClientToServer)
                ProcessClientFrame(frame, channel, completed);
            else
                ProcessServerFrame(frame, channel, completed);

            return frame;
        }

        /// <summary>Reassembles every SDO transfer in a captured message list, in start order.</summary>
        public static List<SdoTransfer> Reassemble(IEnumerable<Models.CanMessage> messages, byte standardClientNodeId)
        {
            var tracker = new SdoTracker(standardClientNodeId);
            var transfers = new List<SdoTransfer>();
            if (messages != null)
            {
                foreach (var msg in messages)
                {
                    byte.TryParse(msg.Dlc, out byte dlc);
                    tracker.Process(msg.CanIdValue, msg.IsExtended, msg.IsRtr, dlc, msg.DataBytes,
                        msg.Direction, msg.TimeValue, 0, msg.WallTime, transfers);
                }
            }
            tracker.FlushOpenTransfers(transfers);
            transfers.Sort((a, b) => a.StartTimeMs.CompareTo(b.StartTimeMs));
            return transfers;
        }

        /// <summary>Closes every open transfer, e.g. when the capture ends.</summary>
        public void FlushOpenTransfers(List<SdoTransfer> completed)
        {
            foreach (var channel in _channels.Values)
            {
                if (channel.Active != null)
                    Close(channel, "capture ended before the transfer finished", completed);
                channel.Phase = BlockPhase.None;
            }
        }

        // ------------------------------------------------------------------ client → server

        private void ProcessClientFrame(SdoFrame f, Channel ch, List<SdoTransfer> completed)
        {
            byte b = f.CommandByte;

            // 80h is an abort in every state: sequence number 0 is never a valid segment.
            if (b == 0x80)
            {
                HandleAbort(f, ch, "client", completed);
                return;
            }

            if (ch.Phase == BlockPhase.DownloadSegments)
            {
                HandleBlockSegment(f, ch, "Block Download");
                return;
            }

            int ccs = b >> 5;
            switch (ccs)
            {
                case 0:
                {
                    int toggle = (b >> 4) & 1;
                    int length = 7 - ((b >> 1) & 7);
                    bool last = (b & 1) != 0;
                    f.Service = "Download";
                    f.Command = $"Download Segment Request (t={toggle}, {length} bytes{(last ? ", last" : string.Empty)})";

                    var t = EnsureActive(ch, f, "Download", completed);
                    if (t.Mode == "Unknown" || t.Mode == "Expedited")
                        t.Mode = "Segmented";
                    Track(ch, f);
                    AppendSegment(ch, t, f, toggle, length);
                    if (last)
                    {
                        t.DataComplete = true;
                        ch.AwaitingConfirm = true;
                    }
                    break;
                }

                case 1:
                {
                    f.Service = "Download";
                    SetMultiplexer(f);
                    f.Command = "Initiate Download Request" + DescribeInitiatePayload(f);

                    var t = StartTransfer(ch, f, "Download", completed);
                    ch.Phase = BlockPhase.None;
                    ApplyInitiatePayload(t, f);
                    Track(ch, f);
                    break;
                }

                case 2:
                {
                    f.Service = "Upload";
                    SetMultiplexer(f);
                    f.Command = "Initiate Upload Request";

                    var t = StartTransfer(ch, f, "Upload", completed);
                    t.Mode = "Unknown";
                    ch.Phase = BlockPhase.None;
                    Track(ch, f);
                    break;
                }

                case 3:
                    f.Service = "Upload";
                    f.Command = $"Upload Segment Request (t={(b >> 4) & 1})";
                    EnsureActive(ch, f, "Upload", completed);
                    Track(ch, f);
                    ch.PeerSegmentCount++;
                    break;

                case 4:
                    HandleAbort(f, ch, "client", completed);
                    break;

                case 5:
                    f.Service = "Block Upload";
                    switch (b & 0x03)
                    {
                        case 0:
                        {
                            SetMultiplexer(f);
                            f.Command = $"Initiate Block Upload Request (blksize={f.Data[4]}, pst={f.Data[5]}{((b & 0x04) != 0 ? ", CRC" : string.Empty)})";
                            var t = StartTransfer(ch, f, "Upload", completed);
                            t.Mode = "Block";
                            ch.Phase = BlockPhase.None;
                            Track(ch, f);
                            break;
                        }
                        case 1:
                            f.Command = "End Block Upload Response";
                            ch.Phase = BlockPhase.None;
                            if (ch.Active != null)
                            {
                                Track(ch, f);
                                Complete(ch, completed);
                            }
                            break;
                        case 2:
                            f.Command = $"Block Upload Response (ackseq={f.Data[1]}, blksize={f.Data[2]})";
                            if (ch.Active != null)
                            {
                                Track(ch, f);
                                CommitSubBlock(ch, f.Data[1]);
                            }
                            // After the segment flagged last, the server sends End Block Upload
                            // Request (a normal command frame); otherwise another sub-block follows.
                            ch.Phase = ch.LastSegmentSeen ? BlockPhase.None : BlockPhase.UploadSegments;
                            break;
                        default:
                            f.Command = "Start Block Upload";
                            ch.Phase = BlockPhase.UploadSegments;
                            ch.LastSegmentSeen = false;
                            ch.SubBlock.Clear();
                            if (ch.Active != null)
                                Track(ch, f);
                            break;
                    }
                    break;

                case 6:
                    f.Service = "Block Download";
                    if ((b & 0x01) == 0)
                    {
                        SetMultiplexer(f);
                        bool sizeIndicated = (b & 0x02) != 0;
                        f.Command = "Initiate Block Download Request" +
                                    (sizeIndicated ? $" (size={ReadUInt32(f.Data, 4)} bytes" : " (size not indicated") +
                                    ((b & 0x04) != 0 ? ", CRC)" : ")");
                        var t = StartTransfer(ch, f, "Download", completed);
                        t.Mode = "Block";
                        if (sizeIndicated)
                            t.DeclaredSize = ReadUInt32(f.Data, 4);
                        ch.Phase = BlockPhase.None;
                        Track(ch, f);
                    }
                    else
                    {
                        int unused = (b >> 2) & 7;
                        f.Command = $"End Block Download Request ({7 - unused} bytes in last segment, CRC={ReadUInt16(f.Data, 1):X4}h)";
                        ch.Phase = BlockPhase.None;
                        if (ch.Active != null)
                        {
                            Track(ch, f);
                            TrimBlockPadding(ch.Active, unused);
                            ch.Active.DataComplete = true;
                            ch.AwaitingConfirm = true;
                        }
                    }
                    break;

                default:
                    f.Service = "Unknown";
                    f.Command = $"Invalid client command specifier ({b:X2}h)";
                    if (ch.Active != null)
                        Track(ch, f);
                    break;
            }
        }

        // ------------------------------------------------------------------ server → client

        private void ProcessServerFrame(SdoFrame f, Channel ch, List<SdoTransfer> completed)
        {
            byte b = f.CommandByte;

            if (b == 0x80)
            {
                HandleAbort(f, ch, "server", completed);
                return;
            }

            if (ch.Phase == BlockPhase.UploadSegments)
            {
                HandleBlockSegment(f, ch, "Block Upload");
                return;
            }

            int scs = b >> 5;
            switch (scs)
            {
                case 0:
                {
                    int toggle = (b >> 4) & 1;
                    int length = 7 - ((b >> 1) & 7);
                    bool last = (b & 1) != 0;
                    f.Service = "Upload";
                    f.Command = $"Upload Segment Response (t={toggle}, {length} bytes{(last ? ", last" : string.Empty)})";

                    var t = EnsureActive(ch, f, "Upload", completed);
                    if (t.Mode == "Unknown")
                        t.Mode = "Segmented";
                    Track(ch, f);
                    AppendSegment(ch, t, f, toggle, length);
                    if (last)
                    {
                        t.DataComplete = true;
                        Complete(ch, completed);
                    }
                    break;
                }

                case 1:
                    f.Service = "Download";
                    f.Command = $"Download Segment Response (t={(b >> 4) & 1})";
                    if (ch.Active != null && ch.Active.TransferType == "Download")
                    {
                        Track(ch, f);
                        ch.PeerSegmentCount++;
                        if (ch.AwaitingConfirm)
                            Complete(ch, completed);
                    }
                    break;

                case 2:
                {
                    f.Service = "Upload";
                    SetMultiplexer(f);
                    f.Command = "Initiate Upload Response" + DescribeInitiatePayload(f);

                    var t = EnsureActive(ch, f, "Upload", completed);
                    ApplyInitiatePayload(t, f);
                    Track(ch, f);
                    if (t.Mode == "Expedited")
                        Complete(ch, completed);
                    break;
                }

                case 3:
                {
                    f.Service = "Download";
                    SetMultiplexer(f);
                    f.Command = "Initiate Download Response";

                    var t = EnsureActive(ch, f, "Download", completed);
                    Track(ch, f);
                    if (t.Mode == "Expedited")
                        Complete(ch, completed);
                    break;
                }

                case 4:
                    HandleAbort(f, ch, "server", completed);
                    break;

                case 5:
                    f.Service = "Block Download";
                    switch (b & 0x03)
                    {
                        case 0:
                            SetMultiplexer(f);
                            f.Command = $"Initiate Block Download Response (blksize={f.Data[4]}{((b & 0x04) != 0 ? ", CRC" : string.Empty)})";
                            ch.Phase = BlockPhase.DownloadSegments;
                            ch.LastSegmentSeen = false;
                            ch.SubBlock.Clear();
                            if (ch.Active != null)
                                Track(ch, f);
                            break;
                        case 1:
                            f.Command = "End Block Download Response";
                            ch.Phase = BlockPhase.None;
                            if (ch.Active != null)
                            {
                                Track(ch, f);
                                Complete(ch, completed);
                            }
                            break;
                        case 2:
                            f.Command = $"Block Download Response (ackseq={f.Data[1]}, blksize={f.Data[2]})";
                            if (ch.Active != null)
                            {
                                Track(ch, f);
                                CommitSubBlock(ch, f.Data[1]);
                            }
                            // After the segment flagged last, the client sends End Block
                            // Download Request; otherwise it starts the next sub-block.
                            ch.Phase = ch.LastSegmentSeen ? BlockPhase.None : BlockPhase.DownloadSegments;
                            break;
                        default:
                            f.Service = "Unknown";
                            f.Command = $"Invalid block download sub-command ({b:X2}h)";
                            if (ch.Active != null)
                                Track(ch, f);
                            break;
                    }
                    break;

                case 6:
                    f.Service = "Block Upload";
                    if ((b & 0x01) == 0)
                    {
                        SetMultiplexer(f);
                        bool sizeIndicated = (b & 0x02) != 0;
                        f.Command = "Initiate Block Upload Response" +
                                    (sizeIndicated ? $" (size={ReadUInt32(f.Data, 4)} bytes" : " (size not indicated") +
                                    ((b & 0x04) != 0 ? ", CRC)" : ")");
                        if (ch.Active != null)
                        {
                            if (sizeIndicated)
                                ch.Active.DeclaredSize = ReadUInt32(f.Data, 4);
                            Track(ch, f);
                        }
                    }
                    else
                    {
                        int unused = (b >> 2) & 7;
                        f.Command = $"End Block Upload Request ({7 - unused} bytes in last segment, CRC={ReadUInt16(f.Data, 1):X4}h)";
                        ch.Phase = BlockPhase.None;
                        if (ch.Active != null)
                        {
                            Track(ch, f);
                            TrimBlockPadding(ch.Active, unused);
                            ch.Active.DataComplete = true;
                            ch.AwaitingConfirm = true;
                        }
                    }
                    break;

                default:
                    f.Service = "Unknown";
                    f.Command = $"Invalid server command specifier ({b:X2}h)";
                    if (ch.Active != null)
                        Track(ch, f);
                    break;
            }
        }

        // ------------------------------------------------------------------ transfer assembly

        private static SdoTransfer NewTransfer(SdoFrame f, string type) => new SdoTransfer
        {
            TransferType = type,
            Mode = "Unknown",
            Scheme = f.Scheme,
            ClientNodeId = f.ClientNodeId,
            ServerNodeId = f.ServerNodeId,
            SdoNumber = f.SdoNumber,
            Index = f.Index,
            SubIndex = f.SubIndex,
            StartTimeMs = f.TimestampMs,
            EndTimeMs = f.TimestampMs,
            StartWallTime = f.WallTime,
            EndWallTime = f.WallTime
        };

        /// <summary>An initiate frame: any transfer still open on the channel never finished.</summary>
        private static SdoTransfer StartTransfer(Channel ch, SdoFrame f, string type, List<SdoTransfer> completed)
        {
            if (ch.Active != null)
                Close(ch, "superseded by a new transfer on the same SDO channel", completed);

            ch.ResetTransferState();
            ch.Active = NewTransfer(f, type);
            return ch.Active;
        }

        /// <summary>
        /// A continuation frame. With no matching open transfer the start was not captured;
        /// the data is still collected and the transfer flagged.
        /// </summary>
        private static SdoTransfer EnsureActive(Channel ch, SdoFrame f, string type, List<SdoTransfer> completed)
        {
            if (ch.Active != null && ch.Active.TransferType == type)
            {
                if (!ch.Active.Index.HasValue && f.Index.HasValue)
                {
                    ch.Active.Index = f.Index;
                    ch.Active.SubIndex = f.SubIndex;
                }
                return ch.Active;
            }

            var t = StartTransfer(ch, f, type, completed);
            t.AddIssue("start of transfer not captured");
            ch.StartedMidStream = true;
            return t;
        }

        private static void Track(Channel ch, SdoFrame f)
        {
            var t = ch.Active;
            if (t == null)
                return;

            if (ch.PendingAbort != null && !ReferenceEquals(ch.PendingAbort, f))
            {
                var abort = ch.PendingAbort;
                t.AddIssue($"abort for another object ({abort.IndexDisplay} sub {abort.SubIndexDisplay}, {abort.AbortCode.Value:X8}h) seen mid-transfer; transfer continued");
                abort.Command += " — transfer continued";
                ch.PendingAbort = null;
            }

            t.Frames.Add(f);
            f.Transfer = t;
            if (f.TimestampMs >= t.EndTimeMs)
            {
                t.EndTimeMs = f.TimestampMs;
                t.EndWallTime = f.WallTime;
            }
        }

        private static void ApplyInitiatePayload(SdoTransfer t, SdoFrame f)
        {
            byte b = f.CommandByte;
            bool expedited = (b & 0x02) != 0;
            bool sizeIndicated = (b & 0x01) != 0;

            if (expedited)
            {
                int length = sizeIndicated ? 4 - ((b >> 2) & 0x03) : 4;
                t.Mode = "Expedited";
                t.PayloadBuffer.Clear();
                for (int i = 0; i < length; i++)
                    t.PayloadBuffer.Add(f.Data[4 + i]);
                t.DeclaredSize = sizeIndicated ? length : (long?)null;
                t.DataComplete = true;
            }
            else
            {
                t.Mode = "Segmented";
                t.DeclaredSize = sizeIndicated ? ReadUInt32(f.Data, 4) : (long?)null;
            }
        }

        private static void AppendSegment(Channel ch, SdoTransfer t, SdoFrame f, int toggle, int length)
        {
            ch.SegmentCount++;

            // The first segment of a transfer whose start was missed has no expected toggle.
            bool toggleKnown = !(ch.StartedMidStream && ch.SegmentCount == 1);
            if (toggleKnown && toggle != ch.NextToggle)
                ch.ToggleErrors++;
            ch.NextToggle = toggle ^ 1;

            for (int i = 1; i <= length; i++)
                t.PayloadBuffer.Add(f.Data[i]);
        }

        private static void HandleBlockSegment(SdoFrame f, Channel ch, string service)
        {
            byte b = f.CommandByte;
            int seqno = b & 0x7F;
            bool last = (b & 0x80) != 0;
            f.Service = service;
            f.Command = $"Block Segment (seqno={seqno}{(last ? ", last" : string.Empty)})";
            if (last)
                ch.LastSegmentSeen = true;

            if (ch.Active == null)
                return;

            Track(ch, f);
            var bytes = new byte[7];
            Array.Copy(f.Data, 1, bytes, 0, 7);
            ch.SubBlock[seqno] = bytes; // a retransmitted segment replaces the earlier copy
        }

        /// <summary>The receiver acknowledged segments 1..ackseq; anything later will be resent.</summary>
        private static void CommitSubBlock(Channel ch, int ackseq)
        {
            var t = ch.Active;
            for (int seq = 1; seq <= ackseq; seq++)
            {
                if (ch.SubBlock.TryGetValue(seq, out var bytes))
                    t.PayloadBuffer.AddRange(bytes);
                else
                    t.AddIssue($"block segment {seq} acknowledged but not captured");
            }
            ch.SubBlock.Clear();
        }

        private static void TrimBlockPadding(SdoTransfer t, int unused)
        {
            int remove = Math.Min(unused, t.PayloadBuffer.Count);
            if (remove > 0)
                t.PayloadBuffer.RemoveRange(t.PayloadBuffer.Count - remove, remove);
        }

        private static void HandleAbort(SdoFrame f, Channel ch, string by, List<SdoTransfer> completed)
        {
            SetMultiplexer(f);
            f.Service = "Abort";
            f.AbortCode = ReadUInt32(f.Data, 4);
            f.Command = $"Abort Transfer by {by}: {DescribeAbortCode(f.AbortCode.Value)}";
            ch.Phase = BlockPhase.None;

            var t = ch.Active;
            if (t == null)
                return;

            Track(ch, f);

            // An abort carries the multiplexer of the transfer it ends, but captures show nodes
            // sending aborts that name another object (9A1D37h during a 6304h download): some
            // are followed by the rest of the transfer, others really end it. Such an abort
            // ends this transfer only if nothing more of it arrives.
            bool matches = !t.Index.HasValue || (f.Index == t.Index && f.SubIndex == t.SubIndex);
            if (!matches)
            {
                ch.PendingAbort = f;
                return;
            }

            t.AbortCode = f.AbortCode;
            t.AddIssue($"aborted by {by}: {f.AbortCode.Value:X8}h {DescribeAbortCode(f.AbortCode.Value)}");
            Finish(ch, "Aborted", completed);
        }

        /// <summary>The protocol confirmed the end of the transfer.</summary>
        private static void Complete(Channel ch, List<SdoTransfer> completed)
        {
            var t = ch.Active;
            if (t == null)
                return;

            Finish(ch, CheckSize(t) ? "Complete" : "Incomplete", completed);
        }

        /// <summary>Closed without the protocol's final confirmation.</summary>
        private static void Close(Channel ch, string reason, List<SdoTransfer> completed)
        {
            var t = ch.Active;
            if (t == null)
                return;

            // Nothing of the transfer followed its abort frame: it was this transfer's abort.
            var abort = ch.PendingAbort;
            if (abort != null)
            {
                ch.PendingAbort = null;
                t.AbortCode = abort.AbortCode;
                t.AddIssue($"aborted: {abort.AbortCode.Value:X8}h {DescribeAbortCode(abort.AbortCode.Value)} (abort frame named object {abort.IndexDisplay} sub {abort.SubIndexDisplay})");
                CheckSize(t);
                Finish(ch, "Aborted", completed);
                return;
            }

            t.AddIssue(reason);
            string status = t.DataComplete && CheckSize(t) ? "Complete (unconfirmed)" : "Incomplete";
            Finish(ch, status, completed);
        }

        private static bool CheckSize(SdoTransfer t)
        {
            if (t.DeclaredSize.HasValue && t.DeclaredSize.Value != t.PayloadSize)
            {
                t.AddIssue($"received {t.PayloadSize} of {t.DeclaredSize.Value} declared bytes");
                return false;
            }
            return true;
        }

        private static void Finish(Channel ch, string status, List<SdoTransfer> completed)
        {
            var t = ch.Active;

            // One summary per transfer rather than a line per segment.
            if (ch.PeerSegmentCount > ch.SegmentCount)
            {
                t.AddIssue($"{ch.PeerSegmentCount - ch.SegmentCount} of {ch.PeerSegmentCount} segments handshaken on the bus were not captured");
                if (status == "Complete" || status == "Complete (unconfirmed)")
                    status = "Incomplete";
            }
            if (ch.ToggleErrors > 0)
                t.AddIssue($"toggle bit did not alternate {ch.ToggleErrors} time(s): segments lost or repeated");

            t.Status = status;
            completed?.Add(t);
            ch.ResetTransferState();
        }

        // ------------------------------------------------------------------ helpers

        private static void SetMultiplexer(SdoFrame f)
        {
            f.Index = ReadUInt16(f.Data, 1);
            f.SubIndex = f.Data[3];
        }

        /// <summary>Expedited / segmented details of an initiate download request or upload response.</summary>
        private static string DescribeInitiatePayload(SdoFrame f)
        {
            byte b = f.CommandByte;
            bool expedited = (b & 0x02) != 0;
            bool sizeIndicated = (b & 0x01) != 0;

            if (expedited)
            {
                int length = sizeIndicated ? 4 - ((b >> 2) & 0x03) : 4;
                var sb = new StringBuilder(" (expedited, ");
                sb.Append(sizeIndicated ? $"{length} bytes" : "size not indicated");
                sb.Append(", data=");
                for (int i = 0; i < length; i++)
                    sb.Append(f.Data[4 + i].ToString("X2")).Append(i < length - 1 ? " " : string.Empty);
                sb.Append(')');
                return sb.ToString();
            }

            return sizeIndicated
                ? $" (segmented, size={ReadUInt32(f.Data, 4)} bytes)"
                : " (segmented, size not indicated)";
        }

        private static ushort ReadUInt16(byte[] data, int offset) =>
            (ushort)(data[offset] | (data[offset + 1] << 8));

        private static uint ReadUInt32(byte[] data, int offset) =>
            (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

        internal static string FormatBytes(IReadOnlyList<byte> data, int count)
        {
            if (data == null || count <= 0)
                return string.Empty;

            int n = Math.Min(count, data.Count);
            var sb = new StringBuilder(n * 3);
            for (int i = 0; i < n; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                sb.Append(data[i].ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>SDO abort codes from CiA 301.</summary>
        public static string DescribeAbortCode(uint code)
        {
            switch (code)
            {
                case 0x05030000: return "Toggle bit not alternated";
                case 0x05040000: return "SDO protocol timed out";
                case 0x05040001: return "Client/server command specifier not valid or unknown";
                case 0x05040002: return "Invalid block size";
                case 0x05040003: return "Invalid sequence number";
                case 0x05040004: return "CRC error";
                case 0x05040005: return "Out of memory";
                case 0x06010000: return "Unsupported access to an object";
                case 0x06010001: return "Attempt to read a write-only object";
                case 0x06010002: return "Attempt to write a read-only object";
                case 0x06020000: return "Object does not exist in the object dictionary";
                case 0x06040041: return "Object cannot be mapped to the PDO";
                case 0x06040042: return "Mapped objects would exceed PDO length";
                case 0x06040043: return "General parameter incompatibility";
                case 0x06040047: return "General internal incompatibility in the device";
                case 0x06060000: return "Access failed due to a hardware error";
                case 0x06070010: return "Data type does not match, length of service parameter does not match";
                case 0x06070012: return "Data type does not match, length of service parameter too high";
                case 0x06070013: return "Data type does not match, length of service parameter too low";
                case 0x06090011: return "Sub-index does not exist";
                case 0x06090030: return "Invalid value for parameter";
                case 0x06090031: return "Value of parameter written too high";
                case 0x06090032: return "Value of parameter written too low";
                case 0x06090036: return "Maximum value is less than minimum value";
                case 0x060A0023: return "Resource not available: SDO connection";
                case 0x08000000: return "General error";
                case 0x08000020: return "Data cannot be transferred or stored to the application";
                case 0x08000021: return "Data cannot be transferred or stored because of local control";
                case 0x08000022: return "Data cannot be transferred or stored because of the present device state";
                case 0x08000023: return "Object dictionary dynamic generation failed or no object dictionary present";
                case 0x08000024: return "No data available";
                default: return "Unknown abort code";
            }
        }
    }
}
