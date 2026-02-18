using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Obj.TjpbDespachoExtractor.Utils;
using Obj.Utils;
using iText.Kernel.Pdf;

namespace Obj.Align
{
    internal static partial class ObjectsTextOpsDiff
    {
        internal sealed class PatternBlock
        {
            public int Index { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string Text { get; set; } = "";
            public string RawText { get; set; } = "";
            public List<string> RawTokens { get; set; } = new List<string>();
            public string Pattern { get; set; } = "";
            public string PatternTyped { get; set; } = "";
            public int MaxTokenLen { get; set; }
            public int LineCount { get; set; }
            public string OpsLabel { get; set; } = "";
            public double? YMin { get; set; }
            public double? YMax { get; set; }
            public double? XMin { get; set; }
            public double? XMax { get; set; }
        }

        internal static List<PatternBlock> ExtractPatternBlocks(PdfStream stream, PdfResources resources, HashSet<string> opFilter)
            => ExtractPatternBlocks(stream, resources, opFilter, allowFix: true, timeoutSec: 0);

        internal static List<PatternBlock> ExtractPatternBlocks(PdfStream stream, PdfResources resources, HashSet<string> opFilter, bool allowFix)
            => ExtractPatternBlocks(stream, resources, opFilter, allowFix, timeoutSec: 0);

        internal static List<PatternBlock> ExtractPatternBlocks(PdfStream stream, PdfResources resources, HashSet<string> opFilter, bool allowFix, double timeoutSec)
        {
            var blocks = ExtractSelfBlocks(stream, resources, opFilter, allowFix, timeoutSec);
            if (blocks.Count == 0) return new List<PatternBlock>();
            return blocks.Select(ToPatternBlock).ToList();
        }

        internal static string EncodePatternSimple(string text)
        {
            var normalized = NormalizePatternText(text);
            if (string.IsNullOrWhiteSpace(normalized)) return "";
            var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            foreach (var part in parts)
                sb.Append(part == ":" ? ":" : (part.Length == 1 ? '1' : 'W'));
            return sb.ToString();
        }

        internal static string EncodePatternTyped(string text)
        {
            var normalized = NormalizePatternText(text);
            return BuildTypedPattern(normalized);
        }

        private static PatternBlock ToPatternBlock(SelfBlock block)
        {
            var text = NormalizePatternText(block.Text ?? "");
            return new PatternBlock
            {
                Index = block.Index,
                StartOp = block.StartOp,
                EndOp = block.EndOp,
                Text = text,
                RawText = block.RawText ?? "",
                RawTokens = block.RawTokens ?? new List<string>(),
                Pattern = block.Pattern ?? "",
                PatternTyped = EncodePatternTyped(text),
                MaxTokenLen = block.MaxTokenLen,
                LineCount = block.LineCount,
                OpsLabel = block.OpsLabel ?? "",
                YMin = block.YMin,
                YMax = block.YMax,
                XMin = block.XMin,
                XMax = block.XMax
            };
        }

        private static string NormalizePatternText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return TextNormalization.NormalizePatternText(text);
        }

        private static string BuildTypedPattern(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                sb.Append(ClassifyToken(part));
            }
            return sb.ToString();
        }

        private static string ClassifyToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "S";
            var raw = token.Trim();
            var hasColon = raw.EndsWith(":", StringComparison.Ordinal);
            if (hasColon && raw.Length > 1)
                raw = raw.Substring(0, raw.Length - 1);
            if (raw == ":" && !hasColon) return ":";
            if (raw.Contains('@') && raw.Contains('.')) return "E";
            if (raw.StartsWith("R$", StringComparison.OrdinalIgnoreCase) || raw.Equals("R$", StringComparison.OrdinalIgnoreCase)) return "V";

            if (IsNumeroMarker(raw))
                return AttachColon(AppendSize("R", SizeSuffix(raw)), hasColon);

            var core = TrimEdgePunct(raw);
            if (string.IsNullOrWhiteSpace(core)) return "S";

            var digits = DigitsOnly(core);
            var size = SizeSuffix(core);
            if (TryOrdinalSuffix(core, out var ord))
                return AttachColon("N" + size + ord, hasColon);
            if (digits.Length == 11) return AttachColon(AppendSize("F", size), hasColon);
            if (digits.Length == 14) return AttachColon(AppendSize("J", size), hasColon);
            if (digits.Length == 20 || digits.Length == 16) return AttachColon(AppendSize("Q", size), hasColon);

            if (LooksLikeMoney(raw) && TextUtils.TryParseMoney(raw, out _)) return AttachColon(AppendSize("V", size), hasColon);
            if (TextUtils.TryParseDate(raw, out _)) return AttachColon(AppendSize("A", size), hasColon);

            var lower = core.ToLowerInvariant();
            if (Particles.Contains(lower)) return AttachColon(AppendSize("P", size), hasColon);

            bool hasDigit = false;
            bool hasLetter = false;
            bool hasUpper = false;
            bool hasLower = false;
            bool hasPunct = false;

            foreach (var ch in core)
            {
                if (char.IsDigit(ch)) { hasDigit = true; continue; }
                if (char.IsLetter(ch))
                {
                    hasLetter = true;
                    if (char.IsUpper(ch)) hasUpper = true;
                    if (char.IsLower(ch)) hasLower = true;
                    continue;
                }
                hasPunct = true;
            }

            if (!hasLetter && hasDigit)
                return AttachColon(AppendSize("N", size), hasColon);

            if (hasLetter && !hasDigit)
            {
                if (hasUpper && !hasLower) return AttachColon(AppendSize("U", size), hasColon);
                if (hasLower && !hasUpper) return AttachColon(AppendSize("L", size), hasColon);
                if (IsTitleCase(core)) return AttachColon(AppendSize("T", size), hasColon);
                return AttachColon(AppendSize("M", size), hasColon);
            }

            if (hasLetter && hasDigit)
                return AttachColon(AppendSize("M", size), hasColon);

            return AttachColon(AppendSize("S", size), hasColon);
        }

        private static string DigitsOnly(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (var ch in text)
            {
                if (char.IsDigit(ch))
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        private static bool TryOrdinalSuffix(string token, out string suffix)
        {
            suffix = "";
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (token.Length < 2) return false;
            var last = token[token.Length - 1];
            var head = token.Substring(0, token.Length - 1);
            if (head.Length == 0 || head.Any(ch => !char.IsDigit(ch)))
                return false;

            if (last == 'ª' || last == 'a' || last == 'A')
            {
                suffix = "a";
                return true;
            }
            if (last == 'º' || last == 'o' || last == 'O')
            {
                suffix = "o";
                return true;
            }
            return false;
        }

        private static string AttachColon(string code, bool hasColon)
        {
            if (!hasColon) return code;
            return code + ":";
        }

        private static string SizeSuffix(string token)
        {
            if (string.IsNullOrEmpty(token)) return "1";
            return token.Length == 1 ? "1" : "2";
        }

        private static string AppendSize(string code, string size)
        {
            if (string.IsNullOrWhiteSpace(code)) return code;
            return code + size;
        }

        private static bool LooksLikeMoney(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var t = raw.Trim();
            if (t.Contains("R$", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.Contains(",")) return true;
            return false;
        }

        private static bool IsNumeroMarker(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            var t = token.Trim().ToLowerInvariant();
            if (t == "nº" || t == "n°" || t == "n.")
                return true;
            return false;
        }

        private static string TrimEdgePunct(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "";
            int start = 0;
            int end = token.Length - 1;
            while (start <= end && IsEdgePunct(token[start])) start++;
            while (end >= start && IsEdgePunct(token[end])) end--;
            if (start > end) return "";
            return token.Substring(start, end - start + 1);
        }

        private static bool IsEdgePunct(char c)
        {
            return char.IsPunctuation(c) || char.IsSymbol(c);
        }

        private static bool IsTitleCase(string token)
        {
            int first = -1;
            for (int i = 0; i < token.Length; i++)
            {
                if (char.IsLetter(token[i]))
                {
                    first = i;
                    break;
                }
            }
            if (first < 0) return false;
            if (!char.IsUpper(token[first])) return false;
            for (int i = first + 1; i < token.Length; i++)
            {
                if (char.IsLetter(token[i]) && char.IsUpper(token[i]))
                    return false;
            }
            return true;
        }

        private static readonly HashSet<string> Particles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "da","de","do","dos","das","e","em","no","na","nos","nas",
            "ao","aos","a","o","nº","n°","n.","nr","n",
            "art","sr","sra","sr.","sra."
        };
    }
}
