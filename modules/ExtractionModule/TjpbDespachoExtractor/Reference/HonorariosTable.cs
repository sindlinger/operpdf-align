using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Utils;

namespace Obj.TjpbDespachoExtractor.Reference
{
    public class HonorariosEntry
    {
        public string Area { get; set; } = "";
        public string Descricao { get; set; } = "";
        public string Id { get; set; } = "";
        public decimal Valor { get; set; }
    }

    public class HonorariosTable
    {
        private readonly List<HonorariosEntry> _entries = new List<HonorariosEntry>();
        private readonly List<HonorariosAlias> _aliases = new List<HonorariosAlias>();
        private readonly HonorariosConfig _cfg;

        public HonorariosTable(HonorariosConfig cfg, string baseDir)
        {
            _cfg = cfg ?? new HonorariosConfig();
            if (!string.IsNullOrWhiteSpace(_cfg.TablePath))
            {
                var path = ResolvePath(baseDir, _cfg.TablePath!);
                if (File.Exists(path))
                    LoadTable(path);
            }
            if (!string.IsNullOrWhiteSpace(_cfg.AliasesPath))
            {
                var path = ResolvePath(baseDir, _cfg.AliasesPath!);
                if (File.Exists(path))
                    LoadAliases(path);
            }
        }

        public bool TryMatch(string especialidade, decimal valor, out HonorariosEntry entry, out double confidence)
        {
            entry = new HonorariosEntry();
            confidence = 0;
            if (string.IsNullOrWhiteSpace(especialidade) || _entries.Count == 0) return false;

            var aliasEntry = MatchAlias(especialidade);
            List<HonorariosEntry> candidates;
            var aliasUsed = false;

            if (aliasEntry != null)
            {
                candidates = new List<HonorariosEntry> { aliasEntry };
                aliasUsed = true;
            }
            else
            {
                var area = MapArea(especialidade);
                if (string.IsNullOrWhiteSpace(area)) return false;

                candidates = _entries.Where(e => e.Area.Equals(area, StringComparison.OrdinalIgnoreCase)).ToList();
                if (candidates.Count == 0) return false;
            }

            HonorariosEntry? best = null;
            decimal bestDiff = decimal.MaxValue;
            foreach (var e in candidates)
            {
                var diff = Math.Abs(e.Valor - valor);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = e;
                }
            }

            if (best == null) return false;

            var pct = valor == 0 ? 1m : bestDiff / valor;
            var tol = (decimal)(_cfg.ValueTolerancePct <= 0 ? 0.15 : _cfg.ValueTolerancePct);
            if (pct > tol) return false;

            entry = best;
            confidence = aliasUsed ? 0.9 : 0.75;
            return true;
        }

        public bool TryMatchDetailed(
            string especialidade,
            decimal valor,
            out HonorariosEntry entry,
            out double confidence,
            out decimal diffPct,
            out string source)
        {
            entry = new HonorariosEntry();
            confidence = 0;
            diffPct = 0;
            source = "";
            if (string.IsNullOrWhiteSpace(especialidade) || _entries.Count == 0) return false;

            var aliasEntry = MatchAlias(especialidade);
            if (aliasEntry != null)
            {
                if (TryMatchCandidates(new[] { aliasEntry }, valor, out entry, out diffPct))
                {
                    confidence = 0.9;
                    source = "alias";
                    return true;
                }
            }

            var area = MapArea(especialidade);
            if (!string.IsNullOrWhiteSpace(area))
            {
                var candidates = _entries.Where(e => e.Area.Equals(area, StringComparison.OrdinalIgnoreCase));
                if (TryMatchCandidates(candidates, valor, out entry, out diffPct))
                {
                    confidence = 0.75;
                    source = "area";
                    return true;
                }
            }

            if (TryMatchCandidates(_entries, valor, out entry, out diffPct))
            {
                confidence = 0.6;
                source = "all";
                return true;
            }

            return false;
        }

        public bool TryGetById(string rawId, out HonorariosEntry entry)
        {
            entry = new HonorariosEntry();
            if (string.IsNullOrWhiteSpace(rawId)) return false;
            var id = rawId.Trim();
            var found = _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (found == null) return false;
            entry = found;
            return true;
        }

        public bool TryResolveAreaFromText(string text, out string area, out string source)
        {
            area = "";
            source = "";
            if (string.IsNullOrWhiteSpace(text) || _entries.Count == 0) return false;

            var aliasEntry = MatchAlias(text);
            if (aliasEntry != null && !string.IsNullOrWhiteSpace(aliasEntry.Area))
            {
                area = aliasEntry.Area;
                source = "alias";
                return true;
            }

            var mapped = MapArea(text);
            if (!string.IsNullOrWhiteSpace(mapped))
            {
                area = mapped;
                source = "area_map";
                return true;
            }

            return false;
        }

        // Normaliza especialidade/profissao a partir de aliases e area map.
        // - alias -> retorna descricao da entrada (id) e area
        // - area  -> retorna area como "especialidade" normalizada
        public bool TryNormalizeEspecialidade(string text, out string normalized, out string source, out string area, out string entryId)
        {
            normalized = "";
            source = "";
            area = "";
            entryId = "";
            if (string.IsNullOrWhiteSpace(text) || _entries.Count == 0) return false;

            var aliasEntry = MatchAlias(text);
            if (aliasEntry != null)
            {
                normalized = aliasEntry.Descricao ?? "";
                area = aliasEntry.Area ?? "";
                entryId = aliasEntry.Id ?? "";
                source = "alias";
                return !string.IsNullOrWhiteSpace(normalized);
            }

            var mapped = MapArea(text);
            if (!string.IsNullOrWhiteSpace(mapped))
            {
                normalized = mapped;
                area = mapped;
                source = "area";
                return true;
            }

            return false;
        }

        private HonorariosEntry? MatchAlias(string text)
        {
            if (_aliases.Count == 0) return null;
            var norm = NormalizeKey(text);
            foreach (var alias in _aliases)
            {
                if (alias.Keywords.Any(k => norm.Contains(k)))
                {
                    var byId = _entries.FirstOrDefault(e => e.Id == alias.TargetId);
                    if (byId != null) return byId;
                }
            }
            return null;
        }

        private string MapArea(string especialidade)
        {
            var norm = NormalizeKey(especialidade);
            foreach (var map in _cfg.AreaMap)
            {
                foreach (var kw in map.Keywords)
                {
                    if (string.IsNullOrWhiteSpace(kw)) continue;
                    if (norm.Contains(NormalizeKey(kw))) return map.Area;
                }
            }
            return MapAreaFallback(norm);
        }

        private static string MapAreaFallback(string norm)
        {
            if (string.IsNullOrWhiteSpace(norm)) return "";
            if (norm.Contains("grafotec") || norm.Contains("grafoscop") || norm.Contains("grafocop") || norm.Contains("contab") || norm.Contains("contador"))
                return "CIÊNCIAS CONTÁBEIS";
            if (norm.Contains("engenh") || norm.Contains("arquitet") || norm.Contains("civil") || norm.Contains("insalubr") || norm.Contains("periculos"))
                return "ENGENHARIA E ARQUITETURA";
            if (norm.Contains("odont") || norm.Contains("medic") || norm.Contains("psiquiat"))
                return "MEDICINA / ODONTOLOGIA";
            if (norm.Contains("assistente social") || norm.Contains("servico social") || norm.Contains("estudo social"))
                return "SERVIÇO SOCIAL";
            if (norm.Contains("psicol") || norm.Contains("entrevistadora forense"))
                return "PSICOLOGIA";
            return "";
        }

        private bool TryMatchCandidates(IEnumerable<HonorariosEntry> candidates, decimal valor, out HonorariosEntry entry, out decimal diffPct)
        {
            entry = new HonorariosEntry();
            diffPct = 0;
            if (candidates == null) return false;

            HonorariosEntry? best = null;
            decimal bestDiff = decimal.MaxValue;
            foreach (var e in candidates)
            {
                var diff = Math.Abs(e.Valor - valor);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = e;
                }
            }

            if (best == null) return false;

            diffPct = valor == 0 ? 1m : bestDiff / valor;
            var tol = (decimal)(_cfg.ValueTolerancePct <= 0 ? 0.15 : _cfg.ValueTolerancePct);
            if (diffPct > tol) return false;

            entry = best;
            return true;
        }

        private void LoadTable(string path)
        {
            foreach (var row in CsvUtils.Read(path))
            {
                var area = Pick(row, "AREA");
                var desc = Pick(row, "DESCRICAO");
                var id = Pick(row, "ID");
                var valorRaw = Pick(row, "VALOR");
                if (string.IsNullOrWhiteSpace(desc) || string.IsNullOrWhiteSpace(valorRaw))
                    continue;
                var cleaned = valorRaw.Trim();
                if (cleaned.Contains(","))
                {
                    cleaned = cleaned.Replace(".", "").Replace(",", ".");
                }
                if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var val))
                {
                    if (!decimal.TryParse(valorRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out val))
                        continue;
                }
                _entries.Add(new HonorariosEntry
                {
                    Area = area,
                    Descricao = desc,
                    Id = id,
                    Valor = val
                });
            }
        }

        private void LoadAliases(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var items = JsonSerializer.Deserialize<List<AliasJson>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (items == null) return;
                foreach (var item in items)
                {
                    var target = item.TargetId?.Trim();
                    if (string.IsNullOrWhiteSpace(target)) continue;
                    var keywords = item.Keywords?.Select(NormalizeKey).Where(k => !string.IsNullOrWhiteSpace(k)).ToList() ?? new List<string>();
                    if (keywords.Count == 0) continue;
                    _aliases.Add(new HonorariosAlias { TargetId = target, Keywords = keywords });
                }
            }
            catch
            {
                // ignore
            }
        }

        private static string NormalizeKey(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = TextUtils.RemoveDiacritics(text).ToLowerInvariant();
            t = Regex.Replace(t, "[^a-z0-9]+", " ");
            t = Regex.Replace(t, "\\s+", " ").Trim();
            return t;
        }

        private static string Pick(Dictionary<string, string> row, string key)
        {
            return row.TryGetValue(key, out var v) ? v.Trim() : "";
        }

        private static string ResolvePath(string baseDir, string path)
        {
            if (Path.IsPathRooted(path)) return path;
            if (string.IsNullOrWhiteSpace(baseDir)) return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(baseDir, path));
        }

        private class HonorariosAlias
        {
            public string TargetId { get; set; } = "";
            public List<string> Keywords { get; set; } = new List<string>();
        }

        private class AliasJson
        {
            [JsonPropertyName("target_id")]
            public string? TargetId { get; set; }

            [JsonPropertyName("keywords")]
            public List<string>? Keywords { get; set; }
        }
    }
}
