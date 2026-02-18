using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Obj.TjpbDespachoExtractor.Utils;

namespace Obj.Honorarios
{
    public sealed class HonorariosBackfillResult
    {
        public string DocType { get; set; } = "";
        public HonorariosSummary? Summary { get; set; }
        public Dictionary<string, string> DerivedValues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> FieldConfidence { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string DerivedValor { get; set; } = "";

        public bool IsDespacho =>
            string.Equals(DocType, "DESPACHO", StringComparison.OrdinalIgnoreCase);

        public bool TryGetField(string key, out string value, out double confidence)
        {
            value = "";
            confidence = 0.0;
            if (!DerivedValues.TryGetValue(key, out var v) || string.IsNullOrWhiteSpace(v))
                return false;
            value = v;
            FieldConfidence.TryGetValue(key, out confidence);
            return true;
        }
    }

    public static class HonorariosBackfill
    {
        public static HonorariosBackfillResult Apply(IDictionary<string, string> values, string? docType)
        {
            var result = new HonorariosBackfillResult
            {
                DocType = NormalizeDocType(docType)
            };

            if (values == null)
                return result;

            ApplyProfissaoAsEspecialidade(values);

            var runValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in values)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    runValues[kv.Key] = kv.Value ?? "";
            }

            var summary = HonorariosEnricher.RunFromValues(runValues, result.DocType, null);
            result.Summary = summary;
            var side = summary.PdfA;
            if (side == null || !result.IsDespacho)
                return result;

            SetDerived(result, "PERITO", side.PeritoName, side.PeritoCheck?.CatalogConfidence ?? side.Confidence);
            SetDerived(result, "CPF_PERITO", side.PeritoCpf, side.PeritoCheck?.CatalogConfidence ?? side.Confidence);
            SetDerived(result, "ESPECIALIDADE", side.Especialidade, side.Confidence);
            SetDerived(result, "ESPECIE_DA_PERICIA", side.EspecieDaPericia, side.Confidence);
            SetDerived(result, "FATOR", side.Fator, side.Confidence);
            SetDerived(result, "VALOR_TABELADO_ANEXO_I", side.ValorTabeladoAnexoI, side.Confidence);

            var valorDerivado = NormalizeDerivedMoney(side.ValorNormalized);
            result.DerivedValor = valorDerivado;
            SetDerived(result, "VALOR_ARBITRADO_JZ", valorDerivado, side.Confidence);
            SetDerived(result, "VALOR_ARBITRADO_FINAL", valorDerivado, side.Confidence);

            FillIfMissing(values, result.DerivedValues, "PERITO");
            FillIfMissing(values, result.DerivedValues, "CPF_PERITO");
            FillIfMissing(values, result.DerivedValues, "ESPECIALIDADE");
            FillIfMissing(values, result.DerivedValues, "ESPECIE_DA_PERICIA");
            FillIfMissing(values, result.DerivedValues, "FATOR");
            FillIfMissing(values, result.DerivedValues, "VALOR_TABELADO_ANEXO_I");
            FillIfMissing(values, result.DerivedValues, "VALOR_ARBITRADO_JZ");

            if (!string.IsNullOrWhiteSpace(valorDerivado) &&
                TryGetValue(values, "VALOR_ARBITRADO_JZ", out var curJz) &&
                LooksLikeFactorValue(curJz))
            {
                values["VALOR_ARBITRADO_JZ"] = valorDerivado;
            }

            if (!TryGetValue(values, "VALOR_ARBITRADO_FINAL", out var curFinal) || string.IsNullOrWhiteSpace(curFinal))
            {
                if (TryGetValue(values, "VALOR_ARBITRADO_DE", out var de) && LooksLikeMoneyValue(de))
                    values["VALOR_ARBITRADO_FINAL"] = de;
                else if (TryGetValue(values, "VALOR_ARBITRADO_JZ", out var jz) && LooksLikeMoneyValue(jz))
                    values["VALOR_ARBITRADO_FINAL"] = jz;
                else if (!string.IsNullOrWhiteSpace(valorDerivado))
                    values["VALOR_ARBITRADO_FINAL"] = valorDerivado;
            }

            return result;
        }

        public static void ApplyProfissaoAsEspecialidade(IDictionary<string, string>? values)
        {
            if (values == null)
                return;

            if (TryGetValue(values, "ESPECIALIDADE", out var current) && !string.IsNullOrWhiteSpace(current))
                return;

            string? profissao = null;
            if (TryGetValue(values, "PROFISSÃO", out var profComAcento) && !string.IsNullOrWhiteSpace(profComAcento))
                profissao = profComAcento;
            else if (TryGetValue(values, "PROFISSAO", out var profSemAcento) && !string.IsNullOrWhiteSpace(profSemAcento))
                profissao = profSemAcento;

            if (string.IsNullOrWhiteSpace(profissao))
                return;

            values["ESPECIALIDADE"] = TextUtils.NormalizeWhitespace(profissao);
        }

        public static string NormalizeDerivedMoney(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "";
            var s = raw.Trim();
            if (s.StartsWith("R$", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2).Trim();
            return s;
        }

        public static bool LooksLikeFactorValue(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            var s = raw.Trim();
            if (s.StartsWith("R$", StringComparison.OrdinalIgnoreCase))
                return false;
            if (!Regex.IsMatch(s, @"^\d{1,2}(?:[,.]\d{1,2})?$"))
                return false;
            if (!TextUtils.TryParseMoney(s, out var val))
                return false;
            return val > 0m && val <= 10m;
        }

        public static bool LooksLikeMoneyValue(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var s = raw.Trim();
            if (s.StartsWith("R$", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(2).Trim();

            if (LooksLikeFactorValue(s))
                return false;

            if (!TextUtils.TryParseMoney(s, out var val))
                return false;
            return val > 10m;
        }

        public static string NormalizeDocType(string? docType)
        {
            if (string.IsNullOrWhiteSpace(docType))
                return "";

            var key = docType.Trim().ToUpperInvariant();
            if (key.Contains("DESPACHO", StringComparison.Ordinal))
                return "DESPACHO";
            if (key.Contains("REQUERIMENTO", StringComparison.Ordinal))
                return "REQUERIMENTO_HONORARIOS";
            if (key.Contains("CERTIDAO", StringComparison.Ordinal) || key.Contains("CERTIDÃO", StringComparison.Ordinal))
                return "CERTIDAO_CM";
            return key;
        }

        private static void SetDerived(HonorariosBackfillResult result, string field, string? value, double confidence)
        {
            if (result == null || string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(value))
                return;

            var normalized = TextUtils.NormalizeWhitespace(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            result.DerivedValues[field] = normalized;
            result.FieldConfidence[field] = confidence > 0 ? Math.Min(1.0, confidence) : 0.90;
        }

        private static void FillIfMissing(IDictionary<string, string> values, IDictionary<string, string> derived, string field)
        {
            if (values == null || derived == null || string.IsNullOrWhiteSpace(field))
                return;

            if (TryGetValue(values, field, out var existing) && !string.IsNullOrWhiteSpace(existing))
                return;

            if (derived.TryGetValue(field, out var candidate) && !string.IsNullOrWhiteSpace(candidate))
                values[field] = candidate;
        }

        private static bool TryGetValue(IDictionary<string, string> values, string key, out string value)
        {
            value = "";
            if (values.TryGetValue(key, out var direct))
            {
                value = direct ?? "";
                return true;
            }

            foreach (var kv in values)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value ?? "";
                    return true;
                }
            }

            return false;
        }
    }
}
