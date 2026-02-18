using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Obj.ValidatorModule;

namespace Obj.Utils
{
    /// <summary>
    /// Normaliza títulos de bookmarks em macro/subcategoria usando
    /// o dicionário data/outputs/bookmark_labels_final_enriched.csv
    /// com fallback heurístico simples por palavras‑chave.
    /// </summary>
    public class BookmarkClassifier
    {
        private readonly Dictionary<string, BookmarkLabel> _map;
        private readonly CultureInfo _culture = new("pt-BR");

        public class BookmarkLabel
        {
            public string Canon { get; set; } = "";
            public string Macro { get; set; } = "outros";
            public string Subcat { get; set; } = "outros";
        }

        public BookmarkClassifier(string? csvPath = null)
        {
            csvPath ??= Path.Combine("data", "outputs", "bookmark_labels_final_enriched.csv");
            _map = LoadCsv(csvPath);
        }

        public BookmarkLabel Classify(string title)
        {
            var norm = Normalize(title);
            if (_map.TryGetValue(norm, out var lbl))
                return lbl;

            // Heurística rápida para títulos novos
            return Heuristic(norm);
        }

        private Dictionary<string, BookmarkLabel> LoadCsv(string path)
        {
            var dict = new Dictionary<string, BookmarkLabel>();
            if (!File.Exists(path))
                return dict; // permanece vazio, só heurística

            foreach (var line in File.ReadAllLines(path).Skip(1)) // skip header
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = SplitCsv(line);
                if (parts.Length < 4) continue;
                var orig = Normalize(parts[0]);
                var canon = string.IsNullOrWhiteSpace(parts[1]) ? parts[0].Trim() : parts[1].Trim();
                var macro = string.IsNullOrWhiteSpace(parts[2]) ? "outros" : parts[2].Trim();
                var sub = string.IsNullOrWhiteSpace(parts[3]) ? "outros" : parts[3].Trim();
                if (!dict.ContainsKey(orig))
                {
                    dict[orig] = new BookmarkLabel { Canon = canon, Macro = macro, Subcat = sub };
                }
            }
            return dict;
        }

        private BookmarkLabel Heuristic(string norm)
        {
            // Busca palavras‑chave simples, ordem por especificidade
            if (Contains(norm, new[] { "laudo", "pericia" }))
                return New("Laudo", "anexo", "laudo");
            if (Contains(norm, new[] { "senten" }))
                return New("Sentenca", "anexo", "sentenca");
            if (Contains(norm, new[] { "capa" }))
                return New("Capa", "anexo", "capa");
            if (Contains(norm, new[] { "sighope", "perito" }))
                return New("SIGHOPE", "anexo", "sighope");
            if (DocumentValidationRules.IsRequerimento(norm))
                return New("Requerimento", "requerimento", "outros");
            if (DocumentValidationRules.IsLikelyOficio(norm))
                return New("Oficio", "oficio", "outros");
            if (DocumentValidationRules.IsCertidaoConselho(norm))
                return New("Certidao", "certidao", "outros");
            if (DocumentValidationRules.IsDespacho(norm))
                return New("Despacho", "despacho", "outros");
            if (Contains(norm, new[] { "pagamento", "honor", "reserva", "rpv" }))
                return New("Pagamento/Honorarios", "anexo", "pagamento");
            return New(ToTitle(norm), "outros", "outros");
        }

        private static BookmarkLabel New(string canon, string macro, string sub)
            => new BookmarkLabel { Canon = canon, Macro = macro, Subcat = sub };

        private static bool Contains(string text, IEnumerable<string> needles)
            => needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

        private string Normalize(string s)
        {
            s ??= "";
            var trimmed = s.Trim();
            return _culture.TextInfo.ToLower(trimmed);
        }

        private string ToTitle(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Sem título";
            var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Select(w => _culture.TextInfo.ToTitleCase(w.ToLower()))
                         .ToArray();
            return string.Join(" ", words);
        }

        private static string[] SplitCsv(string line)
        {
            // CSV simples sem aspas aninhadas
            return line.Split(',');
        }
    }
}
