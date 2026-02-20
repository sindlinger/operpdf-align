using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Utils;
using Obj.Utils;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.Align
{
    internal static partial class ObjectsTextOpsDiff
    {
        private sealed class AlignHelperPhrase
        {
            public string Key { get; set; } = "";
            public string Phrase { get; set; } = "";
            public string Normalized { get; set; } = "";
            public string Compact { get; set; } = "";
            public double Weight { get; set; }
            public bool RequirePrefix { get; set; }
        }

        private sealed class AlignHelperLexicon
        {
            public List<AlignHelperPhrase> Phrases { get; } = new List<AlignHelperPhrase>();
        }

        private sealed class AlignHelperHit
        {
            public int Index { get; set; }
            public string Key { get; set; } = "";
            public double Score { get; set; }
        }

        private static readonly Lazy<AlignHelperLexicon> AlignHelperLexiconCache =
            new Lazy<AlignHelperLexicon>(LoadAlignHelperLexicon, true);

        private static string DetectAlignHelperKey(string normalizedBlockText)
        {
            var lexicon = AlignHelperLexiconCache.Value;
            if (lexicon.Phrases.Count == 0)
                return "";

            var text = NormalizeAnchorSearchText(normalizedBlockText);
            if (text.Length == 0)
                return "";

            var compact = text.Replace(" ", "", StringComparison.Ordinal);
            string bestKey = "";
            double bestScore = 0.0;

            foreach (var phrase in lexicon.Phrases)
            {
                if (phrase.Normalized.Length == 0)
                    continue;

                var matchPos = text.IndexOf(phrase.Normalized, StringComparison.Ordinal);
                bool matched;
                if (phrase.RequirePrefix)
                {
                    matched = matchPos >= 0 && matchPos <= 24;
                    if (!matched && phrase.Compact.Length >= 4)
                        matched = compact.StartsWith(phrase.Compact, StringComparison.Ordinal);
                }
                else
                {
                    matched = matchPos >= 0;
                    if (!matched && phrase.Compact.Length >= 4)
                        matched = compact.Contains(phrase.Compact, StringComparison.Ordinal);
                }
                if (!matched)
                    continue;

                var positionBonus = matchPos >= 0 && matchPos <= 36 ? 0.12 : 0.0;
                var score = Math.Min(0.99, phrase.Weight + positionBonus);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestKey = phrase.Key;
                }
            }

            return bestScore >= 0.86 ? bestKey : "";
        }

        private static List<AnchorPair> BuildAnchorPairsAlignHelper(List<string> normA, List<string> normB, double minLenRatio)
        {
            var lexicon = AlignHelperLexiconCache.Value;
            if (lexicon.Phrases.Count == 0 || normA.Count == 0 || normB.Count == 0)
                return new List<AnchorPair>();

            var hitsA = DetectAlignHelperHits(normA, lexicon);
            var hitsB = DetectAlignHelperHits(normB, lexicon);
            if (hitsA.Count == 0 || hitsB.Count == 0)
                return new List<AnchorPair>();

            var byKeyB = hitsB
                .GroupBy(h => h.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(v => v.Index).ToList(), StringComparer.Ordinal);

            var candidates = new List<AnchorPair>();
            foreach (var hitA in hitsA.OrderBy(h => h.Index))
            {
                if (!byKeyB.TryGetValue(hitA.Key, out var hitsForKey))
                    continue;

                foreach (var hitB in hitsForKey)
                {
                    var sim = ComputeAlignmentSimilarity(normA[hitA.Index], normB[hitB.Index]);
                    var lenRatio = ComputeLenRatio(normA[hitA.Index], normB[hitB.Index]);
                    if (minLenRatio > 0 && lenRatio < minLenRatio * 0.50)
                        continue;
                    if (sim < 0.08)
                        continue;

                    var posA = normA.Count <= 1 ? 0.0 : hitA.Index / (double)(normA.Count - 1);
                    var posB = normB.Count <= 1 ? 0.0 : hitB.Index / (double)(normB.Count - 1);
                    var posDelta = Math.Abs(posA - posB);
                    if (posDelta > 0.35)
                        continue;

                    var helperScore = Math.Min(hitA.Score, hitB.Score);
                    var score = Math.Max(sim, helperScore) - (posDelta * 0.20);
                    if (score < 0.10)
                        continue;
                    candidates.Add(new AnchorPair
                    {
                        AIndex = hitA.Index,
                        BIndex = hitB.Index,
                        Score = Math.Min(1.0, score + 0.08)
                    });
                }
            }

            return SelectBestMonotonicAnchors(candidates);
        }

        private static List<AnchorPair> MergeAnchorPairsWithHelper(List<AnchorPair> anchors, List<AnchorPair> helperAnchors)
        {
            if ((anchors == null || anchors.Count == 0) && (helperAnchors == null || helperAnchors.Count == 0))
                return new List<AnchorPair>();
            if (anchors == null || anchors.Count == 0)
                return helperAnchors.OrderBy(a => a.AIndex).ThenBy(a => a.BIndex).ToList();
            if (helperAnchors == null || helperAnchors.Count == 0)
                return anchors.OrderBy(a => a.AIndex).ThenBy(a => a.BIndex).ToList();

            var merged = anchors
                .Concat(helperAnchors)
                .GroupBy(a => (a.AIndex, a.BIndex))
                .Select(g => new AnchorPair
                {
                    AIndex = g.Key.AIndex,
                    BIndex = g.Key.BIndex,
                    Score = g.Max(v => v.Score)
                })
                .OrderBy(v => v.AIndex)
                .ThenBy(v => v.BIndex)
                .ToList();

            return SelectBestMonotonicAnchors(merged);
        }

        private static List<AnchorPair> SelectBestMonotonicAnchors(List<AnchorPair> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return new List<AnchorPair>();

            var ordered = candidates
                .Where(v => v.AIndex >= 0 && v.BIndex >= 0)
                .OrderBy(v => v.AIndex)
                .ThenBy(v => v.BIndex)
                .ThenByDescending(v => v.Score)
                .ToList();
            if (ordered.Count == 0)
                return new List<AnchorPair>();

            var dp = new double[ordered.Count];
            var prev = new int[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                dp[i] = ordered[i].Score;
                prev[i] = -1;
                for (int j = 0; j < i; j++)
                {
                    if (ordered[j].AIndex < ordered[i].AIndex &&
                        ordered[j].BIndex < ordered[i].BIndex)
                    {
                        var cand = dp[j] + ordered[i].Score;
                        if (cand > dp[i])
                        {
                            dp[i] = cand;
                            prev[i] = j;
                        }
                    }
                }
            }

            int best = 0;
            for (int i = 1; i < dp.Length; i++)
            {
                if (dp[i] > dp[best])
                    best = i;
            }

            var stack = new Stack<AnchorPair>();
            int cur = best;
            while (cur >= 0)
            {
                stack.Push(ordered[cur]);
                cur = prev[cur];
            }

            return stack.ToList();
        }

        private static List<AlignHelperHit> DetectAlignHelperHits(List<string> normalizedBlocks, AlignHelperLexicon lexicon)
        {
            var hits = new List<AlignHelperHit>();
            if (normalizedBlocks == null || normalizedBlocks.Count == 0 || lexicon.Phrases.Count == 0)
                return hits;

            for (int i = 0; i < normalizedBlocks.Count; i++)
            {
                var text = NormalizeAnchorSearchText(normalizedBlocks[i]);
                if (text.Length == 0)
                    continue;

                var compact = text.Replace(" ", "", StringComparison.Ordinal);
                string bestKey = "";
                double bestScore = 0.0;

                foreach (var phrase in lexicon.Phrases)
                {
                    if (phrase.Normalized.Length == 0)
                        continue;

                    var matchPos = text.IndexOf(phrase.Normalized, StringComparison.Ordinal);
                    bool matched;
                    if (phrase.RequirePrefix)
                    {
                        matched = matchPos >= 0 && matchPos <= 24;
                        if (!matched && phrase.Compact.Length >= 4)
                            matched = compact.StartsWith(phrase.Compact, StringComparison.Ordinal);
                    }
                    else
                    {
                        matched = matchPos >= 0;
                        if (!matched && phrase.Compact.Length >= 4)
                            matched = compact.Contains(phrase.Compact, StringComparison.Ordinal);
                    }
                    if (!matched)
                        continue;

                    // Prioriza marcador em posição de cabeçalho/label.
                    var positionBonus = matchPos >= 0 && matchPos <= 36 ? 0.12 : 0.0;
                    var score = Math.Min(0.99, phrase.Weight + positionBonus);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestKey = phrase.Key;
                    }
                }

                if (bestKey.Length > 0)
                {
                    hits.Add(new AlignHelperHit
                    {
                        Index = i,
                        Key = bestKey,
                        Score = bestScore
                    });
                }
            }

            return hits;
        }

        private static AlignHelperLexicon LoadAlignHelperLexicon()
        {
            var lexicon = new AlignHelperLexicon();

            try
            {
                var partes = ProcessoPartesMarkers.Load();
                AddHelperPhrases(lexicon, "promovente", partes.PromoventeStart, 0.90, requirePrefix: false);
                AddHelperPhrases(lexicon, "promovente", partes.PromoventeCut, 0.86, requirePrefix: false);
                AddHelperPhrases(lexicon, "promovido", partes.PromovidoStart, 0.90, requirePrefix: false);
            }
            catch
            {
                // Keep lexicon with whatever could be loaded.
            }

            try
            {
                var rulesPath = PatternRegistry.FindFile("markers", "field_rules.yml");
                if (!string.IsNullOrWhiteSpace(rulesPath) && File.Exists(rulesPath))
                {
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .IgnoreUnmatchedProperties()
                        .Build();

                    var rules = deserializer.Deserialize<FieldRulesConfig>(File.ReadAllText(rulesPath));
                    if (rules != null)
                    {
                        AddRulePhrases(lexicon, "processo_administrativo", rules.ProcessoAdministrativo);
                        AddRulePhrases(lexicon, "processo_judicial", rules.ProcessoJudicial);
                        AddRulePhrases(lexicon, "vara", rules.Vara);
                        AddRulePhrases(lexicon, "comarca", rules.Comarca);
                        AddRulePhrases(lexicon, "promovente", rules.Promovente);
                        AddRulePhrases(lexicon, "promovido", rules.Promovido);
                        AddRulePhrases(lexicon, "perito", rules.Perito);
                        AddRulePhrases(lexicon, "cpf_perito", rules.CpfPerito);
                        AddRulePhrases(lexicon, "especialidade", rules.Especialidade);
                        AddRulePhrases(lexicon, "especie_pericia", rules.EspeciePericia);
                        AddRulePhrases(lexicon, "valor_jz", rules.ValorJz);
                        AddRulePhrases(lexicon, "valor_de", rules.ValorDe);
                        AddRulePhrases(lexicon, "valor_cm", rules.ValorCm);
                        AddRulePhrases(lexicon, "adiantamento", rules.Adiantamento);
                        AddRulePhrases(lexicon, "percentual", rules.Percentual);
                        AddRulePhrases(lexicon, "parcela", rules.Parcela);
                        AddRulePhrases(lexicon, "data", rules.Data);
                    }
                }
            }
            catch
            {
                // Keep lexicon with whatever could be loaded.
            }

            return lexicon;
        }

        private static void AddRulePhrases(AlignHelperLexicon lexicon, string key, FieldRuleConfig? rule)
        {
            if (rule == null)
                return;
            AddHelperPhrases(lexicon, key, ExpandTemplateAnchors(rule.Templates), 0.88, requirePrefix: true);
        }

        private static IEnumerable<string> ExpandTemplateAnchors(IEnumerable<string>? templates)
        {
            if (templates == null)
                yield break;

            foreach (var rawTemplate in templates)
            {
                var template = (rawTemplate ?? "").Trim();
                if (template.Length == 0)
                    continue;

                if (template.IndexOf("{{value}}", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var parts = template
                        .Split("{{value}}", StringSplitOptions.None)
                        .Select(v => (v ?? "").Trim())
                        .Where(v => v.Length >= 3);

                    foreach (var part in parts)
                        yield return part;

                    continue;
                }

                yield return template;
            }
        }

        private static void AddHelperPhrases(AlignHelperLexicon lexicon, string key, IEnumerable<string>? phrases, double baseWeight, bool requirePrefix)
        {
            if (lexicon == null || string.IsNullOrWhiteSpace(key) || phrases == null)
                return;

            foreach (var raw in phrases)
            {
                var phrase = (raw ?? "").Trim();
                if (phrase.Length < 3)
                    continue;

                var normalized = NormalizeAnchorSearchText(phrase);
                if (normalized.Length < 3)
                    continue;

                var tokenCount = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (requirePrefix && tokenCount < 2)
                    continue;

                var compact = normalized.Replace(" ", "", StringComparison.Ordinal);
                if (compact.Length < 3)
                    continue;

                if (lexicon.Phrases.Any(p =>
                    string.Equals(p.Key, key, StringComparison.Ordinal) &&
                    string.Equals(p.Normalized, normalized, StringComparison.Ordinal)))
                    continue;

                var weightBoost = Math.Min(0.08, normalized.Length / 120.0);
                lexicon.Phrases.Add(new AlignHelperPhrase
                {
                    Key = key,
                    Phrase = phrase,
                    Normalized = normalized,
                    Compact = compact,
                    Weight = Math.Min(0.98, baseWeight + weightBoost),
                    RequirePrefix = requirePrefix
                });
            }
        }

        private static string NormalizeAnchorSearchText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var lower = RemoveDiacritics(text).ToLowerInvariant();
            var sb = new StringBuilder(lower.Length);
            foreach (var ch in lower)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(ch);
                else
                    sb.Append(' ');
            }

            return CollapseSpaces(sb.ToString());
        }
    }
}
