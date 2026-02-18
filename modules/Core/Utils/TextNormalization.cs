using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Obj.Utils
{
    public static class TextNormalization
    {
        public static string NormalizeWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var cleaned = Regex.Replace(text, "\\s+", " ");
            return cleaned.Trim();
        }

        public static string FixMissingSpaces(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            var t = text;
            t = Regex.Replace(t, @"(?<=[,:;])(?=\S)", " ");
            t = Regex.Replace(t, @"(?<=\))(?=\S)", ") ");
            t = Regex.Replace(t, @"(?<=\S)(?=\()", " ");
            t = Regex.Replace(t, @"(?<=[A-Za-zÀ-ÿ])(?=[0-9])", " ");
            t = Regex.Replace(t, @"(?<=[0-9])(?=[A-Za-zÀ-ÿ])", " ");
            t = Regex.Replace(t, @"(?<=[a-zà-ÿ])(?=[A-ZÁÂÃÀÉÊÍÓÔÕÚÇ])", " ");
            t = Regex.Replace(t, "\\s+", " ");
            return t.Trim();
        }

        public static string CollapseSpacedLettersText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = text
                .Replace('\u00A0', ' ')
                .Replace('\u2007', ' ')
                .Replace('\u202F', ' ')
                .Replace('\u200B', ' ')
                .Replace('\u200C', ' ')
                .Replace('\u200D', ' ')
                .Replace('\uFEFF', ' ');
            var parts = Regex.Matches(t, "\\S+|\\s+");
            if (parts.Count == 0) return "";
            var sb = new StringBuilder();
            var buffer = new StringBuilder();
            string pendingWs = "";

            foreach (Match m in parts)
            {
                var s = m.Value;
                if (string.IsNullOrWhiteSpace(s))
                {
                    pendingWs = s;
                    continue;
                }

                var token = s;
                var joinable = IsJoinToken(token);
                var canJoin = joinable && buffer.Length > 0 && IsTightSpace(pendingWs);

                if (canJoin)
                {
                    buffer.Append(token);
                }
                else
                {
                    if (buffer.Length > 0)
                    {
                        AppendWithSpace(sb, buffer.ToString());
                        buffer.Clear();
                    }
                    if (!string.IsNullOrEmpty(pendingWs))
                        AppendSpace(sb);

                    if (joinable)
                        buffer.Append(token);
                    else
                        AppendWithSpace(sb, token);
                }
                pendingWs = "";
            }

            if (buffer.Length > 0)
                AppendWithSpace(sb, buffer.ToString());

            return Regex.Replace(sb.ToString(), "\\s+", " ").Trim();
        }

        public static string FixUppercaseSplitTokens(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return text;

            static bool IsUpperToken(string token, int maxLen)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                var letters = token.Where(char.IsLetter).ToArray();
                if (letters.Length == 0 || letters.Length > maxLen) return false;
                foreach (var ch in letters)
                {
                    if (!char.IsUpper(ch)) return false;
                }
                return true;
            }

            static bool IsConnector(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                var t = token.ToUpperInvariant();
                return t is "DE" or "DA" or "DO" or "DOS" or "DAS" or "E" or "EM" or "NO" or "NA" or "NOS" or "NAS" or "POR" or "PELO" or "PELA";
            }

            var merged = new System.Collections.Generic.List<string>(parts.Length);
            int i = 0;
            while (i < parts.Length)
            {
                if (!IsUpperToken(parts[i], 3) || IsConnector(parts[i]))
                {
                    merged.Add(parts[i]);
                    i++;
                    continue;
                }

                var run = new StringBuilder();
                int j = i;
                int letters = 0;
                while (j < parts.Length && IsUpperToken(parts[j], 3) && !IsConnector(parts[j]))
                {
                    run.Append(parts[j]);
                    letters += parts[j].Count(char.IsLetter);
                    j++;
                }

                if (j - i >= 2 && letters >= 4)
                {
                    merged.Add(run.ToString());
                    i = j;
                    continue;
                }

                merged.Add(parts[i]);
                i++;
            }

            return NormalizeWhitespace(string.Join(" ", merged));
        }

        public static string FixUppercaseGlue(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            if (!IsMostlyUppercase(text) || CountSpaces(text) > 2) return text;

            var t = text;
            t = Regex.Replace(t, @"(?<=\p{Lu})(DA|DE|DO|DAS|DOS|E|EM|NO|NA|NOS|NAS|AO|AOS)(?=\p{Lu})", " $1 ");
            t = Regex.Replace(t, @"(?<=[0-9])(?=\p{Lu})", " ");
            t = Regex.Replace(t, @"(?<=\p{Lu})(?=[0-9])", " ");
            return NormalizeWhitespace(t);
        }

        public static string NormalizePatternText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = CollapseSpacedLettersText(text);
            t = NormalizeWhitespace(FixMissingSpaces(t));
            t = FixUppercaseTokenRuns(t);
            t = FixBrokenWords(t);
            t = FixUppercaseWordSplits(t);
            t = FixUppercasePrefixSplits(t);
            return t;
        }

        public static string NormalizeFullText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = NormalizePatternText(text);
            t = FixUppercaseSplitTokens(t);
            t = FixUppercaseGlue(t);
            t = FixTitleGlue(t);
            return t;
        }

        public static string FixBrokenWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return text;

            var result = new System.Collections.Generic.List<string>(parts.Length);
            var current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = parts[i];
                if (TryMergeBrokenTokens(current, next, out var merged, out var remainder))
                {
                    current = merged;
                    if (!string.IsNullOrEmpty(remainder))
                    {
                        result.Add(current);
                        current = remainder;
                    }
                    continue;
                }
                result.Add(current);
                current = next;
            }
            result.Add(current);
            return NormalizeWhitespace(string.Join(" ", result));
        }

        public static string FixUppercaseTokenRuns(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return text;

            var result = new System.Collections.Generic.List<string>(parts.Length);
            int i = 0;
            while (i < parts.Length)
            {
                if (!IsUpperShortToken(parts[i]))
                {
                    result.Add(parts[i]);
                    i++;
                    continue;
                }

                int j = i;
                var sb = new StringBuilder();
                while (j < parts.Length && IsUpperShortToken(parts[j]))
                {
                    sb.Append(parts[j]);
                    j++;
                }

                if (j - i >= 3)
                {
                    result.Add(sb.ToString());
                }
                else
                {
                    for (int k = i; k < j; k++)
                        result.Add(parts[k]);
                }
                i = j;
            }

            return NormalizeWhitespace(string.Join(" ", result));
        }

        public static string FixTitleGlue(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            var sb = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (i > 0 && char.IsLower(text[i - 1]) && char.IsUpper(c))
                {
                    var prevLen = CountLettersBackward(text, i - 1);
                    var nextLen = CountLettersForward(text, i);
                    if (prevLen >= 3 && nextLen >= 3)
                        sb.Append(' ');
                }
                sb.Append(c);
            }
            return NormalizeWhitespace(sb.ToString());
        }

        public static string FixUppercaseWordSplits(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return text;

            static bool IsUpperToken(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                var letters = token.Where(char.IsLetter).ToArray();
                if (letters.Length == 0) return false;
                foreach (var ch in letters)
                {
                    if (!char.IsUpper(ch)) return false;
                }
                return true;
            }

            static bool IsConnector(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                var t = token.ToUpperInvariant();
                return t is "DE" or "DA" or "DO" or "DOS" or "DAS" or "E" or "EM" or "NO" or "NA" or "NOS" or "NAS" or "POR" or "PELO" or "PELA";
            }

            var merged = new System.Collections.Generic.List<string>();
            int i = 0;
            while (i < parts.Length)
            {
                if (i + 1 < parts.Length)
                {
                    var a = parts[i];
                    var b = parts[i + 1];
                    if (IsUpperToken(a) && IsUpperToken(b) && !IsConnector(b))
                    {
                        var aLen = a.Count(char.IsLetter);
                        var bLen = b.Count(char.IsLetter);
                        if (aLen >= 3 && bLen <= 4)
                        {
                            merged.Add(a + b);
                            i += 2;
                            continue;
                        }
                    }
                }
                merged.Add(parts[i]);
                i++;
            }

            return NormalizeWhitespace(string.Join(" ", merged));
        }

        public static string FixLineBreakWordSplits(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            var t = text.Replace("\r\n", "\n");
            var lines = t.Split('\n');
            if (lines.Length <= 1) return text;

            static bool IsUpperToken(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                var letters = token.Where(char.IsLetter).ToArray();
                if (letters.Length == 0) return false;
                foreach (var ch in letters)
                {
                    if (!char.IsUpper(ch)) return false;
                }
                return true;
            }

            static bool IsConnector(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                var t = token.ToUpperInvariant();
                return t is "DE" or "DA" or "DO" or "DOS" or "DAS" or "E" or "EM" or "NO" or "NA" or "NOS" or "NAS" or "POR" or "PELO" or "PELA";
            }

            static string? LastToken(string line)
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 0 ? null : parts[^1];
            }

            static string? FirstToken(string line)
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length == 0 ? null : parts[0];
            }

            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                var cur = lines[i].TrimEnd();
                if (string.IsNullOrEmpty(cur))
                    continue;

                if (i + 1 < lines.Length)
                {
                    var next = lines[i + 1].TrimStart();
                    if (!string.IsNullOrEmpty(next))
                    {
                        var lastTok = LastToken(cur) ?? "";
                        var firstTok = FirstToken(next) ?? "";

                        if (cur.EndsWith("-", StringComparison.Ordinal))
                        {
                            cur = cur.TrimEnd('-');
                            // remove first token from next
                            var idx = next.IndexOf(firstTok, StringComparison.Ordinal);
                            if (idx >= 0)
                                next = (idx + firstTok.Length < next.Length) ? next.Substring(idx + firstTok.Length) : "";
                            cur += firstTok;
                            lines[i + 1] = next;
                        }
                        else if (IsUpperToken(lastTok) && IsUpperToken(firstTok) && !IsConnector(lastTok) && !IsConnector(firstTok))
                        {
                            var lastLen = lastTok.Count(char.IsLetter);
                            var firstLen = firstTok.Count(char.IsLetter);
                            var shouldMerge = (lastLen >= 3 && firstLen <= 3) || (lastLen <= 3 && firstLen >= 4);
                            if (shouldMerge)
                            {
                                var cut = cur.Substring(0, cur.LastIndexOf(lastTok, StringComparison.Ordinal));
                                // remove first token from next
                                var idx = next.IndexOf(firstTok, StringComparison.Ordinal);
                                if (idx >= 0)
                                    next = (idx + firstTok.Length < next.Length) ? next.Substring(idx + firstTok.Length) : "";
                                cur = cut + lastTok + firstTok;
                                lines[i + 1] = next;
                            }
                        }
                    }
                }

                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(cur);
            }

            return NormalizeWhitespace(sb.ToString());
        }

        private static bool IsJoinToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (token.Length == 1)
            {
                var c = token[0];
                if (char.IsLetterOrDigit(c)) return true;
                if (c == '$' || c == '/' || c == '-' || c == '–' || c == '.' || c == ',' ||
                    c == 'ª' || c == 'º' || c == '°')
                    return true;
            }
            // Join short ALL‑CAPS chunks (1–3 letters) when spacing is tight.
            if (token.Length <= 3)
            {
                bool allLetters = true;
                bool allUpper = true;
                foreach (var ch in token)
                {
                    if (!char.IsLetter(ch)) { allLetters = false; break; }
                    if (!char.IsUpper(ch)) allUpper = false;
                }
                if (allLetters && allUpper)
                {
                    var t = token.ToUpperInvariant();
                    if (t is "DE" or "DA" or "DO" or "DOS" or "DAS" or "E" or "EM" or "NO" or "NA" or "NOS" or "NAS" or "POR" or "PELO" or "PELA")
                        return false;
                    return true;
                }
            }
            return false;
        }

        private static bool TryMergeBrokenTokens(string left, string right, out string merged, out string? remainder)
        {
            merged = left;
            remainder = null;
            if (!IsWordToken(left) || !IsWordToken(right))
                return false;

            var normLeft = NormalizeWordToken(left);
            var normRight = NormalizeWordToken(right);
            if (normLeft.Length == 0 || normRight.Length == 0)
                return false;

            if (IsAllUpperWord(left) && IsAllUpperWord(right) && right.Length <= 3)
            {
                merged = left + right;
                return true;
            }

            if (IsConnectorToken(normLeft) || IsConnectorToken(normRight))
                return false;

            if (right.Length >= 2 && char.IsLower(right[0]) && char.IsUpper(right[1]) && EndsWithLetter(left))
            {
                merged = left + right[0];
                remainder = right.Substring(1);
                return true;
            }

            if (right.Length <= 2 && EndsWithLetter(left))
            {
                merged = left + right;
                return true;
            }

            if (left.Length <= 2 && right.Length <= 2)
            {
                merged = left + right;
                return true;
            }

            if (left.Length <= 3 && right.Length <= 3 && char.IsLower(right[0]))
            {
                merged = left + right;
                return true;
            }

            return false;
        }

        private static bool IsWordToken(string token)
        {
            foreach (var ch in token)
            {
                if (!char.IsLetter(ch))
                    return false;
            }
            return token.Length > 0;
        }

        private static bool IsUpperShortToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (token.Length > 3) return false;
            foreach (var ch in token)
            {
                if (!char.IsLetter(ch))
                    return false;
                if (!char.IsUpper(ch))
                    return false;
            }
            return true;
        }

        private static bool IsAllUpperWord(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            bool hasLetter = false;
            foreach (var ch in token)
            {
                if (!char.IsLetter(ch))
                    return false;
                hasLetter = true;
                if (!char.IsUpper(ch))
                    return false;
            }
            return hasLetter;
        }

        private static string NormalizeWordToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "";
            var sb = new StringBuilder(token.Length);
            foreach (var ch in token)
            {
                if (char.IsLetter(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        private static bool IsConnectorToken(string token)
        {
            return token is "de" or "da" or "do" or "dos" or "das" or "e" or "em" or "no" or "na" or "nos" or "nas" or "por" or "pelo" or "pela";
        }

        private static bool EndsWithLetter(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            var last = token[token.Length - 1];
            return char.IsLetter(last);
        }

        private static int CountLettersBackward(string text, int idx)
        {
            int count = 0;
            for (int i = idx; i >= 0; i--)
            {
                if (!char.IsLetter(text[i]))
                    break;
                count++;
            }
            return count;
        }

        private static int CountLettersForward(string text, int idx)
        {
            int count = 0;
            for (int i = idx; i < text.Length; i++)
            {
                if (!char.IsLetter(text[i]))
                    break;
                count++;
            }
            return count;
        }

        private static bool IsTightSpace(string ws)
        {
            if (string.IsNullOrEmpty(ws)) return false;
            if (ws.IndexOf('\n') >= 0 || ws.IndexOf('\r') >= 0) return false;
            for (int i = 0; i < ws.Length; i++)
            {
                var c = ws[i];
                if (c != ' ' && c != '\t' && c != '\u00A0' && c != '\u2007' && c != '\u202F')
                    return false;
            }
            return ws.Length <= 2;
        }

        private static string FixUppercasePrefixSplits(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return text;

            static bool IsConnector(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                var t = token.ToUpperInvariant();
                return t is "DE" or "DA" or "DO" or "DAS" or "DOS" or "E" or "EM" or "NO" or "NA" or "NOS" or "NAS" or "AO" or "AOS" or "POR" or "PELO" or "PELA";
            }

            static bool IsUpperToken(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                bool hasLetter = false;
                foreach (var ch in token)
                {
                    if (!char.IsLetter(ch)) continue;
                    hasLetter = true;
                    if (!char.IsUpper(ch)) return false;
                }
                return hasLetter;
            }

            var merged = new System.Collections.Generic.List<string>(parts.Length);
            int i = 0;
            while (i < parts.Length)
            {
                if (i + 1 < parts.Length)
                {
                    var a = parts[i];
                    var b = parts[i + 1];
                    if (IsUpperToken(a) && IsUpperToken(b) &&
                        a.Length <= 3 && b.Length >= 4 &&
                        !IsConnector(a))
                    {
                        merged.Add(a + b);
                        i += 2;
                        continue;
                    }
                }
                merged.Add(parts[i]);
                i++;
            }

            return NormalizeWhitespace(string.Join(" ", merged));
        }

        private static void AppendSpace(StringBuilder sb)
        {
            if (sb.Length == 0) return;
            if (sb[sb.Length - 1] != ' ')
                sb.Append(' ');
        }

        private static void AppendWithSpace(StringBuilder sb, string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            if (sb.Length > 0 && sb[sb.Length - 1] != ' ')
                sb.Append(' ');
            sb.Append(token);
        }

        private static bool IsMostlyUppercase(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            int letters = 0;
            int upper = 0;
            foreach (var c in text)
            {
                if (!char.IsLetter(c)) continue;
                letters++;
                if (char.IsUpper(c)) upper++;
            }
            if (letters == 0) return false;
            return (upper / (double)letters) >= 0.75;
        }

        private static int CountSpaces(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0;
            foreach (var c in text)
            {
                if (c == ' ' || c == '\t' || c == '\u00A0' || c == '\u2007' || c == '\u202F')
                    count++;
            }
            return count;
        }
    }
}
