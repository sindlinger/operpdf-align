using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Obj.Models;
using Obj.TjpbDespachoExtractor.Models;
using Obj.Utils;

namespace Obj.TjpbDespachoExtractor.Utils
{
    public static class TextUtils
    {
        public static string NormalizeWhitespace(string text)
        {
            return TextNormalization.NormalizeWhitespace(text);
        }

        public static string FixMissingSpaces(string text)
        {
            return TextNormalization.FixMissingSpaces(text);
        }

        public static string FixUppercaseSplitTokens(string text)
        {
            return TextNormalization.FixUppercaseSplitTokens(text);
        }

        public static string BuildLineText(List<WordInfo> words, double wordGapX)
        {
            if (words == null || words.Count == 0) return "";
            var ordered = DeduplicateWords(words).OrderBy(w => w.NormX0).ToList();
            var gapThreshold = wordGapX;
            if (ordered.Count >= 4)
            {
                var gaps = new List<double>();
                for (int i = 1; i < ordered.Count; i++)
                {
                    var gap = ordered[i].NormX0 - ordered[i - 1].NormX1;
                    if (gap > 0) gaps.Add(gap);
                }
                if (gaps.Count >= 4)
                {
                    gaps.Sort();
                    var idx = (int)Math.Floor(0.9 * (gaps.Count - 1));
                    var p90 = gaps[idx];
                    var dynamic = p90 * 0.3;
                    gapThreshold = Math.Max(0.001, Math.Min(wordGapX, dynamic));
                }
            }
            var sb = new StringBuilder();
            WordInfo? prev = null;
            foreach (var w in ordered)
            {
                if (prev != null)
                {
                    var gap = w.NormX0 - prev.NormX1;
                    if (gap > gapThreshold)
                        sb.Append(' ');
                }
                sb.Append(NormalizeToken(w.Text));
                prev = w;
            }
            var line = sb.ToString();
            line = TextNormalization.CollapseSpacedLettersText(line);
            return NormalizeWhitespace(line);
        }

        public static string BuildTextFromWords(List<WordInfo> words, double lineMergeY, double wordGapX)
        {
            if (words == null || words.Count == 0) return "";
            var ordered = DeduplicateWords(words)
                .OrderByDescending(w => (w.NormY0 + w.NormY1) / 2.0)
                .ThenBy(w => w.NormX0)
                .ToList();

            var lines = new List<List<WordInfo>>();
            var current = new List<WordInfo>();
            double prevCy = double.MaxValue;
            foreach (var w in ordered)
            {
                var cy = (w.NormY0 + w.NormY1) / 2.0;
                if (current.Count == 0 || Math.Abs(cy - prevCy) <= lineMergeY)
                {
                    current.Add(w);
                }
                else
                {
                    lines.Add(current);
                    current = new List<WordInfo> { w };
                }
                prevCy = cy;
            }
            if (current.Count > 0) lines.Add(current);

            var sb = new StringBuilder();
            foreach (var line in lines)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(BuildLineText(line, wordGapX));
            }
            return NormalizeWhitespace(sb.ToString());
        }

        public static string NormalizeForHash(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = text.ToLowerInvariant();
            t = Regex.Replace(t, "\\p{C}+", " ");
            t = Regex.Replace(t, "\\s+", " ").Trim();
            return t;
        }

        public static string NormalizeForMatch(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var collapsed = TextNormalization.NormalizePatternText(text);
            var t = RemoveDiacritics(collapsed).ToLowerInvariant();
            t = Regex.Replace(t, "[^a-z0-9\\s/\\-\\.]+", " ");
            t = Regex.Replace(t, "\\s+", " ").Trim();
            return t;
        }

        public static double ComputeWeirdSpacingRatio(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0.0;

            var tokens = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return 0.0;

            var singleCount = 0;
            foreach (var token in tokens)
            {
                var trimmed = token.Trim();
                if (trimmed.Length == 1 && char.IsLetterOrDigit(trimmed[0]))
                    singleCount++;
            }

            return singleCount / (double)tokens.Length;
        }

        public static string NormalizeForDiff(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = RemoveDiacritics(text);
            return t.ToLowerInvariant();
        }

        public static string NormalizeToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return "";
            return CollapseDoubledChars(token);
        }

        public static string CollapseDoubledChars(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "";
            var t = token;
            if (t.Length < 4 || (t.Length % 2) != 0)
                return t;
            int totalPairs = t.Length / 2;
            int equalPairs = 0;
            for (int i = 0; i < t.Length - 1; i += 2)
            {
                if (t[i] == t[i + 1])
                    equalPairs++;
            }
            if (equalPairs < Math.Max(3, (int)Math.Ceiling(totalPairs * 0.7)))
                return t;
            var sb = new StringBuilder(totalPairs);
            for (int i = 0; i < t.Length; i += 2)
                sb.Append(t[i]);
            return sb.ToString();
        }

        public static string CollapseSpacedLettersText(string text)
        {
            return TextNormalization.CollapseSpacedLettersText(text);
        }

        public static List<WordInfo> DeduplicateWords(List<WordInfo> words, int decimals = 3)
        {
            if (words == null || words.Count == 0) return new List<WordInfo>();
            var map = new Dictionary<string, WordInfo>();
            foreach (var w in words)
            {
                if (w == null || string.IsNullOrWhiteSpace(w.Text)) continue;
                var key = $"{w.Text}|{Math.Round(w.NormX0, decimals)}|{Math.Round(w.NormY0, decimals)}|{Math.Round(w.NormX1, decimals)}|{Math.Round(w.NormY1, decimals)}";
                if (!map.ContainsKey(key))
                    map[key] = w;
            }
            return map.Values.ToList();
        }

        public static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        public static string Sha256Hex(string text)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static BBoxN? UnionBBox(IEnumerable<WordInfo> words)
        {
            if (words == null) return null;
            double x0 = 1, y0 = 1, x1 = 0, y1 = 0;
            bool any = false;
            foreach (var w in words)
            {
                any = true;
                x0 = Math.Min(x0, w.NormX0);
                y0 = Math.Min(y0, w.NormY0);
                x1 = Math.Max(x1, w.NormX1);
                y1 = Math.Max(y1, w.NormY1);
            }
            if (!any) return null;
            return new BBoxN { X0 = Clamp01(x0), Y0 = Clamp01(y0), X1 = Clamp01(x1), Y1 = Clamp01(y1) };
        }

        public static BBoxN? UnionBBox(IEnumerable<BBoxN> boxes)
        {
            if (boxes == null) return null;
            double x0 = 1, y0 = 1, x1 = 0, y1 = 0;
            bool any = false;
            foreach (var b in boxes)
            {
                any = true;
                x0 = Math.Min(x0, b.X0);
                y0 = Math.Min(y0, b.Y0);
                x1 = Math.Max(x1, b.X1);
                y1 = Math.Max(y1, b.Y1);
            }
            if (!any) return null;
            return new BBoxN { X0 = Clamp01(x0), Y0 = Clamp01(y0), X1 = Clamp01(x1), Y1 = Clamp01(y1) };
        }

        public static string SafeSnippet(string text, int start, int length, int maxLen = 160)
        {
            if (string.IsNullOrEmpty(text)) return "";
            start = Math.Max(0, Math.Min(start, text.Length));
            length = Math.Max(0, Math.Min(length, text.Length - start));
            var snippet = text.Substring(start, length);
            snippet = NormalizeWhitespace(snippet);
            if (snippet.Length > maxLen)
                snippet = snippet.Substring(0, maxLen);
            return snippet;
        }

        public static string NormalizeCpf(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            return digits;
        }
        public static bool TryParseMoney(string raw, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            // OCR/textops can introduce weird spacing inside numbers (ex: "45 0, 00").
            // Normalize aggressively for parsing but keep it strict enough to avoid
            // accidentally parsing unrelated digits.
            var cleaned = raw.Trim().Replace('\u00A0', ' ');

            // Keep only digits, separators and sign; drop labels/words.
            cleaned = Regex.Replace(cleaned, @"[^\d\.,\-\s]+", "");
            // Remove *all* whitespace to fix broken numeric tokens.
            cleaned = Regex.Replace(cleaned, @"\s+", "");

            if (string.IsNullOrWhiteSpace(cleaned))
                return false;

            var sign = 1m;
            if (cleaned.StartsWith("-", StringComparison.Ordinal))
            {
                sign = -1m;
                cleaned = cleaned.TrimStart('-');
            }

            if (string.IsNullOrWhiteSpace(cleaned))
                return false;

            // If we have an explicit decimal separator at the end, treat it as the decimal part.
            // Accept 1-2 decimals because OCR sometimes drops the trailing zero.
            string canonical;
            var m = Regex.Match(cleaned, @"([,\.])(\d{1,2})$");
            if (m.Success)
            {
                var decimals = m.Groups[2].Value.PadRight(2, '0');
                var prefix = cleaned.Substring(0, m.Index);
                var integerDigits = Regex.Replace(prefix, @"\D", "");
                if (string.IsNullOrWhiteSpace(integerDigits))
                    integerDigits = "0";
                canonical = integerDigits + "." + decimals;
            }
            else
            {
                // No explicit decimals; treat as integer.
                var integerDigits = Regex.Replace(cleaned, @"\D", "");
                if (string.IsNullOrWhiteSpace(integerDigits))
                    return false;
                canonical = integerDigits;
            }

            if (!decimal.TryParse(canonical, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                return false;

            value = parsed * sign;
            return true;
        }

        public static string NormalizeMoney(string raw)
        {
            if (!TryParseMoney(raw, out var val)) return "";
            return $"R$ {val:N2}".Replace(",", "X").Replace(".", ",").Replace("X", ".");
        }

        public static bool TryParseDate(string raw, out string iso)
        {
            iso = "";
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var r = raw.Trim();
            string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy", "dd-MM-yyyy", "d-M-yyyy", "dd-MM-yy", "d-M-yy", "yyyy-MM-dd" };
            if (DateTime.TryParseExact(r, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                iso = dt.ToString("yyyy-MM-dd");
                return true;
            }
            var m = Regex.Match(r, @"\b(\d{1,2})\s+de\s+([A-Za-z]+)\s+de\s+(\d{4})\b", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var day = int.Parse(m.Groups[1].Value);
                var month = MonthFromPortuguese(m.Groups[2].Value);
                var year = int.Parse(m.Groups[3].Value);
                if (month >= 1 && month <= 12)
                {
                    var dt2 = new DateTime(year, month, day);
                    iso = dt2.ToString("yyyy-MM-dd");
                    return true;
                }
            }
            return false;
        }

        public static int MonthFromPortuguese(string month)
        {
            var m = RemoveDiacritics(month).ToLowerInvariant();
            return m switch
            {
                "janeiro" => 1,
                "fevereiro" => 2,
                "marco" => 3,
                "abril" => 4,
                "maio" => 5,
                "junho" => 6,
                "julho" => 7,
                "agosto" => 8,
                "setembro" => 9,
                "outubro" => 10,
                "novembro" => 11,
                "dezembro" => 12,
                _ => 0
            };
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }
}
