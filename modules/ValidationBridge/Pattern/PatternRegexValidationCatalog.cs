using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Obj.ValidatorModule;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.Commands
{
    internal static partial class ObjectsPattern
    {
        private sealed class MarkerDoc
        {
            public List<string> PromoventeStart { get; set; } = new();
            public List<string> PromoventeCut { get; set; } = new();
            public List<string> PromovidoStart { get; set; } = new();
        }

        private sealed class MarkerLists
        {
            public string[] PromoventeStart { get; set; } = Array.Empty<string>();
            public string[] PromoventeCut { get; set; } = Array.Empty<string>();
            public string[] PromovidoStart { get; set; } = Array.Empty<string>();
        }

        private static readonly Lazy<MarkerLists> _markers = new Lazy<MarkerLists>(LoadMarkers, true);

        private static string[] PromoventeStartMarkers => _markers.Value.PromoventeStart;
        private static string[] PromoventeCutMarkers => _markers.Value.PromoventeCut;
        private static string[] PromovidoStartMarkers => _markers.Value.PromovidoStart;

        private static MarkerLists LoadMarkers()
        {
            var file = Obj.Utils.PatternRegistry.FindFile("markers", "processo_partes.yml");
            if (string.IsNullOrWhiteSpace(file))
                return new MarkerLists();

            try
            {
                var deserializer = new DeserializerBuilder()
                    .IgnoreUnmatchedProperties()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .Build();
                var doc = deserializer.Deserialize<MarkerDoc>(File.ReadAllText(file));
                if (doc == null)
                    return new MarkerLists();

                return new MarkerLists
                {
                    PromoventeStart = NormalizeMarkers(doc.PromoventeStart),
                    PromoventeCut = NormalizeMarkers(doc.PromoventeCut),
                    PromovidoStart = NormalizeMarkers(doc.PromovidoStart)
                };
            }
            catch
            {
                return new MarkerLists();
            }
        }

        private static string[] NormalizeMarkers(List<string> items)
        {
            if (items == null || items.Count == 0)
                return Array.Empty<string>();
            var list = new List<string>();
            foreach (var item in items)
            {
                var v = (item ?? "").Trim();
                if (v.Length == 0)
                    continue;
                list.Add(v.ToLowerInvariant());
            }
            return list.ToArray();
        }

        private sealed class RegexRule
        {
            public string Pattern { get; set; } = "";
            public int Group { get; set; }
            public string Source { get; set; } = "";
            public string? Band { get; set; }
            public Regex? Compiled { get; set; }
        }

        private sealed class TemplateRegexDoc
        {
            public string? Doc { get; set; }
            public Dictionary<string, TemplateRegexField> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class TemplateRegexField
        {
            public string? Band { get; set; }
            public List<TemplateRegexItem> Regex { get; set; } = new();
        }

        private sealed class TemplateRegexItem
        {
            public string Pattern { get; set; } = "";
            public int Group { get; set; }
        }

        private sealed class ExtractRegexDoc
        {
            public string? Doc { get; set; }
            public Dictionary<string, ExtractRegexField> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class ExtractRegexField
        {
            public List<ExtractRegexCandidate> Candidates { get; set; } = new();
        }

        private sealed class ExtractRegexCandidate
        {
            public string? MatchRegex { get; set; }
            public int MatchGroup { get; set; }
        }

        private static Dictionary<string, List<RegexRule>> LoadRegexCatalog(string docName)
        {
            var catalog = new Dictionary<string, List<RegexRule>>(StringComparer.OrdinalIgnoreCase);
            AddTemplateRegexRules(docName, catalog);
            AddExtractRegexRules(docName, catalog);
            foreach (var list in catalog.Values)
            {
                foreach (var rule in list)
                {
                    try
                    {
                        rule.Compiled = new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
                    }
                    catch
                    {
                        rule.Compiled = null;
                    }
                }
            }
            return catalog;
        }

        private static void AddRule(Dictionary<string, List<RegexRule>> catalog, string field, RegexRule rule)
        {
            if (!catalog.TryGetValue(field, out var list))
            {
                list = new List<RegexRule>();
                catalog[field] = list;
            }
            list.Add(rule);
        }

        private static void AddTemplateRegexRules(string docName, Dictionary<string, List<RegexRule>> catalog)
        {
            var file = ResolveTemplateRegexFile(docName);
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                return;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .IgnoreUnmatchedProperties()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .Build();
                var doc = deserializer.Deserialize<TemplateRegexDoc>(File.ReadAllText(file));
                foreach (var kv in doc.Fields)
                {
                    var field = kv.Key;
                    var fieldInfo = kv.Value;
                    var band = NormalizeBand(fieldInfo.Band);
                    foreach (var item in fieldInfo.Regex)
                    {
                        if (string.IsNullOrWhiteSpace(item.Pattern))
                            continue;
                        AddRule(catalog, field, new RegexRule
                        {
                            Pattern = item.Pattern,
                            Group = item.Group,
                            Source = $"template_fields:{Path.GetFileName(file)}",
                            Band = band
                        });
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        private static void AddExtractRegexRules(string docName, Dictionary<string, List<RegexRule>> catalog)
        {
            var dir = Obj.Utils.PatternRegistry.FindDir("extract_fields");
            if (string.IsNullOrWhiteSpace(dir))
                return;

            foreach (var file in Directory.GetFiles(dir, "*.yml"))
            {
                try
                {
                    var deserializer = new DeserializerBuilder()
                        .IgnoreUnmatchedProperties()
                        .WithNamingConvention(CamelCaseNamingConvention.Instance)
                        .Build();
                    var doc = deserializer.Deserialize<ExtractRegexDoc>(File.ReadAllText(file));
                    if (!string.IsNullOrWhiteSpace(docName) && !string.Equals(doc.Doc ?? "", docName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (var kv in doc.Fields)
                    {
                        var field = kv.Key;
                        foreach (var cand in kv.Value.Candidates)
                        {
                            if (string.IsNullOrWhiteSpace(cand.MatchRegex))
                                continue;
                            AddRule(catalog, field, new RegexRule
                            {
                                Pattern = cand.MatchRegex!,
                                Group = cand.MatchGroup,
                                Source = $"extract_fields:{Path.GetFileName(file)}"
                            });
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        private static string ResolveTemplateRegexFile(string docName)
        {
            if (string.IsNullOrWhiteSpace(docName))
                return "";
            var baseDir = Obj.Utils.PatternRegistry.ResolvePath("template_fields");
            var file = Path.Combine(baseDir, docName + ".yml");
            if (File.Exists(file))
                return file;
            // fallbacks
            var localDir = baseDir;
            file = Path.Combine(localDir, docName + ".yml");
            if (File.Exists(file))
                return file;
            var docKey = DocumentValidationRules.ResolveDocKeyFromHint(docName);
            if (DocumentValidationRules.IsDocMatch(docKey, DocumentValidationRules.DocKeyCertidaoConselho))
            {
                var alt = Path.Combine(baseDir, "tjpb_certidao.yml");
                if (File.Exists(alt))
                    return alt;
                alt = Path.Combine(localDir, "tjpb_certidao.yml");
                if (File.Exists(alt))
                    return alt;
            }
            return "";
        }

        private static string NormalizeBand(string? band)
        {
            if (string.IsNullOrWhiteSpace(band))
                return "";
            var b = band.ToLowerInvariant();
            if (b.Contains("front"))
                return "front";
            if (b.Contains("back"))
                return "back";
            return band;
        }
    }
}
