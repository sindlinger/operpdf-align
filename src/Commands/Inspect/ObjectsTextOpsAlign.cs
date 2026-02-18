using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DiffMatchPatch;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Obj.Align;
using Obj.DocDetector;
using Obj.Honorarios;
using Obj.Utils;
using Obj.ValidatorModule;

namespace Obj.Commands
{
    internal static class ObjectsTextOpsAlign
    {
        private const string AnsiReset = "\u001b[0m";
        private const string AnsiInfo = "\u001b[38;5;81m";
        private const string AnsiOk = "\u001b[38;5;46m";
        private const string AnsiWarn = "\u001b[38;5;214m";
        private const string AnsiErr = "\u001b[38;5;196m";

        internal enum OutputMode
        {
            All,
            VariablesOnly,
            FixedOnly
        }

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
                Console.WriteLine($"  {Colorize(key + ":", AnsiWarn)} {value}");
            if (!string.IsNullOrWhiteSpace(nextStep))
                Console.WriteLine($"  {Colorize("usa no próximo:", AnsiWarn)} {nextStep}");
            Console.WriteLine();
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
                        Value = before ?? "",
                        Source = "",
                        OpRange = "",
                        Obj = 0,
                        Status = string.IsNullOrWhiteSpace(after) ? "NOT_FOUND" : "OK",
                        BBox = null
                    };
                }

                var tag = string.IsNullOrWhiteSpace(before) && !string.IsNullOrWhiteSpace(after)
                    ? $"derived:{moduleTag}"
                    : $"adjusted:{moduleTag}";
                if (string.IsNullOrWhiteSpace(meta.Source))
                    meta.Source = tag;
                else if (!meta.Source.Contains(moduleTag, StringComparison.OrdinalIgnoreCase))
                    meta.Source = meta.Source + "|" + tag;

                meta.Status = string.IsNullOrWhiteSpace(after) ? "NOT_FOUND" : "OK";
                fields[key] = meta;
            }
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
                            OpRange = "",
                            Obj = 0,
                            Status = "NOT_FOUND",
                            BBox = null
                        };
                    }
                    else
                    {
                        var tag = "policy:validator:strict_found";
                        if (string.IsNullOrWhiteSpace(fieldMeta.Source))
                            fieldMeta.Source = tag;
                        else if (!fieldMeta.Source.Contains(tag, StringComparison.OrdinalIgnoreCase))
                            fieldMeta.Source = fieldMeta.Source + "|" + tag;
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
                    if (string.IsNullOrWhiteSpace(keepMeta.Source))
                        keepMeta.Source = tag;
                    else if (!keepMeta.Source.Contains(tag, StringComparison.OrdinalIgnoreCase))
                        keepMeta.Source = keepMeta.Source + "|" + tag;
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
                    "front_head");

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
            Console.OutputEncoding = Encoding.UTF8;
            PrintStage("iniciando o modo de detecção");
            if (!ParseOptions(args, out var inputs, out var pageA, out var pageB, out var objA, out var objB, out var opFilter, out var backoff, out var outPath, out var outSpecified, out var top, out var minSim, out var band, out var minLenRatio, out var lenPenalty, out var anchorMinSim, out var anchorMinLenRatio, out var gapPenalty, out var showAlign, out var alignTop, out var pageAUser, out var pageBUser, out var objAUser, out var objBUser, out var docKey, out var useBack, out var sideSpecified))
            {
                ShowHelp();
                return;
            }

            if (inputs.Count < 2)
            {
                ShowHelp();
                return;
            }

            var aPath = inputs[0].Trim().Trim('"');
            if (!File.Exists(aPath))
            {
                Console.WriteLine($"PDF nao encontrado: {aPath}");
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

            int PickPageOrDefault(string path)
            {
                var p = ResolvePage(path, trace: true);
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
                var p1 = ResolvePage(pdfPath, trace: true);
                if (p1 <= 0)
                    return false;

                var o1 = PickObjForPage(pdfPath, p1);
                if (o1 <= 0)
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

                var p2 = p1 + 1;
                var o2 = 0;
                if (totalPages > 0 && p2 <= totalPages)
                    o2 = PickObjForPage(pdfPath, p2);

                if (back && p2 > 0 && o2 > 0)
                {
                    page = p2;
                    obj = o2;
                    reason = "despacho_back_auto";
                    return true;
                }

                page = p1;
                obj = o1;
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

            ResolveSelection(aPath, pageAUser, objAUser, pageA, objA, out pageA, out objA, out var sourceA);
            if (sourceA.StartsWith("despacho", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"Despacho route A: p{pageA} o{objA} ({sourceA})");

            var reports = new List<ObjectsTextOpsDiff.AlignDebugReport>();
            foreach (var rawB in inputs.Skip(1))
            {
                var bPath = rawB.Trim().Trim('"');
                if (!File.Exists(bPath))
                {
                    Console.WriteLine($"PDF nao encontrado: {bPath}");
                    return;
                }

                int localPageA = pageA;
                int localObjA = objA;
                ResolveSelection(bPath, pageBUser, objBUser, pageB, objB, out var localPageB, out var localObjB, out var sourceB);
                if (sourceB.StartsWith("despacho", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"Despacho route B: p{localPageB} o{localObjB} ({sourceB})");

                var sideLabel = useBack ? "back_tail" : "front_head";
                if (!ReturnUtils.IsEnabled())
                {
                    PrintPipelineStep(
                        "passo 1/4 - detecção e seleção de objetos",
                        "passo 2/4 - alinhamento",
                        ("modulo", "Obj.DocDetector + ObjectsFindDespacho + ContentsStreamPicker"),
                        ("pdf_a", Path.GetFileName(aPath)),
                        ("pdf_b", Path.GetFileName(bPath)),
                        ("sel_a", $"page={localPageA} obj={localObjA} source={sourceA}"),
                        ("sel_b", $"page={localPageB} obj={localObjB} source={sourceB}"),
                        ("band", sideLabel),
                        ("ops", string.Join(",", opFilter.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))),
                        ("params", $"backoff={backoff} minSim={minSim.ToString("0.##", CultureInfo.InvariantCulture)} band={band} minLen={minLenRatio.ToString("0.##", CultureInfo.InvariantCulture)}")
                    );
                }
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

                ObjectsTextOpsDiff.AlignDebugReport? backReport = null;
                var autoDualDespacho = DocumentValidationRules.IsDocMatch(docKey, "despacho") && !useBack && !sideSpecified;
                if (autoDualDespacho)
                {
                    var hasBackA = TryResolveDespachoSelection(aPath, docKey, true, out var backPageA, out var backObjA, out var backSourceA);
                    var hasBackB = TryResolveDespachoSelection(bPath, docKey, true, out var backPageB, out var backObjB, out var backSourceB);
                    var backAResolved = hasBackA && backPageA > 0 && backObjA > 0 && backSourceA.Contains("back", StringComparison.OrdinalIgnoreCase);
                    var backBResolved = hasBackB && backPageB > 0 && backObjB > 0 && backSourceB.Contains("back", StringComparison.OrdinalIgnoreCase);
                    if (backAResolved && backBResolved)
                    {
                        if (!ReturnUtils.IsEnabled())
                        {
                            PrintPipelineStep(
                                "passo 2b/4 - alinhamento da segunda página (back_tail)",
                                "passo 3/4 - extração combinada front_head + back_tail",
                                ("modulo", "Obj.Align.ObjectsTextOpsDiff"),
                                ("sel_back_a", $"page={backPageA} obj={backObjA} source={backSourceA}"),
                                ("sel_back_b", $"page={backPageB} obj={backObjB} source={backSourceB}")
                            );
                        }

                        backReport = ObjectsTextOpsDiff.ComputeAlignDebugForSelection(
                            aPath,
                            bPath,
                            new ObjectsTextOpsDiff.PageObjSelection { Page = backPageA, Obj = backObjA },
                            new ObjectsTextOpsDiff.PageObjSelection { Page = backPageB, Obj = backObjB },
                            opFilter,
                            backoff,
                            "back_tail",
                            minSim,
                            band,
                            minLenRatio,
                            lenPenalty,
                            anchorMinSim,
                            anchorMinLenRatio,
                            gapPenalty);

                        if (!ReturnUtils.IsEnabled())
                        {
                            if (backReport != null)
                            {
                                var backRangeA = backReport.RangeA?.HasValue == true ? $"op{backReport.RangeA.StartOp}-op{backReport.RangeA.EndOp}" : "(vazio)";
                                var backRangeB = backReport.RangeB?.HasValue == true ? $"op{backReport.RangeB.StartOp}-op{backReport.RangeB.EndOp}" : "(vazio)";
                                PrintPipelineStep(
                                    "passo 2c/4 - saída do alinhamento back_tail",
                                    "passo 3/4 - extração combinada front_head + back_tail",
                                    ("pairs_back", backReport.Alignments.Count.ToString(CultureInfo.InvariantCulture)),
                                    ("range_back_a", backRangeA),
                                    ("range_back_b", backRangeB)
                                );
                            }
                            else
                            {
                                PrintPipelineStep(
                                    "passo 2c/4 - saída do alinhamento back_tail",
                                    "passo 3/4 - extração apenas front_head",
                                    ("resultado", "falhou ao alinhar back_tail")
                                );
                            }
                        }
                    }
                    else if (!ReturnUtils.IsEnabled())
                    {
                        PrintPipelineStep(
                            "passo 2b/4 - alinhamento da segunda página (back_tail)",
                            "passo 3/4 - extração apenas front_head",
                            ("resultado", "segunda página não resolvida para A e/ou B")
                        );
                    }
                }

                if (!ReturnUtils.IsEnabled())
                {
                    var variableCount = report.Alignments.Count(p => string.Equals(p.Kind, "variable", StringComparison.OrdinalIgnoreCase));
                    var gapCount = report.Alignments.Count(p => p.Kind.StartsWith("gap", StringComparison.OrdinalIgnoreCase));
                    var rangeA = report.RangeA?.HasValue == true ? $"op{report.RangeA.StartOp}-op{report.RangeA.EndOp}" : "(vazio)";
                    var rangeB = report.RangeB?.HasValue == true ? $"op{report.RangeB.StartOp}-op{report.RangeB.EndOp}" : "(vazio)";
                    PrintPipelineStep(
                        "passo 2/4 - saída do alinhamento",
                        "passo 3/4 - extração (usa op_range + value_full)",
                        ("modulo", "Obj.Align.ObjectsTextOpsDiff"),
                        ("anchors", report.Anchors.Count.ToString(CultureInfo.InvariantCulture)),
                        ("pairs", report.Alignments.Count.ToString(CultureInfo.InvariantCulture)),
                        ("fixed", report.FixedPairs.Count.ToString(CultureInfo.InvariantCulture)),
                        ("variable", variableCount.ToString(CultureInfo.InvariantCulture)),
                        ("gaps", gapCount.ToString(CultureInfo.InvariantCulture)),
                        ("range_a", rangeA),
                        ("range_b", rangeB)
                    );
                }

                if (inputs.Count == 2 && !ReturnUtils.IsEnabled())
                {
                    PrintHumanSummary(report, top, outputMode);
                    if (showAlign)
                        PrintAlignmentList(report, alignTop, showDiff: true);
                }

                PrintStage("iniciando o modo de extração");
                report.Extraction = BuildExtractionPayload(report, backReport, aPath, bPath, docKey, verbose: !ReturnUtils.IsEnabled());
                reports.Add(report);

                if (inputs.Count == 2)
                {
                    var jsonOptions = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    var json = JsonSerializer.Serialize(report, jsonOptions);
                    var emitJsonToStdout = ReturnUtils.IsEnabled();
                    if (emitJsonToStdout)
                        Console.WriteLine(json);

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
                            var baseA = Path.GetFileNameWithoutExtension(aPath);
                            var baseB = Path.GetFileNameWithoutExtension(bPath);
                            outPath = Path.Combine("outputs", "align_ranges", $"{baseA}__{baseB}__textops_align.json");
                            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                            File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                            Console.WriteLine("Arquivo salvo: " + outPath);
                        }
                    }

                    if (report.Extraction != null && !ReturnUtils.IsEnabled())
                    {
                        var baseA = Path.GetFileNameWithoutExtension(aPath);
                        var baseB = Path.GetFileNameWithoutExtension(bPath);
                        var extractionOutPath = Path.Combine("outputs", "extract", $"{baseA}__{baseB}__textops_extract.json");
                        Directory.CreateDirectory(Path.GetDirectoryName(extractionOutPath) ?? ".");
                        var extractionJson = JsonSerializer.Serialize(report.Extraction, jsonOptions);
                        File.WriteAllText(extractionOutPath, extractionJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                        Console.WriteLine("Arquivo extração salvo: " + extractionOutPath);
                    }

                    if (!ReturnUtils.IsEnabled())
                        PrintPipelineStep("passo 4/4 - saída e resumo", "fim", ("modulo", "ObjectsTextOpsAlign + JsonSerializer"), ("align_json", string.IsNullOrWhiteSpace(outPath) ? "(stdout/default)" : outPath), ("extraction", "resumo final da extração + JSON em outputs/extract"));
                    if (!ReturnUtils.IsEnabled())
                        PrintExtractionSummary(report.Extraction);
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

        private static object BuildExtractionPayload(ObjectsTextOpsDiff.AlignDebugReport report, string aPath, string bPath, string docKey, bool verbose = false)
        {
            return BuildExtractionPayload(report, null, aPath, bPath, docKey, verbose);
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

        private static object BuildExtractionPayload(ObjectsTextOpsDiff.AlignDebugReport report, ObjectsTextOpsDiff.AlignDebugReport? backReport, string aPath, string bPath, string docKey, bool verbose = false)
        {
            var resolvedDoc = DocumentValidationRules.ResolveDocKeyForDetection(docKey);
            var outputDocType = DocumentValidationRules.MapDocKeyToOutputType(resolvedDoc);
            var bandFront = string.IsNullOrWhiteSpace(report.Label) ? "front_head" : report.Label.Trim();
            var bandBack = string.IsNullOrWhiteSpace(backReport?.Label) ? "back_tail" : backReport!.Label.Trim();
            var mapPath = ResolveAlignRangeMapPath(resolvedDoc);
            var dualBand = backReport != null;

            if (verbose)
            {
                PrintPipelineStep(
                    "passo 3.1/4 - preparação da extração",
                    "passo 3.2/4 - recorte value_full/op_range",
                    ("modulo", "ObjectsTextOpsAlign + DocumentValidationRules"),
                    ("doc_key", resolvedDoc),
                    ("doc_type", outputDocType),
                    ("band", dualBand ? $"{bandFront}+{bandBack}" : bandFront),
                    ("map_path", string.IsNullOrWhiteSpace(mapPath) ? "(não encontrado)" : mapPath)
                );
            }
            if (string.IsNullOrWhiteSpace(mapPath))
            {
                return new Dictionary<string, object>
                {
                    ["status"] = "map_not_found",
                    ["doc_key"] = resolvedDoc,
                    ["doc_type"] = outputDocType,
                    ["band"] = dualBand ? $"{bandFront}+{bandBack}" : bandFront,
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
                    "passo 3.2/4 - resultado do recorte para parser (front_head)",
                    dualBand ? "passo 3.2b/4 - recorte back_tail" : "passo 3.3/4 - parser do mapa YAML",
                    ("modulo", "ObjectsTextOpsAlign.BuildValueFullFromBlocks"),
                    ("op_range_a", string.IsNullOrWhiteSpace(opRangeAFront) ? "(vazio)" : opRangeAFront),
                    ("op_range_b", string.IsNullOrWhiteSpace(opRangeBFront) ? "(vazio)" : opRangeBFront),
                    ("len_value_full_a", valueFullAFront.Length.ToString(CultureInfo.InvariantCulture)),
                    ("len_value_full_b", valueFullBFront.Length.ToString(CultureInfo.InvariantCulture)),
                    ("sample_a", ShortText(valueFullAFront)),
                    ("sample_b", ShortText(valueFullBFront))
                );
            }

            ObjectsMapFields.CompactExtractionOutput? parsedBack = null;
            if (dualBand)
            {
                if (!TryParseBandReport(backReport!, mapPath, bandBack, aPath, bPath, out parsedBack, out var backError, out var opRangeABack, out var opRangeBBack, out var valueFullABack, out var valueFullBBack) || parsedBack == null)
                {
                    if (verbose)
                    {
                        PrintPipelineStep(
                            "passo 3.2b/4 - recorte back_tail",
                            "passo 3.3/4 - parser do mapa YAML",
                            ("modulo", "ObjectsTextOpsAlign.BuildValueFullFromBlocks"),
                            ("resultado", "falhou para back_tail; mantendo front_head"),
                            ("erro", string.IsNullOrWhiteSpace(backError) ? "(sem detalhe)" : backError)
                        );
                    }
                    dualBand = false;
                }
                else if (verbose)
                {
                    PrintPipelineStep(
                        "passo 3.2b/4 - resultado do recorte para parser (back_tail)",
                        "passo 3.3/4 - parser do mapa YAML",
                        ("modulo", "ObjectsTextOpsAlign.BuildValueFullFromBlocks"),
                        ("op_range_a_back", string.IsNullOrWhiteSpace(opRangeABack) ? "(vazio)" : opRangeABack),
                        ("op_range_b_back", string.IsNullOrWhiteSpace(opRangeBBack) ? "(vazio)" : opRangeBBack),
                        ("len_value_full_a_back", valueFullABack.Length.ToString(CultureInfo.InvariantCulture)),
                        ("len_value_full_b_back", valueFullBBack.Length.ToString(CultureInfo.InvariantCulture)),
                        ("sample_a_back", ShortText(valueFullABack)),
                        ("sample_b_back", ShortText(valueFullBBack))
                    );
                }
            }

            var valuesA = new Dictionary<string, string>(parsedFront.PdfA.Values, StringComparer.OrdinalIgnoreCase);
            var valuesB = new Dictionary<string, string>(parsedFront.PdfB.Values, StringComparer.OrdinalIgnoreCase);
            var fieldsA = new Dictionary<string, ObjectsMapFields.CompactFieldOutput>(parsedFront.PdfA.Fields, StringComparer.OrdinalIgnoreCase);
            var fieldsB = new Dictionary<string, ObjectsMapFields.CompactFieldOutput>(parsedFront.PdfB.Fields, StringComparer.OrdinalIgnoreCase);

            if (dualBand && parsedBack != null)
            {
                MergeSideFrom(valuesA, fieldsA, parsedBack.PdfA);
                MergeSideFrom(valuesB, fieldsB, parsedBack.PdfB);
            }

            var parserFieldsA = CloneFieldOutputs(fieldsA);
            var parserFieldsB = CloneFieldOutputs(fieldsB);

            if (verbose)
            {
                PrintPipelineStep(
                    "passo 3.3/4 - parser do mapa YAML (ok)",
                    "passo 3.4/4 - enriquecimento de honorários",
                    ("modulo", "Obj.Commands.ObjectsMapFields (alignrange_fields/*.yml)"),
                    ("fields_a_non_empty", CountNonEmptyValues(valuesA).ToString(CultureInfo.InvariantCulture)),
                    ("fields_b_non_empty", CountNonEmptyValues(valuesB).ToString(CultureInfo.InvariantCulture)),
                    ("origem_values_a", dualBand ? "merge: front_head prioridade + fill de back_tail" : "parsed.pdf_a.values <= parsed.pdf_a.fields (source/op_range/obj)"),
                    ("origem_values_b", dualBand ? "merge: front_head prioridade + fill de back_tail" : "parsed.pdf_b.values <= parsed.pdf_b.fields (source/op_range/obj)")
                );
            }

            var beforeHonorariosA = new Dictionary<string, string>(valuesA, StringComparer.OrdinalIgnoreCase);
            var beforeHonorariosB = new Dictionary<string, string>(valuesB, StringComparer.OrdinalIgnoreCase);
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
                    "passo 3.5/4 - validação documental",
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

            var catalog = ValidatorFacade.GetPeritoCatalog(null);
            var beforeValidatorA = new Dictionary<string, string>(valuesA, StringComparer.OrdinalIgnoreCase);
            var beforeValidatorB = new Dictionary<string, string>(valuesB, StringComparer.OrdinalIgnoreCase);
            var okB = ValidatorFacade.ApplyAndValidateDocumentValues(valuesB, outputDocType, catalog, out var reasonB, out var validatorChangedB);
            var policyChangedA = EnforceStrictArbitradoPolicy(beforeHonorariosA, parserFieldsA, valuesA, fieldsA);
            var policyChangedB = EnforceStrictArbitradoPolicy(beforeHonorariosB, parserFieldsB, valuesB, fieldsB);
            MarkModuleChanges(beforeValidatorA, valuesA, fieldsA, "validator");
            MarkModuleChanges(beforeValidatorB, valuesB, fieldsB, "validator");
            var ok = okB;
            var reason = reasonB ?? "";
            if (verbose)
            {
                PrintPipelineStep(
                    "passo 3.5/4 - validação",
                    "passo 4/4 - resumo colorido e persistência",
                    ("modulo", "Obj.ValidatorModule.ValidatorFacade"),
                    ("changed_keys_validator_a", DescribeChangedKeys(beforeValidatorA, valuesA)),
                    ("changed_keys_validator_b", validatorChangedB == null || validatorChangedB.Count == 0
                        ? "(nenhum)"
                        : string.Join(", ", validatorChangedB)),
                    ("policy_strict_money_a", policyChangedA.Count == 0 ? "(nenhum)" : string.Join(", ", policyChangedA)),
                    ("policy_strict_money_b", policyChangedB.Count == 0 ? "(nenhum)" : string.Join(", ", policyChangedB)),
                    ("valor_path_b_pos_validator", DescribeMoneyPath(valuesB, fieldsB)),
                    ("validator_ok_b", okB.ToString().ToLowerInvariant()),
                    ("validator_reason_b", string.IsNullOrWhiteSpace(reasonB) ? "(ok)" : reasonB!)
                );
            }

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
            var flowA = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var flowB = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in trackedFields)
            {
                flowA[field] = BuildFieldFlow(beforeHonorariosA, valuesA, honorariosA?.DerivedValues, field);
                flowB[field] = BuildFieldFlow(beforeHonorariosB, valuesB, honorariosB?.DerivedValues, field);
            }

            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["status"] = "ok",
                ["doc_key"] = resolvedDoc,
                ["doc_type"] = outputDocType,
                ["map_path"] = parsedFront.MapPath,
                ["band"] = dualBand ? $"{bandFront}+{bandBack}" : bandFront,
                ["parsed"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pdf_a"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["values"] = valuesA,
                        ["fields"] = fieldsA
                    },
                    ["pdf_b"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["values"] = valuesB,
                        ["fields"] = fieldsB
                    }
                },
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
                        ["3_5_validator"] = "Obj.ValidatorModule.ValidatorFacade",
                        ["4_output"] = "ObjectsTextOpsAlign + JsonSerializer"
                    },
                    ["merge_policy"] = dualBand ? "front_head prioridade; back_tail preenche apenas campos vazios" : "single_band",
                    ["values_origin_note"] = "Os valores finais de parsed.pdf_a.values e parsed.pdf_b.values vêm do parser YAML (parsed.*.fields) e podem receber complemento do módulo de honorários."
                },
                ["value_flow"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pdf_a"] = flowA,
                    ["pdf_b"] = flowB
                },
                ["validator"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ok"] = ok,
                    ["ok_b"] = okB,
                    ["reason"] = reason,
                    ["reason_b"] = reasonB ?? ""
                },
                ["honorarios"] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["pdf_a"] = BuildHonorariosSnapshot(honorariosA),
                    ["pdf_b"] = BuildHonorariosSnapshot(honorariosB)
                }
            };
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

                Console.WriteLine(Colorize(
                    $"[EXTRACTION] {(ok ? "OK" : "FAIL")} doc={docKey} type={docType} band={band}",
                    ok ? AnsiOk : AnsiErr));

                if (root.TryGetProperty("parsed", out var parsed))
                {
                    PrintSideValues(parsed, "pdf_a", "VALORES A");
                    PrintSideValues(parsed, "pdf_b", "VALORES B");
                }

                if (root.TryGetProperty("validator", out var validator))
                {
                    var vOk = validator.TryGetProperty("ok", out var vOkEl) && vOkEl.ValueKind == JsonValueKind.True;
                    var reason = validator.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() ?? "" : "";
                    var color = vOk ? AnsiOk : AnsiErr;
                    Console.WriteLine(Colorize($"[VALIDATOR] {vOk.ToString().ToLowerInvariant()} reason=\"{reason}\"", color));
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
                var obj = 0;
                var parserValue = "";
                if (hasFields && fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(key, out var fieldMeta))
                {
                    if (fieldMeta.TryGetProperty("Source", out var sourceEl))
                        source = string.IsNullOrWhiteSpace(sourceEl.GetString()) ? "(vazio)" : sourceEl.GetString() ?? "(vazio)";
                    if (fieldMeta.TryGetProperty("OpRange", out var rangeEl))
                        opRange = string.IsNullOrWhiteSpace(rangeEl.GetString()) ? "(vazio)" : rangeEl.GetString() ?? "(vazio)";
                    if (fieldMeta.TryGetProperty("Status", out var statusEl))
                        status = statusEl.GetString() ?? "";
                    if (fieldMeta.TryGetProperty("Obj", out var objEl) && objEl.TryGetInt32(out var parsedObj))
                        obj = parsedObj;
                    if (fieldMeta.TryGetProperty("Value", out var parserEl))
                        parserValue = parserEl.GetString() ?? "";
                }

                Console.WriteLine($"  {Colorize(key + ":", AnsiWarn)} \"{value}\"");
                Console.WriteLine($"    origem={source} op={opRange} obj={obj} status={status} modulo=Obj.Commands.ObjectsMapFields");
                var sourceNorm = source ?? "";
                var adjustModule = "";
                if (sourceNorm.Contains("validator", StringComparison.OrdinalIgnoreCase))
                    adjustModule = "Obj.ValidatorModule.ValidatorFacade";
                else if (sourceNorm.Contains("honorarios", StringComparison.OrdinalIgnoreCase))
                    adjustModule = "Obj.Honorarios.HonorariosFacade";

                if (!string.IsNullOrWhiteSpace(adjustModule))
                {
                    if (string.IsNullOrWhiteSpace(parserValue) && !string.IsNullOrWhiteSpace(value))
                        Console.WriteLine($"    ajuste={adjustModule} parser=\"\" (valor preenchido por módulo autorizado)");
                    else if (!string.IsNullOrWhiteSpace(parserValue) && !SameNormalizedValue(parserValue, value))
                        Console.WriteLine($"    ajuste={adjustModule} parser={ShortText(parserValue, 72)}");
                }
            }
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
            out bool sideSpecified)
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

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == "--help" || arg == "-h")
                    return false;

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
                if (string.Equals(arg, "--align-top", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out alignTop);
                    continue;
                }
                if (string.Equals(arg, "--align", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--show-align", StringComparison.OrdinalIgnoreCase))
                {
                    showAlign = true;
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
            Console.WriteLine("operpdf inspect textopsalign|textopsvar|textopsfixed <pdfA> <pdfB|pdfC|...> [--inputs a.pdf,b.pdf] [--doc tjpb_despacho] [--front|--back|--side front|back] [--pageA N] [--pageB N] [--objA N] [--objB N] [--ops Tj,TJ] [--backoff N] [--min-sim N] [--band N|--max-shift N] [--min-len-ratio N] [--len-penalty N] [--anchor-sim N] [--anchor-len N] [--gap N] [--top N] [--align] [--align-top N] [--out file]");
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
            var showVariables = outputMode != OutputMode.FixedOnly;
            var showFixed = outputMode != OutputMode.VariablesOnly;
            ReportUtils.WriteSummary("TEXTOPS ALIGN", new List<(string Key, string Value)>
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
            });
            Console.WriteLine();

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
