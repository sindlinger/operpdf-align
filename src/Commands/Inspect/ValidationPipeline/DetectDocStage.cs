using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Obj.DocDetector;
using Obj.ValidatorModule;

namespace Obj.Commands
{
    internal static class ObjectsDetectDoc
    {
        private const string CReset = "\u001b[0m";
        private const string CRed = "\u001b[31m";
        private const string CGreen = "\u001b[32m";
        private const string CYellow = "\u001b[33m";
        private const string CBlue = "\u001b[34m";
        private const string CMagenta = "\u001b[35m";
        private const string CCyan = "\u001b[36m";

        public static void Execute(string[] args)
        {
            if (!TryParseArgs(args, out var input, out var page, out var trace, out var useColor, out var useColorTags,
                    out var validate, out var patterns, out var only))
            {
                ShowHelp();
                return;
            }

            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
            {
                Console.WriteLine("PDF nao encontrado: " + (input ?? ""));
                return;
            }

            var onlyDoc = NormalizeOnlyDoc(only);
            var showBaseDetectors = string.IsNullOrWhiteSpace(onlyDoc) || trace;

            Console.WriteLine("=== DETECTORS ===");
            Console.WriteLine("pdf=" + Path.GetFileName(input));
            if (showBaseDetectors)
            {
                PrintWeightLegend(useColor, useColorTags);

                if (trace) Console.WriteLine($"[STEP] BookmarkDetector.Detect(\"{Path.GetFileName(input)}\")");
                var bookmark = BookmarkDetector.Detect(input);
                PrintHit("BookmarkDetector", bookmark, useColor, useColorTags);

                if (trace) Console.WriteLine($"[STEP] ContentsPrefixDetector.Detect(\"{Path.GetFileName(input)}\")");
                var prefix = ContentsPrefixDetector.Detect(input);
                PrintHit("ContentsPrefixDetector", prefix, useColor, useColorTags);

                if (trace) Console.WriteLine($"[STEP] HeaderLabelDetector.Detect(\"{Path.GetFileName(input)}\")");
                var header = HeaderLabelDetector.Detect(input);
                PrintHit("HeaderLabelDetector", header, useColor, useColorTags);

                if (trace) Console.WriteLine($"[STEP] LargestContentsDetector.Detect(\"{Path.GetFileName(input)}\")");
                var largest = LargestContentsDetector.Detect(input);
                PrintHit("LargestContentsDetector", largest, useColor, useColorTags);

                if (trace) Console.WriteLine($"[STEP] NonDespachoDetector.Detect(\"{Path.GetFileName(input)}\")");
                var nonDespacho = NonDespachoDetector.Detect(input);
                PrintHit("NonDespachoDetector", nonDespacho, useColor, useColorTags);

                var detectPage = page > 0 ? page : ResolvePage(bookmark, prefix, header, largest);
                if (detectPage <= 0) detectPage = 1;

                Console.WriteLine($"[INFO] stream_pick_page={detectPage}");
                if (trace) Console.WriteLine($"[STEP] ContentsStreamPicker.Pick(page={detectPage}, requireMarker=true)");
                var pickReq = new StreamPickRequest { PdfPath = input, Page = detectPage, RequireMarker = true };
                var pickMarker = ContentsStreamPicker.Pick(pickReq);
                PrintHit("ContentsStreamPicker (marker)", pickMarker, useColor, useColorTags);

                if (trace) Console.WriteLine($"[STEP] ContentsStreamPicker.Pick(page={detectPage}, requireMarker=false)");
                var pickAny = ContentsStreamPicker.Pick(new StreamPickRequest { PdfPath = input, Page = detectPage, RequireMarker = false });
                PrintHit("ContentsStreamPicker (largest)", pickAny, useColor, useColorTags);

                if (trace) Console.WriteLine($"[STEP] ContentsStreamPicker.PickSecondLargest(page={detectPage})");
                var pickSecond = ContentsStreamPicker.PickSecondLargest(new StreamPickRequest { PdfPath = input, Page = detectPage, RequireMarker = false });
                PrintHit("ContentsStreamPicker (second)", pickSecond, useColor, useColorTags);

                var headers = DespachoContentsDetector.GetDespachoHeaderLabels("tjpb_despacho", 0);
                bool fallback;
                if (trace) Console.WriteLine("[STEP] DespachoContentsDetector.ResolveContentsPageForDoc(page=auto)");
                var contentsPage = DespachoContentsDetector.ResolveContentsPageForDoc(input, 0, headers, out fallback, "tjpb_despacho");
                Console.WriteLine($"DespachoContentsDetector.page={contentsPage} fallback={fallback}");
            }
            else
            {
                Console.WriteLine($"[INFO] fast_mode=on only={onlyDoc}");
            }

            Console.WriteLine();
            Console.WriteLine("=== WEIGHTED (CENTRALIZED) ===");

            var weighted = new List<WeightedDetectionResult>();

            foreach (var docKey in ObjectsDetectionRouter.DetectableDocKeys)
            {
                if (!ShouldRunDoc(onlyDoc, docKey))
                    continue;

                if (trace) Console.WriteLine($"[STEP] {ObjectsDetectionRouter.GetStepNameForDoc(docKey)}");
                var result = ObjectsDetectionRouter.DetectWeighted(input, docKey);
                PrintWeighted(ObjectsDetectionRouter.GetLabelForDoc(docKey), result, useColor, useColorTags);
                weighted.Add(result);
            }

            var best = PickBestWeighted(onlyDoc, weighted.ToArray());
            if (best != null && best.Found)
            {
                Console.WriteLine();
                Console.WriteLine("=== BEST (WEIGHTED) ===");
                Console.WriteLine($"{best.DocType}: page1={best.Page1} page2={best.Page2} obj1={best.Obj1} score={best.Score:0.###}");
            }

            if (validate && best != null && best.Found)
            {
                var patternsDoc = !string.IsNullOrWhiteSpace(patterns) ? patterns : ResolvePatternsForDoc(best.DocType);
                var page1 = best.Page1;
                var page2 = best.Page2;
                var obj1 = best.Obj1 > 0 ? best.Obj1 : ResolveObjForPage(input, page1, requireMarker: true);
                var obj2 = page2 > 0 ? ResolveObjForPage(input, page2, requireMarker: false) : 0;

                if (obj1 > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("=== VALIDATION (pattern match + validator + honorarios) ===");
                    Console.WriteLine($"patterns={patternsDoc} p1={page1} obj1={obj1} p2={page2} obj2={obj2}");
                    ObjectsPattern.RunPatternMatchForPages(patternsDoc, input, page1, obj1, page2, obj2, log: true);
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("[WARN] VALIDATION skipped: obj1 nao encontrado.");
                }
            }
        }

        private static void PrintWeighted(string label, WeightedDetectionResult weighted, bool useColor, bool useColorTags)
        {
            if (weighted == null)
            {
                Console.WriteLine($"{label}: NOT_FOUND");
                return;
            }

            var sum = weighted.DetectorScores.Sum(d => d.Score);
            var max = weighted.MaxScore;
            var maxText = max > 0 ? $" max={max:0.###}" : "";

            if (!weighted.Found)
            {
                Console.WriteLine($"{label}: NOT_FOUND sum={sum:0.###}{maxText} reason={weighted.BlockReason}");
            }
            else
            {
                Console.WriteLine($"{label}: page1={weighted.Page1} page2={weighted.Page2} obj1={weighted.Obj1} score={weighted.Score:0.###} sum={sum:0.###}{maxText}");
            }

            if (weighted.DetectorScores.Count > 0)
            {
                Console.WriteLine("  detectors:");
                foreach (var det in weighted.DetectorScores
                             .OrderByDescending(d => d.Score)
                             .ThenByDescending(d => d.MatchScore)
                             .ThenBy(d => d.Detector, StringComparer.OrdinalIgnoreCase))
                {
                    var color = DetectorColor(det.Detector, useColor);
                    var name = Colorize(det.Detector, color, useColor, useColorTags);
                    var hit = det.Hit ? "hit" : "miss";
                    Console.WriteLine(
                        $"  - {name}: {hit} page={det.Page} obj={det.Obj} weight={det.Weight:0.##} match={det.MatchScore:0.##} score={det.Score:0.##} reason={det.Reason} keyword={det.Keyword} matched={det.MatchedDocKey} notes={det.Notes}");
                }
            }

            if (weighted.PageScores.Count > 0)
            {
                Console.WriteLine("  pages:");
                foreach (var page in weighted.PageScores.Take(5))
                {
                    var dets = string.Join(",", page.Detectors);
                    Console.WriteLine($"  - p{page.Page} obj={page.Obj} score={page.Score:0.##} detectors={dets}");
                }
            }

            if (weighted.Signals.Count > 0)
            {
                Console.WriteLine("  winner_signals:");
                foreach (var sig in weighted.Signals)
                {
                    var color = DetectorColor(sig.Detector, useColor);
                    var name = Colorize(sig.Detector, color, useColor, useColorTags);
                    Console.WriteLine($"  - {name}: page={sig.Page} obj={sig.Obj} score={sig.Weight:0.##} reason={sig.Reason} keyword={sig.Keyword}");
                }
            }
        }

        private static void PrintHit(string label, DetectionHit hit, bool useColor, bool useColorTags)
        {
            if (hit == null)
            {
                Console.WriteLine($"{label}: <null>");
                return;
            }
            if (!hit.Found)
            {
                var name = Colorize(label, DetectorColor(label, useColor), useColor, useColorTags);
                var w0 = ResolveWeight(label);
                Console.WriteLine($"{name}: NOT_FOUND score=0 weight={w0:0.##} reason={hit.Reason}");
                return;
            }
            var extra = "";
            if (!string.IsNullOrWhiteSpace(hit.MatchedKeyword))
                extra = $" keyword={hit.MatchedKeyword}";
            var w = ResolveWeight(label);
            var nameOk = Colorize(label, DetectorColor(label, useColor), useColor, useColorTags);
            Console.WriteLine($"{nameOk}: page={hit.Page} obj={hit.Obj} score={w:0.##} weight={w:0.##} reason={hit.Reason}{extra}");
        }

        private static int ResolvePage(params DetectionHit[] hits)
        {
            foreach (var hit in hits)
            {
                if (hit != null && hit.Found && hit.Page > 0)
                    return hit.Page;
            }
            return 0;
        }

        private static bool ShouldRunDoc(string onlyDoc, string docKey)
        {
            return DocumentValidationRules.IsDocMatch(docKey, onlyDoc);
        }

        private static string NormalizeOnlyDoc(string? only)
        {
            return DocumentValidationRules.NormalizeOnlyDocFilter(only);
        }

        private static bool TryParseArgs(
            string[] args,
            out string input,
            out int page,
            out bool trace,
            out bool useColor,
            out bool useColorTags,
            out bool validate,
            out string? patterns,
            out string? only)
        {
            input = "";
            page = 0;
            trace = false;
            useColor = true;
            useColorTags = false;
            validate = false;
            patterns = null;
            only = null;
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if ((arg == "--input" || arg == "-i") && i + 1 < args.Length)
                {
                    input = args[++i];
                    continue;
                }
                if (arg == "--page" && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out page);
                    continue;
                }
                if (arg == "--trace")
                {
                    trace = true;
                    continue;
                }
                if (arg == "--no-color" || arg == "--no-colors")
                {
                    useColor = false;
                    continue;
                }
                if (arg == "--color" || arg == "--colors")
                {
                    useColor = true;
                    continue;
                }
                if (arg == "--color-tags")
                {
                    useColorTags = true;
                    continue;
                }
                if (arg == "--validate" || arg == "--extract")
                {
                    validate = true;
                    continue;
                }
                if (arg == "--patterns" && i + 1 < args.Length)
                {
                    patterns = args[++i];
                    continue;
                }
                if ((arg == "--only" || arg == "--doc") && i + 1 < args.Length)
                {
                    only = args[++i];
                    continue;
                }
            }
            return !string.IsNullOrWhiteSpace(input);
        }

        private static void ShowHelp()
        {
            Console.WriteLine("operpdf inspect detectdoc --input <pdf> [--page N] [--trace] [--color|--no-color]");
            Console.WriteLine("                   [--color-tags] [--validate|--extract] [--patterns <doc|json>] [--only despacho|certidao|requerimento]");
        }

        private static WeightedDetectionResult? PickBestWeighted(string? only, params WeightedDetectionResult[] results)
        {
            if (!string.IsNullOrWhiteSpace(only))
            {
                foreach (var r in results)
                {
                    if (r == null || !r.Found) continue;
                    if (DocumentValidationRules.IsDocMatch(r.DocType, only))
                        return r;
                }
                return null;
            }

            WeightedDetectionResult? best = null;
            foreach (var r in results)
            {
                if (r == null || !r.Found) continue;
                if (best == null || r.Score > best.Score)
                    best = r;
            }
            return best;
        }

        private static string ResolvePatternsForDoc(string? docType)
        {
            return DocumentValidationRules.ResolvePatternForDoc(docType);
        }

        private static int ResolveObjForPage(string pdf, int page, bool requireMarker)
        {
            if (page <= 0) return 0;
            var hit = ContentsStreamPicker.Pick(new StreamPickRequest
            {
                PdfPath = pdf,
                Page = page,
                RequireMarker = requireMarker
            });
            if (hit != null && hit.Found && hit.Obj > 0)
                return hit.Obj;
            if (requireMarker)
            {
                var fallback = ContentsStreamPicker.Pick(new StreamPickRequest
                {
                    PdfPath = pdf,
                    Page = page,
                    RequireMarker = false
                });
                if (fallback != null && fallback.Found && fallback.Obj > 0)
                    return fallback.Obj;
            }
            return 0;
        }

        private static string DetectorColor(string name, bool useColor)
        {
            if (!useColor) return "";
            var tag = DocumentDetectionPolicy.ResolveDetectorColorTag(name);
            return tag switch
            {
                "green" => CGreen,
                "cyan" => CCyan,
                "blue" => CBlue,
                "magenta" => CMagenta,
                "yellow" => CYellow,
                "red" => CRed,
                _ => ""
            };
        }

        private static double ResolveWeight(string detector)
        {
            return DocumentDetectionPolicy.ResolveWeight(detector, DocumentDetectionPolicy.ProfileDetectDocCli);
        }

        private static string Colorize(string text, string color, bool useColor, bool useColorTags)
        {
            if (!useColor || string.IsNullOrWhiteSpace(color)) return text;
            if (useColorTags || Console.IsOutputRedirected)
            {
                var tag = color switch
                {
                    var c when c == CGreen => "[GREEN]",
                    var c when c == CCyan => "[CYAN]",
                    var c when c == CBlue => "[BLUE]",
                    var c when c == CMagenta => "[MAGENTA]",
                    var c when c == CYellow => "[YELLOW]",
                    var c when c == CRed => "[RED]",
                    _ => "[COLOR]"
                };
                return $"{tag}{text}[/]";
            }
            return color + text + CReset;
        }

        private static void PrintWeightLegend(bool useColor, bool useColorTags)
        {
            Console.WriteLine("weights:");
            var profile = DocumentDetectionPolicy.ProfileDetectDocCli;
            foreach (var detector in DocumentDetectionPolicy.GetLegendDetectors(profile))
            {
                var color = DetectorColor(detector, useColor);
                var weight = DocumentDetectionPolicy.ResolveWeight(detector, profile);
                Console.WriteLine($"  {Colorize(detector, color, useColor, useColorTags)} = {weight:0.###}");
            }
            Console.WriteLine();
        }
    }
}
