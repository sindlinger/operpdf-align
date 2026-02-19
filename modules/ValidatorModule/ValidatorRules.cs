using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Obj.TjpbDespachoExtractor.Reference;
using Obj.TjpbDespachoExtractor.Utils;
using Obj.Utils;

namespace Obj.ValidatorModule
{
    public static class ValidatorRules
    {
        public delegate bool FieldValueValidator(string field, string value, PeritoCatalog? catalog, out string reason);
        public delegate bool PeritoCatalogResolver(string value, PeritoCatalog? catalog, out double confidence);

        public static bool LooksLikeCpf(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var digits = Regex.Replace(value, "[^0-9]", "");
            return digits.Length == 11;
        }

        public static bool ContainsCpfPattern(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return Regex.IsMatch(value, @"\b\d{3}\s*\.?\s*\d{3}\s*\.?\s*\d{3}\s*-?\s*\d{2}\b");
        }

        public static bool ContainsEmail(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return Regex.IsMatch(value, @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase);
        }

        public static bool ContainsInstitutional(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = TextUtils.RemoveDiacritics(value).ToLowerInvariant();
            var compact = Regex.Replace(v, @"\s+", "");
            return v.Contains("juizo") || v.Contains("vara") || v.Contains("comarca") ||
                v.Contains("tribunal") || v.Contains("forum") || v.Contains("justica") ||
                v.Contains("judiciario") ||
                v.Contains("diretoria") || v.Contains("diretor") || v.Contains("diretora") || v.Contains("gerencia") ||
                v.Contains("secretaria") || v.Contains("departamento") || v.Contains("presidencia") ||
                v.Contains("juizado") || v.Contains("cartorio") || v.Contains("serventia") ||
                compact.Contains("juizo") || compact.Contains("comarca") || compact.Contains("tribunal") || compact.Contains("justica") ||
                compact.Contains("judiciario") ||
                compact.Contains("diretoria") || compact.Contains("diretor") || compact.Contains("diretora") || compact.Contains("gerencia") ||
                compact.Contains("secretaria") || compact.Contains("presidencia") || compact.Contains("juizado") ||
                compact.Contains("cartorio") || compact.Contains("serventia");
        }

        public static bool ContainsVaraComarca(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = TextUtils.RemoveDiacritics(value).ToLowerInvariant();
            var compact = Regex.Replace(v, @"\s+", "");
            return v.Contains("vara") || v.Contains("comarca") || v.Contains("juizo") ||
                v.Contains("juizado") || v.Contains("forum") || v.Contains("cartorio") ||
                compact.Contains("vara") || compact.Contains("comarca") || compact.Contains("juizo") ||
                compact.Contains("juizado") || compact.Contains("forum") || compact.Contains("cartorio");
        }

        public static bool ContainsProcessualNoise(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = TextUtils.RemoveDiacritics(value).ToLowerInvariant();
            return Regex.IsMatch(v, @"\b(aposentadoria|auxilio|beneficio|procedimento|classe|assunto|processo|requerente|interessad[oa]|promovent[eo]|promovid[oa]|reu|autor|juiz|juiza|juizo|vara|comarca|tribunal|diretoria|diretor|diretora|documento|pagina|p[aá]gina|fls|assinado|eletronicamente|honorari\w*|pagamento|requer\w*|reserva|orcament\w*|conta|bancari\w*|natureza|servic\w*|relatoria|desembargador|sess[aã]o|excel[êe]ncia|considera[cç][aã]o|submet\w*)\b");
        }

        public static bool ContainsDocumentBoilerplate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = TextUtils.RemoveDiacritics(value).ToLowerInvariant();
            if (Regex.IsMatch(v, @"\bdocumento\s*\d+\b")) return true;
            if (Regex.IsMatch(v, @"\bp[aá]gina\b")) return true;
            if (Regex.IsMatch(v, @"\bassinad[ao]\b")) return true;
            if (Regex.IsMatch(v, @"\bprocesso\s*n[ºo]?\b")) return true;
            if (Regex.IsMatch(v, @"\b(diretoria|tribunal|forum|cartorio|juizo)\b")) return true;
            return false;
        }

        public static bool LooksLikeComarcaValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var v = TextUtils.NormalizeWhitespace(value).Trim();
            if (v.Length < 3 || v.Length > 80) return false;
            if (v.Any(char.IsDigit)) return false;
            if (ContainsEmail(v) || ContainsCpfPattern(v)) return false;

            var norm = TextUtils.RemoveDiacritics(v).ToLowerInvariant();
            if (norm.Contains("comarca") || norm.Contains("juizo") || norm.Contains("juizado"))
                return true;

            // Evita vazamento de rótulos/ruído processual no valor capturado.
            if (Regex.IsMatch(norm, @"(interessad|requerente|autor|r[eé]u|reu|promovent|promovid|movid[oa]|em\s+face|processo|autos|vara|cartorio|tribunal|diretoria)"))
                return false;

            // Formato clássico: "Cidade / UF".
            if (Regex.IsMatch(v, @"[A-Za-zÀ-ÿ]{3,}(?:\s+[A-Za-zÀ-ÿ]{2,})*\s*/\s*[A-Z]{2}"))
                return true;

            // Formato comum: apenas o nome do município (com UF opcional via "- PB" ou "/PB").
            return Regex.IsMatch(v, @"^[A-Za-zÀ-ÿ]{3,}(?:[ '\-][A-Za-zÀ-ÿ]{2,}){0,6}(?:\s*(?:-|/)?\s*[A-Z]{2})?$");
        }

        public static bool LooksLikeVaraValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = TextUtils.RemoveDiacritics(value).ToLowerInvariant();
            return v.Contains("vara") || v.Contains("juizo") || v.Contains("juizado");
        }

        public static bool LooksLikeEspecialidadeValue(string? value, Func<string, bool>? containsEspecialidadeToken = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (containsEspecialidadeToken != null && containsEspecialidadeToken(value))
                return true;
            var v = TextUtils.RemoveDiacritics(value).ToLowerInvariant();
            return Regex.IsMatch(v, @"\b(engenheir\w*|engenhar\w*|arquitet\w*|psicolog\w*|psiquiatr\w*|medic\w*|fonoaud\w*|fisioterap\w*|odont\w*|contador\w*|assistente\s+social|grafotec\w*|economist\w*|administrador\w*|biolog\w*|quimic\w*|farmac\w*|ortoped\w*|cardiolog\w*|neurolog\w*|nutricion\w*|terapeut\w*|seguranca\s+do\s+trabalho)\b");
        }

        public static bool LooksLikePartyValue(
            string? value,
            Func<string, bool>? institutionalCheck = null,
            Func<string, bool>? boilerplateCheck = null,
            Func<string, bool>? processualNoiseCheck = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = TextUtils.NormalizeWhitespace(value).Trim();
            if (v.Length < 4 || v.Length > 120) return false;
            if (!Regex.IsMatch(v, @"[\p{L}]")) return false;
            if (Regex.IsMatch(v, @"\d")) return false;
            if (ContainsEmail(v) || ContainsCpfPattern(v)) return false;

            var isInstitutional = institutionalCheck ?? ContainsInstitutional;
            var hasBoilerplate = boilerplateCheck ?? ContainsDocumentBoilerplate;
            var hasProcessualNoise = processualNoiseCheck ?? ContainsProcessualNoise;
            if (isInstitutional(v) || hasBoilerplate(v) || hasProcessualNoise(v)) return false;

            if (Regex.IsMatch(v, @"(?i)\b(R\s*\$|valor|honor[aá]ri|percentual|resolu[cç][aã]o|guarda\s+unilateral)\b"))
                return false;
            var tokens = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hasCompanySuffix = Regex.IsMatch(v, @"(?i)(?:S\s*[\./]?\s*A\.?$|LTDA\.?$|EIRELI$)");
            if (tokens.Length < 2 && !hasCompanySuffix) return false;
            if (tokens.Any(t => t.Length > 28)) return false;
            return true;
        }

        public static bool IsValidFieldFormat(
            string field,
            string value,
            Func<string, bool>? peritoValidator = null,
            Func<string, bool>? partyValidator = null,
            Func<string, bool>? especialidadeValidator = null,
            Func<string, bool>? comarcaValidator = null,
            Func<string, bool>? varaValidator = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            if (field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase))
                return LooksLikeCpf(value);

            if (field.Equals("PROCESSO_JUDICIAL", StringComparison.OrdinalIgnoreCase))
            {
                var compact = Regex.Replace(value, @"\s+", "");
                var digits = Regex.Replace(compact, @"\D", "");
                if (Regex.IsMatch(compact, @"^\d{7}-\d{2}\.\d{4}\.\d\.\d{2}\.\d{4}$")) return true;
                if (Regex.IsMatch(compact, @"^\d{7}-\d{2}\.\d{4}\.\d{3}\.\d{4}$")) return true;
                if (Regex.IsMatch(compact, @"^\d{7}\.\d{2}\.\d{4}\.\d\.\d{2}\.\d{4}$")) return true;
                if (Regex.IsMatch(compact, @"^\d{7}\.\d{2}\.\d{4}\.\d{3}\.\d{4}$")) return true;
                // Legacy format still present in older TJPB records.
                if (Regex.IsMatch(compact, @"^\d{3}\.\d{4}\.\d{3}\.\d{3}-\d$")) return true;
                if (digits.Length == 20)
                    return true;
                return Regex.IsMatch(compact, @"^\d{20}$");
            }

            if (field.Equals("PROCESSO_ADMINISTRATIVO", StringComparison.OrdinalIgnoreCase))
            {
                var compact = Regex.Replace(value, @"\s+", "");
                if (Regex.IsMatch(compact, @"^\d{1,2}[/-]\d{1,2}[/-]\d{2,4}$"))
                    return false;
                if (Regex.IsMatch(compact, @"^\d{5}-\d{3}$"))
                    return false;
                if (Regex.IsMatch(compact, @"^\d{2}/\d{4}$") || Regex.IsMatch(compact, @"^\d{4}/\d{2}$"))
                    return false;
                if (Regex.IsMatch(compact, @"^\d{6,7}-\d{2}\.\d{4}\.\d\.\d{2}(?:\.\d{4})?$"))
                    return true;
                if (Regex.IsMatch(compact, @"^\d{4}\.\d{3}\.\d{3}$"))
                    return true;
                if (Regex.IsMatch(compact, @"^(?:19|20)\d{8}$"))
                    return true;
                return false;
            }

            if (field.Equals("VALOR_ARBITRADO_JZ", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("VALOR_ARBITRADO_DE", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("VALOR_ARBITRADO_FINAL", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("VALOR_ARBITRADO_CM", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("VALOR_TABELADO_ANEXO_I", StringComparison.OrdinalIgnoreCase))
            {
                var compact = Regex.Replace(value, @"\s+", "");
                return Regex.IsMatch(compact, @"^(?:R\$)?(?:\d{1,3}(?:\.\d{3})*|\d+),\d{2}$");
            }

            if (field.Equals("DATA_ARBITRADO_FINAL", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("DATA_AUTORIZACAO_CM", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("DATA_REQUISICAO", StringComparison.OrdinalIgnoreCase))
            {
                return TextUtils.TryParseDate(value, out _);
            }

            if (field.Equals("PERCENTUAL", StringComparison.OrdinalIgnoreCase))
            {
                var compact = Regex.Replace(value, @"\s+", "");
                return Regex.IsMatch(compact, @"^\d{1,3}(?:[.,]\d{1,2})?%$");
            }

            if (field.Equals("ADIANTAMENTO", StringComparison.OrdinalIgnoreCase))
            {
                var compact = Regex.Replace(value, @"\s+", "");
                if (Regex.IsMatch(compact, @"^(?:R\$)?\d{1,3}(?:\.\d{3})*,\d{2}$"))
                    return true;
                return Regex.IsMatch(compact, @"^\d{1,3}(?:[.,]\d{1,2})?%$");
            }

            if (field.Equals("PARCELA", StringComparison.OrdinalIgnoreCase))
            {
                var compact = Regex.Replace(value, @"\s+", "");
                if (Regex.IsMatch(compact, @"^(?:R\$)?(?:\d{1,3}(?:\.\d{3})*|\d+),\d{2}$"))
                    return true;
                if (Regex.IsMatch(compact, @"^\d{1,3}(?:[.,]\d{1,2})?%$"))
                    return true;
                return false;
            }

            if (field.Equals("PERITO", StringComparison.OrdinalIgnoreCase))
            {
                if (peritoValidator != null)
                    return peritoValidator(value);
                return value.Trim().Length >= 5;
            }

            if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
            {
                if (partyValidator != null)
                    return partyValidator(value);
                return LooksLikePartyValue(value);
            }

            if (field.Equals("ESPECIALIDADE", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("ESPECIE_DA_PERICIA", StringComparison.OrdinalIgnoreCase))
            {
                var hasEmail = ContainsEmail(value);
                var hasCpf = ContainsCpfPattern(value);
                var hasNoise = ContainsProcessualNoise(value);
                var looksPerson = LooksLikePersonNameLoose(value);
                if (hasEmail || hasCpf || hasNoise || looksPerson)
                {
                    if (Environment.GetEnvironmentVariable("OPERPDF_VAL_DEBUG") == "1")
                        Console.WriteLine($"[VALDBG-legacy] field={field} reject email={hasEmail} cpf={hasCpf} noise={hasNoise} person={looksPerson} value=\"{TextUtils.NormalizeWhitespace(value)}\"");
                    return false;
                }

                if (especialidadeValidator != null && especialidadeValidator(value))
                {
                    if (Environment.GetEnvironmentVariable("OPERPDF_VAL_DEBUG") == "1")
                        Console.WriteLine($"[VALDBG-legacy] field={field} accept=external_validator value=\"{TextUtils.NormalizeWhitespace(value)}\"");
                    return true;
                }

                if (LooksLikeEspecialidadeFallback(value))
                {
                    if (Environment.GetEnvironmentVariable("OPERPDF_VAL_DEBUG") == "1")
                        Console.WriteLine($"[VALDBG-legacy] field={field} accept=fallback_regex value=\"{TextUtils.NormalizeWhitespace(value)}\"");
                    return true;
                }

                var fallback = TextUtils.NormalizeWhitespace(value);
                var okLoose = fallback.Length >= 4 && Regex.IsMatch(fallback, @"[\p{L}]");
                if (Environment.GetEnvironmentVariable("OPERPDF_VAL_DEBUG") == "1")
                    Console.WriteLine($"[VALDBG-legacy] field={field} accept=loose:{okLoose.ToString().ToLowerInvariant()} value=\"{fallback}\"");
                return okLoose;
            }

            if (field.Equals("COMARCA", StringComparison.OrdinalIgnoreCase))
            {
                if (comarcaValidator != null)
                    return comarcaValidator(value);
                return LooksLikeComarcaValue(value);
            }

            if (field.Equals("VARA", StringComparison.OrdinalIgnoreCase))
            {
                if (varaValidator != null)
                    return varaValidator(value);
                return LooksLikeVaraValue(value);
            }

            return true;
        }

        private static bool LooksLikeEspecialidadeFallback(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = TextUtils.RemoveDiacritics(value).ToLowerInvariant();
            return Regex.IsMatch(v, @"\b(engenheir\w*|engenhar\w*|arquitet\w*|psicolog\w*|psiquiatr\w*|medic\w*|fonoaud\w*|fisioterap\w*|odont\w*|contador\w*|assistente\s+social|grafotec\w*|economist\w*|administrador\w*|biolog\w*|quimic\w*|farmac\w*|ortoped\w*|cardiolog\w*|neurolog\w*|nutricion\w*|terapeut\w*|seguranca\s+do\s+trabalho)\b");
        }

        public static bool LooksLikePersonNameLoose(string text)
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

        public static bool LooksLikePersonNameStrict(string text)
        {
            if (!LooksLikePersonNameLoose(text))
                return false;
            var tokens = TextUtils.NormalizeWhitespace(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length >= 2 && tokens.All(t => t.Length >= 2);
        }

        public static bool LooksLikeCatalogName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (name.Contains('@')) return false;
            if (name.Length < 5) return false;
            if (Regex.IsMatch(name, "interessad[oa]", RegexOptions.IgnoreCase)) return false;
            if (Regex.IsMatch(name, "sighop", RegexOptions.IgnoreCase)) return false;
            return Regex.IsMatch(name, "[A-Za-zÁÂÃÀÉÊÍÓÔÕÚÇ]", RegexOptions.IgnoreCase);
        }

        public static bool TryNormalizeProcessoJudicial(string? value, Regex cnjRegex, out string normalized)
        {
            normalized = "";
            if (string.IsNullOrWhiteSpace(value)) return false;
            var cleaned = Regex.Replace(value, "\\s+", "");
            var m = cnjRegex.Match(cleaned);
            if (!m.Success) return false;
            normalized = m.Value;
            return !string.IsNullOrWhiteSpace(normalized);
        }

        public static bool IsValidProcessoJudicial(string? value, Regex cnjRegex)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var cleaned = Regex.Replace(value, "\\s+", "");
            return cnjRegex.IsMatch(cleaned);
        }

        public static bool TryNormalizeCpfDigits(string? value, out string digits11)
        {
            digits11 = "";
            if (string.IsNullOrWhiteSpace(value)) return false;
            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length != 11) return false;
            digits11 = digits;
            return true;
        }

        public static bool LooksLikeNoisePerito(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            if (value.Contains("@")) return true;
            var norm = TextUtils.NormalizeForMatch(value);
            if (norm.Contains("perito")) return true;
            if (norm.Contains("engenheiro")) return true;
            if (norm.Contains("medic")) return true;
            if (norm.Contains("grafotec") || norm.Contains("grafoscop")) return true;
            if (norm.Contains("psicol")) return true;
            if (norm.Contains("assistente social")) return true;
            return false;
        }

        public static bool LooksLikePartyNameSimple(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            if (v.Length < 4) return false;
            if (v.Contains("@")) return false;
            var norm = TextUtils.NormalizeForMatch(v);
            if (norm.Contains("perito")) return false;
            if (!Regex.IsMatch(v, "[A-Za-zÁÂÃÀÉÊÍÓÔÕÚÇ]")) return false;
            return true;
        }

        public static bool IsInstitutionalPartyValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var norm = TextUtils.NormalizeForMatch(value);
            return norm.Contains("juizo") || norm.Contains("vara") || norm.Contains("comarca") ||
                norm.Contains("tribunal") || norm.Contains("poder judiciario") ||
                norm.Contains("diretoria") || norm.Contains("secretaria") ||
                norm.Contains("cartorio") || norm.Contains("serventia");
        }

        public static bool IsWeakPeritoValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;
            var norm = TextUtils.NormalizeForMatch(value);
            if (string.IsNullOrWhiteSpace(norm))
                return true;
            return Regex.IsMatch(norm, @"\b(sua excelencia|consideracao|submet|presentes|movido por|processo|autos do processo|perante)\b");
        }

        public static bool IsWeakPartyValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;
            var norm = TextUtils.NormalizeForMatch(value);
            if (string.IsNullOrWhiteSpace(norm))
                return true;
            norm = Regex.Replace(norm, @"\s+", " ").Trim();
            if (Regex.IsMatch(norm, @"\b(relatoria|desembargador|sessao|excelencia|consideracao|submet)\b"))
                return true;
            return norm is "e outros" or "e outras" or "outros" or "outras" or "outro" or "outra" or "autor" or "autora" or "autores" or "reu" or "reu(s)" or "reu(s)." or "reus";
        }

        public static bool LooksLikeAssinanteName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = TextUtils.NormalizeWhitespace(value);
            if (v.Length < 5) return false;
            if (v.Contains("pg.", StringComparison.OrdinalIgnoreCase)) return false;
            if (v.Contains("sei", StringComparison.OrdinalIgnoreCase)) return false;
            if (v.Any(char.IsDigit)) return false;
            var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                // Some PDFs collapse spaces between words; accept long single token.
                return v.Length >= 10;
            }
            return true;
        }

        public static bool LooksWeakEspecialidadeValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var norm = TextUtils.NormalizeForMatch(value);
            if (norm.Length <= 6) return true;
            var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 1 && (norm == "engenheiro" || norm == "medico" || norm == "medica" || norm == "psicologo" || norm == "psicologa"))
                return true;
            return false;
        }

        public static string CleanPartyName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var v = TextUtils.CollapseSpacedLettersText(value);
            v = Regex.Replace(v, @"(?i)\b(CPF|CNPJ)\b.*$", "");
            v = Regex.Replace(v, @"(?i)\bperante\b.*$", "");
            v = Regex.Replace(v, @"(?i)\bju[ií]zo\b.*$", "");
            v = Regex.Replace(v, @"\d+", "");
            v = Regex.Replace(v, @"[^A-Za-zÁÂÃÀÉÊÍÓÔÕÚÇ\\s'\\-]+", " ");
            v = v.Trim();
            v = v.Trim(',', ';', '-', '–', ' ');
            return TextUtils.NormalizeWhitespace(v);
        }

        public static bool IsInstitutionalRequester(string? label, string? value)
        {
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value)) return false;
            var l = TextUtils.NormalizeForMatch(label);
            if (!(l.Contains("requerente") || l.Contains("promovente") || l.Contains("autor")))
                return false;
            return IsInstitutionalPartyValue(TextUtils.NormalizeForMatch(value));
        }

        public static string CleanPersonName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var v = TextUtils.CollapseSpacedLettersText(value);
            v = Regex.Replace(v, @"(?i)\bperit[oa]\b", "");
            v = Regex.Replace(v, @"(?i)\\s*[-–]\\s*[^\\s@]*@[^\\s,;]+", "");
            v = Regex.Replace(v, @"[^A-Za-zÁÂÃÀÉÊÍÓÔÕÚÇ\s'-]+", " ");
            v = RestoreNameSpacing(v);
            v = TextUtils.NormalizeWhitespace(v);
            return v;
        }

        public static string TrimPeritoName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var v = TextUtils.CollapseSpacedLettersText(value);
            v = TextUtils.NormalizeWhitespace(TextUtils.FixMissingSpaces(v));
            v = Regex.Replace(v, @"(?i)^\\s*(?:interessad[oa]|perit[oa])\\s*[:\\-–—]\\s*", "");
            var dashIdx = v.IndexOfAny(new[] { '-', '–', '—' });
            if (dashIdx > 0)
            {
                var right = v.Substring(dashIdx + 1).Trim();
                if (Regex.IsMatch(right, @"(?i)\\b(perit[oa]|grafot[eê]cnic[oa]|m[eé]dic[oa]|engenheir[oa]|arquitet[oa]|contador[a]?|psic[oó]log[oa]|odont[oó]log[oa]|assistente\\s+social|fonoaudi[oó]log[oa]|fisioterapeut[oa]|economist[a]|administrador[a]|b[ií]olog[oa]|qu[ií]mic[oa]|farmac[eê]utic[oa])\\b"))
                    v = v.Substring(0, dashIdx).Trim();
            }
            v = Regex.Replace(v, @"(?i)\\s*(?:CPF|PIS|CRM|CREA|CNPJ)\\b.*$", "");
            v = Regex.Replace(v, @"(?i)\\s+perit[oa]\\b.*$", "");
            v = v.Trim().Trim(',', '-', '–', '—');
            v = TextUtils.NormalizeWhitespace(v);
            return v;
        }

        public static string InferEspecieFromEspecialidade(string? especialidade)
        {
            var norm = TextUtils.NormalizeForMatch(especialidade ?? "");
            if (norm.Contains("medic")) return "medica";
            if (norm.Contains("grafotec")) return "grafotecnica";
            if (norm.Contains("contab")) return "contabil";
            if (norm.Contains("engenh")) return "engenharia";
            if (norm.Contains("psicol")) return "psicologica";
            if (norm.Contains("odontol")) return "odontologica";
            if (norm.Contains("psiquiatr")) return "psiquiatrica";
            if (norm.Contains("fonoaud")) return "fonoaudiologica";
            if (norm.Contains("fisioter")) return "fisioterapica";
            if (norm.Contains("informat")) return "informatica";
            if (norm.Contains("ambient")) return "ambiental";
            if (norm.Contains("arquitet")) return "arquitetonica";
            return "";
        }

        public static bool IsWeakEspecieToken(string? tokenNorm)
        {
            if (string.IsNullOrWhiteSpace(tokenNorm)) return true;
            var bad = new[] { "nos", "no", "na", "dos", "das", "do", "da", "em", "de", "do processo", "autos", "autos do processo" };
            return bad.Any(b => tokenNorm == b);
        }

        public static bool HasPjeSignatureAnchor(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var norm = TextUtils.NormalizeForMatch(text).Replace(" ", "");
            if (norm.Contains("numerododocumento") && norm.Contains("por")) return true;
            if (norm.Contains("documento") && norm.Contains("por") && norm.Contains("eletron")) return true;
            return false;
        }

        public static bool HasSignatureAnchor(string? text, IEnumerable<string>? hints = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (hints != null)
            {
                var normHint = TextUtils.NormalizeForMatch(text);
                foreach (var hint in hints)
                {
                    if (string.IsNullOrWhiteSpace(hint)) continue;
                    if (normHint.Contains(TextUtils.NormalizeForMatch(hint)))
                        return true;
                }
            }
            var norm = TextUtils.NormalizeForMatch(text).Replace(" ", "");
            if (norm.Contains("assinadoeletronicamente")) return true;
            if (norm.Contains("assinadodigitalmente")) return true;
            return HasPjeSignatureAnchor(text);
        }

        public static bool ContainsGeorc(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return text.Contains("GEORC", StringComparison.OrdinalIgnoreCase)
                || text.Contains("GERENCIA DE PROGRAMACAO ORCAMENTARIA", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ContainsAdiantamento(string? text, Func<string, string>? normalizePatternText = null)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var norm = normalizePatternText != null ? normalizePatternText(text) : TextUtils.NormalizeForMatch(text);
            if (norm.Contains("adiantamento", StringComparison.OrdinalIgnoreCase))
                return true;
            if (norm.Contains("parcela", StringComparison.OrdinalIgnoreCase))
                return true;
            if (norm.Contains("%", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        public static string ApplyStrategyCleanRule(string cleanKey, string value)
        {
            var v = value ?? "";
            if (string.IsNullOrWhiteSpace(cleanKey))
                return TextUtils.NormalizeWhitespace(v);

            var key = cleanKey.Trim().ToUpperInvariant();
            if (key == "CLEANMONEY")
            {
                var money = TextUtils.NormalizeMoney(v);
                return !string.IsNullOrWhiteSpace(money) ? money : v;
            }

            if (key == "CLEANPARTE")
            {
                v = TextUtils.NormalizeWhitespace(v);
                v = Regex.Replace(v, "(?i)\\bCPF\\s*[:\\-]?\\s*\\d{3}\\.\\d{3}\\.\\d{3}-\\d{2}\\b", "");
                v = Regex.Replace(v, "\\b\\d{3}\\.\\d{3}\\.\\d{3}-\\d{2}\\b", "");
                v = Regex.Replace(v, "(?i)\\s+perante\\s+o\\s+ju[ií]zo.+$", "");
                v = TextUtils.NormalizeWhitespace(v);
                v = Regex.Replace(v, "\\s*,\\s+e\\s+", " e ");
                v = Regex.Replace(v, "\\s*,\\s*$", "");
                return v.Trim().Trim(',', ';', '-');
            }

            if (key == "CLEANPERITO" || key == "CLEANPROFISSAO" || key == "CLEANPROFISSÃO" || key == "CLEANCOMARCA")
                return TextUtils.NormalizeWhitespace(v);

            return v;
        }

        public static bool IsStrategyValidationRuleValid(string validationKey, string value, Regex moneyRegex)
        {
            if (string.IsNullOrWhiteSpace(validationKey))
                return true;

            var key = validationKey.Trim().ToUpperInvariant();
            if (key == "VALIDATEMONEY")
                return moneyRegex.IsMatch(value);
            if (key == "VALIDATEPARTE")
                return Regex.IsMatch(value, "[A-Za-zÁÂÃÀÉÊÍÓÔÕÚÇ]");
            if (key == "VALIDATEPERITO")
                return value.Length >= 5;
            if (key == "VALIDATECOMARCA")
                return value.Length >= 3;
            return true;
        }

        public static string NormalizeExtractorValue(string field, string value, Regex cnjRegex, bool collapseWeirdSpacing)
        {
            var v = TextUtils.NormalizeWhitespace(value);
            if (collapseWeirdSpacing && TextUtils.ComputeWeirdSpacingRatio(v) >= 0.12)
                v = TextUtils.CollapseSpacedLettersText(v);

            v = TextUtils.NormalizeWhitespace(TextUtils.FixMissingSpaces(v));
            if (field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase))
                return TextUtils.NormalizeCpf(v);
            if (field.StartsWith("VALOR_", StringComparison.OrdinalIgnoreCase) || field.Equals("ADIANTAMENTO", StringComparison.OrdinalIgnoreCase))
                return TextUtils.NormalizeMoney(v);
            if (field.Equals("DATA", StringComparison.OrdinalIgnoreCase))
            {
                if (TextUtils.TryParseDate(v, out var iso)) return iso;
            }
            if (field.StartsWith("PROCESSO_", StringComparison.OrdinalIgnoreCase))
            {
                var cleaned = Regex.Replace(v, "\\s+", "");
                var m = cnjRegex.Match(cleaned);
                if (m.Success) return m.Value.Replace(" ", "");
            }
            return v;
        }

        public static bool IsPeritoNameFromCatalog(string? value, PeritoCatalog? catalog, out double confidence)
        {
            confidence = 0;
            if (catalog == null || string.IsNullOrWhiteSpace(value)) return false;
            if (catalog.TryResolve(value, null, out var info, out var conf))
            {
                if (!string.IsNullOrWhiteSpace(info.Name))
                {
                    confidence = conf;
                    return true;
                }
            }
            return false;
        }

        private static string RestoreNameSpacing(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value ?? "";
            return Regex.Replace(value, @"(?<=[a-záâãàéêíóôõúç])(?=[A-ZÁÂÃÀÉÊÍÓÔÕÚÇ])", " ");
        }

        public static string StripKnownLabelPrefix(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value ?? "";
            var v = value.Trim();
            var labels = new[]
            {
                "processo", "requerente", "interessado", "interessada",
                "promovente", "promovido", "perito", "cpf", "comarca", "vara", "juizo", "juízo"
            };
            foreach (var label in labels)
            {
                var m = Regex.Match(v, $"(?i)^\\s*{Regex.Escape(label)}\\s*[:\\-]\\s*(.+)$");
                if (m.Success)
                    return m.Groups[1].Value.Trim();
            }
            return v;
        }

        public static bool IsValueValidForField(
            string field,
            string value,
            PeritoCatalog? catalog,
            Func<string, string, string>? normalizeValueByField,
            Func<string, string, bool>? isValidFieldFormat,
            out string reason)
        {
            reason = "ok";
            if (string.IsNullOrWhiteSpace(value))
            {
                reason = "empty";
                return false;
            }

            var cleaned = StripKnownLabelPrefix(value);
            if (!string.Equals(cleaned, value, StringComparison.Ordinal))
                value = cleaned;

            var hasAnchor = Regex.IsMatch(value, @"(?i)\b(requerente|interessad[oa]|promovent[eo]|promovid[oa]|processo|perito|cpf)\b");
            if (hasAnchor)
            {
                reason = "anchor_leak";
                if (field.Equals("PROCESSO_JUDICIAL", StringComparison.OrdinalIgnoreCase) ||
                    field.Equals("PROCESSO_ADMINISTRATIVO", StringComparison.OrdinalIgnoreCase) ||
                    field.Equals("VARA", StringComparison.OrdinalIgnoreCase) ||
                    field.Equals("COMARCA", StringComparison.OrdinalIgnoreCase))
                {
                    var normalized = normalizeValueByField != null ? normalizeValueByField(field, value) : value;
                    var okFormat = isValidFieldFormat != null ? isValidFieldFormat(field, normalized) : IsValidFieldFormat(field, normalized);
                    if (!okFormat)
                        return false;
                }
                else
                {
                    return false;
                }
            }

            if (!field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase) && ContainsCpfPattern(value))
            {
                reason = "cpf_in_other_field";
                return false;
            }

            if (!field.Equals("PERITO", StringComparison.OrdinalIgnoreCase) &&
                !field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) &&
                !field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
            {
                if (IsPeritoNameFromCatalog(value, catalog, out var conf) && conf >= 0.6)
                {
                    reason = "perito_in_other_field";
                    return false;
                }
            }

            if (!field.Equals("VARA", StringComparison.OrdinalIgnoreCase) &&
                !field.Equals("COMARCA", StringComparison.OrdinalIgnoreCase) &&
                ContainsVaraComarca(value))
            {
                reason = "vara_comarca_in_other_field";
                return false;
            }

            if ((field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                 field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase) ||
                 field.Equals("PERITO", StringComparison.OrdinalIgnoreCase)) &&
                ContainsInstitutional(value))
            {
                reason = "institutional_in_field";
                return false;
            }

            if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
            {
                if (ContainsDocumentBoilerplate(value) || ContainsProcessualNoise(value))
                {
                    reason = "boilerplate_in_party";
                    return false;
                }
            }

            {
                var normalized = normalizeValueByField != null ? normalizeValueByField(field, value) : value;
                var okFormat = isValidFieldFormat != null ? isValidFieldFormat(field, normalized) : IsValidFieldFormat(field, normalized);
                if (!okFormat)
                {
                    reason = "format_invalid";
                    return false;
                }
            }

            return true;
        }

        public static void ApplyValidatorFiltersAndReanalysis<TMatch>(
            Dictionary<string, List<TMatch>> fieldMatches,
            IEnumerable<string>? orderedFields,
            string fullNorm,
            string streamNorm,
            string opsNorm,
            PeritoCatalog? catalog,
            Func<TMatch, string?> getValue,
            Action<TMatch, string> setValue,
            Func<TMatch, string, string, TMatch> cloneMatch,
            Func<string, string, double, string, TMatch> createMatch,
            Func<string, string, string> normalizeValueByField,
            FieldValueValidator isValueValidForField,
            PeritoCatalogResolver isPeritoNameFromCatalog,
            Func<string, bool> containsCpfPattern,
            Func<string, string, string, string, string?> findReanalysisCandidate)
        {
            if (fieldMatches == null)
                return;

            var allFields = new HashSet<string>(orderedFields ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var k in fieldMatches.Keys)
                allFields.Add(k);

            var peritoCandidates = new List<TMatch>();
            var cpfCandidates = new List<TMatch>();

            foreach (var field in allFields)
            {
                if (!fieldMatches.TryGetValue(field, out var matches) || matches == null || matches.Count == 0)
                    continue;

                var kept = new List<TMatch>();
                foreach (var m in matches)
                {
                    var val = getValue(m) ?? "";
                    val = StripKnownLabelPrefix(val);
                    if (!string.IsNullOrWhiteSpace(val))
                        setValue(m, normalizeValueByField(field, val));

                    var current = getValue(m) ?? "";
                    if (isValueValidForField(field, current, catalog, out _))
                    {
                        kept.Add(m);
                        continue;
                    }

                    if (!field.Equals("PERITO", StringComparison.OrdinalIgnoreCase) &&
                        isPeritoNameFromCatalog(current, catalog, out var conf) && conf >= 0.6)
                    {
                        peritoCandidates.Add(cloneMatch(m, "PERITO", "validator_relocate"));
                    }

                    if (!field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase) &&
                        containsCpfPattern(current))
                    {
                        cpfCandidates.Add(cloneMatch(m, "CPF_PERITO", "validator_relocate"));
                    }
                }

                fieldMatches[field] = kept;
            }

            if (peritoCandidates.Count > 0)
            {
                if (!fieldMatches.TryGetValue("PERITO", out var list) || list == null || list.Count == 0)
                    fieldMatches["PERITO"] = peritoCandidates;
                else
                    list.AddRange(peritoCandidates);
            }

            if (cpfCandidates.Count > 0)
            {
                if (!fieldMatches.TryGetValue("CPF_PERITO", out var list) || list == null || list.Count == 0)
                    fieldMatches["CPF_PERITO"] = cpfCandidates;
                else
                    list.AddRange(cpfCandidates);
            }

            foreach (var field in allFields)
            {
                if (fieldMatches.TryGetValue(field, out var list) && list != null && list.Count > 0)
                    continue;

                var candidate = findReanalysisCandidate(field, fullNorm, streamNorm, opsNorm);
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    isValueValidForField(field, candidate, catalog, out _))
                {
                    fieldMatches[field] = new List<TMatch>
                    {
                        createMatch(field, candidate!, 0.85, "reanalysis_fulltext")
                    };
                }
            }
        }

        public static bool ShouldRejectByValidator(
            Dictionary<string, string>? values,
            HashSet<string>? optionalFields,
            string? patternsPath,
            PeritoCatalog? catalog,
            FieldValueValidator isValueValidForField,
            out string reason)
        {
            reason = "";
            if (values == null)
            {
                reason = "values_null";
                return true;
            }

            if (string.IsNullOrWhiteSpace(patternsPath))
                return false;

            bool TryGetNonEmpty(string field, out string value)
            {
                if (values.TryGetValue(field, out var raw) && !string.IsNullOrWhiteSpace(raw))
                {
                    value = raw;
                    return true;
                }
                value = "";
                return false;
            }

            var docKey = DocumentValidationRules.ResolveDocKeyFromPatternPath(patternsPath);
            var isRequerimento = DocumentValidationRules.IsDocMatch(docKey, "requerimento_honorarios");
            if (isRequerimento)
            {
                if (!TryGetNonEmpty("PROCESSO_ADMINISTRATIVO", out var pa))
                {
                    reason = "missing:PROCESSO_ADMINISTRATIVO";
                    return true;
                }
                if (!isValueValidForField("PROCESSO_ADMINISTRATIVO", pa, catalog, out var paWhy))
                {
                    reason = $"PROCESSO_ADMINISTRATIVO:{paWhy}";
                    return true;
                }

                // Requerimento precisa de sinal próprio do documento para não colidir com certidão.
                // DATA_REQUISICAO é o discriminador mais confiável nesse fluxo.
                var hasData = TryGetNonEmpty("DATA_REQUISICAO", out var dataReq);
                if (!hasData)
                {
                    reason = "missing:DATA_REQUISICAO";
                    return true;
                }
                if (!isValueValidForField("DATA_REQUISICAO", dataReq, catalog, out var dataWhy))
                {
                    reason = $"DATA_REQUISICAO:{dataWhy}";
                    return true;
                }

                if (TryGetNonEmpty("PROCESSO_JUDICIAL", out var pj) &&
                    !isValueValidForField("PROCESSO_JUDICIAL", pj, catalog, out var pjWhy))
                {
                    reason = $"PROCESSO_JUDICIAL:{pjWhy}";
                    return true;
                }

                return false;
            }

            var isCertidao = DocumentValidationRules.IsDocMatch(docKey, "certidao_conselho");
            if (isCertidao)
            {
                if (!TryGetNonEmpty("PROCESSO_ADMINISTRATIVO", out var pa))
                {
                    reason = "missing:PROCESSO_ADMINISTRATIVO";
                    return true;
                }
                if (!isValueValidForField("PROCESSO_ADMINISTRATIVO", pa, catalog, out var paWhy))
                {
                    reason = $"PROCESSO_ADMINISTRATIVO:{paWhy}";
                    return true;
                }

                var hasValor = TryGetNonEmpty("VALOR_ARBITRADO_CM", out var valorCm);
                var hasData = TryGetNonEmpty("DATA_AUTORIZACAO_CM", out var dataCm);
                // Certidão não deve passar só com PERITO (colide com requerimento/despacho).
                if (!hasValor && !hasData)
                {
                    reason = "missing_one_of:VALOR_ARBITRADO_CM|DATA_AUTORIZACAO_CM";
                    return true;
                }
                if (hasValor && !isValueValidForField("VALOR_ARBITRADO_CM", valorCm, catalog, out var valorWhy))
                {
                    reason = $"VALOR_ARBITRADO_CM:{valorWhy}";
                    return true;
                }
                if (hasData && !isValueValidForField("DATA_AUTORIZACAO_CM", dataCm, catalog, out var dataCmWhy))
                {
                    reason = $"DATA_AUTORIZACAO_CM:{dataCmWhy}";
                    return true;
                }
                if (TryGetNonEmpty("PERITO", out var perito) &&
                    !isValueValidForField("PERITO", perito, catalog, out var peritoWhy))
                {
                    reason = $"PERITO:{peritoWhy}";
                    return true;
                }

                return false;
            }

            if (!DocumentValidationRules.IsDocMatch(docKey, "despacho"))
                return false;

            var core = new[]
            {
                "PROCESSO_ADMINISTRATIVO",
                "PROCESSO_JUDICIAL",
                "PERITO",
                "CPF_PERITO",
                "PROMOVENTE",
                "VARA",
                "COMARCA",
                "VALOR_ARBITRADO_JZ"
            };

            var optSet = optionalFields ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in core)
            {
                if (!values.TryGetValue(f, out var v) || string.IsNullOrWhiteSpace(v))
                {
                    if (optSet.Contains(f))
                        continue;
                    reason = $"missing:{f}";
                    return true;
                }

                if (!isValueValidForField(f, v, catalog, out var why))
                {
                    if ((f.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                         f.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase)) &&
                        string.Equals(why, "format_invalid", StringComparison.OrdinalIgnoreCase))
                    {
                        var repaired = CleanPartyName(v);
                        if (!string.IsNullOrWhiteSpace(repaired))
                        {
                            repaired = TextUtils.NormalizeWhitespace(TextUtils.FixMissingSpaces(repaired));
                            if (isValueValidForField(f, repaired, catalog, out var repairedWhy))
                            {
                                values[f] = repaired;
                                continue;
                            }

                            // Soft-accept for OCR-heavy despacho pages when value is still a
                            // plausible party name and no institutional/boilerplate noise appears.
                            if (LooksLikePartyNameSimple(repaired) &&
                                !ContainsInstitutional(repaired) &&
                                !ContainsDocumentBoilerplate(repaired))
                            {
                                values[f] = repaired;
                                continue;
                            }
                        }

                        // Keep extraction when the only issue is party formatting noise.
                        continue;
                    }

                    reason = $"{f}:{why}";
                    return true;
                }
            }

            return false;
        }

        public static bool PassesDocumentValidator(
            IReadOnlyDictionary<string, string>? inputValues,
            string? outputDocType,
            PeritoCatalog? catalog,
            out string reason)
        {
            var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (inputValues != null)
            {
                foreach (var kv in inputValues)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key))
                        continue;
                    local[kv.Key] = kv.Value ?? "";
                }
            }

            return ApplyAndValidateDocumentValues(local, outputDocType, catalog, out reason, out _);
        }

        public static bool ApplyAndValidateDocumentValues(
            IDictionary<string, string>? values,
            string? outputDocType,
            PeritoCatalog? catalog,
            out string reason,
            out List<string> changedFields)
        {
            changedFields = new List<string>();
            reason = "";
            if (string.IsNullOrWhiteSpace(outputDocType))
            {
                reason = "doc_empty";
                return false;
            }
            if (values == null)
            {
                reason = "values_null";
                return false;
            }

            var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in values)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    before[kv.Key] = kv.Value ?? "";
            }

            var patternsPath = ResolvePatternPathForOutputDocType(outputDocType);
            if (string.IsNullOrWhiteSpace(patternsPath) || !File.Exists(patternsPath))
            {
                reason = "pattern_not_found";
                return false;
            }

            var local = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in values)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                    continue;
                local[kv.Key] = TextUtils.NormalizeWhitespace(kv.Value);
            }

            if (string.Equals(outputDocType, DocumentValidationRules.OutputDocCertidaoCm, StringComparison.OrdinalIgnoreCase) &&
                !local.ContainsKey("DATA_AUTORIZACAO_CM") &&
                local.TryGetValue("DATA_ARBITRADO_FINAL", out var certDate) &&
                !string.IsNullOrWhiteSpace(certDate))
            {
                local["DATA_AUTORIZACAO_CM"] = certDate;
            }

            var optional = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ADIANTAMENTO"
            };

            var reject = ShouldRejectByValidator(
                local,
                optional,
                patternsPath,
                catalog,
                (string field, string value, PeritoCatalog? peritoCatalog, out string why) =>
                    IsValueValidForField(field, value, peritoCatalog, null, null, out why),
                out reason);

            foreach (var kv in local)
                values[kv.Key] = kv.Value;

            var allKeys = new HashSet<string>(before.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var k in values.Keys)
            {
                if (!string.IsNullOrWhiteSpace(k))
                    allKeys.Add(k);
            }

            foreach (var key in allKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var oldValue = before.TryGetValue(key, out var old) ? old ?? "" : "";
                var newValue = values.TryGetValue(key, out var cur) ? cur ?? "" : "";
                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                    changedFields.Add(key);
            }

            return !reject;
        }

        public static string ResolvePatternPathForOutputDocType(string? outputDocType)
        {
            var docKey = DocumentValidationRules.MapOutputTypeToDocKey(outputDocType);
            var patternName = DocumentValidationRules.ResolvePatternForDoc(docKey);
            if (string.IsNullOrWhiteSpace(patternName))
                return "";

            var path = Path.Combine("modules", "PatternModules", "registry", "patterns", patternName + ".json");
            if (File.Exists(path))
                return Path.GetFullPath(path);

            return "";
        }

        public static string ExplainPeritoReject(
            string? value,
            Func<string?, string> cleanPeritoValue,
            Func<string, string> normalizePatternText,
            Func<string, bool> containsInstitutional,
            Func<string, bool> containsProcessualNoise,
            Func<string, bool> containsDocumentBoilerplate,
            Func<string, bool> looksLikePersonNameLoose,
            Func<string, string> normalizeToken,
            Func<string, bool> isEspecialidadeToken)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "empty";
            var rawLower = value.ToLowerInvariant();
            if (Regex.IsMatch(rawLower, @"\b(raz[aã]o|per[ií]cia)\b"))
                return "stopword";
            var cleaned = cleanPeritoValue(value);
            if (string.IsNullOrWhiteSpace(cleaned))
                return "cleaned_empty";
            var norm = normalizePatternText(cleaned);
            if (string.IsNullOrWhiteSpace(norm))
                return "norm_empty";
            if (norm.Length < 5)
                return "len<5";
            var lower = norm.ToLowerInvariant();
            if (lower.Contains("nomead"))
                return "nomead";
            if (Regex.IsMatch(lower, @"https?://|www\."))
                return "url";
            if (containsInstitutional(norm))
                return "institutional";
            if (containsProcessualNoise(norm) || containsDocumentBoilerplate(norm))
                return "processual_noise";
            if (Regex.IsMatch(lower, @"\b(nos\s+autos|processo|movido\s+por|perante|cpf|pis|cnpj|raz[aã]o|per[ií]cia|assunto|classe|procedimento|documento|p[aá]gina|assinad|eletronicamente|fls|honor[aá]ri|pagamento|reserva|or[cç]ament|conta|banc[aá]ri|servi[cç]o)\b"))
                return "stopword";
            if (norm.Any(char.IsDigit))
                return "digit";
            if (Regex.IsMatch(norm, @"[:;@\[\]\(\)\{\}]"))
                return "punct";
            if (!looksLikePersonNameLoose(norm))
                return "not_name";
            var cleanedWords = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var upperInitials = cleanedWords.Count(w => w.Length > 0 && char.IsLetter(w[0]) && char.IsUpper(w[0]));
            if (upperInitials < 2)
                return "titlecase";
            var normTokens = norm
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => normalizeToken(t))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            if (normTokens.Count > 0 && normTokens.All(t => isEspecialidadeToken(t)) && normTokens.Count <= 3)
                return "espec_only";
            var words = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2)
                return "words<2";
            if (words.Length > 7)
                return "words>7";
            return "ok";
        }

        public static string CleanPeritoValue(
            string? value,
            Func<string, string> normalizeToken,
            Func<string, bool> isPeritoStopwordToken,
            Func<string, string> stripPeritoTrailingContext,
            Func<string, bool> looksLikeUpperGlue,
            Func<string, string> splitUpperByCommonNames,
            Func<string, string> fixDanglingUpperInitial,
            Func<string, bool> containsEspecialidadeToken,
            Func<string, bool> looksLikePersonNameLoose,
            Func<string, string> extractLeadingNameCandidate,
            Func<string, bool> isLikelyOrganization)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            var cut = TextNormalization.NormalizeFullText(value);
            var perIdx = Regex.Match(cut, @"(?i)\bperit[oa]\b");
            if (perIdx.Success && perIdx.Index > 0)
            {
                var prefix = cut.Substring(0, perIdx.Index);
                var prefixTokens = prefix
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(normalizeToken)
                    .Where(t => !string.IsNullOrWhiteSpace(t) && !isPeritoStopwordToken(t))
                    .ToList();
                if (prefixTokens.Count < 2)
                    cut = cut.Substring(perIdx.Index);
            }
            cut = Regex.Replace(cut, @"(?i)^\s*interessad[oa]\s*[:\-–—]\s*", "");
            cut = Regex.Replace(cut, @"(?i)^\s*perit[oa]\s*[:\-–—]\s*", "");
            cut = Regex.Replace(cut, @"(?i)^\s*perit[oa]\s*(?:do\s*\.?\s*ju[ií]zo\s*)?(?:a|o)?\s*", "");
            var titleRx = @"(?i)^\s*(?:dr\.?|dra\.?|doutor|doutora)\s+";
            var profRx = @"(?i)^\s*(?:m[eé]dic[oa]|psic[oó]log[oa]|psiquiatr[a]?|engenheir[oa]|grafot[eê]cnic[oa]|arquitet[oa]|contador[a]?|assistente\s+social|fonoaudi[oó]log[oa]|fisioterapeut[oa]|economist[a]|administrador[a]|b[ií]olog[oa]|qu[ií]mic[oa]|farmac[eê]utic[oa])\s+(?:[a-zçãéíóú]+\\s+){0,2}";
            cut = Regex.Replace(cut, titleRx, "");
            cut = Regex.Replace(cut, profRx, "");
            for (int i = 0; i < 2; i++)
            {
                var before = cut;
                cut = Regex.Replace(cut, titleRx, "");
                cut = Regex.Replace(cut, profRx, "");
                if (string.Equals(before, cut, StringComparison.Ordinal))
                    break;
            }
            cut = stripPeritoTrailingContext(cut);

            if (Regex.IsMatch(cut, @"(?i)\bperit[oa]\b") || Regex.IsMatch(cut, @"(?i)\bju[ií]zo\b"))
            {
                var tokens = cut.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "perito","perita","do","da","de","juizo","juízo","a","o",
                    "medico","medica","psiquiatra","dr","dra","doutor","doutora"
                };
                int idx = 0;
                while (idx < tokens.Count)
                {
                    var norm = normalizeToken(tokens[idx]);
                    if (string.IsNullOrWhiteSpace(norm))
                    {
                        idx++;
                        continue;
                    }
                    if (!skip.Contains(norm))
                        break;
                    idx++;
                }
                if (idx > 0 && idx < tokens.Count)
                    cut = string.Join(" ", tokens.Skip(idx));
            }

            if (looksLikeUpperGlue(cut))
                cut = splitUpperByCommonNames(cut);
            cut = fixDanglingUpperInitial(cut);

            var lower = cut.ToLowerInvariant();
            var stopMarkers = new[]
            {
                " para ", " nos autos", " no processo", " processo", " movido por", " perante "
            };
            var stopIdx = -1;
            foreach (var marker in stopMarkers)
            {
                var idx = lower.IndexOf(marker, StringComparison.Ordinal);
                if (idx > 0)
                    stopIdx = stopIdx < 0 ? idx : Math.Min(stopIdx, idx);
            }
            if (stopIdx > 0)
                cut = cut.Substring(0, stopIdx).Trim();

            lower = cut.ToLowerInvariant();
            var stopRx = Regex.Match(lower, @"\b(para|para\s+realiza(?:cao|ção)|per[ií]cia|nos\s+autos|no\s+processo|processo|movido\s+por|perante|cpf|pis|cnpj)\b");
            if (stopRx.Success && stopRx.Index > 0)
                cut = cut.Substring(0, stopRx.Index).Trim();
            var commaIdx = cut.IndexOf(',');
            if (commaIdx > 0)
                cut = cut.Substring(0, commaIdx).Trim();

            var dashIdx = cut.IndexOfAny(new[] { '-', '–', '—' });
            if (dashIdx > 0)
            {
                var left = cut.Substring(0, dashIdx).Trim();
                var right = cut.Substring(dashIdx + 1).Trim();
                var leftCompact = Regex.Replace(TextUtils.RemoveDiacritics(left).ToLowerInvariant(), @"[^a-z]", "");
                var leftLooksLikeEspecialidade = containsEspecialidadeToken(left) ||
                    Regex.IsMatch(leftCompact, @"(grafotecnic|engenheir|arquitet|psicolog|psiquiatr|medic|contador|assistentesocial|fonoaudiolog|fisioterapeut|economist|administrador|biolog|quimic|farmaceutic)");
                if (containsEspecialidadeToken(right) || Regex.IsMatch(right, @"(?i)^perit[oa]\b"))
                    cut = left;
                else if (leftLooksLikeEspecialidade && looksLikePersonNameLoose(right))
                    cut = right;
            }

            var peritoSuffix = Regex.Match(cut, @"(?i)^(?<name>.+?)(?:,|\s[-–—]\s)\s*perit[oa]\b");
            if (peritoSuffix.Success)
                cut = peritoSuffix.Groups["name"].Value.Trim();
            cut = Regex.Replace(cut, @"(?i)\s*(?:CPF|PIS|CRM|CREA|CNPJ)\b.*$", "");
            if (Regex.IsMatch(cut, @"(?i)\b(nos\s+autos|processo|movido\s+por|perante|cpf|pis|cnpj|per[ií]cia)\b"))
            {
                var extracted = extractLeadingNameCandidate(cut);
                if (!string.IsNullOrWhiteSpace(extracted))
                    cut = extracted;
            }

            cut = cut.Trim().Trim(',', '-', '–', '—');
            cut = TextUtils.CollapseSpacedLettersText(cut);
            cut = TextUtils.NormalizeWhitespace(TextUtils.FixMissingSpaces(cut));
            if (looksLikeUpperGlue(cut))
                cut = splitUpperByCommonNames(cut);
            if (isLikelyOrganization(cut))
                return "";
            return cut;
        }

        public static string ExtractLeadingNameCandidate(string text, Func<string, string> normalizeToken)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var m = Regex.Match(text, @"^(?<name>[A-Za-zÀ-ÿ]{2,}(?:\s+[A-Za-zÀ-ÿ]{2,}){1,6})");
            if (!m.Success)
                return text;
            var name = m.Groups["name"].Value.Trim();
            var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
                return text;
            foreach (var t in tokens)
            {
                var nt = normalizeToken(t);
                if (string.IsNullOrWhiteSpace(nt))
                    continue;
                if (IsPeritoStopwordToken(nt))
                    return text;
            }
            return name;
        }

        public static bool IsPeritoStopwordToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;
            var t = token.ToLowerInvariant();
            return t is "processo" or "autos" or "pericia" or "perito" or "perita" or
                "interessado" or "interessada" or "movido" or "perante" or "cpf" or "pis" or "cnpj" or
                "assunto" or "classe" or "procedimento" or "documento" or "pagina" or "pag" or "página" or
                "assinad" or "eletronicamente" or "fls";
        }

        public static string StripPeritoTrailingContext(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var cut = text;
            var lower = cut.ToLowerInvariant();

            var commaIdx = cut.IndexOf(',');
            if (commaIdx > 0)
            {
                var tail = lower.Substring(commaIdx + 1);
                if (Regex.IsMatch(tail, @"\b(para|nos\s+autos|no\s+processo|no\s+proc|em\s+tramit|em\s+tramita|para\s+realiza)\b"))
                    cut = cut.Substring(0, commaIdx);
            }

            lower = cut.ToLowerInvariant();
            var markers = new[]
            {
                " para ", " para a ", " para o ", " para realização", " para realizacao",
                " para a realização", " para a realizacao", " para fins ", " nos autos",
                " nos presentes autos", " no processo", " no proc", " em tramita",
                " em tramit", " autos do processo"
            };

            var cutIdx = -1;
            foreach (var marker in markers)
            {
                var idx = lower.IndexOf(marker, StringComparison.Ordinal);
                if (idx > 0)
                    cutIdx = cutIdx < 0 ? idx : Math.Min(cutIdx, idx);
            }

            if (cutIdx > 0)
                cut = cut.Substring(0, cutIdx);

            return cut.Trim();
        }
    }
}
