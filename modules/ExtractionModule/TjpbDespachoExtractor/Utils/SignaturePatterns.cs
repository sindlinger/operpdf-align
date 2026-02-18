using System;
using System.IO;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Obj.Utils;

namespace Obj.TjpbDespachoExtractor.Utils
{
    internal sealed class SignaturePatternCatalog
    {
        private sealed class SignatureDoc
        {
            public SignaturePatterns Patterns { get; set; } = new SignaturePatterns();
        }

        private sealed class SignaturePatterns
        {
            public string AssinanteMain { get; set; } = "";
            public string AssinanteCollapsed { get; set; } = "";
            public string AssinantePje { get; set; } = "";
            public string SignerName { get; set; } = "";
            public string SignerPje { get; set; } = "";
            public string Diretor { get; set; } = "";
            public string DiretorAlt { get; set; } = "";
            public string Cn { get; set; } = "";
            public string DatePt { get; set; } = "";
            public string DateSlash { get; set; } = "";
        }

        private static readonly Lazy<SignaturePatternCatalog> _cached = new Lazy<SignaturePatternCatalog>(Load, true);

        public static SignaturePatternCatalog Current => _cached.Value;

        private static readonly Regex EmptyRegex = new Regex("$a", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public Regex AssinanteMain { get; private set; } = EmptyRegex;
        public Regex AssinanteCollapsed { get; private set; } = EmptyRegex;
        public Regex AssinantePje { get; private set; } = EmptyRegex;
        public Regex SignerName { get; private set; } = EmptyRegex;
        public Regex SignerPje { get; private set; } = EmptyRegex;
        public Regex Diretor { get; private set; } = EmptyRegex;
        public Regex DiretorAlt { get; private set; } = EmptyRegex;
        public Regex Cn { get; private set; } = EmptyRegex;
        public Regex DatePt { get; private set; } = EmptyRegex;
        public Regex DateSlash { get; private set; } = EmptyRegex;

        private static SignaturePatternCatalog Load()
        {
            var catalog = new SignaturePatternCatalog();
            var path = PatternRegistry.FindFile("regex", "signature.yml");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return catalog;

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                var doc = deserializer.Deserialize<SignatureDoc>(File.ReadAllText(path));
                if (doc?.Patterns == null)
                    return catalog;

                catalog.AssinanteMain = BuildRegex(doc.Patterns.AssinanteMain);
                catalog.AssinanteCollapsed = BuildRegex(doc.Patterns.AssinanteCollapsed);
                catalog.AssinantePje = BuildRegex(doc.Patterns.AssinantePje);
                catalog.SignerName = BuildRegex(doc.Patterns.SignerName);
                catalog.SignerPje = BuildRegex(doc.Patterns.SignerPje);
                catalog.Diretor = BuildRegex(doc.Patterns.Diretor);
                catalog.DiretorAlt = BuildRegex(doc.Patterns.DiretorAlt);
                catalog.Cn = BuildRegex(doc.Patterns.Cn);
                catalog.DatePt = BuildRegex(doc.Patterns.DatePt);
                catalog.DateSlash = BuildRegex(doc.Patterns.DateSlash);
            }
            catch
            {
                return catalog;
            }

            return catalog;
        }

        private static Regex BuildRegex(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return EmptyRegex;
            try
            {
                return new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }
            catch
            {
                return EmptyRegex;
            }
        }
    }
}
