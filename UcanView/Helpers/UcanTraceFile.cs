using System;
using System.IO;
using System.Text;
using static UcanDotNET.USBcanServer;

namespace UcanView.Helpers
{
    /// <summary>
    /// A binary Systec trace (.ucantrc), read one message at a time so a large trace never
    /// has to fit in memory.
    /// </summary>
    public sealed class UcanTraceFile : IDisposable
    {
        public const uint Signature = 0x5543414E;

        /// <summary>Bytes per message record: ID, frame format, DLC, 8 data bytes, timestamp.</summary>
        public const int RecordSize = 18;

        private readonly UcanTraceBinaryReader _reader;
        private long _firstMessageOffset;

        private UcanTraceFile(UcanTraceBinaryReader reader)
        {
            _reader = reader;
        }

        public uint Version { get; private set; }
        public SYSTEMTIME SystemTime { get; private set; }
        public OSVERSIONINFO OsVersion { get; private set; }
        public tUcanHardwareInfoEx HardwareInfo { get; private set; }
        public tUcanChannelInfo Channel0 { get; private set; }
        public tUcanChannelInfo Channel1 { get; private set; }

        public tUcanChannelInfo ActiveChannel => Channel0.m_fCanIsInit ? Channel0 : Channel1;

        /// <summary>Milliseconds per timestamp tick: 0.1 for a G4 high-resolution timer, else 1.</summary>
        public double TickMs => IsHighResTimer ? 0.1 : 1.0;

        /// <summary>A G4 module recording with its 0.1 ms timer.</summary>
        public bool IsHighResTimer =>
            CheckIs_G4(HardwareInfo) && (ActiveChannel.m_bMode & (byte)tUcanMode.kUcanModeHighResTimer) != 0;

        /// <summary>Whole message records in the file; a truncated last record is not counted.</summary>
        public long MessageCount => Math.Max(0, (_reader.BaseStream.Length - _firstMessageOffset) / RecordSize);

        /// <summary>Opens the trace and reads its header.</summary>
        /// <exception cref="InvalidDataException">The file is not a binary CAN trace.</exception>
        public static UcanTraceFile Open(string path)
        {
            var reader = new UcanTraceBinaryReader(File.OpenRead(path));
            try
            {
                if (reader.ReadUInt32() != Signature)
                    throw new InvalidDataException("Invalid binary trace file (wrong signature).");

                var file = new UcanTraceFile(reader);
                reader.ReadUInt32(); // offset of the first message; the header is read through instead
                file.Version = reader.ReadUInt32();
                file.SystemTime = reader.ReadSystemTime();
                file.OsVersion = reader.ReadOsVersion();
                file.HardwareInfo = reader.ReadUcanHwInfo();
                file.Channel0 = reader.ReadUcanChannelInfo();
                file.Channel1 = reader.ReadUcanChannelInfo();
                file._firstMessageOffset = reader.BaseStream.Position;
                return file;
            }
            catch (EndOfStreamException)
            {
                reader.Dispose();
                throw new InvalidDataException("Invalid binary trace file (header truncated).");
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        /// <summary>The next message, or false at the end of the trace (a truncated last record included).</summary>
        public bool TryReadMessage(out tCanMsgStruct message)
        {
            try
            {
                message = _reader.ReadUcanMessage();
                return true;
            }
            catch (EndOfStreamException)
            {
                message = default;
                return false;
            }
        }

        /// <summary>
        /// The message at a zero-based position. Records have a fixed size, so any line of a
        /// trace can be shown without reading the ones before it. Moves the sequential read
        /// position used by <see cref="TryReadMessage"/>.
        /// </summary>
        public tCanMsgStruct ReadMessageAt(long index)
        {
            if (index < 0 || index >= MessageCount)
                throw new ArgumentOutOfRangeException(nameof(index));

            _reader.BaseStream.Position = _firstMessageOffset + index * RecordSize;
            return _reader.ReadUcanMessage();
        }

        /// <summary>The descriptive header written above the messages of a converted trace.</summary>
        public string BuildHeaderText(string path)
        {
            var sb = new StringBuilder(512);
            sb.AppendLine($"InputFile: {path}");
            sb.AppendLine($"Version:   {string.Format(new VersionFormatProvider(), "PeakUcan.dll {0}", Version)}");
            sb.AppendLine($"Date/Time: {string.Format(new SystemTimeFormatProvider(), "{0}", SystemTime)}");
            sb.AppendLine($"OS:        {string.Format(new OsVerisonFormatProvider(), "{0}", OsVersion)}");
            sb.AppendLine($"Hardware:  {string.Format(new UcanHwInfoFormatProvider(), "{0}", HardwareInfo)}");
            sb.AppendLine($"Channel0:  {string.Format(new UcanChannelInfoFormatProvider(), "{0}", Channel0)}");
            sb.AppendLine($"Channel1:  {string.Format(new UcanChannelInfoFormatProvider(), "{0}", Channel1)}");
            return sb.ToString();
        }

        public void Dispose() => _reader.Dispose();
    }
}
