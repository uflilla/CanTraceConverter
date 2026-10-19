using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text;
using System.Threading;
using CanTraceConverter.Models;

namespace CanTraceConverter.Services
{
    public sealed class CanMessageRepository : IDisposable
    {
        /// <summary>Upper bound on unwritten messages. Beyond this the writer is not keeping
        /// up (or is failing), and growing without limit would end in OutOfMemoryException.</summary>
        private const int MaxPendingMessages = 250000;

        /// <summary>
        /// sdo_transfers: one row per reassembled SDO transfer — the full packet a node split
        /// into SDO frames, with when, from which node, to which node, and whether it arrived
        /// intact. sdo_messages: every individual SDO frame, linked to its raw can_messages row
        /// and to the transfer it belongs to.
        /// </summary>
        private const string SdoSchema = @"
CREATE TABLE IF NOT EXISTS sdo_transfers (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  start_time_ms REAL NOT NULL,
  end_time_ms REAL NOT NULL,
  duration_ms REAL NOT NULL,
  start_wall_time TEXT,
  end_wall_time TEXT,
  transfer_type TEXT NOT NULL,
  transfer_mode TEXT NOT NULL,
  addressing TEXT NOT NULL,
  sender_node_id INTEGER NOT NULL,
  receiver_node_id INTEGER NOT NULL,
  client_node_id INTEGER NOT NULL,
  server_node_id INTEGER NOT NULL,
  sdo_number INTEGER NOT NULL,
  od_index INTEGER,
  od_subindex INTEGER,
  declared_size INTEGER,
  payload_size INTEGER NOT NULL,
  payload BLOB NOT NULL,
  payload_hex TEXT NOT NULL,
  status TEXT NOT NULL,
  abort_code INTEGER,
  issues TEXT,
  frame_count INTEGER NOT NULL,
  first_can_message_id INTEGER,
  last_can_message_id INTEGER
);
CREATE INDEX IF NOT EXISTS idx_sdo_transfers_time ON sdo_transfers(start_time_ms);
CREATE INDEX IF NOT EXISTS idx_sdo_transfers_sender ON sdo_transfers(sender_node_id);
CREATE INDEX IF NOT EXISTS idx_sdo_transfers_receiver ON sdo_transfers(receiver_node_id);
CREATE TABLE IF NOT EXISTS sdo_messages (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  can_message_id INTEGER NOT NULL REFERENCES can_messages(id),
  transfer_id INTEGER REFERENCES sdo_transfers(id),
  timestamp_ms REAL NOT NULL,
  raw_hw_time INTEGER NOT NULL,
  wall_time TEXT NOT NULL,
  bus_direction TEXT NOT NULL,
  sdo_direction TEXT NOT NULL,
  addressing TEXT NOT NULL,
  cob_id INTEGER NOT NULL,
  sender_node_id INTEGER NOT NULL,
  receiver_node_id INTEGER NOT NULL,
  server_node_id INTEGER NOT NULL,
  client_node_id INTEGER NOT NULL,
  sdo_number INTEGER NOT NULL,
  service TEXT NOT NULL,
  command TEXT NOT NULL,
  command_byte INTEGER NOT NULL,
  od_index INTEGER,
  od_subindex INTEGER,
  abort_code INTEGER,
  dlc INTEGER NOT NULL,
  data BLOB NOT NULL,
  data_hex TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sdo_messages_time ON sdo_messages(timestamp_ms);
CREATE INDEX IF NOT EXISTS idx_sdo_messages_transfer ON sdo_messages(transfer_id);";

        private readonly ConcurrentQueue<CanMessageRecord> _pending = new ConcurrentQueue<CanMessageRecord>();

        /// <summary>Batch that failed to write, kept in order and retried before new messages.</summary>
        private readonly Queue<CanMessageRecord> _retry = new Queue<CanMessageRecord>();

        /// <summary>Serialises every use of <see cref="_connection"/> — reads included.
        /// An ADO.NET connection is not safe for concurrent use.</summary>
        private readonly object _dbLock = new object();

        private SQLiteConnection _connection;
        private Thread _writerThread;
        private volatile bool _running;
        private long _totalWritten;
        private long _totalSdoWritten;
        private long _totalTransfersWritten;
        private long _totalDropped;

        /// <summary>Writer side only, under <see cref="_dbLock"/>: frames must reach it in capture order.</summary>
        private SdoTracker _sdoTracker;
        private readonly List<SdoTransfer> _completedScratch = new List<SdoTransfer>();
        private string _headerText;
        private int _disposed;

        public string DatabasePath { get; private set; }
        public string HeaderText => _headerText;
        public long TotalWritten => Interlocked.Read(ref _totalWritten);
        public long TotalSdoWritten => Interlocked.Read(ref _totalSdoWritten);
        public long TotalTransfersWritten => Interlocked.Read(ref _totalTransfersWritten);

        /// <summary>One-line description of the most recently stored SDO transfer.</summary>
        public string LastTransferSummary { get; private set; }
        public long TotalDropped => Interlocked.Read(ref _totalDropped);
        public int PendingCount => _pending.Count;
        public bool IsOpen => _connection != null;

        public void Open(SessionConfig config, string headerText)
        {
            if (IsOpen)
                return;

            DatabasePath = config.BuildDatabasePath();
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath));
            _headerText = headerText ?? string.Empty;

            _connection = new SQLiteConnection($"Data Source={DatabasePath};Version=3;");
            _connection.Open();

            using (var cmd = _connection.CreateCommand())
            {
                // WAL lets the analysis window read this file while the writer is committing,
                // and NORMAL removes an fsync from every batch commit.
                cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=5000;";
                cmd.ExecuteNonQuery();
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS session_info (
  key TEXT PRIMARY KEY,
  value TEXT
);
CREATE TABLE IF NOT EXISTS can_messages (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  direction TEXT NOT NULL,
  timestamp_ms REAL NOT NULL,
  raw_hw_time INTEGER NOT NULL,
  wall_time TEXT NOT NULL,
  can_id INTEGER NOT NULL,
  is_extended INTEGER NOT NULL,
  is_rtr INTEGER NOT NULL,
  dlc INTEGER NOT NULL,
  data BLOB NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_can_messages_time ON can_messages(timestamp_ms);
CREATE INDEX IF NOT EXISTS idx_can_messages_can_id ON can_messages(can_id);
" + SdoSchema;
                cmd.ExecuteNonQuery();
            }

            // Every SDO is captured whatever its client: 29-bit IDs name both nodes, and an
            // 11-bit 600h/580h ID names only the server, so its client is left as 0.
            _sdoTracker = new SdoTracker(0);

            SetSessionInfo("title", config.Title);
            SetSessionInfo("device", config.DeviceNumber.ToString());
            SetSessionInfo("channel", config.Channel.ToString());
            SetSessionInfo("baudrate_kbps", config.BaudRateKbps.ToString());
            SetSessionInfo("listen_only", config.ListenOnly ? "1" : "0");
            SetSessionInfo("header", _headerText);
            SetSessionInfo("started_at", DateTime.Now.ToString("o"));

            _running = true;
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "CanSqliteWriter"
            };
            _writerThread.Start();
        }

        private void SetSessionInfo(string key, string value)
        {
            lock (_dbLock)
            {
                if (_connection == null)
                    return;

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR REPLACE INTO session_info(key, value) VALUES(@k, @v)";
                    cmd.Parameters.AddWithValue("@k", key);
                    cmd.Parameters.AddWithValue("@v", value);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void Enqueue(CanMessageRecord record)
        {
            if (record == null || !_running)
                return;

            if (_pending.Count >= MaxPendingMessages)
            {
                // Losing messages loudly beats dying silently: the counter is surfaced in
                // the monitor's status line.
                if (Interlocked.Increment(ref _totalDropped) == 1)
                    AppLog.Warn("SQLite write queue reached {0} messages; further messages are being dropped.", MaxPendingMessages);
                return;
            }

            _pending.Enqueue(record);
        }

        public void Flush()
        {
            FlushUntilEmpty(TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Writes every queued message. Retries until the queue is empty or the timeout elapses.
        /// </summary>
        public bool FlushUntilEmpty(TimeSpan timeout)
        {
            if (!IsOpen)
                return true;

            var deadline = DateTime.UtcNow + timeout;
            while (HasWork)
            {
                try
                {
                    FlushBatch(int.MaxValue);
                }
                catch (Exception ex)
                {
                    AppLog.Warn(ex, "SQLite flush attempt failed; messages remain queued.");
                }

                if (!HasWork)
                    return true;

                if (DateTime.UtcNow >= deadline)
                {
                    AppLog.Warn("SQLite flush timed out with {0} message(s) still queued.", PendingTotal);
                    return false;
                }

                Thread.Sleep(10);
            }

            return true;
        }

        private bool HasWork
        {
            get
            {
                if (!_pending.IsEmpty)
                    return true;
                lock (_dbLock)
                    return _retry.Count > 0;
            }
        }

        private int PendingTotal
        {
            get
            {
                lock (_dbLock)
                    return _pending.Count + _retry.Count;
            }
        }

        private void WriterLoop()
        {
            while (_running)
            {
                try
                {
                    FlushBatch(500);
                }
                catch (Exception ex)
                {
                    AppLog.Warn(ex, "SQLite writer flush failed; messages remain queued.");
                    Thread.Sleep(100);
                }

                if (!HasWork)
                    Thread.Sleep(250);
            }

            // Final drain after stop — keep trying until empty.
            try
            {
                FlushUntilEmpty(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "SQLite final drain failed.");
            }
        }

        private void FlushBatch(int maxBatch)
        {
            if (!IsOpen)
                return;

            lock (_dbLock)
            {
                if (_connection == null)
                    return;

                var batch = new List<CanMessageRecord>();

                // Anything that failed last time goes first, in its original order.
                while (batch.Count < maxBatch && _retry.Count > 0)
                    batch.Add(_retry.Dequeue());

                while (batch.Count < maxBatch && _pending.TryDequeue(out var item))
                {
                    // Decoded exactly once, in capture order: the tracker reassembles
                    // transfers across frames, and a retried batch must not replay into it.
                    if (_sdoTracker != null)
                    {
                        item.Sdo = _sdoTracker.Process(item, _completedScratch);
                        if (_completedScratch.Count > 0)
                        {
                            item.CompletedTransfers = _completedScratch.ToArray();
                            _completedScratch.Clear();
                        }
                    }
                    batch.Add(item);
                }

                if (batch.Count == 0)
                    return;

                int sdoCount = 0;
                int transferCount = 0;
                SdoTransfer lastTransfer = null;
                try
                {
                    using (var tx = _connection.BeginTransaction())
                    using (var cmd = _connection.CreateCommand())
                    using (var sdoCmd = CreateSdoInsertCommand(tx))
                    using (var transferCmd = CreateTransferInsertCommand(tx))
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"INSERT INTO can_messages
(direction, timestamp_ms, raw_hw_time, wall_time, can_id, is_extended, is_rtr, dlc, data)
VALUES(@dir, @ts, @raw, @wall, @id, @ext, @rtr, @dlc, @data)";
                        var pDir = cmd.Parameters.Add("@dir", System.Data.DbType.String);
                        var pTs = cmd.Parameters.Add("@ts", System.Data.DbType.Double);
                        var pRaw = cmd.Parameters.Add("@raw", System.Data.DbType.Int64);
                        var pWall = cmd.Parameters.Add("@wall", System.Data.DbType.String);
                        var pId = cmd.Parameters.Add("@id", System.Data.DbType.Int64);
                        var pExt = cmd.Parameters.Add("@ext", System.Data.DbType.Int32);
                        var pRtr = cmd.Parameters.Add("@rtr", System.Data.DbType.Int32);
                        var pDlc = cmd.Parameters.Add("@dlc", System.Data.DbType.Int32);
                        var pData = cmd.Parameters.Add("@data", System.Data.DbType.Binary);

                        foreach (var m in batch)
                        {
                            pDir.Value = m.Direction;
                            pTs.Value = m.TimestampMs;
                            pRaw.Value = m.RawHardwareTime;
                            pWall.Value = m.WallTime.ToString("o");
                            pId.Value = (long)m.CanId;
                            pExt.Value = m.IsExtended ? 1 : 0;
                            pRtr.Value = m.IsRtr ? 1 : 0;
                            pDlc.Value = m.Dlc;
                            pData.Value = m.Data ?? new byte[8];
                            cmd.ExecuteNonQuery();

                            if (m.Sdo != null)
                            {
                                m.Sdo.CanMessageId = _connection.LastInsertRowId;
                                WriteSdo(sdoCmd, m.Sdo);
                                m.Sdo.DbId = _connection.LastInsertRowId;
                                sdoCount++;
                            }

                            // Written after the frame that finished them, so every frame of
                            // the transfer already has its row id (this batch or an earlier,
                            // committed one).
                            if (m.CompletedTransfers != null)
                            {
                                foreach (var transfer in m.CompletedTransfers)
                                {
                                    WriteTransfer(transferCmd, tx, transfer);
                                    transferCount++;
                                    lastTransfer = transfer;
                                }
                            }
                        }
                        tx.Commit();
                    }

                    Interlocked.Add(ref _totalWritten, batch.Count);
                    Interlocked.Add(ref _totalSdoWritten, sdoCount);
                    Interlocked.Add(ref _totalTransfersWritten, transferCount);
                    if (lastTransfer != null)
                        LastTransferSummary = DescribeTransfer(lastTransfer);
                }
                catch
                {
                    // Preserve order: the batch is retried ahead of anything newer, in the
                    // sequence it was received.
                    var carried = new Queue<CanMessageRecord>(batch);
                    while (_retry.Count > 0)
                        carried.Enqueue(_retry.Dequeue());
                    while (carried.Count > 0)
                        _retry.Enqueue(carried.Dequeue());
                    throw;
                }
            }
        }

        private SQLiteCommand CreateSdoInsertCommand(SQLiteTransaction tx)
        {
            var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO sdo_messages
(can_message_id, timestamp_ms, raw_hw_time, wall_time, bus_direction, sdo_direction, addressing, cob_id,
 sender_node_id, receiver_node_id, server_node_id, client_node_id, sdo_number, service, command, command_byte,
 od_index, od_subindex, abort_code, dlc, data, data_hex)
VALUES(@msg, @ts, @raw, @wall, @bdir, @sdir, @addr, @cob, @snd, @rcv, @srv, @cli, @num, @svc, @cmd, @cb,
 @idx, @sub, @abort, @dlc, @data, @hex)";
            cmd.Parameters.Add("@msg", System.Data.DbType.Int64);
            cmd.Parameters.Add("@addr", System.Data.DbType.String);
            cmd.Parameters.Add("@num", System.Data.DbType.Int32);
            cmd.Parameters.Add("@ts", System.Data.DbType.Double);
            cmd.Parameters.Add("@raw", System.Data.DbType.Int64);
            cmd.Parameters.Add("@wall", System.Data.DbType.String);
            cmd.Parameters.Add("@bdir", System.Data.DbType.String);
            cmd.Parameters.Add("@sdir", System.Data.DbType.String);
            cmd.Parameters.Add("@cob", System.Data.DbType.Int64);
            cmd.Parameters.Add("@snd", System.Data.DbType.Int32);
            cmd.Parameters.Add("@rcv", System.Data.DbType.Int32);
            cmd.Parameters.Add("@srv", System.Data.DbType.Int32);
            cmd.Parameters.Add("@cli", System.Data.DbType.Int32);
            cmd.Parameters.Add("@svc", System.Data.DbType.String);
            cmd.Parameters.Add("@cmd", System.Data.DbType.String);
            cmd.Parameters.Add("@cb", System.Data.DbType.Int32);
            cmd.Parameters.Add("@idx", System.Data.DbType.Int32);
            cmd.Parameters.Add("@sub", System.Data.DbType.Int32);
            cmd.Parameters.Add("@abort", System.Data.DbType.Int64);
            cmd.Parameters.Add("@dlc", System.Data.DbType.Int32);
            cmd.Parameters.Add("@data", System.Data.DbType.Binary);
            cmd.Parameters.Add("@hex", System.Data.DbType.String);
            return cmd;
        }

        private static void WriteSdo(SQLiteCommand cmd, SdoFrame sdo)
        {
            var p = cmd.Parameters;
            p["@msg"].Value = sdo.CanMessageId;
            p["@ts"].Value = sdo.TimestampMs;
            p["@raw"].Value = sdo.RawHardwareTime;
            p["@wall"].Value = (sdo.WallTime ?? DateTime.Now).ToString("o");
            p["@bdir"].Value = sdo.BusDirection ?? string.Empty;
            p["@sdir"].Value = sdo.Direction == SdoDirection.ClientToServer ? "client->server" : "server->client";
            p["@addr"].Value = sdo.Scheme == SdoAddressingScheme.Extended ? "extended" : "standard";
            p["@cob"].Value = (long)sdo.CobId;
            p["@snd"].Value = (int)sdo.SenderNodeId;
            p["@rcv"].Value = (int)sdo.ReceiverNodeId;
            p["@srv"].Value = (int)sdo.ServerNodeId;
            p["@cli"].Value = (int)sdo.ClientNodeId;
            p["@num"].Value = (int)sdo.SdoNumber;
            p["@svc"].Value = sdo.Service ?? string.Empty;
            p["@cmd"].Value = sdo.Command ?? string.Empty;
            p["@cb"].Value = (int)sdo.CommandByte;
            p["@idx"].Value = sdo.Index.HasValue ? (object)(int)sdo.Index.Value : DBNull.Value;
            p["@sub"].Value = sdo.SubIndex.HasValue ? (object)(int)sdo.SubIndex.Value : DBNull.Value;
            p["@abort"].Value = sdo.AbortCode.HasValue ? (object)(long)sdo.AbortCode.Value : DBNull.Value;
            p["@dlc"].Value = (int)sdo.Dlc;
            p["@data"].Value = sdo.Data ?? new byte[8];
            p["@hex"].Value = sdo.DataHex;
            cmd.ExecuteNonQuery();
        }

        private SQLiteCommand CreateTransferInsertCommand(SQLiteTransaction tx)
        {
            var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO sdo_transfers
(start_time_ms, end_time_ms, duration_ms, start_wall_time, end_wall_time, transfer_type, transfer_mode, addressing,
 sender_node_id, receiver_node_id, client_node_id, server_node_id, sdo_number, od_index, od_subindex,
 declared_size, payload_size, payload, payload_hex, status, abort_code, issues, frame_count,
 first_can_message_id, last_can_message_id)
VALUES(@start, @end, @dur, @swall, @ewall, @type, @mode, @addr, @snd, @rcv, @cli, @srv, @num, @idx, @sub,
 @decl, @size, @payload, @hex, @status, @abort, @issues, @frames, @first, @last)";
            foreach (var name in new[] { "@start", "@end", "@dur" })
                cmd.Parameters.Add(name, System.Data.DbType.Double);
            foreach (var name in new[] { "@swall", "@ewall", "@type", "@mode", "@addr", "@hex", "@status", "@issues" })
                cmd.Parameters.Add(name, System.Data.DbType.String);
            foreach (var name in new[] { "@snd", "@rcv", "@cli", "@srv", "@num", "@idx", "@sub", "@size", "@frames" })
                cmd.Parameters.Add(name, System.Data.DbType.Int32);
            foreach (var name in new[] { "@decl", "@abort", "@first", "@last" })
                cmd.Parameters.Add(name, System.Data.DbType.Int64);
            cmd.Parameters.Add("@payload", System.Data.DbType.Binary);
            return cmd;
        }

        private void WriteTransfer(SQLiteCommand cmd, SQLiteTransaction tx, SdoTransfer t)
        {
            long firstId = 0, lastId = 0;
            foreach (var frame in t.Frames)
            {
                if (frame.CanMessageId <= 0)
                    continue;
                if (firstId == 0 || frame.CanMessageId < firstId)
                    firstId = frame.CanMessageId;
                if (frame.CanMessageId > lastId)
                    lastId = frame.CanMessageId;
            }

            var p = cmd.Parameters;
            p["@start"].Value = t.StartTimeMs;
            p["@end"].Value = t.EndTimeMs;
            p["@dur"].Value = t.DurationMs;
            p["@swall"].Value = t.StartWallTime.HasValue ? (object)t.StartWallTime.Value.ToString("o") : DBNull.Value;
            p["@ewall"].Value = t.EndWallTime.HasValue ? (object)t.EndWallTime.Value.ToString("o") : DBNull.Value;
            p["@type"].Value = t.TransferType;
            p["@mode"].Value = t.Mode;
            p["@addr"].Value = t.Scheme == SdoAddressingScheme.Extended ? "extended" : "standard";
            p["@snd"].Value = (int)t.SenderNodeId;
            p["@rcv"].Value = (int)t.ReceiverNodeId;
            p["@cli"].Value = (int)t.ClientNodeId;
            p["@srv"].Value = (int)t.ServerNodeId;
            p["@num"].Value = (int)t.SdoNumber;
            p["@idx"].Value = t.Index.HasValue ? (object)(int)t.Index.Value : DBNull.Value;
            p["@sub"].Value = t.SubIndex.HasValue ? (object)(int)t.SubIndex.Value : DBNull.Value;
            p["@decl"].Value = t.DeclaredSize.HasValue ? (object)t.DeclaredSize.Value : DBNull.Value;
            p["@size"].Value = t.PayloadSize;
            p["@payload"].Value = t.Payload;
            p["@hex"].Value = t.PayloadHex;
            p["@status"].Value = t.Status;
            p["@abort"].Value = t.AbortCode.HasValue ? (object)(long)t.AbortCode.Value : DBNull.Value;
            p["@issues"].Value = t.Issues.Count > 0 ? (object)t.IssuesDisplay : DBNull.Value;
            p["@frames"].Value = t.Frames.Count;
            p["@first"].Value = firstId > 0 ? (object)firstId : DBNull.Value;
            p["@last"].Value = lastId > 0 ? (object)lastId : DBNull.Value;
            cmd.ExecuteNonQuery();
            t.DbId = _connection.LastInsertRowId;

            // Link the individual frames to their transfer.
            var frameIds = new List<string>(t.Frames.Count);
            foreach (var frame in t.Frames)
            {
                if (frame.DbId > 0)
                    frameIds.Add(frame.DbId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (frameIds.Count > 0)
            {
                using (var link = _connection.CreateCommand())
                {
                    link.Transaction = tx;
                    // Ids are integers this process generated, never user text.
                    link.CommandText = "UPDATE sdo_messages SET transfer_id=@t WHERE id IN (" + string.Join(",", frameIds) + ")";
                    link.Parameters.AddWithValue("@t", t.DbId);
                    link.ExecuteNonQuery();
                }
            }
        }

        private static string DescribeTransfer(SdoTransfer t) =>
            $"{t.TransferType} {t.RouteDisplay} · {t.IndexDisplay} · {t.PayloadSize} bytes · {t.Status}";

        /// <summary>
        /// Writes transfers still open when the session ends, so a packet cut off by the
        /// end of the capture is still on record (flagged incomplete).
        /// </summary>
        private void WriteOpenTransfers()
        {
            lock (_dbLock)
            {
                if (_connection == null || _sdoTracker == null)
                    return;

                var open = new List<SdoTransfer>();
                _sdoTracker.FlushOpenTransfers(open);
                if (open.Count == 0)
                    return;

                using (var tx = _connection.BeginTransaction())
                using (var cmd = CreateTransferInsertCommand(tx))
                {
                    foreach (var transfer in open)
                        WriteTransfer(cmd, tx, transfer);
                    tx.Commit();
                }

                Interlocked.Add(ref _totalTransfersWritten, open.Count);
            }
        }

        public static CanMessageRepository OpenExisting(string dbPath)
        {
            if (!File.Exists(dbPath))
                throw new FileNotFoundException("Session database not found.", dbPath);

            var repo = new CanMessageRepository();
            repo.DatabasePath = dbPath;
            repo._connection = new SQLiteConnection($"Data Source={dbPath};Version=3;");
            repo._connection.Open();

            using (var cmd = repo._connection.CreateCommand())
            {
                // A live session may be writing this file.
                cmd.CommandText = "PRAGMA busy_timeout=5000;";
                cmd.ExecuteNonQuery();
            }

            repo._headerText = repo.GetSessionInfo("header") ?? $"Session: {Path.GetFileName(dbPath)}";
            return repo;
        }

        private string GetSessionInfo(string key)
        {
            lock (_dbLock)
            {
                if (_connection == null)
                    return null;

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT value FROM session_info WHERE key=@k";
                    cmd.Parameters.AddWithValue("@k", key);
                    return cmd.ExecuteScalar() as string;
                }
            }
        }

        public string GetSessionInfoValue(string key) => GetSessionInfo(key);

        public List<CanMessage> LoadMessages(double? timeFromMs = null, double? timeToMs = null)
        {
            var result = new List<CanMessage>();
            ReadMessages(timeFromMs, timeToMs, m => result.Add(m));
            return result;
        }

        /// <summary>
        /// Streams stored messages to <paramref name="onMessage"/> without materialising the
        /// whole table — a long session does not fit in a 32-bit process.
        /// </summary>
        public void ReadMessages(double? timeFromMs, double? timeToMs, Action<CanMessage> onMessage)
        {
            if (onMessage == null)
                return;

            foreach (var record in EnumerateRecords(timeFromMs, timeToMs))
                onMessage(CanMessageHelper.ToDisplayMessage(record));
        }

        /// <summary>Stored messages in capture order, read lazily as the caller enumerates.</summary>
        public IEnumerable<CanMessageRecord> EnumerateRecords(double? timeFromMs = null, double? timeToMs = null)
        {
            if (!IsOpen || string.IsNullOrEmpty(DatabasePath))
                yield break;

            // Bulk reads get their own connection so a long export never blocks the writer
            // thread — and never shares a connection with it, which is not thread-safe.
            using (var connection = CreateReadConnection())
            {
                using (var cmd = connection.CreateCommand())
                {
                    var sb = new StringBuilder("SELECT direction, timestamp_ms, wall_time, can_id, is_extended, is_rtr, dlc, data FROM can_messages WHERE 1=1");
                    if (timeFromMs.HasValue)
                    {
                        sb.Append(" AND timestamp_ms >= @from");
                        cmd.Parameters.AddWithValue("@from", timeFromMs.Value);
                    }
                    if (timeToMs.HasValue)
                    {
                        sb.Append(" AND timestamp_ms <= @to");
                        cmd.Parameters.AddWithValue("@to", timeToMs.Value);
                    }
                    sb.Append(" ORDER BY id");
                    cmd.CommandText = sb.ToString();

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var data = reader["data"] as byte[];
                            yield return new CanMessageRecord
                            {
                                Direction = reader.GetString(0),
                                TimestampMs = reader.GetDouble(1),
                                WallTime = DateTime.Parse(reader.GetString(2)),
                                CanId = (uint)reader.GetInt64(3),
                                IsExtended = reader.GetInt32(4) != 0,
                                IsRtr = reader.GetInt32(5) != 0,
                                Dlc = (byte)reader.GetInt32(6),
                                Data = data ?? new byte[8]
                            };
                        }
                    }
                }
            }
        }

        private SQLiteConnection CreateReadConnection()
        {
            var connection = new SQLiteConnection($"Data Source={DatabasePath};Version=3;");
            connection.Open();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout=5000;";
                cmd.ExecuteNonQuery();
            }
            return connection;
        }

        public long CountMessages()
        {
            lock (_dbLock)
            {
                if (_connection == null)
                    return 0;

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM can_messages";
                    object value = cmd.ExecuteScalar();
                    return value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value);
                }
            }
        }

        /// <summary>Writes the trace out a line at a time; nothing is buffered in memory.</summary>
        public void ExportToText(string outputPath, double? timeFromMs, double? timeToMs)
        {
            long count = 0;
            using (var writer = new StreamWriter(outputPath, false, Encoding.UTF8))
            {
                writer.WriteLine(_headerText);
                writer.WriteLine(Helpers.UcanMsgFormatProvider.GetMessageHeader());

                ReadMessages(timeFromMs, timeToMs, msg =>
                {
                    writer.WriteLine(msg.RawLine);
                    count++;
                });

                writer.WriteLine();
                writer.WriteLine($"Total number of CAN messages: {count}");
                writer.WriteLine($"Exported on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _running = false;

            var writer = _writerThread;
            _writerThread = null;
            if (writer != null && writer != Thread.CurrentThread)
            {
                try
                {
                    if (!writer.Join(TimeSpan.FromSeconds(10)))
                        AppLog.Warn("SQLite writer did not stop within 10 s.");
                }
                catch { /* ignore */ }
            }

            try { FlushUntilEmpty(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }

            if (PendingTotal == 0)
            {
                try { WriteOpenTransfers(); }
                catch (Exception ex) { AppLog.Warn(ex, "Could not store SDO transfers still open at session end."); }
            }

            int left = PendingTotal;
            if (left > 0)
                AppLog.Warn("Disposing repository with {0} unsaved message(s).", left);

            long dropped = TotalDropped;
            if (dropped > 0)
                AppLog.Warn("{0} message(s) were dropped because the write queue was full.", dropped);

            lock (_dbLock)
            {
                try
                {
                    _connection?.Close();
                    _connection?.Dispose();
                }
                catch { /* ignore */ }
                finally
                {
                    _connection = null;
                }
            }
        }
    }
}
