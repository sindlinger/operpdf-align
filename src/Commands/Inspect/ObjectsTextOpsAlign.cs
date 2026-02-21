using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiffMatchPatch;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Obj.Align;
using Obj.DocDetector;
using Obj.Honorarios;
using Obj.RootProbe;
using Obj.Utils;
using Obj.ValidatorModule;

namespace Obj.Commands
{
    internal static class ObjectsTextOpsAlign
    {
        internal static int LastExitCode { get; private set; } = 0;
        private const string AnsiReset = "\u001b[0m";
        private const string AnsiCodexBlue = "\u001b[38;5;39m";
        private const string AnsiClaudeOrange = "\u001b[38;5;214m";
        private const string AnsiDodgeBlue = "\u001b[38;2;30;144;255m";
        private const string AnsiInfo = AnsiCodexBlue;
        private const string AnsiOk = "\u001b[1;92m";
        private const string AnsiWarn = AnsiClaudeOrange;
        private const string AnsiErr = "\u001b[1;91m";
        private const string AnsiSoft = "\u001b[38;5;246m";

        internal enum OutputMode
        {
            All,
            VariablesOnly,
            FixedOnly
        }

        private const int PipelineFirstStep = 1;
        private const int PipelineLastStep = 8;

        private static string Colorize(string text, string color)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            return $"{color}{text}{AnsiReset}";
        }

        private static void PrintStage(string message)
        {
            Console.WriteLine(Colorize($"[STAGE] {message}", AnsiInfo));
        }

        private static void PrintPipelineStep(string step, string nextStep, params (string Key, string Value)[] values)
        {
            Console.WriteLine(Colorize($"[PIPELINE] {step}", AnsiInfo));
            foreach (var (key, value) in values)
            {
                var formattedValue = value ?? "";
                if (string.Equals(key, "modulo", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, "module", StringComparison.OrdinalIgnoreCase))
                    formattedValue = ColorizeModuleChain(formattedValue);
                else if (string.Equals(key, "scope", StringComparison.OrdinalIgnoreCase))
                    formattedValue = Colorize(formattedValue, AnsiCodexBlue);
                Console.WriteLine($"  {Colorize(key + ":", AnsiWarn)} {formattedValue}");
            }
            if (!string.IsNullOrWhiteSpace(nextStep))
                Console.WriteLine($"  {Colorize("usa no próximo:", AnsiWarn)} {Colorize(nextStep, AnsiSoft)}");
            Console.WriteLine();
        }

        private static void PrintResolvedParameters(params (string Key, string Value)[] items)
        {
            Console.WriteLine(Colorize("[PARAMETROS]", AnsiInfo));
            foreach (var (key, value) in items)
                Console.WriteLine($"  {Colorize(key + ":", AnsiWarn)} {Colorize(value ?? "", AnsiSoft)}");
            Console.WriteLine();
        }

        private static void PrintBoolParameters(params (string Key, bool Value)[] flags)
        {
            Console.WriteLine(Colorize("[PARAMETROS BOOL]", AnsiInfo));
            foreach (var (key, value) in flags)
            {
                var text = value ? "true" : "false";
                var color = value ? AnsiOk : AnsiSoft;
                Console.WriteLine($"  {Colorize(key + ":", AnsiWarn)} {Colorize(text, color)}");
            }
            Console.WriteLine();
        }

        private static void PrintAppliedAutoDefaults(List<string> applied)
        {
            Console.WriteLine(Colorize("[DEFAULTS AUTOMATICOS]", AnsiInfo));
            if (applied == null || applied.Count == 0)
            {
                Console.WriteLine($"  {Colorize("nenhum", AnsiSoft)}");
                Console.WriteLine();
                return;
            }

            foreach (var line in applied)
                Console.WriteLine($"  {Colorize(line, AnsiSoft)}");
            Console.WriteLine();
        }

        private static string ResolveModuleColor(string moduleName)
        {
            var norm = (moduleName ?? "").Trim().ToLowerInvariant();
            if (norm.Length == 0)
                return AnsiSoft;
            if (norm.Contains("docdetector", StringComparison.Ordinal) ||
                norm.Contains("finddespacho", StringComparison.Ordinal) ||
                norm.Contains("contentsstreampicker", StringComparison.Ordinal))
                return AnsiCodexBlue;
            if (norm.Contains("align", StringComparison.Ordinal) || norm.Contains("textopsdiff", StringComparison.Ordinal))
                return "\u001b[38;5;75m";
            if (norm.Contains("objectsmapfields", StringComparison.Ordinal))
                return "\u001b[38;5;44m";
            if (norm.Contains("honorarios", StringComparison.Ordinal))
                return "\u001b[38;5;71m";
            if (norm.Contains("validationrepairer", StringComparison.Ordinal) || norm.Contains("repairer", StringComparison.Ordinal))
                return "\u001b[38;5;141m";
            if (norm.Contains("validator", StringComparison.Ordinal))
                return "\u001b[38;5;203m";
            if (norm.Contains("probe", StringComparison.Ordinal))
                return "\u001b[38;5;111m";
            if (norm.Contains("jsonserializer", StringComparison.Ordinal))
                return "\u001b[38;5;250m";
            return "\u001b[38;5;153m";
        }

        private static string ColorizeModuleChain(string moduleChain)
        {
            if (string.IsNullOrWhiteSpace(moduleChain))
                return "";

            var tokens = Regex.Split(moduleChain, "(\\s*[+|]\\s*)");
            var sb = new StringBuilder(moduleChain.Length + 32);
            foreach (var token in tokens)
            {
                if (string.IsNullOrEmpty(token))
                    continue;

                if (token.Contains('+') || token.Contains('|'))
                {
                    sb.Append(Colorize(token, AnsiSoft));
                    continue;
                }

                var core = token.Trim();
                if (core.Length == 0)
                {
                    sb.Append(token);
                    continue;
                }

                var leading = token.Substring(0, token.IndexOf(core, StringComparison.Ordinal));
                var trailing = token.Substring(token.IndexOf(core, StringComparison.Ordinal) + core.Length);
                sb.Append(leading);
                sb.Append(Colorize(core, ResolveModuleColor(core)));
                sb.Append(trailing);
            }

            return sb.ToString();
        }

        private static string ResolveStageKey(int step)
        {
            return step switch
            {
                1 => "detection_selection",
                2 => "alignment",
                3 => "extraction",
                4 => "honorarios",
                5 => "repairer",
                6 => "validator",
                7 => "probe",
                8 => "output",
                _ => $"step_{step}"
            };
        }

        private static string ResolveStageLabel(int step)
        {
            return step switch
            {
                1 => "detecção e seleção",
                2 => "alinhamento",
                3 => "extração",
                4 => "honorários",
                5 => "reparador",
                6 => "validador",
                7 => "probe",
                8 => "persistência e resumo",
                _ => $"etapa {step}"
            };
        }

        private static bool TryParseRunRange(string? raw, out int fromStep, out int toStep)
        {
            fromStep = PipelineFirstStep;
            toStep = PipelineLastStep;

            var t = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(t))
                return false;

            t = t.Replace("..", "-", StringComparison.Ordinal)
                .Replace(":", "-", StringComparison.Ordinal)
                .Replace("_", "-", StringComparison.Ordinal);

            var parts = t.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts.Length > 2)
                return false;

            if (parts.Length == 1)
            {
                if (!int.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var only))
                    return false;
                if (only < PipelineFirstStep || only > PipelineLastStep)
                    return false;
                fromStep = PipelineFirstStep;
                toStep = only;
                return true;
            }

            if (!int.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var start))
                return false;
            if (!int.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var end))
                return false;

            if (start < PipelineFirstStep || start > PipelineLastStep)
                return false;
            if (end < PipelineFirstStep || end > PipelineLastStep)
                return false;
            if (start > end)
                return false;

            fromStep = start;
            toStep = end;
            return true;
        }

        private static Dictionary<string, object> BuildStageOutput(
            int step,
            string status,
            IDictionary<string, object>? payload = null,
            string? stageKey = null,
            string? stageLabel = null)
        {
            var output = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["step"] = step,
                ["stage_key"] = string.IsNullOrWhiteSpace(stageKey) ? ResolveStageKey(step) : stageKey!,
                ["stage"] = string.IsNullOrWhiteSpace(stageLabel) ? ResolveStageLabel(step) : stageLabel!,
                ["status"] = string.IsNullOrWhiteSpace(status) ? "ok" : status,
                ["timestamp_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["payload"] = payload != null
                    ? new Dictionary<string, object>(payload, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            };
            return output;
        }

        private static void EmitStageOutput(
            List<Dictionary<string, object>> sink,
            Dictionary<string, object> stageOutput,
            bool echoJson,
            int displayFromStep,
            int displayToStep)
        {
            sink.Add(stageOutput);
            if (!echoJson || ReturnUtils.IsEnabled())
                return;

            if (!stageOutput.TryGetValue("step", out var stepObj))
                return;
            if (!TryGetInt(stepObj, out var step))
                return;
            if (step < displayFromStep || step > displayToStep)
                return;

            var stageLabel = stageOutput.TryGetValue("stage", out var stageObj) ? stageObj?.ToString() ?? "" : "";
            Console.WriteLine(Colorize($"[STEP_OUTPUT] {step} - {stageLabel}", AnsiInfo));
            var json = JsonSerializer.Serialize(
                stageOutput,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            Console.WriteLine(json);
            Console.WriteLine();
        }

        private static bool TryGetInt(object? value, out int parsed)
        {
            parsed = 0;
            if (value == null)
                return false;
            if (value is int i)
            {
                parsed = i;
                return true;
            }
            if (value is long l && l >= int.MinValue && l <= int.MaxValue)
            {
                parsed = (int)l;
                return true;
            }
            return int.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
        }

        private static string SanitizeFileToken(string? raw)
        {
            var value = TextNormalization.NormalizeWhitespace(raw ?? "");
            if (string.IsNullOrWhiteSpace(value))
                return "step";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (invalid.Contains(ch) || char.IsWhiteSpace(ch))
                    sb.Append('_');
                else
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        private static void SaveStageOutputs(
            string outputDir,
            string baseA,
            string baseB,
            List<Dictionary<string, object>> stageOutputs)
        {
            if (string.IsNullOrWhiteSpace(outputDir) || stageOutputs == null || stageOutputs.Count == 0)
                return;

            Directory.CreateDirectory(outputDir);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var prefix = $"{baseA}__{baseB}";
            for (var i = 0; i < stageOutputs.Count; i++)
            {
                var stage = stageOutputs[i];
                var step = stage.TryGetValue("step", out var stepObj) && TryGetInt(stepObj, out var pStep)
                    ? pStep
                    : (i + 1);
                var key = stage.TryGetValue("stage_key", out var keyObj) ? keyObj?.ToString() ?? "stage" : "stage";
                var file = $"{prefix}__step{step:D2}_{SanitizeFileToken(key)}.json";
                var path = Path.Combine(outputDir, file);
                var json = JsonSerializer.Serialize(stage, options);
                File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }

            var combinedPath = Path.Combine(outputDir, $"{prefix}__pipeline_steps.json");
            var combinedJson = JsonSerializer.Serialize(stageOutputs, options);
            File.WriteAllText(combinedPath, combinedJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            if (!ReturnUtils.IsEnabled())
                Console.WriteLine("Arquivos das etapas salvos em: " + outputDir);
        }

        private static string ShortText(string? text, int max = 140)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "\"\"";
            var t = TextNormalization.NormalizeWhitespace(text);
            if (t.Length <= max)
                return "\"" + t + "\"";
            return "\"" + t.Substring(0, max - 3) + "...\"";
        }

        private static IEnumerable<string> ResolveKnownModelDirectories()
        {
            var rawDirs = new List<string>();

            void AddFromEnv(string key)
            {
                var value = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrWhiteSpace(value))
                    rawDirs.Add(value);
            }

            void AddDirOfModelEnv(string key)
            {
                var value = Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrWhiteSpace(value))
                    return;
                try
                {
                    var full = Path.GetFullPath(value);
                    var dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrWhiteSpace(dir))
                        rawDirs.Add(dir);
                }
                catch
                {
                    var dir = Path.GetDirectoryName(value);
                    if (!string.IsNullOrWhiteSpace(dir))
                        rawDirs.Add(dir);
                }
            }

            AddFromEnv("OBJPDF_MODELS_DES_DIR");
            AddFromEnv("OBJPDF_MODELS_CER_DIR");
            AddFromEnv("OBJPDF_MODELS_REQ_DIR");
            AddFromEnv("OBJPDF_MODELS_DESPACHO_DIR");
            AddFromEnv("OBJPDF_MODELS_CERTIDAO_DIR");
            AddFromEnv("OBJPDF_MODELS_REQUERIMENTO_DIR");
            AddFromEnv("OBJPDF_MODELS_DIR");
            AddDirOfModelEnv("OBJPDF_MODEL_DESPACHO");
            AddDirOfModelEnv("OBJPDF_MODEL_CERTIDAO");
            AddDirOfModelEnv("OBJPDF_MODEL_REQUERIMENTO");
            AddDirOfModelEnv("OBJPDF_MODEL");

            try
            {
                var cwd = Directory.GetCurrentDirectory();
                if (!string.IsNullOrWhiteSpace(cwd))
                {
                    rawDirs.Add(Path.Combine(cwd, "models", "aliases", "despacho"));
                    rawDirs.Add(Path.Combine(cwd, "models", "aliases", "certidao"));
                    rawDirs.Add(Path.Combine(cwd, "models", "aliases", "requerimento"));
                    rawDirs.Add(Path.Combine(cwd, "models", "nossos"));
                }
            }
            catch
            {
                // ignore cwd fallback
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in rawDirs)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                string full;
                try
                {
                    full = Path.GetFullPath(raw).Replace('\\', '/');
                }
                catch
                {
                    full = raw.Replace('\\', '/');
                }

                if (!full.EndsWith("/", StringComparison.Ordinal))
                    full += "/";
                if (seen.Add(full))
                    yield return full;
            }
        }

        private static bool IsPathUnderKnownModelDirectory(string fullPathNormalized)
        {
            if (string.IsNullOrWhiteSpace(fullPathNormalized))
                return false;

            var path = fullPathNormalized.Replace('\\', '/');
            if (!path.EndsWith("/", StringComparison.Ordinal))
                path += "/";

            foreach (var modelDir in ResolveKnownModelDirectories())
            {
                if (path.StartsWith(modelDir, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string DetectInputRole(string path, bool preferTemplateForFirstInput = false)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "pdf_alvo_extracao";

            var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            var full = "";
            try
            {
                full = Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
            }
            catch
            {
                full = (path ?? "").Replace('\\', '/').ToLowerInvariant();
            }

            var envModelDespacho = (Environment.GetEnvironmentVariable("OBJPDF_MODEL_DESPACHO") ?? "").Replace('\\', '/').ToLowerInvariant();
            var envModelCertidao = (Environment.GetEnvironmentVariable("OBJPDF_MODEL_CERTIDAO") ?? "").Replace('\\', '/').ToLowerInvariant();
            var envModelReq = (Environment.GetEnvironmentVariable("OBJPDF_MODEL_REQUERIMENTO") ?? "").Replace('\\', '/').ToLowerInvariant();
            var envModelGeneric = (Environment.GetEnvironmentVariable("OBJPDF_MODEL") ?? "").Replace('\\', '/').ToLowerInvariant();

            if ((!string.IsNullOrWhiteSpace(envModelDespacho) && full == envModelDespacho) ||
                (!string.IsNullOrWhiteSpace(envModelCertidao) && full == envModelCertidao) ||
                (!string.IsNullOrWhiteSpace(envModelReq) && full == envModelReq) ||
                (!string.IsNullOrWhiteSpace(envModelGeneric) && full == envModelGeneric) ||
                name.Contains("model", StringComparison.Ordinal) ||
                name.Contains("modelo", StringComparison.Ordinal) ||
                name.Contains("anchor", StringComparison.Ordinal) ||
                name.Contains("template", StringComparison.Ordinal) ||
                IsPathUnderKnownModelDirectory(full))
            {
                return "template_modelo";
            }

            if (preferTemplateForFirstInput)
                return "template_modelo";

            return "pdf_alvo_extracao";
        }

        private static bool IsTemplateRole(string? role)
        {
            return string.Equals(role?.Trim(), "template_modelo", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTargetRole(string? role)
        {
            return string.Equals(role?.Trim(), "pdf_alvo_extracao", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveExtractionScopeTag(string? roleA, string? roleB)
        {
            if (IsTemplateRole(roleA) && IsTargetRole(roleB))
                return "target_b_only(model_a_reference)";
            if (IsTargetRole(roleA) && IsTemplateRole(roleB))
                return "target_a_only(model_b_reference)";

            // Fallback operacional: por convenção o primeiro input é referência/modelo e o
            // segundo é o PDF alvo. Evita exibir extração do template em cenários ambíguos.
            return "target_b_only(model_a_reference)";
        }

        private static string ResolveTargetSideFromScope(string scopeTag)
        {
            if (string.Equals(scopeTag, "target_a_only(model_b_reference)", StringComparison.OrdinalIgnoreCase))
                return "pdf_a";
            return "pdf_b";
        }

        private static int CountNonEmptyValues(Dictionary<string, string>? values)
        {
            if (values == null || values.Count == 0)
                return 0;
            return values.Count(kv => !string.IsNullOrWhiteSpace(kv.Value));
        }

        private static string DescribeChangedKeys(
            Dictionary<string, string> before,
            Dictionary<string, string> after,
            int maxKeys = 8)
        {
            var changed = new List<string>();
            foreach (var kv in after)
            {
                var oldValue = before.TryGetValue(kv.Key, out var old) ? old ?? "" : "";
                var newValue = kv.Value ?? "";
                if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                    changed.Add(kv.Key);
            }

            if (changed.Count == 0)
                return "(nenhum)";
            changed = changed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (changed.Count <= maxKeys)
                return string.Join(", ", changed);
            return string.Join(", ", changed.Take(maxKeys)) + $" ... (+{changed.Count - maxKeys})";
        }

        private static string GetValueIgnoreCase(IDictionary<string, string>? values, string key)
        {
            if (values == null || string.IsNullOrWhiteSpace(key))
                return "";
            if (values.TryGetValue(key, out var direct))
                return direct ?? "";
            foreach (var kv in values)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value ?? "";
            }
            return "";
        }

        private static bool SameNormalizedValue(string? left, string? right)
        {
            var l = TextNormalization.NormalizeWhitespace(left ?? "");
            var r = TextNormalization.NormalizeWhitespace(right ?? "");
            return string.Equals(l, r, StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> BuildFieldFlow(
            IDictionary<string, string> parserValues,
            IDictionary<string, string> finalValues,
            IDictionary<string, string>? derivedValues,
            string field)
        {
            var parserValue = GetValueIgnoreCase(parserValues, field);
            var finalValue = GetValueIgnoreCase(finalValues, field);
            var derivedValue = GetValueIgnoreCase(derivedValues, field);

            var parserHas = !string.IsNullOrWhiteSpace(parserValue);
            var finalHas = !string.IsNullOrWhiteSpace(finalValue);
            var derivedHas = !string.IsNullOrWhiteSpace(derivedValue);
            var applied = false;
            var decision = "nao_encontrado";

            if (parserHas)
            {
                if (!SameNormalizedValue(finalValue, parserValue))
                {
                    if (derivedHas && SameNormalizedValue(finalValue, derivedValue))
                    {
                        applied = true;
                        decision = "derivado_aplicado";
                    }
                    else
                    {
                        applied = true;
                        decision = "ajustado_pos_parser";
                    }
                }
                else if (derivedHas && !SameNormalizedValue(parserValue, derivedValue))
                    decision = "nao_aplicado_parser_prioritario";
                else
                    decision = "mantido_parser";
            }
            else if (derivedHas && finalHas && SameNormalizedValue(finalValue, derivedValue))
            {
                applied = true;
                decision = "derivado_aplicado";
            }
            else if (!parserHas && finalHas && !derivedHas)
            {
                decision = "preenchido_outro_modulo";
            }
            else if (derivedHas && !finalHas)
            {
                decision = "derivado_nao_aplicado";
            }

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["parser"] = parserValue,
                ["derived_candidate"] = derivedValue,
                ["final"] = finalValue,
                ["applied"] = applied,
                ["decision"] = decision
            };
        }

        private static Dictionary<string, object> BuildTrackedValueFlow(
            IEnumerable<string> trackedFields,
            IDictionary<string, string> parserValues,
            IDictionary<string, string> finalValues,
            IDictionary<string, string>? derivedValues,
            IDictionary<string, ObjectsMapFields.CompactFieldOutput> fields)
        {
            var flow = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in trackedFields)
            {
                var fieldFlow = BuildFieldFlow(parserValues, finalValues, derivedValues, field);
                if (fields.TryGetValue(field, out var meta) && meta != null)
                {
                    fieldFlow["source"] = meta.Source ?? "";
                    fieldFlow["module"] = string.IsNullOrWhiteSpace(meta.Module) ? "parser" : meta.Module;
                    fieldFlow["status"] = meta.Status ?? "";
                    fieldFlow["op_range"] = meta.OpRange ?? "";
                    fieldFlow["obj"] = meta.Obj;
                }
                flow[field] = fieldFlow;
            }
            return flow;
        }

        private static Dictionary<string, object> BuildSkippedModulePayload(string module, string reason)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = false,
                ["module"] = module ?? "",
                ["status"] = "skipped",
                ["reason"] = reason ?? ""
            };
        }

        private static string DescribeFieldFlowInline(
            IDictionary<string, string> parserValues,
            IDictionary<string, string> finalValues,
            IDictionary<string, string>? derivedValues,
            string field)
        {
            var flow = BuildFieldFlow(parserValues, finalValues, derivedValues, field);
            var parser = flow.TryGetValue("parser", out var pv) ? pv?.ToString() ?? "" : "";
            var derived = flow.TryGetValue("derived_candidate", out var dv) ? dv?.ToString() ?? "" : "";
            var final = flow.TryGetValue("final", out var fv) ? fv?.ToString() ?? "" : "";
            var applied = flow.TryGetValue("applied", out var ap) && ap is bool b && b;
            var decision = flow.TryGetValue("decision", out var dec) ? dec?.ToString() ?? "" : "";
            return $"parser={ShortText(parser, 64)} derived={ShortText(derived, 64)} final={ShortText(final, 64)} applied={applied.ToString().ToLowerInvariant()} decision={decision}";
        }

        private static void MarkModuleChanges(
            IDictionary<string, string> beforeValues,
            IDictionary<string, string> afterValues,
            IDictionary<string, ObjectsMapFields.CompactFieldOutput> fields,
            string moduleTag)
        {
            if (beforeValues == null || afterValues == null || fields == null || string.IsNullOrWhiteSpace(moduleTag))
                return;

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in beforeValues.Keys)
                keys.Add(k);
            foreach (var k in afterValues.Keys)
                keys.Add(k);

            foreach (var key in keys)
            {
                var before = GetValueIgnoreCase(beforeValues, key);
                var after = GetValueIgnoreCase(afterValues, key);
                if (string.Equals(before ?? "", after ?? "", StringComparison.Ordinal))
                    continue;

                if (!fields.TryGetValue(key, out var meta) || meta == null)
                {
                    meta = new ObjectsMapFields.CompactFieldOutput
                    {
                        ValueRaw = "",
                        Value = after ?? "",
                        Source = "",
                        Module = moduleTag,
                        OpRange = "",
                        Obj = 0,
                        Status = string.IsNullOrWhiteSpace(after) ? "NOT_FOUND" : "OK",
                        BBox = null
                    };
                }

                var tag = string.IsNullOrWhiteSpace(before) && !string.IsNullOrWhiteSpace(after)
                    ? $"derived:{moduleTag}"
                    : $"adjusted:{moduleTag}";
                meta.Source = AppendPipeTag(meta.Source, tag);
                meta.Module = AppendPipeTag(meta.Module, moduleTag);
                meta.Value = after ?? "";
                if (!string.IsNullOrWhiteSpace(after))
                    meta.ValueRaw = after ?? "";
                meta.Status = string.IsNullOrWhiteSpace(after) ? "NOT_FOUND" : "OK";
                fields[key] = meta;
            }
        }

        private static string AppendPipeTag(string? existing, string? tag)
        {
            var value = (existing ?? "").Trim();
            var add = (tag ?? "").Trim();
            if (string.IsNullOrWhiteSpace(add))
                return value;
            if (string.IsNullOrWhiteSpace(value))
                return add;

            var tags = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tags.Any(t => string.Equals(t, add, StringComparison.OrdinalIgnoreCase)))
                return value;
            return value + "|" + add;
        }

        private static string DescribeMoneyPath(
            IDictionary<string, string> values,
            IDictionary<string, ObjectsMapFields.CompactFieldOutput> fields)
        {
            string Describe(string field)
            {
                var value = GetValueIgnoreCase(values, field);
                var source = fields.TryGetValue(field, out var meta) && meta != null
                    ? (string.IsNullOrWhiteSpace(meta.Source) ? "(sem source)" : meta.Source)
                    : "(sem source)";
                var op = fields.TryGetValue(field, out var meta2) && meta2 != null
                    ? (string.IsNullOrWhiteSpace(meta2.OpRange) ? "(sem op_range)" : meta2.OpRange)
                    : "(sem op_range)";
                if (string.IsNullOrWhiteSpace(value))
                    return $"{field}=<vazio> src={source} op={op}";
                return $"{field}={ShortText(value, 36)} src={source} op={op}";
            }

            return string.Join(" | ", new[]
            {
                Describe("VALOR_ARBITRADO_DE"),
                Describe("VALOR_ARBITRADO_JZ"),
                Describe("VALOR_ARBITRADO_FINAL")
            });
        }

        private static List<string> EnforceStrictArbitradoPolicy(
            IDictionary<string, string> parserValues,
            IDictionary<string, ObjectsMapFields.CompactFieldOutput> parserFields,
            IDictionary<string, string> currentValues,
            IDictionary<string, ObjectsMapFields.CompactFieldOutput> currentFields)
        {
            var changed = new List<string>();
            var strictFields = new[] { "VALOR_ARBITRADO_JZ", "VALOR_ARBITRADO_FINAL" };

            foreach (var field in strictFields)
            {
                var parserValue = GetValueIgnoreCase(parserValues, field);
                var parserSource = parserFields.TryGetValue(field, out var parserMeta) && parserMeta != null
                    ? parserMeta.Source ?? ""
                    : "";
                var parserExplicit = !string.IsNullOrWhiteSpace(parserValue) &&
                    !parserSource.Contains("derived", StringComparison.OrdinalIgnoreCase);

                var currentValue = GetValueIgnoreCase(currentValues, field);
                if (!parserExplicit)
                {
                    if (!string.IsNullOrWhiteSpace(currentValue))
                    {
                        currentValues[field] = "";
                        changed.Add($"{field}:clear_non_explicit");
                    }

                    if (!currentFields.TryGetValue(field, out var fieldMeta) || fieldMeta == null)
                    {
                        fieldMeta = new ObjectsMapFields.CompactFieldOutput
                        {
                            ValueRaw = "",
                            Value = "",
                            Source = "policy:validator:strict_found",
                            Module = "validator",
                            OpRange = "",
                            Obj = 0,
                            Status = "NOT_FOUND",
                            BBox = null
                        };
                    }
                    else
                    {
                        var tag = "policy:validator:strict_found";
                        fieldMeta.Source = AppendPipeTag(fieldMeta.Source, tag);
                        fieldMeta.Module = AppendPipeTag(fieldMeta.Module, "validator");
                        fieldMeta.Value = "";
                        fieldMeta.Status = "NOT_FOUND";
                    }
                    currentFields[field] = fieldMeta;
                    continue;
                }

                if (!SameNormalizedValue(currentValue, parserValue))
                {
                    currentValues[field] = parserValue;
                    changed.Add($"{field}:reset_parser_explicit");
                }

                if (currentFields.TryGetValue(field, out var keepMeta) && keepMeta != null)
                {
                    var tag = "policy:validator:strict_found";
                    keepMeta.Source = AppendPipeTag(keepMeta.Source, tag);
                    keepMeta.Module = AppendPipeTag(keepMeta.Module, "validator");
                    keepMeta.Value = parserValue ?? "";
                    if (string.IsNullOrWhiteSpace(keepMeta.ValueRaw) && !string.IsNullOrWhiteSpace(parserValue))
                        keepMeta.ValueRaw = parserValue ?? "";
                    keepMeta.Status = "OK";
                    currentFields[field] = keepMeta;
                }
            }

            return changed;
        }

        private static Dictionary<string, ObjectsMapFields.CompactFieldOutput> CloneFieldOutputs(
            IDictionary<string, ObjectsMapFields.CompactFieldOutput> source)
        {
            var clone = new Dictionary<string, ObjectsMapFields.CompactFieldOutput>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
                return clone;

            foreach (var kv in source)
            {
                var meta = kv.Value;
                if (meta == null)
                    continue;

                clone[kv.Key] = new ObjectsMapFields.CompactFieldOutput
                {
                    ValueRaw = meta.ValueRaw ?? "",
                    Value = meta.Value ?? "",
                    Source = meta.Source ?? "",
                    Module = meta.Module ?? "",
                    OpRange = meta.OpRange ?? "",
                    Obj = meta.Obj,
                    Status = meta.Status ?? "",
                    BBox = meta.BBox == null
                        ? null
                        : new ObjectsMapFields.CompactBoundingBox
                        {
                            X0 = meta.BBox.X0,
                            Y0 = meta.BBox.Y0,
                            X1 = meta.BBox.X1,
                            Y1 = meta.BBox.Y1,
                            StartOp = meta.BBox.StartOp,
                            EndOp = meta.BBox.EndOp,
                            Items = meta.BBox.Items
                        }
                };
            }

            return clone;
        }

        // Lightweight align helper: compute only the op_range for B, no printing.
        internal static (int StartOp, int EndOp, bool HasValue) ComputeRangeForSelections(
            string modelPdf,
            string targetPdf,
            int modelPage,
            int modelObj,
            int targetPage,
            int targetObj,
            HashSet<string> opFilter,
            int backoff = 2)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(modelPdf) || string.IsNullOrWhiteSpace(targetPdf))
                    return (0, int.MaxValue, false);
                if (!File.Exists(modelPdf) || !File.Exists(targetPdf))
                    return (0, int.MaxValue, false);
                if (modelPage <= 0 || modelObj <= 0 || targetPage <= 0 || targetObj <= 0)
                    return (0, int.MaxValue, false);

                var ops = opFilter != null && opFilter.Count > 0
                    ? opFilter
                    : new HashSet<string>(new[] { "Tj", "TJ" }, StringComparer.OrdinalIgnoreCase);

                var report = ObjectsTextOpsDiff.ComputeAlignDebugForSelection(
                    modelPdf,
                    targetPdf,
                    new ObjectsTextOpsDiff.PageObjSelection { Page = modelPage, Obj = modelObj },
                    new ObjectsTextOpsDiff.PageObjSelection { Page = targetPage, Obj = targetObj },
                    ops,
                    backoff,
                    "single_page");

                if (report?.RangeB != null && report.RangeB.HasValue && report.RangeB.StartOp > 0 && report.RangeB.EndOp > 0)
                    return (report.RangeB.StartOp, report.RangeB.EndOp, true);
            }
            catch
            {
                // Align is best-effort here; fall back to full range.
            }

            return (0, int.MaxValue, false);
        }

        public static void Execute(string[] args)
        {
            ExecuteWithMode(args, OutputMode.All);
        }

        internal static void ExecuteWithMode(string[] args, OutputMode outputMode)
        {
            LastExitCode = 0;
            Console.OutputEncoding = Encoding.UTF8;
            if (!ReturnUtils.IsEnabled())
                PrintStage("iniciando o modo de detecção");
            if (!ParseOptions(args, out var inputs, out var pageA, out var pageB, out var objA, out var objB, out var opFilter, out var backoff, out var outPath, out var outSpecified, out var top, out var minSim, out var band, out var minLenRatio, out var lenPenalty, out var anchorMinSim, out var anchorMinLenRatio, out var gapPenalty, out var showAlign, out var alignTop, out var pageAUser, out var pageBUser, out var objAUser, out var objBUser, out var docKey, out var useBack, out var sideSpecified, out var allowStack, out var probeEnabled, out var probeFile, out var probePage, out var probeSide, out var probeMaxFields, out var runFromStep, out var runToStep, out var stepOutputEcho, out var stepOutputSave, out var stepOutputDir, out var appliedAutoDefaults))
            {
                Environment.ExitCode = 2;
                LastExitCode = 2;
                ShowHelp();
                return;
            }

            var inputCountBeforeDedup = inputs.Count;
            inputs = DeduplicateInputs(inputs);
            if (!ReturnUtils.IsEnabled() && inputCountBeforeDedup != inputs.Count)
            {
                Console.WriteLine($"Aviso: entradas duplicadas removidas ({inputCountBeforeDedup - inputs.Count}).");
            }

            if (!ReturnUtils.IsEnabled())
            {
                PrintResolvedParameters(
                    ("output_mode", outputMode.ToString()),
                    ("inputs_count", inputs.Count.ToString(CultureInfo.InvariantCulture)),
                    ("inputs", inputs.Count == 0 ? "(vazio)" : string.Join(" | ", inputs)),
                    ("pageA", pageA.ToString(CultureInfo.InvariantCulture)),
                    ("pageB", pageB.ToString(CultureInfo.InvariantCulture)),
                    ("objA", objA.ToString(CultureInfo.InvariantCulture)),
                    ("objB", objB.ToString(CultureInfo.InvariantCulture)),
                    ("doc_key", string.IsNullOrWhiteSpace(docKey) ? "(vazio)" : docKey),
                    ("ops", opFilter.Count == 0 ? "(vazio)" : string.Join(",", opFilter)),
                    ("backoff", backoff.ToString(CultureInfo.InvariantCulture)),
                    ("top", top.ToString(CultureInfo.InvariantCulture)),
                    ("min_sim", ReportUtils.F(minSim, 3)),
                    ("band", band.ToString(CultureInfo.InvariantCulture)),
                    ("min_len_ratio", ReportUtils.F(minLenRatio, 3)),
                    ("len_penalty", ReportUtils.F(lenPenalty, 3)),
                    ("anchor_sim", ReportUtils.F(anchorMinSim, 3)),
                    ("anchor_len", ReportUtils.F(anchorMinLenRatio, 3)),
                    ("gap_penalty", ReportUtils.F(gapPenalty, 3)),
                    ("alinhamento_top", alignTop.ToString(CultureInfo.InvariantCulture)),
                    ("probe_file", string.IsNullOrWhiteSpace(probeFile) ? "(vazio)" : probeFile),
                    ("probe_page", probePage.ToString(CultureInfo.InvariantCulture)),
                    ("probe_side", string.IsNullOrWhiteSpace(probeSide) ? "(vazio)" : probeSide),
                    ("probe_max_fields", probeMaxFields.ToString(CultureInfo.InvariantCulture)),
                    ("run_from", runFromStep.ToString(CultureInfo.InvariantCulture)),
                    ("run_to", runToStep.ToString(CultureInfo.InvariantCulture)),
                    ("step_output_dir", string.IsNullOrWhiteSpace(stepOutputDir) ? "(vazio)" : stepOutputDir),
                    ("out_path", string.IsNullOrWhiteSpace(outPath) ? "(vazio)" : outPath)
                );

                PrintBoolParameters(
                    ("return_mode", ReturnUtils.IsEnabled()),
                    ("out_specified", outSpecified),
                    ("show_alignment", showAlign),
                    ("pageA_user", pageAUser),
                    ("pageB_user", pageBUser),
                    ("objA_user", objAUser),
                    ("objB_user", objBUser),
                    ("use_back", useBack),
                    ("side_specified", sideSpecified),
                    ("allow_stack", allowStack),
                    ("probe_enabled", probeEnabled),
                    ("step_output_echo", stepOutputEcho),
                    ("step_output_save", stepOutputSave)
                );
                PrintAppliedAutoDefaults(appliedAutoDefaults);
            }

            if (inputs.Count < 2)
            {
                Console.WriteLine("Erro: são necessários 2 PDFs distintos (modelo + alvo).");
                Environment.ExitCode = 2;
                LastExitCode = 2;
                ShowHelp();
                return;
            }

            if (opFilter.Count == 0)
            {
                opFilter.Add("Tj");
                opFilter.Add("TJ");
            }

            var defaults = ObjectsTextOpsDiff.LoadObjDefaults();
            if (string.IsNullOrWhiteSpace(docKey))
                docKey = defaults?.Doc ?? "tjpb_despacho";

            runFromStep = Math.Max(PipelineFirstStep, Math.Min(PipelineLastStep, runFromStep));
            runToStep = Math.Max(PipelineFirstStep, Math.Min(PipelineLastStep, runToStep));
            if (runFromStep > runToStep)
            {
                Console.WriteLine($"Faixa de execução inválida: {runFromStep}-{runToStep}");
                Environment.ExitCode = 2;
                LastExitCode = 2;
                return;
            }
            if (runFromStep > PipelineFirstStep && !ReturnUtils.IsEnabled())
            {
                Console.WriteLine($"Aviso: dependências internas serão executadas desde a etapa {PipelineFirstStep}; exibição filtrada para {runFromStep}-{runToStep}.");
            }
            if (runToStep < 2 && !ReturnUtils.IsEnabled())
            {
                Console.WriteLine("Aviso: para gerar relatório de alinhamento/extração, a execução mínima efetiva é até a etapa 2.");
            }
            if (runToStep < PipelineLastStep)
                stepOutputEcho = true;
            if (stepOutputSave && string.IsNullOrWhiteSpace(stepOutputDir))
                stepOutputDir = Path.Combine("outputs", "pipeline_steps");

            var despachoCandidateCache = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            List<int> GetDespachoCandidatePages(string pdfPath)
            {
                if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                    return new List<int>();
                if (despachoCandidateCache.TryGetValue(pdfPath, out var cached))
                    return cached;

                var pages = new List<int>();
                void AddPage(int candidatePage)
                {
                    if (candidatePage > 0 && !pages.Contains(candidatePage))
                        pages.Add(candidatePage);
                }

                try
                {
                    var hit = BookmarkDetector.Detect(pdfPath);
                    if (hit.Found)
                        AddPage(hit.Page);
                }
                catch
                {
                    // keep probing other detectors
                }
                try
                {
                    var hit = ContentsPrefixDetector.Detect(pdfPath);
                    if (hit.Found)
                        AddPage(hit.Page);
                }
                catch
                {
                    // keep probing other detectors
                }
                try
                {
                    var hit = HeaderLabelDetector.Detect(pdfPath);
                    if (hit.Found)
                        AddPage(hit.Page);
                }
                catch
                {
                    // keep probing other detectors
                }
                try
                {
                    var hit = LargestContentsDetector.Detect(pdfPath);
                    if (hit.Found)
                        AddPage(hit.Page);
                }
                catch
                {
                    // keep probing other detectors
                }

                despachoCandidateCache[pdfPath] = pages;
                return pages;
            }

            int PickPageOrDefault(string path)
            {
                var p = ResolvePage(path, trace: !ReturnUtils.IsEnabled());
                if (p > 0)
                    return p;
                try
                {
                    using var doc = new PdfDocument(new PdfReader(path));
                    if (doc.GetNumberOfPages() > 0)
                        return 1;
                }
                catch
                {
                    // ignore and return 0
                }
                return 0;
            }

            int PickObjForPage(string path, int page)
            {
                if (page <= 0)
                    return 0;
                var hit = ContentsStreamPicker.Pick(new StreamPickRequest { PdfPath = path, Page = page, RequireMarker = true });
                if (!hit.Found || hit.Obj <= 0)
                    hit = ContentsStreamPicker.PickSecondLargest(new StreamPickRequest { PdfPath = path, Page = page, RequireMarker = false });
                if (!hit.Found || hit.Obj <= 0)
                    hit = ContentsStreamPicker.Pick(new StreamPickRequest { PdfPath = path, Page = page, RequireMarker = false });
                return hit.Obj;
            }

            bool TryResolveDespachoSelection(string pdfPath, string roiDoc, bool back, out int page, out int obj, out string reason)
            {
                page = 0;
                obj = 0;
                reason = "";
                if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                    return false;
                if (string.IsNullOrWhiteSpace(roiDoc))
                    return false;
                var docKey = DocumentValidationRules.ResolveDocKeyForDetection(roiDoc);
                if (!string.Equals(docKey, "despacho", StringComparison.OrdinalIgnoreCase))
                    return false;

                var totalPages = 0;
                try
                {
                    using var doc = new PdfDocument(new PdfReader(pdfPath));
                    totalPages = doc.GetNumberOfPages();
                }
                catch
                {
                    totalPages = 0;
                }

                var candidates = new List<int>(GetDespachoCandidatePages(pdfPath));
                if (candidates.Count == 0)
                {
                    var fallbackPage = ResolvePage(pdfPath, trace: !ReturnUtils.IsEnabled());
                    if (fallbackPage > 0)
                        candidates.Add(fallbackPage);
                }
                if (candidates.Count == 0)
                    return false;

                var objByPage = new Dictionary<int, int>();
                int GetObj(int candidatePage)
                {
                    if (candidatePage <= 0)
                        return 0;
                    if (objByPage.TryGetValue(candidatePage, out var cachedObj))
                        return cachedObj;
                    var resolvedObj = PickObjForPage(pdfPath, candidatePage);
                    objByPage[candidatePage] = resolvedObj;
                    return resolvedObj;
                }

                var frontPage = 0;
                var frontObj = 0;
                var backPage = 0;
                var backObj = 0;
                foreach (var candidatePage in candidates)
                {
                    var candidateObj = GetObj(candidatePage);
                    if (candidateObj <= 0)
                        continue;

                    if (frontObj <= 0)
                    {
                        frontPage = candidatePage;
                        frontObj = candidateObj;
                    }

                    var nextPage = candidatePage + 1;
                    if (totalPages > 0 && nextPage <= totalPages)
                    {
                        var nextObj = GetObj(nextPage);
                        if (nextObj > 0)
                        {
                            frontPage = candidatePage;
                            frontObj = candidateObj;
                            backPage = nextPage;
                            backObj = nextObj;
                            break;
                        }
                    }
                }

                if (frontObj <= 0)
                    return false;

                if (back && backPage > 0 && backObj > 0)
                {
                    page = backPage;
                    obj = backObj;
                    reason = "despacho_back_auto";
                    return true;
                }

                page = frontPage;
                obj = frontObj;
                reason = "despacho_front_auto";
                return true;
            }

            void ResolveSelection(
                string pdfPath,
                bool pageUser,
                bool objUser,
                int pageHint,
                int objHint,
                out int page,
                out int obj,
                out string source)
            {
                page = 0;
                obj = 0;
                source = "";

                if (pageUser || objUser)
                {
                    page = pageHint > 0 ? pageHint : PickPageOrDefault(pdfPath);
                    obj = objHint > 0 ? objHint : PickObjForPage(pdfPath, page);
                    source = "user";
                    return;
                }

                if (TryResolveDespachoSelection(pdfPath, docKey, useBack, out page, out obj, out source))
                    return;

                page = pageHint > 0 ? pageHint : PickPageOrDefault(pdfPath);
                obj = objHint > 0 ? objHint : PickObjForPage(pdfPath, page);
                source = "auto";
            }

            bool TryCollapseModelCandidatesForSingleTarget(out List<string> collapsedInputs)
            {
                collapsedInputs = new List<string>(inputs);
                if (inputs.Count <= 2)
                    return false;

                var targetPath = inputs[^1].Trim().Trim('"');
                if (!File.Exists(targetPath))
                    return false;

                var modelCandidates = inputs
                    .Take(inputs.Count - 1)
                    .Select(v => v.Trim().Trim('"'))
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .ToList();

                if (modelCandidates.Count == 0)
                    return false;
                if (modelCandidates.Any(v => !File.Exists(v)))
                    return false;

                var roleKinds = modelCandidates
                    .Select(v => DetectInputRole(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (roleKinds.Count != 1 || !roleKinds.Contains("template_modelo", StringComparer.OrdinalIgnoreCase))
                    return false;

                ResolveSelection(targetPath, pageBUser, objBUser, pageB, objB, out var targetPage, out var targetObj, out var targetSource);
                if (targetPage <= 0 || targetObj <= 0)
                    return false;

                var bestModel = "";
                var bestScore = double.NegativeInfinity;
                var trialRows = new List<string>();

                foreach (var modelPath in modelCandidates)
                {
                    ResolveSelection(modelPath, pageAUser, objAUser, pageA, objA, out var modelPage, out var modelObj, out var modelSource);
                    if (modelPage <= 0 || modelObj <= 0)
                        continue;

                    ObjectsTextOpsDiff.AlignDebugReport? trialReport = null;
                    try
                    {
                        trialReport = ObjectsTextOpsDiff.ComputeAlignDebugForSelection(
                            modelPath,
                            targetPath,
                            new ObjectsTextOpsDiff.PageObjSelection { Page = modelPage, Obj = modelObj },
                            new ObjectsTextOpsDiff.PageObjSelection { Page = targetPage, Obj = targetObj },
                            opFilter,
                            backoff,
                            "single_page",
                            minSim,
                            band,
                            minLenRatio,
                            lenPenalty,
                            anchorMinSim,
                            anchorMinLenRatio,
                            gapPenalty);
                    }
                    catch
                    {
                        trialReport = null;
                    }

                    if (trialReport == null)
                        continue;

                    var anchorCount = trialReport.Anchors.Count;
                    var fixedCount = trialReport.FixedPairs.Count;
                    var variableCount = trialReport.Alignments.Count(p => string.Equals(p.Kind, "variable", StringComparison.OrdinalIgnoreCase));
                    var gapCount = trialReport.Alignments.Count(p => string.Equals(p.Kind, "gap_a", StringComparison.OrdinalIgnoreCase) || string.Equals(p.Kind, "gap_b", StringComparison.OrdinalIgnoreCase));
                    var trialScore = (anchorCount * 1000.0) + (variableCount * 20.0) + (fixedCount * 8.0) - (gapCount * 15.0);

                    trialRows.Add($"{Path.GetFileName(modelPath)} anchors={anchorCount} fixed={fixedCount} variable={variableCount} gaps={gapCount} score={trialScore:F1} model_sel={modelSource} target_sel={targetSource}");

                    if (trialScore > bestScore)
                    {
                        bestScore = trialScore;
                        bestModel = modelPath;
                    }
                }

                if (string.IsNullOrWhiteSpace(bestModel))
                    return false;

                if (!ReturnUtils.IsEnabled())
                {
                    Console.WriteLine("Seleção automática de modelo (alias por tipo):");
                    foreach (var row in trialRows)
                        Console.WriteLine("  " + row);
                    Console.WriteLine($"Modelo escolhido: {Path.GetFileName(bestModel)}");
                }

                collapsedInputs = new List<string> { bestModel, targetPath };
                return true;
            }

            if (inputs.Count > 2 && !allowStack)
            {
                if (!TryCollapseModelCandidatesForSingleTarget(out var collapsed))
                {
                    Console.WriteLine("Erro: múltiplos PDFs alvo na mesma execução foram bloqueados para isolamento.");
                    Console.WriteLine("Use exatamente 2 entradas (modelo + alvo), passe --allow-stack, ou use alias @M-DES/@M-CER/@M-REQ com um único alvo.");
                    return;
                }

                inputs = collapsed;
            }

            var aPath = inputs[0].Trim().Trim('"');
            if (!File.Exists(aPath))
            {
                Console.WriteLine($"PDF nao encontrado: {aPath}");
                return;
            }

            ResolveSelection(aPath, pageAUser, objAUser, pageA, objA, out pageA, out objA, out var sourceA);
            var roleA = DetectInputRole(aPath, preferTemplateForFirstInput: true);
            if (!ReturnUtils.IsEnabled() && sourceA.StartsWith("despacho", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Despacho route A (referência/modelo): p{pageA} o{objA} ({sourceA})");
            }

            var reports = new List<ObjectsTextOpsDiff.AlignDebugReport>();
            foreach (var rawB in inputs.Skip(1))
            {
                var bPath = rawB.Trim().Trim('"');
                if (!File.Exists(bPath))
                {
                    Console.WriteLine($"PDF nao encontrado: {bPath}");
                    return;
                }

                if (AreSameFilePath(aPath, bPath))
                {
                    Console.WriteLine($"Aviso: comparação A==B ignorada ({Path.GetFileName(bPath)}).");
                    continue;
                }

                int localPageA = pageA;
                int localObjA = objA;
                ResolveSelection(bPath, pageBUser, objBUser, pageB, objB, out var localPageB, out var localObjB, out var sourceB);
                var roleB = DetectInputRole(bPath);
                if (!ReturnUtils.IsEnabled() && sourceB.StartsWith("despacho", StringComparison.OrdinalIgnoreCase))
                {
                    var routeScope = ResolveExtractionScopeTag(roleA, roleB);
                    var sideLabelB = string.Equals(routeScope, "target_a_only(model_b_reference)", StringComparison.OrdinalIgnoreCase)
                        ? "Despacho route B (referência/modelo)"
                        : "Despacho route B (alvo)";
                    Console.WriteLine($"{sideLabelB}: p{localPageB} o{localObjB} ({sourceB})");
                }
                var stageOutputs = new List<Dictionary<string, object>>();
                void EmitStage(int step, string status, IDictionary<string, object>? payload = null, string? stageKey = null, string? stageLabel = null)
                {
                    var output = BuildStageOutput(step, status, payload, stageKey, stageLabel);
                    EmitStageOutput(stageOutputs, output, stepOutputEcho, runFromStep, runToStep);
                }

                var sideLabel = "single_page";
                var step1Payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["module"] = "Obj.DocDetector + ObjectsFindDespacho + ContentsStreamPicker",
                    ["role_a"] = roleA,
                    ["role_b"] = roleB,
                    ["pdf_a"] = Path.GetFileName(aPath),
                    ["pdf_b"] = Path.GetFileName(bPath),
                    ["sel_a"] = $"page={localPageA} obj={localObjA} source={sourceA}",
                    ["sel_b"] = $"page={localPageB} obj={localObjB} source={sourceB}",
                    ["band"] = sideLabel,
                    ["ops"] = string.Join(",", opFilter.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                    ["params"] = $"backoff={backoff} minSim={minSim.ToString("0.##", CultureInfo.InvariantCulture)} band={band} minLen={minLenRatio.ToString("0.##", CultureInfo.InvariantCulture)}"
                };
                EmitStage(1, "ok", step1Payload);
                if (!ReturnUtils.IsEnabled())
                {
                    var step1Lines = new List<(string Key, string Value)>
                    {
                        ("modulo", "Obj.DocDetector + ObjectsFindDespacho + ContentsStreamPicker"),
                        ("role_a", roleA),
                        ("role_b", roleB),
                        ("pdf_a", Path.GetFileName(aPath)),
                        ("pdf_b", Path.GetFileName(bPath)),
                        ("sel_a", $"page={localPageA} obj={localObjA} source={sourceA}"),
                        ("sel_b", $"page={localPageB} obj={localObjB} source={sourceB}")
                    };
                    step1Lines.Add(("band", sideLabel));
                    step1Lines.Add(("ops", string.Join(",", opFilter.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))));
                    step1Lines.Add(("params", $"backoff={backoff} minSim={minSim.ToString("0.##", CultureInfo.InvariantCulture)} band={band} minLen={minLenRatio.ToString("0.##", CultureInfo.InvariantCulture)}"));

                    PrintPipelineStep(
                        "passo 1/4 - detecção e seleção de objetos",
                        "passo 2/4 - alinhamento",
                        step1Lines.ToArray()
                    );
                }
                if (!ReturnUtils.IsEnabled())
                    PrintStage($"iniciando o modo de alinhamento ({sideLabel})");
                var report = ObjectsTextOpsDiff.ComputeAlignDebugForSelection(
                    aPath,
                    bPath,
                    new ObjectsTextOpsDiff.PageObjSelection { Page = localPageA, Obj = localObjA },
                    new ObjectsTextOpsDiff.PageObjSelection { Page = localPageB, Obj = localObjB },
                    opFilter,
                    backoff,
                    sideLabel,
                    minSim,
                    band,
                    minLenRatio,
                    lenPenalty,
                    anchorMinSim,
                    anchorMinLenRatio,
                    gapPenalty);

                if (report == null)
                {
                    var hasUserSelection = pageAUser || pageBUser || objAUser || objBUser;
                    if (hasUserSelection)
                    {
                        ResolveSelection(aPath, false, false, 0, 0, out var autoPageA, out var autoObjA, out var autoSourceA);
                        ResolveSelection(bPath, false, false, 0, 0, out var autoPageB, out var autoObjB, out var autoSourceB);

                        if (autoPageA > 0 && autoObjA > 0 && autoPageB > 0 && autoObjB > 0)
                        {
                            report = ObjectsTextOpsDiff.ComputeAlignDebugForSelection(
                                aPath,
                                bPath,
                                new ObjectsTextOpsDiff.PageObjSelection { Page = autoPageA, Obj = autoObjA },
                                new ObjectsTextOpsDiff.PageObjSelection { Page = autoPageB, Obj = autoObjB },
                                opFilter,
                                backoff,
                                sideLabel,
                                minSim,
                                band,
                                minLenRatio,
                                lenPenalty,
                                anchorMinSim,
                                anchorMinLenRatio,
                                gapPenalty);

                            if (report != null)
                            {
                                Console.WriteLine($"Aviso: page/obj informados inválidos. Auto-pick A p{autoPageA} o{autoObjA} ({autoSourceA}) | B p{autoPageB} o{autoObjB} ({autoSourceB})");
                            }
                        }
                    }

                    if (report == null)
                    {
                        Console.WriteLine("Falha ao gerar report.");
                        return;
                    }
                }
                report.RoleA = roleA;
                report.RoleB = roleB;

                ObjectsTextOpsDiff.AlignDebugReport? backReport = null;

                var variableCount = report.Alignments.Count(p => string.Equals(p.Kind, "variable", StringComparison.OrdinalIgnoreCase));
                var gapCount = report.Alignments.Count(p => p.Kind.StartsWith("gap", StringComparison.OrdinalIgnoreCase));
                var rangeA = report.RangeA?.HasValue == true ? $"op{report.RangeA.StartOp}-op{report.RangeA.EndOp}" : "(vazio)";
                var rangeB = report.RangeB?.HasValue == true ? $"op{report.RangeB.StartOp}-op{report.RangeB.EndOp}" : "(vazio)";
                var helper = report.HelperDiagnostics;
                var step2Payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["module"] = "Obj.Align.ObjectsTextOpsDiff",
                    ["anchors"] = report.Anchors.Count,
                    ["pairs"] = report.Alignments.Count,
                    ["fixed"] = report.FixedPairs.Count,
                    ["variable"] = variableCount,
                    ["gaps"] = gapCount,
                    ["range_a"] = rangeA,
                    ["range_b"] = rangeB
                };
                if (helper != null)
                {
                    step2Payload["helper_hits_a"] = helper.HitsA;
                    step2Payload["helper_hits_b"] = helper.HitsB;
                    step2Payload["helper_candidates"] = helper.Candidates;
                    step2Payload["helper_accepted"] = helper.Accepted;
                    step2Payload["helper_rejected"] = helper.Rejected;
                    step2Payload["helper_used"] = helper.UsedInFinalAnchors;
                }
                EmitStage(2, "ok", step2Payload);
                if (!ReturnUtils.IsEnabled())
                {
                    var stepItems = new List<(string Key, string Value)>
                    {
                        ("modulo", "Obj.Align.ObjectsTextOpsDiff"),
                        ("anchors", report.Anchors.Count.ToString(CultureInfo.InvariantCulture)),
                        ("pairs", report.Alignments.Count.ToString(CultureInfo.InvariantCulture)),
                        ("fixed", report.FixedPairs.Count.ToString(CultureInfo.InvariantCulture)),
                        ("variable", variableCount.ToString(CultureInfo.InvariantCulture)),
                        ("gaps", gapCount.ToString(CultureInfo.InvariantCulture)),
                        ("range_a", rangeA),
                        ("range_b", rangeB)
                    };
                    if (helper != null)
                    {
                        stepItems.Add(("helper_hits_a", helper.HitsA.ToString(CultureInfo.InvariantCulture)));
                        stepItems.Add(("helper_hits_b", helper.HitsB.ToString(CultureInfo.InvariantCulture)));
                        stepItems.Add(("helper_acc_rej", $"{helper.Accepted}/{helper.Rejected}"));
                        stepItems.Add(("helper_used", helper.UsedInFinalAnchors.ToString(CultureInfo.InvariantCulture)));
                    }
                    PrintPipelineStep("passo 2/4 - saída do alinhamento", "passo 3/4 - extração (usa op_range + value_full)", stepItems.ToArray());
                }

                if (inputs.Count == 2 && !ReturnUtils.IsEnabled())
                {
                    PrintHumanSummary(report, top, outputMode);
                    if (showAlign)
                        PrintAlignmentList(report, alignTop, showDiff: true);
                }

                Dictionary<string, object>? deferredProbePayload = null;
                if (runToStep >= 3)
                {
                    if (!ReturnUtils.IsEnabled())
                        PrintStage("iniciando o modo de extração");
                    report.Extraction = BuildExtractionPayload(
                        report,
                        backReport,
                        aPath,
                        bPath,
                        docKey,
                        verbose: !ReturnUtils.IsEnabled(),
                        maxPipelineStep: runToStep,
                        onStepOutput: (step, stageKey, status, payload) =>
                        {
                            EmitStage(step, status, payload, stageKey, ResolveStageLabel(step));
                        });
                }
                else
                {
                    report.Extraction = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["status"] = "not_executed",
                        ["reason"] = "run_to_step_below_extraction",
                        ["run_to_step"] = runToStep
                    };
                    EmitStage(3, "skipped", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["reason"] = "run_to_step_below_extraction",
                        ["run_to_step"] = runToStep
                    });
                }

                if (runToStep >= 7)
                {
                    if (probeEnabled)
                    {
                        var probeSideNormalized = (probeSide ?? "").Trim().ToLowerInvariant();
                        var hasExplicitProbeSide = !string.IsNullOrWhiteSpace(probeSideNormalized);
                        var probeUseA = hasExplicitProbeSide
                            ? (probeSideNormalized == "a" || probeSideNormalized == "pdf_a" || probeSideNormalized == "left")
                            : (IsTargetRole(roleA) && IsTemplateRole(roleB));
                        var sideKey = probeUseA ? "pdf_a" : "pdf_b";
                        var effectiveProbeFile = string.IsNullOrWhiteSpace(probeFile) ? (probeUseA ? aPath : bPath) : probeFile;
                        var effectiveProbePage = probePage > 0 ? probePage : (probeUseA ? report.PageA : report.PageB);
                        var probeValues = ReadValuesForProbe(report.Extraction, sideKey);

                        if (!ReturnUtils.IsEnabled())
                        {
                            PrintPipelineStep(
                                "passo 3.7/4 - probe pós-extração",
                                "passo 4/4 - saída e resumo",
                                ("modulo", "Obj.RootProbe.ExtractionProbeModule"),
                                ("probe_side", sideKey),
                                ("probe_file", effectiveProbeFile),
                                ("probe_page", effectiveProbePage.ToString(CultureInfo.InvariantCulture)),
                                ("probe_fields", probeValues.Count.ToString(CultureInfo.InvariantCulture))
                            );
                        }

                        var probePayload = ExtractionProbeModule.Run(
                            effectiveProbeFile,
                            effectiveProbePage,
                            probeValues,
                            sideKey,
                            probeMaxFields);

                        AttachProbeToExtraction(report.Extraction, probePayload);
                        deferredProbePayload = probePayload;
                        EmitStage(7, "ok", new Dictionary<string, object>(probePayload, StringComparer.OrdinalIgnoreCase));
                    }
                    else
                    {
                        EmitStage(7, "skipped", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["reason"] = "probe_disabled"
                        });
                    }
                }
                var reportBaseA = Path.GetFileNameWithoutExtension(aPath);
                var reportBaseB = Path.GetFileNameWithoutExtension(bPath);
                report.ReturnInfo = BuildReturnInfo(outputMode, $"{reportBaseA}__{reportBaseB}__output_pipe.json");
                report.ReturnView = BuildReturnView(report, outputMode);
                report.PipelineStages = stageOutputs;
                if (report.ReturnView != null)
                {
                    report.ReturnView["pipeline_stage_outputs"] = stageOutputs;
                    report.ReturnView["run_range"] = $"{runFromStep}-{runToStep}";
                }
                reports.Add(report);

                if (inputs.Count == 2)
                {
                    var jsonOptions = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    var json = JsonSerializer.Serialize(report, jsonOptions);
                    var baseA = Path.GetFileNameWithoutExtension(aPath);
                    var baseB = Path.GetFileNameWithoutExtension(bPath);
                    var emitJsonToStdout = ReturnUtils.IsEnabled();
                    if (emitJsonToStdout)
                    {
                        Console.WriteLine(json);
                        ReturnUtils.PersistJson(json, $"{baseA}__{baseB}__output_pipe.json");
                    }

                    if (!string.IsNullOrWhiteSpace(outPath) && outSpecified)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                        File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                        if (!ReturnUtils.IsEnabled())
                            Console.WriteLine("Arquivo salvo: " + outPath);
                    }
                    else if (string.IsNullOrWhiteSpace(outPath) && !ReturnUtils.IsEnabled())
                    {
                        if (outputMode == OutputMode.All)
                        {
                            outPath = Path.Combine("outputs", "align_ranges", $"{baseA}__{baseB}__textops_align.json");
                            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                            File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                            Console.WriteLine("Arquivo salvo: " + outPath);
                        }
                    }

                    if (report.Extraction != null && !ReturnUtils.IsEnabled())
                    {
                        var extractionOutPath = Path.Combine("outputs", "extract", $"{baseA}__{baseB}__textops_extract.json");
                        Directory.CreateDirectory(Path.GetDirectoryName(extractionOutPath) ?? ".");
                        var extractionJson = JsonSerializer.Serialize(report.Extraction, jsonOptions);
                        File.WriteAllText(extractionOutPath, extractionJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                        Console.WriteLine("Arquivo extração salvo: " + extractionOutPath);
                    }

                    EmitStage(8, "ok", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["module"] = "ObjectsTextOpsAlign + JsonSerializer",
                        ["output_mode"] = outputMode.ToString(),
                        ["run_range"] = $"{runFromStep}-{runToStep}",
                        ["align_out"] = string.IsNullOrWhiteSpace(outPath) ? "(stdout/default)" : outPath,
                        ["return_enabled"] = ReturnUtils.IsEnabled(),
                        ["partial_run"] = runToStep < PipelineLastStep
                    });

                    if (stepOutputSave)
                        SaveStageOutputs(stepOutputDir, baseA, baseB, stageOutputs);

                    if (!ReturnUtils.IsEnabled())
                        PrintPipelineStep("passo 4/4 - saída e resumo", "fim", ("modulo", "ObjectsTextOpsAlign + JsonSerializer"), ("align_json", string.IsNullOrWhiteSpace(outPath) ? "(stdout/default)" : outPath), ("extraction", "resumo final da extração + JSON em outputs/extract"));
                    if (!ReturnUtils.IsEnabled())
                        PrintExtractionSummary(report.Extraction);
                    if (!ReturnUtils.IsEnabled() && deferredProbePayload != null)
                        PrintProbeSummary(deferredProbePayload);
                }
                else
                {
                    EmitStage(8, "ok", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["module"] = "ObjectsTextOpsAlign + JsonSerializer",
                        ["output_mode"] = outputMode.ToString(),
                        ["run_range"] = $"{runFromStep}-{runToStep}",
                        ["stack_mode"] = true,
                        ["partial_run"] = runToStep < PipelineLastStep
                    });
                    if (stepOutputSave)
                        SaveStageOutputs(stepOutputDir, reportBaseA, reportBaseB, stageOutputs);
                }
            }

            if (inputs.Count > 2)
            {
                if (ReturnUtils.IsEnabled())
                {
                    var jsonOptions = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    var json = JsonSerializer.Serialize(reports, jsonOptions);
                    Console.WriteLine(json);
                    var baseA = Path.GetFileNameWithoutExtension(aPath);
                    ReturnUtils.PersistJson(json, $"{baseA}__STACK__output_pipe.json");
                    if (!string.IsNullOrWhiteSpace(outPath) && outSpecified)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                        File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    }
                }
                else
                {
                    WriteStackedOutput(aPath, reports, outPath);
                }
            }
        }

        private static List<string> DeduplicateInputs(List<string> inputs)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in inputs ?? Enumerable.Empty<string>())
            {
                var t = (raw ?? "").Trim().Trim('"');
                if (t.Length == 0)
                    continue;
                var key = CanonicalizePathKey(t);
                if (seen.Add(key))
                    result.Add(t);
            }
            return result;
        }

        private static string CanonicalizePathKey(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";
            var normalized = PathUtils.NormalizePathForCurrentOS(path.Trim().Trim('"'));
            try
            {
                return Path.GetFullPath(normalized);
            }
            catch
            {
                return normalized;
            }
        }

        private static bool AreSameFilePath(string a, string b)
        {
            var ka = CanonicalizePathKey(a);
            var kb = CanonicalizePathKey(b);
            if (ka.Length == 0 || kb.Length == 0)
                return false;
            return string.Equals(ka, kb, StringComparison.OrdinalIgnoreCase);
        }

        private static int ResolvePage(string pdfPath, bool trace)
        {
            void Trace(string detector, DetectionHit hit)
            {
                if (!trace)
                    return;
                if (hit.Found)
                    Console.WriteLine(Colorize($"[DETECÇÃO] {detector}: hit p{hit.Page}", AnsiOk));
                else
                    Console.WriteLine(Colorize($"[DETECÇÃO] {detector}: miss", AnsiWarn));
            }

            var hit = BookmarkDetector.Detect(pdfPath);
            Trace("bookmark", hit);
            if (hit.Found)
                return hit.Page;
            hit = ContentsPrefixDetector.Detect(pdfPath);
            Trace("contents_prefix", hit);
            if (hit.Found)
                return hit.Page;
            hit = HeaderLabelDetector.Detect(pdfPath);
            Trace("header_label", hit);
            if (hit.Found)
                return hit.Page;
            hit = LargestContentsDetector.Detect(pdfPath);
            Trace("largest_contents", hit);
            if (hit.Found)
                return hit.Page;
            return 0;
        }

        private static string NormalizeSimilarityText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var t = TextNormalization.NormalizePatternText(text);
            if (string.IsNullOrWhiteSpace(t))
                return "";
            var sb = new StringBuilder(t.Length);
            foreach (var c in t)
            {
                if (char.IsDigit(c))
                    sb.Append('#');
                else if (!char.IsWhiteSpace(c))
                    sb.Append(char.ToLowerInvariant(c));
                else if (sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        private static double ComputeSimilarityScore(string a, string b)
        {
            if (a.Length == 0 && b.Length == 0)
                return 1.0;
            if (a.Length == 0 || b.Length == 0)
                return 0.0;
            var dmp = new diff_match_patch();
            var diffs = dmp.diff_main(a, b, false);
            var dist = dmp.diff_levenshtein(diffs);
            var maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0)
                return 0.0;
            var textSim = 1.0 - (double)dist / maxLen;
            var lenSim = 1.0 - (double)Math.Abs(a.Length - b.Length) / maxLen;
            return (textSim * 0.7) + (lenSim * 0.3);
        }

        private static string ResolveAlignRangeMapPath(string docKey)
        {
            var resolvedDoc = DocumentValidationRules.ResolveDocKeyForDetection(docKey);
            var mapName = "tjpb_despacho.yml";
            if (DocumentValidationRules.IsDocMatch(resolvedDoc, "certidao_conselho"))
                mapName = "tjpb_certidao.yml";
            else if (DocumentValidationRules.IsDocMatch(resolvedDoc, "requerimento_honorarios"))
                mapName = "tjpb_requerimento.yml";

            var reg = PatternRegistry.FindDir("alignrange_fields");
            if (!string.IsNullOrWhiteSpace(reg))
            {
                var p = Path.Combine(reg, mapName);
                if (File.Exists(p))
                    return p;
            }

            var local = Path.Combine("modules", "PatternModules", "registry", "alignrange_fields", mapName);
            if (File.Exists(local))
                return Path.GetFullPath(local);

            return "";
        }

        private static string BuildValueFullFromBlocks(List<ObjectsTextOpsDiff.AlignDebugBlock> blocks, ObjectsTextOpsDiff.AlignDebugRange range)
        {
            if (blocks == null || blocks.Count == 0)
                return "";

            IEnumerable<ObjectsTextOpsDiff.AlignDebugBlock> selected = blocks;
            if (range != null && range.HasValue && range.StartOp > 0 && range.EndOp >= range.StartOp)
            {
                selected = blocks.Where(b => b.EndOp >= range.StartOp && b.StartOp <= range.EndOp);
            }

            return string.Join("\n", selected
                .Select(b => b?.Text ?? "")
                .Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        private static string BuildOpRange(ObjectsTextOpsDiff.AlignDebugRange range)
        {
            if (range == null || !range.HasValue || range.StartOp <= 0 || range.EndOp <= 0)
                return "";
            return range.StartOp == range.EndOp
                ? $"op{range.StartOp}"
                : $"op{range.StartOp}-op{range.EndOp}";
        }

        private static Dictionary<string, object> BuildHonorariosSnapshot(HonorariosBackfillResult? backfill)
        {
            var payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["doc_type"] = backfill?.DocType ?? "",
                ["derived_valor"] = backfill?.DerivedValor ?? "",
                ["derived_values"] = backfill?.DerivedValues ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            var side = backfill?.Summary?.PdfA;
            if (side != null)
            {
                payload["status"] = side.Status ?? "";
                payload["source"] = side.Source ?? "";
                payload["especialidade"] = side.Especialidade ?? "";
                payload["especialidade_source"] = side.EspecialidadeSource ?? "";
                payload["especie_da_pericia"] = side.EspecieDaPericia ?? "";
                payload["valor_field"] = side.ValorField ?? "";
                payload["valor_raw"] = side.ValorRaw ?? "";
                payload["fator"] = side.Fator ?? "";
                payload["entry_id"] = side.EntryId ?? "";
                payload["area"] = side.Area ?? "";
                payload["perito_name_source"] = side.PeritoNameSource ?? "";
                payload["perito_cpf_source"] = side.PeritoCpfSource ?? "";
                payload["derivations"] = side.Derivations ?? new List<string>();
                payload["valor_tabelado_anexo_i"] = side.ValorTabeladoAnexoI ?? "";
                payload["valor_normalized"] = side.ValorNormalized ?? "";
                payload["confidence"] = side.Confidence;
            }

            return payload;
        }

        private static string ResolveReturnCommandName(OutputMode outputMode)
        {
            return outputMode switch
            {
                OutputMode.VariablesOnly => "textopsvar",
                OutputMode.FixedOnly => "textopsfixed",
                _ => "textopsalign"
            };
        }

        private static Dictionary<string, object> BuildReturnInfo(OutputMode outputMode, string defaultFileName)
        {
            var enabled = ReturnUtils.IsEnabled();
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["schema_version"] = "operpdf.return.v1",
                ["command"] = ResolveReturnCommandName(outputMode),
                ["output_mode"] = outputMode.ToString(),
                ["return_enabled"] = enabled,
                ["io_file"] = enabled ? ReturnUtils.ResolveOutputPath(defaultFileName) : "",
                ["generated_at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
        }

        private static string CompactText(string? text, int max = 180)
        {
            var normalized = TextNormalization.NormalizeWhitespace(text ?? "");
            if (normalized.Length <= max)
                return normalized;
            return normalized.Substring(0, max - 3) + "...";
        }

        private static string BuildBlockOpRange(ObjectsTextOpsDiff.AlignDebugBlock? block)
        {
            if (block == null || block.StartOp <= 0 || block.EndOp <= 0)
                return "";
            return block.StartOp == block.EndOp
                ? $"op{block.StartOp}"
                : $"op{block.StartOp}-op{block.EndOp}";
        }

        private static bool IsSelectedByOutputMode(string? kind, OutputMode outputMode)
        {
            var normalized = (kind ?? "").Trim().ToLowerInvariant();
            return outputMode switch
            {
                OutputMode.VariablesOnly => normalized == "variable" || normalized == "gap_a" || normalized == "gap_b",
                OutputMode.FixedOnly => normalized == "fixed",
                _ => normalized == "fixed" || normalized == "variable" || normalized == "gap_a" || normalized == "gap_b"
            };
        }

        private static Dictionary<string, object> BuildReturnPairPreview(int indexZeroBased, ObjectsTextOpsDiff.AlignDebugPair pair)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["n"] = indexZeroBased + 1,
                ["kind"] = pair.Kind ?? "",
                ["score"] = pair.Score,
                ["a_index"] = pair.AIndex >= 0 ? pair.AIndex + 1 : 0,
                ["b_index"] = pair.BIndex >= 0 ? pair.BIndex + 1 : 0,
                ["a_op_range"] = BuildBlockOpRange(pair.A),
                ["b_op_range"] = BuildBlockOpRange(pair.B),
                ["a_text"] = CompactText(pair.A?.Text),
                ["b_text"] = CompactText(pair.B?.Text)
            };
        }

        private static int CountNonEmptyParsedValues(JsonElement root, string sideName)
        {
            if (!root.TryGetProperty("parsed", out var parsed) || parsed.ValueKind != JsonValueKind.Object)
                return 0;
            if (!parsed.TryGetProperty(sideName, out var side) || side.ValueKind != JsonValueKind.Object)
                return 0;

            JsonElement values;
            var hasValues = side.TryGetProperty("values", out values) || side.TryGetProperty("Values", out values);
            if (!hasValues || values.ValueKind != JsonValueKind.Object)
                return 0;

            var count = 0;
            foreach (var prop in values.EnumerateObject())
            {
                var value = prop.Value.ValueKind == JsonValueKind.String
                    ? prop.Value.GetString() ?? ""
                    : prop.Value.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    count++;
            }
            return count;
        }

        private static Dictionary<string, object> BuildExtractionReturnSummary(object? extraction)
        {
            var summary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["status"] = extraction == null ? "not_available" : "unknown",
                ["doc_key"] = "",
                ["doc_type"] = "",
                ["band"] = "",
                ["scope"] = "",
                ["target_side"] = "",
                ["fields_non_empty_a"] = 0,
                ["fields_non_empty_b"] = 0,
                ["fields_non_empty_target"] = 0,
                ["validator_ok"] = false,
                ["validator_reason"] = "",
                ["honorarios_apply_a"] = false,
                ["honorarios_apply_b"] = false
            };

            if (extraction == null)
                return summary;

            try
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(extraction));
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var statusEl))
                    summary["status"] = statusEl.GetString() ?? "";
                if (root.TryGetProperty("doc_key", out var docKeyEl))
                    summary["doc_key"] = docKeyEl.GetString() ?? "";
                if (root.TryGetProperty("doc_type", out var docTypeEl))
                    summary["doc_type"] = docTypeEl.GetString() ?? "";
                if (root.TryGetProperty("band", out var bandEl))
                    summary["band"] = bandEl.GetString() ?? "";

                if (root.TryGetProperty("pipeline", out var pipeline) && pipeline.ValueKind == JsonValueKind.Object)
                {
                    if (pipeline.TryGetProperty("scope", out var scopeEl))
                        summary["scope"] = scopeEl.GetString() ?? "";
                    if (pipeline.TryGetProperty("target_side", out var targetSideEl))
                        summary["target_side"] = targetSideEl.GetString() ?? "";
                }

                summary["fields_non_empty_a"] = CountNonEmptyParsedValues(root, "pdf_a");
                summary["fields_non_empty_b"] = CountNonEmptyParsedValues(root, "pdf_b");
                var targetSide = summary["target_side"]?.ToString() ?? "";
                summary["fields_non_empty_target"] = string.Equals(targetSide, "pdf_a", StringComparison.OrdinalIgnoreCase)
                    ? CountNonEmptyParsedValues(root, "pdf_a")
                    : CountNonEmptyParsedValues(root, "pdf_b");

                if (root.TryGetProperty("validator", out var validator) && validator.ValueKind == JsonValueKind.Object)
                {
                    if (validator.TryGetProperty("ok", out var okEl) && (okEl.ValueKind == JsonValueKind.True || okEl.ValueKind == JsonValueKind.False))
                        summary["validator_ok"] = okEl.GetBoolean();
                    if (validator.TryGetProperty("reason", out var reasonEl))
                        summary["validator_reason"] = reasonEl.GetString() ?? "";
                }

                if (root.TryGetProperty("honorarios", out var honorarios) && honorarios.ValueKind == JsonValueKind.Object)
                {
                    if (honorarios.TryGetProperty("apply_a", out var applyAEl) && (applyAEl.ValueKind == JsonValueKind.True || applyAEl.ValueKind == JsonValueKind.False))
                        summary["honorarios_apply_a"] = applyAEl.GetBoolean();
                    if (honorarios.TryGetProperty("apply_b", out var applyBEl) && (applyBEl.ValueKind == JsonValueKind.True || applyBEl.ValueKind == JsonValueKind.False))
                        summary["honorarios_apply_b"] = applyBEl.GetBoolean();
                }
            }
            catch
            {
                summary["status"] = "invalid";
            }

            return summary;
        }

        private static Dictionary<string, object> BuildReturnView(ObjectsTextOpsDiff.AlignDebugReport report, OutputMode outputMode)
        {
            var allPairs = report.Alignments ?? new List<ObjectsTextOpsDiff.AlignDebugPair>();
            var indexed = allPairs.Select((pair, idx) => (pair, idx)).ToList();
            var selected = indexed.Where(x => IsSelectedByOutputMode(x.pair.Kind, outputMode)).ToList();

            var countsAll = allPairs
                .GroupBy(p => string.IsNullOrWhiteSpace(p.Kind) ? "(unknown)" : p.Kind.Trim().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var countsSelected = selected
                .GroupBy(p => string.IsNullOrWhiteSpace(p.pair.Kind) ? "(unknown)" : p.pair.Kind.Trim().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var selectedIndexes = selected.Select(x => x.idx + 1).ToList();
            var preview = selected
                .Take(20)
                .Select(x => BuildReturnPairPreview(x.idx, x.pair))
                .ToList();

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["mode"] = outputMode.ToString(),
                ["selected_rule"] = outputMode switch
                {
                    OutputMode.VariablesOnly => "variable + gap_a + gap_b",
                    OutputMode.FixedOnly => "fixed",
                    _ => "fixed + variable + gap_a + gap_b"
                },
                ["counts"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["all"] = allPairs.Count,
                    ["selected"] = selected.Count,
                    ["all_by_kind"] = countsAll,
                    ["selected_by_kind"] = countsSelected
                },
                ["selected_alignment_indexes"] = selectedIndexes,
                ["selected_preview_top20"] = preview,
                ["extraction_summary"] = BuildExtractionReturnSummary(report.Extraction)
            };
        }

        private static object BuildExtractionPayload(
            ObjectsTextOpsDiff.AlignDebugReport report,
            string aPath,
            string bPath,
            string docKey,
            bool verbose = false,
            int maxPipelineStep = PipelineLastStep,
            Action<int, string, string, Dictionary<string, object>>? onStepOutput = null)
        {
            return BuildExtractionPayload(report, null, aPath, bPath, docKey, verbose, maxPipelineStep, onStepOutput);
        }

        private static bool TryParseBandReport(
            ObjectsTextOpsDiff.AlignDebugReport report,
            string mapPath,
            string band,
            string aPath,
            string bPath,
            out ObjectsMapFields.CompactExtractionOutput? parsed,
            out string error,
            out string opRangeA,
            out string opRangeB,
            out string valueFullA,
            out string valueFullB)
        {
            valueFullA = BuildValueFullFromBlocks(report.BlocksA, report.RangeA);
            valueFullB = BuildValueFullFromBlocks(report.BlocksB, report.RangeB);
            opRangeA = BuildOpRange(report.RangeA);
            opRangeB = BuildOpRange(report.RangeB);

            return ObjectsMapFields.TryExtractFromInlineSegments(
                mapPath,
                band,
                valueFullA,
                valueFullB,
                opRangeA,
                opRangeB,
                report.ObjA,
                report.ObjB,
                aPath,
                bPath,
                out parsed,
                out error);
        }

        private static void MergeSideFrom(
            Dictionary<string, string> baseValues,
            Dictionary<string, ObjectsMapFields.CompactFieldOutput> baseFields,
            ObjectsMapFields.CompactSideOutput fromSide)
        {
            foreach (var kv in fromSide.Values)
            {
                var incoming = kv.Value ?? "";
                if (string.IsNullOrWhiteSpace(incoming))
                    continue;
                if (!baseValues.TryGetValue(kv.Key, out var current) || string.IsNullOrWhiteSpace(current))
                {
                    baseValues[kv.Key] = incoming;
                    if (fromSide.Fields.TryGetValue(kv.Key, out var meta))
                        baseFields[kv.Key] = meta;
                }
            }

            foreach (var kv in fromSide.Fields)
            {
                if (!baseFields.ContainsKey(kv.Key))
                    baseFields[kv.Key] = kv.Value;
            }
        }

        private static object BuildExtractionPayload(
            ObjectsTextOpsDiff.AlignDebugReport report,
            ObjectsTextOpsDiff.AlignDebugReport? backReport,
            string aPath,
            string bPath,
            string docKey,
            bool verbose = false,
            int maxPipelineStep = PipelineLastStep,
            Action<int, string, string, Dictionary<string, object>>? onStepOutput = null)
        {
            var stepLimit = Math.Max(3, Math.Min(6, maxPipelineStep));
            var resolvedDoc = DocumentValidationRules.ResolveDocKeyForDetection(docKey);
            var outputDocType = DocumentValidationRules.MapDocKeyToOutputType(resolvedDoc);
            var bandFront = "single_page";
            var mapPath = ResolveAlignRangeMapPath(resolvedDoc);
            var extractionScope = ResolveExtractionScopeTag(report.RoleA, report.RoleB);
            var targetSide = ResolveTargetSideFromScope(extractionScope);
            var targetIsA = string.Equals(targetSide, "pdf_a", StringComparison.OrdinalIgnoreCase);

            if (verbose)
            {
                PrintPipelineStep(
                    "passo 3.1/4 - preparação da extração",
                    "passo 3.2/4 - recorte value_full/op_range",
                    ("modulo", "ObjectsTextOpsAlign + DocumentValidationRules"),
                    ("doc_key", resolvedDoc),
                    ("doc_type", outputDocType),
                    ("scope", extractionScope),
                    ("band", bandFront),
                    ("map_path", string.IsNullOrWhiteSpace(mapPath) ? "(não encontrado)" : mapPath)
                );
            }
            if (string.IsNullOrWhiteSpace(mapPath))
            {
                onStepOutput?.Invoke(3, "extraction", "error", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["status"] = "map_not_found",
                    ["doc_key"] = resolvedDoc,
                    ["doc_type"] = outputDocType,
                    ["band"] = bandFront,
                    ["map_path"] = ""
                });
                return new Dictionary<string, object>
                {
                    ["status"] = "map_not_found",
                    ["doc_key"] = resolvedDoc,
                    ["doc_type"] = outputDocType,
                    ["band"] = bandFront,
                    ["map_path"] = ""
                };
            }

            if (!TryParseBandReport(report, mapPath, bandFront, aPath, bPath, out var parsedFront, out var frontError, out var opRangeAFront, out var opRangeBFront, out var valueFullAFront, out var valueFullBFront) || parsedFront == null)
            {
                if (verbose)
                {
                    PrintPipelineStep(
                        "passo 3.3/4 - parser do mapa YAML (falhou)",
                        "encerrado com erro de extração",
                        ("modulo", "Obj.Commands.ObjectsMapFields"),
                        ("erro", string.IsNullOrWhiteSpace(frontError) ? "(sem detalhe)" : frontError)
                    );
                }
                onStepOutput?.Invoke(3, "extraction", "error", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["status"] = "extract_failed",
                    ["doc_key"] = resolvedDoc,
                    ["doc_type"] = outputDocType,
                    ["band"] = bandFront,
                    ["map_path"] = mapPath,
                    ["error"] = frontError ?? ""
                });
                return new Dictionary<string, object>
                {
                    ["status"] = "extract_failed",
                    ["doc_key"] = resolvedDoc,
                    ["doc_type"] = outputDocType,
                    ["band"] = bandFront,
                    ["map_path"] = mapPath,
                    ["error"] = frontError ?? ""
                };
            }

            if (verbose)
            {
                PrintPipelineStep(
                    "passo 3.2/4 - resultado do recorte para parser (single_page)",
                    "passo 3.3/4 - parser do mapa YAML",
                    ("modulo", "ObjectsTextOpsAlign.BuildValueFullFromBlocks"),
                    ("op_range_a", string.IsNullOrWhiteSpace(opRangeAFront) ? "(vazio)" : opRangeAFront),
                    ("op_range_b", string.IsNullOrWhiteSpace(opRangeBFront) ? "(vazio)" : opRangeBFront),
                    ("len_value_full_a", valueFullAFront.Length.ToString(CultureInfo.InvariantCulture)),
                    ("len_value_full_b", valueFullBFront.Length.ToString(CultureInfo.InvariantCulture)),
                    ("sample_a", ShortText(valueFullAFront)),
                    ("sample_b", ShortText(valueFullBFront))
                );
            }

            var valuesA = new Dictionary<string, string>(parsedFront.PdfA.Values, StringComparer.OrdinalIgnoreCase);
            var valuesB = new Dictionary<string, string>(parsedFront.PdfB.Values, StringComparer.OrdinalIgnoreCase);
            var fieldsA = new Dictionary<string, ObjectsMapFields.CompactFieldOutput>(parsedFront.PdfA.Fields, StringComparer.OrdinalIgnoreCase);
            var fieldsB = new Dictionary<string, ObjectsMapFields.CompactFieldOutput>(parsedFront.PdfB.Fields, StringComparer.OrdinalIgnoreCase);

            var parserFieldsA = CloneFieldOutputs(fieldsA);
            var parserFieldsB = CloneFieldOutputs(fieldsB);
            var parserValuesA = new Dictionary<string, string>(valuesA, StringComparer.OrdinalIgnoreCase);
            var parserValuesB = new Dictionary<string, string>(valuesB, StringComparer.OrdinalIgnoreCase);

            if (verbose)
            {
                if (string.Equals(extractionScope, "pair_both", StringComparison.OrdinalIgnoreCase))
                {
                    PrintPipelineStep(
                        "passo 3.3/4 - parser do mapa YAML (ok)",
                        "passo 3.4/4 - enriquecimento de honorários",
                        ("modulo", "Obj.Commands.ObjectsMapFields (alignrange_fields/*.yml)"),
                        ("fields_a_non_empty", CountNonEmptyValues(valuesA).ToString(CultureInfo.InvariantCulture)),
                        ("fields_b_non_empty", CountNonEmptyValues(valuesB).ToString(CultureInfo.InvariantCulture)),
                        ("origem_values_a", "parsed.pdf_a.values <= parsed.pdf_a.fields (source/op_range/obj)"),
                        ("origem_values_b", "parsed.pdf_b.values <= parsed.pdf_b.fields (source/op_range/obj)")
                    );
                }
                else
                {
                    var targetCount = targetIsA ? CountNonEmptyValues(valuesA) : CountNonEmptyValues(valuesB);
                    PrintPipelineStep(
                        "passo 3.3/4 - parser do mapa YAML (ok)",
                        "passo 3.4/4 - enriquecimento de honorários",
                        ("modulo", "Obj.Commands.ObjectsMapFields (alignrange_fields/*.yml)"),
                        ("scope", extractionScope),
                        ("target_side", targetSide),
                        ("fields_target_non_empty", targetCount.ToString(CultureInfo.InvariantCulture)),
                        ("origem_values_target", $"{targetSide}: parsed.{targetSide}.values <= parsed.{targetSide}.fields (source/op_range/obj)")
                    );
                }
            }
            var step3Payload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["module"] = "Obj.Commands.ObjectsMapFields (alignrange_fields/*.yml)",
                ["doc_key"] = resolvedDoc,
                ["doc_type"] = outputDocType,
                ["scope"] = extractionScope,
                ["target_side"] = targetSide,
                ["band"] = bandFront,
                ["map_path"] = parsedFront.MapPath,
                ["fields_a_non_empty"] = CountNonEmptyValues(valuesA),
                ["fields_b_non_empty"] = CountNonEmptyValues(valuesB),
                ["fields_target_non_empty"] = targetIsA ? CountNonEmptyValues(valuesA) : CountNonEmptyValues(valuesB),
                ["merge_policy"] = "single_band"
            };
            onStepOutput?.Invoke(3, "extraction", "ok", step3Payload);

            var beforeHonorariosA = new Dictionary<string, string>(valuesA, StringComparer.OrdinalIgnoreCase);
            var beforeHonorariosB = new Dictionary<string, string>(valuesB, StringComparer.OrdinalIgnoreCase);
            var trackedFields = new[]
            {
                "PROCESSO_ADMINISTRATIVO",
                "PROCESSO_JUDICIAL",
                "COMARCA",
                "VARA",
                "PROMOVENTE",
                "PROMOVIDO",
                "PERITO",
                "CPF_PERITO",
                "ESPECIALIDADE",
                "ESPECIE_DA_PERICIA",
                "VALOR_ARBITRADO_JZ",
                "VALOR_ARBITRADO_FINAL",
                "FATOR",
                "VALOR_TABELADO_ANEXO_I"
            };

            Dictionary<string, object> BuildExtractionResult(
                int executedUntilStep,
                Dictionary<string, object>? honorariosPayload,
                Dictionary<string, object>? repairerPayload,
                Dictionary<string, object>? validatorPayload,
                IDictionary<string, string>? derivedValuesA,
                IDictionary<string, string>? derivedValuesB)
            {
                var flowA = BuildTrackedValueFlow(trackedFields, parserValuesA, valuesA, derivedValuesA, fieldsA);
                var flowB = BuildTrackedValueFlow(trackedFields, parserValuesB, valuesB, derivedValuesB, fieldsB);
                var runInfo = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["requested_max_step"] = maxPipelineStep,
                    ["executed_until_step"] = executedUntilStep,
                    ["stopped_before_step"] = executedUntilStep < 6 ? executedUntilStep + 1 : 0,
                    ["scope"] = extractionScope,
                    ["target_side"] = targetSide
                };

                var parsedOutput = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                var valueFlowOutput = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (string.Equals(extractionScope, "target_b_only(model_a_reference)", StringComparison.OrdinalIgnoreCase))
                {
                    parsedOutput["pdf_b"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["values"] = valuesB,
                        ["fields"] = fieldsB
                    };
                    parsedOutput["pdf_a_reference"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["role"] = "template_modelo",
                        ["scope"] = "alignment_reference_only"
                    };
                    valueFlowOutput["pdf_b"] = flowB;
                }
                else if (string.Equals(extractionScope, "target_a_only(model_b_reference)", StringComparison.OrdinalIgnoreCase))
                {
                    parsedOutput["pdf_a"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["values"] = valuesA,
                        ["fields"] = fieldsA
                    };
                    parsedOutput["pdf_b_reference"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["role"] = "template_modelo",
                        ["scope"] = "alignment_reference_only"
                    };
                    valueFlowOutput["pdf_a"] = flowA;
                }
                else
                {
                    parsedOutput["pdf_a"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["values"] = valuesA,
                        ["fields"] = fieldsA
                    };
                    parsedOutput["pdf_b"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["values"] = valuesB,
                        ["fields"] = fieldsB
                    };
                    valueFlowOutput["pdf_a"] = flowA;
                    valueFlowOutput["pdf_b"] = flowB;
                }

                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["status"] = "ok",
                    ["doc_key"] = resolvedDoc,
                    ["doc_type"] = outputDocType,
                    ["map_path"] = parsedFront.MapPath,
                    ["band"] = bandFront,
                    ["parsed"] = parsedOutput,
                    ["pipeline"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["step_modules"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["1_detection_selection"] = "Obj.DocDetector + ObjectsFindDespacho + ContentsStreamPicker",
                            ["2_alignment"] = "Obj.Align.ObjectsTextOpsDiff",
                            ["3_1_prepare"] = "ObjectsTextOpsAlign + DocumentValidationRules",
                            ["3_2_crop"] = "ObjectsTextOpsAlign.BuildValueFullFromBlocks",
                            ["3_3_yaml_parser"] = "Obj.Commands.ObjectsMapFields",
                            ["3_4_honorarios"] = "Obj.Honorarios.HonorariosFacade",
                            ["3_5_repairer"] = "Obj.ValidationCore.ValidationRepairer",
                            ["3_6_validator"] = "Obj.ValidatorModule.ValidatorFacade",
                            ["3_7_probe_optional"] = "Obj.RootProbe.ExtractionProbeModule",
                            ["4_output"] = "ObjectsTextOpsAlign + JsonSerializer"
                        },
                        ["scope"] = extractionScope,
                        ["target_side"] = targetSide,
                        ["merge_policy"] = "single_band",
                        ["values_origin_note"] = string.Equals(extractionScope, "pair_both", StringComparison.OrdinalIgnoreCase)
                            ? "Os valores finais de parsed.pdf_a.values e parsed.pdf_b.values vêm do parser YAML (parsed.*.fields) e podem receber complemento dos módulos honorarios/repairer/validator, com trilha em parsed.*.fields.Source e parsed.*.fields.Module."
                            : $"Somente parsed.{targetSide}.values é considerado saída de extração; o lado de modelo/template fica como referência de alinhamento.",
                        ["run"] = runInfo
                    },
                    ["value_flow"] = valueFlowOutput,
                    ["validator"] = validatorPayload ?? BuildSkippedModulePayload("Obj.ValidatorModule.ValidatorFacade", $"run_limit_step_{executedUntilStep}"),
                    ["repairer"] = repairerPayload ?? BuildSkippedModulePayload("Obj.ValidationCore.ValidationRepairer", $"run_limit_step_{executedUntilStep}"),
                    ["honorarios"] = honorariosPayload ?? BuildSkippedModulePayload("Obj.Honorarios.HonorariosFacade", $"run_limit_step_{executedUntilStep}")
                };
            }

            if (stepLimit <= 3)
                return BuildExtractionResult(3, null, null, null, null, null);

            HonorariosFacade.ApplyProfissaoAsEspecialidade(valuesA);
            HonorariosFacade.ApplyProfissaoAsEspecialidade(valuesB);
            var honorariosA = HonorariosFacade.ApplyBackfill(valuesA, outputDocType);
            var honorariosB = HonorariosFacade.ApplyBackfill(valuesB, outputDocType);
            MarkModuleChanges(beforeHonorariosA, valuesA, fieldsA, "honorarios");
            MarkModuleChanges(beforeHonorariosB, valuesB, fieldsB, "honorarios");

            if (verbose)
            {
                PrintPipelineStep(
                    "passo 3.4/4 - honorários aplicado",
                    "passo 3.5/4 - reparador",
                    ("modulo", "Obj.Honorarios.HonorariosFacade (Backfill + Enricher)"),
                    ("changed_keys_a", DescribeChangedKeys(beforeHonorariosA, valuesA)),
                    ("changed_keys_b", DescribeChangedKeys(beforeHonorariosB, valuesB)),
                    ("status_honorarios_a", honorariosA?.Summary?.PdfA?.Status ?? "(sem status)"),
                    ("status_honorarios_b", honorariosB?.Summary?.PdfA?.Status ?? "(sem status)"),
                    ("especialidade_flow_a", DescribeFieldFlowInline(beforeHonorariosA, valuesA, honorariosA?.DerivedValues, "ESPECIALIDADE")),
                    ("especialidade_flow_b", DescribeFieldFlowInline(beforeHonorariosB, valuesB, honorariosB?.DerivedValues, "ESPECIALIDADE")),
                    ("especie_flow_a", DescribeFieldFlowInline(beforeHonorariosA, valuesA, honorariosA?.DerivedValues, "ESPECIE_DA_PERICIA")),
                    ("especie_flow_b", DescribeFieldFlowInline(beforeHonorariosB, valuesB, honorariosB?.DerivedValues, "ESPECIE_DA_PERICIA")),
                    ("valor_jz_flow_a", DescribeFieldFlowInline(beforeHonorariosA, valuesA, honorariosA?.DerivedValues, "VALOR_ARBITRADO_JZ")),
                    ("valor_jz_flow_b", DescribeFieldFlowInline(beforeHonorariosB, valuesB, honorariosB?.DerivedValues, "VALOR_ARBITRADO_JZ")),
                    ("valor_path_a_pre_validator", DescribeMoneyPath(valuesA, fieldsA)),
                    ("valor_path_b_pre_validator", DescribeMoneyPath(valuesB, fieldsB)),
                    ("derived_valor_a", honorariosA?.DerivedValor ?? ""),
                    ("derived_valor_b", honorariosB?.DerivedValor ?? "")
                );
            }

            var honorariosPayload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = true,
                ["module"] = "Obj.Honorarios.HonorariosFacade",
                ["apply_a"] = true,
                ["apply_b"] = true,
                ["pdf_a"] = BuildHonorariosSnapshot(honorariosA),
                ["pdf_b"] = BuildHonorariosSnapshot(honorariosB)
            };
            onStepOutput?.Invoke(4, "honorarios", "ok", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["module"] = "Obj.Honorarios.HonorariosFacade",
                ["scope"] = extractionScope,
                ["target_side"] = targetSide,
                ["changed_keys_a"] = DescribeChangedKeys(beforeHonorariosA, valuesA),
                ["changed_keys_b"] = DescribeChangedKeys(beforeHonorariosB, valuesB),
                ["status_honorarios_a"] = honorariosA?.Summary?.PdfA?.Status ?? "",
                ["status_honorarios_b"] = honorariosB?.Summary?.PdfA?.Status ?? "",
                ["derived_valor_a"] = honorariosA?.DerivedValor ?? "",
                ["derived_valor_b"] = honorariosB?.DerivedValor ?? ""
            });
            if (stepLimit <= 4)
                return BuildExtractionResult(4, honorariosPayload, null, null, honorariosA?.DerivedValues, honorariosB?.DerivedValues);

            var catalog = ValidatorFacade.GetPeritoCatalog(null);
            var beforeRepairA = new Dictionary<string, string>(valuesA, StringComparer.OrdinalIgnoreCase);
            var beforeRepairB = new Dictionary<string, string>(valuesB, StringComparer.OrdinalIgnoreCase);
            var repairA = Obj.ValidationCore.ValidationRepairer.ApplyWithValidatorRules(valuesA, outputDocType, catalog);
            var repairB = Obj.ValidationCore.ValidationRepairer.ApplyWithValidatorRules(valuesB, outputDocType, catalog);
            MarkModuleChanges(beforeRepairA, valuesA, fieldsA, "repairer");
            MarkModuleChanges(beforeRepairB, valuesB, fieldsB, "repairer");

            if (verbose)
            {
                PrintPipelineStep(
                    "passo 3.5/4 - reparador",
                    "passo 3.6/4 - validação documental",
                    ("modulo", "Obj.ValidationCore.ValidationRepairer"),
                    ("repair_apply_a", repairA.Applied.ToString().ToLowerInvariant()),
                    ("repair_apply_b", repairB.Applied.ToString().ToLowerInvariant()),
                    ("repair_changed_a", Obj.ValidationCore.ValidationRepairer.DescribeChangedFields(repairA.ChangedFields)),
                    ("repair_changed_b", Obj.ValidationCore.ValidationRepairer.DescribeChangedFields(repairB.ChangedFields)),
                    ("repair_ok_a", repairA.Ok.ToString().ToLowerInvariant()),
                    ("repair_reason_a", string.IsNullOrWhiteSpace(repairA.Reason) ? "(ok)" : repairA.Reason),
                    ("repair_ok_b", repairB.Ok.ToString().ToLowerInvariant()),
                    ("repair_reason_b", string.IsNullOrWhiteSpace(repairB.Reason) ? "(ok)" : repairB.Reason),
                    ("repair_legacy_mirror_a", repairA.LegacyMirrorMatchesCore ? "match" : "mismatch"),
                    ("repair_legacy_mirror_b", repairB.LegacyMirrorMatchesCore ? "match" : "mismatch")
                );
            }

            var repairerPayload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = true,
                ["module"] = "Obj.ValidationCore.ValidationRepairer",
                ["apply_a"] = repairA.Applied,
                ["apply_b"] = repairB.Applied,
                ["ok_a"] = repairA.Ok,
                ["ok_b"] = repairB.Ok,
                ["reason_a"] = repairA.Reason,
                ["reason_b"] = repairB.Reason,
                ["changed_a"] = repairA.ChangedFields,
                ["changed_b"] = repairB.ChangedFields,
                ["legacy_mirror_a"] = repairA.LegacyMirrorMatchesCore,
                ["legacy_mirror_b"] = repairB.LegacyMirrorMatchesCore
            };
            onStepOutput?.Invoke(5, "repairer", "ok", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["module"] = "Obj.ValidationCore.ValidationRepairer",
                ["scope"] = extractionScope,
                ["target_side"] = targetSide,
                ["repair_apply_a"] = repairA.Applied,
                ["repair_apply_b"] = repairB.Applied,
                ["repair_ok_a"] = repairA.Ok,
                ["repair_ok_b"] = repairB.Ok,
                ["repair_reason_a"] = repairA.Reason ?? "",
                ["repair_reason_b"] = repairB.Reason ?? ""
            });
            if (stepLimit <= 5)
                return BuildExtractionResult(5, honorariosPayload, repairerPayload, null, honorariosA?.DerivedValues, honorariosB?.DerivedValues);

            var beforeValidatorA = new Dictionary<string, string>(valuesA, StringComparer.OrdinalIgnoreCase);
            var beforeValidatorB = new Dictionary<string, string>(valuesB, StringComparer.OrdinalIgnoreCase);
            var okA = ValidatorFacade.ApplyAndValidateDocumentValues(valuesA, outputDocType, catalog, out var reasonA, out var validatorChangedA);
            var okB = ValidatorFacade.ApplyAndValidateDocumentValues(valuesB, outputDocType, catalog, out var reasonB, out var validatorChangedB);
            var policyChangedA = EnforceStrictArbitradoPolicy(beforeHonorariosA, parserFieldsA, valuesA, fieldsA);
            var policyChangedB = EnforceStrictArbitradoPolicy(beforeHonorariosB, parserFieldsB, valuesB, fieldsB);
            MarkModuleChanges(beforeValidatorA, valuesA, fieldsA, "validator");
            MarkModuleChanges(beforeValidatorB, valuesB, fieldsB, "validator");
            var okPair = okA && okB;
            var reasonParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(reasonA))
                reasonParts.Add($"A:{reasonA}");
            if (!string.IsNullOrWhiteSpace(reasonB))
                reasonParts.Add($"B:{reasonB}");
            var reasonPair = reasonParts.Count == 0 ? "" : string.Join(" | ", reasonParts);
            var ok = string.Equals(extractionScope, "target_a_only(model_b_reference)", StringComparison.OrdinalIgnoreCase)
                ? okA
                : string.Equals(extractionScope, "target_b_only(model_a_reference)", StringComparison.OrdinalIgnoreCase)
                    ? okB
                    : okPair;
            var reason = string.Equals(extractionScope, "target_a_only(model_b_reference)", StringComparison.OrdinalIgnoreCase)
                ? (reasonA ?? "")
                : string.Equals(extractionScope, "target_b_only(model_a_reference)", StringComparison.OrdinalIgnoreCase)
                    ? (reasonB ?? "")
                    : reasonPair;
            if (verbose)
            {
                PrintPipelineStep(
                    "passo 3.6/4 - validação",
                    "passo 4/4 - resumo colorido e persistência",
                    ("modulo", "Obj.ValidatorModule.ValidatorFacade"),
                    ("changed_keys_validator_a_apply", validatorChangedA == null || validatorChangedA.Count == 0
                        ? "(nenhum)"
                        : string.Join(", ", validatorChangedA)),
                    ("changed_keys_validator_a", DescribeChangedKeys(beforeValidatorA, valuesA)),
                    ("changed_keys_validator_b", validatorChangedB == null || validatorChangedB.Count == 0
                        ? "(nenhum)"
                        : string.Join(", ", validatorChangedB)),
                    ("policy_strict_money_a", policyChangedA.Count == 0 ? "(nenhum)" : string.Join(", ", policyChangedA)),
                    ("policy_strict_money_b", policyChangedB.Count == 0 ? "(nenhum)" : string.Join(", ", policyChangedB)),
                    ("valor_path_a_pos_validator", DescribeMoneyPath(valuesA, fieldsA)),
                    ("valor_path_b_pos_validator", DescribeMoneyPath(valuesB, fieldsB)),
                    ("validator_ok_pair", okPair.ToString().ToLowerInvariant()),
                    ("validator_reason_pair", string.IsNullOrWhiteSpace(reasonPair) ? "(ok)" : reasonPair),
                    ("validator_ok_a", okA.ToString().ToLowerInvariant()),
                    ("validator_reason_a", string.IsNullOrWhiteSpace(reasonA) ? "(ok)" : reasonA!),
                    ("validator_ok_b", okB.ToString().ToLowerInvariant()),
                    ("validator_reason_b", string.IsNullOrWhiteSpace(reasonB) ? "(ok)" : reasonB!)
                );
            }

            var validatorPayload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = true,
                ["module"] = "Obj.ValidatorModule.ValidatorFacade",
                ["scope"] = extractionScope,
                ["ok"] = ok,
                ["ok_pair"] = okPair,
                ["ok_a"] = okA,
                ["ok_b"] = okB,
                ["reason"] = reason,
                ["reason_pair"] = reasonPair,
                ["reason_a"] = reasonA ?? "",
                ["reason_b"] = reasonB ?? ""
            };
            onStepOutput?.Invoke(6, "validator", "ok", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["module"] = "Obj.ValidatorModule.ValidatorFacade",
                ["scope"] = extractionScope,
                ["target_side"] = targetSide,
                ["ok"] = ok,
                ["ok_pair"] = okPair,
                ["ok_a"] = okA,
                ["ok_b"] = okB,
                ["reason"] = reason,
                ["reason_pair"] = reasonPair,
                ["policy_strict_money_a"] = policyChangedA,
                ["policy_strict_money_b"] = policyChangedB
            });

            return BuildExtractionResult(6, honorariosPayload, repairerPayload, validatorPayload, honorariosA?.DerivedValues, honorariosB?.DerivedValues);
        }

        private static Dictionary<string, string> ReadValuesForProbe(object? extraction, string sideKey)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (extraction == null)
                return values;

            try
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(extraction));
                var root = doc.RootElement;
                if (!root.TryGetProperty("parsed", out var parsed) || parsed.ValueKind != JsonValueKind.Object)
                    return values;
                if (!parsed.TryGetProperty(sideKey, out var side) || side.ValueKind != JsonValueKind.Object)
                    return values;

                JsonElement valuesEl;
                var hasValues = side.TryGetProperty("values", out valuesEl) || side.TryGetProperty("Values", out valuesEl);
                if (!hasValues || valuesEl.ValueKind != JsonValueKind.Object)
                    return values;

                foreach (var prop in valuesEl.EnumerateObject())
                {
                    var value = prop.Value.ValueKind == JsonValueKind.String
                        ? (prop.Value.GetString() ?? "")
                        : prop.Value.ToString();
                    values[prop.Name] = value ?? "";
                }
            }
            catch
            {
                // best-effort only
            }

            return values;
        }

        private static void AttachProbeToExtraction(object? extraction, Dictionary<string, object> probePayload)
        {
            if (extraction is Dictionary<string, object> extractionDict)
            {
                extractionDict["probe"] = probePayload;
                return;
            }

            if (extraction is Dictionary<string, object?> extractionNullableDict)
                extractionNullableDict["probe"] = probePayload;
        }

        private static void PrintProbeSummary(Dictionary<string, object> probePayload)
        {
            if (probePayload == null)
                return;

            try
            {
                var status = probePayload.TryGetValue("status", out var statusObj) ? statusObj?.ToString() ?? "" : "";
                var pdf = probePayload.TryGetValue("pdf", out var pdfObj) ? pdfObj?.ToString() ?? "" : "";
                var page = probePayload.TryGetValue("page", out var pageObj) ? pageObj?.ToString() ?? "0" : "0";
                var side = probePayload.TryGetValue("side", out var sideObj) ? sideObj?.ToString() ?? "" : "";
                var checkedFields = probePayload.TryGetValue("fields_checked", out var checkedObj) ? checkedObj?.ToString() ?? "0" : "0";
                var found = probePayload.TryGetValue("found", out var foundObj) ? foundObj?.ToString() ?? "0" : "0";
                var missing = probePayload.TryGetValue("missing", out var missingObj) ? missingObj?.ToString() ?? "0" : "0";
                var ok = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);

                Console.WriteLine(Colorize(
                    $"[PROBE] {(ok ? "OK" : "FAIL")} side={side} file={Path.GetFileName(pdf)} page={page} found={found}/{checkedFields} missing={missing}",
                    ok ? AnsiOk : AnsiErr));

                if (probePayload.TryGetValue("items", out var itemsObj) && itemsObj is List<Dictionary<string, object>> items)
                {
                    foreach (var item in items)
                    {
                        var field = item.TryGetValue("field", out var fObj) ? fObj?.ToString() ?? "" : "";
                        var value = item.TryGetValue("value", out var vObj) ? vObj?.ToString() ?? "" : "";
                        var itemFound = item.TryGetValue("found", out var ffObj) && ffObj is bool b && b;
                        var method = item.TryGetValue("method", out var mObj) ? mObj?.ToString() ?? "" : "";
                        var matches = item.TryGetValue("matches", out var mmObj) ? mmObj?.ToString() ?? "0" : "0";
                        var color = itemFound ? AnsiOk : AnsiErr;
                        var valueDisplay = ShortText(value, 64);
                        Console.WriteLine($"  {Colorize(field + ":", AnsiWarn)} {Colorize(valueDisplay, AnsiDodgeBlue)} -> {Colorize(itemFound ? "FOUND" : "MISS", color)} method={method} matches={matches}");
                    }
                }
                Console.WriteLine();
            }
            catch
            {
                // best-effort only
            }
        }

        private static void PrintExtractionSummary(object? extraction)
        {
            if (extraction == null)
                return;

            try
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(extraction));
                var root = doc.RootElement;
                var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() ?? "" : "";
                var ok = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
                var docKey = root.TryGetProperty("doc_key", out var docEl) ? docEl.GetString() ?? "" : "";
                var docType = root.TryGetProperty("doc_type", out var typeEl) ? typeEl.GetString() ?? "" : "";
                var band = root.TryGetProperty("band", out var bandEl) ? bandEl.GetString() ?? "" : "";
                var scope = "";
                var targetSide = "";
                if (root.TryGetProperty("pipeline", out var pipeline) && pipeline.ValueKind == JsonValueKind.Object)
                {
                    if (pipeline.TryGetProperty("scope", out var scopeEl))
                        scope = scopeEl.GetString() ?? "";
                    if (pipeline.TryGetProperty("target_side", out var targetSideEl))
                        targetSide = targetSideEl.GetString() ?? "";
                }

                Console.WriteLine(Colorize(
                    $"[EXTRACTION] {(ok ? "OK" : "FAIL")} doc={docKey} type={docType} band={band} scope={scope}",
                    ok ? AnsiOk : AnsiErr));

                if (root.TryGetProperty("parsed", out var parsed))
                {
                    if (string.Equals(scope, "target_b_only(model_a_reference)", StringComparison.OrdinalIgnoreCase))
                    {
                        PrintSideValues(parsed, "pdf_b", "VALORES EXTRAIDOS (PDF ALVO B)");
                    }
                    else if (string.Equals(scope, "target_a_only(model_b_reference)", StringComparison.OrdinalIgnoreCase))
                    {
                        PrintSideValues(parsed, "pdf_a", "VALORES EXTRAIDOS (PDF ALVO A)");
                    }
                    else
                    {
                        PrintSideValues(parsed, "pdf_a", "VALORES A");
                        PrintSideValues(parsed, "pdf_b", "VALORES B");
                    }
                }

                if (root.TryGetProperty("validator", out var validator))
                {
                    var vOk = validator.TryGetProperty("ok", out var vOkEl) && vOkEl.ValueKind == JsonValueKind.True;
                    var okPair = validator.TryGetProperty("ok_pair", out var okPairEl) && okPairEl.ValueKind == JsonValueKind.True;
                    var okA = validator.TryGetProperty("ok_a", out var okAEl) && okAEl.ValueKind == JsonValueKind.True;
                    var okB = validator.TryGetProperty("ok_b", out var okBEl) && okBEl.ValueKind == JsonValueKind.True;
                    var reason = validator.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? "" : "";
                    var reasonPair = validator.TryGetProperty("reason_pair", out var reasonPairEl) ? reasonPairEl.GetString() ?? "" : "";
                    var reasonA = validator.TryGetProperty("reason_a", out var reasonAEl) ? reasonAEl.GetString() ?? "" : "";
                    var reasonB = validator.TryGetProperty("reason_b", out var reasonBEl) ? reasonBEl.GetString() ?? "" : "";
                    var color = vOk ? AnsiOk : AnsiErr;
                    Console.WriteLine(Colorize("[VALIDATOR]", color));
                    Console.WriteLine($"  {Colorize("scope:", AnsiWarn)} {Colorize(string.IsNullOrWhiteSpace(scope) ? "(n/a)" : scope, AnsiSoft)}");
                    Console.WriteLine($"  {Colorize("target_side:", AnsiWarn)} {Colorize(string.IsNullOrWhiteSpace(targetSide) ? "(n/a)" : targetSide, AnsiSoft)}");
                    Console.WriteLine($"  {Colorize("effective:", AnsiWarn)} {Colorize(vOk ? "ok" : "fail", color)} reason=\"{(string.IsNullOrWhiteSpace(reason) ? "(ok)" : reason)}\"");
                    Console.WriteLine($"  {Colorize("pair:", AnsiWarn)} {Colorize(okPair ? "ok" : "fail", okPair ? AnsiOk : AnsiErr)} reason=\"{(string.IsNullOrWhiteSpace(reasonPair) ? "(ok)" : reasonPair)}\"");
                    Console.WriteLine($"  {Colorize("pdf_a:", AnsiWarn)} {Colorize(okA ? "ok" : "fail", okA ? AnsiOk : AnsiErr)} reason=\"{(string.IsNullOrWhiteSpace(reasonA) ? "(ok)" : reasonA)}\"");
                    Console.WriteLine($"  {Colorize("pdf_b:", AnsiWarn)} {Colorize(okB ? "ok" : "fail", okB ? AnsiOk : AnsiErr)} reason=\"{(string.IsNullOrWhiteSpace(reasonB) ? "(ok)" : reasonB)}\"");
                }
                Console.WriteLine();
            }
            catch
            {
                // summary is best-effort only
            }
        }

        private static void PrintSideValues(JsonElement parsed, string sideProp, string label)
        {
            if (!parsed.TryGetProperty(sideProp, out var side))
                return;
            var hasValues = side.TryGetProperty("values", out var values) || side.TryGetProperty("Values", out values);
            if (!hasValues || values.ValueKind != JsonValueKind.Object)
                return;
            var hasFields = side.TryGetProperty("fields", out var fields) || side.TryGetProperty("Fields", out fields);

            var pairs = new List<(string Key, string Value)>();
            foreach (var prop in values.EnumerateObject())
            {
                var value = prop.Value.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(value))
                    pairs.Add((prop.Name, value));
            }

            pairs = pairs.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
            Console.WriteLine(Colorize($"[{label}] ({pairs.Count})", AnsiInfo));
            foreach (var (key, value) in pairs)
            {
                string source = "(sem source)";
                string opRange = "(sem op_range)";
                string status = "";
                string moduleTag = "parser";
                var obj = 0;
                var parserValue = "";
                if (hasFields && fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(key, out var fieldMeta))
                {
                    if (fieldMeta.TryGetProperty("Source", out var sourceEl))
                        source = string.IsNullOrWhiteSpace(sourceEl.GetString()) ? "(vazio)" : sourceEl.GetString() ?? "(vazio)";
                    if (fieldMeta.TryGetProperty("Module", out var moduleEl))
                        moduleTag = string.IsNullOrWhiteSpace(moduleEl.GetString()) ? "parser" : moduleEl.GetString() ?? "parser";
                    if (fieldMeta.TryGetProperty("OpRange", out var rangeEl))
                        opRange = string.IsNullOrWhiteSpace(rangeEl.GetString()) ? "(vazio)" : rangeEl.GetString() ?? "(vazio)";
                    if (fieldMeta.TryGetProperty("Status", out var statusEl))
                        status = statusEl.GetString() ?? "";
                    if (fieldMeta.TryGetProperty("Obj", out var objEl) && objEl.TryGetInt32(out var parsedObj))
                        obj = parsedObj;
                    if (fieldMeta.TryGetProperty("Value", out var parserEl))
                        parserValue = parserEl.GetString() ?? "";
                }

                var moduleDisplay = ResolveModuleDisplay(moduleTag);
                Console.WriteLine($"  {Colorize(key + ":", AnsiWarn)} {Colorize("\"" + value + "\"", AnsiDodgeBlue)}");
                Console.WriteLine($"    origem={source} op={opRange} obj={obj} status={status} modulo={ColorizeModuleChain(moduleDisplay)}");
                var adjustModule = ResolveLastAdjustmentModule(moduleTag);

                if (!string.IsNullOrWhiteSpace(adjustModule))
                {
                    if (string.IsNullOrWhiteSpace(parserValue) && !string.IsNullOrWhiteSpace(value))
                        Console.WriteLine($"    ajuste={adjustModule} parser=\"\" (valor preenchido por módulo autorizado)");
                    else if (!string.IsNullOrWhiteSpace(parserValue) && !SameNormalizedValue(parserValue, value))
                        Console.WriteLine($"    ajuste={adjustModule} parser={ShortText(parserValue, 72)}");
                }
            }
        }

        private static string ResolveModuleDisplay(string moduleTags)
        {
            if (string.IsNullOrWhiteSpace(moduleTags))
                return "Obj.Commands.ObjectsMapFields";

            var mapped = new List<string>();
            var parts = moduleTags.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var name = MapModuleTag(part);
                if (!mapped.Contains(name, StringComparer.OrdinalIgnoreCase))
                    mapped.Add(name);
            }

            if (mapped.Count == 0)
                return "Obj.Commands.ObjectsMapFields";
            return string.Join("|", mapped);
        }

        private static string ResolveLastAdjustmentModule(string moduleTags)
        {
            if (string.IsNullOrWhiteSpace(moduleTags))
                return "";

            var parts = moduleTags.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = parts.Length - 1; i >= 0; i--)
            {
                var part = parts[i];
                if (string.Equals(part, "parser", StringComparison.OrdinalIgnoreCase))
                    continue;
                return MapModuleTag(part);
            }
            return "";
        }

        private static string MapModuleTag(string moduleTag)
        {
            var norm = (moduleTag ?? "").Trim().ToLowerInvariant();
            return norm switch
            {
                "parser" => "Obj.Commands.ObjectsMapFields",
                "honorarios" => "Obj.Honorarios.HonorariosFacade",
                "repairer" => "Obj.ValidationCore.ValidationRepairer",
                "validator" => "Obj.ValidatorModule.ValidatorFacade",
                _ => moduleTag
            };
        }

        private static void AddInput(List<string> inputs, string rawValue)
        {
            var t = (rawValue ?? "").Trim();
            if (t.Length == 0) return;
            var norm = PathUtils.NormalizeArgs(new[] { t });
            var resolved = norm.Length > 0 ? norm[0] : t;
            foreach (var part in resolved.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var piece = part.Trim();
                if (piece.Length > 0)
                    inputs.Add(piece);
            }
        }

        private static bool TryReadEnvBool(string key, out bool value)
        {
            value = false;
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            var t = raw.Trim().ToLowerInvariant();
            if (t is "1" or "true" or "yes" or "on")
            {
                value = true;
                return true;
            }
            if (t is "0" or "false" or "no" or "off")
            {
                value = false;
                return true;
            }
            return false;
        }

        private static bool TryReadEnvInt(string key, out int value)
        {
            value = 0;
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            return int.TryParse(raw.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadEnvDouble(string key, out double value)
        {
            value = 0;
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            return double.TryParse(raw.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private static int AddInputsFromEnv(List<string> inputs, string key)
        {
            var before = inputs.Count;
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw))
                return 0;
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                AddInput(inputs, token);
            return Math.Max(0, inputs.Count - before);
        }

        private static int AddOpFilterFromEnv(HashSet<string> opFilter, string key)
        {
            var before = opFilter.Count;
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw))
                return 0;
            foreach (var op in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = op.Trim();
                if (t.Length > 0)
                    opFilter.Add(t);
            }
            return Math.Max(0, opFilter.Count - before);
        }

        private static void ApplyTextOpsAlignEnvDefaults(
            ref List<string> inputs,
            ref int pageA,
            ref int pageB,
            ref int objA,
            ref int objB,
            ref HashSet<string> opFilter,
            ref int backoff,
            ref string outPath,
            ref bool outSpecified,
            ref int top,
            ref double minSim,
            ref int band,
            ref double minLenRatio,
            ref double lenPenalty,
            ref double anchorMinSim,
            ref double anchorMinLenRatio,
            ref double gapPenalty,
            ref bool showAlign,
            ref int alignTop,
            ref bool pageAUser,
            ref bool pageBUser,
            ref bool objAUser,
            ref bool objBUser,
            ref string docKey,
            ref bool useBack,
            ref bool sideSpecified,
            ref bool allowStack,
            ref bool probeEnabled,
            ref string probeFile,
            ref int probePage,
            ref string probeSide,
            ref int probeMaxFields,
            ref int runFromStep,
            ref int runToStep,
            ref bool stepOutputEcho,
            ref bool stepOutputSave,
            ref string stepOutputDir,
            List<string> applied)
        {
            var envOpsAdded = AddOpFilterFromEnv(opFilter, "OBJ_TEXTOPSALIGN_OPS");
            if (envOpsAdded > 0)
                applied.Add($"OBJ_TEXTOPSALIGN_OPS -> +{envOpsAdded} op(s) [{string.Join(",", opFilter.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}]");

            if (TryReadEnvInt("OBJ_TEXTOPSALIGN_PAGE_A", out var envPageA) && envPageA > 0)
            {
                pageA = envPageA;
                pageAUser = true;
                applied.Add($"OBJ_TEXTOPSALIGN_PAGE_A -> pageA={pageA.ToString(CultureInfo.InvariantCulture)}");
            }
            if (TryReadEnvInt("OBJ_TEXTOPSALIGN_PAGE_B", out var envPageB) && envPageB > 0)
            {
                pageB = envPageB;
                pageBUser = true;
                applied.Add($"OBJ_TEXTOPSALIGN_PAGE_B -> pageB={pageB.ToString(CultureInfo.InvariantCulture)}");
            }
            if (TryReadEnvInt("OBJ_TEXTOPSALIGN_OBJ_A", out var envObjA) && envObjA > 0)
            {
                objA = envObjA;
                objAUser = true;
                applied.Add($"OBJ_TEXTOPSALIGN_OBJ_A -> objA={objA.ToString(CultureInfo.InvariantCulture)}");
            }
            if (TryReadEnvInt("OBJ_TEXTOPSALIGN_OBJ_B", out var envObjB) && envObjB > 0)
            {
                objB = envObjB;
                objBUser = true;
                applied.Add($"OBJ_TEXTOPSALIGN_OBJ_B -> objB={objB.ToString(CultureInfo.InvariantCulture)}");
            }

            if (TryReadEnvInt("OBJ_TEXTOPSALIGN_BACKOFF", out var envBackoff))
            {
                backoff = envBackoff;
                applied.Add($"OBJ_TEXTOPSALIGN_BACKOFF -> backoff={backoff.ToString(CultureInfo.InvariantCulture)}");
            }
            if (TryReadEnvInt("OBJ_TEXTOPSALIGN_TOP", out var envTop))
            {
                top = envTop;
                applied.Add($"OBJ_TEXTOPSALIGN_TOP -> top={top.ToString(CultureInfo.InvariantCulture)}");
            }
            if (TryReadEnvDouble("OBJ_TEXTOPSALIGN_MIN_SIM", out var envMinSim))
            {
                minSim = envMinSim;
                applied.Add($"OBJ_TEXTOPSALIGN_MIN_SIM -> min_sim={ReportUtils.F(minSim, 3)}");
            }
            if (TryReadEnvInt("OBJ_TEXTOPSALIGN_BAND", out var envBand))
            {
                band = envBand;
                applied.Add($"OBJ_TEXTOPSALIGN_BAND -> band={band.ToString(CultureInfo.InvariantCulture)}");
            }
            if (TryReadEnvDouble("OBJ_TEXTOPSALIGN_MIN_LEN_RATIO", out var envMinLenRatio))
            {
                minLenRatio = Math.Max(0, Math.Min(1, envMinLenRatio));
                applied.Add($"OBJ_TEXTOPSALIGN_MIN_LEN_RATIO -> min_len_ratio={ReportUtils.F(minLenRatio, 3)}");
            }
            if (TryReadEnvDouble("OBJ_TEXTOPSALIGN_LEN_PENALTY", out var envLenPenalty))
            {
                lenPenalty = Math.Max(0, Math.Min(1, envLenPenalty));
                applied.Add($"OBJ_TEXTOPSALIGN_LEN_PENALTY -> len_penalty={ReportUtils.F(lenPenalty, 3)}");
            }
            if (TryReadEnvDouble("OBJ_TEXTOPSALIGN_ANCHOR_MIN_SIM", out var envAnchorSim))
            {
                anchorMinSim = Math.Max(0, Math.Min(1, envAnchorSim));
                applied.Add($"OBJ_TEXTOPSALIGN_ANCHOR_MIN_SIM -> anchor_sim={ReportUtils.F(anchorMinSim, 3)}");
            }
            if (TryReadEnvDouble("OBJ_TEXTOPSALIGN_ANCHOR_MIN_LEN_RATIO", out var envAnchorLen))
            {
                anchorMinLenRatio = Math.Max(0, Math.Min(1, envAnchorLen));
                applied.Add($"OBJ_TEXTOPSALIGN_ANCHOR_MIN_LEN_RATIO -> anchor_len={ReportUtils.F(anchorMinLenRatio, 3)}");
            }
            if (TryReadEnvDouble("OBJ_TEXTOPSALIGN_GAP_PENALTY", out var envGap))
            {
                gapPenalty = Math.Max(-1, Math.Min(1, envGap));
                applied.Add($"OBJ_TEXTOPSALIGN_GAP_PENALTY -> gap_penalty={ReportUtils.F(gapPenalty, 3)}");
            }
            if (TryReadEnvInt("OBJ_TEXTOPSALIGN_ALIGN_TOP", out var envAlignTop))
            {
                alignTop = envAlignTop;
                applied.Add($"OBJ_TEXTOPSALIGN_ALIGN_TOP -> alinhamento_top={alignTop.ToString(CultureInfo.InvariantCulture)}");
            }
            if (TryReadEnvBool("OBJ_TEXTOPSALIGN_SHOW_ALIGN", out var envShowAlign))
            {
                showAlign = envShowAlign;
                applied.Add($"OBJ_TEXTOPSALIGN_SHOW_ALIGN -> show_alignment={showAlign.ToString().ToLowerInvariant()}");
            }
            if (TryReadEnvBool("OBJ_TEXTOPSALIGN_ALLOW_STACK", out var envAllowStack))
            {
                allowStack = envAllowStack;
                applied.Add($"OBJ_TEXTOPSALIGN_ALLOW_STACK -> allow_stack={allowStack.ToString().ToLowerInvariant()}");
            }

            var envOut = Environment.GetEnvironmentVariable("OBJ_TEXTOPSALIGN_OUT");
            if (!string.IsNullOrWhiteSpace(envOut))
            {
                outPath = envOut.Trim();
                outSpecified = true;
                applied.Add($"OBJ_TEXTOPSALIGN_OUT -> out_path={outPath}");
            }

            var envDoc = Environment.GetEnvironmentVariable("OBJ_TEXTOPSALIGN_DOC");
            if (!string.IsNullOrWhiteSpace(envDoc))
            {
                docKey = envDoc.Trim();
                applied.Add($"OBJ_TEXTOPSALIGN_DOC -> doc_key={docKey}");
            }

            var envSide = (Environment.GetEnvironmentVariable("OBJ_TEXTOPSALIGN_SIDE") ?? "").Trim().ToLowerInvariant();
            if (envSide is "back" or "p2" or "verso")
            {
                useBack = true;
                sideSpecified = true;
                applied.Add("OBJ_TEXTOPSALIGN_SIDE -> use_back=true side_specified=true");
            }
            else if (envSide is "front" or "p1" or "anverso")
            {
                useBack = false;
                sideSpecified = true;
                applied.Add("OBJ_TEXTOPSALIGN_SIDE -> use_back=false side_specified=true");
            }

            if (TryReadEnvBool("OBJ_TEXTOPSALIGN_BACK", out var envBack))
            {
                useBack = envBack;
                sideSpecified = true;
                applied.Add($"OBJ_TEXTOPSALIGN_BACK -> use_back={useBack.ToString().ToLowerInvariant()} side_specified=true");
            }

            if (TryReadEnvBool("OBJ_TEXTOPSALIGN_PROBE", out var envProbe))
            {
                probeEnabled = envProbe;
                applied.Add($"OBJ_TEXTOPSALIGN_PROBE -> probe_enabled={probeEnabled.ToString().ToLowerInvariant()}");
            }
            var envProbeFile = Environment.GetEnvironmentVariable("OBJ_TEXTOPSALIGN_PROBE_FILE");
            if (!string.IsNullOrWhiteSpace(envProbeFile))
            {
                probeFile = envProbeFile.Trim();
                probeEnabled = true;
                applied.Add($"OBJ_TEXTOPSALIGN_PROBE_FILE -> probe_file={probeFile} probe_enabled=true");
            }
            if (TryReadEnvInt("OBJ_TEXTOPSALIGN_PROBE_PAGE", out var envProbePage))
            {
                probePage = Math.Max(0, envProbePage);
                probeEnabled = true;
                applied.Add($"OBJ_TEXTOPSALIGN_PROBE_PAGE -> probe_page={probePage.ToString(CultureInfo.InvariantCulture)} probe_enabled=true");
            }
            var envProbeSide = (Environment.GetEnvironmentVariable("OBJ_TEXTOPSALIGN_PROBE_SIDE") ?? "").Trim().ToLowerInvariant();
            if (envProbeSide is "a" or "pdf_a" or "left")
            {
                probeSide = "a";
                probeEnabled = true;
                applied.Add("OBJ_TEXTOPSALIGN_PROBE_SIDE -> probe_side=a probe_enabled=true");
            }
            else if (envProbeSide is "b" or "pdf_b" or "right")
            {
                probeSide = "b";
                probeEnabled = true;
                applied.Add("OBJ_TEXTOPSALIGN_PROBE_SIDE -> probe_side=b probe_enabled=true");
            }
            if (TryReadEnvInt("OBJ_TEXTOPSALIGN_PROBE_MAX_FIELDS", out var envProbeMax))
            {
                probeMaxFields = Math.Max(0, envProbeMax);
                probeEnabled = true;
                applied.Add($"OBJ_TEXTOPSALIGN_PROBE_MAX_FIELDS -> probe_max_fields={probeMaxFields.ToString(CultureInfo.InvariantCulture)} probe_enabled=true");
            }

            var envRun = Environment.GetEnvironmentVariable("OBJ_TEXTOPSALIGN_RUN");
            if (!string.IsNullOrWhiteSpace(envRun) && TryParseRunRange(envRun, out var envFrom, out var envTo))
            {
                runFromStep = envFrom;
                runToStep = envTo;
                applied.Add($"OBJ_TEXTOPSALIGN_RUN -> run={runFromStep.ToString(CultureInfo.InvariantCulture)}-{runToStep.ToString(CultureInfo.InvariantCulture)}");
            }

            if (TryReadEnvBool("OBJ_TEXTOPSALIGN_STEP_OUTPUT_ECHO", out var envStepEcho))
            {
                stepOutputEcho = envStepEcho;
                applied.Add($"OBJ_TEXTOPSALIGN_STEP_OUTPUT_ECHO -> step_output_echo={stepOutputEcho.ToString().ToLowerInvariant()}");
            }
            if (TryReadEnvBool("OBJ_TEXTOPSALIGN_STEP_OUTPUT_SAVE", out var envStepSave))
            {
                stepOutputSave = envStepSave;
                applied.Add($"OBJ_TEXTOPSALIGN_STEP_OUTPUT_SAVE -> step_output_save={stepOutputSave.ToString().ToLowerInvariant()}");
            }
            var envStepDir = Environment.GetEnvironmentVariable("OBJ_TEXTOPSALIGN_STEP_OUTPUT_DIR");
            if (!string.IsNullOrWhiteSpace(envStepDir))
            {
                stepOutputDir = envStepDir.Trim();
                stepOutputSave = true;
                applied.Add($"OBJ_TEXTOPSALIGN_STEP_OUTPUT_DIR -> step_output_dir={stepOutputDir} step_output_save=true");
            }
        }

        private static bool ParseOptions(
            string[] args,
            out List<string> inputs,
            out int pageA,
            out int pageB,
            out int objA,
            out int objB,
            out HashSet<string> opFilter,
            out int backoff,
            out string outPath,
            out bool outSpecified,
            out int top,
            out double minSim,
            out int band,
            out double minLenRatio,
            out double lenPenalty,
            out double anchorMinSim,
            out double anchorMinLenRatio,
            out double gapPenalty,
            out bool showAlign,
            out int alignTop,
            out bool pageAUser,
            out bool pageBUser,
            out bool objAUser,
            out bool objBUser,
            out string docKey,
            out bool useBack,
            out bool sideSpecified,
            out bool allowStack,
            out bool probeEnabled,
            out string probeFile,
            out int probePage,
            out string probeSide,
            out int probeMaxFields,
            out int runFromStep,
            out int runToStep,
            out bool stepOutputEcho,
            out bool stepOutputSave,
            out string stepOutputDir,
            out List<string> appliedAutoDefaults)
        {
            inputs = new List<string>();
            pageA = 0;
            pageB = 0;
            objA = 0;
            objB = 0;
            opFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            backoff = 2;
            outPath = "";
            outSpecified = false;
            top = 10;
            minSim = 0.12;
            band = 2;
            minLenRatio = 0.20;
            lenPenalty = 0.4;
            anchorMinSim = 0.0;
            anchorMinLenRatio = 0.0;
            gapPenalty = -0.20;
            showAlign = true;
            alignTop = 0;
            pageAUser = false;
            pageBUser = false;
            objAUser = false;
            objBUser = false;
            docKey = "";
            useBack = false;
            sideSpecified = false;
            allowStack = false;
            probeEnabled = false;
            probeFile = "";
            probePage = 0;
            probeSide = "b";
            probeMaxFields = 0;
            runFromStep = PipelineFirstStep;
            runToStep = PipelineLastStep;
            stepOutputEcho = false;
            stepOutputSave = false;
            stepOutputDir = "";
            appliedAutoDefaults = new List<string>();

            var forbiddenEnvInputs = Environment.GetEnvironmentVariable("OBJ_TEXTOPSALIGN_INPUTS");
            if (!string.IsNullOrWhiteSpace(forbiddenEnvInputs))
            {
                Console.WriteLine("Erro: OBJ_TEXTOPSALIGN_INPUTS não é suportado. Use --inputs explícito no comando.");
                return false;
            }

            ApplyTextOpsAlignEnvDefaults(
                ref inputs,
                ref pageA,
                ref pageB,
                ref objA,
                ref objB,
                ref opFilter,
                ref backoff,
                ref outPath,
                ref outSpecified,
                ref top,
                ref minSim,
                ref band,
                ref minLenRatio,
                ref lenPenalty,
                ref anchorMinSim,
                ref anchorMinLenRatio,
                ref gapPenalty,
                ref showAlign,
                ref alignTop,
                ref pageAUser,
                ref pageBUser,
                ref objAUser,
                ref objBUser,
                ref docKey,
                ref useBack,
                ref sideSpecified,
                ref allowStack,
                ref probeEnabled,
                ref probeFile,
                ref probePage,
                ref probeSide,
                ref probeMaxFields,
                ref runFromStep,
                ref runToStep,
                ref stepOutputEcho,
                ref stepOutputSave,
                ref stepOutputDir,
                appliedAutoDefaults);

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == "--help" || arg == "-h")
                    return false;
                if ((string.Equals(arg, "run", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "steps", StringComparison.OrdinalIgnoreCase))
                    && i + 1 < args.Length
                    && TryParseRunRange(args[i + 1], out runFromStep, out runToStep))
                {
                    i++;
                    stepOutputEcho = true;
                    continue;
                }

                if (string.Equals(arg, "--inputs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        AddInput(inputs, token);
                    }
                    continue;
                }
                if (string.Equals(arg, "--doc", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    docKey = (args[++i] ?? "").Trim();
                    continue;
                }
                if (string.Equals(arg, "--back", StringComparison.OrdinalIgnoreCase))
                {
                    useBack = true;
                    sideSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--front", StringComparison.OrdinalIgnoreCase))
                {
                    useBack = false;
                    sideSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--side", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var side = (args[++i] ?? "").Trim().ToLowerInvariant();
                    sideSpecified = true;
                    if (side == "back" || side == "p2" || side == "verso")
                        useBack = true;
                    else if (side == "front" || side == "p1" || side == "anverso")
                        useBack = false;
                    continue;
                }
                if (string.Equals(arg, "--pageA", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out pageA);
                    pageAUser = true;
                    continue;
                }
                if (string.Equals(arg, "--pageB", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out pageB);
                    pageBUser = true;
                    continue;
                }
                if (string.Equals(arg, "--objA", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out objA);
                    objAUser = true;
                    continue;
                }
                if (string.Equals(arg, "--objB", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out objB);
                    objBUser = true;
                    continue;
                }
                if ((string.Equals(arg, "--op", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--ops", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    foreach (var raw in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        opFilter.Add(raw.Trim());
                    continue;
                }
                if (string.Equals(arg, "--backoff", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out backoff);
                    continue;
                }
                if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    outPath = args[++i];
                    outSpecified = true;
                    continue;
                }
                if (string.Equals(arg, "--top", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out top);
                    continue;
                }
                if (string.Equals(arg, "--min-sim", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out minSim);
                    continue;
                }
                if ((string.Equals(arg, "--band", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--max-shift", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out band);
                    continue;
                }
                if ((string.Equals(arg, "--min-len-ratio", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--min-length-ratio", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out minLenRatio);
                    minLenRatio = Math.Max(0, Math.Min(1, minLenRatio));
                    continue;
                }
                if ((string.Equals(arg, "--len-penalty", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--length-penalty", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out lenPenalty);
                    lenPenalty = Math.Max(0, Math.Min(1, lenPenalty));
                    continue;
                }
                if ((string.Equals(arg, "--anchor-sim", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--anchors-sim", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out anchorMinSim);
                    anchorMinSim = Math.Max(0, Math.Min(1, anchorMinSim));
                    continue;
                }
                if ((string.Equals(arg, "--anchor-len", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--anchor-len-ratio", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out anchorMinLenRatio);
                    anchorMinLenRatio = Math.Max(0, Math.Min(1, anchorMinLenRatio));
                    continue;
                }
                if ((string.Equals(arg, "--gap", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--gap-penalty", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out gapPenalty);
                    gapPenalty = Math.Max(-1, Math.Min(1, gapPenalty));
                    continue;
                }
                if (string.Equals(arg, "--alinhamento-top", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out alignTop);
                    continue;
                }
                if (string.Equals(arg, "--alinhamento-detalhe", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--mostrar-alinhamento", StringComparison.OrdinalIgnoreCase))
                {
                    showAlign = true;
                    continue;
                }
                if (string.Equals(arg, "--sem-alinhamento", StringComparison.OrdinalIgnoreCase))
                {
                    showAlign = false;
                    continue;
                }
                if (string.Equals(arg, "--allow-stack", StringComparison.OrdinalIgnoreCase))
                {
                    allowStack = true;
                    continue;
                }
                if ((string.Equals(arg, "--run", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--steps", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    var rawRange = args[++i] ?? "";
                    if (!TryParseRunRange(rawRange, out runFromStep, out runToStep))
                    {
                        Console.WriteLine($"Faixa inválida para --run/--steps: {rawRange}. Use N ou N-M (1..8).");
                        return false;
                    }
                    stepOutputEcho = true;
                    continue;
                }
                if (arg.StartsWith("--run=", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("--steps=", StringComparison.OrdinalIgnoreCase))
                {
                    var split = arg.Split('=', 2);
                    var rawRange = split.Length == 2 ? split[1] : "";
                    if (!TryParseRunRange(rawRange, out runFromStep, out runToStep))
                    {
                        Console.WriteLine($"Faixa inválida para --run/--steps: {rawRange}. Use N ou N-M (1..8).");
                        return false;
                    }
                    stepOutputEcho = true;
                    continue;
                }
                if (string.Equals(arg, "--step-output", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var mode = (args[++i] ?? "").Trim().ToLowerInvariant();
                    if (mode is "echo" or "screen" or "stdout")
                    {
                        stepOutputEcho = true;
                    }
                    else if (mode is "save" or "file")
                    {
                        stepOutputSave = true;
                    }
                    else if (mode is "both" or "all")
                    {
                        stepOutputEcho = true;
                        stepOutputSave = true;
                    }
                    else if (mode is "none" or "off")
                    {
                        stepOutputEcho = false;
                        stepOutputSave = false;
                    }
                    continue;
                }
                if (string.Equals(arg, "--step-echo", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--show-step-output", StringComparison.OrdinalIgnoreCase))
                {
                    stepOutputEcho = true;
                    continue;
                }
                if (string.Equals(arg, "--step-save", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--save-step-output", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--save-steps", StringComparison.OrdinalIgnoreCase))
                {
                    stepOutputSave = true;
                    continue;
                }
                if ((string.Equals(arg, "--step-output-dir", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--steps-dir", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    stepOutputDir = (args[++i] ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(stepOutputDir))
                        stepOutputSave = true;
                    continue;
                }
                if (string.Equals(arg, "--probe", StringComparison.OrdinalIgnoreCase))
                {
                    probeEnabled = true;
                    if (i + 1 < args.Length)
                    {
                        var next = args[i + 1] ?? "";
                        if (!next.StartsWith("-", StringComparison.Ordinal))
                        {
                            probeFile = next.Trim().Trim('"');
                            i++;
                        }
                    }
                    continue;
                }
                if (arg.StartsWith("--probe=", StringComparison.OrdinalIgnoreCase))
                {
                    probeEnabled = true;
                    var split = arg.Split('=', 2);
                    probeFile = split.Length == 2 ? split[1].Trim().Trim('"') : "";
                    continue;
                }
                if (string.Equals(arg, "--probe-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    probeEnabled = true;
                    probeFile = (args[++i] ?? "").Trim().Trim('"');
                    continue;
                }
                if (string.Equals(arg, "--probe-page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    probeEnabled = true;
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out probePage);
                    continue;
                }
                if (string.Equals(arg, "--probe-side", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    probeEnabled = true;
                    var rawSide = (args[++i] ?? "").Trim().ToLowerInvariant();
                    if (rawSide == "a" || rawSide == "pdf_a" || rawSide == "left")
                        probeSide = "a";
                    else if (rawSide == "b" || rawSide == "pdf_b" || rawSide == "right")
                        probeSide = "b";
                    continue;
                }
                if (string.Equals(arg, "--probe-max-fields", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    probeEnabled = true;
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out probeMaxFields);
                    probeMaxFields = Math.Max(0, probeMaxFields);
                    continue;
                }

                if (!arg.StartsWith("-", StringComparison.Ordinal))
                {
                    foreach (var token in arg.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        AddInput(inputs, token);
                    }
                }
            }

            return true;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("operpdf inspect textopsalign|textopsvar|textopsfixed <pdfA> <pdfB|pdfC|...> [--inputs a.pdf,b.pdf] [--doc tjpb_despacho] [--front|--back|--side front|back] [--pageA N] [--pageB N] [--objA N] [--objB N] [--ops Tj,TJ] [--backoff N] [--min-sim N] [--band N|--max-shift N] [--min-len-ratio N] [--len-penalty N] [--anchor-sim N] [--anchor-len N] [--gap N] [--top N] [--alinhamento-detalhe] [--alinhamento-top N] [--sem-alinhamento] [--out file] [--run N|N-M] [--step-output echo|save|both|none] [--step-echo] [--step-save] [--steps-dir dir] [--probe[ file.pdf] --probe-page N --probe-side a|b --probe-max-fields N]");
            Console.WriteLine("atalho: run N-M (sem --), ex.: textopsalign-despacho run 1-4 --inputs @MODEL --inputs :Q22");
            Console.WriteLine("aliases de modelo por tipo: @M-DES (despacho), @M-CER (certidao), @M-REQ (requerimento). Se houver múltiplos modelos no alias, o pipeline testa e escolhe o melhor para o alvo.");
            Console.WriteLine("env: OBJ_TEXTOPSALIGN_* (defaults), ex.: OBJ_TEXTOPSALIGN_MIN_SIM=0.15 OBJ_TEXTOPSALIGN_PROBE=1 OBJ_TEXTOPSALIGN_RUN=1-4");
            Console.WriteLine("obs: OBJ_TEXTOPSALIGN_INPUTS não é suportado e aborta a execução; use sempre --inputs explícito (aliases :D/:Q/@M-*).");
        }

        private static void WriteStackedOutput(string aPath, List<ObjectsTextOpsDiff.AlignDebugReport> reports, string outPath)
        {
            var baseA = Path.GetFileNameWithoutExtension(aPath);
            if (string.IsNullOrWhiteSpace(outPath))
                outPath = Path.Combine("outputs", "align_ranges", $"{baseA}__STACK__textops_align.txt");

            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");

            var lines = new List<string>();
            var blocksA = reports[0].BlocksA;
            lines.Add($"MODEL: {Path.GetFileName(aPath)}");
            lines.Add($"TARGETS: {string.Join(" | ", reports.Select(r => r.PdfB))}");
            lines.Add("");

            for (int i = 0; i < blocksA.Count; i++)
            {
                var aBlock = blocksA[i];
                lines.Add($"[{i + 1:D3}] A({aBlock.Index}) op{aBlock.StartOp}-{aBlock.EndOp} | {aBlock.Text}");

                foreach (var report in reports)
                {
                    var match = report.Alignments.FirstOrDefault(p => p.AIndex == i);
                    var bText = match?.B?.Text ?? "<gap>";
                    var bIndex = match?.B != null ? match.B.Index.ToString(CultureInfo.InvariantCulture) : "-";
                    var kind = match?.Kind ?? "gap";
                    lines.Add($"    {report.PdfB} | B({bIndex}) | {kind} | {bText}");
                }

                lines.Add("");
            }

            File.WriteAllLines(outPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine(string.Join(Environment.NewLine, lines));
            Console.WriteLine("Arquivo salvo: " + outPath);
        }

        private static void PrintHumanSummary(ObjectsTextOpsDiff.AlignDebugReport report)
        {
            PrintHumanSummary(report, 10, OutputMode.All);
        }

        private static void PrintHumanSummary(ObjectsTextOpsDiff.AlignDebugReport report, int top)
        {
            PrintHumanSummary(report, top, OutputMode.All);
        }

        private static void PrintHumanSummary(ObjectsTextOpsDiff.AlignDebugReport report, int top, OutputMode outputMode)
        {
            var fixedCount = report.FixedPairs.Count;
            var varCount = report.Alignments.Count(p => string.Equals(p.Kind, "variable", StringComparison.OrdinalIgnoreCase));
            var gaps = report.Alignments.Count(p => p.Kind.StartsWith("gap", StringComparison.OrdinalIgnoreCase));
            var helper = report.HelperDiagnostics;
            var showVariables = outputMode != OutputMode.FixedOnly;
            var showFixed = outputMode != OutputMode.VariablesOnly;
            var summary = new List<(string Key, string Value)>
            {
                ("A", $"{report.PdfA} p{report.PageA} o{report.ObjA}"),
                ("B", $"{report.PdfB} p{report.PageB} o{report.ObjB}"),
                ("blocks", report.Alignments.Count.ToString(CultureInfo.InvariantCulture)),
                ("fixed", fixedCount.ToString(CultureInfo.InvariantCulture)),
                ("variable", varCount.ToString(CultureInfo.InvariantCulture)),
                ("gaps", gaps.ToString(CultureInfo.InvariantCulture)),
                ("minSim", ReportUtils.F(report.MinSim, 2)),
                ("band", report.Band.ToString(CultureInfo.InvariantCulture)),
                ("lenRatio", ReportUtils.F(report.MinLenRatio, 2)),
                ("lenPenalty", ReportUtils.F(report.LenPenalty, 2)),
                ("anchorSim", ReportUtils.F(report.AnchorMinSim, 2)),
                ("anchorLen", ReportUtils.F(report.AnchorMinLenRatio, 2)),
                ("gap", ReportUtils.F(report.GapPenalty, 2)),
                ("rangeA", report.RangeA.HasValue ? $"op{report.RangeA.StartOp}-op{report.RangeA.EndOp}" : "-"),
                ("rangeB", report.RangeB.HasValue ? $"op{report.RangeB.StartOp}-op{report.RangeB.EndOp}" : "-")
            };
            if (helper != null)
            {
                summary.Add(("helperHitsA", helper.HitsA.ToString(CultureInfo.InvariantCulture)));
                summary.Add(("helperHitsB", helper.HitsB.ToString(CultureInfo.InvariantCulture)));
                summary.Add(("helperAcc/Rej", $"{helper.Accepted}/{helper.Rejected}"));
                summary.Add(("helperUsed", helper.UsedInFinalAnchors.ToString(CultureInfo.InvariantCulture)));
            }
            ReportUtils.WriteSummary("TEXTOPS ALIGN", summary);
            Console.WriteLine();

            Console.WriteLine("ALIGN-HELPER");
            if (helper == null)
            {
                Console.WriteLine("  sem dados de helper para este alinhamento");
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine($"  hitsA={helper.HitsA} hitsB={helper.HitsB} candidates={helper.Candidates} accepted={helper.Accepted} rejected={helper.Rejected} used={helper.UsedInFinalAnchors}");

                var helperTake = top <= 0 ? 12 : Math.Max(6, Math.Min(24, top));
                var acceptedRows = helper.Decisions
                    .Where(d => string.Equals(d.Status, "accepted", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => d.Score)
                    .ThenBy(d => d.AIndex)
                    .ThenBy(d => d.BIndex)
                    .Take(helperTake)
                    .Select(d => new[]
                    {
                        d.Key,
                        d.AIndex >= 0 ? d.AIndex.ToString(CultureInfo.InvariantCulture) : "-",
                        d.BIndex >= 0 ? d.BIndex.ToString(CultureInfo.InvariantCulture) : "-",
                        ReportUtils.F(d.Score, 3),
                        d.Reason
                    })
                    .ToList();
                if (acceptedRows.Count > 0)
                    ReportUtils.WriteTable("ALIGN-HELPER ACCEPTED", new[] { "key", "Aidx", "Bidx", "score", "reason" }, acceptedRows);

                var rejectedRows = helper.Decisions
                    .Where(d => string.Equals(d.Status, "rejected", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(d => string.Equals(d.Status, "accepted", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(d => d.Score)
                    .ThenBy(d => d.AIndex)
                    .ThenBy(d => d.BIndex)
                    .Take(helperTake)
                    .Select(d => new[]
                    {
                        d.Key,
                        d.AIndex >= 0 ? d.AIndex.ToString(CultureInfo.InvariantCulture) : "-",
                        d.BIndex >= 0 ? d.BIndex.ToString(CultureInfo.InvariantCulture) : "-",
                        ReportUtils.F(d.Score, 3),
                        d.Reason
                    })
                    .ToList();
                if (rejectedRows.Count > 0)
                    ReportUtils.WriteTable("ALIGN-HELPER REJECTED (TOP)", new[] { "key", "Aidx", "Bidx", "score", "reason" }, rejectedRows);

                Console.WriteLine();
            }

            if (report.Anchors.Count > 0)
            {
                Console.WriteLine("ANCHORS");
                foreach (var a in report.Anchors)
                    PrintAlignPair(a, showDiff: false);
                Console.WriteLine();
            }

            // Mostrar variáveis (o que importa para extração).
            var varPairs = report.Alignments
                .Where(p => string.Equals(p.Kind, "variable", StringComparison.OrdinalIgnoreCase) || p.Kind.StartsWith("gap", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (showVariables && varPairs.Count > 0)
            {
                Console.WriteLine("VARIAVEIS (DMP)");
                foreach (var p in varPairs)
                    PrintAlignPair(p, showDiff: p.A != null && p.B != null);
                Console.WriteLine();
            }

            // Mostrar fixos (âncoras naturais).
            if (showFixed && report.FixedPairs.Count > 0)
            {
                Console.WriteLine("FIXOS");
                foreach (var p in report.FixedPairs)
                    PrintAlignPair(p, showDiff: false);
                Console.WriteLine();
            }

            PrintHumanAlignmentTop(report, top, outputMode);

            var topLimit = top <= 0 ? int.MaxValue : top;
            if (showVariables)
            {
                var topVar = report.Alignments
                    .Where(p => p.A != null && p.B != null)
                    .Where(p => string.Equals(p.Kind, "variable", StringComparison.OrdinalIgnoreCase) || p.Kind.StartsWith("gap", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.Score)
                    .Take(topLimit)
                    .Select(p => new[]
                    {
                        p.Kind,
                        ReportUtils.F(p.Score, 3),
                        p.A?.Text ?? "",
                        p.B?.Text ?? ""
                    })
                    .ToList();

                if (topVar.Count > 0)
                {
                    ReportUtils.WriteTable("TOP VARIAVEIS/DIFF", new[]
                    {
                        "kind", "score", ReportUtils.BlueLabel("A"), ReportUtils.OrangeLabel("B")
                    }, topVar);
                    Console.WriteLine();
                }
            }

            if (showFixed)
            {
                var topFixed = report.FixedPairs
                    .Where(p => p.A != null && p.B != null)
                    .OrderByDescending(p => p.Score)
                    .Take(topLimit)
                    .Select(p => new[]
                    {
                        p.Kind,
                        ReportUtils.F(p.Score, 3),
                        p.A?.Text ?? "",
                        p.B?.Text ?? ""
                    })
                    .ToList();

                if (topFixed.Count > 0)
                {
                    ReportUtils.WriteTable("TOP FIXOS", new[]
                    {
                        "kind", "score", ReportUtils.BlueLabel("A"), ReportUtils.OrangeLabel("B")
                    }, topFixed);
                    Console.WriteLine();
                }
                else if (!showVariables)
                {
                    var topAlign = report.Alignments
                        .Where(p => p.A != null && p.B != null)
                        .OrderByDescending(p => p.Score)
                        .Take(topLimit)
                        .Select(p => new[]
                        {
                            p.Kind,
                            ReportUtils.F(p.Score, 3),
                            p.A?.Text ?? "",
                            p.B?.Text ?? ""
                        })
                        .ToList();
                    ReportUtils.WriteTable("TOP ALIGNMENTS", new[]
                    {
                        "kind", "score", ReportUtils.BlueLabel("A"), ReportUtils.OrangeLabel("B")
                    }, topAlign);
                    Console.WriteLine();
                }
            }

            if (showVariables && !showFixed)
            {
                // no-op: já exibiu TOP VARIAVEIS.
            }
            else if (!showVariables && !showFixed)
            {
                var topFixed = report.Alignments
                    .Where(p => p.A != null && p.B != null)
                    .OrderByDescending(p => p.Score)
                    .Take(topLimit)
                    .Select(p => new[]
                    {
                        p.Kind,
                        ReportUtils.F(p.Score, 3),
                        p.A?.Text ?? "",
                        p.B?.Text ?? ""
                    })
                    .ToList();
                ReportUtils.WriteTable("TOP ALIGNMENTS", new[]
                {
                    "kind", "score", ReportUtils.BlueLabel("A"), ReportUtils.OrangeLabel("B")
                }, topFixed);
                Console.WriteLine();
            }
        }

        private static void PrintHumanAlignmentTop(ObjectsTextOpsDiff.AlignDebugReport report, int top, OutputMode outputMode)
        {
            var limit = top <= 0 ? 16 : Math.Max(8, Math.Min(40, top * 2));
            var indexed = report.Alignments
                .Select((pair, idx) => (pair, idx: idx + 1))
                .Where(x => IsSelectedByOutputMode(x.pair.Kind, outputMode))
                .Take(limit)
                .ToList();
            if (indexed.Count == 0)
                return;

            var rows = indexed
                .Select(x => new[]
                {
                    x.idx.ToString(CultureInfo.InvariantCulture),
                    x.pair.Kind ?? "",
                    ReportUtils.F(x.pair.Score, 3),
                    x.pair.A != null ? $"op{FormatOpRange(x.pair.A.StartOp, x.pair.A.EndOp)}" : "-",
                    x.pair.B != null ? $"op{FormatOpRange(x.pair.B.StartOp, x.pair.B.EndOp)}" : "-",
                    ShortInlineText(x.pair.A?.Text ?? "<gap>"),
                    ShortInlineText(x.pair.B?.Text ?? "<gap>")
                })
                .ToList();
            ReportUtils.WriteTable("ALINHAMENTO HUMANO (TOP)", new[] { "idx", "kind", "score", "A_op", "B_op", ReportUtils.BlueLabel("A"), ReportUtils.OrangeLabel("B") }, rows);
            Console.WriteLine();
        }

        private static string ShortInlineText(string? text, int max = 100)
        {
            var normalized = TextNormalization.NormalizeWhitespace(text ?? "");
            if (string.IsNullOrWhiteSpace(normalized))
                return "";
            if (normalized.Length <= max)
                return normalized;
            return normalized.Substring(0, Math.Max(0, max - 3)) + "...";
        }

        private static void PrintAlignPair(ObjectsTextOpsDiff.AlignDebugPair pair, bool showDiff)
        {
            var a = pair.A;
            var b = pair.B;
            var aRange = a != null ? FormatOpRange(a.StartOp, a.EndOp) : "-";
            var bRange = b != null ? FormatOpRange(b.StartOp, b.EndOp) : "-";
            var aText = a?.Text ?? "<gap>";
            var bText = b?.Text ?? "<gap>";
            Console.WriteLine($"[{pair.Kind}] score={pair.Score:F2}");
            Console.WriteLine($"  A op{aRange} \"{aText}\"");
            Console.WriteLine($"  B op{bRange} \"{bText}\"");
            if (showDiff)
                PrintFixedVarSegments(aText, bText);
        }

        private static void PrintAlignmentList(ObjectsTextOpsDiff.AlignDebugReport report, int top, bool showDiff)
        {
            var limit = top <= 0 ? int.MaxValue : top;
            var list = report.Alignments.Take(limit).ToList();
            if (list.Count == 0)
                return;

            Console.WriteLine("ALINHAMENTO");
            for (int i = 0; i < list.Count; i++)
            {
                var pair = list[i];
                var a = pair.A;
                var b = pair.B;
                var aIdx = a != null ? a.Index.ToString(CultureInfo.InvariantCulture) : "-";
                var bIdx = b != null ? b.Index.ToString(CultureInfo.InvariantCulture) : "-";
                var aRange = a != null ? FormatOpRange(a.StartOp, a.EndOp) : "-";
                var bRange = b != null ? FormatOpRange(b.StartOp, b.EndOp) : "-";
                Console.WriteLine($"[{i + 1:D3}] {pair.Kind} score={pair.Score:F2} A({aIdx}) op{aRange} | B({bIdx}) op{bRange}");
                Console.WriteLine($"  A: \"{a?.Text ?? "<gap>"}\"");
                Console.WriteLine($"  B: \"{b?.Text ?? "<gap>"}\"");
                if (showDiff && pair.Kind == "variable")
                    PrintFixedVarSegments(a?.Text ?? "", b?.Text ?? "");
            }
            Console.WriteLine();
        }

        private static string FormatOpRange(int startOp, int endOp)
        {
            if (startOp <= 0 && endOp <= 0) return "-";
            return startOp == endOp ? $"{startOp}" : $"{startOp}-{endOp}";
        }

        private static void PrintFixedVarSegments(string textA, string textB)
        {
            if (string.IsNullOrWhiteSpace(textA) && string.IsNullOrWhiteSpace(textB))
                return;
            var dmp = new diff_match_patch();
            var diffs = dmp.diff_main(textA ?? "", textB ?? "", false);
            dmp.diff_cleanupSemantic(diffs);

            var varA = new StringBuilder();
            var varB = new StringBuilder();
            const int minFixedLen = 2;

            void FlushVar()
            {
                if (varA.Length == 0 && varB.Length == 0)
                    return;
                var a = varA.ToString().Trim();
                var b = varB.ToString().Trim();
                if (a.Length == 0 && b.Length == 0)
                {
                    varA.Clear();
                    varB.Clear();
                    return;
                }
                Console.WriteLine($"    VAR A: \"{a}\"");
                Console.WriteLine($"    VAR B: \"{b}\"");
                varA.Clear();
                varB.Clear();
            }

            foreach (var diff in diffs)
            {
                if (diff.operation == Operation.EQUAL)
                {
                    FlushVar();
                    var fixedText = diff.text.Trim();
                    if (fixedText.Length >= minFixedLen)
                        Console.WriteLine($"    FIXO: \"{fixedText}\"");
                    continue;
                }
                if (diff.operation == Operation.DELETE)
                {
                    varA.Append(diff.text);
                    continue;
                }
                if (diff.operation == Operation.INSERT)
                    varB.Append(diff.text);
            }
            FlushVar();
        }
    }
}
