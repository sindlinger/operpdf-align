using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Obj.Utils;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.TjpbDespachoExtractor.Utils
{
    internal sealed class ProcessoPartesMarkers
    {
        public List<string> PromoventeStart { get; set; } = new List<string>();
        public List<string> PromoventeCut { get; set; } = new List<string>();
        public List<string> PromovidoStart { get; set; } = new List<string>();

        private sealed class MarkerDoc
        {
            public List<string> PromoventeStart { get; set; } = new List<string>();
            public List<string> PromoventeCut { get; set; } = new List<string>();
            public List<string> PromovidoStart { get; set; } = new List<string>();
        }

        private static readonly Lazy<ProcessoPartesMarkers> Cache = new Lazy<ProcessoPartesMarkers>(LoadInternal, true);

        public static ProcessoPartesMarkers Load() => Cache.Value;

        public static string BuildSpacedPattern(string phrase)
        {
            if (string.IsNullOrWhiteSpace(phrase)) return "";
            var sb = new StringBuilder();
            bool lastSpace = false;
            foreach (var ch in phrase)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!lastSpace)
                    {
                        sb.Append("\\s+");
                        lastSpace = true;
                    }
                    continue;
                }
                lastSpace = false;
                sb.Append(System.Text.RegularExpressions.Regex.Escape(ch.ToString()));
                sb.Append("\\s*");
            }
            var pattern = sb.ToString();
            if (pattern.EndsWith("\\s*", StringComparison.Ordinal))
                pattern = pattern.Substring(0, pattern.Length - 3);
            return pattern;
        }

        private static ProcessoPartesMarkers LoadInternal()
        {
            try
            {
                var path = PatternRegistry.FindFile("markers", "processo_partes.yml");
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return new ProcessoPartesMarkers();

                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var doc = deserializer.Deserialize<MarkerDoc>(File.ReadAllText(path));
                if (doc == null)
                    return new ProcessoPartesMarkers();

                return new ProcessoPartesMarkers
                {
                    PromoventeStart = NormalizeList(doc.PromoventeStart),
                    PromoventeCut = NormalizeList(doc.PromoventeCut),
                    PromovidoStart = NormalizeList(doc.PromovidoStart)
                };
            }
            catch
            {
                return new ProcessoPartesMarkers();
            }
        }

        private static List<string> NormalizeList(List<string> items)
        {
            if (items == null || items.Count == 0)
                return new List<string>();
            var list = items
                .Select(v => (v ?? "").Trim())
                .Where(v => v.Length > 0)
                .ToList();
            return list;
        }
    }
}
