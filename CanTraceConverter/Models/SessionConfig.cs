using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CanTraceConverter.Models
{
    public class SessionConfig
    {
        public string Title { get; set; }
        public byte DeviceNumber { get; set; }
        public byte Channel { get; set; }
        public int BaudRateKbps { get; set; }
        public bool ListenOnly { get; set; }
        public string LogDirectory { get; set; }

        public string BuildDatabasePath()
        {
            string safeTitle = Regex.Replace(Title ?? "Session", @"[^\w\-]", "_");
            if (string.IsNullOrWhiteSpace(safeTitle))
                safeTitle = "Session";

            string fileName = $"{safeTitle}_{DateTime.Now:yyyyMMdd_HHmmss}.db";
            return Path.Combine(LogDirectory, fileName);
        }

        public static string DefaultLogDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "CanTraceConverter",
                "Sessions");
    }

    /// <summary>
    /// Decoded CAN controller status. Only genuine bus/controller faults count as errors —
    /// the driver also reports normal transient states such as an empty receive queue.
    /// </summary>
    public sealed class CanBusStatus
    {
        private const int OVERRUN = 0x0002;
        private const int BUSLIGHT = 0x0004;
        private const int BUSHEAVY = 0x0008;
        private const int BUSOFF = 0x0010;
        private const int QOVERRUN = 0x0040;
        private const int REGTEST = 0x0100;
        private const int MEMTEST = 0x0200;
        private const int TXMSGLOST = 0x0400;

        public static readonly CanBusStatus Ok = new CanBusStatus(0);

        private CanBusStatus(int raw)
        {
            RawStatus = raw;
            IsBusOff = (raw & BUSOFF) != 0;
            IsBusHeavy = (raw & BUSHEAVY) != 0;
            IsBusLight = (raw & BUSLIGHT) != 0;
            IsOverrun = (raw & (OVERRUN | QOVERRUN)) != 0;
            IsControllerFault = (raw & (REGTEST | MEMTEST)) != 0;
            Message = Describe(raw);
        }

        public static CanBusStatus FromRawStatus(int raw) => raw == 0 ? Ok : new CanBusStatus(raw);

        public int RawStatus { get; }
        public string Message { get; }
        public bool IsBusOff { get; }
        public bool IsBusHeavy { get; }
        public bool IsBusLight { get; }
        public bool IsOverrun { get; }
        public bool IsControllerFault { get; }

        public bool IsError => !string.IsNullOrEmpty(Message);

        /// <summary>True while the controller cannot put frames on the wire.</summary>
        public bool BlocksTransmit => IsBusOff;

        private static string Describe(int raw)
        {
            var parts = new List<string>();
            if ((raw & BUSOFF) != 0) parts.Add("Bus off — controller has left the bus");
            if ((raw & BUSHEAVY) != 0) parts.Add("Bus heavy (error passive)");
            if ((raw & BUSLIGHT) != 0) parts.Add("Bus light (error warning limit)");
            if ((raw & OVERRUN) != 0) parts.Add("Receive buffer overrun");
            if ((raw & QOVERRUN) != 0) parts.Add("Receive queue overrun");
            if ((raw & TXMSGLOST) != 0) parts.Add("Transmit message lost");
            if ((raw & REGTEST) != 0) parts.Add("Controller register test failed");
            if ((raw & MEMTEST) != 0) parts.Add("Controller memory test failed");
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
    }

    public class CanDeviceItem
    {
        /// <summary>Product-ID bit that marks a two-channel module.</summary>
        private const int ProductCodeTwoChannel = 0x0001;
        private const int ProductCodeMaskPid = 0xFFFF;

        public byte DeviceNumber { get; set; }
        public int SerialNumber { get; set; }
        public int ProductCode { get; set; }
        public bool InUse { get; set; }
        public string Label { get; set; }

        /// <summary>False for GW-001/GW-002/USB-CANmodul1, which only expose channel 0.</summary>
        public bool SupportsTwoChannels =>
            ProductCode == 0 || (ProductCode & ProductCodeMaskPid & ProductCodeTwoChannel) != 0;

        public string DisplayName =>
            !string.IsNullOrWhiteSpace(Label)
                ? Label
                : InUse
                    ? $"Device {DeviceNumber} (SN:{SerialNumber}) — in use"
                    : $"Device {DeviceNumber} (SN:{SerialNumber})";
    }
}
