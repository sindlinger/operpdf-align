using System.Collections.Generic;
using Obj.TjpbDespachoExtractor.Reference;

namespace Obj.ValidatorModule
{
    public static class ValidatorFacade
    {
        public static PeritoCatalog? GetPeritoCatalog(string? configPath = null)
        {
            return ValidatorContext.GetPeritoCatalog(configPath);
        }

        public static bool IsValueValidForField(
            string field,
            string value,
            PeritoCatalog? catalog,
            System.Func<string, string, string>? normalizeValueByField,
            System.Func<string, string, bool>? isValidFieldFormat,
            out string reason)
        {
            return ValidatorRules.IsValueValidForField(
                field,
                value,
                catalog,
                normalizeValueByField,
                isValidFieldFormat,
                out reason);
        }

        public static bool ShouldRejectByValidator(
            Dictionary<string, string>? values,
            HashSet<string>? optionalFields,
            string? patternsPath,
            PeritoCatalog? catalog,
            ValidatorRules.FieldValueValidator isValueValidForField,
            out string reason)
        {
            return ValidatorRules.ShouldRejectByValidator(
                values,
                optionalFields,
                patternsPath,
                catalog,
                isValueValidForField,
                out reason);
        }

        public static bool PassesDocumentValidator(
            IReadOnlyDictionary<string, string>? inputValues,
            string? outputDocType,
            PeritoCatalog? catalog,
            out string reason)
        {
            return ValidatorRules.PassesDocumentValidator(inputValues, outputDocType, catalog, out reason);
        }
    }
}
