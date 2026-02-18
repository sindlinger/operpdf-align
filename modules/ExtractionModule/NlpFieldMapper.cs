using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Obj.Utils;
using Obj.ValidatorModule;

namespace Obj.Extraction
{
    public sealed class NlpFieldMapConfig
    {
        public List<DocTypeRule> DocTypes { get; set; } = new();
        public List<FieldRule> Fields { get; set; } = new();
    }

    public sealed class DocTypeRule
    {
        public string Key { get; set; } = "";
        public List<string> KeywordsAny { get; set; } = new();
        public List<string> KeywordsAll { get; set; } = new();
    }

    public sealed class FieldRule
    {
        public string Key { get; set; } = "";
        public List<string> Labels { get; set; } = new();
        public List<string> DocTypes { get; set; } = new();
        public List<string> BeforeAny { get; set; } = new();
        public List<string> AfterAny { get; set; } = new();
        public List<string> BeforeAll { get; set; } = new();
        public List<string> AfterAll { get; set; } = new();
        public List<string> ExcludeAny { get; set; } = new();
        public int Window { get; set; } = 140;
        public int Priority { get; set; } = 0;
        public RegexRule? Regex { get; set; }
    }

    public sealed class RegexRule
    {
        public string Pattern { get; set; } = "";
        public int Group { get; set; } = 0;
    }

    public sealed class NlpFieldMapRequest
    {
        public string RawText { get; set; } = "";
        public string NlpJsonPath { get; set; } = "";
        public string Label { get; set; } = "segment";
        public string BaseName { get; set; } = "nlp";
        public string OutputDir { get; set; } = "";
        public string DocTypeHint { get; set; } = "";
    }

    public sealed class MappedField
    {
        public string Field { get; set; } = "";
        public string Value { get; set; } = "";
        public string Label { get; set; } = "";
        public int Start { get; set; }
        public int End { get; set; }
        public string Reason { get; set; } = "";
        public string DocType { get; set; } = "";
    }

    public sealed class NlpFieldMapResult
    {
        public bool Success { get; set; }
        public string JsonPath { get; set; } = "";
        public string Error { get; set; } = "";
        public List<MappedField> Fields { get; set; } = new();
        public string DocType { get; set; } = "";
    }

    internal sealed class NlpSpan
    {
        public string Label { get; set; } = "";
        public int Start { get; set; }
        public int End { get; set; }
        public string Text { get; set; } = "";
    }

    public static class NlpFieldMapper
    {
        private static readonly Regex CpfRegex = new(@"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CnpjRegex = new(@"\b\d{2}\.?\d{3}\.?\d{3}/\d{4}-?\d{2}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CnjRegex = new(@"\b\d{6,7}-?\d{2}[.\-]?\d{4}[.\-]?\d[.\-]?\d{2}(?:[.\-]?\d{4})?\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex MoneyRegex = new(@"\b(?:R\$\s*)?\d{1,3}(?:\.\d{3})*,\d{2}\b|\b(?:R\$\s*)?\d+,\d{2}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex DateSlashRegex = new(@"\b\d{1,2}/\d{1,2}/\d{4}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex DatePtRegex = new(@"\b\d{1,2}\s+de\s+(?:janeiro|fevereiro|março|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\s+de\s+\d{4}\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static NlpFieldMapResult Run(NlpFieldMapRequest request)
        {
            var result = new NlpFieldMapResult();
            if (request == null)
            {
                result.Error = "request_null";
                return result;
            }
            if (string.IsNullOrWhiteSpace(request.RawText))
            {
                result.Error = "raw_text_empty";
                return result;
            }
            if (string.IsNullOrWhiteSpace(request.NlpJsonPath) || !File.Exists(request.NlpJsonPath))
            {
                result.Error = "nlp_json_not_found";
                return result;
            }

            var mapPath = ResolveMapPath();
            if (string.IsNullOrWhiteSpace(mapPath) || !File.Exists(mapPath))
            {
                result.Error = "map_not_found";
                return result;
            }

            var map = LoadMap(mapPath);
            if (map == null || map.Fields.Count == 0)
            {
                result.Error = "map_empty";
                return result;
            }

            var raw = request.RawText;
            var norm = Normalize(raw);
            var docTypeHint = request.DocTypeHint?.Trim() ?? "";
            var docTypeHintKey = DocumentValidationRules.ResolveDocKeyFromHint(docTypeHint);
            var docType = "";
            var best = DetectBestDocType(norm, map.DocTypes);
            if (!string.IsNullOrWhiteSpace(best.key))
                docType = best.key;
            if (string.IsNullOrWhiteSpace(docType) && !string.IsNullOrWhiteSpace(docTypeHintKey))
            {
                var rule = map.DocTypes.FirstOrDefault(r =>
                    DocumentValidationRules.IsDocMatch(r.Key, docTypeHintKey) ||
                    string.Equals(r.Key, docTypeHintKey, StringComparison.OrdinalIgnoreCase));
                if (rule != null && IsDocTypeMatch(norm, rule))
                    docType = DocumentValidationRules.ResolveDocKeyFromHint(rule.Key);
            }
            if (string.IsNullOrWhiteSpace(docType))
                docType = docTypeHintKey;
            if (string.IsNullOrWhiteSpace(docType))
                docType = docTypeHint;
            var resolvedDocType = DocumentValidationRules.ResolveDocKeyFromHint(docType);
            if (!string.IsNullOrWhiteSpace(resolvedDocType))
                docType = resolvedDocType;
            result.DocType = docType;

            var spans = new List<NlpSpan>();
            spans.AddRange(ReadNlpJson(request.NlpJsonPath));
            spans.AddRange(RegexSpans(raw));

            var mapped = new List<MappedField>();
            foreach (var rule in map.Fields)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.Key))
                    continue;
                if (rule.DocTypes.Count > 0 && !rule.DocTypes.Any(dt =>
                    DocumentValidationRules.IsDocMatch(docType, dt) ||
                    string.Equals(dt, docType, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var field = ApplyRule(rule, spans, raw, norm, docType);
                if (field != null)
                    mapped.Add(field);
            }

            mapped = PostProcess(mapped, docType);

            if (string.IsNullOrWhiteSpace(request.OutputDir))
                request.OutputDir = Path.Combine("outputs", "fields");
            Directory.CreateDirectory(request.OutputDir);

            var label = SanitizeLabel(request.Label);
            var baseName = string.IsNullOrWhiteSpace(request.BaseName) ? "nlp" : request.BaseName;
            var outPath = Path.Combine(request.OutputDir, $"{baseName}_{label}_fields.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(mapped, JsonUtils.Indented));

            result.Fields = mapped;
            result.JsonPath = outPath;
            result.Success = true;
            return result;
        }

        private static NlpFieldMapConfig? LoadMap(string path)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            return deserializer.Deserialize<NlpFieldMapConfig>(File.ReadAllText(path));
        }

        private static string ResolveMapPath()
        {
            var reg = PatternRegistry.FindFile("nlp", "nlp_field_map.yml");
            if (!string.IsNullOrWhiteSpace(reg) && File.Exists(reg))
                return reg;

            var cwd = Directory.GetCurrentDirectory();
            var candidates = new[]
            {
                Path.Combine(cwd, "configs", "nlp_field_map.yml"),
                Path.Combine(cwd, "OBJ", "configs", "nlp_field_map.yml"),
                Path.Combine(cwd, "..", "configs", "nlp_field_map.yml")
            };
            return candidates.FirstOrDefault(File.Exists) ?? "";
        }

        private static string DetectDocType(string normText, List<DocTypeRule> rules)
        {
            var best = DetectBestDocType(normText, rules);
            return best.key ?? "";
        }

        private static (string? key, int score) DetectBestDocType(string normText, List<DocTypeRule> rules)
        {
            var bestKey = "";
            var bestScore = 0;
            if (rules == null || rules.Count == 0)
                return ("", 0);

            foreach (var rule in rules)
            {
                if (rule == null || string.IsNullOrWhiteSpace(rule.Key))
                    continue;
                var score = ScoreDocType(normText, rule);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestKey = DocumentDetectionPolicy.NormalizeDocTypeKey(rule.Key);
                }
            }

            return (bestKey, bestScore);
        }

        private static int ScoreDocType(string normText, DocTypeRule rule)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Key))
                return 0;
            return DocumentDetectionPolicy.ScoreKeywordRule(normText, rule.KeywordsAny, rule.KeywordsAll);
        }

        private static bool IsDocTypeMatch(string normText, DocTypeRule rule)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Key))
                return false;
            return DocumentDetectionPolicy.MatchesKeywordRule(normText, rule.KeywordsAny, rule.KeywordsAll);
        }

        private static MappedField? ApplyRule(FieldRule rule, List<NlpSpan> spans, string raw, string normText, string docType)
        {
            var labelSet = new HashSet<string>(rule.Labels ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var candidates = spans.Where(s => labelSet.Contains(s.Label)).ToList();

            MappedField? best = null;
            var bestScore = int.MinValue;

            foreach (var span in candidates)
            {
                var (before, after) = ExtractContext(raw, span.Start, span.End, rule.Window);
                var beforeNorm = Normalize(before);
                var afterNorm = Normalize(after);

                if (rule.BeforeAny.Count > 0 && !rule.BeforeAny.Any(k => Contains(beforeNorm, k)))
                    continue;
                if (rule.AfterAny.Count > 0 && !rule.AfterAny.Any(k => Contains(afterNorm, k)))
                    continue;
                if (rule.BeforeAll.Count > 0 && !rule.BeforeAll.All(k => Contains(beforeNorm, k)))
                    continue;
                if (rule.AfterAll.Count > 0 && !rule.AfterAll.All(k => Contains(afterNorm, k)))
                    continue;
                if (rule.ExcludeAny.Count > 0 && rule.ExcludeAny.Any(k => Contains(beforeNorm, k) || Contains(afterNorm, k)))
                    continue;

                var score = rule.Priority;
                score += CountMatches(rule.BeforeAny, beforeNorm);
                score += CountMatches(rule.AfterAny, afterNorm);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = new MappedField
                    {
                        Field = rule.Key,
                        Value = span.Text.Trim(),
                        Label = span.Label,
                        Start = span.Start,
                        End = span.End,
                        DocType = docType,
                        Reason = BuildReason(rule, beforeNorm, afterNorm)
                    };
                }
            }

            if (best == null && rule.Regex != null && !string.IsNullOrWhiteSpace(rule.Regex.Pattern))
            {
                var rx = new Regex(rule.Regex.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var m = rx.Match(raw);
                if (m.Success)
                {
                    var g = rule.Regex.Group > 0 && rule.Regex.Group < m.Groups.Count ? m.Groups[rule.Regex.Group] : m.Groups[0];
                    best = new MappedField
                    {
                        Field = rule.Key,
                        Value = g.Value.Trim(),
                        Label = "regex",
                        Start = g.Index,
                        End = g.Index + g.Length,
                        DocType = docType,
                        Reason = "regex_fallback"
                    };
                }
            }

            return best;
        }

        private static List<NlpSpan> ReadNlpJson(string path)
        {
            var spans = new List<NlpSpan>();
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results))
                return spans;
            if (results.ValueKind != JsonValueKind.Array)
                return spans;

            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("start", out var startEl) || !item.TryGetProperty("end", out var endEl))
                    continue;
                if (startEl.ValueKind != JsonValueKind.Number || endEl.ValueKind != JsonValueKind.Number)
                    continue;
                var start = startEl.GetInt32();
                var end = endEl.GetInt32();
                if (start < 0 || end <= start) continue;

                var label = item.TryGetProperty("entity_group", out var labelEl)
                    ? labelEl.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(label))
                {
                    if (item.TryGetProperty("entity", out var e))
                        label = e.GetString() ?? "";
                }

                var text = item.TryGetProperty("text_snippet", out var txtEl)
                    ? txtEl.GetString() ?? ""
                    : "";
                if (string.IsNullOrWhiteSpace(text) && item.TryGetProperty("word", out var wordEl))
                    text = wordEl.GetString() ?? "";

                spans.Add(new NlpSpan
                {
                    Label = (label ?? "").ToUpperInvariant(),
                    Start = start,
                    End = end,
                    Text = text
                });
            }

            return spans;
        }

        private static IEnumerable<NlpSpan> RegexSpans(string raw)
        {
            foreach (Match m in CpfRegex.Matches(raw))
                yield return new NlpSpan { Label = "CPF", Start = m.Index, End = m.Index + m.Length, Text = m.Value };
            foreach (Match m in CnpjRegex.Matches(raw))
                yield return new NlpSpan { Label = "CNPJ", Start = m.Index, End = m.Index + m.Length, Text = m.Value };
            foreach (Match m in CnjRegex.Matches(raw))
                yield return new NlpSpan { Label = "CNJ", Start = m.Index, End = m.Index + m.Length, Text = m.Value };
            foreach (Match m in MoneyRegex.Matches(raw))
                yield return new NlpSpan { Label = "MONEY", Start = m.Index, End = m.Index + m.Length, Text = m.Value };
            foreach (Match m in DateSlashRegex.Matches(raw))
                yield return new NlpSpan { Label = "DATE", Start = m.Index, End = m.Index + m.Length, Text = m.Value };
            foreach (Match m in DatePtRegex.Matches(raw))
                yield return new NlpSpan { Label = "DATE", Start = m.Index, End = m.Index + m.Length, Text = m.Value };
        }

        private static (string before, string after) ExtractContext(string text, int start, int end, int window)
        {
            if (string.IsNullOrEmpty(text))
                return ("", "");

            if (start < 0) start = 0;
            if (end < start) end = start;
            if (start > text.Length) start = text.Length;
            if (end > text.Length) end = text.Length;

            var s = Math.Max(0, start - window);
            var e = Math.Min(text.Length, end + window);
            var before = text.Substring(s, start - s);
            var after = text.Substring(end, e - end);
            return (before, after);
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in normalized)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == UnicodeCategory.NonSpacingMark)
                    continue;
                sb.Append(char.ToLowerInvariant(ch));
            }
            var collapsed = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            return collapsed;
        }

        private static bool Contains(string normText, string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            var n = Normalize(token);
            if (string.IsNullOrWhiteSpace(n)) return false;
            return normText.Contains(n, StringComparison.OrdinalIgnoreCase);
        }

        private static int CountMatches(List<string> tokens, string normText)
        {
            if (tokens == null || tokens.Count == 0) return 0;
            var count = 0;
            foreach (var t in tokens)
                if (Contains(normText, t)) count++;
            return count;
        }

        private static string BuildReason(FieldRule rule, string beforeNorm, string afterNorm)
        {
            var bits = new List<string>();
            if (rule.BeforeAny.Count > 0)
                bits.Add("before_any");
            if (rule.AfterAny.Count > 0)
                bits.Add("after_any");
            return string.Join(",", bits);
        }

        private static List<MappedField> PostProcess(List<MappedField> fields, string docType)
        {
            if (fields == null) return new List<MappedField>();
            var byKey = fields.ToDictionary(f => f.Field, StringComparer.OrdinalIgnoreCase);

            string? finalValue = null;
            if (byKey.TryGetValue("VALOR_ARBITRADO_CM", out var vcm) && !string.IsNullOrWhiteSpace(vcm.Value))
                finalValue = vcm.Value;
            else if (byKey.TryGetValue("VALOR_ARBITRADO_DE", out var vde) && !string.IsNullOrWhiteSpace(vde.Value))
                finalValue = vde.Value;
            else if (byKey.TryGetValue("VALOR_ARBITRADO_JZ", out var vjz) && !string.IsNullOrWhiteSpace(vjz.Value))
                finalValue = vjz.Value;

            if (!string.IsNullOrWhiteSpace(finalValue))
            {
                fields.RemoveAll(f => f.Field.Equals("VALOR_ARBITRADO_FINAL", StringComparison.OrdinalIgnoreCase));
                fields.Add(new MappedField
                {
                    Field = "VALOR_ARBITRADO_FINAL",
                    Value = finalValue,
                    Label = "derived",
                    Start = -1,
                    End = -1,
                    DocType = docType,
                    Reason = "derived_by_priority"
                });
            }

            string? finalDate = null;
            if (byKey.TryGetValue("DATA_ARBITRADO_FINAL", out var dcm) && !string.IsNullOrWhiteSpace(dcm.Value))
                finalDate = dcm.Value;
            else if (byKey.TryGetValue("DATA_REQUISICAO", out var dreq) && !string.IsNullOrWhiteSpace(dreq.Value))
                finalDate = dreq.Value;

            if (!string.IsNullOrWhiteSpace(finalDate))
            {
                fields.RemoveAll(f => f.Field.Equals("DATA_ARBITRADO_FINAL", StringComparison.OrdinalIgnoreCase));
                fields.Add(new MappedField
                {
                    Field = "DATA_ARBITRADO_FINAL",
                    Value = finalDate,
                    Label = "derived",
                    Start = -1,
                    End = -1,
                    DocType = docType,
                    Reason = "derived_date"
                });
            }

            return fields;
        }

        private static string SanitizeLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "segment";
            foreach (var ch in Path.GetInvalidFileNameChars())
                label = label.Replace(ch, '_');
            return label.Replace(' ', '_');
        }
    }
}
