using System.Collections.Generic;

namespace Obj.Honorarios
{
    public static class HonorariosFacade
    {
        public static void ApplyProfissaoAsEspecialidade(IDictionary<string, string>? values)
        {
            HonorariosBackfill.ApplyProfissaoAsEspecialidade(values);
        }

        public static HonorariosBackfillResult ApplyBackfill(IDictionary<string, string> values, string? docTypeOrPattern)
        {
            return HonorariosBackfill.Apply(values, docTypeOrPattern);
        }

        public static HonorariosSummary RunFromMapFields(string? mapFieldsPath, string docType, string? configPath = null)
        {
            return HonorariosEnricher.Run(mapFieldsPath, docType, configPath);
        }

        public static HonorariosSummary RunFromMapFields(string? mapFieldsPath, string? configPath = null)
        {
            return HonorariosEnricher.Run(mapFieldsPath, configPath);
        }

        public static HonorariosSummary RunFromValues(Dictionary<string, string> values, string docType, string? configPath = null)
        {
            return HonorariosEnricher.RunFromValues(values, docType, configPath);
        }
    }
}
