using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Reference;
using Obj.TjpbDespachoExtractor.Utils;

namespace Obj.Honorarios
{
    public sealed class HonorariosSummary
    {
        public string ConfigPath { get; set; } = "";
        public List<string> Errors { get; set; } = new List<string>();
        public HonorariosSide? PdfA { get; set; }
        public HonorariosSide? PdfB { get; set; }
    }

    public sealed class HonorariosSide
    {
        public string Status { get; set; } = "";
        public string Source { get; set; } = "";
        public string Especialidade { get; set; } = "";
        public string EspecialidadeSource { get; set; } = "";
        public string PeritoName { get; set; } = "";
        public string PeritoNameSource { get; set; } = "";
        public string PeritoCpf { get; set; } = "";
        public string PeritoCpfSource { get; set; } = "";
        public HonorariosPeritoCheck PeritoCheck { get; set; } = new HonorariosPeritoCheck();
        public string ValorField { get; set; } = "";
        public string ValorRaw { get; set; } = "";
        public decimal ValorParsed { get; set; }
        public string ValorNormalized { get; set; } = "";
        public string Area { get; set; } = "";
        public string EntryId { get; set; } = "";
        public string EspecieDaPericia { get; set; } = "";
        public string ValorTabeladoAnexoI { get; set; } = "";
        public string Fator { get; set; } = "";
        public double Confidence { get; set; }
        public List<HonorariosMatch> Matches { get; set; } = new List<HonorariosMatch>();
        public List<string> Derivations { get; set; } = new List<string>();
    }

    public sealed class HonorariosPeritoCheck
    {
        public string InputName { get; set; } = "";
        public string InputCpf { get; set; } = "";
        public string InputEspecialidade { get; set; } = "";
        public bool CatalogFound { get; set; }
        public string CatalogMatchBy { get; set; } = "";
        public string CatalogName { get; set; } = "";
        public string CatalogCpf { get; set; } = "";
        public string CatalogEspecialidade { get; set; } = "";
        public string CatalogSource { get; set; } = "";
        public double CatalogConfidence { get; set; }
        public bool CpfMatches { get; set; }
        public bool EspecialidadeMatches { get; set; }
    }

    public sealed class HonorariosMatch
    {
        public string ValueField { get; set; } = "";
        public string RawValue { get; set; } = "";
        public decimal ParsedValue { get; set; }
        public string DerivedFrom { get; set; } = "";
        public string MatchSource { get; set; } = "";
        public decimal DiffPct { get; set; }
        public string Area { get; set; } = "";
        public string EntryId { get; set; } = "";
        public string EspecieDaPericia { get; set; } = "";
        public string ValorTabeladoAnexoI { get; set; } = "";
        public string Fator { get; set; } = "";
        public double Confidence { get; set; }
    }

    public static class HonorariosEnricher
    {
        public static HonorariosSummary Run(string? mapFieldsPath, string? configPath = null)
        {
            return Run(mapFieldsPath, "", configPath);
        }

        public static HonorariosSummary Run(string? mapFieldsPath, string docType, string? configPath = null)
        {
            var summary = new HonorariosSummary();
            var cfgPath = ResolveConfigPath(configPath);
            summary.ConfigPath = cfgPath;
            if (string.IsNullOrWhiteSpace(cfgPath) || !File.Exists(cfgPath))
            {
                summary.Errors.Add("config_not_found");
                return summary;
            }

            TjpbDespachoConfig cfg;
            try
            {
                cfg = TjpbDespachoConfig.Load(cfgPath);
            }
            catch (Exception ex)
            {
                summary.Errors.Add("config_error: " + ex.Message);
                return summary;
            }

            var honorarios = new HonorariosTable(cfg.Reference.Honorarios, cfg.BaseDir);
            var peritos = PeritoCatalog.Load(cfg.BaseDir, cfg.Reference.PeritosCatalogPaths);

            summary.PdfA = ComputeHonorariosSide("pdf_a", mapFieldsPath, honorarios, peritos, cfg.Reference.Honorarios, docType);
            summary.PdfB = ComputeHonorariosSide("pdf_b", mapFieldsPath, honorarios, peritos, cfg.Reference.Honorarios, docType);

            if (summary.PdfA?.Status == "error")
                summary.Errors.Add("pdf_a_error");
            if (summary.PdfB?.Status == "error")
                summary.Errors.Add("pdf_b_error");

            return summary;
        }

        public static HonorariosSummary RunFromValues(Dictionary<string, string> values, string docType, string? configPath = null)
        {
            var summary = new HonorariosSummary();
            var cfgPath = ResolveConfigPath(configPath);
            summary.ConfigPath = cfgPath;
            if (string.IsNullOrWhiteSpace(cfgPath) || !File.Exists(cfgPath))
            {
                summary.Errors.Add("config_not_found");
                return summary;
            }

            TjpbDespachoConfig cfg;
            try
            {
                cfg = TjpbDespachoConfig.Load(cfgPath);
            }
            catch (Exception ex)
            {
                summary.Errors.Add("config_error: " + ex.Message);
                return summary;
            }

            var honorarios = new HonorariosTable(cfg.Reference.Honorarios, cfg.BaseDir);
            var peritos = PeritoCatalog.Load(cfg.BaseDir, cfg.Reference.PeritosCatalogPaths);

            var safeValues = values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            summary.PdfA = ComputeHonorariosSideFromValues("derived", safeValues, honorarios, peritos, cfg.Reference.Honorarios, docType, "values");

            if (summary.PdfA?.Status == "error")
                summary.Errors.Add("pdf_a_error");

            return summary;
        }

        private static HonorariosSide ComputeHonorariosSide(
            string side,
            string? mapFieldsPath,
            HonorariosTable honorarios,
            PeritoCatalog peritos,
            HonorariosConfig cfg,
            string docType)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var source = "";

            if (!string.IsNullOrWhiteSpace(mapFieldsPath) && File.Exists(mapFieldsPath))
            {
                values = ReadMapFields(mapFieldsPath, side);
                if (values.Count > 0)
                    source = "mapfields";
            }

            return ComputeHonorariosSideFromValues(side, values, honorarios, peritos, cfg, docType, source);
        }

        private static HonorariosSide ComputeHonorariosSideFromValues(
            string side,
            Dictionary<string, string> values,
            HonorariosTable honorarios,
            PeritoCatalog peritos,
            HonorariosConfig cfg,
            string docType,
            string source)
        {
            var result = new HonorariosSide();
            var safeValues = values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            result.Source = source;
            if (safeValues.Count == 0)
            {
                result.Status = "no_fields";
                return result;
            }

            var especialidade = PickValue(safeValues, "ESPECIALIDADE");
            var espSource = "fields";
            var peritoText = PickValue(safeValues, "PERITO");
            var peritoInfo = ParsePeritoText(peritoText);
            var peritoName = PickValue(safeValues, "PERITO");
            var peritoNameSource = string.IsNullOrWhiteSpace(peritoName) ? "" : "fields";
            if (!string.IsNullOrWhiteSpace(peritoInfo.Name))
            {
                peritoName = peritoInfo.Name;
                peritoNameSource = "perito_text";
            }
            var peritoCpf = PickValue(safeValues, "CPF_PERITO");
            var peritoCpfSource = string.IsNullOrWhiteSpace(peritoCpf) ? "" : "fields";
            if (string.IsNullOrWhiteSpace(peritoCpf) && !string.IsNullOrWhiteSpace(peritoInfo.Cpf))
            {
                peritoCpf = peritoInfo.Cpf;
                peritoCpfSource = "perito_text";
            }

            // Regras de derivacao permitidas:
            // - nome -> CPF (catalogo)
            // - CPF -> nome (catalogo)
            // - nome/CPF -> especialidade (catalogo)
            // - especialidade+valor -> especie/valor tabelado/fator (honorarios)
            if (peritos != null)
            {
                if (string.IsNullOrWhiteSpace(peritoName) && !string.IsNullOrWhiteSpace(peritoCpf))
                {
                    if (peritos.TryResolve("", peritoCpf, out var infoByCpf, out _))
                    {
                        if (!string.IsNullOrWhiteSpace(infoByCpf.Name))
                        {
                            peritoName = infoByCpf.Name;
                            peritoNameSource = "catalog_cpf";
                            result.Derivations.Add("perito_name_from_cpf");
                        }
                    }
                }
                else if (string.IsNullOrWhiteSpace(peritoCpf) && !string.IsNullOrWhiteSpace(peritoName))
                {
                    if (peritos.TryResolve(peritoName, "", out var infoByName, out _))
                    {
                        if (!string.IsNullOrWhiteSpace(infoByName.Cpf))
                        {
                            peritoCpf = infoByName.Cpf;
                            peritoCpfSource = "catalog_name";
                            result.Derivations.Add("perito_cpf_from_name");
                        }
                    }
                }
            }

            result.PeritoName = peritoName ?? "";
            result.PeritoNameSource = peritoNameSource;
            result.PeritoCpf = peritoCpf ?? "";
            result.PeritoCpfSource = peritoCpfSource;

            if (string.IsNullOrWhiteSpace(especialidade) && !string.IsNullOrWhiteSpace(peritoInfo.Especialidade))
            {
                especialidade = peritoInfo.Especialidade;
                espSource = "perito_text";
                result.Derivations.Add("especialidade_from_perito_text");
            }
            if (string.IsNullOrWhiteSpace(especialidade))
            {
                var perito = PickValue(safeValues, "PERITO");
                if (!string.IsNullOrWhiteSpace(peritoInfo.Name))
                    perito = peritoInfo.Name;

                var cpf = PickValue(safeValues, "CPF_PERITO");
                if (string.IsNullOrWhiteSpace(cpf) && !string.IsNullOrWhiteSpace(peritoInfo.Cpf))
                    cpf = peritoInfo.Cpf;
                if (peritos != null)
                {
                    if (!string.IsNullOrWhiteSpace(perito) && peritos.TryResolve(perito, "", out var infoByName, out _))
                    {
                        if (!string.IsNullOrWhiteSpace(infoByName.Especialidade))
                        {
                            especialidade = infoByName.Especialidade;
                            espSource = "perito_name_catalog";
                            result.Derivations.Add("especialidade_from_name");
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(cpf) && peritos.TryResolve("", cpf, out var infoByCpf, out _))
                    {
                        if (!string.IsNullOrWhiteSpace(infoByCpf.Especialidade))
                        {
                            especialidade = infoByCpf.Especialidade;
                            espSource = "perito_cpf_catalog";
                            result.Derivations.Add("especialidade_from_cpf");
                        }
                    }
                }
            }
            result.Especialidade = especialidade;
            result.EspecialidadeSource = espSource;

            // Hint when especialidade matches an alias that points to a specific table entry (ID).
            // This is critical for "vezes do anexo I" dispatchos where no money value is present.
            var hintedEntryId = "";

            // Normaliza especialidade/profissao usando aliases/area da tabela de honorarios.
            if (!string.IsNullOrWhiteSpace(especialidade))
            {
                if (honorarios.TryNormalizeEspecialidade(especialidade, out var normEsp, out var normSource, out var normArea, out var entryId))
                {
                    if (string.Equals(normSource, "alias", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(entryId))
                    {
                        hintedEntryId = entryId;
                        if (!string.IsNullOrWhiteSpace(normArea))
                        {
                            especialidade = normArea;
                            result.Especialidade = normArea;
                            result.EspecialidadeSource = "honorarios_alias_area";
                            result.Derivations.Add("especialidade_area_alias");
                        }
                        else if (!string.IsNullOrWhiteSpace(normEsp))
                        {
                            // Fallback: keep previous behavior if area is missing.
                            especialidade = normEsp;
                            result.Especialidade = normEsp;
                            result.EspecialidadeSource = "honorarios_alias";
                            result.Derivations.Add("especialidade_normalized_alias");
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(normEsp))
                    {
                        especialidade = normEsp;
                        result.Especialidade = normEsp;
                        result.EspecialidadeSource = $"honorarios_{normSource}";
                        result.Derivations.Add($"especialidade_normalized_{normSource}");
                    }
                }
            }

            result.PeritoCheck = BuildPeritoCheck(peritos, honorarios, peritoName, peritoCpf, especialidade);
            if (string.IsNullOrWhiteSpace(especialidade))
            {
                result.Status = "missing_especialidade";
                return result;
            }

            var percent = TryParsePercent(PickValue(safeValues, "PERCENTUAL"));
            var parcela = TryParseParcelaCount(PickValue(safeValues, "PARCELA"));

            HonorariosMatch? forcedByFator = null;
            var fatorRaw = PickValue(safeValues, "FATOR");
            if (!string.IsNullOrWhiteSpace(fatorRaw) && honorarios.TryGetById(fatorRaw, out var fatorEntry))
            {
                forcedByFator = BuildMatch("FATOR", fatorRaw, fatorEntry, fatorEntry.Valor, "fator_id", 0.95);
            }

            var valueFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var allowed = GetAllowedValueFields(docType);
            foreach (var field in allowed)
            {
                valueFields[field] = PickValue(safeValues, field);
            }

            if (valueFields.Count == 0)
            {
                valueFields["VALOR_ARBITRADO_JZ"] = PickValue(safeValues, "VALOR_ARBITRADO_JZ");
                valueFields["VALOR_ARBITRADO_DE"] = PickValue(safeValues, "VALOR_ARBITRADO_DE");
                valueFields["VALOR_ARBITRADO_CM"] = PickValue(safeValues, "VALOR_ARBITRADO_CM");
                valueFields["ADIANTAMENTO"] = PickValue(safeValues, "ADIANTAMENTO");
            }

            // If we have an alias-based factor (specific table entry), we can derive the money value from:
            // - a multiplier (ex.: "1,5 (uma vez e meia) do anexo I")
            // - or default to 1x table value when no multiplier was extracted.
            HonorariosMatch? forcedByAlias = null;
            if (!string.IsNullOrWhiteSpace(hintedEntryId) && honorarios.TryGetById(hintedEntryId, out var hintedEntry))
            {
                var multiplier = 1.0m;
                var multField = "ALIAS";
                var multRaw = "";

                // Prefer the allowed fields (doc-type specific) when choosing a multiplier candidate.
                var multCandidates = new List<string>(allowed ?? new List<string>());
                if (multCandidates.Count == 0)
                    multCandidates.AddRange(valueFields.Keys);

                foreach (var field in multCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!valueFields.TryGetValue(field, out var raw) || string.IsNullOrWhiteSpace(raw))
                        continue;
                    if (raw.Contains("R$", StringComparison.OrdinalIgnoreCase))
                        continue; // looks like a real money value, not a multiplier
                    if (!TextUtils.TryParseMoney(raw, out var parsed))
                        continue;
                    if (parsed <= 0m || parsed > 20m)
                        continue;

                    multiplier = parsed;
                    multField = field;
                    multRaw = raw;
                    break;
                }

                var derivedValue = hintedEntry.Valor * multiplier;
                var matchSource = multiplier == 1.0m ? "alias_id" : "alias_id_multiplier";
                var derivedFrom = multiplier == 1.0m ? "table_default_1x" : "multiplier*table";
                var conf = multiplier == 1.0m ? 0.85 : 0.92;

                forcedByAlias = BuildMatch(multField, multRaw, hintedEntry, derivedValue, matchSource, conf, 0m, derivedFrom);
                result.Derivations.Add(multiplier == 1.0m ? "honorarios_from_alias_1x" : "honorarios_from_alias_multiplier");
            }

            var matchesByField = new Dictionary<string, HonorariosMatch>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in valueFields)
            {
                var field = kv.Key;
                var raw = kv.Value;
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                if (!TextUtils.TryParseMoney(raw, out var baseValue))
                    continue;

                var candidates = BuildValueCandidates(field, raw, baseValue, percent, parcela);
                HonorariosMatch? best = null;
                foreach (var cand in candidates)
                {
                    if (!honorarios.TryMatchDetailed(especialidade, cand.Value, out var entry, out var conf, out var diffPct, out var matchSource))
                        continue;

                    var match = BuildMatch(field, raw, entry, cand.Value, matchSource, conf, diffPct, cand.DerivedFrom);
                    if (best == null || CompareMatch(match, best) > 0)
                        best = match;
                }

                if (best != null)
                    matchesByField[field] = best;
            }

            var allMatches = matchesByField.Values.ToList();
            if (forcedByFator != null)
                allMatches.Insert(0, forcedByFator);
            if (forcedByAlias != null)
                allMatches.Insert(0, forcedByAlias);

            result.Matches = allMatches;

            if (allMatches.Count == 0)
            {
                result.Status = "missing_valor";
                return result;
            }

            var entryIds = allMatches
                .Where(m => !string.IsNullOrWhiteSpace(m.EntryId))
                .Select(m => m.EntryId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            HonorariosMatch? chosen = null;
            if (forcedByFator != null)
            {
                if (entryIds.Count == 0 || entryIds.All(id => string.Equals(id, forcedByFator.EntryId, StringComparison.OrdinalIgnoreCase)))
                {
                    chosen = forcedByFator;
                }
                else
                {
                    result.Status = "ambiguous_match";
                    return result;
                }
            }

            if (chosen == null)
            {
                if (entryIds.Count > 1)
                {
                    // If an alias pinned a specific table entry (ex.: grafotécnico), it can be used as a tie-breaker
                    // as long as it is consistent with the money extracted from the document.
                    if (forcedByAlias != null)
                    {
                        decimal? moneyInput = null;
                        foreach (var raw in valueFields.Values)
                        {
                            if (string.IsNullOrWhiteSpace(raw))
                                continue;
                            if (!TextUtils.TryParseMoney(raw, out var parsed))
                                continue;
                            if (parsed > 20m && (moneyInput == null || parsed > moneyInput.Value))
                                moneyInput = parsed;
                        }

                        var aliasOk = !moneyInput.HasValue;
                        if (moneyInput.HasValue)
                        {
                            if (moneyInput.Value == 0m)
                                aliasOk = forcedByAlias.ParsedValue == 0m;
                            else
                                aliasOk = (Math.Abs(forcedByAlias.ParsedValue - moneyInput.Value) / moneyInput.Value) <= 0.05m;
                        }

                        if (aliasOk)
                        {
                            chosen = forcedByAlias;
                        }
                        else
                        {
                            // If alias conflicts with the extracted money, drop it and try to proceed with value-based match.
                            var noAlias = allMatches.Where(m => m != forcedByAlias).ToList();
                            var idsNoAlias = noAlias
                                .Where(m => !string.IsNullOrWhiteSpace(m.EntryId))
                                .Select(m => m.EntryId)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            if (idsNoAlias.Count == 1 && noAlias.Count > 0)
                                chosen = noAlias.OrderByDescending(m => m.Confidence).ThenBy(m => m.DiffPct).First();
                        }
                    }

                    if (chosen == null)
                    {
                        result.Status = "ambiguous_match";
                        return result;
                    }
                }

                if (chosen == null)
                    chosen = allMatches.OrderByDescending(m => m.Confidence).ThenBy(m => m.DiffPct).First();
            }

            result.Status = "ok";
            result.ValorField = chosen.ValueField;
            result.ValorRaw = chosen.RawValue;
            result.ValorParsed = chosen.ParsedValue;
            result.ValorNormalized = FormatMoney(chosen.ParsedValue);
            result.Area = chosen.Area ?? "";
            result.EntryId = chosen.EntryId ?? "";
            result.EspecieDaPericia = chosen.EspecieDaPericia ?? "";
            result.ValorTabeladoAnexoI = chosen.ValorTabeladoAnexoI ?? "";
            result.Fator = chosen.Fator ?? "";
            result.Confidence = chosen.Confidence;
            if (!result.Derivations.Contains("honorarios_from_especialidade_valor", StringComparer.OrdinalIgnoreCase))
                result.Derivations.Add("honorarios_from_especialidade_valor");
            return result;
        }

        private static HonorariosPeritoCheck BuildPeritoCheck(
            PeritoCatalog? peritos,
            HonorariosTable honorarios,
            string? name,
            string? cpf,
            string? especialidade)
        {
            var check = new HonorariosPeritoCheck
            {
                InputName = name ?? "",
                InputCpf = cpf ?? "",
                InputEspecialidade = especialidade ?? ""
            };

            if (peritos == null)
                return check;

            if (!peritos.TryResolve(name, cpf, out var info, out var conf))
                return check;

            check.CatalogFound = true;
            check.CatalogConfidence = conf;
            check.CatalogName = info.Name ?? "";
            check.CatalogCpf = info.Cpf ?? "";
            check.CatalogEspecialidade = info.Especialidade ?? "";
            check.CatalogSource = info.Source ?? "";

            var cpfNorm = TextUtils.NormalizeCpf(cpf ?? "");
            if (!string.IsNullOrWhiteSpace(cpfNorm) && !string.IsNullOrWhiteSpace(info.Cpf))
                check.CpfMatches = string.Equals(cpfNorm, info.Cpf, StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(check.CatalogCpf) && !string.IsNullOrWhiteSpace(cpfNorm) &&
                string.Equals(cpfNorm, check.CatalogCpf, StringComparison.OrdinalIgnoreCase))
            {
                check.CatalogMatchBy = "cpf";
            }
            else if (!string.IsNullOrWhiteSpace(check.CatalogName))
            {
                check.CatalogMatchBy = "name";
            }

            if (!string.IsNullOrWhiteSpace(especialidade) && !string.IsNullOrWhiteSpace(info.Especialidade))
            {
                if (honorarios.TryResolveAreaFromText(especialidade, out var a1, out _) &&
                    honorarios.TryResolveAreaFromText(info.Especialidade, out var a2, out _))
                {
                    check.EspecialidadeMatches = string.Equals(a1, a2, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    check.EspecialidadeMatches = string.Equals(
                        TextUtils.NormalizeWhitespace(especialidade),
                        TextUtils.NormalizeWhitespace(info.Especialidade),
                        StringComparison.OrdinalIgnoreCase);
                }
            }

            return check;
        }

        private static Dictionary<string, string> ReadMapFields(string mapFieldsPath, string side)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(mapFieldsPath));
                if (!doc.RootElement.TryGetProperty(side, out var sideObj) || sideObj.ValueKind != JsonValueKind.Object)
                    return result;

                foreach (var prop in sideObj.EnumerateObject())
                {
                    var value = "";
                    if (prop.Value.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.String)
                        value = v.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(value) &&
                        prop.Value.TryGetProperty("ValueRaw", out var raw) && raw.ValueKind == JsonValueKind.String)
                        value = raw.GetString() ?? "";

                    if (!string.IsNullOrWhiteSpace(value))
                        result[prop.Name] = value;
                }
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return result;
        }

        private static string PickValue(Dictionary<string, string> values, string key)
        {
            return values.TryGetValue(key, out var v) ? v ?? "" : "";
        }

        private sealed class PeritoTextInfo
        {
            public string Name { get; set; } = "";
            public string Cpf { get; set; } = "";
            public string Especialidade { get; set; } = "";
        }

        private static PeritoTextInfo ParsePeritoText(string? text)
        {
            var info = new PeritoTextInfo();
            if (string.IsNullOrWhiteSpace(text))
                return info;

            var cleaned = TextUtils.NormalizeWhitespace(TextUtils.CollapseSpacedLettersText(text));
            if (string.IsNullOrWhiteSpace(cleaned))
                return info;

            var cpfMatch = Regex.Match(cleaned, @"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b");
            if (cpfMatch.Success)
                info.Cpf = TextUtils.NormalizeCpf(cpfMatch.Value);

            // remover sufixos de documento (CPF/PIS/RG/INSS etc.)
            cleaned = Regex.Split(cleaned, @"(?i)\bCPF\b").FirstOrDefault() ?? cleaned;
            cleaned = Regex.Split(cleaned, @"(?i)\bPIS/?PASEP\b").FirstOrDefault() ?? cleaned;
            cleaned = Regex.Split(cleaned, @"(?i)\bRG\b").FirstOrDefault() ?? cleaned;
            cleaned = Regex.Split(cleaned, @"(?i)\bINSS\b").FirstOrDefault() ?? cleaned;
            cleaned = cleaned.Trim();

            if (TryExtractEspecialidadeFromText(cleaned, out var esp))
            {
                info.Especialidade = esp;
                cleaned = Regex.Replace(cleaned, Regex.Escape(esp), "", RegexOptions.IgnoreCase);
                cleaned = TextUtils.NormalizeWhitespace(cleaned);
            }

            cleaned = Regex.Replace(cleaned, @"(?i)^(interessad[oa]|perit[oa])\b[:\-\s]*", "");
            cleaned = cleaned.Trim().Trim(',', ';', '-', '–', '—');

            var parts = cleaned
                .Split(new[] { " - ", " – ", " — ", "-", "–", "—", "," }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => TextUtils.NormalizeWhitespace(p))
                .Where(LooksLikePersonName)
                .ToList();

            if (parts.Count > 0)
                info.Name = parts.OrderByDescending(p => p.Length).First();
            else if (LooksLikePersonName(cleaned))
                info.Name = cleaned;

            return info;
        }

        private static bool LooksLikePersonName(string text)
        {
            if (!LooksLikePersonNameLoose(text))
                return false;
            var tokens = TextUtils.NormalizeWhitespace(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length >= 2 && tokens.All(t => t.Length >= 2);
        }

        private static bool LooksLikePersonNameLoose(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var norm = TextUtils.NormalizeWhitespace(text);
            if (norm.Any(char.IsDigit)) return false;
            var lower = TextUtils.RemoveDiacritics(norm).ToLowerInvariant();
            if (Regex.IsMatch(lower, @"\b(perito|perita|interessad[oa]|cpf|cnpj|pis|pasep|inss|rg)\b"))
                return false;
            if (Regex.IsMatch(lower, @"\b(engenheir|arquitet|contador|psicol|medic|odont|assistente\s+social|fonoaud|fisioterap|economist|administrador|b[ií]olog|qu[ií]mic|farmac)\b"))
                return false;
            var tokens = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) return false;
            var longTokens = tokens.Count(t => t.Length >= 2);
            return longTokens >= 2;
        }

        private static bool TryExtractEspecialidadeFromText(string? text, out string especialidade)
        {
            especialidade = "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var rx = new Regex(@"\b(engenheir[oa](?:\s+(?:civil|el[eé]tric[oa]|mec[aâ]nic[oa]|ambiental|sanitar(?:ista)?|de\s+seguran[çc]a(?:\s+do\s+trabalho)?|de\s+produ[cç][aã]o|de\s+minas|qu[ií]mic[oa]|agr[oô]nom[oa]|florestal|agrimensor[oa]))?|grafot[eê]cnic[oa]|grafoscop[ia]|arquitet[oa]|contador[a]?|cont[aá]bil|psic[oó]log[oa]|m[eé]dic[oa]|odont[oó]log[oa]|assistente\s+social|fonoaudi[oó]log[oa]|fisioterapeut[oa]|economist[a]|administrador[a]?|b[ií]olog[oa]|qu[ií]mic[oa]|farmac[eê]utic[oa])\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var m = rx.Match(text);
            if (!m.Success)
                return false;

            especialidade = m.Value.Trim();
            return !string.IsNullOrWhiteSpace(especialidade);
        }

        private static List<string> GetAllowedValueFields(string docType)
        {
            if (string.IsNullOrWhiteSpace(docType))
                return new List<string>();

            var key = docType.Trim().ToUpperInvariant();
            if (key == "HONORARIOS_DERIVED")
                return new List<string> { "VALOR_ARBITRADO_DE", "VALOR_ARBITRADO_JZ" };
            if (key == "DESPACHO")
                return new List<string> { "VALOR_ARBITRADO_DE", "VALOR_ARBITRADO_JZ" };
            if (key == "REQUERIMENTO_HONORARIOS")
                return new List<string> { "VALOR_ARBITRADO_JZ" };
            if (key == "CERTIDAO_CM")
                return new List<string> { "ADIANTAMENTO" };

            return new List<string>();
        }

        private static (string Field, string Raw) PickValor(Dictionary<string, string> values, HonorariosConfig cfg)
        {
            var candidates = new List<string>();
            if (cfg.PreferValorDe)
            {
                candidates.Add("VALOR_ARBITRADO_DE");
                if (cfg.AllowValorJz)
                    candidates.Add("VALOR_ARBITRADO_JZ");
            }
            else
            {
                if (cfg.AllowValorJz)
                    candidates.Add("VALOR_ARBITRADO_JZ");
                candidates.Add("VALOR_ARBITRADO_DE");
            }

            candidates.Add("VALOR_ARBITRADO_CM");

            if (cfg.AllowValorJz && !candidates.Contains("VALOR_ARBITRADO_JZ"))
                candidates.Add("VALOR_ARBITRADO_JZ");
            if (!candidates.Contains("VALOR_ARBITRADO_DE"))
                candidates.Add("VALOR_ARBITRADO_DE");

            foreach (var key in candidates)
            {
                if (values.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
                    return (key, raw);
            }

            return ("", "");
        }

        private static HonorariosMatch BuildMatch(
            string field,
            string raw,
            HonorariosEntry entry,
            decimal parsedValue,
            string source,
            double confidence,
            decimal diffPct = 0m,
            string derivedFrom = "raw")
        {
            return new HonorariosMatch
            {
                ValueField = field,
                RawValue = raw,
                ParsedValue = parsedValue,
                DerivedFrom = derivedFrom,
                MatchSource = source,
                DiffPct = diffPct,
                Area = entry.Area ?? "",
                EntryId = entry.Id ?? "",
                EspecieDaPericia = entry.Descricao ?? "",
                ValorTabeladoAnexoI = FormatMoney(entry.Valor),
                Fator = entry.Id ?? "",
                Confidence = confidence
            };
        }

        private static int CompareMatch(HonorariosMatch left, HonorariosMatch right)
        {
            var scoreLeft = left.Confidence - (double)left.DiffPct - DerivationPenalty(left.DerivedFrom);
            var scoreRight = right.Confidence - (double)right.DiffPct - DerivationPenalty(right.DerivedFrom);
            return scoreLeft.CompareTo(scoreRight);
        }

        private static double DerivationPenalty(string derivedFrom)
        {
            if (!string.IsNullOrWhiteSpace(derivedFrom)
                && derivedFrom.StartsWith("multiple_", StringComparison.OrdinalIgnoreCase))
                return 0.18;
            return derivedFrom switch
            {
                "raw" => 0,
                "percent_total" => 0.05,
                "per_unit" => 0.08,
                "percent_total_per_unit" => 0.12,
                _ => 0.1
            };
        }

        private static List<(decimal Value, string DerivedFrom)> BuildValueCandidates(
            string field,
            string raw,
            decimal baseValue,
            decimal? percent,
            int? parcela)
        {
            var list = new List<(decimal, string)>
            {
                (baseValue, "raw")
            };

            var usePercent = string.Equals(field, "ADIANTAMENTO", StringComparison.OrdinalIgnoreCase);
            var percentValue = usePercent ? percent : null;
            var parcelaValue = usePercent ? parcela : null;

            if (percentValue.HasValue && percentValue.Value > 0 && percentValue.Value <= 100m)
            {
                var factor = percentValue.Value / 100m;
                if (factor > 0)
                {
                    var total = baseValue / factor;
                    if (total > 0)
                        list.Add((total, "percent_total"));
                    if (parcelaValue.HasValue && parcelaValue.Value > 1)
                    {
                        var unit = total / parcelaValue.Value;
                        if (unit > 0)
                            list.Add((unit, "percent_total_per_unit"));
                    }
                }
            }

            if (parcelaValue.HasValue && parcelaValue.Value > 1)
            {
                var unit = baseValue / parcelaValue.Value;
                if (unit > 0)
                    list.Add((unit, "per_unit"));
            }

            if (!percentValue.HasValue && (!parcelaValue.HasValue || parcelaValue.Value <= 1))
            {
                for (var n = 2; n <= 20; n++)
                {
                    var unit = baseValue / n;
                    if (unit > 0)
                        list.Add((unit, $"multiple_{n}"));
                }
            }

            return list;
        }

        private static decimal? TryParsePercent(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var cleaned = raw.Replace("%", "").Trim();
            if (!decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                if (!decimal.TryParse(cleaned.Replace(",", "."), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
                    return null;
            }
            if (value <= 0) return null;
            if (value <= 1m) value *= 100m;
            return value;
        }

        private static int? TryParseParcelaCount(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"\d{1,3}");
            if (!m.Success) return null;
            if (int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                return count;
            return null;
        }

        private static string ResolveConfigPath(string? configPath)
        {
            if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
                return configPath;

            var cwd = Directory.GetCurrentDirectory();
            var candidates = new[]
            {
                Path.Combine(cwd, "configs", "config.yaml"),
                Path.Combine(cwd, "configs", "config.yml"),
                Path.Combine(cwd, "OBJ", "configs", "config.yaml"),
                Path.Combine(cwd, "..", "configs", "config.yaml")
            };
            return candidates.FirstOrDefault(File.Exists) ?? "";
        }

        private static string FormatMoney(decimal value)
        {
            var formatted = value.ToString("C", new CultureInfo("pt-BR"));
            return formatted.Replace('\u00A0', ' ');
        }
    }
}
