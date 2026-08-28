using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CanTraceConverter.Models;

namespace CanTraceConverter.Services
{
    public sealed class CanTraceFilterCriteria
    {
        public string Direction { get; set; } = "All";
        public string CanIdContains { get; set; }
        public string QuickSearch { get; set; }
        public string Dlc { get; set; } = "All";
        public string TimeFrom { get; set; }
        public string TimeTo { get; set; }
        public string DataPattern { get; set; }
        public string SourceIdContains { get; set; }
        public int FunctionCodeByteIndex { get; set; }
        public string FunctionCodeHex { get; set; }
        public bool? ExtendedOnly { get; set; }
        public bool HideRtr { get; set; }
    }

    public sealed class CanTraceStatistics
    {
        public int TotalMessages { get; set; }
        public int FilteredMessages { get; set; }
        public int UniqueCanIds { get; set; }
        public int RxCount { get; set; }
        public int TxCount { get; set; }
        public double MinTimeMs { get; set; }
        public double MaxTimeMs { get; set; }
        public double DurationMs { get; set; }
        public double MessagesPerSecond { get; set; }
        public string MostFrequentCanId { get; set; }
        public int MostFrequentCount { get; set; }
        public int RtrCount { get; set; }
        public int ExtendedIdCount { get; set; }

        public string ToDisplayText()
        {
            var stats = new StringBuilder();
            stats.AppendLine($"Total Messages: {TotalMessages:N0}");
            stats.AppendLine($"Filtered:       {FilteredMessages:N0}");
            stats.AppendLine($"Unique CAN IDs: {UniqueCanIds:N0}");
            stats.AppendLine($"RX / TX:        {RxCount:N0} / {TxCount:N0}");
            stats.AppendLine($"Extended IDs:   {ExtendedIdCount:N0}");
            stats.AppendLine($"RTR messages:   {RtrCount:N0}");
            if (FilteredMessages > 0)
            {
                stats.AppendLine($"Time span:      {MinTimeMs:N1} – {MaxTimeMs:N1} ms");
                stats.AppendLine($"Duration:       {DurationMs:N1} ms");
                if (DurationMs > 0)
                    stats.AppendLine($"Avg rate:       {MessagesPerSecond:F2} msg/s");
            }
            if (!string.IsNullOrEmpty(MostFrequentCanId))
            {
                stats.AppendLine($"Top CAN ID:     {MostFrequentCanId}");
                stats.AppendLine($"                ({MostFrequentCount:N0} msgs)");
            }
            return stats.ToString().TrimEnd();
        }
    }

    public static class CanTraceAnalyzer
    {
        public static List<CanMessage> ApplyFilters(IEnumerable<CanMessage> source, CanTraceFilterCriteria criteria)
        {
            if (source == null)
                return new List<CanMessage>();

            IEnumerable<CanMessage> filtered = source;

            if (!string.IsNullOrWhiteSpace(criteria.QuickSearch))
            {
                var q = criteria.QuickSearch.Trim().ToUpperInvariant();
                filtered = filtered.Where(m =>
                    (m.CanId ?? string.Empty).ToUpperInvariant().Contains(q) ||
                    (m.Data ?? string.Empty).ToUpperInvariant().Contains(q) ||
                    (m.RawLine ?? string.Empty).ToUpperInvariant().Contains(q) ||
                    (m.Direction ?? string.Empty).ToUpperInvariant().Contains(q));
            }

            if (!string.IsNullOrWhiteSpace(criteria.Direction) && criteria.Direction != "All")
                filtered = filtered.Where(m => m.Direction == criteria.Direction);

            if (!string.IsNullOrWhiteSpace(criteria.CanIdContains))
            {
                var idFilter = criteria.CanIdContains.Trim().ToUpperInvariant().Replace("H", string.Empty);
                filtered = filtered.Where(m =>
                    (m.CanId ?? string.Empty).ToUpperInvariant().Replace("H", string.Empty).Contains(idFilter) ||
                    m.CanIdValue.ToString("X").Contains(idFilter));
            }

            if (!string.IsNullOrWhiteSpace(criteria.Dlc) && criteria.Dlc != "All")
                filtered = filtered.Where(m => m.Dlc == criteria.Dlc);

            if (TryParseDouble(criteria.TimeFrom, out double timeFrom))
                filtered = filtered.Where(m => m.TimeValue >= timeFrom);

            if (TryParseDouble(criteria.TimeTo, out double timeTo))
                filtered = filtered.Where(m => m.TimeValue <= timeTo);

            if (!string.IsNullOrWhiteSpace(criteria.DataPattern))
            {
                var pattern = criteria.DataPattern.Trim().ToUpperInvariant();
                filtered = filtered.Where(m => (m.Data ?? string.Empty).ToUpperInvariant().Contains(pattern));
            }

            if (!string.IsNullOrWhiteSpace(criteria.SourceIdContains))
            {
                var sourceId = criteria.SourceIdContains.Trim().ToUpperInvariant();
                filtered = filtered.Where(m => ExtractSourceId(m.CanId).Contains(sourceId));
            }

            if (!string.IsNullOrWhiteSpace(criteria.FunctionCodeHex))
            {
                var funcCode = criteria.FunctionCodeHex.Trim().ToUpperInvariant();
                var byteIndex = criteria.FunctionCodeByteIndex;
                filtered = filtered.Where(m =>
                {
                    if (m.DataBytes == null || m.DataBytes.Length <= byteIndex)
                        return false;
                    return m.DataBytes[byteIndex].ToString("X2").Contains(funcCode);
                });
            }

            if (criteria.ExtendedOnly == true)
                filtered = filtered.Where(m => m.IsExtended);

            if (criteria.HideRtr)
                filtered = filtered.Where(m => !m.IsRtr);

            return filtered.ToList();
        }

        public static CanTraceStatistics ComputeStatistics(IList<CanMessage> all, IList<CanMessage> filtered)
        {
            var stats = new CanTraceStatistics
            {
                TotalMessages = all?.Count ?? 0,
                FilteredMessages = filtered?.Count ?? 0
            };

            if (filtered == null || filtered.Count == 0)
                return stats;

            var idGroups = filtered.GroupBy(m => m.CanId).ToList();
            stats.UniqueCanIds = idGroups.Count;
            stats.RxCount = filtered.Count(m => m.Direction == "RX");
            stats.TxCount = filtered.Count(m => m.Direction == "TX");
            stats.ExtendedIdCount = filtered.Count(m => m.IsExtended);
            stats.RtrCount = filtered.Count(m => m.IsRtr);
            stats.MinTimeMs = filtered.Min(m => m.TimeValue);
            stats.MaxTimeMs = filtered.Max(m => m.TimeValue);
            stats.DurationMs = stats.MaxTimeMs - stats.MinTimeMs;
            if (stats.DurationMs > 0)
                stats.MessagesPerSecond = filtered.Count * 1000.0 / stats.DurationMs;

            var top = idGroups.OrderByDescending(g => g.Count()).FirstOrDefault();
            if (top != null)
            {
                stats.MostFrequentCanId = top.Key;
                stats.MostFrequentCount = top.Count();
            }

            return stats;
        }

        public static List<CanIdListItem> BuildCanIdList(IEnumerable<CanMessage> messages)
        {
            return messages
                .GroupBy(m => m.CanId)
                .Select(g => new CanIdListItem { CanId = g.Key, Count = g.Count() })
                .OrderBy(x => x.CanId)
                .ToList();
        }

        public static Dictionary<string, List<CanMessage>> IndexByCanId(IEnumerable<CanMessage> messages)
        {
            var dict = new Dictionary<string, List<CanMessage>>(StringComparer.OrdinalIgnoreCase);
            foreach (var msg in messages ?? Enumerable.Empty<CanMessage>())
            {
                if (!dict.TryGetValue(msg.CanId, out var list))
                {
                    list = new List<CanMessage>();
                    dict[msg.CanId] = list;
                }
                list.Add(msg);
            }
            return dict;
        }

        public static List<CanMessageSummary> BuildUniqueSummaries(IEnumerable<CanMessage> messages)
        {
            return messages
                .GroupBy(m => m.CanId)
                .Select(g =>
                {
                    var list = g.OrderBy(m => m.TimeValue).ToList();
                    return new CanMessageSummary
                    {
                        CanId = g.Key,
                        Count = list.Count,
                        Direction = SummarizeDirection(list),
                        Dlc = ModeOrFirst(list.Select(m => m.Dlc)),
                        FirstTime = CanMessageHelper.FormatTime(list.First().TimeValue),
                        LastTime = CanMessageHelper.FormatTime(list.Last().TimeValue),
                        AvgIntervalMs = ComputeAverageGap(list)
                    };
                })
                .OrderByDescending(s => s.Count)
                .ThenBy(s => s.CanId)
                .ToList();
        }

        public static List<CanMessage> BuildLatestPerId(IEnumerable<CanMessage> messages)
        {
            return messages
                .GroupBy(m => m.CanId)
                .Select(g => g.OrderByDescending(m => m.TimeValue).First())
                .OrderBy(m => m.CanId)
                .ToList();
        }

        public static List<DataChangeRow> BuildDataChanges(IEnumerable<CanMessage> messages)
        {
            var rows = new List<DataChangeRow>();
            foreach (var group in messages.GroupBy(m => m.CanId))
            {
                string lastData = null;
                foreach (var msg in group.OrderBy(m => m.TimeValue))
                {
                    var data = msg.Data ?? string.Empty;
                    if (lastData != null && !string.Equals(lastData, data, StringComparison.OrdinalIgnoreCase))
                    {
                        rows.Add(new DataChangeRow
                        {
                            CanId = msg.CanId,
                            Time = msg.Time,
                            TimeValue = msg.TimeValue,
                            PreviousData = lastData,
                            NewData = data,
                            Direction = msg.Direction
                        });
                    }
                    lastData = data;
                }
            }
            return rows.OrderBy(r => r.TimeValue).ToList();
        }

        public static List<GapAnalysisRow> BuildGapAnalysis(IEnumerable<CanMessage> messages)
        {
            var rows = new List<GapAnalysisRow>();
            foreach (var group in messages.GroupBy(m => m.CanId).Where(g => g.Count() > 1))
            {
                var ordered = group.OrderBy(m => m.TimeValue).ToList();
                var gaps = new List<double>();
                for (int i = 1; i < ordered.Count; i++)
                    gaps.Add(ordered[i].TimeValue - ordered[i - 1].TimeValue);

                rows.Add(new GapAnalysisRow
                {
                    CanId = group.Key,
                    MessageCount = ordered.Count,
                    MinGapMs = gaps.Min(),
                    MaxGapMs = gaps.Max(),
                    AvgGapMs = gaps.Average(),
                    EstimatedHz = gaps.Average() > 0 ? 1000.0 / gaps.Average() : 0
                });
            }
            return rows.OrderByDescending(r => r.MessageCount).ToList();
        }

        public static List<TimelineSummary> BuildTimeline(IEnumerable<CanMessage> messages, double intervalMs)
        {
            var list = messages?.ToList() ?? new List<CanMessage>();
            var timeline = new List<TimelineSummary>();
            if (list.Count == 0 || intervalMs <= 0)
                return timeline;

            double minTime = list.Min(m => m.TimeValue);
            double maxTime = list.Max(m => m.TimeValue);

            for (double time = minTime; time <= maxTime; time += intervalMs)
            {
                var bucket = list.Where(m => m.TimeValue >= time && m.TimeValue < time + intervalMs).ToList();
                if (bucket.Count == 0)
                    continue;

                timeline.Add(new TimelineSummary
                {
                    TimeRange = $"{CanMessageHelper.FormatTime(time)} – {CanMessageHelper.FormatTime(time + intervalMs)}",
                    MessageCount = bucket.Count,
                    UniqueIds = bucket.Select(m => m.CanId).Distinct().Count(),
                    RxCount = bucket.Count(m => m.Direction == "RX"),
                    TxCount = bucket.Count(m => m.Direction == "TX")
                });
            }

            return timeline;
        }

        public static List<FrequencyAnalysis> BuildFrequencyAnalysis(IEnumerable<CanMessage> messages, int totalFiltered)
        {
            return messages
                .GroupBy(m => m.CanId)
                .Select(g =>
                {
                    var list = g.ToList();
                    var minTime = list.Min(m => m.TimeValue);
                    var maxTime = list.Max(m => m.TimeValue);
                    var durationSec = (maxTime - minTime) / 1000.0;
                    return new FrequencyAnalysis
                    {
                        CanId = g.Key,
                        TotalCount = list.Count,
                        Percentage = totalFiltered > 0 ? list.Count * 100.0 / totalFiltered : 0,
                        FrequencyHz = durationSec > 0 ? list.Count / durationSec : 0,
                        Direction = SummarizeDirection(list)
                    };
                })
                .OrderByDescending(x => x.TotalCount)
                .ToList();
        }

        private static string SummarizeDirection(List<CanMessage> list)
        {
            bool hasRx = list.Any(m => m.Direction == "RX");
            bool hasTx = list.Any(m => m.Direction == "TX");
            if (hasRx && hasTx) return "RX+TX";
            if (hasTx) return "TX";
            return "RX";
        }

        private static string ModeOrFirst(IEnumerable<string> values)
        {
            return values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
        }

        private static double ComputeAverageGap(List<CanMessage> ordered)
        {
            if (ordered.Count < 2)
                return 0;
            double sum = 0;
            for (int i = 1; i < ordered.Count; i++)
                sum += ordered[i].TimeValue - ordered[i - 1].TimeValue;
            return sum / (ordered.Count - 1);
        }

        private static string ExtractSourceId(string canId)
        {
            if (string.IsNullOrWhiteSpace(canId))
                return string.Empty;
            var hex = canId.Trim().TrimEnd('h', 'H');
            return hex.Length >= 4 ? hex.Substring(Math.Max(0, hex.Length - 4), Math.Min(2, hex.Length)) : hex;
        }

        private static bool TryParseDouble(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
