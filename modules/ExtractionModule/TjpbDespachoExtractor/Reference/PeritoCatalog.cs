using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Obj.TjpbDespachoExtractor.Utils;
using Obj.ValidatorModule;

namespace Obj.TjpbDespachoExtractor.Reference
{
    public class PeritoInfo
    {
        public string Name { get; set; } = "";
        public string Cpf { get; set; } = "";
        public string Especialidade { get; set; } = "";
        public string Source { get; set; } = "";
        public bool HasMultipleEspecialidades { get; set; }
    }

    public class PeritoCatalog
    {
        private readonly Dictionary<string, PeritoInfo> _byCpf = new Dictionary<string, PeritoInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<PeritoInfo>> _byName = new Dictionary<string, List<PeritoInfo>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<PeritoInfo>> _byNameCompact = new Dictionary<string, List<PeritoInfo>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _sources = new List<string>();

        public static PeritoCatalog Load(string baseDir, IEnumerable<string> paths)
        {
            var cat = new PeritoCatalog();
            foreach (var raw in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var path = ResolvePath(baseDir, raw);
                if (!File.Exists(path)) continue;
                cat._sources.Add(path);
                cat.LoadFile(path);
            }
            return cat;
        }

        public bool TryResolve(string? name, string? cpf, out PeritoInfo info, out double confidence)
        {
            info = new PeritoInfo();
            confidence = 0;

            var cpfDigits = TextUtils.NormalizeCpf(cpf ?? "");
            if (!string.IsNullOrWhiteSpace(cpfDigits) && _byCpf.TryGetValue(cpfDigits, out var byCpf))
            {
                info = byCpf;
                confidence = 0.9;
                return true;
            }

            var key = NormalizeNameKey(name ?? "");
            if (!string.IsNullOrWhiteSpace(key) && _byName.TryGetValue(key, out var list) && list.Count > 0)
            {
                var chosen = ChooseBest(list);
                info = chosen;
                confidence = chosen.HasMultipleEspecialidades ? 0.6 : 0.75;
                return true;
            }

            var compact = NormalizeNameKeyCompact(name ?? "");
            if (!string.IsNullOrWhiteSpace(compact) && _byNameCompact.TryGetValue(compact, out var list2) && list2.Count > 0)
            {
                var chosen = ChooseBest(list2);
                info = chosen;
                confidence = chosen.HasMultipleEspecialidades ? 0.55 : 0.7;
                return true;
            }

            return false;
        }

        private void LoadFile(string path)
        {
            foreach (var row in CsvUtils.Read(path))
            {
                var name = Pick(row, new[] { "PERITO", "NOME", "NOME_PERITO" });
                var cpf = Pick(row, new[] { "CPF/CNPJ", "CPF", "DOCUMENTO" });
                var esp = Pick(row, new[] { "ESPECIALIDADE", "PROFISSAO", "PROFISSÃO" });

                var cpfDigits = TextUtils.NormalizeCpf(cpf ?? "");
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(cpfDigits))
                    continue;

                if (!string.IsNullOrWhiteSpace(name) && !LooksLikeName(name))
                    continue;

                var info = new PeritoInfo
                {
                    Name = CleanName(name),
                    Cpf = cpfDigits,
                    Especialidade = CleanEspecialidade(esp),
                    Source = Path.GetFileName(path)
                };

                if (!string.IsNullOrWhiteSpace(info.Cpf))
                {
                    if (_byCpf.TryGetValue(info.Cpf, out var existing))
                    {
                        _byCpf[info.Cpf] = ChooseBest(new List<PeritoInfo> { existing, info });
                    }
                    else
                    {
                        _byCpf[info.Cpf] = info;
                    }
                }

                var key = NormalizeNameKey(info.Name);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    if (!_byName.TryGetValue(key, out var list))
                    {
                        list = new List<PeritoInfo>();
                        _byName[key] = list;
                    }
                    list.Add(info);
                }
                var compact = NormalizeNameKeyCompact(info.Name);
                if (!string.IsNullOrWhiteSpace(compact))
                {
                    if (!_byNameCompact.TryGetValue(compact, out var list2))
                    {
                        list2 = new List<PeritoInfo>();
                        _byNameCompact[compact] = list2;
                    }
                    list2.Add(info);
                }
            }

            foreach (var kv in _byName)
            {
                var unique = kv.Value
                    .Where(v => !string.IsNullOrWhiteSpace(v.Especialidade))
                    .Select(v => v.Especialidade)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (unique.Count > 1)
                {
                    foreach (var v in kv.Value)
                        v.HasMultipleEspecialidades = true;
                }
            }
        }

        private static PeritoInfo ChooseBest(List<PeritoInfo> list)
        {
            return list
                .OrderByDescending(p => !string.IsNullOrWhiteSpace(p.Especialidade))
                .ThenByDescending(p => !string.IsNullOrWhiteSpace(p.Cpf))
                .First();
        }

        private static bool LooksLikeName(string name)
        {
            return ValidatorRules.LooksLikeCatalogName(name);
        }

        private static string CleanName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var v = name.Trim();
            v = v.Trim(',', ';', '.', '-', ' ');
            return TextUtils.NormalizeWhitespace(v);
        }

        private static string CleanEspecialidade(string esp)
        {
            if (string.IsNullOrWhiteSpace(esp)) return "";
            var v = esp.Trim();
            if (v.Contains('@')) return "";
            if (Regex.IsMatch(v, "interessad[oa]", RegexOptions.IgnoreCase)) return "";
            if (Regex.IsMatch(v, "sighop", RegexOptions.IgnoreCase)) return "";
            v = Regex.Replace(v, @"(?i)\b(CPF|CNPJ|PIS|INSS|RG|CRM|CRP|CRO|COREN|CREFITO)\b.*$", "");
            if (v.Length > 120)
                v = v.Substring(0, 120);
            return TextUtils.NormalizeWhitespace(v);
        }

        private static string NormalizeNameKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var n = TextUtils.RemoveDiacritics(name).ToUpperInvariant();
            n = Regex.Replace(n, "[^A-Z0-9 ]+", " ");
            n = Regex.Replace(n, "\\s+", " ").Trim();
            return n;
        }

        private static string NormalizeNameKeyCompact(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var n = TextUtils.RemoveDiacritics(name).ToUpperInvariant();
            n = Regex.Replace(n, "[^A-Z0-9 ]+", " ");
            n = Regex.Replace(n, "\\s+", "");
            return n;
        }

        private static string Pick(Dictionary<string, string> row, string[] keys)
        {
            foreach (var k in keys)
            {
                if (row.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }
            return "";
        }

        private static string ResolvePath(string baseDir, string path)
        {
            if (Path.IsPathRooted(path)) return path;
            if (string.IsNullOrWhiteSpace(baseDir)) return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(baseDir, path));
        }
    }

    internal static class CsvUtils
    {
        public static IEnumerable<Dictionary<string, string>> Read(string path)
        {
            using var reader = new StreamReader(path);
            var header = ReadRow(reader);
            if (header == null || header.Count == 0) yield break;
            while (!reader.EndOfStream)
            {
                var row = ReadRow(reader);
                if (row == null || row.Count == 0) continue;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < header.Count && i < row.Count; i++)
                {
                    dict[header[i]] = row[i];
                }
                yield return dict;
            }
        }

        private static List<string>? ReadRow(StreamReader reader)
        {
            var line = reader.ReadLine();
            if (line == null) return null;
            return ParseCsvLine(line);
        }

        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            if (line == null) return result;
            var sb = new System.Text.StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                    continue;
                }
                if (c == ',' && !inQuotes)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
            result.Add(sb.ToString());
            return result;
        }
    }
}
