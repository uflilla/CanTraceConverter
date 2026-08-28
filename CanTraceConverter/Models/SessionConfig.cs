using System;
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

    public class CanDeviceItem
    {
        public byte DeviceNumber { get; set; }
        public int SerialNumber { get; set; }
        public int ProductCode { get; set; }
        public bool InUse { get; set; }
        public string Label { get; set; }

        public string DisplayName =>
            !string.IsNullOrWhiteSpace(Label)
                ? Label
                : InUse
                    ? $"Device {DeviceNumber} (SN:{SerialNumber}) — in use"
                    : $"Device {DeviceNumber} (SN:{SerialNumber})";
    }
}
