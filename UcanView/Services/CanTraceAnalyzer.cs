using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UcanView.Models;

namespace UcanView.Services
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

        /// <summary>These criteria with no CAN ID filter.</summary>
        public CanTraceFilterCriteria WithoutCanId()
        {
            var copy = (CanTraceFilterCriteria)MemberwiseClone();
            copy.CanIdContains = null;
            return copy;
        }
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
                // The trace line holds the direction, identifier and data too, so one formatted
                // string per frame is searched rather than four.
                var q = criteria.QuickSearch.Trim();
                filtered = filtered.Where(m => m.RawLine.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (!string.IsNullOrWhiteSpace(criteria.Direction) && criteria.Direction != "All")
                filtered = filtered.Where(m => m.Direction == criteria.Direction);

            if (!string.IsNullOrWhiteSpace(criteria.CanIdContains))
            {
                // A whole ID ending in "h", as the unique ID list gives it, is matched exactly:
                // "100h" must not also pick 09020100h. Bare digits match any ID containing them.
                if (TryParseExactCanId(criteria.CanIdContains, out uint exactId, out bool exactExtended))
                {
                    filtered = filtered.Where(m => m.CanIdValue == exactId && m.IsExtended == exactExtended);
                }
                else
                {
                    var idFilter = criteria.CanIdContains.Trim().ToUpperInvariant().Replace("H", string.Empty);
                    filtered = filtered.Where(m =>
                        (m.CanId ?? string.Empty).ToUpperInvariant().Replace("H", string.Empty).Contains(idFilter) ||
                        m.CanIdValue.ToString("X").Contains(idFilter));
                }
            }

            if (!string.IsNullOrWhiteSpace(criteria.Dlc) && criteria.Dlc != "All")
                filtered = filtered.Where(m => m.Dlc == criteria.Dlc);

            if (TryParseDouble(criteria.TimeFrom, out double timeFrom))
                filtered = filtered.Where(m => m.TimeValue >= timeFrom);

            if (TryParseDouble(criteria.TimeTo, out double timeTo))
                filtered = filtered.Where(m => m.TimeValue <= timeTo);

            if (!string.IsNullOrWhiteSpace(criteria.DataPattern))
            {
                var pattern = criteria.DataPattern.Trim();
                filtered = filtered.Where(m => m.Data.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            if (!string.IsNullOrWhiteSpace(criteria.SourceIdContains))
            {
                // An unreadable node matches nothing rather than everything.
                bool valid = TryParseHexByte(criteria.SourceIdContains, out byte node);
                filtered = filtered.Where(m => valid && SourceNodeId(m) == node);
            }

            if (!string.IsNullOrWhiteSpace(criteria.FunctionCodeHex))
            {
                var funcCode = criteria.FunctionCodeHex.Trim().ToUpperInvariant();
                var byteIndex = criteria.FunctionCodeByteIndex;
                filtered = filtered.Where(m =>
                {
                    if (byteIndex < 0 || byteIndex >= 8)
                        return false;
                    return m.DataByte(byteIndex).ToString("X2").Contains(funcCode);
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

            var idGroups = filtered.GroupBy(IdKey).ToList();
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
                stats.MostFrequentCanId = top.First().CanId;
                stats.MostFrequentCount = top.Count();
            }

            return stats;
        }

        public static List<CanIdListItem> BuildCanIdList(IEnumerable<CanMessage> messages)
        {
            return messages
                .GroupBy(IdKey)
                .Select(g => new CanIdListItem { CanId = g.First().CanId, Count = g.Count() })
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
                .GroupBy(IdKey)
                .Select(g =>
                {
                    var list = g.OrderBy(m => m.TimeValue).ToList();
                    return new CanMessageSummary
                    {
                        CanId = list[0].CanId,
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
                .GroupBy(IdKey)
                .Select(g => g.OrderByDescending(m => m.TimeValue).First())
                .OrderBy(m => m.CanId)
                .ToList();
        }

        public static List<DataChangeRow> BuildDataChanges(IEnumerable<CanMessage> messages)
        {
            var rows = new List<DataChangeRow>();
            foreach (var group in messages.GroupBy(IdKey))
            {
                CanMessage last = null;
                foreach (var msg in group.OrderBy(m => m.TimeValue))
                {
                    // Rows refer to the frames; their text is formatted only when shown.
                    if (last != null && !msg.HasSameData(last))
                        rows.Add(new DataChangeRow(last, msg));
                    last = msg;
                }
            }
            return rows.OrderBy(r => r.TimeValue).ToList();
        }

        public static List<GapAnalysisRow> BuildGapAnalysis(IEnumerable<CanMessage> messages)
        {
            var rows = new List<GapAnalysisRow>();

            // A TX echo and a received frame with the same identifier are separate streams;
            // interleaving them halved the measured period.
            foreach (var group in messages.GroupBy(m => new { Id = IdKey(m), m.Direction }))
            {
                // Stable sort: equal timestamps keep their capture order.
                var ordered = group.OrderBy(m => m.TimeValue).ToList();
                if (ordered.Count < 2)
                    continue;

                double min = double.MaxValue, max = double.MinValue, sum = 0, sumSq = 0;
                int n = ordered.Count - 1;
                for (int i = 1; i < ordered.Count; i++)
                {
                    double gap = ordered[i].TimeValue - ordered[i - 1].TimeValue;
                    if (gap < min) min = gap;
                    if (gap > max) max = gap;
                    sum += gap;
                    sumSq += gap * gap;
                }

                double avg = sum / n;
                double variance = Math.Max(0, sumSq / n - avg * avg);

                rows.Add(new GapAnalysisRow
                {
                    CanId = ordered[0].CanId,
                    Direction = group.Key.Direction,
                    MessageCount = ordered.Count,
                    MinGapMs = min,
                    MaxGapMs = max,
                    AvgGapMs = avg,
                    JitterMs = Math.Sqrt(variance),
                    EstimatedHz = avg > 0 ? 1000.0 / avg : 0
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

            // One pass, bucket = floor((t - min) / interval). The old loop rescanned every
            // message for every bucket and accumulated floating-point error in the bounds.
            var buckets = new SortedDictionary<long, List<CanMessage>>();
            foreach (var message in list)
            {
                long index = (long)Math.Floor((message.TimeValue - minTime) / intervalMs);
                if (!buckets.TryGetValue(index, out var bucket))
                {
                    bucket = new List<CanMessage>();
                    buckets[index] = bucket;
                }
                bucket.Add(message);
            }

            foreach (var pair in buckets)
            {
                double start = minTime + pair.Key * intervalMs;
                var bucket = pair.Value;
                timeline.Add(new TimelineSummary
                {
                    TimeRange = $"{CanMessageHelper.FormatTime(start)} – {CanMessageHelper.FormatTime(start + intervalMs)}",
                    MessageCount = bucket.Count,
                    UniqueIds = bucket.Select(IdKey).Distinct().Count(),
                    RxCount = bucket.Count(m => m.Direction == "RX"),
                    TxCount = bucket.Count(m => m.Direction == "TX")
                });
            }

            return timeline;
        }

        public static List<FrequencyAnalysis> BuildFrequencyAnalysis(IEnumerable<CanMessage> messages, int totalFiltered)
        {
            return messages
                .GroupBy(IdKey)
                .Select(g =>
                {
                    var list = g.ToList();
                    var minTime = list.Min(m => m.TimeValue);
                    var maxTime = list.Max(m => m.TimeValue);
                    var durationSec = (maxTime - minTime) / 1000.0;
                    return new FrequencyAnalysis
                    {
                        CanId = list[0].CanId,
                        TotalCount = list.Count,
                        Percentage = totalFiltered > 0 ? list.Count * 100.0 / totalFiltered : 0,
                        FrequencyHz = durationSec > 0 ? list.Count / durationSec : 0,
                        Direction = SummarizeDirection(list)
                    };
                })
                .OrderByDescending(x => x.TotalCount)
                .ToList();
        }

        /// <summary>
        /// Groups frames by identifier without formatting a display string per frame: a trace
        /// has millions of frames and only a few hundred identifiers.
        /// </summary>
        private static long IdKey(CanMessage m) => m.IsExtended ? (1L << 32) | m.CanIdValue : m.CanIdValue;

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

        /// <summary>
        /// The node that sent a frame. In the 29-bit layout (see <see cref="SdoAddressing"/>) that is
        /// bits 23..16 — function code 28..24, sender 23..16, SDO number 15..8, receiver 7..0 —
        /// e.g. 02 in the SDO response 0C020E0Eh. An 11-bit CANopen ID carries its node in bits 6..0.
        /// </summary>
        /// <remarks>This used to take bits 15..8, the SDO number: on this network that equals the
        /// client node, so it gave the sender of requests but the receiver of responses.</remarks>
        public static byte SourceNodeId(CanMessage m) =>
            m.IsExtended ? (byte)(m.CanIdValue >> 16) : (byte)(m.CanIdValue & 0x7F);

        /// <summary>A node as hex: "2", "02", "02h" or "0x02".</summary>
        private static bool TryParseHexByte(string text, out byte value)
        {
            var hex = text.Trim().TrimEnd('h', 'H');
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex.Substring(2);
            return byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>A whole CAN ID written as displayed, e.g. 0E0C0000h or 581h.</summary>
        private static bool TryParseExactCanId(string text, out uint id, out bool isExtended)
        {
            id = 0;
            isExtended = false;
            var trimmed = text.Trim();
            if (!trimmed.EndsWith("h", StringComparison.OrdinalIgnoreCase))
                return false;

            var hex = trimmed.Substring(0, trimmed.Length - 1);
            if (hex.Length == 0 || hex.Length > 8 ||
                !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id))
                return false;

            // As the trace shows them: 8 digits for a 29-bit ID, 3 for an 11-bit one.
            isExtended = hex.Length > 3 || id > 0x7FF;
            return true;
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
