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
        private readonly ConcurrentQueue<CanMessageRecord> _pending = new ConcurrentQueue<CanMessageRecord>();
        private readonly object _flushLock = new object();
        private SQLiteConnection _connection;
        private Thread _writerThread;
        private volatile bool _running;
        private long _totalWritten;
        private string _headerText;

        public string DatabasePath { get; private set; }
        public string HeaderText => _headerText;
        public long TotalWritten => Interlocked.Read(ref _totalWritten);
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
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "INSERT OR REPLACE INTO session_info(key, value) VALUES(@k, @v)";
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@v", value);
                cmd.ExecuteNonQuery();
            }
        }

        public void Enqueue(CanMessageRecord record)
        {
            if (record == null || !_running)
                return;
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
            while (!_pending.IsEmpty)
            {
                FlushBatch(int.MaxValue);
                if (_pending.IsEmpty)
                    return true;

                if (DateTime.UtcNow >= deadline)
                {
                    AppLog.Warn("SQLite flush timed out with {0} message(s) still queued.", _pending.Count);
                    return false;
                }

                Thread.Sleep(10);
            }

            return true;
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
                }

                if (_pending.IsEmpty)
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
            if (!IsOpen || _pending.IsEmpty)
                return;

            lock (_flushLock)
            {
                if (!IsOpen)
                    return;

                var batch = new List<CanMessageRecord>();
                while (batch.Count < maxBatch && _pending.TryDequeue(out var item))
                    batch.Add(item);

                if (batch.Count == 0)
                    return;

                try
                {
                    using (var tx = _connection.BeginTransaction())
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = @"INSERT INTO can_messages
(direction, timestamp_ms, raw_hw_time, wall_time, can_id, is_extended, is_rtr, dlc, data)
VALUES(@dir, @ts, @raw, @wall, @id, @ext, @rtr, @dlc, @data)";
                        var pDir = cmd.Parameters.Add("@dir", System.Data.DbType.String);
                        var pTs = cmd.Parameters.Add("@ts", System.Data.DbType.Double);
                        var pRaw = cmd.Parameters.Add("@raw", System.Data.DbType.Int32);
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
                    // Re-queue so messages are not lost on transient write failures.
                    for (int i = batch.Count - 1; i >= 0; i--)
                        _pending.Enqueue(batch[i]);
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
            repo._headerText = repo.GetSessionInfo("header") ?? $"Session: {Path.GetFileName(dbPath)}";
            return repo;
        }

        private string GetSessionInfo(string key)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM session_info WHERE key=@k";
                cmd.Parameters.AddWithValue("@k", key);
                return cmd.ExecuteScalar() as string;
            }
        }

        public string GetSessionInfoValue(string key) => GetSessionInfo(key);

        public List<CanMessage> LoadMessages(double? timeFromMs = null, double? timeToMs = null)
        {
            if (!IsOpen)
                return new List<CanMessage>();

            var result = new List<CanMessage>();
            using (var cmd = _connection.CreateCommand())
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
                            CanId = (uint)(long)reader.GetInt64(3),
                            IsExtended = reader.GetInt32(4) != 0,
                            IsRtr = reader.GetInt32(5) != 0,
                            Dlc = (byte)reader.GetInt32(6),
                            Data = data ?? new byte[8]
                        };
                        result.Add(CanMessageHelper.ToDisplayMessage(record));
                    }
                }
            }
            return result;
        }

        public void ExportToText(string outputPath, double? timeFromMs, double? timeToMs)
        {
            var messages = LoadMessages(timeFromMs, timeToMs);
            var sb = new StringBuilder();
            sb.AppendLine(_headerText);
            sb.AppendLine(Helpers.UcanMsgFormatProvider.GetMessageHeader());
            foreach (var msg in messages)
                sb.AppendLine(msg.RawLine);
            sb.AppendLine();
            sb.AppendLine($"Total number of CAN messages: {messages.Count}");
            sb.AppendLine($"Exported on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }

        public void Dispose()
        {
            _running = false;
            try { _writerThread?.Join(15000); } catch { /* ignore */ }
            try { FlushUntilEmpty(TimeSpan.FromSeconds(5)); } catch { /* ignore */ }

            if (!_pending.IsEmpty)
                AppLog.Warn("Disposing repository with {0} unsaved message(s).", _pending.Count);

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
