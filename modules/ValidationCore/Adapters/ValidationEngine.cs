using System.Collections.Generic;
using Obj.TjpbDespachoExtractor.Reference;

namespace Obj.ValidationCore
{
    public static class ValidationEngine
    {
        public static PeritoCatalog? GetPeritoCatalog(string? configPath = null)
        {
            return ValidatorFacade.GetPeritoCatalog(configPath);
        }

        public static bool ApplyAndValidateDocumentValues(
            IDictionary<string, string>? values,
            string? outputDocType,
            PeritoCatalog? catalog,
            out string reason,
            out List<string> changedFields)
        {
            return ValidatorFacade.ApplyAndValidateDocumentValues(
                values,
                outputDocType,
                catalog,
                out reason,
                out changedFields);
        }

        public static bool PassesDocumentValidator(
            IReadOnlyDictionary<string, string>? inputValues,
            string? outputDocType,
            PeritoCatalog? catalog,
            out string reason)
        {
            return ValidatorFacade.PassesDocumentValidator(
                inputValues,
                outputDocType,
                catalog,
                out reason);
        }

        public static IReadOnlyList<string> GetSupportedFieldValidationKeys()
        {
            return ValidatorFacade.GetSupportedFieldValidationKeys();
        }

        public static IReadOnlyList<string> GetSupportedDocumentValidationProfiles()
        {
            return ValidatorFacade.GetSupportedDocumentValidationProfiles();
        }

        public static List<string> ExplainDocumentValidationFlow(
            IReadOnlyDictionary<string, string>? inputValues,
            string? outputDocType,
            PeritoCatalog? catalog,
            out bool ok,
            out string reason)
        {
            return ValidatorFacade.ExplainDocumentValidationFlow(inputValues, outputDocType, catalog, out ok, out reason);
        }
    }
}
