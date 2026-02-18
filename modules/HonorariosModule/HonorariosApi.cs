using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Obj.Honorarios
{
    public sealed class HonorariosRequest
    {
        public string DocType { get; set; } = "DESPACHO";
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? ConfigPath { get; set; }
    }

    public sealed class HonorariosResponse
    {
        public string Status { get; set; } = "";
        public Dictionary<string, string> Resolved { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HonorariosPeritoCheck? PeritoCheck { get; set; }
        public List<string> Derivations { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public HonorariosSummary? Summary { get; set; }
    }

    public static class HonorariosApi
    {
        public static HonorariosRequest ParseRequest(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new HonorariosRequest();

            try
            {
                var req = JsonSerializer.Deserialize<HonorariosRequest>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return req ?? new HonorariosRequest();
            }
            catch
            {
                return new HonorariosRequest();
            }
        }

        public static HonorariosResponse Resolve(HonorariosRequest? request)
        {
            var req = request ?? new HonorariosRequest();
            var values = req.Values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var summary = HonorariosEnricher.RunFromValues(values, req.DocType ?? "DESPACHO", req.ConfigPath);
            var side = summary.PdfA;

            var resp = new HonorariosResponse
            {
                Status = side?.Status ?? "error",
                PeritoCheck = side?.PeritoCheck,
                Derivations = side?.Derivations ?? new List<string>(),
                Errors = summary.Errors ?? new List<string>(),
                Summary = summary
            };

            if (side == null)
                return resp;

            SetResolved(resp, "PERITO", side.PeritoName, side.PeritoNameSource);
            SetResolved(resp, "CPF_PERITO", side.PeritoCpf, side.PeritoCpfSource);
            SetResolved(resp, "ESPECIALIDADE", side.Especialidade, side.EspecialidadeSource);
            SetResolved(resp, "ESPECIE_DA_PERICIA", side.EspecieDaPericia, "honorarios");
            SetResolved(resp, "FATOR", side.Fator, "honorarios");
            SetResolved(resp, "VALOR_TABELADO_ANEXO_I", side.ValorTabeladoAnexoI, "honorarios");

            return resp;
        }

        private static void SetResolved(HonorariosResponse resp, string key, string value, string source)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            resp.Resolved[key] = value;
            if (!string.IsNullOrWhiteSpace(source))
                resp.Sources[key] = source;
        }
    }
}
