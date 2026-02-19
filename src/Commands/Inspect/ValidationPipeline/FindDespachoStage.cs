using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using Obj.Utils;
using Obj.TjpbDespachoExtractor.Utils;
using Obj.Align;
using Obj.DocDetector;
using Obj.Logging;
using Obj.ValidatorModule;
using iText.Kernel.Pdf;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.Commands
{
    internal static class ObjectsFindDespacho
    {
        private sealed class Hit
        {
            public int Page { get; set; }
            public int Obj { get; set; }
            public string Snippet { get; set; } = "";
        }

        private sealed class StreamInfo
        {
            public PdfStream Stream { get; set; } = null!;
            public int Obj { get; set; }
            public int Len { get; set; }
            public bool HasHit { get; set; }
            public string Snippet { get; set; } = "";
        }

        internal sealed class DespachoCandidate
        {
            public int Page1 { get; set; }
            public int Obj1 { get; set; }
            public int Page2 { get; set; }
            public int Obj2 { get; set; }
            public double Score { get; set; }
        }

        private static readonly string DefaultOutDir =
            Path.Combine("outputs", "objects_despacho");
        private const string DefaultDoc = "tjpb_despacho";

        private static string ResolvePatternCatalogPath(string roiDoc)
        {
            if (string.IsNullOrWhiteSpace(roiDoc))
                return "";

            var direct = PatternRegistry.FindFile("patterns", roiDoc);
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;
            if (!Path.HasExtension(roiDoc))
            {
                var byName = PatternRegistry.FindFile("patterns", roiDoc + ".json");
                if (!string.IsNullOrWhiteSpace(byName))
                    return byName;
            }

            var cfg = Path.Combine("configs", "operpdf.json");
            if (!File.Exists(cfg))
                return "";
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                if (!doc.RootElement.TryGetProperty("patterns", out var node) || node.ValueKind != JsonValueKind.Object)
                    return "";
                foreach (var prop in node.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, roiDoc, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (prop.Value.ValueKind != JsonValueKind.String)
                        return "";
                    var mapped = prop.Value.GetString();
                    if (string.IsNullOrWhiteSpace(mapped))
                        return "";
                    if (File.Exists(mapped))
                        return mapped;
                    var regMapped = PatternRegistry.FindFile("patterns", mapped);
                    if (!string.IsNullOrWhiteSpace(regMapped))
                        return regMapped;
                    if (!Path.HasExtension(mapped))
                    {
                        var regMappedByName = PatternRegistry.FindFile("patterns", mapped + ".json");
                        if (!string.IsNullOrWhiteSpace(regMappedByName))
                            return regMappedByName;
                    }
                    return "";
                }
            }
            catch
            {
                return "";
            }

            return "";
        }

        public static void Execute(string[] args)
        {
            if (!ParseOptions(args, out var inputFile, out var inputDir, out var outDir, out var regex, out var rangeStart, out var rangeEnd, out var backtailStart, out var backtailEnd, out var page, out var roiDoc, out var useRoi, out var autoScan, out var shortcutTop, out var noShortcut, out var requireAll, out var findOnly))
                return;

            if (string.IsNullOrWhiteSpace(outDir))
                outDir = DefaultOutDir;

            if (!string.IsNullOrWhiteSpace(inputDir))
            {
                if (!Directory.Exists(inputDir))
                {
                    ShowHelp();
                    return;
                }

                var files = Directory.GetFiles(inputDir, "*.pdf")
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                files = Preflight.FilterInvalid(files, "despacho");
                if (files.Count == 0)
                {
                    Console.WriteLine("Nenhum PDF encontrado no diretorio.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(outDir))
                    Directory.CreateDirectory(outDir);

                foreach (var file in files)
                {
                    Console.WriteLine("================================================================================");
                    ProcessFile(file, outDir, regex, rangeStart, rangeEnd, backtailStart, backtailEnd, page, roiDoc, useRoi, autoScan, shortcutTop, noShortcut, requireAll, findOnly);
                }
                return;
            }

            if (!string.IsNullOrWhiteSpace(inputFile) && inputFile.Contains(',', StringComparison.Ordinal))
            {
                var files = inputFile
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => p.Length > 0)
                    .ToList();

                files = files.Where(File.Exists).ToList();
                files = Preflight.FilterInvalid(files, "despacho");
                if (files.Count == 0)
                {
                    Console.WriteLine("Nenhum PDF encontrado no input.");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(outDir))
                    Directory.CreateDirectory(outDir);

                foreach (var file in files)
                {
                    Console.WriteLine("================================================================================");
                    ProcessFile(file, outDir, regex, rangeStart, rangeEnd, backtailStart, backtailEnd, page, roiDoc, useRoi, autoScan, shortcutTop, noShortcut, requireAll, findOnly);
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            {
                ShowHelp();
                return;
            }
            if (Preflight.IsInvalid(inputFile))
            {
                Console.WriteLine($"PDF invalido (preflight): {Path.GetFileName(inputFile)}");
                return;
            }

            if (!string.IsNullOrWhiteSpace(outDir))
                Directory.CreateDirectory(outDir);

            ProcessFile(inputFile, outDir, regex, rangeStart, rangeEnd, backtailStart, backtailEnd, page, roiDoc, useRoi, autoScan, shortcutTop, noShortcut, requireAll, findOnly);
        }

        private static int PickStreamObj(string pdfPath, int page, bool requireMarker)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath) || page <= 0)
                return 0;

            var hit = ContentsStreamPicker.Pick(new StreamPickRequest
            {
                PdfPath = pdfPath,
                Page = page,
                RequireMarker = requireMarker
            });
            if (!hit.Found || hit.Obj <= 0)
                hit = ContentsStreamPicker.PickSecondLargest(new StreamPickRequest { PdfPath = pdfPath, Page = page, RequireMarker = false });
            if (!hit.Found || hit.Obj <= 0)
                hit = ContentsStreamPicker.Pick(new StreamPickRequest { PdfPath = pdfPath, Page = page, RequireMarker = false });
            return hit.Obj;
        }

        private static int PickBestStreamObjByPattern(string pdfPath, int page, string roiDoc, bool requireMarker, out double patternSignal)
        {
            patternSignal = 0;
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath) || page <= 0)
                return 0;

            try
            {
                var streams = ContentsStreamPicker.ListTextStreams(new StreamPickRequest
                {
                    PdfPath = pdfPath,
                    Page = page,
                    RequireMarker = false
                });

                if (streams.Count > 0)
                {
                    var top = streams
                        .OrderByDescending(s => s.TextOps)
                        .ThenByDescending(s => s.Len)
                        .Take(Math.Min(8, streams.Count))
                        .ToList();

                    int bestObj = 0;
                    double bestSig = 0;
                    foreach (var s in top)
                    {
                        if (TryComputePatternSignal(pdfPath, roiDoc, page, s.Obj, out var sig) && sig > bestSig)
                        {
                            bestSig = sig;
                            bestObj = s.Obj;
                        }
                    }
                    if (bestObj > 0)
                    {
                        patternSignal = bestSig;
                        return bestObj;
                    }
                }
            }
            catch
            {
                // fallback below
            }

            var fallback = PickStreamObj(pdfPath, page, requireMarker);
            if (fallback > 0 && TryComputePatternSignal(pdfPath, roiDoc, page, fallback, out var sigFallback))
                patternSignal = sigFallback;
            return fallback;
        }

        internal static bool TryResolveDespachoPair(string pdfPath, string roiDoc, out int page1, out int obj1, out int page2, out int obj2, bool fast = false)
        {
            page1 = 0;
            obj1 = 0;
            page2 = 0;
            obj2 = 0;
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                return false;

            try
            {
                var candidates = GetDespachoCandidates(pdfPath, roiDoc, top: 3, fast: fast);
                var best = candidates
                    .OrderByDescending(c => c.Score)
                    .ThenBy(c => c.Page1)
                    .FirstOrDefault();

                if (best != null && best.Page1 > 0 && best.Obj1 > 0)
                {
                    page1 = best.Page1;
                    obj1 = best.Obj1;
                    page2 = best.Page2;
                    obj2 = best.Obj2;
                    return true;
                }

                using var doc = new PdfDocument(new PdfReader(pdfPath));
                var pages = ResolveDespachoPages(pdfPath, doc, 0, roiDoc, fast: fast);
                if (pages.Count == 0)
                    return false;

                var targetPage = pages[0];
                var frontObj = PickStreamObj(pdfPath, targetPage, requireMarker: true);
                if (frontObj <= 0)
                    return false;

                page1 = targetPage;
                obj1 = frontObj;

                var nextPage = targetPage + 1;
                if (nextPage <= doc.GetNumberOfPages())
                {
                    var backObj = PickStreamObj(pdfPath, nextPage, requireMarker: false);
                    if (backObj > 0)
                    {
                        page2 = nextPage;
                        obj2 = backObj;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static List<DespachoCandidate> GetDespachoCandidates(string pdfPath, string roiDoc, int top, bool fast = false)
        {
            var result = new List<DespachoCandidate>();
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                return result;
            if (top <= 0) top = 1;

            try
            {
                using var doc = new PdfDocument(new PdfReader(pdfPath));
                var headerLabels = DespachoContentsDetector.GetDespachoHeaderLabels(roiDoc, 0);
                var scored = new Dictionary<int, (double Score, int Obj, double PatternSig)>();
                var total = doc.GetNumberOfPages();
                var hasPatternCatalog = !fast && !string.IsNullOrWhiteSpace(ResolvePatternCatalogPath(roiDoc));
                for (int p = 1; p <= total; p++)
                {
                    var score = fast
                        ? ComputePageSignalFast(doc, p, headerLabels, roiDoc)
                        : ComputePageSignal(doc, p, headerLabels, roiDoc, pdfPath);
                    if (score > 0)
                        scored[p] = (score, 0, -1.0);
                }

                if (scored.Count == 0)
                {
                    var pages = GetDespachoBookmarkPages(doc, roiDoc);
                    if (pages.Count == 0)
                        pages = FindDespachoPagesByContents(doc, headerLabels, roiDoc);
                    foreach (var p in pages)
                        scored[p] = (0.01, 0, -1.0);
                }

                if (hasPatternCatalog && scored.Count > 0)
                {
                    var maxPatternPages = Math.Min(total, Math.Max(5, top * 2));
                    var topForPattern = scored
                        .Select(kv => (Page: kv.Key, Score: kv.Value.Score))
                        .OrderByDescending(s => s.Score)
                        .ThenBy(s => s.Page)
                        .Take(maxPatternPages)
                        .ToList();

                    foreach (var cand in topForPattern)
                    {
                        var bestObj = PickBestStreamObjByPattern(pdfPath, cand.Page, roiDoc, requireMarker: false, out var patternSig);
                        var entry = scored[cand.Page];
                        var combined = entry.Score;
                        if (patternSig > 0)
                        {
                            combined = Math.Max(combined, (entry.Score * 0.45) + (patternSig * 0.55));
                            combined = Math.Max(combined, patternSig);
                        }
                        scored[cand.Page] = (combined, bestObj > 0 ? bestObj : entry.Obj, patternSig);
                    }
                }

                if (scored.Count == 0)
                    return result;

                var topPages = scored
                    .Select(kv => (Page: kv.Key, Score: kv.Value.Score, Obj: kv.Value.Obj, PatternSig: kv.Value.PatternSig))
                    .OrderByDescending(s => s.Score)
                    .ThenBy(s => s.Page)
                    .Take(top)
                    .ToList();

                foreach (var cand in topPages)
                {
                    var patternSig = cand.PatternSig;
                    var frontObj = cand.Obj;
                    if (frontObj <= 0)
                    {
                        if (fast)
                        {
                            frontObj = PickStreamObj(pdfPath, cand.Page, requireMarker: true);
                            patternSig = -1.0;
                        }
                        else
                        {
                            frontObj = PickBestStreamObjByPattern(pdfPath, cand.Page, roiDoc, requireMarker: true, out patternSig);
                        }
                    }
                    if (frontObj <= 0)
                        continue;

                    var p2 = cand.Page + 1;
                    int o2 = 0;
                    if (p2 <= total)
                    {
                        if (fast)
                        {
                            o2 = PickStreamObj(pdfPath, p2, requireMarker: false);
                        }
                        else
                        {
                            o2 = PickBestStreamObjByPattern(pdfPath, p2, roiDoc, requireMarker: false, out _);
                            if (o2 <= 0)
                                o2 = PickStreamObj(pdfPath, p2, requireMarker: false);
                        }
                    }

                    var finalScore = cand.Score;
                    if (patternSig > 0)
                        finalScore = Math.Max(finalScore, (cand.Score * 0.4) + (patternSig * 0.6));

                    result.Add(new DespachoCandidate
                    {
                        Page1 = cand.Page,
                        Obj1 = frontObj,
                        Page2 = p2 <= total ? p2 : 0,
                        Obj2 = o2,
                        Score = finalScore
                    });
                }
            }
            catch
            {
                return result;
            }

            return result;
        }

        private static void ProcessFile(string inputFile, string outDir, string regex, string rangeStart, string rangeEnd, string backtailStart, string backtailEnd, int page, string roiDoc, bool useRoi, bool autoScan, int shortcutTop, bool noShortcut, bool requireAll, bool findOnly)
        {
            if (autoScan)
            {
                Console.WriteLine("== AUTO SCAN (pattern match) ==");
                ObjectsPattern.RunPatternMatchAutoForDoc(roiDoc, inputFile, log: Logger.Enabled, shortcutTop: shortcutTop, noShortcut: noShortcut, requireAll: requireAll);
                return;
            }
            using var doc = new PdfDocument(new PdfReader(inputFile));
            var pages = ResolveDespachoPages(inputFile, doc, page, roiDoc, fast: findOnly);
            if (pages.Count == 0)
            {
                Console.WriteLine("Nenhum despacho encontrado (pages=0).");
                return;
            }

            if (findOnly)
            {
                var totalPagesFast = doc.GetNumberOfPages();
                foreach (var targetPage in pages)
                {
                    if (targetPage < 1 || targetPage > totalPagesFast)
                        continue;

                    var frontObj = PickStreamObj(inputFile, targetPage, requireMarker: true);
                    if (frontObj <= 0)
                        frontObj = PickStreamObj(inputFile, targetPage, requireMarker: false);

                    var nextPage = targetPage + 1;
                    var backObj = 0;
                    if (nextPage <= totalPagesFast)
                    {
                        backObj = PickStreamObj(inputFile, nextPage, requireMarker: false);
                    }

                    Console.WriteLine($"PDF: {Path.GetFileName(inputFile)}");
                    Console.WriteLine($"front: page={targetPage} obj={frontObj}");
                    if (nextPage <= totalPagesFast)
                        Console.WriteLine($"back : page={nextPage} obj={backObj}");

                    if (!string.IsNullOrWhiteSpace(outDir))
                    {
                        var baseName = Path.GetFileNameWithoutExtension(inputFile);
                        var suffix = pages.Count > 1 ? $"__p{targetPage}" : "";
                        var outPath = Path.Combine(outDir, $"{baseName}{suffix}.txt");
                        var lines = new List<string>
                        {
                            $"page1={targetPage}",
                            $"obj1={frontObj}",
                            $"page2={(nextPage <= totalPagesFast ? nextPage : 0)}",
                            $"obj2={backObj}"
                        };
                        File.WriteAllText(outPath, string.Join(Environment.NewLine, lines));
                        Console.WriteLine($"Saida salva em: {outPath}");
                    }

                    if (pages.Count > 1)
                        Console.WriteLine("--------------------------------------------------------------------------------");
                }
                return;
            }

            var hitRegex = BuildHitRegex(regex);
            var totalPages = doc.GetNumberOfPages();
            var multi = pages.Count > 1;

            foreach (var targetPage in pages)
            {
                if (targetPage < 1 || targetPage > totalPages)
                {
                    Console.WriteLine($"Pagina fora do PDF: {targetPage}");
                    continue;
                }

                var (pageObj, streams) = GetStreamsForPage(doc, targetPage, hitRegex);
                if (streams.Count == 0)
                {
                    Console.WriteLine($"Nenhum stream em /Contents na pagina {targetPage}.");
                    continue;
                }

                // Preferir stream com marker (ContentsStreamPicker); fallback para maior stream.
                var pickedObj = PickBestStreamObjByPattern(inputFile, targetPage, roiDoc, requireMarker: true, out var pickedPatternSig);
                var selected = streams.FirstOrDefault(s => s.Obj == pickedObj)
                    ?? streams.OrderByDescending(s => s.Len).First();

                Console.WriteLine($"PDF: {Path.GetFileName(inputFile)}");
                Console.WriteLine($"page={targetPage} page_obj=[{pageObj}] hits={streams.Count(s => s.HasHit)}");
                Console.WriteLine("streams:");
                var maxLen = streams.Max(s => s.Len);
                foreach (var stream in streams.OrderBy(s => s.Len))
                {
                    var flags = new List<string>();
                    if (stream.Obj == selected.Obj) flags.Add("SELECTED");
                    if (stream.HasHit) flags.Add("HIT");
                    if (stream.Len == maxLen) flags.Add("LARGEST");
                    var flagText = flags.Count > 0 ? $" [{string.Join(",", flags)}]" : "";
                    Console.WriteLine($"  obj={stream.Obj} len={stream.Len}{flagText}");
                    if (stream.HasHit && !string.IsNullOrWhiteSpace(stream.Snippet))
                        Console.WriteLine($"    snippet: {stream.Snippet}");
                }

                var selectedStream = streams.FirstOrDefault(s => s.Obj == selected.Obj);
                if (selectedStream?.Stream != null)
                {
                    var pageResources = doc.GetPage(targetPage).GetResources() ?? new PdfResources(new PdfDictionary());
                    var prefixRaw = ExtractPrefixText(selectedStream.Stream, pageResources, 30);
                    var prefixNorm = CollapseForMatch(prefixRaw);
                    var headerLabels = DespachoContentsDetector.GetDespachoHeaderLabels(roiDoc, 0);
                    var hasLabel = ContainsHeaderLabel(prefixNorm, headerLabels, roiDoc);
                    var fieldSignal = ComputeFieldSignal(selectedStream.Stream, pageResources);
                    var hasFieldSignal = HasFieldSignal(selectedStream.Stream, pageResources);
                    var patternCatalog = ResolvePatternCatalogPath(roiDoc);
                    var hasPatternCatalog = !string.IsNullOrWhiteSpace(patternCatalog);
                    var patternSignal = hasPatternCatalog
                        ? (selected.Obj == pickedObj ? pickedPatternSig : ObjectsPattern.ComputePatternSignal(patternCatalog, inputFile, targetPage, selected.Obj))
                        : -1.0;
                    Console.WriteLine();
                    Console.WriteLine("[TEXT_PATTERN]");
                    Console.WriteLine($"  prefix_raw : \"{Truncate(prefixRaw, 160)}\"");
                    Console.WriteLine($"  prefix_norm: \"{Truncate(prefixNorm, 160)}\"");
                    Console.WriteLine($"  has_label  : {(hasLabel ? "yes" : "no")}");
                    Console.WriteLine($"  field_sig  : {fieldSignal:0.00}");
                    Console.WriteLine($"  pattern_sig: {(patternSignal < 0 ? "-" : patternSignal.ToString("0.00", CultureInfo.InvariantCulture))}");

                    var lowField = !hasFieldSignal || fieldSignal < 0.25;
                    var lowPattern = hasPatternCatalog && patternSignal >= 0 && patternSignal < 0.20;
                    if (!noShortcut && !requireAll && (lowPattern || lowField))
                    {
                        Console.WriteLine();
                        Console.WriteLine("[FALLBACK] Sinal baixo (campo/pattern); acionando AUTO SCAN (pattern match completo).");
                        ObjectsPattern.RunPatternMatchAutoForDoc(roiDoc, inputFile, log: Logger.Enabled, shortcutTop: shortcutTop, noShortcut: true, requireAll: requireAll);
                        return;
                    }
                }

                Console.WriteLine();
                var useRoiForPage = useRoi;
                var canRangeFront = true;
                var canRangeBack = true;
                if (useRoiForPage)
                {
                    var roiPath = DespachoContentsDetector.ResolveRoiPathForObj(roiDoc, selected.Obj);
                    if (string.IsNullOrWhiteSpace(roiPath))
                    {
                        Console.WriteLine($"ROI nao encontrado para obj={selected.Obj}; seguindo sem ROI.");
                        useRoiForPage = false;
                        if (string.IsNullOrWhiteSpace(rangeStart) || string.IsNullOrWhiteSpace(rangeEnd))
                            canRangeFront = false;
                        if (string.IsNullOrWhiteSpace(backtailStart))
                            canRangeBack = false;
                    }
                }

                string? frontText = null;
                if (canRangeFront)
                {
                    Console.WriteLine("== Operadores (Tj/TJ) - recorte por range (front/head) ==");
                    var diffArgs = new List<string>
                    {
                        "--inputs", $"{inputFile},{inputFile}",
                        "--obj", selected.Obj.ToString(CultureInfo.InvariantCulture),
                        "--op", "Tj,TJ",
                        "--range-text"
                    };
                    if (useRoiForPage)
                    {
                        diffArgs.Add("--doc");
                        diffArgs.Add(roiDoc);
                    }
                    else
                    {
                        diffArgs.Add("--range-start");
                        diffArgs.Add(rangeStart);
                        diffArgs.Add("--range-end");
                        diffArgs.Add(rangeEnd);
                    }
                    var frontOut = CaptureOutputPlain(() => ObjectsTextOpsDiff.Execute(diffArgs.ToArray(), ObjectsTextOpsDiff.DiffMode.Both));
                    Console.Write(frontOut);
                    frontText = ExtractRangeText(frontOut, inputFile);
                }

                var nextPage = targetPage + 1;
                if (nextPage > totalPages)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Sem backtail: pagina {nextPage} nao existe.");
                    continue;
                }

                var (backPageObj, backStreams) = GetStreamsForPage(doc, nextPage, hitRegex);
                if (backStreams.Count == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Sem backtail: nenhum stream em /Contents na pagina {nextPage}.");
                    continue;
                }

                // Backtail: usar ContentsStreamPicker sem exigir marker; fallback para maior stream.
                var backPickedObj = PickStreamObj(inputFile, nextPage, requireMarker: false);
                var backSelected = backStreams.FirstOrDefault(s => s.Obj == backPickedObj)
                    ?? backStreams.OrderByDescending(s => s.Len).FirstOrDefault();

                Console.WriteLine();
                Console.WriteLine($"== Backtail (pagina {nextPage}) ==");
                Console.WriteLine($"page={nextPage} page_obj=[{backPageObj}] hits={backStreams.Count(s => s.HasHit)}");
                Console.WriteLine("streams:");
                var maxLenBack = backStreams.Max(s => s.Len);
                foreach (var stream in backStreams.OrderBy(s => s.Len))
                {
                    var flags = new List<string>();
                    if (backSelected != null && stream.Obj == backSelected.Obj) flags.Add("SELECTED");
                    if (stream.HasHit) flags.Add("HIT");
                    if (stream.Len == maxLenBack) flags.Add("LARGEST");
                    var flagText = flags.Count > 0 ? $" [{string.Join(",", flags)}]" : "";
                    Console.WriteLine($"  obj={stream.Obj} len={stream.Len}{flagText}");
                    if (stream.HasHit && !string.IsNullOrWhiteSpace(stream.Snippet))
                        Console.WriteLine($"    snippet: {stream.Snippet}");
                }

                if (backSelected == null)
                {
                    Console.WriteLine("Nenhum stream selecionado para backtail.");
                    continue;
                }

                Console.WriteLine();
                string? backText = null;
                if (canRangeBack)
                {
                    Console.WriteLine("== Operadores (Tj/TJ) - recorte por range (backtail) ==");
                    var backDiffArgs = new List<string>
                    {
                        "--inputs", $"{inputFile},{inputFile}",
                        "--obj", backSelected.Obj.ToString(CultureInfo.InvariantCulture),
                        "--op", "Tj,TJ",
                        "--range-text"
                    };
                    if (useRoiForPage)
                    {
                        backDiffArgs.Add("--doc");
                        backDiffArgs.Add(roiDoc);
                    }
                    else
                    {
                        backDiffArgs.Add("--range-start");
                        backDiffArgs.Add(backtailStart);
                        if (!string.IsNullOrWhiteSpace(backtailEnd))
                        {
                            backDiffArgs.Add("--range-end");
                            backDiffArgs.Add(backtailEnd);
                        }
                        else
                        {
                            backDiffArgs.Add("--range-end-op");
                            backDiffArgs.Add("999999");
                        }
                    }
                    var backOut = CaptureOutputPlain(() => ObjectsTextOpsDiff.Execute(backDiffArgs.ToArray(), ObjectsTextOpsDiff.DiffMode.Both));
                    Console.Write(backOut);
                    backText = ExtractRangeText(backOut, inputFile);
                }

                Console.WriteLine();
                Console.WriteLine("== EXTRAÇÃO (pattern match) ==");
                ObjectsPattern.RunPatternMatchForPages(roiDoc, inputFile, targetPage, selected.Obj, nextPage, backSelected.Obj, log: Logger.Enabled);

                if (!string.IsNullOrWhiteSpace(outDir))
                {
                    var baseName = Path.GetFileNameWithoutExtension(inputFile);
                    var suffix = multi ? $"__p{targetPage}" : "";
                    var outPath = Path.Combine(outDir, $"{baseName}{suffix}.txt");
                    var combined = CombineText(frontText, backText);
                    File.WriteAllText(outPath, combined ?? string.Empty);
                    Console.WriteLine();
                    Console.WriteLine($"Saida salva em: {outPath}");
                }

                if (multi)
                    Console.WriteLine("--------------------------------------------------------------------------------");
            }
        }

        private static bool ParseOptions(string[] args, out string inputFile, out string inputDir, out string outDir, out string regex, out string rangeStart, out string rangeEnd, out string backtailStart, out string backtailEnd, out int page, out string roiDoc, out bool useRoi, out bool autoScan, out int shortcutTop, out bool noShortcut, out bool requireAll, out bool findOnly)
        {
            inputFile = "";
            inputDir = "";
            outDir = "";
            page = 0;
            regex = "";
            rangeStart = "";
            rangeEnd = "";
            backtailStart = "";
            backtailEnd = "";
            roiDoc = DefaultDoc;
            useRoi = true;
            autoScan = false;
            shortcutTop = 3;
            noShortcut = false;
            requireAll = false;
            // Default: apenas identificar page/obj (sem extracao) para ser rapido.
            findOnly = true;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--input-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputDir = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--out-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    outDir = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--regex", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    regex = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--range-start", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    rangeStart = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--range-end", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    rangeEnd = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--backtail-start", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    backtailStart = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--backtail-end", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    backtailEnd = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--doc", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    roiDoc = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--no-roi", StringComparison.OrdinalIgnoreCase))
                {
                    useRoi = false;
                    continue;
                }
                if (string.Equals(arg, "--auto", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--scan", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--auto-scan", StringComparison.OrdinalIgnoreCase))
                {
                    autoScan = true;
                    continue;
                }
                if (string.Equals(arg, "--shortcut-top", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                        shortcutTop = Math.Max(1, n);
                    continue;
                }
                if (string.Equals(arg, "--no-shortcut", StringComparison.OrdinalIgnoreCase))
                {
                    noShortcut = true;
                    continue;
                }
                if (string.Equals(arg, "--require-all", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--strict", StringComparison.OrdinalIgnoreCase))
                {
                    requireAll = true;
                    continue;
                }
                if (string.Equals(arg, "--fast", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--find-only", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--no-extract", StringComparison.OrdinalIgnoreCase))
                {
                    findOnly = true;
                    continue;
                }
                if (string.Equals(arg, "--extract", StringComparison.OrdinalIgnoreCase))
                {
                    findOnly = false;
                    continue;
                }
                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                        page = n;
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(inputFile) && string.IsNullOrWhiteSpace(inputDir))
                {
                    var raw = arg.Trim();
                    if (Directory.Exists(raw))
                        inputDir = raw;
                    else
                        inputFile = raw;
                }
            }
            if (!useRoi && !autoScan)
            {
                if (string.IsNullOrWhiteSpace(rangeStart)
                    || string.IsNullOrWhiteSpace(rangeEnd)
                    || string.IsNullOrWhiteSpace(backtailStart))
                {
                    Console.WriteLine("Modo manual (--no-roi) exige --range-start, --range-end e --backtail-start.");
                    return false;
                }
            }

            return true;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("operpdf inspect despacho --input file.pdf [--page N]");
            Console.WriteLine("operpdf inspect despacho --input-dir <dir> [--page N]");
            Console.WriteLine("operpdf inspect despacho --input :Q1-20 [--page N]");
            Console.WriteLine("  [--out-dir <dir>]");
            Console.WriteLine("  [--regex <pat>] (manual/debug)");
            Console.WriteLine("  [--range-start <pat>] [--range-end <pat>] (manual)");
            Console.WriteLine("  [--backtail-start <pat>] [--backtail-end <pat>] (manual)");
            Console.WriteLine("  [--auto|--scan] (auto-scan via pattern match)");
            Console.WriteLine("  [--shortcut-top N] [--no-shortcut] [--require-all]");
            Console.WriteLine("  [--fast|--find-only|--no-extract] (apenas identifica page/obj, sem extracao) [PADRAO]");
            Console.WriteLine("  [--extract] (forca extracao completa)");
            Console.WriteLine($"  default out-dir: {DefaultOutDir}");
            Console.WriteLine($"  default doc (ROI): {DefaultDoc}  (use --no-roi for manual ranges)");
        }

        private static string CaptureOutputPlain(Action action)
        {
            var original = Console.Out;
            using var sw = new StringWriter();
            Console.SetOut(sw);
            try { action(); }
            finally { Console.SetOut(original); }
            return sw.ToString();
        }

        private static List<Hit> ParseFindHits(string output)
        {
            var hits = new List<Hit>();
            var clean = StripAnsi(output);
            var lines = clean.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.StartsWith("=== page=", StringComparison.OrdinalIgnoreCase))
                    continue;
                var m = Regex.Match(line, @"page=(\d+).*obj=\[(\d+)\]");
                if (!m.Success) continue;
                var page = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var obj = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var snippet = "";
                if (i + 2 < lines.Length)
                    snippet = lines[i + 2].Trim();
                hits.Add(new Hit { Page = page, Obj = obj, Snippet = snippet });
            }
            return hits;
        }

        private static (int pageObj, List<StreamInfo> streams) ParseListStreams(string output)
        {
            var streams = new List<StreamInfo>();
            var clean = StripAnsi(output);
            var pageObj = 0;
            var lines = clean.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (pageObj == 0)
                {
                    var mPage = Regex.Match(line, @"page_obj=\[(\d+)\]");
                    if (mPage.Success)
                        pageObj = int.Parse(mPage.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                var m = Regex.Match(line, @"kind=contents obj=\[(\d+)\].*len=(\d+)");
                if (!m.Success) continue;
                streams.Add(new StreamInfo
                {
                    Obj = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    Len = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)
                });
            }
            return (pageObj, streams);
        }

        private static string StripAnsi(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Regex.Replace(text, @"\x1b\[[0-9;]*[A-Za-z]", "");
        }

        private static string? ExtractRangeText(string output, string inputFile)
        {
            if (string.IsNullOrWhiteSpace(output)) return null;
            var clean = StripAnsi(output);
            var lines = clean.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var full = inputFile;
            var baseName = Path.GetFileName(inputFile);
            foreach (var line in lines)
            {
                var text = TryExtractRangeTextFromLine(line, full) ?? TryExtractRangeTextFromLine(line, baseName);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            return null;
        }

        private static string? TryExtractRangeTextFromLine(string line, string name)
        {
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(name)) return null;
            var marker = name + ": \"";
            var idx = line.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;
            var start = idx + marker.Length;
            var end = line.LastIndexOf("\" (len=", StringComparison.Ordinal);
            if (end > start)
                return line.Substring(start, end - start);
            return null;
        }

        private static string CombineText(string? front, string? back)
        {
            var a = front?.Trim() ?? "";
            var b = back?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b))
                return "";
            if (string.IsNullOrWhiteSpace(a))
                return b;
            if (string.IsNullOrWhiteSpace(b))
                return a;
            return a + Environment.NewLine + Environment.NewLine + b;
        }

        private static string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            if (text.Length <= maxLen)
                return text;
            return text.Substring(0, maxLen).TrimEnd() + "…";
        }

        private static List<int> ResolveDespachoPages(string pdfPath, PdfDocument doc, int requestedPage, string roiDoc, bool fast = false)
        {
            if (requestedPage > 0)
                return new List<int> { requestedPage };

            var pages = GetDespachoBookmarkPages(doc, roiDoc);
            if (pages.Count > 0)
            {
                var allPages = GetAllBookmarkPages(doc);
                var total = doc.GetNumberOfPages();
                var selected = new HashSet<int>();
                foreach (var start in pages.OrderBy(p => p))
                {
                    var next = allPages.FirstOrDefault(p => p > start);
                    var end = next > 0 ? next - 1 : total;
                    if (start >= 1 && start <= total)
                        selected.Add(start);
                }
                return selected.OrderBy(p => p).ToList();
            }

            var candidates = GetDespachoCandidates(pdfPath, roiDoc, top: 3, fast: fast);
            var bestCandidate = candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Page1)
                .FirstOrDefault();
            if (bestCandidate != null && bestCandidate.Page1 > 0)
                return new List<int> { bestCandidate.Page1 };

            if (fast)
                return new List<int>();

            var headerLabels = DespachoContentsDetector.GetDespachoHeaderLabels(roiDoc, 0);
            var bestPair = FindBestPairBySignal(pdfPath, doc, headerLabels, roiDoc);
            if (bestPair > 0)
                return new List<int> { bestPair };
            return FindDespachoPagesByContents(doc, headerLabels, roiDoc);
        }

        private static List<int> GetDespachoBookmarkPages(PdfDocument doc, string roiDoc)
        {
            var pages = new HashSet<int>();
            var items = OutlineUtils.GetOutlinePages(doc);
            foreach (var item in items)
            {
                if (item.Page > 0 && DocumentValidationRules.ContainsBookmarkKeywordsForDoc(item.Title, roiDoc))
                    pages.Add(item.Page);
            }
            return pages.OrderBy(p => p).ToList();
        }

        private static List<int> GetAllBookmarkPages(PdfDocument doc)
        {
            var pages = new HashSet<int>();
            var items = OutlineUtils.GetOutlinePages(doc);
            foreach (var item in items)
            {
                if (item.Page > 0)
                    pages.Add(item.Page);
            }
            return pages.OrderBy(p => p).ToList();
        }

        private static List<int> FindDespachoPagesByContents(PdfDocument doc, IReadOnlyList<string> headerLabels, string roiDoc)
        {
            var pages = new List<int>();
            if (doc == null)
                return pages;

            int total = doc.GetNumberOfPages();
            for (int p = 1; p <= total; p++)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                var streams = EnumerateStreams(contents).ToList();
                if (streams.Count == 0)
                {
                    Console.WriteLine($"Aviso: /Contents vazio na pagina {p}.");
                    continue;
                }

                foreach (var stream in streams)
                {
                    if (HasTitlePrefix(stream, resources, roiDoc))
                    {
                        pages.Add(p);
                        goto NextPage;
                    }
                }

                foreach (var stream in streams)
                {
                    var rawText = ExtractStreamTextRaw(stream, resources);
                    if (ContainsRejectText(rawText))
                        continue;
                    var text = CollapseForMatch(rawText);
                    var norm = TextUtils.NormalizeForMatch(text ?? "");
                    if (DocumentValidationRules.IsLikelyOficio(norm))
                        continue;
                    if (ContainsHeaderLabel(text, headerLabels, roiDoc))
                    {
                        pages.Add(p);
                        break;
                    }
                }

            NextPage:
                continue;
            }

            return pages;
        }

        private static int FindBestPairBySignal(string pdfPath, PdfDocument doc, IReadOnlyList<string> headerLabels, string roiDoc)
        {
            if (doc == null)
                return 0;

            int total = doc.GetNumberOfPages();
            if (total < 2)
                return 0;

            var scores = new double[total + 1];
            for (int p = 1; p <= total; p++)
                scores[p] = ComputePageSignal(doc, p, headerLabels, roiDoc, pdfPath);

            var topForPattern = Enumerable.Range(1, total)
                .Select(p => (Page: p, Score: scores[p]))
                .Where(s => s.Score > 0)
                .OrderByDescending(s => s.Score)
                .Take(6)
                .ToList();
            foreach (var cand in topForPattern)
            {
                if (TryComputePatternSignal(pdfPath, roiDoc, cand.Page, out var sig) && sig > 0)
                {
                    var boosted = (cand.Score * 0.55) + (sig * 0.45);
                    scores[cand.Page] = Math.Max(scores[cand.Page], Math.Max(boosted, sig));
                }
            }

            double bestScore = 0;
            int bestStart = 0;
            for (int p = 1; p < total; p++)
            {
                if (scores[p] <= 0)
                    continue;
                var score = (scores[p] * 0.7) + (scores[p + 1] * 0.3);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestStart = p;
                }
            }
            return bestScore > 0 ? bestStart : 0;
        }

        private sealed class PageSignal
        {
            public double Score { get; set; }
            public bool HasHeader { get; set; }
            public bool HasTitle { get; set; }
            public int Lines { get; set; }
            public double MedianStep { get; set; }
        }

        private sealed class TemplateFieldRegex
        {
            public string Pattern { get; set; } = "";
        }

        private sealed class TemplateFieldDef
        {
            public List<TemplateFieldRegex> Regex { get; set; } = new List<TemplateFieldRegex>();
        }

        private sealed class TemplateFieldsDoc
        {
            public Dictionary<string, TemplateFieldDef> Fields { get; set; } =
                new Dictionary<string, TemplateFieldDef>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class DetectCatalog
        {
            public Dictionary<string, List<Regex>> FieldRegex { get; } =
                new Dictionary<string, List<Regex>>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> OptionalFields { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> DetectFields { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> RejectTexts { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> RejectTextsLoose { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Lazy<DetectCatalog> DetectCatalogLazy =
            new Lazy<DetectCatalog>(LoadDetectCatalog);

        private static DetectCatalog LoadDetectCatalog()
        {
            var catalog = new DetectCatalog();
            try
            {
                var patternsPath = PatternRegistry.FindFile("patterns", "tjpb_despacho.json");
                if (!string.IsNullOrWhiteSpace(patternsPath) && File.Exists(patternsPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(patternsPath));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var name in ReadStringList(doc.RootElement, "optional_fields", "optionalFields", "optional"))
                            catalog.OptionalFields.Add(name);
                        foreach (var name in ReadStringList(doc.RootElement, "detect_page1_fields", "detectPage1Fields", "detect_p1_fields", "detect_page1", "detect_p1"))
                            catalog.DetectFields.Add(name);
                        foreach (var name in ReadStringList(doc.RootElement, "reject_texts", "rejectTexts", "reject_text", "rejectText"))
                            AddRejectText(catalog, name);
                    }
                }
            }
            catch
            {
                // ignore
            }

            try
            {
                var tplPath = PatternRegistry.FindFile("template_fields", "tjpb_despacho.yml");
                if (!string.IsNullOrWhiteSpace(tplPath) && File.Exists(tplPath))
                {
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .IgnoreUnmatchedProperties()
                        .Build();
                    using var reader = new StreamReader(tplPath);
                    var doc = deserializer.Deserialize<TemplateFieldsDoc>(reader);
                    if (doc?.Fields != null)
                    {
                        foreach (var kv in doc.Fields)
                        {
                            if (kv.Value == null || kv.Value.Regex == null || kv.Value.Regex.Count == 0)
                                continue;
                            var list = new List<Regex>();
                            foreach (var rx in kv.Value.Regex)
                            {
                                if (string.IsNullOrWhiteSpace(rx.Pattern)) continue;
                                try
                                {
                                    list.Add(new Regex(rx.Pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline));
                                }
                                catch
                                {
                                    // ignore invalid regex
                                }
                            }
                            if (list.Count > 0)
                                catalog.FieldRegex[kv.Key] = list;
                        }
                    }
                }
            }
            catch
            {
                // ignore
            }

            if (catalog.DetectFields.Contains("*"))
            {
                catalog.DetectFields.Clear();
                foreach (var key in catalog.FieldRegex.Keys)
                    catalog.DetectFields.Add(key);
            }

            if (catalog.DetectFields.Count == 0)
            {
                foreach (var key in catalog.FieldRegex.Keys)
                    catalog.DetectFields.Add(key);
            }

            if (catalog.RejectTexts.Count == 0)
            {
                var defaults = new[]
                {
                    "Ofício",
                    "Oficio",
                    "A Sua Senhoria",
                    "ASua Senhoria",
                    "Comunico a Vossa Senhoria",
                    "Comunico aVossa Senhoria",
                    "Senhor Perito",
                    "Certidão",
                    "Termo de Recebimento",
                    "Sistema de Controle de Processos"
                };
                foreach (var text in defaults)
                    AddRejectText(catalog, text);
            }

            return catalog;
        }

        private static void AddRejectText(DetectCatalog catalog, string? text)
        {
            if (catalog == null || string.IsNullOrWhiteSpace(text))
                return;
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
                return;
            catalog.RejectTexts.Add(trimmed);
            var loose = TextUtils.NormalizeForMatch(trimmed);
            if (!string.IsNullOrWhiteSpace(loose))
                catalog.RejectTextsLoose.Add(loose);
        }

        private static bool ContainsRejectText(string? text)
        {
            var cat = DetectCatalogLazy.Value;
            if (cat.RejectTexts.Count == 0 || string.IsNullOrWhiteSpace(text))
                return false;

            foreach (var phrase in cat.RejectTexts)
            {
                if (text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            var loose = TextUtils.NormalizeForMatch(text);
            if (string.IsNullOrWhiteSpace(loose))
                return false;
            foreach (var phrase in cat.RejectTextsLoose)
            {
                if (loose.Contains(phrase, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static IEnumerable<string> ReadStringList(JsonElement root, params string[] names)
        {
            foreach (var name in names)
            {
                if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var item in node.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var value = item.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            yield return value.Trim();
                    }
                }
                yield break;
            }
        }

        private static double ComputePageSignal(PdfDocument doc, int pageNumber, IReadOnlyList<string> headerLabels, string roiDoc, string pdfPath)
        {
            if (doc == null || pageNumber < 1 || pageNumber > doc.GetNumberOfPages())
                return 0;

            var page = doc.GetPage(pageNumber);
            var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
            var contents = page.GetPdfObject().Get(PdfName.Contents);
            if (contents == null)
                return 0;

            double best = 0.0;
            foreach (var stream in EnumerateStreams(contents))
            {
                var prefixRaw = ExtractPrefixText(stream, resources, 30);
                if (ContainsRejectText(prefixRaw))
                    continue;
                var prefixNorm = TextUtils.NormalizeForMatch(prefixRaw ?? "");
                if (DocumentValidationRules.IsLikelyOficio(prefixNorm))
                    continue;
                var collapsed = TextUtils.CollapseSpacedLettersText(prefixNorm);
                if (DocumentValidationRules.IsBlockedDespacho(prefixNorm) || DocumentValidationRules.IsBlockedDespacho(collapsed))
                    continue;
                var hasHeader = ContainsHeaderLabel(collapsed, headerLabels, roiDoc);
                var hasTitle = !string.IsNullOrWhiteSpace(prefixNorm) &&
                               DocumentValidationRules.ContainsContentsTitleKeywordsForDoc(prefixNorm, roiDoc);
                var fieldSignal = ComputeFieldSignal(stream, resources);
                if (!hasHeader && !hasTitle)
                {
                    // allow field-based signal to rescue pages sem cabecalho/titulo
                    if (fieldSignal <= 0)
                        continue;
                }

                var structure = ComputeStructure(stream, resources);
                var score = 0.0;
                if (hasHeader) score += 0.45;
                if (hasTitle) score += 0.25;
                score += structure.Score * 0.30;
                score = Math.Min(1.0, score + fieldSignal * 0.55);
                score = Math.Min(1.0, score);
                if (score > best)
                    best = score;
            }
            return best;
        }

        private static double ComputePageSignalFast(PdfDocument doc, int pageNumber, IReadOnlyList<string> headerLabels, string roiDoc)
        {
            if (doc == null || pageNumber < 1 || pageNumber > doc.GetNumberOfPages())
                return 0;

            var page = doc.GetPage(pageNumber);
            var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
            var contents = page.GetPdfObject().Get(PdfName.Contents);
            if (contents == null)
                return 0;

            double best = 0.0;
            foreach (var stream in EnumerateStreams(contents))
            {
                var prefixRaw = ExtractPrefixText(stream, resources, 30);
                if (ContainsRejectText(prefixRaw))
                    continue;
                var prefixNorm = TextUtils.NormalizeForMatch(prefixRaw ?? "");
                if (DocumentValidationRules.IsLikelyOficio(prefixNorm))
                    continue;
                var collapsed = TextUtils.CollapseSpacedLettersText(prefixNorm);
                if (DocumentValidationRules.IsBlockedDespacho(prefixNorm) || DocumentValidationRules.IsBlockedDespacho(collapsed))
                    continue;
                var hasHeader = ContainsHeaderLabel(collapsed, headerLabels, roiDoc);
                var hasTitle = !string.IsNullOrWhiteSpace(prefixNorm) &&
                               DocumentValidationRules.ContainsContentsTitleKeywordsForDoc(prefixNorm, roiDoc);
                if (!hasHeader && !hasTitle)
                    continue;
                var score = 0.0;
                if (hasHeader) score += 0.75;
                if (hasTitle) score += 0.25;
                if (score > best)
                    best = score;
            }
            return best;
        }

        private static bool TryComputePatternSignal(string pdfPath, string roiDoc, int pageNumber, out double signal)
        {
            signal = -1.0;
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath) || pageNumber <= 0)
                return false;
            var patternCatalog = ResolvePatternCatalogPath(roiDoc);
            if (string.IsNullOrWhiteSpace(patternCatalog))
                return false;

            var obj = PickStreamObj(pdfPath, pageNumber, requireMarker: true);
            if (obj <= 0)
                obj = PickStreamObj(pdfPath, pageNumber, requireMarker: false);
            if (obj <= 0)
                return false;

            return TryComputePatternSignal(pdfPath, roiDoc, pageNumber, obj, out signal);
        }

        private static bool TryComputePatternSignal(string pdfPath, string roiDoc, int pageNumber, int obj, out double signal)
        {
            signal = -1.0;
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath) || pageNumber <= 0 || obj <= 0)
                return false;
            var patternCatalog = ResolvePatternCatalogPath(roiDoc);
            if (string.IsNullOrWhiteSpace(patternCatalog))
                return false;

            signal = ObjectsPattern.ComputePatternSignal(patternCatalog, pdfPath, pageNumber, obj);
            return signal >= 0;
        }

        private static bool HasFieldSignal(PdfStream stream, PdfResources resources)
        {
            var cat = DetectCatalogLazy.Value;
            if (cat.FieldRegex.Count == 0 || cat.DetectFields.Count == 0)
                return false;
            var text = ExtractStreamText(stream, resources);
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var norm = TextUtils.NormalizeForMatch(text);
            var collapsed = TextUtils.CollapseSpacedLettersText(norm);
            int hits = 0;
            int total = 0;
            foreach (var field in cat.DetectFields)
            {
                if (cat.OptionalFields.Contains(field))
                    continue;
                if (!cat.FieldRegex.TryGetValue(field, out var rxList) || rxList.Count == 0)
                    continue;
                total++;
                if (rxList.Any(rx => rx.IsMatch(collapsed)))
                    hits++;
            }
            if (total == 0) return false;
            return hits >= 2;
        }

        private static double ComputeFieldSignal(PdfStream stream, PdfResources resources)
        {
            var cat = DetectCatalogLazy.Value;
            if (cat.FieldRegex.Count == 0 || cat.DetectFields.Count == 0)
                return 0.0;
            var text = ExtractStreamText(stream, resources);
            if (string.IsNullOrWhiteSpace(text))
                return 0.0;
            var norm = TextUtils.NormalizeForMatch(text);
            var collapsed = TextUtils.CollapseSpacedLettersText(norm);
            int hits = 0;
            int total = 0;
            foreach (var field in cat.DetectFields)
            {
                if (cat.OptionalFields.Contains(field))
                    continue;
                if (!cat.FieldRegex.TryGetValue(field, out var rxList) || rxList.Count == 0)
                    continue;
                total++;
                if (rxList.Any(rx => rx.IsMatch(collapsed)))
                    hits++;
            }
            if (total == 0) return 0.0;
            return Math.Min(1.0, (double)hits / total);
        }

        private static string ExtractStreamText(PdfStream stream, PdfResources resources)
        {
            if (stream == null)
                return "";
            if (PdfTextExtraction.TryExtractStreamText(stream, resources, out var text, out _))
                return text ?? "";
            return "";
        }

        private sealed class StructureInfo
        {
            public int Lines { get; set; }
            public double MedianLineStep { get; set; }
            public double Score { get; set; }
        }

        private static StructureInfo ComputeStructure(PdfStream stream, PdfResources resources)
        {
            const int minTextOps = 30;
            const int minLines = 12;
            const double minStep = 8.0;
            const double maxStep = 24.0;
            const double mergeTol = 1.0;

            if (stream == null)
                return new StructureInfo();

            var textOps = PdfTextExtraction.CollectTextOperatorTexts(stream, resources).Count;
            if (textOps < minTextOps)
                return new StructureInfo();

            var items = PdfTextExtraction.CollectTextItems(stream, resources);
            if (items.Count == 0)
                return new StructureInfo();

            var ys = items.Select(i => i.Y).OrderByDescending(y => y).ToList();
            var lines = new List<double>();
            foreach (var y in ys)
            {
                if (lines.Count == 0 || Math.Abs(lines[^1] - y) > mergeTol)
                    lines.Add(y);
            }
            if (lines.Count < minLines)
                return new StructureInfo { Lines = lines.Count };

            var steps = new List<double>();
            for (int i = 1; i < lines.Count; i++)
            {
                var d = Math.Abs(lines[i - 1] - lines[i]);
                if (d > 0.1)
                    steps.Add(d);
            }
            if (steps.Count == 0)
                return new StructureInfo { Lines = lines.Count };

            steps.Sort();
            var median = steps[steps.Count / 2];
            if (median < minStep || median > maxStep)
                return new StructureInfo { Lines = lines.Count, MedianLineStep = median };

            var lineScore = Math.Min(lines.Count, 40) / 40.0;
            var stepScore = 1.0 - Math.Min(Math.Abs(median - 14.0) / 14.0, 0.6);
            var score = Math.Max(0, lineScore * stepScore);

            return new StructureInfo { Lines = lines.Count, MedianLineStep = median, Score = score };
        }

        private static void AddCandidate(List<PdfStream> list, PdfStream stream)
        {
            if (stream == null)
                return;
            if (!list.Contains(stream))
                list.Add(stream);
        }

        private static Regex? BuildHitRegex(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            try
            {
                return new Regex(raw, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch
            {
                return null;
            }
        }

        private static (int pageObj, List<StreamInfo> streams) GetStreamsForPage(PdfDocument doc, int pageNumber, Regex? hitRegex)
        {
            if (doc == null || pageNumber < 1 || pageNumber > doc.GetNumberOfPages())
                return (0, new List<StreamInfo>());

            var page = doc.GetPage(pageNumber);
            var pageObj = page.GetPdfObject().GetIndirectReference()?.GetObjNumber() ?? 0;
            var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
            var contents = page.GetPdfObject().Get(PdfName.Contents);
            var streams = new List<StreamInfo>();

            foreach (var stream in EnumerateStreams(contents))
            {
                int objId = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                var info = new StreamInfo
                {
                    Stream = stream,
                    Obj = objId,
                    Len = stream.GetLength()
                };

                var textRaw = ExtractStreamTextRaw(stream, resources);
                if (hitRegex != null && !string.IsNullOrWhiteSpace(textRaw))
                {
                    var mRaw = hitRegex.Match(textRaw);
                    if (mRaw.Success)
                    {
                        info.HasHit = true;
                        info.Snippet = BuildSnippet(textRaw, mRaw.Index, mRaw.Length);
                    }
                    else
                    {
                        var textMatch = CollapseForMatch(textRaw);
                        var mCollapsed = hitRegex.Match(textMatch);
                        if (mCollapsed.Success)
                        {
                            info.HasHit = true;
                            info.Snippet = BuildSnippet(textRaw, 0, Math.Min(textRaw.Length, 120));
                        }
                    }
                }

                streams.Add(info);
            }

            return (pageObj, streams);
        }

        private static string BuildSnippet(string text, int index, int length)
        {
            const int context = 60;
            var start = Math.Max(0, index - context);
            var end = Math.Min(text.Length, index + length + context);
            var snippet = text.Substring(start, end - start).Trim();
            return snippet;
        }

        private static IEnumerable<PdfStream> EnumerateStreams(PdfObject? obj)
        {
            if (obj == null) yield break;
            if (obj is PdfStream s)
            {
                yield return s;
                yield break;
            }
            if (obj is PdfArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is PdfStream ss) yield return ss;
                }
            }
        }

        private static string ExtractStreamTextRaw(PdfStream stream, PdfResources resources)
        {
            try
            {
                if (PdfTextExtraction.TryExtractStreamText(stream, resources, out var text, out _)
                    && !string.IsNullOrWhiteSpace(text))
                {
                    return NormalizeSpaces(text);
                }
                var parts = PdfTextExtraction.CollectTextOperatorTexts(stream, resources);
                if (parts == null || parts.Count == 0)
                    return "";
                var joined = string.Join(" ", parts);
                return NormalizeSpaces(joined);
            }
            catch
            {
                return "";
            }
        }

        private static string CollapseForMatch(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            return TextUtils.CollapseSpacedLettersText(text);
        }

        private static bool HasTitlePrefix(PdfStream stream, PdfResources resources, string roiDoc)
        {
            if (stream == null)
                return false;
            var prefix = ExtractPrefixText(stream, resources, 20);
            if (string.IsNullOrWhiteSpace(prefix))
                return false;
            var norm = TextUtils.NormalizeForMatch(prefix);
            return DocumentValidationRules.ContainsContentsTitleKeywordsForDoc(norm, roiDoc);
        }

        private static bool ContainsHeaderLabel(string text, IReadOnlyList<string> headerLabels, string roiDoc)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            if (DocumentValidationRules.IsLikelyOficioRaw(text))
                return false;
            var norm = TextUtils.NormalizeForMatch(text);
            if (DocumentValidationRules.IsLikelyOficio(norm))
                return false;
            if (headerLabels != null && headerLabels.Count > 0)
            {
                foreach (var label in headerLabels)
                {
                    if (string.IsNullOrWhiteSpace(label))
                        continue;
                    if (norm.Contains(label, StringComparison.Ordinal))
                        return true;
                }
            }
            return DocumentValidationRules.ContainsHeaderFallbackForDoc(norm, roiDoc);
        }

        private static bool MatchAnyKeyword(string text, IReadOnlyList<string> keywords)
        {
            if (string.IsNullOrWhiteSpace(text) || keywords == null || keywords.Count == 0)
                return false;
            foreach (var key in keywords)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;
                if (text.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static string ExtractPrefixText(PdfStream stream, PdfResources resources, int maxOps)
        {
            try
            {
                var parts = PdfTextExtraction.CollectTextOperatorTexts(stream, resources);
                if (parts == null || parts.Count == 0)
                    return "";
                var take = Math.Max(1, Math.Min(maxOps, parts.Count));
                return string.Join(" ", parts.Take(take));
            }
            catch
            {
                return "";
            }
        }

        private static string NormalizeSpaces(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            return Regex.Replace(text, "\\s+", " ").Trim();
        }
    }
}
