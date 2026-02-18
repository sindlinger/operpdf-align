using System;
using System.Collections.Generic;
using System.Linq;

namespace Obj.ValidatorModule
{
    public static class DocumentDetectionPolicy
    {
        public const string ProfileWeightedDoc = "weighted_doc";
        public const string ProfileWeightedNonDespacho = "weighted_non_despacho";
        public const string ProfileDetectDocCli = "detectdoc_cli";

        private static readonly IReadOnlyDictionary<string, double> WeightedDocWeights =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "BookmarkDetector", 5.0 },
                { "ContentsPrefixDetector", 3.0 },
                { "HeaderLabelDetector", 3.0 },
                { "DespachoContentsDetector", 4.0 },
                { "LargestContentsDetector", 1.0 },
                { "NonDespachoDetector.target", 4.0 },
                { "NonDespachoDetector.generic", 2.0 },
                { "ContentsStreamPicker.marker", 2.0 },
                { "ContentsStreamPicker.largest", 1.0 }
            };

        private static readonly IReadOnlyDictionary<string, double> WeightedNonDespachoWeights =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                { "NonDespachoDetector.target", 8.0 },
                { "DocumentTitleDetector.guard", 6.0 },
                { "DocumentTitleDetector.title", 3.0 },
                { "BookmarkDetector", 2.5 },
                { "ContentsPrefixDetector", 2.0 },
                { "HeaderLabelDetector", 1.5 },
                { "LargestContentsDetector", 0.5 },
                { "ContentsStreamPicker.marker", 1.5 },
                { "ContentsStreamPicker.largest", 0.5 }
            };

        private static readonly IReadOnlyList<string> WeightedDocLegendOrder = new[]
        {
            "BookmarkDetector",
            "ContentsPrefixDetector",
            "HeaderLabelDetector",
            "DespachoContentsDetector",
            "ContentsStreamPicker.marker",
            "ContentsStreamPicker.largest",
            "LargestContentsDetector",
            "NonDespachoDetector.target",
            "NonDespachoDetector.generic"
        };

        private static readonly IReadOnlyList<string> WeightedNonDespachoLegendOrder = new[]
        {
            "NonDespachoDetector.target",
            "DocumentTitleDetector.guard",
            "DocumentTitleDetector.title",
            "BookmarkDetector",
            "ContentsPrefixDetector",
            "HeaderLabelDetector",
            "ContentsStreamPicker.marker",
            "ContentsStreamPicker.largest",
            "LargestContentsDetector"
        };

        private static readonly IReadOnlyDictionary<string, string> DetectorColorTags =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "bookmark", "green" },
                { "contentsprefix", "cyan" },
                { "header", "blue" },
                { "despachocontents", "magenta" },
                { "contentsstreampicker", "yellow" },
                { "largestcontents", "blue" },
                { "nondespacho", "red" },
                { "documenttitle", "magenta" }
            };

        public static IReadOnlyDictionary<string, double> GetWeights(string? profile)
        {
            var normalized = NormalizeProfile(profile);
            if (string.Equals(normalized, ProfileWeightedNonDespacho, StringComparison.OrdinalIgnoreCase))
                return WeightedNonDespachoWeights;
            return WeightedDocWeights;
        }

        public static double ResolveWeight(string? detector, string? profile = null)
        {
            if (string.IsNullOrWhiteSpace(detector))
                return 0.0;

            var weights = GetWeights(profile);
            if (weights.TryGetValue(detector.Trim(), out var direct))
                return direct;

            var key = detector.Trim();
            foreach (var kv in weights)
            {
                if (key.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kv.Value;
            }

            return 0.0;
        }

        public static double ResolveMaxScore(string? profile)
        {
            return GetWeights(profile).Values.Sum();
        }

        public static IReadOnlyList<string> GetLegendDetectors(string? profile)
        {
            var normalized = NormalizeProfile(profile);
            if (string.Equals(normalized, ProfileWeightedNonDespacho, StringComparison.OrdinalIgnoreCase))
                return WeightedNonDespachoLegendOrder;
            return WeightedDocLegendOrder;
        }

        public static string ResolveDetectorColorTag(string? detector)
        {
            if (string.IsNullOrWhiteSpace(detector))
                return "";

            var key = detector.Trim();
            foreach (var kv in DetectorColorTags)
            {
                if (key.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kv.Value;
            }

            return "";
        }

        public static bool MatchesKeywordRule(
            string? normalizedText,
            IEnumerable<string>? keywordsAny,
            IEnumerable<string>? keywordsAll)
        {
            var text = DocumentValidationRules.NormalizeDocText(normalizedText);
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var all = NormalizeKeywords(keywordsAll);
            if (all.Count > 0 && !all.All(k => text.Contains(k, StringComparison.Ordinal)))
                return false;

            var any = NormalizeKeywords(keywordsAny);
            if (any.Count > 0 && !any.Any(k => text.Contains(k, StringComparison.Ordinal)))
                return false;

            return all.Count > 0 || any.Count > 0;
        }

        public static int ScoreKeywordRule(
            string? normalizedText,
            IEnumerable<string>? keywordsAny,
            IEnumerable<string>? keywordsAll)
        {
            var text = DocumentValidationRules.NormalizeDocText(normalizedText);
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            var all = NormalizeKeywords(keywordsAll);
            if (all.Count > 0 && !all.All(k => text.Contains(k, StringComparison.Ordinal)))
                return 0;

            var any = NormalizeKeywords(keywordsAny);
            if (any.Count > 0 && !any.Any(k => text.Contains(k, StringComparison.Ordinal)))
                return 0;

            var score = 0;
            score += all.Count;
            score += any.Count(k => text.Contains(k, StringComparison.Ordinal));
            return score;
        }

        public static string NormalizeDocTypeKey(string? raw)
        {
            var resolved = DocumentValidationRules.ResolveDocKeyFromHint(raw);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
            return raw?.Trim() ?? "";
        }

        private static string NormalizeProfile(string? profile)
        {
            if (string.IsNullOrWhiteSpace(profile))
                return ProfileWeightedDoc;
            var p = profile.Trim();
            if (string.Equals(p, ProfileDetectDocCli, StringComparison.OrdinalIgnoreCase))
                return ProfileWeightedDoc;
            return p;
        }

        private static List<string> NormalizeKeywords(IEnumerable<string>? values)
        {
            if (values == null)
                return new List<string>();

            return values
                .Select(DocumentValidationRules.NormalizeDocText)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
