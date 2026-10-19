using System;
using System.Collections.Generic;
using System.IO;
using CanTraceConverter.Helpers;
using UcanDotNET;

namespace CanTraceConverter.Services
{
    /// <summary>One captured CAN frame, as read back from a trace file or session database.</summary>
    public readonly struct CapturedFrame
    {
        public CapturedFrame(double timestampMs, uint canId, bool isExtended, bool isRtr, byte dlc, byte[] data,
            string direction, DateTime? wallTime)
        {
            TimestampMs = timestampMs;
            CanId = canId;
            IsExtended = isExtended;
            IsRtr = isRtr;
            Dlc = dlc;
            Data = data;
            Direction = direction;
            WallTime = wallTime;
        }

        public double TimestampMs { get; }
        public uint CanId { get; }
        public bool IsExtended { get; }
        public bool IsRtr { get; }
        public byte Dlc { get; }
        public byte[] Data { get; }

        /// <summary>RX, or TX for a frame this adapter sent.</summary>
        public string Direction { get; }
        public DateTime? WallTime { get; }
    }

    /// <summary>
    /// Reads captured frames lazily from a binary trace (.ucantrc), session database (.db) or
    /// converted trace text, and merges the captures of two channels into one stream.
    /// </summary>
    public static class CapturedFrameSource
    {
        /// <summary>The frames of a capture file, read as they are enumerated.</summary>
        public static IEnumerable<CapturedFrame> Open(string path)
        {
            if (path.EndsWith(".ucantrc", StringComparison.OrdinalIgnoreCase))
                return ReadBinaryTrace(path);
            if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                return ReadDatabase(path);
            return ReadText(path);
        }

        /// <summary>
        /// Merges two captures in timestamp order. They must share a clock: two channels of the
        /// same adapter, recorded by trace or by monitor session. On a CANopen network split
        /// across both channels, an SDO request can be in one capture and its response in the
        /// other, so only the merged stream shows whole transfers.
        /// </summary>
        /// <remarks>
        /// Timestamps are often whole milliseconds, so a request and its response can share
        /// one. Between the two captures, a frame that is an SDO response then goes after one
        /// that is not: the response cannot precede its request. Each capture keeps its own order.
        /// </remarks>
        public static IEnumerable<CapturedFrame> Merge(IEnumerable<CapturedFrame> first, IEnumerable<CapturedFrame> second)
        {
            using (var a = first.GetEnumerator())
            using (var b = second.GetEnumerator())
            {
                bool hasA = a.MoveNext();
                bool hasB = b.MoveNext();
                while (hasA || hasB)
                {
                    if (hasA && (!hasB || !GoesAfter(a.Current, b.Current)))
                    {
                        yield return a.Current;
                        hasA = a.MoveNext();
                    }
                    else
                    {
                        yield return b.Current;
                        hasB = b.MoveNext();
                    }
                }
            }
        }

        private static bool GoesAfter(in CapturedFrame x, in CapturedFrame y)
        {
            if (x.TimestampMs != y.TimestampMs)
                return x.TimestampMs > y.TimestampMs;
            return IsSdoResponse(x) && !IsSdoResponse(y);
        }

        private static bool IsSdoResponse(in CapturedFrame f) =>
            SdoAddressing.TryResolve(f.CanId, f.IsExtended, f.IsRtr, 0, out var address) &&
            address.Direction == SdoDirection.ServerToClient;

        /// <summary>The adapter serial number a binary trace was recorded with.</summary>
        public static int ReadTraceSerialNumber(string path)
        {
            using (var trace = UcanTraceFile.Open(path))
                return trace.HardwareInfo.m_dwSerialNr;
        }

        private static IEnumerable<CapturedFrame> ReadBinaryTrace(string path)
        {
            using (var trace = UcanTraceFile.Open(path))
            {
                double tickMs = trace.TickMs;
                while (trace.TryReadMessage(out var msg))
                {
                    byte ff = msg.m_bFF;
                    yield return new CapturedFrame(
                        unchecked((uint)msg.m_dwTime) * tickMs,
                        unchecked((uint)msg.m_dwID),
                        (ff & (byte)USBcanServer.eUcanMsgFrameFormat.USBCAN_MSG_FF_EXT) != 0,
                        (ff & (byte)USBcanServer.eUcanMsgFrameFormat.USBCAN_MSG_FF_RTR) != 0,
                        msg.m_bDLC,
                        msg.m_bData,
                        (ff & (byte)USBcanServer.eUcanMsgFrameFormat.USBCAN_MSG_FF_ECHO) != 0 ? "TX" : "RX",
                        null);
                }
            }
        }

        private static IEnumerable<CapturedFrame> ReadDatabase(string path)
        {
            using (var repo = CanMessageRepository.OpenExisting(path))
            {
                foreach (var r in repo.EnumerateRecords())
                    yield return new CapturedFrame(r.TimestampMs, r.CanId, r.IsExtended, r.IsRtr, r.Dlc, r.Data, r.Direction, r.WallTime);
            }
        }

        private static IEnumerable<CapturedFrame> ReadText(string path)
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("Total number", StringComparison.OrdinalIgnoreCase))
                    yield break;

                var msg = CanTraceTextParser.ParseMessageLine(line);
                if (msg == null)
                    continue;

                byte.TryParse(msg.Dlc, out byte dlc);
                yield return new CapturedFrame(msg.TimeValue, msg.CanIdValue, msg.IsExtended, msg.IsRtr, dlc,
                    msg.DataBytes, msg.Direction, msg.WallTime);
            }
        }
    }
}
