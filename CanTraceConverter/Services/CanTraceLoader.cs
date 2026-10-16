using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CanTraceConverter.Models;
using CanTraceConverter.Services;

namespace CanTraceConverter.Services
{
    public enum AnalysisSourceKind
    {
        ConvertedText,
        TextFile,
        Database
    }

    public sealed class CanTraceLoadResult
    {
        public List<CanMessage> Messages { get; set; } = new List<CanMessage>();
        public string HeaderText { get; set; } = string.Empty;
        public AnalysisSourceKind SourceKind { get; set; }
        public string SourcePath { get; set; }
        public string SourceDisplayName { get; set; }
        public Dictionary<string, string> SessionInfo { get; set; } = new Dictionary<string, string>();

        public string SourceLabel
        {
            get
            {
                switch (SourceKind)
                {
                    case AnalysisSourceKind.Database: return "SQLite session database";
                    case AnalysisSourceKind.TextFile: return "Converted text file";
                    default: return "Converted trace text";
                }
            }
        }
    }

    public static class CanTraceLoader
    {
        public static CanTraceLoadResult Load(string source, AnalysisSourceKind kind)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("Analysis source is empty.", nameof(source));

            if (kind == AnalysisSourceKind.Database || LooksLikeDatabase(source))
                return LoadDatabase(source);

            if (kind == AnalysisSourceKind.TextFile || (File.Exists(source) && !LooksLikeDatabase(source)))
                return LoadTextFile(source);

            return CanTraceTextParser.ParseText(source, sourceHint: null);
        }

        private static bool LooksLikeDatabase(string source) =>
            source.EndsWith(".db", StringComparison.OrdinalIgnoreCase) && File.Exists(source);

        private static CanTraceLoadResult LoadDatabase(string databasePath)
        {
            using (var repo = CanMessageRepository.OpenExisting(databasePath))
            {
                var result = new CanTraceLoadResult
                {
                    SourceKind = AnalysisSourceKind.Database,
                    SourcePath = databasePath,
                    SourceDisplayName = Path.GetFileName(databasePath),
                    HeaderText = repo.HeaderText ?? string.Empty,
                    Messages = repo.LoadMessages()
                };

                result.SessionInfo["title"] = repo.GetSessionInfoValue("title");
                result.SessionInfo["device"] = repo.GetSessionInfoValue("device");
                result.SessionInfo["channel"] = repo.GetSessionInfoValue("channel");
                result.SessionInfo["baudrate_kbps"] = repo.GetSessionInfoValue("baudrate_kbps");
                result.SessionInfo["listen_only"] = repo.GetSessionInfoValue("listen_only");
                result.SessionInfo["started_at"] = repo.GetSessionInfoValue("started_at");
                result.SessionInfo["sdo_client_node_id"] = repo.GetSessionInfoValue("sdo_client_node_id");

                if (string.IsNullOrWhiteSpace(result.HeaderText))
                    result.HeaderText = BuildDatabaseHeader(result);

                AppLog.Info("Analysis loaded {0} messages from database '{1}'.", result.Messages.Count, databasePath);
                return result;
            }
        }

        private static string BuildDatabaseHeader(CanTraceLoadResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Database: {result.SourceDisplayName}");
            if (result.SessionInfo.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title))
                sb.AppendLine($"Session:  {title}");
            if (result.SessionInfo.TryGetValue("device", out var device))
                sb.AppendLine($"Device:   {device}  Channel: {GetMeta(result.SessionInfo, "channel")}");
            if (result.SessionInfo.TryGetValue("baudrate_kbps", out var baud))
                sb.AppendLine($"Baudrate: {baud} kBit/s");
            if (result.SessionInfo.TryGetValue("started_at", out var started))
                sb.AppendLine($"Started:  {started}");
            return sb.ToString().TrimEnd();
        }

        private static string GetMeta(Dictionary<string, string> dict, string key, string fallback = "?")
        {
            if (dict != null && dict.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
            return fallback;
        }

        private static CanTraceLoadResult LoadTextFile(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Trace text file not found.", filePath);

            var text = File.ReadAllText(filePath, Encoding.UTF8);
            var result = CanTraceTextParser.ParseText(text, filePath);
            result.SourceKind = AnalysisSourceKind.TextFile;
            result.SourcePath = filePath;
            result.SourceDisplayName = Path.GetFileName(filePath);
            AppLog.Info("Analysis loaded {0} messages from text file '{1}'.", result.Messages.Count, filePath);
            return result;
        }
    }

    public static class CanTraceTextParser
    {
        private static readonly Regex MessageLineRegex = new Regex(
            @"^(?<dir>RX|TX)\s*:\s+(?<time>[\d.,]+)\s+(?<id>[0-9A-Fa-f]+h?)\s+\((?<dlc>\d+)\)\s+(?<data>.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static CanTraceLoadResult ParseText(string text, string sourceHint)
        {
            var result = new CanTraceLoadResult
            {
                SourceKind = AnalysisSourceKind.ConvertedText,
                SourcePath = sourceHint,
                SourceDisplayName = string.IsNullOrWhiteSpace(sourceHint) ? "Pasted / in-memory trace" : Path.GetFileName(sourceHint)
            };

            var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var headerLines = new List<string>();
            bool inMessages = false;
            int parsed = 0;
            int skipped = 0;

            foreach (var rawLine in lines)
            {
                var line = rawLine?.TrimEnd() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("Total number", StringComparison.OrdinalIgnoreCase))
                    break;

                if (!inMessages)
                {
                    if (line.Contains("DIR TIME") || line.StartsWith("==="))
                    {
                        inMessages = true;
                        continue;
                    }
                    headerLines.Add(line);
                    continue;
                }

                if (line.Contains("DIR TIME") || line.StartsWith("==="))
                    continue;

                var message = ParseMessageLine(line);
                if (message != null)
                {
                    result.Messages.Add(message);
                    parsed++;
                }
                else
                    skipped++;
            }

            result.HeaderText = string.Join(Environment.NewLine, headerLines);
            if (skipped > 0)
                AppLog.Warn("Trace text parser skipped {0} non-message line(s).", skipped);

            AppLog.Info("Analysis parsed {0} messages from converted text.", parsed);
            return result;
        }

        public static CanMessage ParseMessageLine(string line)
        {
            var match = MessageLineRegex.Match(line.Trim());
            if (!match.Success)
                return null;

            var dir = match.Groups["dir"].Value.ToUpperInvariant();
            var timeText = match.Groups["time"].Value.Replace(',', '.');
            if (!double.TryParse(timeText, NumberStyles.Float, CultureInfo.InvariantCulture, out double timeValue))
                timeValue = 0;

            var idToken = match.Groups["id"].Value.Trim();
            ParseCanIdToken(idToken, out uint canIdValue, out bool isExtended);

            var dlc = match.Groups["dlc"].Value;
            var data = match.Groups["data"].Value.Trim();
            var dataBytes = ParseDataBytes(data);
            bool isRtr = data.IndexOf("Remote Request", StringComparison.OrdinalIgnoreCase) >= 0;

            return new CanMessage
            {
                Direction = dir,
                Time = CanMessageHelper.FormatTime(timeValue),
                TimeValue = timeValue,
                CanId = FormatCanIdDisplay(canIdValue, isExtended),
                CanIdValue = canIdValue,
                Dlc = dlc,
                Data = data,
                DataBytes = dataBytes,
                RawLine = line.Trim(),
                IsExtended = isExtended,
                IsRtr = isRtr
            };
        }

        internal static void ParseCanIdToken(string token, out uint canIdValue, out bool isExtended)
        {
            var hex = token.Trim().TrimEnd('h', 'H');
            uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out canIdValue);
            isExtended = hex.Length > 3 || canIdValue > 0x7FF;
        }

        internal static string FormatCanIdDisplay(uint canId, bool isExtended) =>
            isExtended ? $"{canId:X8}h" : $"{canId,3:X3}h".Trim();

        internal static byte[] ParseDataBytes(string data)
        {
            var bytes = new List<byte>();
            foreach (var part in data.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == "--")
                    bytes.Add(0);
                else if (byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                    bytes.Add(b);
            }
            while (bytes.Count < 8)
                bytes.Add(0);
            return bytes.ToArray();
        }
    }
}
