using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;

namespace UcanView.Services
{
    /// <summary>
    /// Reads reassembled SDO packets (sdo_transfers) and their frames (sdo_messages) back
    /// from a session database. Safe to use while a live session is still writing it.
    /// </summary>
    public static class SdoPacketStore
    {
        /// <summary>False for databases recorded before SDO packets were stored.</summary>
        public static bool HasPacketTables(string databasePath)
        {
            using (var connection = OpenRead(databasePath))
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('sdo_transfers','sdo_messages')";
                return Convert.ToInt32(cmd.ExecuteScalar()) == 2;
            }
        }

        /// <summary>Packets with an id above <paramref name="afterId"/>, in id order.</summary>
        public static List<SdoTransfer> ReadTransfers(string databasePath, long afterId)
        {
            var result = new List<SdoTransfer>();
            using (var connection = OpenRead(databasePath))
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, start_time_ms, end_time_ms, start_wall_time, end_wall_time, transfer_type,
 transfer_mode, addressing, client_node_id, server_node_id, sdo_number, od_index, od_subindex, declared_size,
 payload, status, abort_code, issues, frame_count
FROM sdo_transfers WHERE id > @after ORDER BY id";
                cmd.Parameters.AddWithValue("@after", afterId);

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var t = new SdoTransfer
                        {
                            DbId = r.GetInt64(0),
                            StartTimeMs = r.GetDouble(1),
                            EndTimeMs = r.GetDouble(2),
                            StartWallTime = ReadTime(r, 3),
                            EndWallTime = ReadTime(r, 4),
                            TransferType = r.GetString(5),
                            Mode = r.GetString(6),
                            Scheme = ReadScheme(r.GetString(7)),
                            ClientNodeId = (byte)r.GetInt32(8),
                            ServerNodeId = (byte)r.GetInt32(9),
                            SdoNumber = (byte)r.GetInt32(10),
                            Index = r.IsDBNull(11) ? (ushort?)null : (ushort)r.GetInt32(11),
                            SubIndex = r.IsDBNull(12) ? (byte?)null : (byte)r.GetInt32(12),
                            DeclaredSize = r.IsDBNull(13) ? (long?)null : r.GetInt64(13),
                            Status = r.GetString(15),
                            AbortCode = r.IsDBNull(16) ? (uint?)null : (uint)r.GetInt64(16),
                            StoredFrameCount = r.GetInt32(18)
                        };

                        if (r[14] is byte[] payload)
                            t.PayloadBuffer.AddRange(payload);
                        if (!r.IsDBNull(17))
                            t.AddIssue(r.GetString(17));

                        result.Add(t);
                    }
                }
            }
            return result;
        }

        /// <summary>The individual frames of one stored packet, in capture order.</summary>
        public static List<SdoFrame> ReadFrames(string databasePath, long transferId)
        {
            var result = new List<SdoFrame>();
            using (var connection = OpenRead(databasePath))
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT timestamp_ms, wall_time, bus_direction, sdo_direction, addressing, cob_id,
 sender_node_id, receiver_node_id, server_node_id, client_node_id, sdo_number, service, command, command_byte,
 od_index, od_subindex, abort_code, dlc, data
FROM sdo_messages WHERE transfer_id = @t ORDER BY id";
                cmd.Parameters.AddWithValue("@t", transferId);

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var scheme = ReadScheme(r.GetString(4));
                        var data = new byte[8];
                        if (r[18] is byte[] stored)
                            Array.Copy(stored, data, Math.Min(8, stored.Length));

                        result.Add(new SdoFrame
                        {
                            TimestampMs = r.GetDouble(0),
                            WallTime = ReadTime(r, 1),
                            BusDirection = r.GetString(2),
                            Direction = r.GetString(3) == "client->server" ? SdoDirection.ClientToServer : SdoDirection.ServerToClient,
                            Scheme = scheme,
                            IsExtendedId = scheme == SdoAddressingScheme.Extended,
                            CobId = (uint)r.GetInt64(5),
                            SenderNodeId = (byte)r.GetInt32(6),
                            ReceiverNodeId = (byte)r.GetInt32(7),
                            ServerNodeId = (byte)r.GetInt32(8),
                            ClientNodeId = (byte)r.GetInt32(9),
                            SdoNumber = (byte)r.GetInt32(10),
                            Service = r.GetString(11),
                            Command = r.GetString(12),
                            CommandByte = (byte)r.GetInt32(13),
                            Index = r.IsDBNull(14) ? (ushort?)null : (ushort)r.GetInt32(14),
                            SubIndex = r.IsDBNull(15) ? (byte?)null : (byte)r.GetInt32(15),
                            AbortCode = r.IsDBNull(16) ? (uint?)null : (uint)r.GetInt64(16),
                            Dlc = (byte)r.GetInt32(17),
                            Data = data
                        });
                    }
                }
            }
            return result;
        }

        /// <summary>The SDO client node recorded for standard (11-bit) SDO IDs, or 0.</summary>
        public static byte ReadStandardClientNodeId(string databasePath)
        {
            try
            {
                using (var connection = OpenRead(databasePath))
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT value FROM session_info WHERE key='sdo_client_node_id'";
                    return byte.TryParse(cmd.ExecuteScalar() as string, out byte node) && node <= 127 ? node : (byte)0;
                }
            }
            catch
            {
                return 0;
            }
        }

        private static SQLiteConnection OpenRead(string databasePath)
        {
            if (!File.Exists(databasePath))
                throw new FileNotFoundException("Session database not found.", databasePath);

            var connection = new SQLiteConnection($"Data Source={databasePath};Version=3;");
            connection.Open();
            using (var cmd = connection.CreateCommand())
            {
                // A live session may be committing to this file.
                cmd.CommandText = "PRAGMA busy_timeout=5000;";
                cmd.ExecuteNonQuery();
            }
            return connection;
        }

        private static DateTime? ReadTime(SQLiteDataReader r, int ordinal)
        {
            if (r.IsDBNull(ordinal))
                return null;

            return DateTime.TryParse(r.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
                ? value
                : (DateTime?)null;
        }

        private static SdoAddressingScheme ReadScheme(string text) =>
            text == "extended" ? SdoAddressingScheme.Extended : SdoAddressingScheme.Standard;
    }
}
