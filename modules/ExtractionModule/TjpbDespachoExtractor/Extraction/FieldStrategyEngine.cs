using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Obj.Models;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Models;
using Obj.TjpbDespachoExtractor.Utils;
using Obj.ValidatorModule;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.TjpbDespachoExtractor.Extraction
{
    public class FieldStrategyCatalog
    {
        public List<FieldStrategyDefinition> Strategies { get; } = new List<FieldStrategyDefinition>();

        public static FieldStrategyCatalog Load(TjpbDespachoConfig cfg)
        {
            var catalog = new FieldStrategyCatalog();
            if (cfg.FieldStrategies == null || !cfg.FieldStrategies.Enabled)
                return catalog;

            var files = new List<string>();
            if (cfg.FieldStrategies.Files != null)
            {
                foreach (var f in cfg.FieldStrategies.Files)
                {
                    if (string.IsNullOrWhiteSpace(f)) continue;
                    var resolved = ResolvePath(cfg.BaseDir, f);
                    if (File.Exists(resolved)) files.Add(resolved);
                }
            }

            var dir = cfg.FieldStrategies.Dir;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var resolvedDir = ResolvePath(cfg.BaseDir, dir!);
                if (Directory.Exists(resolvedDir))
                {
                    files.AddRange(Directory.GetFiles(resolvedDir, "*.yml"));
                    files.AddRange(Directory.GetFiles(resolvedDir, "*.yaml"));
                }
            }

            files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0) return catalog;

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            foreach (var path in files)
            {
                try
                {
                    using var reader = new StreamReader(path);
                    var def = deserializer.Deserialize<FieldStrategyDefinition>(reader);
                    if (def != null)
                        catalog.Strategies.Add(def);
                }
                catch
                {
                    // ignore malformed strategy files; extraction will continue
                }
            }

            return catalog;
        }

        private static string ResolvePath(string baseDir, string path)
        {
            if (Path.IsPathRooted(path)) return path;
            if (string.IsNullOrWhiteSpace(baseDir)) return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(baseDir, path));
        }
    }

    public class FieldStrategyEngine
    {
        private readonly FieldStrategyCatalog _catalog;
        private readonly Regex _cnjRegex;
        private readonly Regex _moneyRegex;
        private static readonly Dictionary<string, string> FieldMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "PROCESSOJUDICIAL", "PROCESSO_JUDICIAL" },
            { "PROCESSOADMINISTRATIVO", "PROCESSO_ADMINISTRATIVO" },
            { "PROMOVENTE", "PROMOVENTE" },
            { "PROMOVIDO", "PROMOVIDO" },
            { "PERITO", "PERITO" },
            { "CPFCNPJ", "CPF_PERITO" },
            { "CPF", "CPF_PERITO" },
            { "PROFISSAO", "ESPECIALIDADE" },
            { "JUIZO", "VARA" },
            { "VARA", "VARA" },
            { "COMARCA", "COMARCA" },
            { "ESPECIALIDADE", "ESPECIALIDADE" },
            { "ESPECIEDEPERICIA", "ESPECIE_DA_PERICIA" },
            { "VALORARBITRADOJZ", "VALOR_ARBITRADO_JZ" },
            { "VALORARBITRADODE", "VALOR_ARBITRADO_DE" },
            { "VALORARBITRADOCM", "VALOR_ARBITRADO_CM" },
            { "DATADAAUTORIZACAODADESPESA", "DATA" },
            { "ADIANTAMENTO", "ADIANTAMENTO" },
            { "VALORTABELADOANEXOITABELAI", "VALOR_TABELADO_ANEXO_I" },
            { "ASSINANTE", "ASSINANTE" },
            { "ASSINATURA", "ASSINANTE" },
            { "SIGNATARIO", "ASSINANTE" },
            { "NUMPERITO", "NUM_PERITO" },
            { "NUMEROPERITO", "NUM_PERITO" },
            { "PERCENTUAL", "PERCENTUAL" },
            { "PARCELA", "PARCELA" },
            { "FATOR", "FATOR" }
        };

        public FieldStrategyEngine(TjpbDespachoConfig cfg)
        {
            _catalog = FieldStrategyCatalog.Load(cfg);
            _cnjRegex = new Regex(cfg.Regex.ProcessoCnj, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            _moneyRegex = new Regex(cfg.Regex.Money, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        public Dictionary<string, FieldInfo> Extract(DespachoContext ctx)
        {
            var result = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
            if (_catalog.Strategies.Count == 0) return result;

            var segments = BuildSegments(ctx);
            var fileName = ctx.FileName ?? "";

            foreach (var strategy in _catalog.Strategies)
            {
                if (strategy.Patterns == null || strategy.Patterns.Count == 0) continue;
                var priority = strategy.Priority <= 0 ? 1.0 : strategy.Priority;
                var sourceWeight = ComputeSourceWeight(strategy, fileName, out var bucketWeight);

                foreach (var pat in strategy.Patterns)
                {
                    if (string.IsNullOrWhiteSpace(pat.Field)) continue;
                    var field = MapField(pat.Field);
                    if (string.IsNullOrWhiteSpace(field)) continue;

                    var type = (pat.Type ?? "regex").Trim().ToLowerInvariant();
                    if (type != "regex" && type != "keyword") continue;

                    foreach (var seg in segments)
                    {
                        if (string.IsNullOrWhiteSpace(seg.Text)) continue;
                        if (type == "keyword")
                        {
                            var pattern = pat.Pattern ?? "";
                            var idx = seg.Text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                            if (idx < 0) continue;
                            var rawValue = string.IsNullOrWhiteSpace(pat.Value)
                                ? seg.Text.Substring(idx, Math.Min(pattern.Length, seg.Text.Length - idx))
                                : pat.Value!;
                            var value = NormalizeValue(field, ApplyClean(strategy.Clean, rawValue), ctx);
                            if (!IsValidValue(field, value, strategy.Validate)) continue;
                            var conf = ScoreToConfidence(pat.Weight, priority, sourceWeight, bucketWeight, seg.Weight);
                            var bbox = seg.BBox;
                            if (seg.Words != null && seg.Words.Count > 0 && !string.IsNullOrWhiteSpace(pat.Pattern))
                            {
                                var rx = new Regex(Regex.Escape(pat.Pattern), RegexOptions.IgnoreCase);
                                var m = rx.Match(seg.Text);
                                if (m.Success)
                                    bbox = ComputeMatchBBox(seg.Text, seg.Words, m.Index, m.Length) ?? seg.BBox;
                            }
                            var snippet = TextUtils.SafeSnippet(seg.Text, Math.Max(0, idx - 40), Math.Min(seg.Text.Length - Math.Max(0, idx - 40), 160));
                            Upsert(result, field, BuildField(value, conf, $"strategy_keyword:{pat.Label}", seg, snippet, bbox));
                        }
                        else
                        {
                            var rx = new Regex(pat.Pattern ?? "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                            var matches = rx.Matches(seg.Text);
                            foreach (Match m in matches)
                            {
                                if (!m.Success) continue;
                                if (pat.Compose != null && !string.IsNullOrWhiteSpace(pat.Compose.SecondField) && m.Groups.Count >= 3)
                                {
                                    var field2 = MapField(pat.Compose.SecondField);
                                    if (!string.IsNullOrWhiteSpace(field2))
                                    {
                                        ExtractMatchForField(result, field, m.Groups[1], seg, strategy, pat, priority, sourceWeight, bucketWeight, ctx, 1);
                                        ExtractMatchForField(result, field2, m.Groups[2], seg, strategy, pat, priority, sourceWeight, bucketWeight, ctx, 2);
                                        continue;
                                    }
                                }

                                var group = m.Groups.Count > 1 ? m.Groups[1] : m;
                                ExtractMatchForField(result, field, group, seg, strategy, pat, priority, sourceWeight, bucketWeight, ctx, m.Groups.Count > 1 ? 1 : 0);
                            }
                        }
                    }
                }
            }

            return result;
        }

        private void ExtractMatchForField(Dictionary<string, FieldInfo> result, string field, Group group, SegmentRef seg,
            FieldStrategyDefinition strategy, FieldStrategyPattern pat, double priority, double sourceWeight, double bucketWeight, DespachoContext ctx, int groupIndex)
        {
            if (!group.Success) return;
            var rawValue = group.Value.Trim();
            if (string.IsNullOrWhiteSpace(rawValue)) return;
            if (field.Equals("VALOR_TABELADO_ANEXO_I", StringComparison.OrdinalIgnoreCase))
            {
                var window = ExtractWindow(seg.Text ?? "", group.Index, 120);
                if (!Regex.IsMatch(window, "(anexo\\s*[i1]|tabela\\s*[i1])", RegexOptions.IgnoreCase))
                    return;
            }
            var value = NormalizeValue(field, ApplyClean(strategy.Clean, rawValue), ctx);
            if (!IsValidValue(field, value, strategy.Validate)) return;
            var conf = ScoreToConfidence(pat.Weight, priority, sourceWeight, bucketWeight, seg.Weight);
            var segText = seg.Text ?? "";
            var bbox = ComputeMatchBBox(segText, seg.Words, group.Index, group.Length) ?? seg.BBox;
            var snippet = TextUtils.SafeSnippet(segText, Math.Max(0, group.Index - 40), Math.Min(segText.Length - Math.Max(0, group.Index - 40), 160));
            Upsert(result, field, BuildField(value, conf, $"strategy_regex:{pat.Label}", seg, snippet, bbox));
        }

        private static void Upsert(Dictionary<string, FieldInfo> result, string field, FieldInfo candidate)
        {
            if (!result.TryGetValue(field, out var existing) || candidate.Confidence > existing.Confidence)
                result[field] = candidate;
        }

        private static double ScoreToConfidence(double weight, double priority, double sourceWeight, double bucketWeight, double segmentWeight)
        {
            var score = Math.Max(0.2, weight) * Math.Max(0.7, priority) * Math.Max(0.6, sourceWeight) * Math.Max(0.6, bucketWeight) * Math.Max(0.6, segmentWeight);
            var norm = Math.Min(1.0, score / 1.6);
            return Math.Max(0.45, Math.Min(0.92, 0.45 + 0.45 * norm));
        }

        private static string MapField(string name)
        {
            var key = NormalizeFieldKey(name);
            return FieldMap.TryGetValue(key, out var mapped) ? mapped : "";
        }

        private static string NormalizeFieldKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var t = TextUtils.RemoveDiacritics(name).ToUpperInvariant();
            t = Regex.Replace(t, "[^A-Z0-9]+", "");
            return t;
        }

        private static double ComputeSourceWeight(FieldStrategyDefinition strategy, string fileName, out double bucketWeight)
        {
            bucketWeight = 1.0;
            if (strategy.Sources == null || strategy.Sources.Count == 0) return 1.0;

            double best = 0.7;
            double bestBucket = 0.85;
            foreach (var src in strategy.Sources)
            {
                var matched = false;
                foreach (var pattern in src.NameMatches ?? new List<string>())
                {
                    if (WildcardMatch(fileName, pattern))
                    {
                        matched = true;
                        break;
                    }
                }

                if (matched)
                {
                    best = Math.Max(best, 1.0);
                    bestBucket = Math.Max(bestBucket, BucketWeight(src.Bucket));
                }
            }

            bucketWeight = bestBucket;
            return best;
        }

        private static double BucketWeight(string bucket)
        {
            var b = (bucket ?? "").Trim().ToLowerInvariant();
            return b switch
            {
                "principal" => 1.0,
                "apoio" => 0.85,
                "laudo" => 0.75,
                _ => 0.8
            };
        }

        private static bool WildcardMatch(string text, string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return false;
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(text ?? "", rx, RegexOptions.IgnoreCase);
        }

        private string ApplyClean(List<string> cleans, string value)
        {
            var v = value ?? "";
            if (cleans == null || cleans.Count == 0) return TextUtils.NormalizeWhitespace(v);
            foreach (var c in cleans)
            {
                var key = NormalizeFieldKey(c);
                v = ValidatorRules.ApplyStrategyCleanRule(key, v);
            }
            return v;
        }

        private bool IsValidValue(string field, string value, List<string> validations)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var val = value.Trim();
            if (validations != null)
            {
                foreach (var v in validations)
                {
                    var key = NormalizeFieldKey(v);
                    if (!ValidatorRules.IsStrategyValidationRuleValid(key, val, _moneyRegex))
                        return false;
                }
            }

            if (field.Equals("PROCESSO_JUDICIAL", StringComparison.OrdinalIgnoreCase))
            {
                if (!ValidatorRules.IsValidProcessoJudicial(val, _cnjRegex)) return false;
            }
            if (field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase))
            {
                if (!ValidatorRules.TryNormalizeCpfDigits(val, out _)) return false;
            }
            return true;
        }

        private string NormalizeValue(string field, string value, DespachoContext ctx)
        {
            return ValidatorRules.NormalizeExtractorValue(field, value, _cnjRegex, collapseWeirdSpacing: true);
        }

        private static BBoxN? ComputeMatchBBox(string text, List<WordInfo> words, int matchIndex, int matchLength)
        {
            if (words == null || words.Count == 0) return null;
            if (matchIndex < 0 || matchLength <= 0) return null;

            var end = Math.Min(text.Length, matchIndex + matchLength);
            var slice = text.Substring(matchIndex, end - matchIndex);
            if (string.IsNullOrWhiteSpace(slice)) return null;

            var matchTokens = TextUtils.NormalizeWhitespace(slice).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (matchTokens.Length == 0) return null;

            var tokens = words
                .Select(w => new { Word = w, Token = TextUtils.NormalizeWhitespace(w.Text) })
                .Where(w => !string.IsNullOrWhiteSpace(w.Token))
                .ToList();

            int startIdx = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!tokens[i].Token.Equals(matchTokens[0], StringComparison.OrdinalIgnoreCase)) continue;
                var ok = true;
                for (int j = 1; j < matchTokens.Length && i + j < tokens.Count; j++)
                {
                    if (!tokens[i + j].Token.Equals(matchTokens[j], StringComparison.OrdinalIgnoreCase)) { ok = false; break; }
                }
                if (ok)
                {
                    startIdx = i;
                    break;
                }
            }

            if (startIdx < 0) return null;
            var selected = tokens.Skip(startIdx).Take(matchTokens.Length).Select(t => t.Word);
            return TextUtils.UnionBBox(selected);
        }

        private static FieldInfo BuildField(string value, double confidence, string method, SegmentRef seg, string snippet, BBoxN? bbox)
        {
            return new FieldInfo
            {
                Value = value,
                Confidence = confidence,
                Method = method,
                Evidence = new EvidenceInfo
                {
                    Page1 = seg.Page1,
                    BBoxN = bbox,
                    Snippet = snippet
                }
            };
        }

        private static string ExtractWindow(string text, int index, int size)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var start = Math.Max(0, index - size);
            var end = Math.Min(text.Length, index + size);
            if (end <= start) return "";
            return text.Substring(start, end - start);
        }

        private List<SegmentRef> BuildSegments(DespachoContext ctx)
        {
            var segments = new List<SegmentRef>();

            foreach (var region in ctx.Regions)
            {
                segments.Add(new SegmentRef
                {
                    Page1 = region.Page1,
                    Text = region.Text ?? "",
                    Words = region.Words ?? new List<WordInfo>(),
                    BBox = region.BBox,
                    Weight = region.Name.StartsWith("first_top", StringComparison.OrdinalIgnoreCase) ||
                             region.Name.StartsWith("last_bottom", StringComparison.OrdinalIgnoreCase) ||
                             region.Name.Equals("second_bottom", StringComparison.OrdinalIgnoreCase)
                        ? 1.0
                        : 0.9
                });
            }

            foreach (var band in ctx.BandSegments)
            {
                var weight = 0.85;
                if (band.Band.Equals("header", StringComparison.OrdinalIgnoreCase) || band.Band.Equals("subheader", StringComparison.OrdinalIgnoreCase))
                    weight = 0.9;
                segments.Add(new SegmentRef
                {
                    Page1 = band.Page1,
                    Text = band.Text ?? "",
                    Words = band.Words ?? new List<WordInfo>(),
                    BBox = band.BBox,
                    Weight = weight
                });
            }

            foreach (var p in ctx.Paragraphs)
            {
                segments.Add(new SegmentRef
                {
                    Page1 = p.Page1,
                    Text = p.Text ?? "",
                    Words = p.Words ?? new List<WordInfo>(),
                    BBox = p.BBox,
                    Weight = 0.85
                });
            }

            return segments;
        }

        private class SegmentRef
        {
            public int Page1 { get; set; }
            public string Text { get; set; } = "";
            public List<WordInfo> Words { get; set; } = new List<WordInfo>();
            public BBoxN? BBox { get; set; }
            public double Weight { get; set; } = 0.85;
        }
    }
}
