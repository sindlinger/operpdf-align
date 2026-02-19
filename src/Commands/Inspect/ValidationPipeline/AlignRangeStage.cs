using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Obj.Align;
using Obj.DocDetector;
using Obj.FrontBack;
using Obj.Utils;
using Obj.ValidatorModule;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.Commands
{
    internal static class ObjectsAlignRange
    {
        private const int DefaultBackoff = 2;
        private static readonly string DefaultOutDir = Path.Combine("outputs", "align_ranges");

        public static void Execute(string[] args)
        {
            if (!AllowDirect(args))
            {
                Console.WriteLine("frontback direto desativado. Use fluxo de alto nivel (front/back continua interno).");
                Console.WriteLine("Para liberar temporariamente: --force ou OBJ_ALLOW_ALIGNRANGE=1");
                return;
            }
            if (!ParseOptions(args, out var inputs, out var opFilter, out var contentsPage, out var outDir, out var mapPath, out var skipMapFields, out var useFront, out var useBack, out var side, out var backPageAOverride, out var backPageBOverride, out var docKey, out var mapKey, out var forceBackPageNext, out var requireMarker))
                return;

            if (string.IsNullOrWhiteSpace(mapPath) && !string.IsNullOrWhiteSpace(mapKey))
                mapPath = mapKey;

            if (inputs.Count == 1 && !string.IsNullOrWhiteSpace(docKey))
            {
                var modelPath = ResolveModelPathForDoc(docKey);
                if (string.IsNullOrWhiteSpace(modelPath))
                {
                    Console.WriteLine($"Modelo nao encontrado para: {docKey}");
                    return;
                }
                inputs.Insert(0, modelPath);
            }

            if (inputs.Count < 2)
            {
                ShowHelp();
                return;
            }

            var aPath = inputs[0];
            var bPath = inputs[1];

            if (!File.Exists(aPath))
            {
                Console.WriteLine($"PDF nao encontrado: {aPath}");
                return;
            }

            if (!File.Exists(bPath))
            {
                Console.WriteLine($"PDF nao encontrado: {bPath}");
                return;
            }

            if (opFilter.Count == 0)
            {
                opFilter.Add("Tj");
                opFilter.Add("TJ");
            }

            var pageA = contentsPage > 0 ? contentsPage : ResolveDocPage(aPath);
            var pageB = contentsPage > 0 ? contentsPage : ResolveDocPage(bPath);
            if (pageA <= 0 || pageB <= 0)
            {
                Console.WriteLine("Pagina do despacho nao encontrada (detector falhou).");
                return;
            }

            if (forceBackPageNext && backPageAOverride <= 0 && backPageBOverride <= 0)
            {
                backPageAOverride = pageA + 1;
                backPageBOverride = pageB + 1;
            }

            if (string.IsNullOrWhiteSpace(side))
                side = !string.IsNullOrWhiteSpace(docKey) ? "b" : "both";

            var resolved = FrontBackResolver.Resolve(new FrontBackRequest
            {
                PdfA = aPath,
                PdfB = bPath,
                PageA = pageA,
                PageB = pageB,
                OpFilter = opFilter,
                Backoff = DefaultBackoff,
                FrontRequireMarker = requireMarker,
                BackPageAOverride = backPageAOverride,
                BackPageBOverride = backPageBOverride
            });
            if (resolved.Errors.Count > 0)
            {
                var fatal = resolved.Errors
                    .Where(e => !e.StartsWith("back_page", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (fatal.Count > 0)
                {
                    foreach (var err in fatal)
                        Console.WriteLine(err);
                    return;
                }
            }
            if (resolved.AlignRange == null)
                return;

            if (string.IsNullOrWhiteSpace(outDir))
                outDir = DefaultOutDir;

            Directory.CreateDirectory(outDir);

            var nameA = Path.GetFileName(aPath);
            var nameB = Path.GetFileName(bPath);

            var frontObjA = resolved.FrontA?.Obj ?? 0;
            var frontObjB = resolved.FrontB?.Obj ?? 0;
            var backObjA = resolved.BackBodyA?.Obj ?? 0;
            var backObjB = resolved.BackBodyB?.Obj ?? 0;
            var sigObjA = resolved.BackSignatureA?.Obj ?? 0;
            var sigObjB = resolved.BackSignatureB?.Obj ?? 0;
            var sigAValue = resolved.BackSignatureA?.Obj > 0
                ? ObjectsTextOpsDiff.ComputeFullRangeForSelection(aPath, new ObjectsTextOpsDiff.PageObjSelection
                {
                    Page = resolved.BackSignatureA.Page,
                    Obj = resolved.BackSignatureA.Obj
                }, opFilter)
                : null;
            var sigBValue = resolved.BackSignatureB?.Obj > 0
                ? ObjectsTextOpsDiff.ComputeFullRangeForSelection(bPath, new ObjectsTextOpsDiff.PageObjSelection
                {
                    Page = resolved.BackSignatureB.Page,
                    Obj = resolved.BackSignatureB.Obj
                }, opFilter)
                : null;

            AlignRangeValue? sigAOut = null;
            if (sigAValue != null)
                sigAOut = new AlignRangeValue
                {
                    Page = sigAValue.Page,
                    StartOp = sigAValue.StartOp,
                    EndOp = sigAValue.EndOp,
                    ValueFull = sigAValue.ValueFull
                };

            AlignRangeValue? sigBOut = null;
            if (sigBValue != null)
                sigBOut = new AlignRangeValue
                {
                    Page = sigBValue.Page,
                    StartOp = sigBValue.StartOp,
                    EndOp = sigBValue.EndOp,
                    ValueFull = sigBValue.ValueFull
                };

            var output = FormatOutput(
                resolved.AlignRange,
                nameA,
                nameB,
                aPath,
                bPath,
                frontObjA,
                frontObjB,
                backObjA,
                backObjB,
                sigObjA,
                sigObjB,
                sigAOut,
                sigBOut);
            Console.WriteLine(output);

            var outFile = Path.Combine(outDir,
                $"{Path.GetFileNameWithoutExtension(aPath)}__{Path.GetFileNameWithoutExtension(bPath)}.txt");
            File.WriteAllText(outFile, output);
            Console.WriteLine($"Arquivo salvo: {outFile}");

            WriteTextOpsAlignDebug(outDir, aPath, bPath, resolved);

            if (!skipMapFields)
                RunMapFieldsIfPossible(outFile, mapPath, useFront, useBack, side);
        }

        private static bool ParseOptions(string[] args, out List<string> inputs, out HashSet<string> opFilter, out int contentsPage, out string outDir, out string mapPath, out bool skipMapFields, out bool useFront, out bool useBack, out string side, out int backPageAOverride, out int backPageBOverride, out string docKey, out string mapKey, out bool forceBackPageNext, out bool requireMarker)
        {
            inputs = new List<string>();
            opFilter = new HashSet<string>(StringComparer.Ordinal);
            contentsPage = 0;
            outDir = "";
            mapPath = "";
            skipMapFields = false;
            useFront = false;
            useBack = false;
            side = "";
            backPageAOverride = 0;
            backPageBOverride = 0;
            docKey = "";
            mapKey = "";
            forceBackPageNext = false;
            requireMarker = true;
            var opSpecified = false;

            var defaults = ObjectsTextOpsDiff.LoadObjDefaults();
            if (defaults != null)
            {
                if (defaults.Ops != null && defaults.Ops.Count > 0)
                {
                    foreach (var op in defaults.Ops)
                    {
                        if (!string.IsNullOrWhiteSpace(op))
                            opFilter.Add(op.Trim());
                    }
                }
                if (defaults.Page.HasValue && defaults.Page.Value > 0)
                    contentsPage = defaults.Page.Value;
            }

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == "--help" || arg == "-h")
                {
                    ShowHelp();
                    return false;
                }

                if (string.Equals(arg, "--inputs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    foreach (var raw in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        inputs.Add(raw.Trim());
                    continue;
                }

                if ((string.Equals(arg, "--op", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--ops", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    if (!opSpecified)
                    {
                        opFilter.Clear();
                        opSpecified = true;
                    }
                    foreach (var raw in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        opFilter.Add(raw.Trim());
                    continue;
                }

                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var page))
                        contentsPage = page;
                    continue;
                }

                if ((string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--out-dir", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    outDir = args[++i];
                    continue;
                }
                if ((string.Equals(arg, "--map", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--doc", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    mapPath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--no-mapfields", StringComparison.OrdinalIgnoreCase))
                {
                    skipMapFields = true;
                    continue;
                }
                if (string.Equals(arg, "--front", StringComparison.OrdinalIgnoreCase))
                {
                    useFront = true;
                    continue;
                }
                if (string.Equals(arg, "--back", StringComparison.OrdinalIgnoreCase))
                {
                    useBack = true;
                    continue;
                }
                if (string.Equals(arg, "--both", StringComparison.OrdinalIgnoreCase))
                {
                    useFront = true;
                    useBack = true;
                    continue;
                }
                if (string.Equals(arg, "--side", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    side = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--back-page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var backPage))
                    {
                        backPageAOverride = backPage;
                        backPageBOverride = backPage;
                    }
                    continue;
                }
                if (string.Equals(arg, "--back-page-a", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var backPage))
                        backPageAOverride = backPage;
                    continue;
                }
                if (string.Equals(arg, "--back-page-b", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var backPage))
                        backPageBOverride = backPage;
                    continue;
                }

                if (string.Equals(arg, "--no-marker", StringComparison.OrdinalIgnoreCase))
                {
                    requireMarker = false;
                    continue;
                }

                if (string.Equals(arg, "--contents", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!arg.StartsWith("-", StringComparison.Ordinal))
                {
                    if (TryParseDocHint(arg, out var parsedDoc, out var parsedMap))
                    {
                        if (string.IsNullOrWhiteSpace(docKey))
                            docKey = parsedDoc;
                        if (string.IsNullOrWhiteSpace(mapKey))
                            mapKey = parsedMap;
                        forceBackPageNext = true;
                        continue;
                    }
                    inputs.Add(arg);
                }
            }

            if (string.IsNullOrWhiteSpace(docKey))
            {
                var envDoc = Environment.GetEnvironmentVariable("OBJ_DEFAULT_DOC");
                if (TryParseDocHint(envDoc, out var parsedDoc, out var parsedMap))
                {
                    docKey = parsedDoc;
                    mapKey = parsedMap;
                    forceBackPageNext = true;
                }
            }

            if (string.IsNullOrWhiteSpace(mapPath))
            {
                var envMap = Environment.GetEnvironmentVariable("OBJ_DEFAULT_MAP");
                if (!string.IsNullOrWhiteSpace(envMap))
                    mapPath = envMap;
            }

            if (string.IsNullOrWhiteSpace(side))
            {
                var envSide = Environment.GetEnvironmentVariable("OBJ_DEFAULT_SIDE");
                if (!string.IsNullOrWhiteSpace(envSide))
                    side = envSide;
            }

            if (backPageAOverride <= 0 && backPageBOverride <= 0)
            {
                var envBack = Environment.GetEnvironmentVariable("OBJ_DEFAULT_BACK_PAGE");
                if (int.TryParse(envBack, NumberStyles.Any, CultureInfo.InvariantCulture, out var backPage))
                {
                    backPageAOverride = backPage;
                    backPageBOverride = backPage;
                }
            }

            return true;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("operpdf inspect frontback --contents --op Tj,TJ <pdfA> <pdfB>");
            Console.WriteLine("  frontback despacho <pdfB>   (atalho: usa modelo + mapa do despacho)");
            Console.WriteLine("  frontback requerimento <pdfB>");
            Console.WriteLine("  frontback certidao <pdfB>");
            Console.WriteLine("  --inputs a.pdf,b.pdf   (opcional)");
            Console.WriteLine("  --page N               (opcional, força pagina do despacho)");
            Console.WriteLine("  --out <dir>            (opcional, default outputs/align_ranges)");
            Console.WriteLine("  --map <doc|map.yml>    (opcional, mapa para mapfields)");
            Console.WriteLine("  --no-mapfields         (opcional, nao roda mapfields)");
            Console.WriteLine("  --front|--back|--both  (opcional, bandas para mapfields)");
            Console.WriteLine("  --side a|b|both        (opcional, lado do mapa; default both)");
            Console.WriteLine("  --back-page N          (opcional, força pagina do back_tail)");
            Console.WriteLine("  --back-page-a N        (opcional)");
            Console.WriteLine("  --back-page-b N        (opcional)");
            Console.WriteLine("  --no-marker           (opcional, nao exige marcador no front)");
            Console.WriteLine("  --force               (opcional, habilita execucao direta do frontback)");
        }

        private static bool AllowDirect(string[] args)
        {
            foreach (var arg in args)
            {
                if (string.Equals(arg, "--force", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            var env = Environment.GetEnvironmentVariable("OBJ_ALLOW_ALIGNRANGE");
            if (string.IsNullOrWhiteSpace(env)) return false;
            env = env.Trim();
            return env == "1" || env.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static int ResolveDocPage(string pdfPath)
        {
            var hit = BookmarkDetector.Detect(pdfPath);
            if (hit.Found)
                return hit.Page;
            hit = ContentsPrefixDetector.Detect(pdfPath);
            if (hit.Found)
                return hit.Page;
            hit = HeaderLabelDetector.Detect(pdfPath);
            if (hit.Found)
                return hit.Page;
            hit = LargestContentsDetector.Detect(pdfPath);
            if (hit.Found)
                return hit.Page;
            return 0;
        }

        private static DetectionHit PickStream(string pdfPath, int page, bool requireMarker)
        {
            return ContentsStreamPicker.Pick(new StreamPickRequest
            {
                PdfPath = pdfPath,
                Page = page,
                RequireMarker = requireMarker
            });
        }

        private static void WriteTextOpsAlignDebug(string outDir, string aPath, string bPath, FrontBackResult resolved)
        {
            try
            {
                if (resolved?.FrontA == null || resolved.FrontB == null)
                    return;

                var defaults = ObjectsTextOpsDiff.LoadObjDefaults();
                var opFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (defaults?.Ops != null)
                {
                    foreach (var op in defaults.Ops)
                        if (!string.IsNullOrWhiteSpace(op))
                            opFilter.Add(op.Trim());
                }
                if (opFilter.Count == 0)
                {
                    opFilter.Add("Tj");
                    opFilter.Add("TJ");
                }

                var front = ObjectsTextOpsDiff.ComputeAlignDebugForSelection(
                    aPath,
                    bPath,
                    new ObjectsTextOpsDiff.PageObjSelection { Page = resolved.FrontA.Page, Obj = resolved.FrontA.Obj },
                    new ObjectsTextOpsDiff.PageObjSelection { Page = resolved.FrontB.Page, Obj = resolved.FrontB.Obj },
                    opFilter,
                    DefaultBackoff,
                    "front_head");

                ObjectsTextOpsDiff.AlignDebugReport? back = null;
                if (resolved.BackBodyA != null && resolved.BackBodyB != null && resolved.BackBodyA.Obj > 0 && resolved.BackBodyB.Obj > 0)
                {
                    back = ObjectsTextOpsDiff.ComputeAlignDebugForSelection(
                        aPath,
                        bPath,
                        new ObjectsTextOpsDiff.PageObjSelection { Page = resolved.BackBodyA.Page, Obj = resolved.BackBodyA.Obj },
                        new ObjectsTextOpsDiff.PageObjSelection { Page = resolved.BackBodyB.Page, Obj = resolved.BackBodyB.Obj },
                        opFilter,
                        DefaultBackoff,
                        "back_tail");
                }

                var payload = new { front, back };
                var json = System.Text.Json.JsonSerializer.Serialize(payload, JsonUtils.Indented);
                var baseName = $"{Path.GetFileNameWithoutExtension(aPath)}__{Path.GetFileNameWithoutExtension(bPath)}";
                var outPath = Path.Combine(outDir, $"{baseName}__textops_align.json");
                File.WriteAllText(outPath, json);
                Console.WriteLine($"TextOpsAlign salvo: {outPath}");
            }
            catch
            {
                // debug should not break alignrange
            }
        }

        private static void RunMapFieldsIfPossible(string alignrangePath, string mapPath, bool useFront, bool useBack, string side)
        {
            if (string.IsNullOrWhiteSpace(alignrangePath) || !File.Exists(alignrangePath))
                return;

            var doc = mapPath;
            if (string.IsNullOrWhiteSpace(doc))
            {
                var defaults = ObjectsTextOpsDiff.LoadObjDefaults();
                doc = defaults?.Doc;
            }
            if (string.IsNullOrWhiteSpace(doc))
                doc = "tjpb_despacho";

            try
            {
                doc = ResolveMapPathForMapFields(doc);
                Console.WriteLine($"[mapfields] usando mapa: {doc}");
                var args = new List<string>
                {
                    "--alignrange", alignrangePath,
                    "--map", doc
                };
                if (useFront || useBack)
                {
                    if (useFront) args.Add("--front");
                    if (useBack) args.Add("--back");
                }
                else
                {
                    args.Add("--both");
                }
                if (!string.IsNullOrWhiteSpace(side))
                {
                    args.Add("--side");
                    args.Add(side);
                }
                ObjectsMapFields.Execute(args.ToArray());
            }
            catch (Exception ex)
            {
                Console.WriteLine("[mapfields] erro: " + ex.Message);
            }
        }

        private static string FormatOutput(
            FrontBackAlignRange result,
            string nameA,
            string nameB,
            string pathA,
            string pathB,
            int frontObjA,
            int frontObjB,
            int backObjA,
            int backObjB,
            int sigObjA,
            int sigObjB,
            AlignRangeValue? sigA,
            AlignRangeValue? sigB)
        {
            var sb = new StringBuilder();
            AppendSection(sb, "front_head", result.FrontA, result.FrontB, nameA, nameB, pathA, pathB, frontObjA, frontObjB);
            AppendSection(sb, "back_tail", result.BackA, result.BackB, nameA, nameB, pathA, pathB, backObjA, backObjB);
            if (sigA != null || sigB != null)
            {
                AppendSection(
                    sb,
                    "back_signature",
                    sigA ?? new AlignRangeValue(),
                    sigB ?? new AlignRangeValue(),
                    nameA,
                    nameB,
                    pathA,
                    pathB,
                    sigObjA,
                    sigObjB);
            }
            return sb.ToString();
        }

        private static void AppendSection(
            StringBuilder sb,
            string label,
            AlignRangeValue a,
            AlignRangeValue b,
            string nameA,
            string nameB,
            string pathA,
            string pathB,
            int objA,
            int objB)
        {
            sb.AppendLine($"{label}:");
            AppendValue(sb, "a", nameA, pathA, objA, a);
            AppendValue(sb, "b", nameB, pathB, objB, b);
        }

        private static void AppendValue(StringBuilder sb, string suffix, string name, string path, int obj, AlignRangeValue value)
        {
            sb.AppendLine($"  pdf_{suffix}: {name}");
            sb.AppendLine($"  pdf_{suffix}_path: \"{EscapeValue(path)}\"");
            sb.AppendLine($"  obj_{suffix}: {obj}");
            sb.AppendLine($"  op_range_{suffix}: {FormatOpRange(value.StartOp, value.EndOp)}");
            sb.AppendLine($"  value_full_{suffix}: \"{EscapeValue(value.ValueFull)}\"");
        }

        private static string FormatOpRange(int start, int end)
        {
            if (start <= 0 || end <= 0) return "op0";
            if (end < start) (start, end) = (end, start);
            return start == end ? $"op{start}" : $"op{start}-{end}";
        }

        private static string EscapeValue(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var normalized = value.Replace('\r', ' ').Replace('\n', ' ');
            // YAML double-quoted strings require backslashes to be escaped.
            normalized = normalized.Replace("\\", "\\\\");
            return normalized.Replace("\"", "\\\"");
        }

        private static string ResolveMapPathForMapFields(string mapKey)
        {
            if (string.IsNullOrWhiteSpace(mapKey)) return mapKey;
            if (File.Exists(mapKey)) return mapKey;

            var regDirect = Obj.Utils.PatternRegistry.FindFile("alignrange_fields", mapKey);
            if (!string.IsNullOrWhiteSpace(regDirect))
                return regDirect;
            if (!Path.HasExtension(mapKey))
            {
                var regYml = Obj.Utils.PatternRegistry.FindFile("alignrange_fields", mapKey + ".yml");
                if (!string.IsNullOrWhiteSpace(regYml))
                    return regYml;
                var regYaml = Obj.Utils.PatternRegistry.FindFile("alignrange_fields", mapKey + ".yaml");
                if (!string.IsNullOrWhiteSpace(regYaml))
                    return regYaml;
            }

            var repoRoot = FindRepoRoot();
            if (!string.IsNullOrWhiteSpace(repoRoot))
            {
                var byKey = Path.Combine(repoRoot, "configs", "alignrange_fields", mapKey);
                if (File.Exists(byKey)) return byKey;
                var byKeyYml = Path.Combine(repoRoot, "configs", "alignrange_fields", mapKey + ".yml");
                if (File.Exists(byKeyYml)) return byKeyYml;
                var byKeyYaml = Path.Combine(repoRoot, "configs", "alignrange_fields", mapKey + ".yaml");
                if (File.Exists(byKeyYaml)) return byKeyYaml;
            }

            return mapKey;
        }

        private static bool TryParseDocHint(string? raw, out string docKey, out string mapKey)
        {
            docKey = "";
            mapKey = "";
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var resolved = DocumentValidationRules.ResolveDocKeyForDetection(raw);
            if (string.IsNullOrWhiteSpace(resolved))
                return false;

            docKey = resolved;
            mapKey = resolved switch
            {
                "despacho" => "tjpb_despacho",
                "requerimento_honorarios" => "tjpb_requerimento",
                "certidao_conselho" => "tjpb_certidao",
                _ => resolved
            };
            return true;
        }

        private static string ResolveModelPathForDoc(string docKey)
        {
            if (string.IsNullOrWhiteSpace(docKey)) return "";
            var envKey = docKey switch
            {
                "despacho" => "OBJ_MODEL_DESPACHO",
                "requerimento_honorarios" => "OBJ_MODEL_REQUERIMENTO",
                "certidao_conselho" => "OBJ_MODEL_CERTIDAO",
                _ => ""
            };
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                var envPath = Environment.GetEnvironmentVariable(envKey);
                if (!string.IsNullOrWhiteSpace(envPath))
                {
                    var p = ResolveModelPath(envPath);
                    if (!string.IsNullOrWhiteSpace(p))
                        return p;
                }
            }

            var configPath = ResolveModelsConfigPath();
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                return "";

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var cfg = deserializer.Deserialize<ObjModelsConfig>(File.ReadAllText(configPath));
                if (cfg?.Models == null) return "";
                if (!cfg.Models.TryGetValue(docKey, out var rawPath) || string.IsNullOrWhiteSpace(rawPath))
                    return "";
                return ResolveModelPath(rawPath);
            }
            catch
            {
                return "";
            }
        }

        private static string ResolveModelsConfigPath()
        {
            var reg = Obj.Utils.PatternRegistry.FindFile("models", "obj_models.yml");
            if (!string.IsNullOrWhiteSpace(reg)) return reg;
            var regYaml = Obj.Utils.PatternRegistry.FindFile("models", "obj_models.yaml");
            if (!string.IsNullOrWhiteSpace(regYaml)) return regYaml;
            return "";
        }

        private static string ResolveModelPath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return "";
            if (File.Exists(rawPath)) return rawPath;
            var repoRoot = FindRepoRoot();
            if (!string.IsNullOrWhiteSpace(repoRoot))
            {
                var full = Path.Combine(repoRoot, rawPath);
                if (File.Exists(full)) return full;
            }
            return rawPath;
        }

        private static string? FindRepoRoot()
        {
            var baseDir = Obj.Utils.PatternRegistry.ResolveBaseDir();
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var probe = Path.Combine(dir.FullName, "modules", "PatternModules", "registry");
                if (Directory.Exists(probe))
                    return dir.FullName;
                dir = dir.Parent;
            }

            dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var probe = Path.Combine(dir.FullName, "modules", "PatternModules", "registry");
                if (Directory.Exists(probe))
                    return dir.FullName;
                dir = dir.Parent;
            }

            return null;
        }

        private sealed class ObjModelsConfig
        {
            public Dictionary<string, string> Models { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
