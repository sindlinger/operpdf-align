using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using DiffMatchPatch;
using Obj.Models;
using Obj.Utils;
using Obj.ValidatorModule;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Models;
using Obj.TjpbDespachoExtractor.Reference;
using Obj.TjpbDespachoExtractor.Utils;

namespace Obj.TjpbDespachoExtractor.Extraction
{
    public class DespachoContext
    {
        public string FullText { get; set; } = "";
        public List<ParagraphSegment> Paragraphs { get; set; } = new List<ParagraphSegment>();
        public List<BandInfo> Bands { get; set; } = new List<BandInfo>();
        public List<BandSegment> BandSegments { get; set; } = new List<BandSegment>();
        public List<RegionSegment> Regions { get; set; } = new List<RegionSegment>();
        public List<PageTextInfo> Pages { get; set; } = new List<PageTextInfo>();
        public string FileName { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string ProcessNumber { get; set; } = "";
        public List<string> FooterSigners { get; set; } = new List<string>();
        public string? FooterSignatureRaw { get; set; }
        public TjpbDespachoConfig Config { get; set; } = new TjpbDespachoConfig();
        public int StartPage1 { get; set; }
        public int EndPage1 { get; set; }
    }

    public class PageTextInfo
    {
        public int Page1 { get; set; }
        public string Text { get; set; } = "";
    }

    public class FieldExtractor
    {
        private readonly TjpbDespachoConfig _cfg;
        private readonly TemplateFieldExtractor _template;
        private readonly FieldStrategyEngine _strategies;
        private readonly Regex _cnj;
        private readonly Regex _cnjLoose;
        private readonly Regex _sei;
        private readonly Regex _adme;
        private readonly Regex _cpf;
        private readonly Regex _money;
        private readonly Regex _datePt;
        private readonly Regex _dateSlash;
        private readonly PeritoCatalog _peritoCatalog;
        private readonly HonorariosTable _honorarios;
        private readonly diff_match_patch _dmpField;

        public FieldExtractor(TjpbDespachoConfig cfg)
        {
            _cfg = cfg;
            _template = new TemplateFieldExtractor();
            _strategies = new FieldStrategyEngine(cfg);
            _cnj = new Regex(cfg.Regex.ProcessoCnj, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var cnjLoose = string.IsNullOrWhiteSpace(cfg.Regex.ProcessoCnjLoose)
                ? @"\d{7}\s*-\s*\d{2}\s*\.\s*\d{4}\s*\.\s*\d\s*\.\s*\d{2}\s*\.\s*\d{4}"
                : cfg.Regex.ProcessoCnjLoose;
            _cnjLoose = new Regex(cnjLoose, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            _sei = new Regex(cfg.Regex.ProcessoSei, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            _adme = new Regex(cfg.Regex.ProcessoAdme, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            _cpf = new Regex(cfg.Regex.Cpf, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            _money = new Regex(cfg.Regex.Money, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            _datePt = new Regex(cfg.Regex.DatePt, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            _dateSlash = new Regex(cfg.Regex.DateSlash, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            _peritoCatalog = PeritoCatalog.Load(cfg.BaseDir, cfg.Reference.PeritosCatalogPaths);
            _honorarios = new HonorariosTable(cfg.Reference.Honorarios, cfg.BaseDir);
            _dmpField = new diff_match_patch
            {
                Match_Threshold = 0.6f,
                Match_Distance = 5000
            };
        }

        public Dictionary<string, FieldInfo> ExtractAll(DespachoContext ctx)
        {
            var result = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
            var seed = ExtractFromRegionTemplates(ctx);
            var strategyHits = _strategies.Extract(ctx);
            seed = MergeSeed(seed, strategyHits);

            result["PROCESSO_ADMINISTRATIVO"] = Prefer(seed, "PROCESSO_ADMINISTRATIVO", ExtractProcessoAdministrativo(ctx));
            result["PROCESSO_JUDICIAL"] = Prefer(seed, "PROCESSO_JUDICIAL", ExtractProcessoJudicial(ctx));

            var (vara, comarca) = ExtractVaraComarca(ctx);
            result["VARA"] = Prefer(seed, "VARA", vara);
            result["COMARCA"] = Prefer(seed, "COMARCA", comarca);

            var (promovente, promovido) = ExtractPromoventePromovido(ctx);
            result["PROMOVENTE"] = Prefer(seed, "PROMOVENTE", promovente);
            result["PROMOVIDO"] = Prefer(seed, "PROMOVIDO", promovido);

            var perito = ExtractPeritoCpfEspecialidade(ctx, result);
            result["PERITO"] = Prefer(seed, "PERITO", perito.perito);
            result["CPF_PERITO"] = Prefer(seed, "CPF_PERITO", perito.cpf);
            result["ESPECIALIDADE"] = Prefer(seed, "ESPECIALIDADE", perito.especialidade);
            result["ESPECIE_DA_PERICIA"] = Prefer(seed, "ESPECIE_DA_PERICIA", perito.especie);

            var valores = ExtractValores(ctx);
            result["VALOR_ARBITRADO_JZ"] = PreferByPage(seed, "VALOR_ARBITRADO_JZ", valores.valorJz, ctx.StartPage1);
            result["VALOR_ARBITRADO_DE"] = PreferByPage(seed, "VALOR_ARBITRADO_DE", valores.valorDe, ctx.StartPage1 + 1);
            result["VALOR_ARBITRADO_CM"] = Prefer(seed, "VALOR_ARBITRADO_CM", valores.valorCm);
            result["VALOR_TABELADO_ANEXO_I"] = Prefer(seed, "VALOR_TABELADO_ANEXO_I", valores.valorTabela);

            var extras = ExtractAdiantamentoPercentualParcela(ctx);
            result["ADIANTAMENTO"] = Prefer(seed, "ADIANTAMENTO", extras.adiantamento);
            result["PERCENTUAL"] = Prefer(seed, "PERCENTUAL", extras.percentual);
            result["PARCELA"] = Prefer(seed, "PARCELA", extras.parcela);

            result["DATA"] = Prefer(seed, "DATA", ExtractData(ctx));
            result["ASSINANTE"] = Prefer(seed, "ASSINANTE", ExtractAssinante(ctx));

            result["NUM_PERITO"] = Prefer(seed, "NUM_PERITO", ExtractNumPerito(ctx));

            EnsureField(result, "VALOR_ARBITRADO_CM");
            EnsureField(result, "VALOR_TABELADO_ANEXO_I");
            EnsureField(result, "ADIANTAMENTO");
            EnsureField(result, "PERCENTUAL");
            EnsureField(result, "PARCELA");
            EnsureField(result, "NUM_PERITO");

            PostValidate(result);
            EnrichWithPeritoCatalog(result);
            NormalizeEspecialidadeArea(result);
            ApplyHonorariosLookup(result);

            return result;
        }

        private FieldInfo Prefer(Dictionary<string, FieldInfo> seed, string key, FieldInfo fallback)
        {
            if (key.Equals("ASSINANTE", StringComparison.OrdinalIgnoreCase))
                return fallback;
            if (seed.TryGetValue(key, out var s) && s.Method != "not_found")
            {
                if ((key.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase)) &&
                    IsInstitutionalPartyValue(s.Value))
                {
                    // evita "Juízo/Vara/Comarca" como parte processual
                    return fallback;
                }
                if (fallback.Method == "not_found" || s.Confidence >= fallback.Confidence)
                    return s;
            }
            return fallback;
        }

        private FieldInfo PreferByPage(Dictionary<string, FieldInfo> seed, string key, FieldInfo fallback, int requiredPage1)
        {
            if (seed.TryGetValue(key, out var s) && s.Method != "not_found")
            {
                var page = s.Evidence?.Page1 ?? 0;
                if (requiredPage1 > 0 && page == requiredPage1)
                {
                    if (fallback.Method == "not_found" || s.Confidence >= fallback.Confidence)
                        return s;
                }
            }
            if (fallback.Method != "not_found")
            {
                var fpage = fallback.Evidence?.Page1 ?? 0;
                if (requiredPage1 > 0 && fpage == requiredPage1)
                    return fallback;
                return NotFound();
            }
            return fallback;
        }

        private Dictionary<string, FieldInfo> MergeSeed(Dictionary<string, FieldInfo> baseSeed, Dictionary<string, FieldInfo> extra)
        {
            var merged = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in baseSeed)
                merged[kv.Key] = kv.Value;
            foreach (var kv in extra)
            {
                if (!merged.TryGetValue(kv.Key, out var existing) || kv.Value.Confidence > existing.Confidence)
                    merged[kv.Key] = kv.Value;
            }
            return merged;
        }

        private Dictionary<string, FieldInfo> ExtractFromRegionTemplates(DespachoContext ctx)
        {
            var result = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PROCESSO_ADMINISTRATIVO","PROCESSO_JUDICIAL","VARA","COMARCA",
                "PROMOVENTE","PROMOVIDO","PERITO","CPF_PERITO","ESPECIALIDADE",
                "ESPECIE_DA_PERICIA","VALOR_ARBITRADO_JZ","VALOR_ARBITRADO_DE",
                "VALOR_ARBITRADO_CM","VALOR_TABELADO_ANEXO_I","ADIANTAMENTO",
                "PERCENTUAL","PARCELA","DATA","ASSINANTE","NUM_PERITO"
            };

            var certAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "VALOR_ARBITRADO_CM","DATA","ADIANTAMENTO","PERCENTUAL","PARCELA"
            };

            foreach (var region in ctx.Regions)
            {
                var isBottom = region.Name.StartsWith("last_bottom", StringComparison.OrdinalIgnoreCase) ||
                               region.Name.Equals("second_bottom", StringComparison.OrdinalIgnoreCase);
                var isCertidao = region.Name.StartsWith("certidao_", StringComparison.OrdinalIgnoreCase);
                var templates = region.Name == "first_top"
                    ? ctx.Config.TemplateRegions.FirstPageTop.Templates
                    : isBottom
                        ? ctx.Config.TemplateRegions.LastPageBottom.Templates
                        : region.Name == "certidao_full"
                            ? ctx.Config.TemplateRegions.CertidaoFull.Templates
                            : region.Name == "certidao_value_date"
                                ? ctx.Config.TemplateRegions.CertidaoValueDate.Templates
                                : new List<string>();

                foreach (var template in templates)
                {
                    var hits = ApplyTemplate(region, template);
                    foreach (var kv in hits)
                    {
                        if (isCertidao)
                        {
                            if (!certAllowed.Contains(kv.Key)) continue;
                        }
                        else
                        {
                            if (!allowed.Contains(kv.Key)) continue;
                        }
                        if (!result.ContainsKey(kv.Key) || kv.Value.Confidence > result[kv.Key].Confidence)
                            result[kv.Key] = kv.Value;
                    }
                }
            }

            return result;
        }

        private Dictionary<string, FieldInfo> ApplyTemplate(RegionSegment region, string template)
        {
            var output = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(template) || string.IsNullOrWhiteSpace(region.Text)) return output;

            var diffHits = ApplyTemplateDiff(region, template);
            if (diffHits.Count > 0)
                return diffHits;

            var placeholder = new Regex(@"\{\{\s*([A-Z0-9_]+)\s*\}\}", RegexOptions.IgnoreCase);
            var fields = new List<string>();
            var pattern = new System.Text.StringBuilder();
            int last = 0;
            var matches = placeholder.Matches(template);
            int matchIndex = 0;
            foreach (Match m in matches)
            {
                var literal = template.Substring(last, m.Index - last);
                pattern.Append(BuildLooseLiteralPattern(literal));
                var isLast = matchIndex == matches.Count - 1;
                var remainingLiteral = template.Substring(m.Index + m.Length);
                if (isLast && string.IsNullOrWhiteSpace(remainingLiteral))
                    pattern.Append("(.+)");
                else
                    pattern.Append("(.+?)");
                fields.Add(m.Groups[1].Value.Trim().ToUpperInvariant());
                last = m.Index + m.Length;
                matchIndex++;
            }
            pattern.Append(BuildLooseLiteralPattern(template.Substring(last)));

            var rxPattern = pattern.ToString();
            rxPattern = Regex.Replace(rxPattern, "\\\\s\\+", "\\\\s+");
            rxPattern = Regex.Replace(rxPattern, "\\s+", "\\\\s+");
            var rx = new Regex(rxPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var match = rx.Match(region.Text);
            if (!match.Success) return output;

            var templateCore = placeholder.Replace(template, "");
            var score = DiffPlexMatcher.Similarity(TextUtils.NormalizeForMatch(templateCore), TextUtils.NormalizeForMatch(region.Text));
            var conf = Math.Max(0.65, Math.Min(0.92, 0.65 + 0.35 * score));

            for (int i = 0; i < fields.Count; i++)
            {
                var group = match.Groups[i + 1];
                if (!group.Success) continue;
                var raw = group.Value.Trim();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var value = NormalizeTemplateValue(fields[i], raw);
                var bbox = ComputeMatchBBox(region.Text, region.Words, group.Index, group.Length) ?? region.BBox;
                var fieldInfo = BuildField(value, conf, $"template_region:{region.Name}", null, TextUtils.SafeSnippet(region.Text, Math.Max(0, group.Index - 40), Math.Min(region.Text.Length - Math.Max(0, group.Index - 40), 160)), bbox, region.Page1);
                output[fields[i]] = fieldInfo;
            }

            return output;
        }

        private Dictionary<string, FieldInfo> ApplyTemplateDiff(RegionSegment region, string template)
        {
            var output = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(template)) return output;

            var (collapsedText, spans) = BuildCollapsedTextWithSpans(region.Words);
            if (string.IsNullOrWhiteSpace(collapsedText)) return output;

            var textNorm = TextUtils.NormalizeForDiff(collapsedText);
            if (textNorm.Length != collapsedText.Length)
                textNorm = collapsedText.ToLowerInvariant();

            var placeholder = new Regex(@"\{\{\s*([A-Z0-9_]+)\s*\}\}", RegexOptions.IgnoreCase);
            var segments = new List<(string literal, string? field)>();
            int last = 0;
            foreach (Match m in placeholder.Matches(template))
            {
                var literal = template.Substring(last, m.Index - last);
                var field = m.Groups[1].Value.Trim().ToUpperInvariant();
                segments.Add((literal, field));
                last = m.Index + m.Length;
            }
            var tail = template.Substring(last);
            segments.Add((tail, null));

            var literalPositions = new List<(int idx, int len)>();
            int searchStart = 0;
            int anchorsFound = 0;
            int anchorsTotal = 0;
            bool allAnchorsFound = true;
            foreach (var seg in segments)
            {
                var lit = seg.literal;
                if (string.IsNullOrWhiteSpace(lit))
                {
                    literalPositions.Add((-1, 0));
                    continue;
                }
                anchorsTotal++;
                var litCollapsed = TextUtils.CollapseSpacedLettersText(lit);
                var litNorm = TextUtils.NormalizeForDiff(litCollapsed);
                if (litNorm.Length == 0)
                {
                    literalPositions.Add((-1, 0));
                    continue;
                }
                if (litNorm.Length != litCollapsed.Length)
                    litNorm = litCollapsed.ToLowerInvariant();

                var idx = _dmpField.match_main(textNorm, litNorm, searchStart);
                if (idx < 0)
                {
                    literalPositions.Add((-1, 0));
                    allAnchorsFound = false;
                    continue;
                }
                anchorsFound++;
                literalPositions.Add((idx, litNorm.Length));
                searchStart = Math.Min(textNorm.Length, idx + litNorm.Length);
            }

            if (anchorsFound == 0 || !allAnchorsFound)
                return output;

            for (int i = 0; i < segments.Count; i++)
            {
                var field = segments[i].field;
                if (string.IsNullOrWhiteSpace(field)) continue;

                var prevLit = literalPositions[i];
                var nextLit = (i + 1 < literalPositions.Count) ? literalPositions[i + 1] : (-1, 0);
                var start = prevLit.Item1 >= 0 ? prevLit.Item1 + prevLit.Item2 : 0;
                var end = nextLit.Item1 >= 0 ? nextLit.Item1 : collapsedText.Length;
                if (end < start) continue;
                var rawWords = GetWordsFromSpans(spans, start, end - start);
                var rawValue = rawWords.Count > 0
                    ? TextUtils.NormalizeWhitespace(string.Join(" ", rawWords.Select(w => w.Text)))
                    : collapsedText.Substring(start, end - start).Trim();
                if (string.IsNullOrWhiteSpace(rawValue)) continue;

                if (!TryNormalizeTemplateValue(field, rawValue, out var normalized))
                    continue;

                var conf = Math.Min(0.95, 0.65 + (anchorsFound > 0 ? (anchorsFound / Math.Max(1.0, anchorsTotal)) * 0.25 : 0));
                var info = BuildFieldFromSpanWithSpans(normalized, conf, $"template_diff_region:{region.Name}", collapsedText, start, end - start, spans, region.Page1);
                output[field] = info;
            }

            return output;
        }

        private List<WordInfo> GetWordsFromSpans(List<(WordInfo word, int start, int end)> spans, int matchIndex, int matchLength)
        {
            if (spans == null || spans.Count == 0) return new List<WordInfo>();
            if (matchIndex < 0 || matchLength <= 0) return new List<WordInfo>();
            var matchEnd = matchIndex + matchLength;
            return spans.Where(s => s.start < matchEnd && s.end > matchIndex)
                        .Select(s => s.word)
                        .ToList();
        }

        private bool TryNormalizeTemplateValue(string field, string raw, out string normalized)
        {
            normalized = "";
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var value = TextUtils.NormalizeWhitespace(raw);
            if (TextUtils.ComputeWeirdSpacingRatio(value) >= 0.12)
                value = TextUtils.CollapseSpacedLettersText(value);
            value = TextUtils.NormalizeWhitespace(TextUtils.FixMissingSpaces(value));

            if (field.Equals("PROCESSO_ADMINISTRATIVO", StringComparison.OrdinalIgnoreCase))
            {
                var m = _sei.Match(value);
                if (!m.Success) m = _adme.Match(value);
                if (!m.Success) return false;
                normalized = m.Value.Trim();
                return true;
            }

            if (field.Equals("PROCESSO_JUDICIAL", StringComparison.OrdinalIgnoreCase))
            {
                var m = _cnj.Match(value);
                if (!m.Success) return false;
                normalized = m.Value.Trim();
                return true;
            }

            if (field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase))
            {
                var m = _cpf.Match(value);
                if (!m.Success) return false;
                normalized = m.Value.Trim();
                return true;
            }

            if (field.StartsWith("VALOR_", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("ADIANTAMENTO", StringComparison.OrdinalIgnoreCase))
            {
                var m = _money.Match(value);
                if (!m.Success) return false;
                normalized = m.Value.Trim();
                return true;
            }

            if (field.Equals("DATA", StringComparison.OrdinalIgnoreCase))
            {
                if (TextUtils.TryParseDate(value, out var iso))
                {
                    normalized = iso;
                    return true;
                }
                return false;
            }

            if (field.Equals("NUM_PERITO", StringComparison.OrdinalIgnoreCase))
            {
                var digits = new string(value.Where(char.IsDigit).ToArray());
                if (digits.Length < 6) return false;
                normalized = digits;
                return true;
            }

            if (field.Equals("ASSINANTE", StringComparison.OrdinalIgnoreCase))
            {
                var cleaned = CleanPersonName(value);
                if (string.IsNullOrWhiteSpace(cleaned)) return false;
                if (!LooksLikeAssinante(cleaned)) return false;
                var norm = TextUtils.NormalizeForMatch(cleaned);
                if (norm.Contains("documento") || norm.Contains("assin"))
                    return false;
                normalized = cleaned;
                return true;
            }

            if (field is "PROMOVENTE" or "PROMOVIDO")
            {
                var cleaned = CleanPartyName(value);
                if (!LooksLikePartyName(cleaned)) return false;
                if (IsInstitutionalPartyValue(cleaned)) return false;
                normalized = cleaned;
                return true;
            }

            if (field is "PERITO" or "ESPECIALIDADE" or "VARA" or "COMARCA")
            {
                if (value.Length > 140) return false;
                normalized = TextUtils.NormalizeWhitespace(value);
                return !string.IsNullOrWhiteSpace(normalized);
            }

            return false;
        }

        private string NormalizeTemplateValue(string field, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            var v = TextUtils.NormalizeWhitespace(raw);
            if (TextUtils.ComputeWeirdSpacingRatio(v) >= 0.12)
                v = TextUtils.CollapseSpacedLettersText(v);
            v = TextUtils.NormalizeWhitespace(TextUtils.FixMissingSpaces(v));
            if (field.Equals("ASSINANTE", StringComparison.OrdinalIgnoreCase))
            {
                var cleaned = CleanPersonName(v);
                return LooksLikeAssinante(cleaned) ? cleaned : "-";
            }
            if (field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase))
            {
                var mcpf = _cpf.Match(v);
                if (mcpf.Success) return TextUtils.NormalizeCpf(mcpf.Value);
                var digits = new string(v.Where(char.IsDigit).ToArray());
                if (digits.Length >= 11) return digits.Substring(0, 11);
                return digits;
            }
            if (field.Equals("DATA", StringComparison.OrdinalIgnoreCase))
            {
                var m = _datePt.Match(v);
                if (!m.Success) m = _dateSlash.Match(v);
                if (!m.Success) m = Regex.Match(v, @"\d{1,2}de[A-Za-z]+de\d{4}");
                if (m.Success)
                {
                    var rawDate = m.Value;
                    if (!rawDate.Contains(" "))
                    {
                        rawDate = Regex.Replace(rawDate, @"(\d{1,2})de([A-Za-z]+)de(\d{4})", "$1 de $2 de $3", RegexOptions.IgnoreCase);
                    }
                    if (TextUtils.TryParseDate(rawDate, out var iso)) return iso;
                }
                if (TextUtils.TryParseDate(v, out var iso2)) return iso2;
                return v;
            }
            if (field.StartsWith("VALOR_", StringComparison.OrdinalIgnoreCase) || field == "ADIANTAMENTO")
            {
                var m = _money.Match(v);
                if (!m.Success)
                    m = Regex.Match(v, @"\d{1,3}(?:\.\d{3})*,\d{2}");
                if (m.Success) return TextUtils.NormalizeMoney(m.Value);
                return TextUtils.NormalizeMoney(v);
            }
            if (field.Equals("ASSINANTE", StringComparison.OrdinalIgnoreCase))
            {
                var cut = CutAtKeywords(v, new[] { "Diretor", "Diretora", "Diretor(a)", "," });
                return cut;
            }
            if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) || field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase) || field.Equals("PERITO", StringComparison.OrdinalIgnoreCase))
            {
                var cut = CutAtKeywords(v, new[] { "CPF", "CNPJ", "EM FACE", "PERANTE", "PERANTE O" });
                return cut;
            }
            if (field.Equals("ESPECIALIDADE", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(v, @"(?i)(grafot[eé]cnico|m[eé]dic[oa]|cont[aá]bil|engenh[aá]ria|psicol[oó]gic[oa])");
                if (m.Success) return m.Value;
            }
            if (field.StartsWith("PROCESSO_", StringComparison.OrdinalIgnoreCase))
            {
                var m1 = _cnj.Match(v);
                if (m1.Success) return m1.Value;
                var m2 = _sei.Match(v);
                if (m2.Success) return m2.Value;
                var m3 = _adme.Match(v);
                if (m3.Success) return m3.Value;
            }
            return v;
        }

        private string CutAtKeywords(string value, string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var v = value;
            var idx = -1;
            foreach (var k in keywords)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                var i = v.IndexOf(k, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && (idx == -1 || i < idx))
                    idx = i;
            }
            if (idx > 0) v = v.Substring(0, idx);
            v = v.Trim();
            v = v.TrimEnd(',', ';', '.', '-', '–');
            return v.Trim();
        }

        private string BuildLooseLiteralPattern(string literal)
        {
            if (string.IsNullOrEmpty(literal)) return "";
            var sb = new System.Text.StringBuilder();
            bool lastWasWs = false;
            foreach (var ch in literal)
            {
                if (char.IsWhiteSpace(ch))
                {
                    if (!lastWasWs)
                        sb.Append("\\s*");
                    lastWasWs = true;
                    continue;
                }

                sb.Append(Regex.Escape(ch.ToString()));
                sb.Append("\\s*");
                lastWasWs = false;
            }

            var pat = sb.ToString();
            if (pat.EndsWith("\\s*"))
                pat = pat.Substring(0, pat.Length - 3);
            return pat;
        }

        private FieldInfo ExtractProcessoAdministrativo(DespachoContext ctx)
        {
            var templates = MergeTemplates(_cfg.Fields.ProcessoAdministrativo.Templates, BuildTemplates(_cfg.Priorities.ProcessoAdminLabels));
            var cand = _template.ExtractFromParagraphs(ctx.Paragraphs.Take(8), templates, _sei);
            if (cand == null)
                cand = _template.ExtractFromParagraphs(ctx.Paragraphs.Take(8), templates, _adme);

            if (cand != null)
            {
                return BuildFieldFromSpan(cand.Value, 0.90, "template_dmp", cand.Paragraph, cand.MatchIndex, cand.MatchLength, cand.Snippet);
            }

            foreach (var p in ctx.Paragraphs.Take(12))
            {
                var pNorm = TextUtils.NormalizeForMatch(p.Text);
                if (!_cfg.Priorities.ProcessoAdminLabels.Any(l => pNorm.Contains(TextUtils.NormalizeForMatch(l))))
                    continue;

                var m1 = _sei.Match(p.Text);
                if (m1.Success)
                    return BuildFieldFromMatch(m1.Value, 0.75, "regex", p, m1, 0, Snip(p.Text, m1));
                var m2 = _adme.Match(p.Text);
                if (m2.Success)
                    return BuildFieldFromMatch(m2.Value, 0.7, "regex", p, m2, 0, Snip(p.Text, m2));
            }

            foreach (var band in ctx.BandSegments)
            {
                if (band == null || string.IsNullOrWhiteSpace(band.Text)) continue;
                var bNorm = TextUtils.NormalizeForMatch(band.Text);
                if (!(bNorm.Contains("processo") || bNorm.Contains("sei") || bNorm.Contains("adme") || bNorm.Contains("ci")))
                    continue;

                var m1 = _sei.Match(band.Text);
                if (m1.Success)
                    return BuildFieldFromBandMatch(m1.Value, 0.7, $"regex_band:{band.Band}", band, m1, 0, Snip(band.Text, m1));
                var m2 = _adme.Match(band.Text);
                if (m2.Success)
                    return BuildFieldFromBandMatch(m2.Value, 0.65, $"regex_band:{band.Band}", band, m2, 0, Snip(band.Text, m2));
            }

            var fn = ctx.FileName ?? "";
            var mf = _sei.Match(fn);
            if (!mf.Success) mf = _adme.Match(fn);
            if (mf.Success)
                return BuildField(mf.Value, 0.35, "filename_fallback", null, mf.Value);

            return NotFound();
        }

        private FieldInfo ExtractProcessoJudicial(DespachoContext ctx)
        {
            var templates = MergeTemplates(_cfg.Fields.ProcessoJudicial.Templates, new List<string> { "Processo Judicial: {{value}}" });
            var cand = _template.ExtractFromParagraphs(ctx.Paragraphs.Take(10), templates, _cnj);
            if (cand != null)
                return BuildFieldFromSpan(cand.Value, 0.85, "template_dmp", cand.Paragraph, cand.MatchIndex, cand.MatchLength, cand.Snippet);

            FieldInfo best = NotFound();
            double bestScore = 0;
            var firstBody = ctx.BandSegments.FirstOrDefault(b =>
                b.Page1 == ctx.StartPage1 && string.Equals(b.Band, "body", StringComparison.OrdinalIgnoreCase));
            if (firstBody != null && firstBody.Words != null && firstBody.Words.Count > 0)
            {
                var (collapsedBody, spansBody) = BuildCollapsedTextWithSpans(firstBody.Words);
                var mBody = _cnj.Match(collapsedBody);
                if (!mBody.Success)
                    mBody = _cnjLoose.Match(collapsedBody);
                if (mBody.Success)
                {
                    return BuildFieldFromSpanWithSpans(mBody.Value, 0.75, "regex_band:body", collapsedBody, mBody.Index, mBody.Length, spansBody, firstBody.Page1);
                }
            }
            foreach (var p in ctx.Paragraphs)
            {
                var (collapsed, spans) = BuildCollapsedTextWithSpans(p.Words ?? new List<WordInfo>());
                var m = _cnj.Match(collapsed);
                if (!m.Success)
                    m = _cnjLoose.Match(collapsed);
                if (!m.Success) continue;
                var score = 0.6;
                var norm = TextUtils.NormalizeForMatch(collapsed);
                if (norm.Contains("vara") || norm.Contains("comarca") || norm.Contains("processo judicial"))
                    score += 0.2;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = BuildFieldFromSpanWithSpans(m.Value, score, "regex", collapsed, m.Index, m.Length, spans, p.Page1);
                }
            }

            if (best.Confidence > 0)
                return best;

            foreach (var r in ctx.Regions.Where(r =>
                         r.Name.StartsWith("last_bottom", StringComparison.OrdinalIgnoreCase) ||
                         r.Name.Equals("second_bottom", StringComparison.OrdinalIgnoreCase)))
            {
                var text = r.Text ?? "";
                if (string.IsNullOrWhiteSpace(text)) continue;
                var collapsed = TextUtils.CollapseSpacedLettersText(text);
                var norm = TextUtils.NormalizeForMatch(collapsed);
                if (!(norm.Contains("processo") || norm.Contains("autos")))
                    continue;
                var m = _cnj.Match(collapsed);
                if (!m.Success)
                    m = _cnjLoose.Match(collapsed);
                if (m.Success)
                {
                    return BuildFieldFromRegionMatch(m.Value, 0.72, "regex_region", r, m, 0, Snip(text, m));
                }
            }

            var fn = ctx.FileName ?? "";
            var mf = _cnj.Match(fn);
            if (mf.Success)
                return BuildField(mf.Value, 0.35, "filename_fallback", null, mf.Value);

            return NotFound();
        }

        private (FieldInfo vara, FieldInfo comarca) ExtractVaraComarca(DespachoContext ctx)
        {
            FieldInfo vara = NotFound();
            FieldInfo comarca = NotFound();

            foreach (var p in ctx.Paragraphs.Take(15))
            {
                var text = p.Text ?? "";
                var norm = TextUtils.NormalizeForMatch(text);

                if (vara.Method == "not_found" && _cfg.Priorities.VaraLabels.Any(l => norm.Contains(TextUtils.NormalizeForMatch(l))))
                {
                    var m = MatchAny(text, FieldRegexCatalog.Get("vara_line"));
                    if (!m.Success)
                        m = Regex.Match(text, @"(?i)\bvara\b\s*[:\-]?\s*([^\n;]+)");
                    if (m.Success)
                        vara = BuildFieldFromMatch(m.Groups[1].Value.Trim(), 0.7, "regex", p, m, 1, Snip(text, m));
                }

                if (comarca.Method == "not_found" && _cfg.Priorities.ComarcaLabels.Any(l => norm.Contains(TextUtils.NormalizeForMatch(l))))
                {
                    var m = MatchAny(text, FieldRegexCatalog.Get("comarca_line"));
                    if (!m.Success)
                        m = Regex.Match(text, @"(?i)\bcomarca\b\s*[:\-]?\s*([^\n;]+)");
                    if (m.Success)
                        comarca = BuildFieldFromMatch(m.Groups[1].Value.Trim(), 0.7, "regex", p, m, 1, Snip(text, m));
                }

                if (vara.Method != "not_found" && comarca.Method != "not_found")
                    break;
            }

            return (vara, comarca);
        }

        private (FieldInfo promovente, FieldInfo promovido) ExtractPromoventePromovido(DespachoContext ctx)
        {
            FieldInfo promovente = NotFound();
            FieldInfo promovido = NotFound();

            foreach (var p in ctx.Paragraphs)
            {
                var (ordered, text) = PrepareMatchText(p.Words);
                var (autorMatch, reuMatch) = FindPartiesMatches(text);
                if (autorMatch != null && autorMatch.Success)
                {
                    var p1 = CleanPartyName(autorMatch.Groups[1].Value);
                    if (LooksLikePartyName(p1))
                        promovente = BuildFieldFromWordsMatch(p1, 0.7, "regex", ordered, p.Page1, text, autorMatch, 1);
                }
                if (reuMatch != null && reuMatch.Success)
                {
                    var p2 = CleanPartyName(reuMatch.Groups[1].Value);
                    if (LooksLikePartyName(p2))
                        promovido = BuildFieldFromWordsMatch(p2, 0.7, "regex", ordered, p.Page1, text, reuMatch, 1);
                }
                if (promovente.Method != "not_found" && promovido.Method != "not_found")
                    return (promovente, promovido);
            }

            foreach (var p in ctx.Paragraphs)
            {
                if (promovente.Method == "not_found")
                {
                    var m = MatchAny(p.Text, FieldRegexCatalog.Get("promovente_label"));
                    if (m.Success)
                    {
                        var label = m.Groups.Count > 1 ? (m.Groups[1].Value ?? "") : "";
                        var val = CleanPartyName(m.Groups.Count > 2 ? m.Groups[2].Value : m.Value);
                        if (IsInstitutionalRequester(label, val))
                            continue;
                        if (LooksLikePartyName(val))
                            promovente = BuildFieldFromMatch(val, 0.65, "regex", p, m, 2, Snip(p.Text, m));
                    }
                }
                if (promovido.Method == "not_found")
                {
                    var m = MatchAny(p.Text, FieldRegexCatalog.Get("promovido_label"));
                    if (m.Success)
                    {
                        var val = CleanPartyName(m.Groups.Count > 2 ? m.Groups[2].Value : m.Value);
                        if (LooksLikePartyName(val))
                            promovido = BuildFieldFromMatch(val, 0.65, "regex", p, m, 2, Snip(p.Text, m));
                    }
                }
                if (promovente.Method != "not_found" && promovido.Method != "not_found") break;
            }

            if (promovente.Method == "not_found" || promovido.Method == "not_found")
            {
                var regions = ctx.Regions.Where(r =>
                    r.Name.StartsWith("last_bottom", StringComparison.OrdinalIgnoreCase) ||
                    r.Name.Equals("second_bottom", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var r in regions)
                {
                    var text = r.Text ?? "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var (autorMatch, reuMatch) = FindPartiesMatches(text);
                    if (autorMatch != null && autorMatch.Success)
                    {
                        var p1 = CleanPartyName(autorMatch.Groups[1].Value);
                        if (promovente.Method == "not_found" && LooksLikePartyName(p1))
                            promovente = BuildFieldFromRegionMatch(p1, 0.7, "regex_region", r, autorMatch, 1, Snip(text, autorMatch));
                    }
                    if (reuMatch != null && reuMatch.Success)
                    {
                        var p2 = CleanPartyName(reuMatch.Groups[1].Value);
                        if (promovido.Method == "not_found" && LooksLikePartyName(p2))
                            promovido = BuildFieldFromRegionMatch(p2, 0.7, "regex_region", r, reuMatch, 1, Snip(text, reuMatch));
                    }
                    if (promovente.Method != "not_found" && promovido.Method != "not_found") break;

                    if (promovido.Method == "not_found")
                    {
                        var m2 = MatchAny(text, FieldRegexCatalog.Get("em_face"));
                        if (m2.Success)
                        {
                            var p2 = CleanPartyName(m2.Groups.Count > 1 ? m2.Groups[1].Value : m2.Value);
                            if (LooksLikePartyName(p2))
                                promovido = BuildFieldFromRegionMatch(p2, 0.65, "regex_region", r, m2, 1, Snip(text, m2));
                        }
                    }
                }
            }

            if (promovente.Method == "not_found" || promovido.Method == "not_found")
            {
                foreach (var band in ctx.BandSegments.Where(b => string.Equals(b.Band, "body", StringComparison.OrdinalIgnoreCase) ||
                                                                string.Equals(b.Band, "footer", StringComparison.OrdinalIgnoreCase)))
                {
                    var (ordered, text) = PrepareMatchText(band.Words);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var (autorMatch, reuMatch) = FindPartiesMatches(text);
                    if (autorMatch != null && autorMatch.Success)
                    {
                        var p1 = CleanPartyName(autorMatch.Groups[1].Value);
                        if (promovente.Method == "not_found" && LooksLikePartyName(p1))
                            promovente = BuildFieldFromWordsMatch(p1, 0.65, "regex_band", ordered, band.Page1, text, autorMatch, 1);
                    }
                    if (reuMatch != null && reuMatch.Success)
                    {
                        var p2 = CleanPartyName(reuMatch.Groups[1].Value);
                        if (promovido.Method == "not_found" && LooksLikePartyName(p2))
                            promovido = BuildFieldFromWordsMatch(p2, 0.65, "regex_band", ordered, band.Page1, text, reuMatch, 1);
                    }
                    if (promovente.Method != "not_found" && promovido.Method != "not_found")
                        break;
                }
            }

            return (promovente, promovido);
        }

        private (Match? autor, Match? reu) FindPartiesMatches(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return (null, null);
            var markers = ProcessoPartesMarkers.Load();
            var movid = BuildAlternation(markers.PromoventeStart);
            var face = BuildAlternation(markers.PromoventeCut);
            var reuStart = BuildAlternation(markers.PromovidoStart);
            var perante = @"p\s*e\s*r\s*a\s*n\s*t\s*e";
            var juizo = @"j\s*u\s*(?:i|í)\s*z\s*o";
            if (string.IsNullOrWhiteSpace(movid) || string.IsNullOrWhiteSpace(reuStart))
                return (null, null);

            var autor = Regex.Match(text,
                $@"(?i)(?:{movid})\s+(.+?)(?=(?:\s+(?:{face})|\s+{perante}|\s+{juizo}|[\\.;\\n]))",
                RegexOptions.Singleline);
            var reu = Regex.Match(text,
                $@"(?i)(?:{reuStart})\s+(.+?)(?=(?:,|\\n|\\.|;|\\s+{perante}|\\s+{juizo}|$))",
                RegexOptions.Singleline);
            return (autor.Success ? autor : null, reu.Success ? reu : null);
        }

        private (FieldInfo perito, FieldInfo cpf, FieldInfo especialidade, FieldInfo especie) ExtractPeritoCpfEspecialidade(DespachoContext ctx, Dictionary<string, FieldInfo> existing)
        {
            FieldInfo perito = NotFound();
            FieldInfo cpf = NotFound();
            FieldInfo especialidade = NotFound();
            FieldInfo especie = NotFound();

            var peritoTemplates = MergeTemplates(_cfg.Fields.Perito.Templates, new List<string> { "Interessado: {{value}}", "Perito: {{value}}", "Perita: {{value}}" });
            var peritoRx = FieldRegexCatalog.GetFirst("interessado");
            var peritoCand = peritoRx == null ? null : _template.ExtractFromParagraphs(ctx.Paragraphs, peritoTemplates, peritoRx);
            if (peritoCand != null)
            {
                var trimmed = TrimPeritoName(peritoCand.Value);
                if (!string.IsNullOrWhiteSpace(trimmed))
                    perito = BuildFieldFromSpan(trimmed, 0.75, "template_dmp", peritoCand.Paragraph, peritoCand.MatchIndex, peritoCand.MatchLength, peritoCand.Snippet);
            }

            foreach (var p in ctx.Paragraphs)
            {
                var m = MatchAny(p.Text, FieldRegexCatalog.Get("interessado"));
                if (m.Success)
                {
                    var trimmed = TrimPeritoName((m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim());
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        perito = BuildFieldFromMatch(trimmed, 0.75, "regex", p, m, 1, Snip(p.Text, m));
                    break;
                }
            }

            if (perito.Method == "not_found")
            {
                foreach (var p in ctx.Paragraphs)
                {
                    var m = MatchAny(p.Text, FieldRegexCatalog.Get("perito_em"));
                    if (m.Success)
                    {
                        var trimmed = TrimPeritoName((m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim());
                        if (!string.IsNullOrWhiteSpace(trimmed))
                            perito = BuildFieldFromMatch(trimmed, 0.7, "regex", p, m, 1, Snip(p.Text, m));
                        break;
                    }
                }
            }
            if (perito.Method == "not_found")
            {
                foreach (var p in ctx.Paragraphs)
                {
                    var m = MatchAny(p.Text, FieldRegexCatalog.Get("perito_nome"));
                    if (m.Success)
                    {
                        var trimmed = TrimPeritoName((m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim());
                        if (!string.IsNullOrWhiteSpace(trimmed))
                            perito = BuildFieldFromMatch(trimmed, 0.7, "regex", p, m, 1, Snip(p.Text, m));
                        break;
                    }
                }
            }

            var cpfTemplates = MergeTemplates(_cfg.Fields.CpfPerito.Templates, new List<string> { "CPF: {{value}}" });
            var cpfRx = FieldRegexCatalog.GetFirst("cpf_line");
            var cpfCand = cpfRx == null ? null : _template.ExtractFromParagraphs(ctx.Paragraphs, cpfTemplates, cpfRx);
            if (cpfCand != null)
                cpf = BuildFieldFromSpan(TextUtils.NormalizeCpf(cpfCand.Value), 0.75, "template_dmp", cpfCand.Paragraph, cpfCand.MatchIndex, cpfCand.MatchLength, cpfCand.Snippet);

            foreach (var p in ctx.Paragraphs)
            {
                var m = MatchAny(p.Text, FieldRegexCatalog.Get("cpf_line"));
                if (!m.Success)
                    continue;
                var raw = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                var cpfNorm = TextUtils.NormalizeCpf(raw);
                if (string.IsNullOrWhiteSpace(cpfNorm))
                    continue;
                cpf = BuildFieldFromMatch(cpfNorm, 0.75, "regex", p, m, 1, Snip(p.Text, m));
                break;
            }

            var espTemplates = MergeTemplates(_cfg.Fields.Especialidade.Templates, new List<string> { "Perito em {{value}}", "Perito - {{value}}" });
            var espRx = FieldRegexCatalog.GetFirst("perito_em");
            var espCand = espRx == null ? null : _template.ExtractFromParagraphs(ctx.Paragraphs, espTemplates, espRx);
            if (espCand != null)
                especialidade = BuildFieldFromSpan(espCand.Value, 0.7, "template_dmp", espCand.Paragraph, espCand.MatchIndex, espCand.MatchLength, espCand.Snippet);

            foreach (var p in ctx.Paragraphs)
            {
                var m = MatchAny(p.Text, FieldRegexCatalog.Get("perito_em"));
                if (m.Success)
                {
                    var trimmed = (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
                    especialidade = BuildFieldFromMatch(trimmed, 0.7, "regex", p, m, 1, Snip(p.Text, m));
                    break;
                }
            }

            foreach (var p in ctx.Paragraphs)
            {
                var m = MatchAny(p.Text, FieldRegexCatalog.Get("pericia_especie"));
                if (m.Success)
                {
                    var esp = (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
                    var espNorm = TextUtils.NormalizeForMatch(esp);
                    if (!IsWeakEspecieToken(espNorm))
                    {
                        especie = BuildFieldFromMatch(esp, 0.7, "regex", p, m, 1, Snip(p.Text, m));
                        break;
                    }
                }
            }

            if (especie.Method == "not_found" && especialidade.Method != "not_found")
            {
                var esp = InferEspecie(especialidade.Value);
                if (!string.IsNullOrWhiteSpace(esp))
                    especie = BuildField(esp, 0.55, "heuristic", null, especialidade.Value);
            }

            return (perito, cpf, especialidade, especie);
        }

        private (FieldInfo valorJz, FieldInfo valorDe, FieldInfo valorCm, FieldInfo valorTabela) ExtractValores(DespachoContext ctx)
        {
            FieldInfo valorJz = NotFound();
            FieldInfo valorDe = NotFound();
            FieldInfo valorCm = NotFound();
            FieldInfo valorTabela = NotFound();

            var tipo = DetectDespachoTipo(ctx);
            var firstPage = ctx.StartPage1;
            var secondPage = ctx.StartPage1 + 1;
            var lastPage = ctx.EndPage1;
            var firstParas = ctx.Paragraphs.Where(p => p.Page1 == firstPage).ToList();
            var secondParas = ctx.Paragraphs.Where(p => p.Page1 == secondPage).ToList();
            var lastParas = ctx.Paragraphs.Where(p => p.Page1 == lastPage).ToList();
            var secondBody = ctx.BandSegments
                .Where(b => b.Page1 == secondPage && string.Equals(b.Band, "body", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.Words?.Count ?? 0)
                .ThenByDescending(b => (b.Text ?? "").Length)
                .FirstOrDefault();
            var firstBody = ctx.BandSegments
                .Where(b => b.Page1 == firstPage && string.Equals(b.Band, "body", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.Words?.Count ?? 0)
                .ThenByDescending(b => (b.Text ?? "").Length)
                .FirstOrDefault();
            var lastBody = ctx.BandSegments
                .Where(b => b.Page1 == lastPage && string.Equals(b.Band, "body", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(b => b.Words?.Count ?? 0)
                .ThenByDescending(b => (b.Text ?? "").Length)
                .FirstOrDefault();

            if (tipo != "encaminhamento_cm" && secondBody != null && valorDe.Method == "not_found")
            {
                var text = secondBody.Text ?? "";
                var patterns = _cfg.DespachoType.DeValuePatterns ?? new List<string>();
                if (patterns.Count == 0)
                    patterns.Add(@"(?i)proceder\s*(?:à|a)?\s*reserva\s+orçament[aá]ria[^\d]{0,120}?(R\$\s*\d{1,3}(?:\.\d{3})*,\d{2})");
                foreach (var pat in patterns)
                {
                    var m = Regex.Match(text, pat);
                    if (!m.Success) continue;
                    var raw = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                    var money = TextUtils.NormalizeMoney(raw);
                    if (string.IsNullOrWhiteSpace(money))
                    {
                        var mm = _money.Match(m.Value);
                        if (mm.Success) money = TextUtils.NormalizeMoney(mm.Value);
                    }
                    if (!string.IsNullOrWhiteSpace(money))
                    {
                        valorDe = BuildFieldFromBandMatch(money, 0.85, "regex_band:de_georc", secondBody, m, 1, Snip(text, m));
                        break;
                    }
                }

                if (valorDe.Method == "not_found" && secondBody.Words != null && secondBody.Words.Count > 0)
                {
                    var (collapsed, spans) = BuildCollapsedTextWithSpans(secondBody.Words);
                    var matches = _money.Matches(collapsed);
                    if (matches.Count > 0)
                    {
                        var m = matches[matches.Count - 1];
                        var money = TextUtils.NormalizeMoney(m.Value);
                        if (!string.IsNullOrWhiteSpace(money))
                        {
                            var bbox = ComputeMatchBBox(spans, m.Index, m.Length) ?? secondBody.BBox;
                            valorDe = BuildField(money, 0.65, "heuristic:second_bottom_last_money", null, Snip(collapsed, m), bbox, secondBody.Page1);
                        }
                    }
                    if (valorDe.Method == "not_found")
                    {
                        var numRx = new Regex(@"\b\d{1,3}(?:\.\d{3})*,\d{2}\b");
                        var numMatches = numRx.Matches(collapsed);
                        for (int i = numMatches.Count - 1; i >= 0; i--)
                        {
                            var m = numMatches[i];
                            var window = collapsed.Substring(Math.Max(0, m.Index - 80), Math.Min(collapsed.Length - Math.Max(0, m.Index - 80), 160));
                            var winNorm = TextUtils.NormalizeForMatch(window);
                            if (!(winNorm.Contains("valor") || winNorm.Contains("empenho") || winNorm.Contains("arbitrad") || winNorm.Contains("orcament") || winNorm.Contains("georc")))
                                continue;
                            var money = TextUtils.NormalizeMoney(m.Value);
                            if (string.IsNullOrWhiteSpace(money)) continue;
                            var bbox = ComputeMatchBBox(spans, m.Index, m.Length) ?? secondBody.BBox;
                            valorDe = BuildField(money, 0.6, "heuristic:second_bottom_numeric", null, Snip(window, m), bbox, secondBody.Page1);
                            break;
                        }
                    }
                }
            }
            if (tipo != "encaminhamento_cm" && valorDe.Method == "not_found")
            {
                var secondBand = ctx.Bands
                    .Where(b => b.Page1 == secondPage && string.Equals(b.Band, "body", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(b => (b.Text ?? "").Length)
                    .FirstOrDefault();
                if (secondBand != null && !string.IsNullOrWhiteSpace(secondBand.Text))
                {
                    var collapsed = TextUtils.CollapseSpacedLettersText(secondBand.Text);
                    var m = _money.Match(collapsed);
                    if (m.Success)
                    {
                        var money = TextUtils.NormalizeMoney(m.Value);
                        if (!string.IsNullOrWhiteSpace(money))
                            valorDe = BuildField(money, 0.55, "heuristic:second_band_text", null, Snip(collapsed, m), secondBand.BBoxN, secondBand.Page1);
                    }
                }
            }

            if (tipo == "autorizacao")
            {
                valorJz = FindValor(firstParas, preferArbitrado: true);
                if (valorDe.Method == "not_found")
                    valorDe = FindValor(secondParas, preferGeorc: true);
            }
            else if (tipo == "encaminhamento_cm")
            {
                valorJz = FindValor(firstParas, preferArbitrado: true);
                // valorDe permanece "-"
                valorCm = FindValor(lastParas, preferConselho: true);
            }
            else
            {
                foreach (var p in firstParas)
                {
                    foreach (Match m in _money.Matches(p.Text ?? ""))
                    {
                        var text = p.Text ?? "";
                        var norm = TextUtils.NormalizeForMatch(text);
                        var isArb = norm.Contains("arbitrado") || norm.Contains("arbitramento") || norm.Contains("honorario") || norm.Contains("honorarios");
                        if (isArb || valorJz.Method == "not_found")
                            valorJz = BuildFieldFromMatch(TextUtils.NormalizeMoney(m.Value), 0.7, "heuristic", p, m, 0, Snip(text, m));
                    }
                }

                foreach (var p in secondParas)
                {
                    foreach (Match m in _money.Matches(p.Text ?? ""))
                    {
                        var text = p.Text ?? "";
                        var norm = TextUtils.NormalizeForMatch(text);
                        if (norm.Contains("diretoria") || norm.Contains("assinad") || norm.Contains("georc") || valorDe.Method == "not_found")
                            valorDe = BuildFieldFromMatch(TextUtils.NormalizeMoney(m.Value), 0.7, "heuristic", p, m, 0, Snip(text, m));
                        if (norm.Contains("anexo i") || norm.Contains("tabelad"))
                            valorTabela = BuildFieldFromMatch(TextUtils.NormalizeMoney(m.Value), 0.65, "regex", p, m, 0, Snip(text, m));
                    }
                }

                foreach (var p in lastParas)
                {
                    foreach (Match m in _money.Matches(p.Text ?? ""))
                    {
                        var text = p.Text ?? "";
                        var norm = TextUtils.NormalizeForMatch(text);
                        if (ContainsAny(norm, ctx.Config.DespachoType.ConselhoHints))
                            valorCm = BuildFieldFromMatch(TextUtils.NormalizeMoney(m.Value), 0.8, "regex", p, m, 0, Snip(text, m));
                    }
                }
            }

            if (valorJz.Method == "not_found" && firstBody != null)
            {
                var include = new List<string> { "arbitr", "honor", "perit", "peric", "fixa", "valor" };
                var exclude = new List<string> { "reserva", "georc", "empenh", "conselho", "diretoria" };
                var hit = FindMoneyInBand(firstBody, include, exclude, "heuristic:band_first", 0.62);
                if (hit.Method != "not_found")
                    valorJz = hit;
            }

            if (valorDe.Method == "not_found" && lastBody != null)
            {
                var include = new List<string> { "reserva", "georc", "orcament", "autoriz", "encaminh", "pagamento", "empenh" };
                var exclude = new List<string> { "conselho", "certidao" };
                var hit = FindMoneyInBand(lastBody, include, exclude, "heuristic:band_last", 0.6);
                if (hit.Method != "not_found")
                    valorDe = hit;
            }

            return (Ensure(valorJz), Ensure(valorDe), Ensure(valorCm), Ensure(valorTabela));
        }

        private FieldInfo FindMoneyInBand(BandSegment band, List<string> includeHints, List<string> excludeHints, string method, double baseScore)
        {
            if (band == null || band.Words == null || band.Words.Count == 0) return NotFound();
            var (collapsed, spans) = BuildCollapsedTextWithSpans(band.Words);
            if (string.IsNullOrWhiteSpace(collapsed)) return NotFound();
            var matches = _money.Matches(collapsed);
            if (matches.Count == 0) return NotFound();

            FieldInfo best = NotFound();
            double bestScore = 0;
            foreach (Match m in matches)
            {
                var windowStart = Math.Max(0, m.Index - 100);
                var windowLen = Math.Min(collapsed.Length - windowStart, 200);
                var window = collapsed.Substring(windowStart, windowLen);
                var norm = TextUtils.NormalizeForMatch(window);
                var score = baseScore;
                if (includeHints != null && includeHints.Count > 0 && includeHints.Any(h => norm.Contains(TextUtils.NormalizeForMatch(h))))
                    score += 0.15;
                if (excludeHints != null && excludeHints.Count > 0 && excludeHints.Any(h => norm.Contains(TextUtils.NormalizeForMatch(h))))
                    score -= 0.15;
                if (score <= 0) continue;

                var money = TextUtils.NormalizeMoney(m.Value);
                if (string.IsNullOrWhiteSpace(money)) continue;
                var bbox = ComputeMatchBBox(spans, m.Index, m.Length) ?? band.BBox;
                var info = BuildField(money, Math.Min(0.85, score), method, null, Snip(window, m), bbox, band.Page1);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = info;
                }
            }
            return best.Method == "not_found" ? best : best;
        }

        private FieldInfo FindValor(List<ParagraphSegment> paragraphs, bool preferArbitrado = false, bool preferGeorc = false, bool preferConselho = false)
        {
            FieldInfo best = NotFound();
            double bestScore = 0;
            foreach (var p in paragraphs)
            {
                var text = p.Text ?? "";
                var norm = TextUtils.NormalizeForMatch(text);
                var matchText = text;
                List<(WordInfo word, int start, int end)> spans = new List<(WordInfo word, int start, int end)>();
                if (p.Words != null && p.Words.Count > 0)
                {
                    var collapsed = BuildCollapsedTextWithSpans(p.Words);
                    if (!string.IsNullOrWhiteSpace(collapsed.text))
                    {
                        matchText = collapsed.text;
                        spans = collapsed.spans;
                    }
                }
                foreach (Match m in _money.Matches(matchText))
                {
                    var score = 0.6;
                    if (preferArbitrado && (norm.Contains("arbitrado") || norm.Contains("arbitramento") || norm.Contains("honorario")))
                        score += 0.2;
                    if (preferGeorc && ContainsAny(norm, _cfg.DespachoType.GeorcHints))
                        score += 0.2;
                    if (preferConselho && ContainsAny(norm, _cfg.DespachoType.ConselhoHints))
                        score += 0.2;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        var bbox = spans.Count > 0 ? ComputeMatchBBox(spans, m.Index, m.Length) : p.BBox;
                        best = BuildField(TextUtils.NormalizeMoney(m.Value), score, "heuristic", p, Snip(matchText, m), bbox, p.Page1);
                    }
                }
            }
            return best.Method == "not_found" ? best : best;
        }

        private string DetectDespachoTipo(DespachoContext ctx)
        {
            var bottomText = string.Join(" ", ctx.Regions
                .Where(r => r.Name.Equals("second_bottom", StringComparison.OrdinalIgnoreCase) ||
                            r.Name.Equals("last_bottom", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Text));
            var bottomNorm = TextUtils.NormalizeForMatch(bottomText);
            if (ContainsAny(bottomNorm, _cfg.DespachoType.GeorcHints) || ContainsAny(bottomNorm, _cfg.DespachoType.AutorizacaoHints))
                return "autorizacao";
            var conselhoStrong = new List<string> { "encaminh", "submet", "remet", "remetam", "remessa" };
            if (ContainsAny(bottomNorm, _cfg.DespachoType.ConselhoHints) && ContainsAny(bottomNorm, conselhoStrong))
                return "encaminhamento_cm";

            var tail = string.Join(" ", ctx.Paragraphs.Where(p => p.Page1 >= Math.Max(ctx.StartPage1, ctx.EndPage1 - 1)).Select(p => p.Text));
            var norm = TextUtils.NormalizeForMatch(tail);
            if (ContainsAny(norm, _cfg.DespachoType.GeorcHints) || ContainsAny(norm, _cfg.DespachoType.AutorizacaoHints))
                return "autorizacao";
            return "indefinido";
        }

        private bool ContainsAny(string norm, List<string> hints)
        {
            foreach (var h in hints)
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                if (norm.Contains(TextUtils.NormalizeForMatch(h))) return true;
            }
            return false;
        }

        private (FieldInfo adiantamento, FieldInfo percentual, FieldInfo parcela) ExtractAdiantamentoPercentualParcela(DespachoContext ctx)
        {
            FieldInfo adiantamento = NotFound();
            FieldInfo percentual = NotFound();
            FieldInfo parcela = NotFound();

            foreach (var p in ctx.Paragraphs)
            {
                var text = p.Text ?? "";
                var norm = TextUtils.NormalizeForMatch(text);
                if (adiantamento.Method == "not_found" && norm.Contains("adiantamento"))
                {
                    var m = _money.Match(text);
                    if (m.Success)
                        adiantamento = BuildFieldFromMatch(TextUtils.NormalizeMoney(m.Value), 0.65, "regex", p, m, 0, Snip(text, m));
                }

                if (percentual.Method == "not_found")
                {
                    var m = MatchAny(text, FieldRegexCatalog.Get("percentual"));
                    if (!m.Success)
                        m = Regex.Match(text, @"\b\d{1,2}%\b");
                    if (m.Success)
                        percentual = BuildFieldFromMatch(m.Value, 0.6, "regex", p, m, 0, Snip(text, m));
                }

                if (parcela.Method == "not_found" && norm.Contains("parcela"))
                {
                    var m = MatchAny(text, FieldRegexCatalog.Get("parcela"));
                    if (!m.Success)
                        m = Regex.Match(text, @"(?i)(\d+ª\s*parcela|primeira\s*parcela|segunda\s*parcela|terceira\s*parcela)");
                    if (m.Success)
                        parcela = BuildFieldFromMatch(m.Value, 0.6, "regex", p, m, 0, Snip(text, m));
                }
            }

            return (Ensure(adiantamento), Ensure(percentual), Ensure(parcela));
        }

        private FieldInfo ExtractData(DespachoContext ctx)
        {
            var dataTemplates = MergeTemplates(_cfg.Fields.Data.Templates, new List<string> { "Documento assinado eletronicamente em {{value}}" });
            var dataCand = _template.ExtractFromParagraphs(ctx.Paragraphs, dataTemplates, _datePt);
            if (dataCand != null && TextUtils.TryParseDate(dataCand.Value, out var iso0) && IsRecentDate(iso0))
                return BuildFieldFromSpan(FormatDateBr(iso0), 0.75, "template_dmp", dataCand.Paragraph, dataCand.MatchIndex, dataCand.MatchLength, dataCand.Snippet);

            foreach (var band in ctx.BandSegments.Where(b => string.Equals(b.Band, "footer", StringComparison.OrdinalIgnoreCase)))
            {
                var footerMatch = _datePt.Match(band.Text ?? "");
                if (footerMatch.Success && TextUtils.TryParseDate(footerMatch.Value, out var iso) && IsRecentDate(iso))
                {
                    return BuildFieldFromBandMatch(FormatDateBr(iso), 0.8, "regex", band, footerMatch, 0, footerMatch.Value);
                }
            }

            return NotFound();
        }

        private bool IsRecentDate(string iso, int maxYears = 5)
        {
            if (string.IsNullOrWhiteSpace(iso)) return false;
            if (!DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return false;
            var today = DateTime.UtcNow.Date;
            if (dt > today.AddDays(1)) return false;
            return dt >= today.AddYears(-maxYears);
        }

        private string FormatDateBr(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return iso ?? "";
            if (!DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                return iso;
            return dt.ToString("dd/MM/yyyy", new CultureInfo("pt-BR"));
        }

        private FieldInfo ExtractAssinante(DespachoContext ctx)
        {
            var catalog = SignaturePatternCatalog.Current;
            var sigRegex = catalog.AssinanteMain;
            var sigCollapsedRegex = catalog.AssinanteCollapsed;
            var pjeRegex = catalog.AssinantePje;
            foreach (var band in ctx.BandSegments.Where(b => string.Equals(b.Band, "footer", StringComparison.OrdinalIgnoreCase)))
            {
                var bandHit = TryExtractAssinanteFromBand(band, "footer", sigRegex, sigCollapsedRegex, pjeRegex, ctx);
                if (bandHit != null) return bandHit;
            }

            foreach (var r in ctx.Regions.Where(r =>
                         r.Name.StartsWith("last_bottom", StringComparison.OrdinalIgnoreCase) ||
                         r.Name.Equals("second_bottom", StringComparison.OrdinalIgnoreCase)))
            {
                var text = r.Text ?? "";
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!HasSignatureAnchor(text)) continue;
                var regionHit = TryExtractAssinanteFromText(text, r, "regex_region", sigRegex, pjeRegex, ctx);
                if (regionHit != null) return regionHit;

                var collapsed = TextUtils.CollapseSpacedLettersText(text);
                if (!string.Equals(collapsed, text, StringComparison.Ordinal))
                {
                    var regionCollapsedHit = TryExtractAssinanteFromCollapsed(collapsed, r, "regex_region:collapsed", sigRegex, sigCollapsedRegex, pjeRegex, ctx);
                    if (regionCollapsedHit != null) return regionCollapsedHit;
                }

            }

            foreach (var p in ctx.Paragraphs)
            {
                var text = p.Text ?? "";
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!HasSignatureAnchor(text)) continue;
                var m = sigRegex.Match(text);
                if (m.Success)
                {
                    var name = ResolveSignerName(m.Groups["name"].Value.Trim(), ctx);
                    if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                        return BuildFieldFromMatch(name, 0.8, "regex_paragraph", p, m, 1, Snip(text, m));
                }
                var collapsed = TextUtils.CollapseSpacedLettersText(text);
                if (!string.Equals(collapsed, text, StringComparison.Ordinal))
                {
                    var mCollapsed = sigRegex.Match(collapsed);
                    if (mCollapsed.Success)
                    {
                        var name = ResolveSignerName(mCollapsed.Groups["name"].Value.Trim(), ctx);
                        if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                            return BuildField(name, 0.78, "regex_paragraph:collapsed", null, Snip(collapsed, mCollapsed), p.BBox, p.Page1);
                    }
                    var mCollapsedTight = sigCollapsedRegex.Match(collapsed.Replace(" ", ""));
                    if (mCollapsedTight.Success)
                    {
                        var name = ResolveSignerName(mCollapsedTight.Groups["name"].Value.Trim(), ctx);
                        if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                            return BuildField(name, 0.76, "regex_paragraph:collapsed", null, Snip(collapsed, mCollapsedTight), p.BBox, p.Page1);
                    }
                    var pje = pjeRegex.Match(collapsed);
                    if (pje.Success)
                    {
                        var name = ResolveSignerName(pje.Groups["name"].Value.Trim(), ctx);
                        if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                            return BuildField(name, 0.75, "regex_paragraph:pje", null, Snip(collapsed, pje), p.BBox, p.Page1);
                    }
                }
            }

            var directorHit = TryExtractAssinanteFromDirectorLine(ctx);
            if (directorHit != null) return directorHit;

            var footerCacheHit = TryExtractAssinanteFromFooterCache(ctx, sigRegex, sigCollapsedRegex, pjeRegex);
            if (footerCacheHit != null) return footerCacheHit;

            // Fallback final: assinatura digital do PDF (sem depender do texto extraido)
            if (!string.IsNullOrWhiteSpace(ctx.FilePath))
            {
                var sigs = SignatureExtractor.ExtractSignatures(ctx.FilePath);
                var best = sigs.LastOrDefault(s => !string.IsNullOrWhiteSpace(s.SignerName));
                if (best != null)
                {
                    var name = NormalizeSignerName(best.SignerName);
                    if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                    {
                        return BuildField(name, 0.9, "digital_signature", null, best.FieldName ?? "signature", best.BBoxN, best.Page1);
                    }
                }
            }
            return NotFound();
        }

        private FieldInfo? TryExtractAssinanteFromDirectorLine(DespachoContext ctx)
        {
            var catalog = SignaturePatternCatalog.Current;
            var directorRegex = catalog.Diretor;
            var directorRegexAlt = catalog.DiretorAlt;
            var candidateRegions = ctx.Regions.Where(r => r.Name.StartsWith("last_bottom", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var r in candidateRegions)
            {
                var text = r.Text ?? "";
                if (string.IsNullOrWhiteSpace(text)) continue;
                var m = directorRegex.Match(text);
                if (!m.Success)
                    m = directorRegexAlt.Match(text);
                if (m.Success)
                {
                    var name = ResolveSignerName(m.Groups["name"].Value.Trim(), ctx);
                    if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                        return BuildFieldFromRegionMatch(name, 0.82, "director_line", r, m, 1, Snip(text, m));
                }
            }

            var lastPageText = ctx.Pages.FirstOrDefault(p => p.Page1 == ctx.EndPage1)?.Text ?? "";
            if (!string.IsNullOrWhiteSpace(lastPageText))
            {
                var m = directorRegex.Match(lastPageText);
                if (!m.Success)
                    m = directorRegexAlt.Match(lastPageText);
                if (m.Success)
                {
                    var name = ResolveSignerName(m.Groups["name"].Value.Trim(), ctx);
                    if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                    {
                        var bbox = ctx.BandSegments.FirstOrDefault(b => string.Equals(b.Band, "footer", StringComparison.OrdinalIgnoreCase) && b.Page1 == ctx.EndPage1)?.BBox;
                        return BuildField(name, 0.8, "director_line", null, Snip(lastPageText, m), bbox, ctx.EndPage1);
                    }
                }
            }

            return null;
        }

        private FieldInfo? TryExtractAssinanteFromFooterCache(DespachoContext ctx, Regex sigRegex, Regex sigCollapsedRegex, Regex pjeRegex)
        {
            var signers = ctx.FooterSigners ?? new List<string>();
            var raw = ctx.FooterSignatureRaw ?? "";

            string? picked = null;
            if (signers.Count > 0)
                picked = PickSignerFromList(signers);

            if (string.IsNullOrWhiteSpace(picked) && !string.IsNullOrWhiteSpace(raw) && HasSignatureAnchor(raw))
            {
                var m = sigRegex.Match(raw);
                if (m.Success)
                    picked = ResolveSignerName(m.Groups["name"].Value.Trim(), ctx);
                if (string.IsNullOrWhiteSpace(picked))
                {
                    var collapsed = TextUtils.CollapseSpacedLettersText(raw);
                    var mCollapsed = sigRegex.Match(collapsed);
                    if (mCollapsed.Success)
                        picked = ResolveSignerName(mCollapsed.Groups["name"].Value.Trim(), ctx);
                    if (string.IsNullOrWhiteSpace(picked))
                    {
                        var tight = collapsed.Replace(" ", "");
                        var mTight = sigCollapsedRegex.Match(tight);
                        if (mTight.Success)
                            picked = ResolveSignerName(mTight.Groups["name"].Value.Trim(), ctx);
                    }
                }
                if (string.IsNullOrWhiteSpace(picked))
                {
                    var pje = pjeRegex.Match(raw);
                    if (pje.Success)
                        picked = ResolveSignerName(pje.Groups["name"].Value.Trim(), ctx);
                }
            }

            if (string.IsNullOrWhiteSpace(picked)) return null;
            picked = ResolveSignerName(picked, ctx);
            if (string.IsNullOrWhiteSpace(picked) || !LooksLikeAssinante(picked)) return null;

            var footerBand = ctx.BandSegments.FirstOrDefault(b => string.Equals(b.Band, "footer", StringComparison.OrdinalIgnoreCase) && b.Page1 == ctx.EndPage1)
                ?? ctx.BandSegments.FirstOrDefault(b => string.Equals(b.Band, "footer", StringComparison.OrdinalIgnoreCase));
            var bbox = footerBand?.BBox;
            var page1 = footerBand?.Page1 ?? ctx.EndPage1;
            var snippet = string.IsNullOrWhiteSpace(raw) ? "footer_signers" : TextUtils.SafeSnippet(raw, 0, Math.Min(160, raw.Length));
            var method = signers.Count > 0 ? "footer_signers" : "footer_raw";
            return BuildField(picked, 0.72, method, null, snippet, bbox, page1);
        }

        private FieldInfo? TryExtractAssinanteFromBand(BandSegment band, string methodPrefix, Regex sigRegex, Regex sigCollapsedRegex, Regex pjeRegex, DespachoContext ctx)
        {
            var text = band.Text ?? "";
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!HasSignatureAnchor(text)) return null;
            var m = sigRegex.Match(text);
            if (m.Success)
            {
                var name = ResolveSignerName(m.Groups["name"].Value.Trim(), ctx);
                if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                    return BuildFieldFromBandMatch(name, 0.82, methodPrefix, band, m, 1, Snip(text, m));
            }

            var collapsed = TextUtils.CollapseSpacedLettersText(text);
            if (!string.Equals(collapsed, text, StringComparison.Ordinal))
            {
                var mCollapsed = sigRegex.Match(collapsed);
                if (mCollapsed.Success)
                {
                    var name = ResolveSignerName(mCollapsed.Groups["name"].Value.Trim(), ctx);
                    if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                        return BuildField(name, 0.72, $"{methodPrefix}:collapsed", null, Snip(collapsed, mCollapsed), band.BBox, band.Page1);
                }
                var tight = collapsed.Replace(" ", "");
                var mTight = sigCollapsedRegex.Match(tight);
                if (mTight.Success)
                {
                    var name = ResolveSignerName(mTight.Groups["name"].Value.Trim(), ctx);
                    if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                        return BuildField(name, 0.7, $"{methodPrefix}:collapsed", null, Snip(collapsed, mTight), band.BBox, band.Page1);
                }
                var pje = pjeRegex.Match(collapsed);
                if (pje.Success)
                {
                    var name = ResolveSignerName(pje.Groups["name"].Value.Trim(), ctx);
                    if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                        return BuildField(name, 0.7, $"{methodPrefix}:pje", null, Snip(collapsed, pje), band.BBox, band.Page1);
                }
            }
            return null;
        }

        private FieldInfo? TryExtractAssinanteByHint(string text, RegionSegment region, string hint, string method)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(hint)) return null;
            var idx = text.IndexOf(hint, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var window = text.Substring(idx, Math.Min(140, text.Length - idx));
            var m = Regex.Match(window, @"([\\p{L}][\\p{L}'\\-]+(?:\\s+[\\p{L}][\\p{L}'\\-]+){0,5})");
            if (m.Success)
            {
                var name = CleanPersonName(m.Groups[1].Value.Trim());
                if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                    return BuildField(name, 0.7, method, null, Snip(window, m), region.BBox, region.Page1);
            }
            return null;
        }

        private FieldInfo? TryExtractAssinanteByHint(string text, BBoxN? bbox, int page1, string hint, string method)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(hint)) return null;
            var idx = text.IndexOf(hint, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var window = text.Substring(idx, Math.Min(140, text.Length - idx));
            var m = Regex.Match(window, @"([\\p{L}][\\p{L}'\\-]+(?:\\s+[\\p{L}][\\p{L}'\\-]+){0,5})");
            if (m.Success)
            {
                var name = CleanPersonName(m.Groups[1].Value.Trim());
                if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                    return BuildField(name, 0.7, method, null, Snip(window, m), bbox, page1);
            }
            return null;
        }

        private FieldInfo? TryExtractAssinanteFromText(string text, RegionSegment region, string methodPrefix, Regex sigRegex, Regex pjeRegex, DespachoContext ctx)
        {
            var m = sigRegex.Match(text);
            if (m.Success)
            {
                var name = ResolveSignerName(m.Groups["name"].Value.Trim(), ctx);
                if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                    return BuildFieldFromRegionMatch(name, 0.85, methodPrefix, region, m, 1, Snip(text, m));
            }
            if (HasPjeSignatureAnchor(text))
            {
                var mPje = pjeRegex.Match(text);
                if (mPje.Success)
                {
                    var name = ResolveSignerName(mPje.Groups["name"].Value.Trim(), ctx);
                    if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                        return BuildFieldFromRegionMatch(name, 0.78, $"{methodPrefix}:pje", region, mPje, 1, Snip(text, mPje));
                }
            }
            return null;
        }

        private FieldInfo? TryExtractAssinanteFromCollapsed(string collapsedText, RegionSegment region, string methodPrefix, Regex sigRegex, Regex sigCollapsedRegex, Regex pjeRegex, DespachoContext ctx)
        {
            var m = sigRegex.Match(collapsedText);
            if (m.Success)
            {
                var name = ResolveSignerName(m.Groups["name"].Value.Trim(), ctx);
                if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                    return BuildField(name, 0.78, methodPrefix, null, Snip(collapsedText, m), region.BBox, region.Page1);
            }
            var tight = collapsedText.Replace(" ", "");
            var mTight = sigCollapsedRegex.Match(tight);
            if (mTight.Success)
            {
                var name = ResolveSignerName(mTight.Groups["name"].Value.Trim(), ctx);
                if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                    return BuildField(name, 0.76, methodPrefix, null, Snip(collapsedText, mTight), region.BBox, region.Page1);
            }
            var pje = pjeRegex.Match(collapsedText);
            if (pje.Success)
            {
                var name = ResolveSignerName(pje.Groups["name"].Value.Trim(), ctx);
                if (!string.IsNullOrWhiteSpace(name) && LooksLikeAssinante(name))
                    return BuildField(name, 0.74, methodPrefix, null, Snip(collapsedText, pje), region.BBox, region.Page1);
            }
            return null;
        }

        private string NormalizeSignerName(string raw)
        {
            var cleaned = CleanPersonName(raw);
            if (string.IsNullOrWhiteSpace(cleaned)) return "";
            // remove cargo if it leaks into the name
            cleaned = CutAtKeywords(cleaned, new[]
            {
                "Diretor", "Diretora", "Diretor(a)", "Diretor Especial",
                "Juiz", "Juiza", "Juíza", "Juiz(a)",
                "Desembargador", "Desembargadora", "Presidente"
            });
            return cleaned;
        }

        private string ResolveSignerName(string raw, DespachoContext ctx)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var cleaned = NormalizeSignerName(raw);
            var key = NormalizeNameKey(raw);
            if (string.IsNullOrWhiteSpace(key)) return cleaned;

            var signers = ctx.FooterSigners ?? new List<string>();
            foreach (var s in signers)
            {
                var k = NormalizeNameKey(s);
                if (string.IsNullOrWhiteSpace(k)) continue;
                if (k == key || k.Contains(key) || key.Contains(k))
                    return NormalizeSignerName(s);
            }
            return cleaned;
        }

        private string? PickSignerFromList(List<string> signers)
        {
            if (signers == null || signers.Count == 0) return null;
            var hints = _cfg.Anchors?.SignerHints ?? new List<string>();
            if (hints.Count > 0)
            {
                foreach (var s in signers)
                {
                    var norm = TextUtils.NormalizeForMatch(s);
                    if (hints.Any(h => !string.IsNullOrWhiteSpace(h) && norm.Contains(TextUtils.NormalizeForMatch(h))))
                        return s;
                }
            }
            return signers.FirstOrDefault();
        }

        private string NormalizeNameKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var t = TextUtils.RemoveDiacritics(value);
            t = Regex.Replace(t, @"[^A-Za-z]", "");
            return t.ToLowerInvariant();
        }

        private bool HasSignatureAnchor(string text)
        {
            return ValidatorRules.HasSignatureAnchor(text, _cfg.Anchors?.SignerHints);
        }

        private bool HasPjeSignatureAnchor(string text)
        {
            return ValidatorRules.HasPjeSignatureAnchor(text);
        }

        private bool LooksLikeAssinante(string value)
        {
            return ValidatorRules.LooksLikeAssinanteName(value);
        }

        private FieldInfo ExtractNumPerito(DespachoContext ctx)
        {
            foreach (var p in ctx.Paragraphs)
            {
                var m = MatchAny(p.Text, FieldRegexCatalog.Get("perito_num"));
                if (!m.Success)
                    m = Regex.Match(p.Text ?? "", @"(?i)(matricula|cadastro|numero do perito|num\.\s*perito)\s*[:\-]?\s*(\d{3,})");
                if (m.Success)
                    return BuildFieldFromMatch(m.Groups[2].Value.Trim(), 0.55, "regex", p, m, 2, Snip(p.Text, m));

                var mInss = MatchAny(p.Text, FieldRegexCatalog.Get("perito_inss"));
                if (!mInss.Success)
                    mInss = Regex.Match(p.Text ?? "", @"(?i)inscri[cç][aã]o\\s*no\\s*inss.*?n[ºo]\\s*(\\d{6,})");
                if (mInss.Success)
                    return BuildFieldFromMatch(mInss.Groups[1].Value.Trim(), 0.5, "regex", p, mInss, 1, Snip(p.Text, mInss));

                var mPis = MatchAny(p.Text, FieldRegexCatalog.Get("perito_pis"));
                if (!mPis.Success)
                    mPis = Regex.Match(p.Text ?? "", @"(?i)pis\\s*/?\\s*pasep.*?n[ºo]\\s*(\\d{6,})");
                if (mPis.Success)
                    return BuildFieldFromMatch(mPis.Groups[1].Value.Trim(), 0.5, "regex", p, mPis, 1, Snip(p.Text, mPis));
            }
            return NotFound();
        }

        private FieldInfo BuildField(string value, double confidence, string method, ParagraphSegment? p, string snippet, BBoxN? bboxOverride = null, int? pageOverride = null)
        {
            var info = new FieldInfo
            {
                Value = string.IsNullOrWhiteSpace(value) ? "-" : value.Trim(),
                Confidence = confidence,
                Method = method
            };

            if (p != null || pageOverride.HasValue)
            {
                info.Evidence = new EvidenceInfo
                {
                    Page1 = pageOverride ?? p?.Page1 ?? 0,
                    BBoxN = bboxOverride ?? p?.BBox,
                    Snippet = snippet ?? ""
                };
            }
            else if (!string.IsNullOrWhiteSpace(snippet))
            {
                info.Evidence = new EvidenceInfo { Page1 = 0, BBoxN = null, Snippet = snippet };
            }
            return info;
        }

        private FieldInfo BuildFieldFromMatch(string value, double confidence, string method, ParagraphSegment p, Match m, int groupIndex, string snippet)
        {
            var group = m.Groups.Count > groupIndex ? m.Groups[groupIndex] : m.Groups[0];
            var bbox = ComputeMatchBBox(p.Text ?? "", p.Words, group.Index, group.Length) ?? p.BBox;
            return BuildField(value, confidence, method, p, snippet, bbox, p.Page1);
        }

        private FieldInfo BuildFieldFromSpan(string value, double confidence, string method, ParagraphSegment p, int matchIndex, int matchLength, string snippet)
        {
            var bbox = ComputeMatchBBox(p.Text ?? "", p.Words, matchIndex, matchLength) ?? p.BBox;
            return BuildField(value, confidence, method, p, snippet, bbox, p.Page1);
        }

        private FieldInfo BuildFieldFromBandMatch(string value, double confidence, string method, BandSegment band, Match m, int groupIndex, string snippet)
        {
            var group = m.Groups.Count > groupIndex ? m.Groups[groupIndex] : m.Groups[0];
            var bbox = ComputeMatchBBox(band.Text ?? "", band.Words, group.Index, group.Length) ?? band.BBox;
            return BuildField(value, confidence, method, null, snippet, bbox, band.Page1);
        }

        private FieldInfo BuildFieldFromWordsMatch(string value, double confidence, string method, List<WordInfo> orderedWords, int page1, string matchText, Match m, int groupIndex)
        {
            var group = m.Groups.Count > groupIndex ? m.Groups[groupIndex] : m.Groups[0];
            var bbox = ComputeMatchBBox(matchText ?? "", orderedWords, group.Index, group.Length);
            var snippet = Snip(matchText ?? "", m);
            return BuildField(value, confidence, method, null, snippet, bbox, page1);
        }

        private FieldInfo BuildFieldFromSpanWithSpans(string value, double confidence, string method, string sourceText, int matchIndex, int matchLength,
            List<(WordInfo word, int start, int end)> spans, int page1)
        {
            var bbox = ComputeMatchBBox(spans, matchIndex, matchLength);
            var snippet = "";
            if (spans != null && spans.Count > 0)
            {
                var windowStart = Math.Max(0, matchIndex - 40);
                var windowEnd = matchIndex + matchLength + 40;
                var words = spans.Where(s => s.end >= windowStart && s.start <= windowEnd)
                                 .Select(s => s.word.Text)
                                 .ToList();
                if (words.Count > 0)
                    snippet = TextUtils.NormalizeWhitespace(string.Join(" ", words));
            }
            if (string.IsNullOrWhiteSpace(snippet))
            {
                var src = sourceText ?? "";
                snippet = TextUtils.SafeSnippet(src, Math.Max(0, matchIndex - 40), Math.Min(src.Length - Math.Max(0, matchIndex - 40), 160));
            }
            if (snippet.Length > 160)
                snippet = snippet.Substring(0, 160);
            return BuildField(value, confidence, method, null, snippet, bbox, page1);
        }

        private FieldInfo BuildFieldFromRegionMatch(string value, double confidence, string method, RegionSegment region, Match m, int groupIndex, string snippet)
        {
            var group = m.Groups.Count > groupIndex ? m.Groups[groupIndex] : m.Groups[0];
            var bbox = ComputeMatchBBox(region.Text ?? "", region.Words, group.Index, group.Length) ?? region.BBox;
            return BuildField(value, confidence, method, null, snippet, bbox, region.Page1);
        }

        private static FieldInfo NotFound()
        {
            return new FieldInfo { Value = "-", Confidence = 0.1, Method = "not_found" };
        }

        private void EnsureField(Dictionary<string, FieldInfo> fields, string key)
        {
            if (!fields.ContainsKey(key))
                fields[key] = NotFound();
        }

        private void PostValidate(Dictionary<string, FieldInfo> fields)
        {
            if (fields.TryGetValue("PROCESSO_JUDICIAL", out var pj) && pj.Method != "not_found")
            {
                if (ValidatorRules.TryNormalizeProcessoJudicial(pj.Value, _cnj, out var normalized))
                {
                    pj.Value = normalized;
                    fields["PROCESSO_JUDICIAL"] = pj;
                }
                else
                {
                    fields["PROCESSO_JUDICIAL"] = NotFound();
                }
            }

            if (fields.TryGetValue("CPF_PERITO", out var cpf) && cpf.Method != "not_found")
            {
                if (ValidatorRules.TryNormalizeCpfDigits(cpf.Value, out var digits))
                {
                    cpf.Value = digits;
                    fields["CPF_PERITO"] = cpf;
                }
                else
                {
                    fields["CPF_PERITO"] = NotFound();
                }
            }

            if (fields.TryGetValue("PERITO", out var perito) && perito.Method != "not_found")
            {
                var cleaned = TrimPeritoName(CleanPersonName(perito.Value));
                if (!string.IsNullOrWhiteSpace(cleaned) && cleaned.Length >= 4)
                {
                    perito.Value = cleaned;
                    fields["PERITO"] = perito;
                }
            }
        }

        private void EnrichWithPeritoCatalog(Dictionary<string, FieldInfo> fields)
        {
            fields.TryGetValue("PERITO", out var perito);
            fields.TryGetValue("CPF_PERITO", out var cpf);
            fields.TryGetValue("ESPECIALIDADE", out var esp);

            var name = perito?.Value ?? "";
            var cpfVal = cpf?.Value ?? "";
            if (!_peritoCatalog.TryResolve(name, cpfVal, out var info, out var conf))
                return;

            var evidence = PickEvidence(perito, cpf);
            var replacePerito = perito == null || perito.Method == "not_found" || perito.Confidence < 0.6 || LooksLikeNoisePerito(perito.Value);
            if (replacePerito && !string.IsNullOrWhiteSpace(info.Name))
                fields["PERITO"] = BuildCatalogField(info.Name, conf, evidence, "catalogo_peritos");

            var cpfDigits = TextUtils.NormalizeCpf(cpf?.Value ?? "");
            var replaceCpf = cpf == null || cpf.Method == "not_found" || cpf.Confidence < 0.6 || cpfDigits.Length != 11;
            if (replaceCpf && !string.IsNullOrWhiteSpace(info.Cpf))
                fields["CPF_PERITO"] = BuildCatalogField(info.Cpf, conf, evidence, "catalogo_peritos");

            var replaceEsp = esp == null || esp.Method == "not_found" || esp.Confidence < 0.6 || LooksWeakEspecialidade(esp.Value);
            if (replaceEsp && !string.IsNullOrWhiteSpace(info.Especialidade))
                fields["ESPECIALIDADE"] = BuildCatalogField(info.Especialidade, conf, evidence, "catalogo_peritos");
        }

        private void NormalizeEspecialidadeArea(Dictionary<string, FieldInfo> fields)
        {
            if (!fields.TryGetValue("ESPECIALIDADE", out var esp) || esp == null || esp.Method == "not_found")
                return;

            var raw = esp.Value ?? "";
            if (string.IsNullOrWhiteSpace(raw))
                return;

            if (_honorarios.TryResolveAreaFromText(raw, out var area, out var source) && !string.IsNullOrWhiteSpace(area))
            {
                if (!string.Equals(raw, area, StringComparison.OrdinalIgnoreCase))
                {
                    fields["ESPECIALIDADE"] = new FieldInfo
                    {
                        Value = area,
                        Confidence = Math.Max(esp.Confidence, 0.6),
                        Method = $"especialidade_area:{source}",
                        Evidence = esp.Evidence
                    };
                }
            }
        }

        private void ApplyHonorariosLookup(Dictionary<string, FieldInfo> fields)
        {
            fields.TryGetValue("ESPECIALIDADE", out var esp);
            if (esp == null || esp.Method == "not_found") return;

            FieldInfo? valorBase = null;
            if (_cfg.Reference.Honorarios.PreferValorDe && fields.TryGetValue("VALOR_ARBITRADO_DE", out var vde) && vde.Method != "not_found")
                valorBase = vde;
            else if (_cfg.Reference.Honorarios.AllowValorJz && fields.TryGetValue("VALOR_ARBITRADO_JZ", out var vjz) && vjz.Method != "not_found")
                valorBase = vjz;

            if (valorBase == null) return;
            if (!TextUtils.TryParseMoney(valorBase.Value, out var valor)) return;

            if (_honorarios.TryMatch(esp.Value, valor, out var entry, out var conf))
            {
                if (valorBase.Method == "template_region:first_top" || valorBase.Method == "heuristic")
                    conf = Math.Max(0.55, conf - 0.1);
                var evidence = valorBase.Evidence;
                if ((!fields.TryGetValue("ESPECIE_DA_PERICIA", out var especie) || especie.Method == "not_found" || especie.Confidence < 0.6) && !string.IsNullOrWhiteSpace(entry.Descricao))
                {
                    fields["ESPECIE_DA_PERICIA"] = BuildCatalogField(entry.Descricao, conf, evidence, "tabela_honorarios");
                }
                if ((!fields.TryGetValue("VALOR_TABELADO_ANEXO_I", out var vtab) || vtab.Method == "not_found" || vtab.Confidence < 0.6))
                {
                    fields["VALOR_TABELADO_ANEXO_I"] = BuildCatalogField(FormatMoney(entry.Valor), conf, evidence, "tabela_honorarios");
                }
                if ((!fields.TryGetValue("FATOR", out var fator) || fator.Method == "not_found" || fator.Confidence < 0.6) && !string.IsNullOrWhiteSpace(entry.Id))
                {
                    fields["FATOR"] = BuildCatalogField(entry.Id, conf, evidence, "tabela_honorarios");
                }
            }
        }

        private FieldInfo BuildCatalogField(string value, double confidence, EvidenceInfo? evidence, string method)
        {
            return new FieldInfo
            {
                Value = value,
                Confidence = Math.Min(0.9, Math.Max(0.55, confidence)),
                Method = method,
                Evidence = evidence
            };
        }

        private EvidenceInfo? PickEvidence(FieldInfo? a, FieldInfo? b)
        {
            if (a?.Evidence != null) return a.Evidence;
            if (b?.Evidence != null) return b.Evidence;
            return null;
        }

        private static string FormatMoney(decimal value)
        {
            var s = value.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
            return $"R$ {s}";
        }

        private bool LooksLikeNoisePerito(string value)
        {
            return ValidatorRules.LooksLikeNoisePerito(value);
        }

        private bool LooksWeakEspecialidade(string value)
        {
            return ValidatorRules.LooksWeakEspecialidadeValue(value);
        }

        private FieldInfo Ensure(FieldInfo info)
        {
            return info.Method == "not_found" ? NotFound() : info;
        }

        private string Snip(string? text, Match m)
        {
            var src = text ?? "";
            return TextUtils.SafeSnippet(src, Math.Max(0, m.Index - 40), Math.Min(src.Length - Math.Max(0, m.Index - 40), 160));
        }

        private string CleanPartyName(string value)
        {
            return ValidatorRules.CleanPartyName(value);
        }

        private bool LooksLikePartyName(string value)
        {
            return ValidatorRules.LooksLikePartyNameSimple(value);
        }

        private bool IsInstitutionalRequester(string label, string value)
        {
            return ValidatorRules.IsInstitutionalRequester(label, value);
        }

        private bool IsInstitutionalPartyValue(string value)
        {
            return ValidatorRules.IsInstitutionalPartyValue(value);
        }

        private string CleanPersonName(string value)
        {
            return ValidatorRules.CleanPersonName(value);
        }

        private string TrimPeritoName(string value)
        {
            return ValidatorRules.TrimPeritoName(value);
        }

        private string InferEspecie(string especialidade)
        {
            return ValidatorRules.InferEspecieFromEspecialidade(especialidade);
        }

        private bool IsWeakEspecieToken(string tokenNorm)
        {
            return ValidatorRules.IsWeakEspecieToken(tokenNorm);
        }

        private double EstimateRelativePosition(DespachoContext ctx, ParagraphSegment p)
        {
            if (ctx.Pages.Count == 0) return 0.5;
            var total = ctx.Pages.Count;
            var idx = Math.Max(0, Math.Min(total - 1, p.Page1 - ctx.Pages.First().Page1));
            if (total <= 1) return 0.5;
            return (double)idx / (total - 1);
        }

        private ParagraphSegment? FindParagraph(DespachoContext ctx, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return ctx.Paragraphs.FirstOrDefault(p => (p.Text ?? "").Contains(text, StringComparison.OrdinalIgnoreCase));
        }

        private BBoxN? ComputeMatchBBox(string text, List<WordInfo> words, int matchIndex, int matchLength)
        {
            if (words == null || words.Count == 0) return null;
            if (matchIndex < 0 || matchLength <= 0) return null;

            var spans = BuildWordSpans(words);
            var matchEnd = matchIndex + matchLength;
            var matched = spans
                .Where(s => s.start < matchEnd && s.end > matchIndex)
                .Select(s => s.word)
                .ToList();

            if (matched.Count == 0) return null;
            return TextUtils.UnionBBox(matched);
        }

        private BBoxN? ComputeMatchBBox(List<(WordInfo word, int start, int end)> spans, int matchIndex, int matchLength)
        {
            if (spans == null || spans.Count == 0) return null;
            if (matchIndex < 0 || matchLength <= 0) return null;
            var matchEnd = matchIndex + matchLength;
            var matched = spans
                .Where(s => s.start < matchEnd && s.end > matchIndex)
                .Select(s => s.word)
                .ToList();
            if (matched.Count == 0) return null;
            return TextUtils.UnionBBox(matched);
        }

        private List<(WordInfo word, int start, int end)> BuildWordSpans(List<WordInfo> words)
        {
            var spans = new List<(WordInfo word, int start, int end)>();
            words = TextUtils.DeduplicateWords(words);
            var pos = 0;
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                if (i > 0) pos += 1; // space
                var start = pos;
                var token = TextUtils.NormalizeToken(w.Text ?? "");
                var end = start + token.Length;
                spans.Add((w, start, end));
                pos = end;
            }
            return spans;
        }

        private (List<WordInfo> ordered, string text) PrepareMatchText(List<WordInfo> words)
        {
            if (words == null || words.Count == 0) return (new List<WordInfo>(), "");
            var ordered = TextUtils.DeduplicateWords(words)
                .OrderByDescending(w => (w.NormY0 + w.NormY1) / 2.0)
                .ThenBy(w => w.NormX0)
                .ToList();
            var text = TextUtils.NormalizeWhitespace(string.Join(" ", ordered.Select(w => TextUtils.NormalizeToken(w.Text ?? ""))));
            text = TextNormalization.CollapseSpacedLettersText(text);
            return (ordered, text);
        }

        private (string text, List<(WordInfo word, int start, int end)> spans) BuildCollapsedTextWithSpans(List<WordInfo> words)
        {
            var spans = new List<(WordInfo word, int start, int end)>();
            if (words == null || words.Count == 0) return ("", spans);
            var ordered = TextUtils.DeduplicateWords(words)
                .OrderByDescending(w => (w.NormY0 + w.NormY1) / 2.0)
                .ThenBy(w => w.NormX0)
                .ToList();
            var sb = new System.Text.StringBuilder();
            bool prevJoin = false;
            foreach (var w in ordered)
            {
                var token = TextUtils.NormalizeToken(w.Text ?? "");
                if (string.IsNullOrEmpty(token)) continue;
                var join = IsJoinToken(token);
                if (sb.Length > 0 && !join && !prevJoin)
                    sb.Append(' ');
                var start = sb.Length;
                sb.Append(token);
                var end = sb.Length;
                spans.Add((w, start, end));
                prevJoin = join;
            }
            return (sb.ToString(), spans);
        }

        private bool IsJoinToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            if (token.Length == 1)
            {
                var c = token[0];
                if (char.IsLetterOrDigit(c)) return true;
                if (c == '$' || c == '/' || c == '-' || c == '–' || c == '.' || c == ',' ||
                    c == 'ª' || c == 'º' || c == '°')
                    return true;
            }
            return false;
        }

        private static string BuildAlternation(IEnumerable<string> phrases)
        {
            if (phrases == null)
                return "";
            var list = new List<string>();
            foreach (var phrase in phrases)
            {
                var trimmed = (phrase ?? "").Trim();
                if (trimmed.Length == 0)
                    continue;
                var spaced = ProcessoPartesMarkers.BuildSpacedPattern(trimmed);
                if (string.IsNullOrWhiteSpace(spaced))
                    continue;
                list.Add($"(?:{spaced})");
            }
            return list.Count == 0 ? "" : string.Join("|", list);
        }

        private static Match MatchAny(string? text, IReadOnlyList<Regex> regexes)
        {
            if (string.IsNullOrWhiteSpace(text) || regexes == null || regexes.Count == 0)
                return Match.Empty;
            foreach (var rx in regexes)
            {
                var m = rx.Match(text);
                if (m.Success)
                    return m;
            }
            return Match.Empty;
        }

        private List<string> BuildTemplates(IEnumerable<string> labels)
        {
            var list = new List<string>();
            foreach (var l in labels)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                list.Add($"{l}: {{value}}");
                list.Add($"{l} - {{value}}");
            }
            return list;
        }

        private List<string> MergeTemplates(IEnumerable<string> primary, IEnumerable<string> fallback)
        {
            var list = new List<string>();
            if (primary != null)
                list.AddRange(primary.Where(p => !string.IsNullOrWhiteSpace(p)));
            if (fallback != null)
                list.AddRange(fallback.Where(p => !string.IsNullOrWhiteSpace(p)));
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
