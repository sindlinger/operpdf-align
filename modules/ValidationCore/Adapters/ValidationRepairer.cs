using System;
using System.Collections.Generic;
using System.Linq;
using Obj.TjpbDespachoExtractor.Reference;

namespace Obj.ValidationCore
{
    public static class ValidationRepairer
    {
        public sealed class RepairOutcome
        {
            public bool Applied { get; set; }
            public bool Ok { get; set; }
            public string Reason { get; set; } = "";
            public List<string> ChangedFields { get; set; } = new List<string>();
            public Dictionary<string, string> ClearedOptionalCandidates { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> Flow { get; set; } = new List<string>();
            public bool LegacyMirrorOk { get; set; }
            public string LegacyMirrorReason { get; set; } = "";
            public List<string> LegacyChangedFields { get; set; } = new List<string>();
            public bool LegacyMirrorMatchesCore { get; set; }
        }

        public static RepairOutcome ApplyWithValidatorRules(
            IDictionary<string, string>? values,
            string? outputDocType,
            PeritoCatalog? catalog)
        {
            var outcome = new RepairOutcome();

            if (values == null)
            {
                outcome.Ok = false;
                outcome.Reason = "values_null";
                return outcome;
            }

            var ok = ValidationEngine.ApplyAndValidateDocumentValues(
                values,
                outputDocType,
                catalog,
                out var reason,
                out var changedFields);

            // Optional-field safeguard: keep the original value for auditability.
            // We do not clear optional fields automatically to avoid dropping potentially correct data.
            if (!ok && TryGetReasonField(reason, out var reasonField) && !IsRequiredFieldForDoc(reasonField, outputDocType))
            {
                if (TryGetValueIgnoreCase(values, reasonField, out var current) && !string.IsNullOrWhiteSpace(current))
                {
                    outcome.ClearedOptionalCandidates[reasonField] = current;
                }
            }

            outcome.Ok = ok;
            outcome.Reason = reason ?? "";
            outcome.ChangedFields = changedFields ?? new List<string>();
            outcome.Applied = outcome.ChangedFields.Count > 0;

            var flow = ValidationEngine.ExplainDocumentValidationFlow(
                new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase),
                outputDocType,
                catalog,
                out _,
                out _);
            outcome.Flow = flow ?? new List<string>();

            var legacyValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in values)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    legacyValues[kv.Key] = kv.Value ?? "";
            }

            var legacyOk = Obj.ValidatorModule.ValidatorFacade.ApplyAndValidateDocumentValues(
                legacyValues,
                outputDocType,
                catalog,
                out var legacyReason,
                out var legacyChanged);

            outcome.LegacyMirrorOk = legacyOk;
            outcome.LegacyMirrorReason = legacyReason ?? "";
            outcome.LegacyChangedFields = legacyChanged ?? new List<string>();
            outcome.LegacyMirrorMatchesCore =
                legacyOk == ok &&
                string.Equals(NormReason(legacyReason), NormReason(reason), StringComparison.OrdinalIgnoreCase);

            return outcome;
        }

        private static bool TryGetReasonField(string? reason, out string field)
        {
            field = "";
            var text = (reason ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var idx = text.IndexOf(':');
            if (idx <= 0)
                return false;
            var candidate = text.Substring(0, idx).Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                return false;
            field = candidate;
            return true;
        }

        private static bool IsRequiredFieldForDoc(string field, string? outputDocType)
        {
            if (string.IsNullOrWhiteSpace(field))
                return false;
            var f = field.Trim();
            var doc = DocumentValidationRules.MapOutputTypeToDocKey(outputDocType);

            if (DocumentValidationRules.IsDocMatch(doc, DocumentValidationRules.DocKeyDespacho))
            {
                return string.Equals(f, "PROCESSO_ADMINISTRATIVO", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f, "PROCESSO_JUDICIAL", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f, "PERITO", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f, "PROMOVENTE", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f, "VARA", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f, "COMARCA", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f, "VALOR_ARBITRADO_JZ", StringComparison.OrdinalIgnoreCase);
            }

            if (DocumentValidationRules.IsDocMatch(doc, DocumentValidationRules.DocKeyCertidaoConselho))
            {
                if (string.Equals(f, "PROCESSO_ADMINISTRATIVO", StringComparison.OrdinalIgnoreCase))
                    return true;
                // One-of required in certidão: keep both protected.
                if (string.Equals(f, "VALOR_ARBITRADO_CM", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(f, "DATA_AUTORIZACAO_CM", StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }

            if (DocumentValidationRules.IsDocMatch(doc, DocumentValidationRules.DocKeyRequerimentoHonorarios))
            {
                return string.Equals(f, "PROCESSO_ADMINISTRATIVO", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f, "DATA_REQUISICAO", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static bool TryGetValueIgnoreCase(IDictionary<string, string> values, string key, out string value)
        {
            if (values.TryGetValue(key, out value!))
                return true;

            foreach (var kv in values)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value ?? "";
                    return true;
                }
            }

            value = "";
            return false;
        }

        private static void SetValueIgnoreCase(IDictionary<string, string> values, string key, string value)
        {
            if (values.ContainsKey(key))
            {
                values[key] = value;
                return;
            }

            foreach (var kv in values.ToList())
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    values[kv.Key] = value;
                    return;
                }
            }

            values[key] = value;
        }

        private static string NormReason(string? reason)
        {
            return (reason ?? "").Trim();
        }

        public static string DescribeChangedFields(IReadOnlyList<string>? fields, int maxItems = 16)
        {
            if (fields == null || fields.Count == 0)
                return "(nenhum)";

            var ordered = fields
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ordered.Count == 0)
                return "(nenhum)";
            if (ordered.Count <= maxItems)
                return string.Join(", ", ordered);

            return string.Join(", ", ordered.Take(maxItems)) + $" ... (+{ordered.Count - maxItems})";
        }
    }
}
