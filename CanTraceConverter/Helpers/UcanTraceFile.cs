using System;
using System.IO;
using static UcanDotNET.USBcanServer;

namespace CanTraceConverter.Helpers
{
    /// <summary>
    /// A binary Systec trace (.ucantrc), read one message at a time so a large trace never
    /// has to fit in memory.
    /// </summary>
    public sealed class UcanTraceFile : IDisposable
    {
        public const uint Signature = 0x5543414E;

        private readonly UcanTraceBinaryReader _reader;

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
        public double TickMs =>
            CheckIs_G4(HardwareInfo) && (ActiveChannel.m_bMode & (byte)tUcanMode.kUcanModeHighResTimer) != 0 ? 0.1 : 1.0;

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

        public void Dispose() => _reader.Dispose();
    }
}
