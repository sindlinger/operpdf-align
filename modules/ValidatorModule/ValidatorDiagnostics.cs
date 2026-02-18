using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Obj.ValidatorModule
{
    public static class ValidatorDiagnostics
    {
        public static List<string> CollectSummaryIssues(IReadOnlyDictionary<string, string>? values)
        {
            var issues = new List<string>();
            if (values == null)
                return issues;

            if (TryGet(values, "CPF_PERITO", out var cpf))
            {
                var digits = Regex.Replace(cpf ?? "", "[^0-9]", "");
                if (digits.Length > 0 && digits.Length != 11)
                    issues.Add("CPF_PERITO:cpf_invalid");
            }

            if (TryGet(values, "PROMOVENTE", out var promovente) && ValidatorRules.ContainsInstitutional(promovente))
                issues.Add("PROMOVENTE:institutional_in_party");

            if (TryGet(values, "PROMOVIDO", out var promovido) && ValidatorRules.ContainsInstitutional(promovido))
                issues.Add("PROMOVIDO:institutional_in_party");

            if (TryGet(values, "PERITO", out var perito) && ValidatorRules.ContainsInstitutional(perito))
                issues.Add("PERITO:institutional_in_name");

            return issues;
        }

        private static bool TryGet(IReadOnlyDictionary<string, string> values, string key, out string value)
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
