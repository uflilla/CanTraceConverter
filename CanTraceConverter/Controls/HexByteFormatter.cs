using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CanTraceConverter.Controls
{
    public static class HexByteFormatter
    {
        public static bool IsHexChar(char c) =>
            (c >= '0' && c <= '9') ||
            (c >= 'A' && c <= 'F') ||
            (c >= 'a' && c <= 'f');

        public static string ExtractHexDigits(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (IsHexChar(c))
                    sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        public static string FormatDigits(string digits, int maxBytes)
        {
            if (string.IsNullOrEmpty(digits))
                return string.Empty;

            int maxDigits = Math.Max(0, maxBytes) * 2;
            if (digits.Length > maxDigits)
                digits = digits.Substring(0, maxDigits);

            var parts = new List<string>();
            int i = 0;
            while (i + 1 < digits.Length)
            {
                parts.Add(digits.Substring(i, 2));
                i += 2;
            }

            if (i < digits.Length)
                parts.Add(digits.Substring(i, 1));

            return string.Join(" ", parts);
        }

        public static string PadIncompleteNibbles(string formatted)
        {
            if (string.IsNullOrWhiteSpace(formatted))
                return string.Empty;

            var parts = formatted.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int p = 0; p < parts.Length; p++)
            {
                if (parts[p].Length == 1)
                    parts[p] = "0" + parts[p];
            }
            return string.Join(" ", parts);
        }

        public static bool TryParseBytes(string text, int expectedDlc, out byte[] data, out string error)
        {
            data = new byte[8];
            error = null;

            if (expectedDlc < 0 || expectedDlc > 8)
            {
                error = "DLC must be between 0 and 8.";
                return false;
            }

            string normalized = PadIncompleteNibbles(FormatDigits(ExtractHexDigits(text), expectedDlc));
            if (string.IsNullOrWhiteSpace(normalized))
            {
                if (expectedDlc == 0)
                    return true;
                error = $"Enter {expectedDlc} data byte(s).";
                return false;
            }

            var parts = normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > expectedDlc)
            {
                error = $"Too many bytes for DLC {expectedDlc}. Maximum is {expectedDlc} byte(s).";
                return false;
            }

            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length != 2 ||
                    !byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out data[i]))
                {
                    error = "Invalid hex byte value.";
                    return false;
                }
            }

            if (parts.Length < expectedDlc)
            {
                error = $"DLC is {expectedDlc} but only {parts.Length} byte(s) entered.";
                return false;
            }

            return true;
        }

        public static byte[] ParseOrDefault(string text, int maxBytes)
        {
            string digits = ExtractHexDigits(text);
            if (digits.Length > maxBytes * 2)
                digits = digits.Substring(0, maxBytes * 2);

            digits = ExtractHexDigits(PadIncompleteNibbles(FormatDigits(digits, maxBytes)));
            var parts = digits.Length == 0
                ? Array.Empty<string>()
                : PadIncompleteNibbles(FormatDigits(digits, maxBytes))
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            var data = new byte[8];
            for (int i = 0; i < Math.Min(maxBytes, parts.Length); i++)
                byte.TryParse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out data[i]);
            return data;
        }
    }
}
