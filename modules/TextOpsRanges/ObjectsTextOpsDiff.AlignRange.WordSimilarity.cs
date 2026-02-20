using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DiffMatchPatch;

namespace Obj.Align
{
    internal static partial class ObjectsTextOpsDiff
    {
        private static double ComputeWordSimilarity(string a, string b, out int tokenCountA, out int tokenCountB)
        {
            var tokensA = TokenizeForWordSimilarity(a);
            var tokensB = TokenizeForWordSimilarity(b);
            tokenCountA = tokensA.Count;
            tokenCountB = tokensB.Count;

            if (tokensA.Count == 0 && tokensB.Count == 0)
                return 1.0;
            if (tokensA.Count == 0 || tokensB.Count == 0)
                return 0.0;

            var encodedA = EncodeTokensForDiff(tokensA, tokensB, out var encodedB);
            if (encodedA.Length == 0 && encodedB.Length == 0)
                return 1.0;

            var dmp = new diff_match_patch();
            var diffs = dmp.diff_main(encodedA, encodedB, false);
            var dist = dmp.diff_levenshtein(diffs);
            var maxLen = Math.Max(tokensA.Count, tokensB.Count);
            if (maxLen == 0)
                return 0.0;

            var textSim = 1.0 - (double)dist / maxLen;
            var lenSim = 1.0 - (double)Math.Abs(tokensA.Count - tokensB.Count) / maxLen;
            return (textSim * 0.78) + (lenSim * 0.22);
        }

        private static List<string> TokenizeForWordSimilarity(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            var normalized = NormalizeAnchorSearchText(text ?? "");
            if (normalized.Length == 0)
                return new List<string>();

            var raw = Regex.Matches(normalized, "[a-z0-9#]+")
                .Select(m => m.Value)
                .Where(v => v.Length > 0)
                .ToList();
            if (raw.Count == 0)
                return raw;

            // Junta sequências OCR com letras separadas (ex.: "d i r e t o r i a").
            var merged = new List<string>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                var token = raw[i];
                if (token.Length == 1 && char.IsLetter(token[0]))
                {
                    int j = i;
                    var chars = new List<char>();
                    while (j < raw.Count && raw[j].Length == 1 && char.IsLetter(raw[j][0]))
                    {
                        chars.Add(raw[j][0]);
                        j++;
                    }
                    if (chars.Count >= 3)
                    {
                        merged.Add(new string(chars.ToArray()));
                        i = j - 1;
                        continue;
                    }
                }

                merged.Add(token);
            }

            return merged;
        }

        private static string EncodeTokensForDiff(List<string> tokensA, List<string> tokensB, out string encodedB)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            var next = 1;

            void AddToken(string token)
            {
                token ??= "";
                if (map.ContainsKey(token))
                    return;
                if (next >= char.MaxValue)
                    return;
                map[token] = next;
                next++;
            }

            foreach (var token in tokensA)
                AddToken(token);
            foreach (var token in tokensB)
                AddToken(token);

            string Encode(List<string> tokens)
            {
                var chars = new char[tokens.Count];
                for (int i = 0; i < tokens.Count; i++)
                {
                    var key = tokens[i] ?? "";
                    chars[i] = map.TryGetValue(key, out var idx) ? (char)idx : (char)0;
                }
                return new string(chars);
            }

            encodedB = Encode(tokensB);
            return Encode(tokensA);
        }
    }
}
