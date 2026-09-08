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
        private long _totalDropped;
        private string _headerText;
        private int _disposed;

        public string DatabasePath { get; private set; }
        public string HeaderText => _headerText;
        public long TotalWritten => Interlocked.Read(ref _totalWritten);
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
CREATE INDEX IF NOT EXISTS idx_can_messages_can_id ON can_messages(can_id);";
                cmd.ExecuteNonQuery();
            }

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
                    batch.Add(item);

                if (batch.Count == 0)
                    return;

                try
                {
                    using (var tx = _connection.BeginTransaction())
                    using (var cmd = _connection.CreateCommand())
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
                        }
                        tx.Commit();
                    }

                    Interlocked.Add(ref _totalWritten, batch.Count);
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
            if (onMessage == null || !IsOpen || string.IsNullOrEmpty(DatabasePath))
                return;

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
                            var record = new CanMessageRecord
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
                            onMessage(CanMessageHelper.ToDisplayMessage(record));
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
