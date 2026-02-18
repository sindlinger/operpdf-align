using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Obj.Utils;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.TjpbDespachoExtractor.Utils
{
    internal static class FieldRegexCatalog
    {
        private const string DocName = "tjpb_despacho";

        private sealed class ExtractFieldRegexDoc
        {
            public string? Doc { get; set; }
            public Dictionary<string, List<string>> RegexCatalog { get; set; } =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Lazy<Dictionary<string, List<Regex>>> Cache =
            new Lazy<Dictionary<string, List<Regex>>>(Load, true);

        public static IReadOnlyList<Regex> Get(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return Array.Empty<Regex>();
            var map = Cache.Value;
            return map.TryGetValue(key, out var list) ? list : Array.Empty<Regex>();
        }

        public static Regex? GetFirst(string key)
        {
            var list = Get(key);
            if (list.Count == 0)
                return null;
            return list[0];
        }

        private static Dictionary<string, List<Regex>> Load()
        {
            var catalog = new Dictionary<string, List<Regex>>(StringComparer.OrdinalIgnoreCase);
            LoadFromExtractFields(catalog);

            return catalog;
        }

        private static void LoadFromExtractFields(Dictionary<string, List<Regex>> catalog)
        {
            var dir = PatternRegistry.FindDir("extract_fields");
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            foreach (var file in Directory.GetFiles(dir, "*.yml"))
            {
                try
                {
                    var doc = deserializer.Deserialize<ExtractFieldRegexDoc>(File.ReadAllText(file));
                    if (doc == null)
                        continue;
                    if (!string.IsNullOrWhiteSpace(doc.Doc) &&
                        !string.Equals(doc.Doc, DocName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (doc.RegexCatalog == null || doc.RegexCatalog.Count == 0)
                        continue;
                    foreach (var kv in doc.RegexCatalog)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null || kv.Value.Count == 0)
                            continue;
                        if (!catalog.TryGetValue(kv.Key, out var list))
                        {
                            list = new List<Regex>();
                            catalog[kv.Key] = list;
                        }
                        foreach (var pattern in kv.Value)
                        {
                            if (string.IsNullOrWhiteSpace(pattern))
                                continue;
                            try
                            {
                                list.Add(new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline));
                            }
                            catch
                            {
                                // ignore invalid regex
                            }
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
