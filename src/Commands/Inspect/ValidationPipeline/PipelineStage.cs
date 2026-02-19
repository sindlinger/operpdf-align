using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Obj.Align;
using Obj.Utils;
using Obj.DocDetector;
using Obj.Honorarios;
using Obj.Extraction;
using Obj.FrontBack;
using Obj.Nlp;
using Obj.ValidatorModule;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Utils;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.Commands
{
    internal static partial class ObjectsPipeline
    {
        private const int DefaultBackoff = 2;
        private static readonly string DefaultOutDir = Path.Combine("outputs", "objects_pipeline");

        internal sealed class PipelineResult
        {
            public string PdfA { get; set; } = "";
            public string PdfB { get; set; } = "";
            public DetectionSummary DetectionA { get; set; } = new DetectionSummary();
            public DetectionSummary DetectionB { get; set; } = new DetectionSummary();
            public HeaderFooterSummary? HeaderFooter { get; set; }
            public CaminsSummary? Camins { get; set; }
            public AlignRangeSummary? AlignRange { get; set; }
            public DespachoSubtypeSummary? DespachoSubtype { get; set; }
            public FooterDateSummary? FooterDate { get; set; }
            public MapFieldsSummary? MapFields { get; set; }
            public JsonElement? MapFieldsData { get; set; }
            public NlpSummary? Nlp { get; set; }
            public Dictionary<string, JsonElement>? NlpData { get; set; }
            public FieldsSummary? Fields { get; set; }
            public Dictionary<string, JsonElement>? FieldsData { get; set; }
            public HonorariosSummary? Honorarios { get; set; }
            public ConsolidatedSummary? Consolidated { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }


        internal sealed class ConsolidatedSummary
        {
            public string JsonPath { get; set; } = "";
            public Dictionary<string, string> Inputs { get; set; } = new();
            public List<string> Errors { get; set; } = new();
        }

        internal sealed class DetectionSummary
        {
            public string TitleKey { get; set; } = "";
            public string Title { get; set; } = "";
            public int StartPage { get; set; }
            public int EndPage { get; set; }
            public int BodyObj { get; set; }
            public int BackPage { get; set; }
            public int BackBodyObj { get; set; }
            public int BackSignatureObj { get; set; }
            public string PathRef { get; set; } = "";
            public string Subtype { get; set; } = "";
            public string SubtypeReason { get; set; } = "";
            public List<string> SubtypeHints { get; set; } = new List<string>();
            public string CertidaoExpected { get; set; } = "";
            public string SuppressedDocType { get; set; } = "";
            public string SuppressedReason { get; set; } = "";
        }

        internal sealed class HeaderFooterSummary
        {
            public HeaderFooterPageInfo? FrontA { get; set; }
            public HeaderFooterPageInfo? FrontB { get; set; }
            public HeaderFooterPageInfo? BackA { get; set; }
            public HeaderFooterPageInfo? BackB { get; set; }
        }

        internal sealed class CaminsSummary
        {
            public string DocTypeHint { get; set; } = "";
            public string PageMapPath { get; set; } = "";
            public CaminsAlgoResult? Simhash { get; set; }
            public CaminsAlgoResult? Tfidf { get; set; }
            public CaminsAlgoResult? Kmeans { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        internal sealed class DespachoSubtypeSummary
        {
            public SubtypeSide? PdfA { get; set; }
            public SubtypeSide? PdfB { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        internal sealed class SubtypeSide
        {
            public string Status { get; set; } = "";
            public string Subtype { get; set; } = "";
            public string Reason { get; set; } = "";
            public List<string> Hints { get; set; } = new List<string>();
        }

        internal sealed class CaminsAlgoResult
        {
            public string Name { get; set; } = "";
            public string CsvPath { get; set; } = "";
            public string SummaryPath { get; set; } = "";
            public Dictionary<string, string>? PdfA { get; set; }
            public Dictionary<string, string>? PdfB { get; set; }
            public string Error { get; set; } = "";
        }

        internal sealed class HeaderFooterPageInfo
        {
            public int Page { get; set; }
            public int PrimaryIndex { get; set; }
            public int PrimaryObj { get; set; }
            public int PrimaryTextOps { get; set; }
            public int PrimaryStreamLen { get; set; }
            public string HeaderText { get; set; } = "";
            public string FooterText { get; set; } = "";
            public int FooterIndex { get; set; }
            public int FooterObj { get; set; }
            public string HeaderKey { get; set; } = "";
        }

        internal sealed class AlignRangeSummary
        {
            public RangeValue FrontA { get; set; } = new RangeValue();
            public RangeValue FrontB { get; set; } = new RangeValue();
            public RangeValue BackA { get; set; } = new RangeValue();
            public RangeValue BackB { get; set; } = new RangeValue();
        }

        internal sealed class MapFieldsSummary
        {
            public string Mode { get; set; } = "";
            public string MapPath { get; set; } = "";
            public string BackMapPath { get; set; } = "";
            public string AlignRangePath { get; set; } = "";
            public string JsonPath { get; set; } = "";
            public string RejectsPath { get; set; } = "";
            public int FrontObjA { get; set; }
            public int FrontObjB { get; set; }
            public int BackObjA { get; set; }
            public int BackObjB { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        internal sealed class NlpSummary
        {
            public NlpSegment? FrontA { get; set; }
            public NlpSegment? FrontB { get; set; }
            public NlpSegment? BackA { get; set; }
            public NlpSegment? BackB { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        internal sealed class FieldsSummary
        {
            public FieldsSegment? FrontA { get; set; }
            public FieldsSegment? FrontB { get; set; }
            public FieldsSegment? BackA { get; set; }
            public FieldsSegment? BackB { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        internal sealed class FooterDateSummary
        {
            public FooterDateSide? PdfA { get; set; }
            public FooterDateSide? PdfB { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
        }

        internal sealed class FooterDateSide
        {
            public string Status { get; set; } = "";
            public string DateText { get; set; } = "";
            public string DateIso { get; set; } = "";
            public string Reason { get; set; } = "";
            public string Source { get; set; } = "";
            public int Obj { get; set; }
            public string PathRef { get; set; } = "";
            public string TextSample { get; set; } = "";
        }

        internal sealed class NlpSegment
        {
            public string Label { get; set; } = "";
            public string Status { get; set; } = "";
            public string TextPath { get; set; } = "";
            public string NlpJsonPath { get; set; } = "";
            public string TypedPath { get; set; } = "";
            public string CboOutPath { get; set; } = "";
            public string Error { get; set; } = "";
        }

        internal sealed class FieldsSegment
        {
            public string Label { get; set; } = "";
            public string Status { get; set; } = "";
            public string JsonPath { get; set; } = "";
            public int Count { get; set; }
            public string Error { get; set; } = "";
        }

        internal sealed class RangeValue
        {
            public int Page { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string ValueFull { get; set; } = "";
        }

        internal sealed class ObjFieldValue
        {
            public string Value { get; set; } = "";
            public string ValueFull { get; set; } = "";
            public string ValueRaw { get; set; } = "";
            public string Status { get; set; } = "";
            public string OpRange { get; set; } = "";
            public string Source { get; set; } = "";
            public int Obj { get; set; }
            public ObjBoundingBox? BBox { get; set; }
        }

        internal sealed class ObjBoundingBox
        {
            public double X0 { get; set; }
            public double Y0 { get; set; }
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public int Items { get; set; }
        }

        private sealed class AlignRangeYaml
        {
            public AlignRangeSection? FrontHead { get; set; }
            public AlignRangeSection? BackTail { get; set; }
        }

        private sealed class PipelineConfig
        {
            public PipelineOptions? Pipeline { get; set; }
        }

        private sealed class ObjModelsConfig
        {
            public int Version { get; set; } = 1;
            public Dictionary<string, string> Models { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class PipelineOptions
        {
            public bool NlpEnabled { get; set; } = true;
            public bool FieldsEnabled { get; set; } = true;
            public bool HonorariosEnabled { get; set; } = true;
        }

        private sealed class AlignRangeSection
        {
            public string PdfA { get; set; } = "";
            public string PdfAPath { get; set; } = "";
            public int ObjA { get; set; }
            public string OpRangeA { get; set; } = "";
            public string ValueFullA { get; set; } = "";
            public string PdfB { get; set; } = "";
            public string PdfBPath { get; set; } = "";
            public int ObjB { get; set; }
            public string OpRangeB { get; set; } = "";
            public string ValueFullB { get; set; } = "";
        }

        public static void Execute(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("finalize", StringComparison.OrdinalIgnoreCase))
            {
                ExecuteFinalize(args.Skip(1).ToArray());
                return;
            }
            if (!ParseOptions(args, out var inputs, out var pageA, out var pageB, out var asJson, out var outPath))
                return;

            if (inputs.Count < 1)
            {
                ShowHelp();
                return;
            }

            var aPath = inputs[0];
            var bPath = inputs.Count > 1 ? inputs[1] : null;

            if (!File.Exists(aPath))
            {
                Console.WriteLine($"PDF nao encontrado: {aPath}");
                return;
            }
            if (!IsPdfFile(aPath))
            {
                Console.WriteLine($"Arquivo nao-PDF ignorado: {aPath}");
                return;
            }
            if (!string.IsNullOrWhiteSpace(bPath) && !File.Exists(bPath))
            {
                Console.WriteLine($"PDF nao encontrado: {bPath}");
                return;
            }
            if (!string.IsNullOrWhiteSpace(bPath) && !IsPdfFile(bPath))
            {
                Console.WriteLine($"Arquivo nao-PDF ignorado: {bPath}");
                return;
            }

            var result = RunPipeline(aPath, bPath, pageA, pageB);
            PrintSummary(result);

            if (asJson || !string.IsNullOrWhiteSpace(outPath))
            {
                if (string.IsNullOrWhiteSpace(outPath))
                {
                    Directory.CreateDirectory(DefaultOutDir);
                    outPath = Path.Combine(DefaultOutDir,
                        $"{Path.GetFileNameWithoutExtension(aPath)}__{Path.GetFileNameWithoutExtension(bPath)}.json");
                }

                var json = JsonSerializer.Serialize(result, JsonUtils.Indented);
                File.WriteAllText(outPath, json);
                Console.WriteLine($"Arquivo salvo: {outPath}");
            }
        }

        internal static void ExecuteDetectDocs(string[] args)
        {
            if (!ParseDetectDocsOptions(args, out var inputFile, out var includePages, out var asJson, out var docOnlyRaw))
                return;
            if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            {
                Console.WriteLine("PDF nao encontrado: " + inputFile);
                return;
            }

            var docOnly = NormalizeDocTypeHintForDetect(docOnlyRaw);
            var keywords = docOnly.Length > 0 ? GetKeywordsForDocType(docOnly) : GetKeywordsAll();

            var opts = DocumentValidationRules.BuildDefaultDetectionOptions(keywords);

            DetectionResult? det;
            try
            {
                det = DocumentTitleDetector.Detect(inputFile, opts);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Falha ao detectar: " + ex.Message);
                return;
            }

            var pages = det.Pages.OrderBy(p => p.Page).ToList();
            var segments = DetectAllDocs(inputFile);
            if (!string.IsNullOrWhiteSpace(docOnly))
                segments = segments.Where(s => string.Equals(NormalizeDocTypeHint(s.TitleKey), docOnly, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!asJson)
            {
                Console.WriteLine("Documentos detectados:");
                foreach (var s in segments)
                    Console.WriteLine($"- {s.TitleKey} p{s.StartPage}-{s.EndPage} obj={s.BodyObj} back={s.BackBodyObj}");
                if (includePages)
                {
                    Console.WriteLine();
                    Console.WriteLine("Paginas:");
                    foreach (var p in pages)
                    {
                        var key = ClassifyDocKey(p, out var method, out var matched);
                        Console.WriteLine($"p{p.Page} {key} ({method}) {matched}");
                    }
                }
                return;
            }

            var payload = new
            {
                Criteria = new
                {
                    Options = new
                    {
                        opts.PrefixOpCount,
                        opts.SuffixOpCount,
                        opts.CarryForward,
                        opts.TopBandPct,
                        opts.BottomBandPct,
                        opts.UseTopTextFallback
                    },
                    Keywords = opts.Keywords,
                    KeywordsByDoc = new
                    {
                        despacho = GetKeywordsForDocType(DocumentValidationRules.DocKeyDespacho),
                        requerimento = GetKeywordsForDocType(DocumentValidationRules.DocKeyRequerimentoHonorarios),
                        certidao = GetKeywordsForDocType(DocumentValidationRules.DocKeyCertidaoConselho)
                    },
                    Only = docOnly
                },
                Documents = segments.Select(s => new
                {
                    DocType = s.TitleKey,
                    s.Title,
                    s.StartPage,
                    s.EndPage,
                    s.BodyObj,
                    s.BackPage,
                    s.BackBodyObj,
                    s.BackSignatureObj,
                    s.PathRef,
                    s.Subtype,
                    s.SubtypeReason,
                    s.SubtypeHints,
                    s.CertidaoExpected,
                    s.SuppressedDocType,
                    s.SuppressedReason
                }).ToList(),
                Pages = includePages
                    ? pages.Select(p =>
                    {
                        var key = ClassifyDocKey(p, out var method, out var matched);
                        return new
                        {
                            p.Page,
                            DocKey = key,
                            Method = method,
                            Matched = matched,
                            p.TitleKey,
                            p.Title,
                            p.TopText,
                            p.BottomText,
                            p.BodyObj,
                            p.BodyPrefix,
                            p.BodySuffix
                        };
                    }).ToList()
                    : null
            };

            var json = JsonSerializer.Serialize(payload, JsonUtils.Indented);
            Console.WriteLine(json);
        }

        internal static List<DetectionSummary> DetectBestDocs(string pdfPath)
        {
            var all = DetectAllDocs(pdfPath);
            return SelectBestDocsPerType(pdfPath, all);
        }

        private static bool ParseDetectDocsOptions(string[] args, out string inputFile, out bool includePages, out bool asJson, out string docOnly)
        {
            inputFile = "";
            includePages = false;
            asJson = true;
            docOnly = "";
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--pages", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--per-page", StringComparison.OrdinalIgnoreCase))
                {
                    includePages = true;
                    continue;
                }
                if (string.Equals(arg, "--text", StringComparison.OrdinalIgnoreCase))
                {
                    asJson = false;
                    continue;
                }
                if ((string.Equals(arg, "--only", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--doc", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    docOnly = args[++i];
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(inputFile))
                {
                    inputFile = arg;
                    continue;
                }
                if (arg == "--help" || arg == "-h")
                {
                    ShowDetectDocsHelp();
                    return false;
                }
            }
            return true;
        }

        private static void ShowDetectDocsHelp()
        {
            Console.WriteLine("objects detectdocs --input <file.pdf> [--pages] [--text] [--only despacho|requerimento|certidao]");
            Console.WriteLine("  --pages     inclui classificacao por pagina (Title/Top/Bottom/Body)");
            Console.WriteLine("  --text      imprime texto simples ao inves de JSON");
        }

        private static string NormalizeDocTypeHintForDetect(string? raw)
        {
            return DocumentValidationRules.NormalizeDocKey(raw);
        }

        private static List<string> GetKeywordsAll()
        {
            return DocumentValidationRules.GetDetectionKeywordsAll(includeGenericHeaders: true, includeExtendedSignals: true).ToList();
        }

        private static List<string> GetKeywordsForDocType(string docType)
        {
            return DocumentValidationRules.GetDetectionKeywordsForDocExtended(docType).ToList();
        }

        private static PipelineResult RunPipeline(string aPath, string? bPath, int? pageA, int? pageB, DetectionSummary? detectionAOverride = null)
        {
            var pipelineOptions = LoadPipelineOptions();

            var result = new PipelineResult
            {
                PdfA = aPath,
                PdfB = bPath ?? ""
            };
            FrontBackResult? frontBackResult = null;

            if (detectionAOverride != null)
            {
                result.DetectionA = CloneDetection(detectionAOverride);
            }
            else
            {
                FillDetection(result.DetectionA, aPath, result.Errors, required: !pageA.HasValue);
            }

            var modelUsed = false;
            var modelDocHint = "";
            if (string.IsNullOrWhiteSpace(bPath))
            {
                var docHint = NormalizeDocTypeHint(result.DetectionA.TitleKey);
                if (string.IsNullOrWhiteSpace(docHint))
                {
                    result.Errors.Add("model_doc_type_not_found");
                    return result;
                }

                modelDocHint = docHint;
                var modelPath = ResolveModelPathForDoc(docHint);
                if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                {
                    result.Errors.Add($"model_not_found: {docHint}");
                    return result;
                }

                bPath = modelPath;
                result.PdfB = bPath;
                modelUsed = true;
            }

            if (detectionAOverride == null &&
                !pageA.HasValue &&
                !IsBookmarkPath(result.DetectionA.PathRef))
            {
                var docHint = NormalizeDocTypeHint(result.DetectionA.TitleKey);
                var best = SelectBestDocForType(aPath, docHint);
                if (best != null)
                    result.DetectionA = CloneDetection(best);
            }

            if (string.IsNullOrWhiteSpace(bPath))
            {
                result.Errors.Add("pdf_b_missing");
                return result;
            }
            if (!File.Exists(bPath))
            {
                result.Errors.Add($"pdf_b_not_found: {bPath}");
                return result;
            }

            var baseA = Path.GetFileNameWithoutExtension(aPath);
            var baseB = Path.GetFileNameWithoutExtension(bPath);
            var debugDir = Path.Combine("outputs", "objects_pipeline", $"{baseA}__{baseB}", "debug");

            WriteDebugJson(debugDir, "00_request.json", new
            {
                PdfA = aPath,
                PdfB = bPath,
                PageA = pageA,
                PageB = pageB,
                ModelUsed = modelUsed
            });
            WriteModuleJson(debugDir, "path", "input", "request.json", new
            {
                PdfA = aPath,
                PdfB = bPath,
                PageA = pageA,
                PageB = pageB,
                ModelUsed = modelUsed
            });
            WriteModuleFile(debugDir, "path", "input", "a.pdf", aPath);
            WriteModuleFile(debugDir, "path", "input", "b.pdf", bPath);
            WriteModuleJson(debugDir, "path", "output", "resolved.json", new
            {
                PdfA = aPath,
                PdfB = bPath,
                BaseA = baseA,
                BaseB = baseB,
                PageA = pageA,
                PageB = pageB,
                ModelUsed = modelUsed
            });

            FillDetection(result.DetectionB, bPath, result.Errors, required: !pageB.HasValue);
            if (modelUsed && !string.IsNullOrWhiteSpace(modelDocHint))
            {
                result.DetectionB.TitleKey = modelDocHint;
            }

            WriteDebugJson(debugDir, "01_detection_a.json", result.DetectionA);
            WriteDebugJson(debugDir, "01_detection_b.json", result.DetectionB);
            WriteModuleJson(debugDir, "detector", "input", "a.json", new { Pdf = aPath, Required = !pageA.HasValue });
            WriteModuleJson(debugDir, "detector", "input", "b.json", new { Pdf = bPath, Required = !pageB.HasValue });
            WriteModuleFile(debugDir, "detector", "input", "a.pdf", aPath);
            WriteModuleFile(debugDir, "detector", "input", "b.pdf", bPath);
            WriteModuleJson(debugDir, "detector", "output", "a.json", result.DetectionA);
            WriteModuleJson(debugDir, "detector", "output", "b.json", result.DetectionB);
            var allA = DetectAllDocs(aPath);
            var allB = DetectAllDocs(bPath);
            WriteModuleJson(debugDir, "detector", "output", "all_a.json", allA);
            WriteModuleJson(debugDir, "detector", "output", "all_b.json", allB);
            WriteModuleJson(debugDir, "detector", "output", "best_a.json", SelectBestDocsPerType(aPath, allA));
            WriteModuleJson(debugDir, "detector", "output", "best_b.json", SelectBestDocsPerType(bPath, allB));

            int startA = pageA ?? result.DetectionA.StartPage;
            int startB = pageB ?? result.DetectionB.StartPage;

            if (startA <= 0)
                result.Errors.Add("Pagina A nao encontrada pelo detector.");
            if (startB <= 0)
                result.Errors.Add("Pagina B nao encontrada pelo detector.");
            if (result.Errors.Count > 0)
            {
                WriteDebugJson(debugDir, "99_errors.json", result.Errors);
                return result;
            }

            result.HeaderFooter = BuildHeaderFooter(aPath, bPath, startA, startB);
            WriteDebugJson(debugDir, "01_header_footer.json", result.HeaderFooter);
            WriteModuleJson(debugDir, "header_footer", "input", "a.json", new { Pdf = aPath, Page = startA });
            WriteModuleJson(debugDir, "header_footer", "input", "b.json", new { Pdf = bPath, Page = startB });
            WriteModuleFile(debugDir, "header_footer", "input", "a.pdf", aPath);
            WriteModuleFile(debugDir, "header_footer", "input", "b.pdf", bPath);
            WriteModuleJson(debugDir, "header_footer", "output", "summary.json", result.HeaderFooter);

            var needCamins = ShouldRunCamins(allA, allB, result.DetectionA, result.DetectionB);
            if (needCamins && IsCaminsAvailable())
            {
                result.Camins = RunCaminsParallel(aPath, bPath, startA, startB, result.DetectionA, result.DetectionB);
                if (result.Camins != null)
                {
                    WriteDebugJson(debugDir, "01_camins.json", result.Camins);
                    WriteModuleJson(debugDir, "camins", "input", "request.json", new
                    {
                        PdfA = aPath,
                        PdfB = bPath,
                        PageA = startA,
                        PageB = startB,
                        DocTypeHint = result.Camins.DocTypeHint,
                        PageMap = result.Camins.PageMapPath
                    });
                    WriteModuleFile(debugDir, "camins", "input", "a.pdf", aPath);
                    WriteModuleFile(debugDir, "camins", "input", "b.pdf", bPath);
                    WriteModuleJson(debugDir, "camins", "output", "result.json", result.Camins);
                }
            }
            else
            {
                WriteModuleJson(debugDir, "camins", "input", "request.json", new
                {
                    Status = "skipped",
                    Reason = needCamins ? "python_not_available" : "no_ambiguity"
                });
                WriteModuleJson(debugDir, "camins", "output", "result.json", new
                {
                    Status = "skipped",
                    Reason = needCamins ? "python_not_available" : "no_ambiguity"
                });
            }

            var docTypeA = NormalizeDocTypeHint(result.DetectionA.TitleKey);
            var docTypeB = NormalizeDocTypeHint(result.DetectionB.TitleKey);
            var isDespacho =
                DocumentValidationRules.IsDocMatch(docTypeA, DocumentValidationRules.DocKeyDespacho) &&
                DocumentValidationRules.IsDocMatch(docTypeB, DocumentValidationRules.DocKeyDespacho);

            var opFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var defaults = ObjectsTextOpsDiff.LoadObjDefaults();
            if (defaults?.Ops != null)
            {
                foreach (var op in defaults.Ops)
                {
                    if (!string.IsNullOrWhiteSpace(op))
                        opFilter.Add(op.Trim());
                }
            }
            if (opFilter.Count == 0)
            {
                opFilter.Add("Tj");
                opFilter.Add("TJ");
            }

            if (isDespacho)
            {
                var frontAObj = result.DetectionA.BodyObj;
                var frontBObj = result.DetectionB.BodyObj;

                if (frontAObj <= 0) result.Errors.Add("frontA_stream_not_found");
                if (frontBObj <= 0) result.Errors.Add("frontB_stream_not_found");
                if (result.Errors.Count > 0)
                {
                    WriteDebugJson(debugDir, "99_errors.json", result.Errors);
                    return result;
                }

                var backPageA = result.DetectionA.BackPage;
                var backPageB = result.DetectionB.BackPage;
                var backAHit = new DetectionHit { PdfPath = aPath, Page = backPageA, Obj = result.DetectionA.BackBodyObj };
                var backBHit = new DetectionHit { PdfPath = bPath, Page = backPageB, Obj = result.DetectionB.BackBodyObj };
                var sigAHit = new DetectionHit { PdfPath = aPath, Page = backPageA, Obj = result.DetectionA.BackSignatureObj };
                var sigBHit = new DetectionHit { PdfPath = bPath, Page = backPageB, Obj = result.DetectionB.BackSignatureObj };

                frontBackResult = new FrontBackResult
                {
                    FrontA = BuildStreamInfoSimple(startA, frontAObj, "detector_body"),
                    FrontB = BuildStreamInfoSimple(startB, frontBObj, "detector_body"),
                    BackBodyA = backAHit.Obj > 0 ? BuildStreamInfoSimple(backPageA, backAHit.Obj, "detector_body") : BuildStreamInfoSimple(backPageA, 0, "back_tail_missing"),
                    BackBodyB = backBHit.Obj > 0 ? BuildStreamInfoSimple(backPageB, backBHit.Obj, "detector_body") : BuildStreamInfoSimple(backPageB, 0, "back_tail_missing"),
                    BackSignatureA = sigAHit.Obj > 0 ? BuildStreamInfoSimple(backPageA, sigAHit.Obj, "detector_footer") : BuildStreamInfoSimple(backPageA, 0, "signature_missing"),
                    BackSignatureB = sigBHit.Obj > 0 ? BuildStreamInfoSimple(backPageB, sigBHit.Obj, "detector_footer") : BuildStreamInfoSimple(backPageB, 0, "signature_missing")
                };

                WriteModuleJson(debugDir, "frontback", "input", "request.json", new { Status = "skipped", Reason = "detector_body_obj" });
                WriteModuleFile(debugDir, "frontback", "input", "a.pdf", aPath);
                WriteModuleFile(debugDir, "frontback", "input", "b.pdf", bPath);
                WriteModuleJson(debugDir, "frontback", "output", "result.json", frontBackResult);

                ObjectsTextOpsDiff.AlignRangeResult? align;
                if (backAHit.Obj <= 0 || backBHit.Obj <= 0)
                {
                    align = ObjectsTextOpsDiff.ComputeFrontAlignRangeForSelections(
                        aPath,
                        bPath,
                        opFilter,
                        new ObjectsTextOpsDiff.PageObjSelection { Page = startA, Obj = frontAObj },
                        new ObjectsTextOpsDiff.PageObjSelection { Page = startB, Obj = frontBObj },
                        DefaultBackoff,
                        backPageA,
                        backPageB);
                }
                else
                {
                    align = ObjectsTextOpsDiff.ComputeAlignRangesForSelections(
                        aPath,
                        bPath,
                        opFilter,
                        new ObjectsTextOpsDiff.PageObjSelection { Page = startA, Obj = frontAObj },
                        new ObjectsTextOpsDiff.PageObjSelection { Page = backPageA, Obj = backAHit.Obj },
                        new ObjectsTextOpsDiff.PageObjSelection { Page = startB, Obj = frontBObj },
                        new ObjectsTextOpsDiff.PageObjSelection { Page = backPageB, Obj = backBHit.Obj },
                        DefaultBackoff);
                }

                if (align == null)
                {
                    result.Errors.Add("alignrange_failed");
                    WriteDebugJson(debugDir, "99_errors.json", result.Errors);
                    return result;
                }

                result.AlignRange = new AlignRangeSummary
                {
                    FrontA = ToRangeValue(align.FrontA),
                    FrontB = ToRangeValue(align.FrontB),
                    BackA = ToRangeValue(align.BackA),
                    BackB = ToRangeValue(align.BackB)
                };

                // Requerimento: usar texto completo do stream do corpo (modelo do ofício do juízo)
                ExpandRequerimentoFullText(result.AlignRange, aPath, bPath, result.DetectionA, result.DetectionB);

                WriteDebugJson(debugDir, "03_alignrange.json", result.AlignRange);
                var textopsFront = ObjectsTextOpsDiff.ComputeAlignDebugForSelection(
                    aPath,
                    bPath,
                    new ObjectsTextOpsDiff.PageObjSelection { Page = startA, Obj = frontAObj },
                    new ObjectsTextOpsDiff.PageObjSelection { Page = startB, Obj = frontBObj },
                    opFilter,
                    DefaultBackoff,
                    "front_head");

                ObjectsTextOpsDiff.AlignDebugReport? textopsBack = null;
                if (backAHit.Obj > 0 && backBHit.Obj > 0 && backPageA > 0 && backPageB > 0)
                {
                    textopsBack = ObjectsTextOpsDiff.ComputeAlignDebugForSelection(
                        aPath,
                        bPath,
                        new ObjectsTextOpsDiff.PageObjSelection { Page = backPageA, Obj = backAHit.Obj },
                        new ObjectsTextOpsDiff.PageObjSelection { Page = backPageB, Obj = backBHit.Obj },
                        opFilter,
                        DefaultBackoff,
                        "back_tail");
                }

                WriteModuleJson(debugDir, "textops_align", "input", "selection.json", new
                {
                    FrontA = new { Page = startA, Obj = frontAObj },
                    FrontB = new { Page = startB, Obj = frontBObj },
                    BackA = new { Page = backPageA, Obj = backAHit.Obj },
                    BackB = new { Page = backPageB, Obj = backBHit.Obj },
                    OpFilter = opFilter.ToArray(),
                    Backoff = DefaultBackoff
                });
                WriteModuleFile(debugDir, "textops_align", "input", "a.pdf", aPath);
                WriteModuleFile(debugDir, "textops_align", "input", "b.pdf", bPath);
                WriteModuleJson(debugDir, "textops_align", "output", "result.json", new { front = textopsFront, back = textopsBack });
                WriteModuleJson(debugDir, "alignrange", "input", "selection.json", new
                {
                    FrontA = new { Page = startA, Obj = frontAObj },
                    FrontB = new { Page = startB, Obj = frontBObj },
                    BackA = new { Page = backPageA, Obj = backAHit.Obj },
                    BackB = new { Page = backPageB, Obj = backBHit.Obj },
                    OpFilter = opFilter.ToArray()
                });
                WriteModuleFile(debugDir, "alignrange", "input", "a.pdf", aPath);
                WriteModuleFile(debugDir, "alignrange", "input", "b.pdf", bPath);
                WriteModuleJson(debugDir, "alignrange", "output", "summary.json", result.AlignRange);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(docTypeA) || string.IsNullOrWhiteSpace(docTypeB))
                    result.Errors.Add("doc_type_not_found");
                else if (!string.Equals(docTypeA, docTypeB, StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add($"doc_type_mismatch: {docTypeA} vs {docTypeB}");
                else if (DocumentValidationRules.IsDocMatch(docTypeA, DocumentValidationRules.DocKeyDespacho))
                    result.Errors.Add("doc_type_invalid");

                if (result.Errors.Count > 0)
                {
                    WriteDebugJson(debugDir, "99_errors.json", result.Errors);
                    return result;
                }

                var frontA = BandTextExtractor.ExtractBandText(aPath, startA, 0.65, 1.0);
                var frontB = BandTextExtractor.ExtractBandText(bPath, startB, 0.65, 1.0);
                var backA = BandTextExtractor.ExtractBandText(aPath, startA, 0.0, 0.35);
                var backB = BandTextExtractor.ExtractBandText(bPath, startB, 0.0, 0.35);

                if (DocumentValidationRules.IsDocMatch(docTypeA, DocumentValidationRules.DocKeyRequerimentoHonorarios))
                {
                    var fullA = ExtractStreamTextByObjId(aPath, result.DetectionA.BodyObj);
                    var fullB = ExtractStreamTextByObjId(bPath, result.DetectionB.BodyObj);
                    if (!string.IsNullOrWhiteSpace(fullA))
                    {
                        frontA = fullA;
                        backA = "";
                    }
                    if (!string.IsNullOrWhiteSpace(fullB))
                    {
                        frontB = fullB;
                        backB = "";
                    }
                }

                result.AlignRange = new AlignRangeSummary
                {
                    FrontA = new RangeValue { Page = startA, StartOp = 0, EndOp = 0, ValueFull = frontA },
                    FrontB = new RangeValue { Page = startB, StartOp = 0, EndOp = 0, ValueFull = frontB },
                    BackA = new RangeValue { Page = startA, StartOp = 0, EndOp = 0, ValueFull = backA },
                    BackB = new RangeValue { Page = startB, StartOp = 0, EndOp = 0, ValueFull = backB }
                };

                WriteDebugJson(debugDir, "03_alignrange.json", result.AlignRange);
                WriteModuleJson(debugDir, "frontback", "input", "request.json", new
                {
                    Status = "skipped",
                    Reason = "non_despacho"
                });
                WriteModuleFile(debugDir, "frontback", "input", "a.pdf", aPath);
                WriteModuleFile(debugDir, "frontback", "input", "b.pdf", bPath);
                WriteModuleJson(debugDir, "frontback", "output", "result.json", new
                {
                    Status = "skipped",
                    Reason = "non_despacho"
                });
                WriteModuleJson(debugDir, "alignrange", "input", "selection.json", new
                {
                    Status = "synthetic",
                    Mode = "band_text"
                });
                WriteModuleFile(debugDir, "alignrange", "input", "a.pdf", aPath);
                WriteModuleFile(debugDir, "alignrange", "input", "b.pdf", bPath);
                WriteModuleJson(debugDir, "alignrange", "output", "summary.json", result.AlignRange);
            }

            result.DespachoSubtype = RunDespachoSubtype(result.AlignRange, result.DetectionA, result.DetectionB);
            if (result.DespachoSubtype != null)
            {
                WriteDebugJson(debugDir, "03_despacho_subtype.json", result.DespachoSubtype);
                WriteModuleJson(debugDir, "despacho_subtype", "input", "request.json", new
                {
                    PdfA = aPath,
                    PdfB = bPath,
                    DocTypeA = NormalizeDocTypeHint(result.DetectionA.TitleKey),
                    DocTypeB = NormalizeDocTypeHint(result.DetectionB.TitleKey),
                    FrontA = result.AlignRange.FrontA.ValueFull,
                    BackA = result.AlignRange.BackA.ValueFull,
                    FrontB = result.AlignRange.FrontB.ValueFull,
                    BackB = result.AlignRange.BackB.ValueFull
                });
                WriteModuleJson(debugDir, "despacho_subtype", "output", "result.json", result.DespachoSubtype);
            }

            result.FooterDate = RunFooterDate(aPath, bPath, result.DetectionA, result.DetectionB, result.HeaderFooter);
            if (result.FooterDate != null)
            {
                WriteDebugJson(debugDir, "03_footer_date.json", result.FooterDate);
                WriteModuleJson(debugDir, "footer_date", "input", "request.json", new
                {
                    PdfA = aPath,
                    PdfB = bPath,
                    BackSignatureObjA = result.DetectionA.BackSignatureObj,
                    BackSignatureObjB = result.DetectionB.BackSignatureObj
                });
                WriteModuleJson(debugDir, "footer_date", "output", "result.json", result.FooterDate);
            }

            result.MapFields = RunMapFields(aPath, bPath, result.AlignRange, result.DetectionA, result.DetectionB, frontBackResult);
            if (result.MapFields != null)
            {
                WriteModuleJson(debugDir, "mapfields", "input", "request.json", new
                {
                    AlignRangePath = result.MapFields.AlignRangePath,
                    MapPath = result.MapFields.MapPath,
                    BackMapPath = result.MapFields.BackMapPath,
                    Mode = result.MapFields.Mode,
                    FrontObjA = result.MapFields.FrontObjA,
                    FrontObjB = result.MapFields.FrontObjB,
                    BackObjA = result.MapFields.BackObjA,
                    BackObjB = result.MapFields.BackObjB,
                    DocTypeHintA = NormalizeDocTypeHint(result.DetectionA.TitleKey),
                    DocTypeHintB = NormalizeDocTypeHint(result.DetectionB.TitleKey)
                });
                WriteModuleFile(debugDir, "mapfields", "input", "alignrange.txt", result.MapFields.AlignRangePath);
                WriteModuleFile(debugDir, "mapfields", "input", "map.yml", result.MapFields.MapPath);
                if (!string.IsNullOrWhiteSpace(result.MapFields.BackMapPath))
                    WriteModuleFile(debugDir, "mapfields", "input", "back_map.yml", result.MapFields.BackMapPath);
                WriteDebugJson(debugDir, "03_mapfields.json", result.MapFields);
                WriteModuleJson(debugDir, "mapfields", "output", "summary.json", result.MapFields);
                result.MapFieldsData = TryLoadJson(result.MapFields.JsonPath);

                if (result.FooterDate != null)
                {
                    ApplyFooterDateToMapFields(result.MapFields.JsonPath, result.FooterDate, result.MapFields.Errors);
                    result.MapFieldsData = TryLoadJson(result.MapFields.JsonPath);
                }

                ApplyProfissaoToEspecialidade(result.MapFields.JsonPath, result.MapFields.Errors);
                result.MapFieldsData = TryLoadJson(result.MapFields.JsonPath);
            }

            if (pipelineOptions.NlpEnabled)
            {
                result.Nlp = RunNlp(aPath, bPath, result.AlignRange);
                WriteDebugText(debugDir, "04_nlp_input_front_head_a.txt", result.AlignRange.FrontA.ValueFull);
                WriteDebugText(debugDir, "04_nlp_input_front_head_b.txt", result.AlignRange.FrontB.ValueFull);
                WriteDebugText(debugDir, "04_nlp_input_back_tail_a.txt", result.AlignRange.BackA.ValueFull);
                WriteDebugText(debugDir, "04_nlp_input_back_tail_b.txt", result.AlignRange.BackB.ValueFull);
                WriteModuleText(debugDir, "nlp", "input", "front_head_a.txt", result.AlignRange.FrontA.ValueFull);
                WriteModuleText(debugDir, "nlp", "input", "front_head_b.txt", result.AlignRange.FrontB.ValueFull);
                WriteModuleText(debugDir, "nlp", "input", "back_tail_a.txt", result.AlignRange.BackA.ValueFull);
                WriteModuleText(debugDir, "nlp", "input", "back_tail_b.txt", result.AlignRange.BackB.ValueFull);
                if (result.Nlp != null)
                {
                    WriteDebugJson(debugDir, "04_nlp_output_front_head_a.json", result.Nlp.FrontA);
                    WriteDebugJson(debugDir, "04_nlp_output_front_head_b.json", result.Nlp.FrontB);
                    WriteDebugJson(debugDir, "04_nlp_output_back_tail_a.json", result.Nlp.BackA);
                    WriteDebugJson(debugDir, "04_nlp_output_back_tail_b.json", result.Nlp.BackB);
                    WriteModuleJson(debugDir, "nlp", "output", "front_head_a.json", result.Nlp.FrontA);
                    WriteModuleJson(debugDir, "nlp", "output", "front_head_b.json", result.Nlp.FrontB);
                    WriteModuleJson(debugDir, "nlp", "output", "back_tail_a.json", result.Nlp.BackA);
                    WriteModuleJson(debugDir, "nlp", "output", "back_tail_b.json", result.Nlp.BackB);

                    result.NlpData = CollectNlpData(result.Nlp);
                }
            }
            else
            {
                WriteModuleJson(debugDir, "nlp", "input", "request.json", new { Status = "skipped", Reason = "nlp_disabled" });
                WriteModuleJson(debugDir, "nlp", "output", "result.json", new { Status = "skipped", Reason = "nlp_disabled" });
            }

            if (pipelineOptions.FieldsEnabled)
            {
                result.Fields = RunFields(aPath, bPath, result.AlignRange, result.Nlp, result.DetectionA, result.DetectionB);
                if (result.Fields != null)
                {
                    WriteDebugJson(debugDir, "05_fields_output_front_head_a.json", result.Fields.FrontA);
                    WriteDebugJson(debugDir, "05_fields_output_front_head_b.json", result.Fields.FrontB);
                    WriteDebugJson(debugDir, "05_fields_output_back_tail_a.json", result.Fields.BackA);
                    WriteDebugJson(debugDir, "05_fields_output_back_tail_b.json", result.Fields.BackB);
                    WriteModuleJson(debugDir, "fields", "input", "front_head_a.json", new { Text = result.AlignRange.FrontA.ValueFull, Nlp = result.Nlp?.FrontA });
                    WriteModuleJson(debugDir, "fields", "input", "front_head_b.json", new { Text = result.AlignRange.FrontB.ValueFull, Nlp = result.Nlp?.FrontB });
                    WriteModuleJson(debugDir, "fields", "input", "back_tail_a.json", new { Text = result.AlignRange.BackA.ValueFull, Nlp = result.Nlp?.BackA });
                    WriteModuleJson(debugDir, "fields", "input", "back_tail_b.json", new { Text = result.AlignRange.BackB.ValueFull, Nlp = result.Nlp?.BackB });
                    WriteModuleJson(debugDir, "fields", "output", "front_head_a.json", result.Fields.FrontA);
                    WriteModuleJson(debugDir, "fields", "output", "front_head_b.json", result.Fields.FrontB);
                    WriteModuleJson(debugDir, "fields", "output", "back_tail_a.json", result.Fields.BackA);
                    WriteModuleJson(debugDir, "fields", "output", "back_tail_b.json", result.Fields.BackB);

                    result.FieldsData = CollectFieldsData(result.Fields);
                }
            }
            else
            {
                WriteModuleJson(debugDir, "fields", "input", "request.json", new { Status = "skipped", Reason = "fields_disabled" });
                WriteModuleJson(debugDir, "fields", "output", "result.json", new { Status = "skipped", Reason = "fields_disabled" });
            }

            if (pipelineOptions.HonorariosEnabled)
            {
                WriteModuleJson(debugDir, "honorarios", "input", "request.json", new
                {
                    MapFieldsJson = result.MapFields?.JsonPath ?? "",
                    ConfigPath = ResolveConfigPath()
                });
                result.Honorarios = HonorariosEnricher.Run(result.MapFields?.JsonPath, ResolveConfigPath());
                if (result.Honorarios != null)
                {
                    WriteDebugJson(debugDir, "06_honorarios.json", result.Honorarios);
                    WriteModuleJson(debugDir, "honorarios", "output", "result.json", result.Honorarios);
                    if (result.MapFields != null && !string.IsNullOrWhiteSpace(result.MapFields.JsonPath))
                    {
                        ApplyHonorariosToMapFields(result.MapFields.JsonPath, result.Honorarios, result.MapFields.Errors);
                        result.MapFieldsData = TryLoadJson(result.MapFields.JsonPath);
                    }
                }
            }
            else
            {
                WriteModuleJson(debugDir, "honorarios", "input", "request.json", new { Status = "skipped", Reason = "honorarios_disabled" });
                WriteModuleJson(debugDir, "honorarios", "output", "result.json", new { Status = "skipped", Reason = "honorarios_disabled" });
            }

            result.Consolidated = RunConsolidation(result.MapFields, result.DetectionA, result.DetectionB);
            if (result.Consolidated != null)
            {
                WriteModuleJson(debugDir, "consolidate", "input", "request.json", result.Consolidated.Inputs);
                if (!string.IsNullOrWhiteSpace(result.Consolidated.JsonPath))
                    WriteModuleFile(debugDir, "consolidate", "output", "consolidated.json", result.Consolidated.JsonPath);
                WriteModuleJson(debugDir, "consolidate", "output", "summary.json", result.Consolidated);
            }

            return result;
        }

        private static void FillDetection(DetectionSummary target, string pdfPath, List<string> errors, bool required)
        {
            var hit = DetectDoc(pdfPath, out var endPage);
            if (!hit.Found)
            {
                if (required)
                    errors.Add($"Detector nao encontrou despacho/diretoria especial em {Path.GetFileName(pdfPath)}.");
                return;
            }

            if (hit.Page > 0 && hit.Obj <= 0)
            {
                var requireMarker = DocumentValidationRules.IsDocMatch(hit.TitleKey, DocumentValidationRules.DocKeyDespacho);

                var picked = ContentsStreamPicker.Pick(new StreamPickRequest
                {
                    PdfPath = pdfPath,
                    Page = hit.Page,
                    RequireMarker = requireMarker
                });

                if (picked.Obj <= 0 && requireMarker)
                {
                    picked = ContentsStreamPicker.Pick(new StreamPickRequest
                    {
                        PdfPath = pdfPath,
                        Page = hit.Page,
                        RequireMarker = false
                    });
                }

                if (picked.Obj <= 0)
                {
                    if (required)
                        errors.Add($"Detector nao encontrou stream de texto em {Path.GetFileName(pdfPath)} (page {hit.Page}, reason={picked.Reason}).");
                    return;
                }

                hit.Obj = picked.Obj;
                if (string.IsNullOrWhiteSpace(hit.TitleKey))
                    hit.TitleKey = picked.TitleKey;
                if (!string.IsNullOrWhiteSpace(picked.PathRef))
                    hit.PathRef = picked.PathRef;
            }

            target.TitleKey = hit.TitleKey;
            target.Title = hit.Title;
            target.StartPage = hit.Page;
            target.EndPage = endPage > 0 ? endPage : hit.Page;
            target.BodyObj = hit.Obj;
            target.PathRef = hit.PathRef;

            if (DocumentValidationRules.IsDocMatch(hit.TitleKey, DocumentValidationRules.DocKeyDespacho) && hit.Page > 0)
            {
                var backPage = hit.Page + 1;
                var backBody = ContentsStreamPicker.Pick(new StreamPickRequest
                {
                    PdfPath = pdfPath,
                    Page = backPage,
                    RequireMarker = false
                });

                if (backBody.Obj > 0)
                {
                    target.BackPage = backPage;
                    target.BackBodyObj = backBody.Obj;
                }
                else
                {
                    target.BackPage = 0;
                    target.BackBodyObj = 0;
                }

                var backSig = ContentsStreamPicker.PickSecondary(new StreamPickRequest
                {
                    PdfPath = pdfPath,
                    Page = backPage,
                    RequireMarker = false
                });
                target.BackSignatureObj = backSig.Obj;
            }
        }

        private static DetectionSummary CloneDetection(DetectionSummary src)
        {
            return new DetectionSummary
            {
                TitleKey = src.TitleKey,
                Title = src.Title,
                StartPage = src.StartPage,
                EndPage = src.EndPage,
                BodyObj = src.BodyObj,
                BackPage = src.BackPage,
                BackBodyObj = src.BackBodyObj,
                BackSignatureObj = src.BackSignatureObj,
                PathRef = src.PathRef
            };
        }

        private static bool IsBookmarkPath(string? pathRef)
        {
            if (string.IsNullOrWhiteSpace(pathRef)) return false;
            return pathRef.StartsWith("bookmark/", StringComparison.OrdinalIgnoreCase);
        }

        private static RangeValue ToRangeValue(AlignRangeValue value)
        {
            return new RangeValue
            {
                Page = value.Page,
                StartOp = value.StartOp,
                EndOp = value.EndOp,
                ValueFull = value.ValueFull ?? ""
            };
        }

        private static RangeValue ToRangeValue(ObjectsTextOpsDiff.AlignRangeValue value)
        {
            return new RangeValue
            {
                Page = value.Page,
                StartOp = value.StartOp,
                EndOp = value.EndOp,
                ValueFull = value.ValueFull ?? ""
            };
        }

        private static void PrintSummary(PipelineResult result)
        {
            Console.WriteLine("OBJ PIPELINE");
            Console.WriteLine($"A: {Path.GetFileName(result.PdfA)}");
            Console.WriteLine($"B: {Path.GetFileName(result.PdfB)}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine("Erros:");
                foreach (var err in result.Errors)
                    Console.WriteLine($"- {err}");
                return;
            }

            Console.WriteLine("Detector A:");
            Console.WriteLine($"  title_key={result.DetectionA.TitleKey} pages={result.DetectionA.StartPage}-{result.DetectionA.EndPage}");
            Console.WriteLine($"  path={result.DetectionA.PathRef}");
            Console.WriteLine("Detector B:");
            Console.WriteLine($"  title_key={result.DetectionB.TitleKey} pages={result.DetectionB.StartPage}-{result.DetectionB.EndPage}");
            Console.WriteLine($"  path={result.DetectionB.PathRef}");

            if (result.Camins != null)
            {
                Console.WriteLine("CAMINS (parallel):");
                PrintCamins("simhash", result.Camins.Simhash);
                PrintCamins("tfidf", result.Camins.Tfidf);
                PrintCamins("kmeans", result.Camins.Kmeans);
                if (result.Camins.Errors.Count > 0)
                {
                    Console.WriteLine("CAMINS errors:");
                    foreach (var err in result.Camins.Errors)
                        Console.WriteLine($"- {err}");
                }
            }

            if (result.AlignRange == null)
                return;

            Console.WriteLine("AlignRange (front/back):");
            PrintRange("front_head A", result.AlignRange.FrontA);
            PrintRange("front_head B", result.AlignRange.FrontB);
            PrintRange("back_tail A", result.AlignRange.BackA);
            PrintRange("back_tail B", result.AlignRange.BackB);

            if (result.DespachoSubtype != null)
            {
                Console.WriteLine("DESPACHO SUBTYPE:");
                PrintSubtype("pdf_a", result.DespachoSubtype.PdfA);
                PrintSubtype("pdf_b", result.DespachoSubtype.PdfB);
                if (result.DespachoSubtype.Errors.Count > 0)
                {
                    Console.WriteLine("DESPACHO SUBTYPE errors:");
                    foreach (var err in result.DespachoSubtype.Errors)
                        Console.WriteLine($"- {err}");
                }
            }

            if (result.FooterDate != null)
            {
                Console.WriteLine("FOOTER DATE:");
                PrintFooterDate("pdf_a", result.FooterDate.PdfA);
                PrintFooterDate("pdf_b", result.FooterDate.PdfB);
                if (result.FooterDate.Errors.Count > 0)
                {
                    Console.WriteLine("FOOTER DATE errors:");
                    foreach (var err in result.FooterDate.Errors)
                        Console.WriteLine($"- {err}");
                }
            }

            if (result.Nlp != null)
            {
                Console.WriteLine("NLP (typed):");
                PrintNlp("front_head A", result.Nlp.FrontA);
                PrintNlp("front_head B", result.Nlp.FrontB);
                PrintNlp("back_tail A", result.Nlp.BackA);
                PrintNlp("back_tail B", result.Nlp.BackB);
                if (result.Nlp.Errors.Count > 0)
                {
                    Console.WriteLine("NLP errors:");
                    foreach (var err in result.Nlp.Errors)
                        Console.WriteLine($"- {err}");
                }
            }

            if (result.Fields != null)
            {
                Console.WriteLine("FIELDS (typed):");
                PrintFields("front_head A", result.Fields.FrontA);
                PrintFields("front_head B", result.Fields.FrontB);
                PrintFields("back_tail A", result.Fields.BackA);
                PrintFields("back_tail B", result.Fields.BackB);
                if (result.Fields.Errors.Count > 0)
                {
                    Console.WriteLine("FIELDS errors:");
                    foreach (var err in result.Fields.Errors)
                    Console.WriteLine($"- {err}");
                }
            }

            if (result.MapFields != null)
            {
                Console.WriteLine("MAPFIELDS (yaml):");
                var status = string.IsNullOrWhiteSpace(result.MapFields.JsonPath) ? "skip" : "ok";
                Console.WriteLine($"  status: {status}");
                if (!string.IsNullOrWhiteSpace(result.MapFields.Mode))
                    Console.WriteLine($"  mode: {result.MapFields.Mode}");
                if (!string.IsNullOrWhiteSpace(result.MapFields.MapPath))
                    Console.WriteLine($"  map: {result.MapFields.MapPath}");
                if (!string.IsNullOrWhiteSpace(result.MapFields.BackMapPath))
                    Console.WriteLine($"  back_map: {result.MapFields.BackMapPath}");
                if (!string.IsNullOrWhiteSpace(result.MapFields.JsonPath))
                    Console.WriteLine($"  json: {result.MapFields.JsonPath}");
                // rejects file disabled (was listing empty fields, not regex)
                if (result.MapFields.FrontObjA > 0 || result.MapFields.FrontObjB > 0)
                    Console.WriteLine($"  front_obj: A={result.MapFields.FrontObjA} B={result.MapFields.FrontObjB}");
                if (result.MapFields.BackObjA > 0 || result.MapFields.BackObjB > 0)
                    Console.WriteLine($"  back_obj: A={result.MapFields.BackObjA} B={result.MapFields.BackObjB}");
                if (result.MapFields.Errors.Count > 0)
                {
                    Console.WriteLine("MAPFIELDS errors:");
                    foreach (var err in result.MapFields.Errors)
                        Console.WriteLine($"- {err}");
                }
            }

            if (result.Consolidated != null)
            {
                Console.WriteLine("CONSOLIDATED:");
                var status = string.IsNullOrWhiteSpace(result.Consolidated.JsonPath) ? "skip" : "ok";
                Console.WriteLine($"  status: {status}");
                if (!string.IsNullOrWhiteSpace(result.Consolidated.JsonPath))
                    Console.WriteLine($"  json: {result.Consolidated.JsonPath}");
                if (result.Consolidated.Inputs.Count > 0)
                    Console.WriteLine($"  inputs: {string.Join(" | ", result.Consolidated.Inputs.Select(kv => kv.Key + "=" + Path.GetFileName(kv.Value)))}");
                if (result.Consolidated.Errors.Count > 0)
                {
                    Console.WriteLine("CONSOLIDATED errors:");
                    foreach (var err in result.Consolidated.Errors)
                        Console.WriteLine($"- {err}");
                }
            }

            if (result.Honorarios != null)
            {
                Console.WriteLine("HONORARIOS (computed):");
                PrintHonorarios("pdf_a", result.Honorarios.PdfA);
                PrintHonorarios("pdf_b", result.Honorarios.PdfB);
                if (result.Honorarios.Errors.Count > 0)
                {
                    Console.WriteLine("HONORARIOS errors:");
                    foreach (var err in result.Honorarios.Errors)
                        Console.WriteLine($"- {err}");
                }
            }
        }

        private static void PrintRange(string label, RangeValue value)
        {
            var range = FormatOpRange(value.StartOp, value.EndOp);
            Console.WriteLine($"  {label}: page={value.Page} range={range}");
            Console.WriteLine($"    value_full: \"{EscapeValue(value.ValueFull)}\"");
        }

        private static void PrintNlp(string label, NlpSegment? seg)
        {
            if (seg == null)
            {
                Console.WriteLine($"  {label}: (skip)");
                return;
            }
            var status = string.IsNullOrWhiteSpace(seg.Status) ? "unknown" : seg.Status;
            Console.WriteLine($"  {label}: {status}");
            if (!string.IsNullOrWhiteSpace(seg.TypedPath))
                Console.WriteLine($"    typed: {seg.TypedPath}");
            if (!string.IsNullOrWhiteSpace(seg.NlpJsonPath))
                Console.WriteLine($"    nlp: {seg.NlpJsonPath}");
            if (!string.IsNullOrWhiteSpace(seg.Error))
                Console.WriteLine($"    error: {seg.Error}");
        }

        private static void PrintFields(string label, FieldsSegment? seg)
        {
            if (seg == null)
            {
                Console.WriteLine($"  {label}: (skip)");
                return;
            }
            var status = string.IsNullOrWhiteSpace(seg.Status) ? "unknown" : seg.Status;
            Console.WriteLine($"  {label}: {status} (count={seg.Count})");
            if (!string.IsNullOrWhiteSpace(seg.JsonPath))
                Console.WriteLine($"    json: {seg.JsonPath}");
            if (!string.IsNullOrWhiteSpace(seg.Error))
                Console.WriteLine($"    error: {seg.Error}");
        }

        private static void PrintHonorarios(string label, HonorariosSide? side)
        {
            if (side == null)
            {
                Console.WriteLine($"  {label}: (skip)");
                return;
            }
            var status = string.IsNullOrWhiteSpace(side.Status) ? "unknown" : side.Status;
            Console.WriteLine($"  {label}: {status} ({side.Source})");
            if (!string.IsNullOrWhiteSpace(side.Especialidade))
                Console.WriteLine($"    especialidade: {side.Especialidade} ({side.EspecialidadeSource})");
            if (!string.IsNullOrWhiteSpace(side.ValorNormalized))
                Console.WriteLine($"    valor_base: {side.ValorNormalized} ({side.ValorField})");
            if (!string.IsNullOrWhiteSpace(side.EspecieDaPericia))
                Console.WriteLine($"    especie: {side.EspecieDaPericia}");
            if (!string.IsNullOrWhiteSpace(side.ValorTabeladoAnexoI))
                Console.WriteLine($"    valor_tabelado: {side.ValorTabeladoAnexoI}");
            if (!string.IsNullOrWhiteSpace(side.Fator))
                Console.WriteLine($"    fator: {side.Fator}");
        }

        private static void PrintSubtype(string label, SubtypeSide? side)
        {
            if (side == null)
            {
                Console.WriteLine($"  {label}: (skip)");
                return;
            }
            var status = string.IsNullOrWhiteSpace(side.Status) ? "unknown" : side.Status;
            var subtype = string.IsNullOrWhiteSpace(side.Subtype) ? "-" : side.Subtype;
            Console.WriteLine($"  {label}: {status} ({subtype})");
            if (!string.IsNullOrWhiteSpace(side.Reason))
                Console.WriteLine($"    reason: {side.Reason}");
            if (side.Hints != null && side.Hints.Count > 0)
                Console.WriteLine($"    hints: {string.Join(" | ", side.Hints)}");
        }

        private static void PrintFooterDate(string label, FooterDateSide? side)
        {
            if (side == null)
            {
                Console.WriteLine($"  {label}: (skip)");
                return;
            }
            var status = string.IsNullOrWhiteSpace(side.Status) ? "unknown" : side.Status;
            Console.WriteLine($"  {label}: {status}");
            if (!string.IsNullOrWhiteSpace(side.DateText))
                Console.WriteLine($"    date: {side.DateText}");
            if (!string.IsNullOrWhiteSpace(side.DateIso))
                Console.WriteLine($"    iso: {side.DateIso}");
            if (!string.IsNullOrWhiteSpace(side.Reason))
                Console.WriteLine($"    reason: {side.Reason}");
            if (!string.IsNullOrWhiteSpace(side.Source))
                Console.WriteLine($"    source: {side.Source}");
            if (side.Obj > 0)
                Console.WriteLine($"    obj: {side.Obj}");
            if (!string.IsNullOrWhiteSpace(side.TextSample))
                Console.WriteLine($"    sample: \"{EscapeValue(side.TextSample)}\"");
        }

        private static void PrintCamins(string label, CaminsAlgoResult? algo)
        {
            if (algo == null)
            {
                Console.WriteLine($"  {label}: (skip)");
                return;
            }
            if (!string.IsNullOrWhiteSpace(algo.Error))
            {
                Console.WriteLine($"  {label}: error ({algo.Error})");
                return;
            }
            Console.WriteLine($"  {label}: ok");
            PrintCaminsRow("pdf_a", algo.PdfA);
            PrintCaminsRow("pdf_b", algo.PdfB);
        }

        private static void PrintCaminsRow(string label, Dictionary<string, string>? row)
        {
            if (row == null || row.Count == 0)
            {
                Console.WriteLine($"    {label}: (not found)");
                return;
            }
            var docSubtype = GetCaminsValue(row, "doc_subtype");
            var guards = GetCaminsValue(row, "guards");
            var simhash = GetCaminsValue(row, "simhash_sim");
            var tfidf = GetCaminsValue(row, "tfidf_sim");
            var cluster = GetCaminsValue(row, "cluster");
            var clusterLabel = GetCaminsValue(row, "cluster_label");

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(docSubtype))
                parts.Add($"subtype={docSubtype}");
            if (!string.IsNullOrWhiteSpace(simhash))
                parts.Add($"simhash={simhash}");
            if (!string.IsNullOrWhiteSpace(tfidf))
                parts.Add($"tfidf={tfidf}");
            if (!string.IsNullOrWhiteSpace(cluster))
                parts.Add($"cluster={cluster}");
            if (!string.IsNullOrWhiteSpace(clusterLabel))
                parts.Add($"cluster_label={clusterLabel}");
            if (!string.IsNullOrWhiteSpace(guards))
                parts.Add($"guards={guards}");
            var line = parts.Count == 0 ? "(ok)" : string.Join(" ", parts);
            Console.WriteLine($"    {label}: {line}");
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
            return normalized.Replace("\"", "\\\"");
        }

        private static bool IsPdfFile(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                Span<byte> header = stackalloc byte[5];
                if (fs.Read(header) < 5)
                    return false;
                return header[0] == (byte)'%' &&
                       header[1] == (byte)'P' &&
                       header[2] == (byte)'D' &&
                       header[3] == (byte)'F' &&
                       header[4] == (byte)'-';
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeDocTypeHint(string? titleKey)
        {
            return DocumentValidationRules.NormalizeDocKey(titleKey);
        }

        private static DespachoSubtypeSummary RunDespachoSubtype(AlignRangeSummary? align, DetectionSummary detA, DetectionSummary detB)
        {
            var summary = new DespachoSubtypeSummary();
            if (align == null)
                return summary;

            var cfgPath = ResolveConfigPath();
            if (string.IsNullOrWhiteSpace(cfgPath) || !File.Exists(cfgPath))
            {
                summary.Errors.Add("config_not_found");
                return summary;
            }

            TjpbDespachoConfig cfg;
            try
            {
                cfg = TjpbDespachoConfig.Load(cfgPath);
            }
            catch (Exception ex)
            {
                summary.Errors.Add("config_error: " + ex.Message);
                return summary;
            }

            var docTypeA = NormalizeDocTypeHint(detA.TitleKey);
            var docTypeB = NormalizeDocTypeHint(detB.TitleKey);

            summary.PdfA = ClassifyDespachoSubtypeSide(align.FrontA.ValueFull, align.BackA.ValueFull, docTypeA, cfg);
            summary.PdfB = ClassifyDespachoSubtypeSide(align.FrontB.ValueFull, align.BackB.ValueFull, docTypeB, cfg);
            return summary;
        }

        private static SubtypeSide ClassifyDespachoSubtypeSide(string front, string back, string docType, TjpbDespachoConfig cfg)
        {
            var side = new SubtypeSide();
            if (!DocumentValidationRules.IsDocMatch(docType, DocumentValidationRules.DocKeyDespacho))
            {
                side.Status = "skip_non_despacho";
                return side;
            }

            if (string.IsNullOrWhiteSpace(back))
            {
                side.Status = "no_back_text";
                return side;
            }

            var raw = back;
            var norm = TextUtils.NormalizeForMatch(raw);
            var hints = cfg?.DespachoType ?? new DespachoTypeConfig();

            if (TryFindHint(norm, hints.ConselhoHints, out var hit))
            {
                side.Status = "ok";
                side.Subtype = "despacho_conselho";
                side.Reason = "conselho_hint";
                side.Hints.Add(hit);
                return side;
            }

            if (TryFindHint(norm, hints.GeorcHints, out hit))
            {
                side.Status = "ok";
                side.Subtype = "despacho_georc";
                side.Reason = "georc_hint";
                side.Hints.Add(hit);
                return side;
            }

            if (TryFindHint(norm, hints.AutorizacaoHints, out hit))
            {
                side.Status = "ok";
                side.Subtype = "despacho_autorizacao";
                side.Reason = "autorizacao_hint";
                side.Hints.Add(hit);
                return side;
            }

            if (TryMatchPattern(raw, hints.DeValuePatterns, out var pat))
            {
                side.Status = "ok";
                side.Subtype = "despacho_georc";
                side.Reason = "de_value_pattern";
                side.Hints.Add(pat);
                return side;
            }

            side.Status = "ok";
            side.Subtype = "despacho_outros";
            side.Reason = "no_hint";
            return side;
        }

        private static bool TryFindHint(string norm, List<string> hints, out string hit)
        {
            hit = "";
            if (string.IsNullOrWhiteSpace(norm) || hints == null || hints.Count == 0)
                return false;
            foreach (var h in hints)
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                var hn = TextUtils.NormalizeForMatch(h);
                if (string.IsNullOrWhiteSpace(hn)) continue;
                if (norm.Contains(hn, StringComparison.Ordinal))
                {
                    hit = h;
                    return true;
                }
            }
            return false;
        }

        private static bool TryMatchPattern(string raw, List<string> patterns, out string hit)
        {
            hit = "";
            if (string.IsNullOrWhiteSpace(raw) || patterns == null || patterns.Count == 0)
                return false;
            foreach (var p in patterns)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    if (Regex.IsMatch(raw, p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        hit = p;
                        return true;
                    }
                }
                catch
                {
                    // ignore invalid patterns
                }
            }
            return false;
        }

        private static FooterDateSummary RunFooterDate(string aPath, string bPath, DetectionSummary detA, DetectionSummary detB, HeaderFooterSummary? headerFooter)
        {
            var pickedA = PickFooterObj(detA, headerFooter?.BackA, out var sourceA);
            var pickedB = PickFooterObj(detB, headerFooter?.BackB, out var sourceB);
            var summary = new FooterDateSummary
            {
                PdfA = ExtractFooterDateSide(aPath, pickedA, sourceA, detA.BackPage),
                PdfB = ExtractFooterDateSide(bPath, pickedB, sourceB, detB.BackPage)
            };

            if (summary.PdfA != null && summary.PdfA.Status.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                summary.Errors.Add("pdf_a_error");
            if (summary.PdfB != null && summary.PdfB.Status.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                summary.Errors.Add("pdf_b_error");

            return summary;
        }

        private static int PickFooterObj(DetectionSummary det, HeaderFooterPageInfo? footer, out string source)
        {
            source = "";
            var sigObj = det.BackSignatureObj;
            var bodyObj = det.BackBodyObj;
            var footerObj = footer?.FooterObj ?? 0;

            if (sigObj > 0 && sigObj != bodyObj)
            {
                source = "signature_obj";
                return sigObj;
            }

            if (footerObj > 0 && footerObj != bodyObj)
            {
                source = "header_footer_footer";
                return footerObj;
            }

            if (sigObj > 0)
            {
                source = sigObj == bodyObj ? "signature_equals_body" : "signature_obj";
                return sigObj;
            }

            if (footerObj > 0)
            {
                source = footerObj == bodyObj ? "footer_equals_body" : "header_footer_footer";
                return footerObj;
            }

            source = "no_footer_obj";
            return 0;
        }

        private static FooterDateSide ExtractFooterDateSide(string pdfPath, int objId, string source, int page)
        {
            var side = new FooterDateSide { Obj = objId };
            side.Source = source;
            if (page > 0)
                side.PathRef = $"page={page}/obj={objId}";
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                side.Status = "error_pdf_not_found";
                return side;
            }
            if (objId <= 0)
            {
                side.Status = "no_signature_obj";
                return side;
            }

            var text = ExtractStreamTextByObjId(pdfPath, objId);
            if (string.IsNullOrWhiteSpace(text))
            {
                side.Status = "no_signature_text";
                return side;
            }
            side.TextSample = text.Length > 200 ? text.Substring(0, 200) : text;

            var match = Regex.Match(text, @"\b(\d{1,2}\s+de\s+[A-Za-zçãáàéêíóôõú]+?\s+de\s+\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                side.Status = "date_not_found";
                return side;
            }

            var dateText = match.Groups[1].Value.Trim();
            side.DateText = dateText;
            if (TextUtils.TryParseDate(dateText, out var iso))
                side.DateIso = iso;

            var norm = TextUtils.NormalizeForMatch(text);
            side.Reason = norm.Contains("documento assinado") || norm.Contains("assinado eletronicamente")
                ? "signature_footer"
                : "footer_text";
            side.Status = "ok";
            return side;
        }

        private static void ApplyFooterDateToMapFields(string mapFieldsPath, FooterDateSummary footer, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(mapFieldsPath) || !File.Exists(mapFieldsPath))
            {
                errors?.Add("mapfields_not_found_for_footer");
                return;
            }

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(mapFieldsPath)) as JsonObject;
                if (root == null)
                {
                    errors?.Add("mapfields_parse_error");
                    return;
                }

                ApplyFooterDateSide(root, "pdf_a", footer.PdfA);
                ApplyFooterDateSide(root, "pdf_b", footer.PdfB);

                File.WriteAllText(mapFieldsPath, root.ToJsonString(JsonUtils.Indented));
            }
            catch (Exception ex)
            {
                errors?.Add("mapfields_footer_error: " + ex.Message);
            }
        }

        private static void ApplyFooterDateSide(JsonObject root, string sideKey, FooterDateSide? footer)
        {
            if (footer == null || string.IsNullOrWhiteSpace(footer.DateText))
                return;

            if (root[sideKey] is not JsonObject sideObj)
                return;

            if (sideObj["DATA_ARBITRADO_FINAL"] is JsonObject existing)
            {
                var current = existing["Value"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(current))
                    return;
            }

            var field = new ObjFieldValue
            {
                Value = footer.DateText,
                ValueFull = footer.DateText,
                ValueRaw = footer.DateText,
                Status = "ok",
                OpRange = "",
                Source = "footer_signature",
                Obj = footer.Obj,
                BBox = null
            };

            sideObj["DATA_ARBITRADO_FINAL"] = JsonSerializer.SerializeToNode(field, JsonUtils.Compact);
        }

        private static void ApplyProfissaoToEspecialidade(string mapFieldsPath, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(mapFieldsPath) || !File.Exists(mapFieldsPath))
                return;

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(mapFieldsPath)) as JsonObject;
                if (root == null)
                {
                    errors?.Add("mapfields_parse_error");
                    return;
                }

                ApplyProfissaoToEspecialidadeSide(root, "pdf_a");
                ApplyProfissaoToEspecialidadeSide(root, "pdf_b");

                File.WriteAllText(mapFieldsPath, root.ToJsonString(JsonUtils.Indented));
            }
            catch (Exception ex)
            {
                errors?.Add("profissao_to_especialidade_error: " + ex.Message);
            }
        }

        private static void ApplyProfissaoToEspecialidadeSide(JsonObject root, string sideKey)
        {
            if (root[sideKey] is not JsonObject sideObj)
                return;

            var esp = sideObj["ESPECIALIDADE"] as JsonObject;
            var espValue = esp?["Value"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(espValue))
                return;

            var prof = sideObj["PROFISSÃO"] as JsonObject ?? sideObj["PROFISSAO"] as JsonObject;
            var profValue = prof?["Value"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(profValue))
                return;

            var field = new ObjFieldValue
            {
                Value = profValue,
                ValueFull = profValue,
                ValueRaw = profValue,
                Status = "OK",
                OpRange = "",
                Source = "profissao_equivalente",
                Obj = GetFieldObj(prof),
                BBox = null
            };

            sideObj["ESPECIALIDADE"] = JsonSerializer.SerializeToNode(field, JsonUtils.Compact);
        }

        private static void ApplyHonorariosToMapFields(string mapFieldsPath, HonorariosSummary honorarios, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(mapFieldsPath) || !File.Exists(mapFieldsPath))
                return;
            if (honorarios == null)
                return;

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(mapFieldsPath)) as JsonObject;
                if (root == null)
                {
                    errors?.Add("mapfields_parse_error");
                    return;
                }

                ApplyHonorariosSide(root, "pdf_a", honorarios.PdfA);
                ApplyHonorariosSide(root, "pdf_b", honorarios.PdfB);

                File.WriteAllText(mapFieldsPath, root.ToJsonString(JsonUtils.Indented));
            }
            catch (Exception ex)
            {
                errors?.Add("honorarios_apply_error: " + ex.Message);
            }
        }

        private static void ApplyHonorariosSide(JsonObject root, string sideKey, HonorariosSide? side)
        {
            if (root[sideKey] is not JsonObject sideObj)
                return;
            if (side == null || !string.Equals(side.Status, "ok", StringComparison.OrdinalIgnoreCase))
                return;

            SetFieldIfEmpty(sideObj, "ESPECIALIDADE", side.Especialidade, "honorarios");
            SetFieldIfEmpty(sideObj, "ESPECIE_DA_PERICIA", side.EspecieDaPericia, "honorarios");
            SetFieldIfEmpty(sideObj, "FATOR", side.Fator, "honorarios");
            SetFieldIfEmpty(sideObj, "VALOR_TABELADO_ANEXO_I", side.ValorTabeladoAnexoI, "honorarios");
        }

        private static void SetFieldIfEmpty(JsonObject sideObj, string fieldName, string value, string source)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var existing = sideObj[fieldName] as JsonObject;
            var existingValue = existing?["Value"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(existingValue))
                return;

            var field = new ObjFieldValue
            {
                Value = value,
                ValueFull = value,
                ValueRaw = value,
                Status = "OK",
                OpRange = "",
                Source = source,
                Obj = GetFieldObj(existing),
                BBox = null
            };

            sideObj[fieldName] = JsonSerializer.SerializeToNode(field, JsonUtils.Compact);
        }

        private static int GetFieldObj(JsonObject? field)
        {
            if (field == null) return 0;
            var obj = field["Obj"]?.ToString() ?? "";
            return int.TryParse(obj, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private static bool IsCaminsAvailable()
        {
            var python = ResolveCaminsPython();
            if (string.IsNullOrWhiteSpace(python))
                return false;

            if (File.Exists(python))
                return true;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null)
                    return false;
                p.WaitForExit(1500);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool ShouldRunCamins(List<DetectionSummary> allA, List<DetectionSummary> allB, DetectionSummary detA, DetectionSummary detB)
        {
            var typeA = NormalizeDocTypeHint(detA.TitleKey);
            var typeB = NormalizeDocTypeHint(detB.TitleKey);
            return CountCandidates(allA, typeA) > 1 || CountCandidates(allB, typeB) > 1;
        }

        private static int CountCandidates(List<DetectionSummary> all, string type)
        {
            if (all == null || all.Count == 0 || string.IsNullOrWhiteSpace(type))
                return 0;
            return all.Count(d => NormalizeDocTypeHint(d.TitleKey) == type);
        }

        private static void ExpandRequerimentoFullText(AlignRangeSummary align, string aPath, string bPath, DetectionSummary detA, DetectionSummary detB)
        {
            if (align == null)
                return;

            var fullA = ExtractStreamTextByObjId(aPath, detA.BodyObj);
            if (!string.IsNullOrWhiteSpace(fullA) && fullA.Length > align.FrontA.ValueFull.Length + 20)
            {
                align.FrontA.ValueFull = fullA;
                align.BackA.ValueFull = "";
                align.BackA.StartOp = 0;
                align.BackA.EndOp = 0;
            }

            var fullB = ExtractStreamTextByObjId(bPath, detB.BodyObj);
            if (!string.IsNullOrWhiteSpace(fullB) && fullB.Length > align.FrontB.ValueFull.Length + 20)
            {
                align.FrontB.ValueFull = fullB;
                align.BackB.ValueFull = "";
                align.BackB.StartOp = 0;
                align.BackB.EndOp = 0;
            }
        }

        private static string ExtractStreamTextByObjId(string pdfPath, int objId)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || objId <= 0 || !File.Exists(pdfPath))
                return "";

            using var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfReader(pdfPath));
            var found = FindStreamAndResourcesByObjId(doc, objId);
            if (found.Stream == null)
                return "";

            var resources = found.Resources ?? new iText.Kernel.Pdf.PdfResources(new iText.Kernel.Pdf.PdfDictionary());
            if (PdfTextExtraction.TryFindResourcesForObjId(doc, objId, out var betterResources) && betterResources != null)
                resources = betterResources;
            var parts = PdfTextExtraction.CollectTextOperatorTexts(found.Stream, resources);
            var joined = string.Join(" ", parts);
            joined = TextUtils.FixMissingSpaces(joined);
            return TextUtils.NormalizeWhitespace(joined);
        }

        private static (iText.Kernel.Pdf.PdfStream? Stream, iText.Kernel.Pdf.PdfResources? Resources) FindStreamAndResourcesByObjId(
            iText.Kernel.Pdf.PdfDocument doc,
            int objId)
        {
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources() ?? new iText.Kernel.Pdf.PdfResources(new iText.Kernel.Pdf.PdfDictionary());
                var contents = page.GetPdfObject().Get(iText.Kernel.Pdf.PdfName.Contents);
                foreach (var s in EnumerateStreams(contents))
                {
                    int id = s.GetIndirectReference()?.GetObjNumber() ?? 0;
                    if (id == objId)
                        return (s, resources);
                }
            }
            return (null, null);
        }

        private static IEnumerable<iText.Kernel.Pdf.PdfStream> EnumerateStreams(iText.Kernel.Pdf.PdfObject? obj)
        {
            if (obj == null) yield break;
            if (obj is iText.Kernel.Pdf.PdfStream s)
            {
                yield return s;
                yield break;
            }
            if (obj is iText.Kernel.Pdf.PdfArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is iText.Kernel.Pdf.PdfStream ss)
                        yield return ss;
                }
                yield break;
            }
            if (obj is iText.Kernel.Pdf.PdfIndirectReference ir && ir.GetRefersTo() is iText.Kernel.Pdf.PdfObject deref)
            {
                foreach (var s2 in EnumerateStreams(deref))
                    yield return s2;
            }
        }

        private static CaminsSummary RunCaminsParallel(string aPath, string bPath, int pageA, int pageB, DetectionSummary detA, DetectionSummary detB)
        {
            var summary = new CaminsSummary();
            var baseA = Path.GetFileNameWithoutExtension(aPath);
            var baseB = Path.GetFileNameWithoutExtension(bPath);
            var outDir = Path.Combine("outputs", "objects_pipeline", $"{baseA}__{baseB}", "camins");
            Directory.CreateDirectory(outDir);

            var docHint = NormalizeDocTypeHint(detA.TitleKey);
            if (string.IsNullOrWhiteSpace(docHint))
                docHint = NormalizeDocTypeHint(detB.TitleKey);
            if (string.IsNullOrWhiteSpace(docHint))
                docHint = DocumentValidationRules.DocKeyDespacho;
            summary.DocTypeHint = docHint;

            var pageMap = ResolvePageDocMapPath();
            summary.PageMapPath = pageMap;
            if (string.IsNullOrWhiteSpace(pageMap) || !File.Exists(pageMap))
            {
                summary.Errors.Add("camins_page_map_not_found");
                return summary;
            }

            var python = ResolveCaminsPython();
            if (string.IsNullOrWhiteSpace(python))
            {
                summary.Errors.Add("camins_python_not_found");
                return summary;
            }

            var pdfNameA = Path.GetFileName(aPath);
            var pdfNameB = Path.GetFileName(bPath);

            summary.Simhash = RunCaminsScript(
                "simhash",
                python,
                Path.Combine("tools", "camins_detector", "simhash_similarity.py"),
                outDir,
                pageMap,
                docHint,
                pdfNameA,
                pageA,
                pdfNameB,
                pageB,
                null,
                summary.Errors);

            summary.Tfidf = RunCaminsScript(
                "tfidf",
                python,
                Path.Combine("tools", "camins_detector", "tfidf_similarity.py"),
                outDir,
                pageMap,
                docHint,
                pdfNameA,
                pageA,
                pdfNameB,
                pageB,
                null,
                summary.Errors);

            var enableKmeans = string.Equals(Environment.GetEnvironmentVariable("CAMINS_ENABLE_KMEANS"), "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Environment.GetEnvironmentVariable("CAMINS_ENABLE_KMEANS"), "true", StringComparison.OrdinalIgnoreCase);

            if (enableKmeans)
            {
                var summaryPath = Path.Combine(outDir, $"{baseA}__{baseB}__kmeans_summary.txt");
                summary.Kmeans = RunCaminsScript(
                    "kmeans",
                    python,
                    Path.Combine("tools", "camins_detector", "tfidf_kmeans.py"),
                    outDir,
                    pageMap,
                    docHint,
                    pdfNameA,
                    pageA,
                    pdfNameB,
                    pageB,
                    summaryPath,
                    summary.Errors);
            }
            else
            {
                summary.Kmeans = new CaminsAlgoResult
                {
                    Name = "kmeans",
                    Error = "kmeans_disabled"
                };
            }

            return summary;
        }

        private static string ResolvePageDocMapPath()
        {
            var cwd = Directory.GetCurrentDirectory();
            var candidates = new[]
            {
                // Preferred location: keep generated artifacts under outputs/ (and avoid resurrecting top-level DocDetector/).
                Path.Combine(cwd, "outputs", "docdetector", "page_doc_map.json"),
                // Legacy locations (kept for compatibility).
                Path.Combine(cwd, "modules", "DocDetector", "outputs", "page_doc_map.json"),
                Path.Combine(cwd, "outputs", "page_doc_map.json"),
                Path.Combine(cwd, "DocDetector", "outputs", "page_doc_map.json"),
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c))
                    return c;
            }

            return Path.Combine(cwd, "outputs", "docdetector", "page_doc_map.json");
        }

        private static string ResolveCaminsPython()
        {
            var env = Environment.GetEnvironmentVariable("CAMINS_PYTHON");
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            env = Environment.GetEnvironmentVariable("NLP_PYTHON");
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            env = Environment.GetEnvironmentVariable("PYTHON");
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            return "python3";
        }

        private static CaminsAlgoResult RunCaminsScript(
            string name,
            string pythonPath,
            string scriptPath,
            string outDir,
            string pageMap,
            string docType,
            string pdfNameA,
            int pageA,
            string pdfNameB,
            int pageB,
            string? summaryPath,
            List<string> errors)
        {
            var result = new CaminsAlgoResult
            {
                Name = name
            };

            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            {
                result.Error = "script_not_found";
                errors.Add($"{name}: script_not_found");
                return result;
            }

            Directory.CreateDirectory(outDir);
            var csvPath = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(scriptPath)}__{docType}.csv");
            result.CsvPath = csvPath;
            if (!string.IsNullOrWhiteSpace(summaryPath))
                result.SummaryPath = summaryPath;

            var psi = new ProcessStartInfo
            {
                FileName = pythonPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("--page-map");
            psi.ArgumentList.Add(pageMap);
            psi.ArgumentList.Add("--doc-type");
            psi.ArgumentList.Add(docType);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add(csvPath);
            if (!string.IsNullOrWhiteSpace(summaryPath))
            {
                psi.ArgumentList.Add("--summary");
                psi.ArgumentList.Add(summaryPath);
            }

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    result.Error = "start_failed";
                    errors.Add($"{name}: start_failed");
                    return result;
                }

                var stdout = proc.StandardOutput.ReadToEnd();
                var stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                File.WriteAllText(Path.Combine(outDir, $"{name}_stdout.txt"), stdout ?? "");
                File.WriteAllText(Path.Combine(outDir, $"{name}_stderr.txt"), stderr ?? "");

                if (proc.ExitCode != 0)
                {
                    var err = string.IsNullOrWhiteSpace(stderr) ? $"exit_code_{proc.ExitCode}" : stderr.Trim();
                    result.Error = err;
                    errors.Add($"{name}: {err}");
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                errors.Add($"{name}: {ex.Message}");
                return result;
            }

            if (!File.Exists(csvPath))
            {
                result.Error = "csv_not_found";
                errors.Add($"{name}: csv_not_found");
                return result;
            }

            result.PdfA = TryFindCsvRow(csvPath, pdfNameA, pageA);
            result.PdfB = TryFindCsvRow(csvPath, pdfNameB, pageB);

            if (result.PdfA == null)
                errors.Add($"{name}: pdf_a_not_found");
            if (result.PdfB == null)
                errors.Add($"{name}: pdf_b_not_found");

            return result;
        }

        private static Dictionary<string, string>? TryFindCsvRow(string csvPath, string pdfName, int page)
        {
            if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
                return null;

            using var reader = new StreamReader(csvPath);
            var headerLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine))
                return null;

            var headers = ParseCsvLine(headerLine);
            if (headers.Count == 0)
                return null;
            headers[0] = headers[0].TrimStart('\uFEFF');

            Dictionary<string, string>? fallback = null;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var values = ParseCsvLine(line);
                if (values.Count == 0)
                    continue;
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var count = Math.Min(headers.Count, values.Count);
                for (int i = 0; i < count; i++)
                    row[headers[i]] = values[i];

                if (!row.TryGetValue("pdf", out var pdf))
                    continue;
                if (!string.Equals(pdf?.Trim(), pdfName, StringComparison.OrdinalIgnoreCase))
                    continue;

                fallback ??= row;
                if (row.TryGetValue("page", out var pageStr) && int.TryParse(pageStr, out var pageVal))
                {
                    if (pageVal == page)
                        return row;
                }
            }

            return fallback;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            if (line == null)
                return fields;

            var sb = new StringBuilder();
            var inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == ',')
                    {
                        fields.Add(sb.ToString());
                        sb.Clear();
                    }
                    else if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }
            fields.Add(sb.ToString());
            return fields;
        }

        private static string GetCaminsValue(Dictionary<string, string>? row, string key)
        {
            if (row == null || string.IsNullOrWhiteSpace(key))
                return "";
            return row.TryGetValue(key, out var value) ? value ?? "" : "";
        }

        private static bool ParseOptions(
            string[] args,
            out List<string> inputs,
            out int? pageA,
            out int? pageB,
            out bool asJson,
            out string outPath)
        {
            inputs = new List<string>();
            pageA = null;
            pageB = null;
            asJson = false;
            outPath = "";

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
                if (string.Equals(arg, "--page-a", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                        pageA = Math.Max(1, p);
                    continue;
                }
                if (string.Equals(arg, "--page-b", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                        pageB = Math.Max(1, p);
                    continue;
                }
                if (string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
                {
                    asJson = true;
                    continue;
                }
                if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    outPath = args[++i];
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal))
                    inputs.Add(arg);
            }

            return true;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("operpdf inspect pipeline <pdfA> [pdfB]");
            Console.WriteLine("operpdf inspect pipeline finalize --input <pdf|dir> [--out <arquivo|dir>] [--strict]");
            Console.WriteLine("  (se pdfB nao informado, tenta modelo em modules/PatternModules/registry/models/obj_models.yml)");
            Console.WriteLine("  --page-a N    (opcional, forca pagina A)");
            Console.WriteLine("  --page-b N    (opcional, forca pagina B)");
            Console.WriteLine("  --json        (salva JSON em outputs/objects_pipeline/)");
            Console.WriteLine("  --out <file>  (salva JSON no caminho informado)");
        }

        private static DetectionHit DetectDoc(string pdfPath, out int endPage)
        {
            endPage = 0;
            var bookmarkHit = DetectByBookmarkRange(pdfPath, out endPage);
            if (bookmarkHit.Found)
                return bookmarkHit;
            return DetectByTitleDetector(pdfPath, out endPage);
        }

        private static MapFieldsSummary RunMapFields(string aPath, string bPath, AlignRangeSummary align, DetectionSummary detA, DetectionSummary detB, FrontBackResult? frontBack)
        {
            var docHintA = NormalizeDocTypeHint(detA.TitleKey);
            var docHintB = NormalizeDocTypeHint(detB.TitleKey);
            var docHint = string.Equals(docHintA, docHintB, StringComparison.OrdinalIgnoreCase) ? docHintA : docHintA;

            // Para requerimento, preferir o mapa com guardas (alignrange_fields)
            if (DocumentValidationRules.IsDocMatch(docHint, DocumentValidationRules.DocKeyRequerimentoHonorarios))
            {
                var mapPath = ResolveMapPathForDoc(docHint);
                if (!string.IsNullOrWhiteSpace(mapPath) && File.Exists(mapPath))
                    return RunMapFieldsAlignRange(aPath, bPath, align, docHint, detA.BodyObj, detB.BodyObj, detA.BackBodyObj, detB.BackBodyObj);
            }

            var templatePath = ResolveTemplateMapPathForDoc(docHint);
            if (!string.IsNullOrWhiteSpace(templatePath) && File.Exists(templatePath))
                return RunMapFieldsTemplate(aPath, bPath, align, detA, detB, templatePath);

            if (DocumentValidationRules.IsDocMatch(docHint, DocumentValidationRules.DocKeyDespacho) && frontBack != null)
                return RunMapFieldsTextOps(aPath, bPath, frontBack);

            return RunMapFieldsAlignRange(aPath, bPath, align, docHint, detA.BodyObj, detB.BodyObj, detA.BackBodyObj, detB.BackBodyObj);
        }

        private static MapFieldsSummary RunMapFieldsTemplate(string aPath, string bPath, AlignRangeSummary align, DetectionSummary detA, DetectionSummary detB, string templatePath)
        {
            var summary = new MapFieldsSummary { Mode = "template", MapPath = templatePath };
            if (align == null)
                return summary;

            var baseA = Path.GetFileNameWithoutExtension(aPath);
            var baseB = Path.GetFileNameWithoutExtension(bPath);
            var outDir = Path.Combine("outputs", "objects_pipeline", $"{baseA}__{baseB}", "mapfields_template");
            Directory.CreateDirectory(outDir);

            var map = ObjectsTextOpsDiff.LoadTemplateFieldMap(templatePath, out var mapErr);
            if (map == null)
            {
                summary.Errors.Add(string.IsNullOrWhiteSpace(mapErr) ? "template_map_error" : $"template_map_error: {mapErr}");
                return summary;
            }

            summary.FrontObjA = detA.BodyObj;
            summary.FrontObjB = detB.BodyObj;
            summary.BackObjA = detA.BackBodyObj;
            summary.BackObjB = detB.BackBodyObj;

            var frontA = ObjectsTextOpsDiff.ExtractTemplateFields(
                aPath,
                detA.BodyObj,
                align.FrontA.StartOp,
                align.FrontA.EndOp,
                map,
                "front_head",
                out var frontErrA);
            if (!string.IsNullOrWhiteSpace(frontErrA))
                summary.Errors.Add("front_a: " + frontErrA);

            var frontB = ObjectsTextOpsDiff.ExtractTemplateFields(
                bPath,
                detB.BodyObj,
                align.FrontB.StartOp,
                align.FrontB.EndOp,
                map,
                "front_head",
                out var frontErrB);
            if (!string.IsNullOrWhiteSpace(frontErrB))
                summary.Errors.Add("front_b: " + frontErrB);

            var backA = new Dictionary<string, ObjectsTextOpsDiff.TemplateFieldResult>(StringComparer.OrdinalIgnoreCase);
            if (detA.BackBodyObj > 0 && align.BackA.StartOp > 0 && align.BackA.EndOp > 0)
            {
                backA = ObjectsTextOpsDiff.ExtractTemplateFields(
                    aPath,
                    detA.BackBodyObj,
                    align.BackA.StartOp,
                    align.BackA.EndOp,
                    map,
                    "back_tail",
                    out var backErrA);
                if (!string.IsNullOrWhiteSpace(backErrA))
                    summary.Errors.Add("back_a: " + backErrA);
            }

            var backB = new Dictionary<string, ObjectsTextOpsDiff.TemplateFieldResult>(StringComparer.OrdinalIgnoreCase);
            if (detB.BackBodyObj > 0 && align.BackB.StartOp > 0 && align.BackB.EndOp > 0)
            {
                backB = ObjectsTextOpsDiff.ExtractTemplateFields(
                    bPath,
                    detB.BackBodyObj,
                    align.BackB.StartOp,
                    align.BackB.EndOp,
                    map,
                    "back_tail",
                    out var backErrB);
                if (!string.IsNullOrWhiteSpace(backErrB))
                    summary.Errors.Add("back_b: " + backErrB);
            }

            var frontAValues = ConvertTemplateFields(frontA);
            var frontBValues = ConvertTemplateFields(frontB);
            var backAValues = ConvertTemplateFields(backA);
            var backBValues = ConvertTemplateFields(backB);

            var mergedA = MergeObjFields(frontAValues, backAValues);
            var mergedB = MergeObjFields(frontBValues, backBValues);

            var outFile = Path.Combine(outDir, $"{baseA}__{baseB}__mapfields_template_both_ab.json");
            WriteObjMapFields(outFile, mergedA, mergedB);
            summary.JsonPath = outFile;

            return summary;
        }

        private static MapFieldsSummary RunMapFieldsAlignRange(
            string aPath,
            string bPath,
            AlignRangeSummary align,
            string docHint,
            int frontObjA,
            int frontObjB,
            int backObjA,
            int backObjB)
        {
            var summary = new MapFieldsSummary { Mode = "alignrange" };
            if (align == null)
                return summary;

            var baseA = Path.GetFileNameWithoutExtension(aPath);
            var baseB = Path.GetFileNameWithoutExtension(bPath);
            var outDir = Path.Combine("outputs", "objects_pipeline", $"{baseA}__{baseB}", "mapfields");
            Directory.CreateDirectory(outDir);

            var mapPath = ResolveMapPathForDoc(docHint);
            if (string.IsNullOrWhiteSpace(mapPath) || !File.Exists(mapPath))
            {
                summary.Errors.Add("map_not_found");
                summary.MapPath = mapPath;
                return summary;
            }

            var alignPath = Path.Combine(outDir, $"{baseA}__{baseB}.yml");
            WriteAlignRangeYaml(align, aPath, bPath, alignPath, frontObjA, frontObjB, backObjA, backObjB);

            summary.MapPath = mapPath;
            summary.AlignRangePath = alignPath;

            try
            {
                ObjectsMapFields.Execute(new[]
                {
                    "--alignrange", alignPath,
                    "--map", mapPath,
                    "--out", outDir,
                    "--both",
                    "--side", "both"
                });

                var baseName = Path.GetFileNameWithoutExtension(alignPath);
                var outFile = Path.Combine(outDir, $"{baseName}__mapfields_both_ab.json");
                if (File.Exists(outFile))
                {
                    summary.JsonPath = outFile;
                    summary.RejectsPath = "";
                }
                else
                {
                    summary.Errors.Add("mapfields_output_not_found");
                }
            }
            catch (Exception ex)
            {
                summary.Errors.Add("mapfields_error: " + ex.Message);
            }

            return summary;
        }

        private static MapFieldsSummary RunMapFieldsTextOps(string aPath, string bPath, FrontBackResult frontBack)
        {
            var summary = new MapFieldsSummary { Mode = "textops" };
            var baseA = Path.GetFileNameWithoutExtension(aPath);
            var baseB = Path.GetFileNameWithoutExtension(bPath);
            var outDir = Path.Combine("outputs", "objects_pipeline", $"{baseA}__{baseB}", "mapfields_obj");
            Directory.CreateDirectory(outDir);

            var frontMapBase = ResolveMapPath("tjpb_despacho_obj6_fields.draft.yml");
            var backMapBase = ResolveMapPath("tjpb_despacho_obj_p2_fields.draft.yml");

            summary.MapPath = frontMapBase;
            summary.BackMapPath = backMapBase;
            summary.FrontObjA = frontBack.FrontA?.Obj ?? 0;
            summary.FrontObjB = frontBack.FrontB?.Obj ?? 0;
            summary.BackObjA = frontBack.BackBodyA?.Obj ?? 0;
            summary.BackObjB = frontBack.BackBodyB?.Obj ?? 0;

            if (string.IsNullOrWhiteSpace(frontMapBase) || !File.Exists(frontMapBase))
                summary.Errors.Add("front_map_not_found");
            if (string.IsNullOrWhiteSpace(backMapBase) || !File.Exists(backMapBase))
                summary.Errors.Add("back_map_not_found");
            if (summary.FrontObjA <= 0) summary.Errors.Add("front_obj_a_not_found");
            if (summary.FrontObjB <= 0) summary.Errors.Add("front_obj_b_not_found");

            var frontMapA = "";
            var frontMapB = "";
            var backMapA = "";
            var backMapB = "";
            var frontOutA = "";
            var frontOutB = "";
            var backOutA = "";
            var backOutB = "";

            if (summary.Errors.Count == 0)
            {
                frontMapA = BuildAdjustedMap(frontMapBase, summary.FrontObjA, outDir, $"{baseA}__front_head_a_map.yml");
                frontMapB = BuildAdjustedMap(frontMapBase, summary.FrontObjB, outDir, $"{baseB}__front_head_b_map.yml");
                frontOutA = Path.Combine(outDir, $"{baseA}__front_head_a.json");
                frontOutB = Path.Combine(outDir, $"{baseB}__front_head_b.json");

                RunTextOpsExtract(aPath, frontMapA, frontOutA, summary.Errors);
                RunTextOpsExtract(bPath, frontMapB, frontOutB, summary.Errors);

                if (summary.BackObjA > 0 && File.Exists(backMapBase))
                {
                    backMapA = BuildAdjustedMap(backMapBase, summary.BackObjA, outDir, $"{baseA}__back_tail_a_map.yml");
                    backOutA = Path.Combine(outDir, $"{baseA}__back_tail_a.json");
                    RunTextOpsExtract(aPath, backMapA, backOutA, summary.Errors);
                }
                if (summary.BackObjB > 0 && File.Exists(backMapBase))
                {
                    backMapB = BuildAdjustedMap(backMapBase, summary.BackObjB, outDir, $"{baseB}__back_tail_b_map.yml");
                    backOutB = Path.Combine(outDir, $"{baseB}__back_tail_b.json");
                    RunTextOpsExtract(bPath, backMapB, backOutB, summary.Errors);
                }
            }

            var frontFieldsA = LoadObjExtract(frontOutA, "front_head", summary.Errors);
            var frontFieldsB = LoadObjExtract(frontOutB, "front_head", summary.Errors);
            var backFieldsA = LoadObjExtract(backOutA, "back_tail", summary.Errors);
            var backFieldsB = LoadObjExtract(backOutB, "back_tail", summary.Errors);

            var mergedA = MergeObjFields(frontFieldsA, backFieldsA);
            var mergedB = MergeObjFields(frontFieldsB, backFieldsB);

            var outFile = Path.Combine(outDir, $"{baseA}__{baseB}__mapfields_obj_both_ab.json");
            WriteObjMapFields(outFile, mergedA, mergedB);
            summary.JsonPath = outFile;

            return summary;
        }

        private static ConsolidatedSummary? RunConsolidation(MapFieldsSummary? mapFields, DetectionSummary detA, DetectionSummary detB)
        {
            if (mapFields == null || string.IsNullOrWhiteSpace(mapFields.JsonPath) || !File.Exists(mapFields.JsonPath))
                return null;

            var summary = new ConsolidatedSummary();
            var docType = NormalizeDocTypeHint(detA.TitleKey);

            var consolidateKey = DocumentValidationRules.MapDocKeyToConsolidationInput(docType);
            if (!string.IsNullOrWhiteSpace(consolidateKey))
            {
                summary.Inputs[consolidateKey] = mapFields.JsonPath;
            }
            else
            {
                summary.Errors.Add("consolidate_doc_type_not_supported");
                return summary;
            }

            try
            {
                var inputs = new List<ObjectsConsolidate.MapFieldsInput>();
                var despachoInputKey = DocumentValidationRules.MapDocKeyToConsolidationInput(DocumentValidationRules.DocKeyDespacho);
                var requerimentoInputKey = DocumentValidationRules.MapDocKeyToConsolidationInput(DocumentValidationRules.DocKeyRequerimentoHonorarios);
                var certidaoInputKey = DocumentValidationRules.MapDocKeyToConsolidationInput(DocumentValidationRules.DocKeyCertidaoConselho);

                if (!string.IsNullOrWhiteSpace(despachoInputKey) && summary.Inputs.TryGetValue(despachoInputKey, out var d))
                    inputs.Add(new ObjectsConsolidate.MapFieldsInput { DocType = despachoInputKey, Path = d });
                if (!string.IsNullOrWhiteSpace(requerimentoInputKey) && summary.Inputs.TryGetValue(requerimentoInputKey, out var r))
                    inputs.Add(new ObjectsConsolidate.MapFieldsInput { DocType = requerimentoInputKey, Path = r });
                if (!string.IsNullOrWhiteSpace(certidaoInputKey) && summary.Inputs.TryGetValue(certidaoInputKey, out var c))
                    inputs.Add(new ObjectsConsolidate.MapFieldsInput { DocType = certidaoInputKey, Path = c });

                var consolidated = ObjectsConsolidate.Run(inputs, "auto");
                var baseName = Path.GetFileNameWithoutExtension(mapFields.JsonPath);
                var outDir = Path.Combine(Path.GetDirectoryName(mapFields.JsonPath) ?? "", "..", "consolidated");
                outDir = Path.GetFullPath(outDir);
                Directory.CreateDirectory(outDir);
                var outPath = Path.Combine(outDir, $"{baseName}__consolidated.json");
                var json = JsonSerializer.Serialize(consolidated, JsonUtils.Indented);
                File.WriteAllText(outPath, json);
                summary.JsonPath = outPath;
                summary.Errors.AddRange(consolidated.Errors);
            }
            catch (Exception ex)
            {
                summary.Errors.Add("consolidate_error: " + ex.Message);
            }

            return summary;
        }

        private static string ResolveMapPathForDoc(string docHint)
        {
            var key = NormalizeDocTypeHint(docHint);
            if (DocumentValidationRules.IsDocMatch(key, DocumentValidationRules.DocKeyDespacho))
                return ResolveMapPath("tjpb_despacho");
            if (DocumentValidationRules.IsDocMatch(key, DocumentValidationRules.DocKeyCertidaoConselho))
                return ResolveMapPath("tjpb_certidao");
            if (DocumentValidationRules.IsDocMatch(key, DocumentValidationRules.DocKeyRequerimentoHonorarios))
                return ResolveMapPath("tjpb_requerimento");
            return ResolveMapPath(key);
        }

        private static string ResolveTemplateMapPathForDoc(string docHint)
        {
            var key = NormalizeDocTypeHint(docHint);
            if (DocumentValidationRules.IsDocMatch(key, DocumentValidationRules.DocKeyDespacho))
                return ResolveTemplateMapPath("tjpb_despacho");
            if (DocumentValidationRules.IsDocMatch(key, DocumentValidationRules.DocKeyCertidaoConselho))
                return ResolveTemplateMapPath("tjpb_certidao");
            if (DocumentValidationRules.IsDocMatch(key, DocumentValidationRules.DocKeyRequerimentoHonorarios))
                return ResolveTemplateMapPath("tjpb_requerimento");
            return ResolveTemplateMapPath(key);
        }

        private static string ResolveModelPathForDoc(string docHint)
        {
            var cfg = LoadObjModels(out var cfgPath);
            if (cfg?.Models == null || cfg.Models.Count == 0)
                return "";

            var key = string.IsNullOrWhiteSpace(docHint) ? "" : docHint.Trim().ToLowerInvariant();
            key = NormalizeDocTypeHint(key);

            if (!cfg.Models.TryGetValue(key, out var rawPath) || string.IsNullOrWhiteSpace(rawPath))
                return "";

            if (File.Exists(rawPath))
                return rawPath;

            var cwd = Directory.GetCurrentDirectory();
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(cfgPath))
            {
                var cfgDir = Path.GetDirectoryName(cfgPath);
                if (!string.IsNullOrWhiteSpace(cfgDir))
                    candidates.Add(Path.Combine(cfgDir, rawPath));
            }
            candidates.Add(Path.Combine(cwd, rawPath));

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return "";
        }

        private static ObjModelsConfig? LoadObjModels(out string configPath)
        {
            configPath = ResolveModelsConfigPath();
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                return null;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                return deserializer.Deserialize<ObjModelsConfig>(File.ReadAllText(configPath));
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveModelsConfigPath()
        {
            foreach (var ext in new[] { ".yml", ".yaml" })
            {
                var file = "obj_models" + ext;
                var reg = Obj.Utils.PatternRegistry.FindFile("models", file);
                if (!string.IsNullOrWhiteSpace(reg))
                    return reg;
            }
            return "";
        }

        private static string ResolveTemplateMapPath(string mapPath)
        {
            if (string.IsNullOrWhiteSpace(mapPath))
                return "";
            if (File.Exists(mapPath)) return mapPath;
            var cwd = Directory.GetCurrentDirectory();
            var regTemplates = Obj.Utils.PatternRegistry.FindDir("template_fields");
            var candidates = new List<string>
            {
                Path.Combine(cwd, mapPath)
            };
            if (!string.IsNullOrWhiteSpace(regTemplates))
            {
                candidates.Add(Path.Combine(regTemplates, mapPath));
                candidates.Add(Path.Combine(regTemplates, mapPath + ".yml"));
                candidates.Add(Path.Combine(regTemplates, mapPath + ".yaml"));
            }
            var baseName = Path.GetFileName(mapPath);
            if (!string.IsNullOrWhiteSpace(baseName) && !string.Equals(baseName, mapPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(regTemplates))
                {
                    candidates.Add(Path.Combine(regTemplates, baseName));
                    candidates.Add(Path.Combine(regTemplates, baseName + ".yml"));
                    candidates.Add(Path.Combine(regTemplates, baseName + ".yaml"));
                }
            }
            foreach (var c in candidates)
            {
                if (File.Exists(c))
                    return c;
            }
            return "";
        }

        private static string ResolveMapPath(string mapPath)
        {
            if (string.IsNullOrWhiteSpace(mapPath))
                return "";
            if (File.Exists(mapPath)) return mapPath;
            var cwd = Directory.GetCurrentDirectory();
            var regAlign = Obj.Utils.PatternRegistry.FindDir("alignrange_fields");
            var regTextOps = Obj.Utils.PatternRegistry.FindDir("textops_fields");
            var regExtract = Obj.Utils.PatternRegistry.FindDir("extract_fields");
            var candidates = new List<string>
            {
                Path.Combine(cwd, mapPath)
            };
            if (!string.IsNullOrWhiteSpace(regAlign))
                candidates.Add(Path.Combine(regAlign, mapPath));
            if (!string.IsNullOrWhiteSpace(regTextOps))
                candidates.Add(Path.Combine(regTextOps, mapPath));
            if (!string.IsNullOrWhiteSpace(regExtract))
                candidates.Add(Path.Combine(regExtract, mapPath));

            if (!mapPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && !mapPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(regAlign))
                {
                    candidates.Add(Path.Combine(regAlign, mapPath + ".yml"));
                    candidates.Add(Path.Combine(regAlign, mapPath + ".yaml"));
                }
                if (!string.IsNullOrWhiteSpace(regTextOps))
                {
                    candidates.Add(Path.Combine(regTextOps, mapPath + ".yml"));
                    candidates.Add(Path.Combine(regTextOps, mapPath + ".yaml"));
                }
                if (!string.IsNullOrWhiteSpace(regExtract))
                {
                    candidates.Add(Path.Combine(regExtract, mapPath + ".yml"));
                    candidates.Add(Path.Combine(regExtract, mapPath + ".yaml"));
                }
            }

            var baseName = Path.GetFileName(mapPath);
            if (!string.IsNullOrWhiteSpace(baseName) && !string.Equals(baseName, mapPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(regAlign))
                    candidates.Add(Path.Combine(regAlign, baseName));
                if (!string.IsNullOrWhiteSpace(regTextOps))
                    candidates.Add(Path.Combine(regTextOps, baseName));
                if (!string.IsNullOrWhiteSpace(regExtract))
                    candidates.Add(Path.Combine(regExtract, baseName));
                if (!baseName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && !baseName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(regAlign))
                    {
                        candidates.Add(Path.Combine(regAlign, baseName + ".yml"));
                        candidates.Add(Path.Combine(regAlign, baseName + ".yaml"));
                    }
                    if (!string.IsNullOrWhiteSpace(regTextOps))
                    {
                        candidates.Add(Path.Combine(regTextOps, baseName + ".yml"));
                        candidates.Add(Path.Combine(regTextOps, baseName + ".yaml"));
                    }
                    if (!string.IsNullOrWhiteSpace(regExtract))
                    {
                        candidates.Add(Path.Combine(regExtract, baseName + ".yml"));
                        candidates.Add(Path.Combine(regExtract, baseName + ".yaml"));
                    }
                }
            }

            foreach (var c in candidates)
            {
                if (File.Exists(c))
                    return c;
            }
            return "";
        }

        private static void WriteAlignRangeYaml(
            AlignRangeSummary align,
            string aPath,
            string bPath,
            string outPath,
            int frontObjA,
            int frontObjB,
            int backObjA,
            int backObjB)
        {
            var data = new AlignRangeYaml
            {
                FrontHead = new AlignRangeSection
                {
                    PdfA = Path.GetFileName(aPath),
                    PdfAPath = aPath,
                    ObjA = frontObjA,
                    OpRangeA = FormatOpRange(align.FrontA.StartOp, align.FrontA.EndOp),
                    ValueFullA = align.FrontA.ValueFull ?? "",
                    PdfB = Path.GetFileName(bPath),
                    PdfBPath = bPath,
                    ObjB = frontObjB,
                    OpRangeB = FormatOpRange(align.FrontB.StartOp, align.FrontB.EndOp),
                    ValueFullB = align.FrontB.ValueFull ?? ""
                },
                BackTail = new AlignRangeSection
                {
                    PdfA = Path.GetFileName(aPath),
                    PdfAPath = aPath,
                    ObjA = backObjA,
                    OpRangeA = FormatOpRange(align.BackA.StartOp, align.BackA.EndOp),
                    ValueFullA = align.BackA.ValueFull ?? "",
                    PdfB = Path.GetFileName(bPath),
                    PdfBPath = bPath,
                    ObjB = backObjB,
                    OpRangeB = FormatOpRange(align.BackB.StartOp, align.BackB.EndOp),
                    ValueFullB = align.BackB.ValueFull ?? ""
                }
            };

            var serializer = new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();

            File.WriteAllText(outPath, serializer.Serialize(data));
        }

        private static string BuildAdjustedMap(string baseMapPath, int objId, string outDir, string fileName)
        {
            if (string.IsNullOrWhiteSpace(baseMapPath) || !File.Exists(baseMapPath) || objId <= 0)
                return "";

            var content = File.ReadAllText(baseMapPath);
            var updated = Regex.Replace(
                content,
                @"(?m)^(\s*obj\s*:\s*)\d+",
                m => m.Groups[1].Value + objId.ToString(CultureInfo.InvariantCulture));
            var outPath = Path.Combine(outDir, fileName);
            File.WriteAllText(outPath, updated);
            return outPath;
        }

        private static void RunTextOpsExtract(string pdfPath, string mapPath, string outPath, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                errors.Add("textops_pdf_not_found");
                return;
            }
            if (string.IsNullOrWhiteSpace(mapPath) || !File.Exists(mapPath))
            {
                errors.Add("textops_map_not_found");
                return;
            }

            try
            {
                ObjectsTextOpsExtractFields.Execute(new[]
                {
                    "--input", pdfPath,
                    "--map", mapPath,
                    "--json",
                    "--out", outPath
                });
            }
            catch (Exception ex)
            {
                errors.Add("textops_error: " + ex.Message);
            }
        }

        private static Dictionary<string, ObjFieldValue> LoadObjExtract(string jsonPath, string source, List<string> errors)
        {
            var result = new Dictionary<string, ObjFieldValue>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(jsonPath))
                return result;
            if (!File.Exists(jsonPath))
            {
                errors.Add($"textops_output_not_found: {Path.GetFileName(jsonPath)}");
                return result;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                if (!doc.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                    return result;

                var objId = ParseObjId(doc.RootElement);

                foreach (var item in fields.EnumerateArray())
                {
                    if (!item.TryGetProperty("Field", out var f) || f.ValueKind != JsonValueKind.String)
                        continue;
                    var fieldName = f.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(fieldName))
                        continue;

                    var status = GetString(item, "Status");
                    var value = GetString(item, "Value");
                    var valueFull = GetString(item, "ValueFull");
                    var valueRaw = GetString(item, "ValueRaw");
                    var opRange = GetString(item, "OpRange");
                    var fieldObj = GetInt(item, "Obj");
                    if (fieldObj <= 0)
                        fieldObj = objId;
                    var bbox = GetBBox(item);

                    result[fieldName] = new ObjFieldValue
                    {
                        Status = status,
                        Value = value,
                        ValueFull = valueFull,
                        ValueRaw = valueRaw,
                        OpRange = opRange,
                        Source = source,
                        Obj = fieldObj,
                        BBox = bbox
                    };
                }
            }
            catch (Exception ex)
            {
                errors.Add("textops_parse_error: " + ex.Message);
            }

            return result;
        }

        private static Dictionary<string, ObjFieldValue> MergeObjFields(
            Dictionary<string, ObjFieldValue> front,
            Dictionary<string, ObjFieldValue> back)
        {
            var merged = new Dictionary<string, ObjFieldValue>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in front)
                merged[kv.Key] = kv.Value;

            foreach (var kv in back)
            {
                if (!merged.TryGetValue(kv.Key, out var existing))
                {
                    merged[kv.Key] = kv.Value;
                    continue;
                }

                if (PreferBackField(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value.Value))
                {
                    merged[kv.Key] = kv.Value;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(existing.Value) && !string.IsNullOrWhiteSpace(kv.Value.Value))
                {
                    merged[kv.Key] = kv.Value;
                }
            }

            return merged;
        }

        private static Dictionary<string, ObjFieldValue> ConvertTemplateFields(
            Dictionary<string, ObjectsTextOpsDiff.TemplateFieldResult> fields)
        {
            var result = new Dictionary<string, ObjFieldValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in fields)
            {
                var f = kv.Value;
                result[kv.Key] = new ObjFieldValue
                {
                    Status = f.Status ?? "",
                    Value = f.Value ?? "",
                    ValueFull = f.ValueFull ?? "",
                    ValueRaw = f.ValueRaw ?? "",
                    OpRange = f.OpRange ?? "",
                    Source = f.Source ?? "",
                    Obj = f.Obj,
                    BBox = f.BBox == null ? null : new ObjBoundingBox
                    {
                        X0 = f.BBox.X0,
                        Y0 = f.BBox.Y0,
                        X1 = f.BBox.X1,
                        Y1 = f.BBox.Y1,
                        StartOp = f.BBox.StartOp,
                        EndOp = f.BBox.EndOp,
                        Items = f.BBox.Items
                    }
                };
            }
            return result;
        }

        private static void WriteObjMapFields(string outPath, Dictionary<string, ObjFieldValue> pdfA, Dictionary<string, ObjFieldValue> pdfB)
        {
            var payload = new
            {
                pdf_a = pdfA,
                pdf_b = pdfB,
                meta = new
                {
                    mode = "textops",
                    generated_at = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                }
            };
            File.WriteAllText(outPath, JsonSerializer.Serialize(payload, JsonUtils.Indented));
        }

        private static bool PreferBackField(string field)
        {
            return field.Equals("DATA_ARBITRADO_FINAL", StringComparison.OrdinalIgnoreCase)
                   || field.Equals("VALOR_ARBITRADO_DE", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseObjId(JsonElement root)
        {
            if (!root.TryGetProperty("obj", out var objEl) || objEl.ValueKind != JsonValueKind.String)
                return 0;
            var raw = objEl.GetString() ?? "";
            var match = Regex.Match(raw, @"\d+");
            return match.Success ? int.Parse(match.Value, CultureInfo.InvariantCulture) : 0;
        }

        private static string GetString(JsonElement item, string prop)
        {
            if (!item.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String)
                return "";
            return el.GetString() ?? "";
        }

        private static int GetInt(JsonElement item, string prop)
        {
            if (!item.TryGetProperty(prop, out var el))
                return 0;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
                return v;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                return n;
            return 0;
        }

        private static ObjBoundingBox? GetBBox(JsonElement item)
        {
            if (!item.TryGetProperty("BBox", out var box) || box.ValueKind != JsonValueKind.Object)
                return null;

            var bbox = new ObjBoundingBox
            {
                X0 = GetDouble(box, "X0"),
                Y0 = GetDouble(box, "Y0"),
                X1 = GetDouble(box, "X1"),
                Y1 = GetDouble(box, "Y1"),
                StartOp = GetInt(box, "StartOp"),
                EndOp = GetInt(box, "EndOp"),
                Items = GetInt(box, "Items")
            };
            return bbox;
        }

        private static double GetDouble(JsonElement item, string prop)
        {
            if (!item.TryGetProperty(prop, out var el))
                return 0;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var v))
                return v;
            if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                return n;
            return 0;
        }

        private static DetectionHit DetectByBookmarkRange(string pdfPath, out int endPage)
        {
            endPage = 0;
            var hit = BookmarkDetector.Detect(pdfPath);
            if (!hit.Found)
                return hit;

            var range = ResolveBookmarkRange(pdfPath, hit.Page);
            endPage = range.EndPage > 0 ? range.EndPage : hit.Page;

            DetectionResult? result;
            try
            {
                result = DocumentTitleDetector.Detect(pdfPath, DocumentValidationRules.BuildDefaultDetectionOptions());
            }
            catch
            {
                return DetectionHit.Empty(pdfPath, "bookmark_title_detect_failed");
            }

            if (result.Pages.Count == 0)
                return DetectionHit.Empty(pdfPath, "bookmark_no_pages");

            var rangeEnd = endPage;
            var pages = result.Pages
                .Where(p => p.Page >= range.StartPage && p.Page <= rangeEnd)
                .OrderBy(p => p.Page)
                .ToList();

            if (pages.Count == 0)
                return DetectionHit.Empty(pdfPath, "bookmark_range_empty");

            return DetectByTitlePages(pdfPath, pages, out endPage, "bookmark");
        }

        private static DetectionHit DetectByTitleDetector(string pdfPath, out int endPage)
        {
            endPage = 0;
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                return DetectionHit.Empty(pdfPath, "pdf_not_found");

            var opts = DocumentValidationRules.BuildDefaultDetectionOptions();

            DetectionResult result;
            try
            {
                result = DocumentTitleDetector.Detect(pdfPath, opts);
            }
            catch (Exception ex)
            {
                return DetectionHit.Empty(pdfPath, $"title_detect_failed: {ex.GetType().Name}");
            }
            if (result.Pages.Count == 0)
                return DetectionHit.Empty(pdfPath, "no_pages");

            return DetectByTitlePages(pdfPath, result.Pages.OrderBy(p => p.Page).ToList(), out endPage, "title_body");
        }

        private static DetectionHit DetectByTitlePages(
            string pdfPath,
            List<PageClassification> pages,
            out int endPage,
            string reason)
        {
            endPage = 0;
            if (pages == null || pages.Count == 0)
                return DetectionHit.Empty(pdfPath, "no_pages");

            DetectionHit? requerimentoCandidate = null;
            DetectionHit? despachoCandidate = null;
            foreach (var page in pages.OrderBy(p => p.Page))
            {
                var top = NormalizeTitleBody(page.TopText ?? "");
                var body = NormalizeTitleBody($"{page.BodyPrefix ?? ""} {page.BodySuffix ?? ""}");
                var combined = NormalizeTitleBody($"{page.TopText ?? ""} {page.BodyPrefix ?? ""} {page.BodySuffix ?? ""} {page.BottomText ?? ""}");
                var key = NormalizeTitleBody(page.TitleKey ?? "");

                if (DocumentValidationRules.IsCertidaoConselho(top) ||
                    DocumentValidationRules.IsCertidaoConselho(body) ||
                    DocumentValidationRules.IsCertidaoConselho(combined))
                {
                    endPage = page.Page;
                    return new DetectionHit
                    {
                        PdfPath = pdfPath,
                        Page = page.Page,
                        Obj = page.BodyObj,
                        TitleKey = DocumentValidationRules.DocKeyCertidaoConselho,
                        Title = page.Title ?? page.TopText ?? "",
                        PathRef = BuildPathRef(page.Page, page.BodyObj),
                        MatchedKeyword = DocumentValidationRules.DocKeyCertidaoConselho,
                        Reason = reason
                    };
                }

                var isReqStrong = DocumentValidationRules.IsRequerimentoStrong(top) ||
                                  DocumentValidationRules.IsRequerimentoStrong(body) ||
                                  DocumentValidationRules.IsRequerimentoStrong(combined);
                if (isReqStrong)
                {
                    endPage = page.Page;
                    return new DetectionHit
                    {
                        PdfPath = pdfPath,
                        Page = page.Page,
                        Obj = page.BodyObj,
                        TitleKey = DocumentValidationRules.DocKeyRequerimentoHonorarios,
                        Title = page.Title ?? page.TopText ?? "",
                        PathRef = BuildPathRef(page.Page, page.BodyObj),
                        MatchedKeyword = DocumentValidationRules.DocKeyRequerimentoHonorarios,
                        Reason = reason
                    };
                }

                if (DocumentValidationRules.IsDespacho(key) ||
                    DocumentValidationRules.IsDespacho(top) ||
                    DocumentValidationRules.IsDespacho(body) ||
                    DocumentValidationRules.IsDespacho(combined))
                {
                    if (despachoCandidate == null)
                    {
                        despachoCandidate = new DetectionHit
                        {
                            PdfPath = pdfPath,
                            Page = page.Page,
                            Obj = page.BodyObj,
                            TitleKey = DocumentValidationRules.DocKeyDespacho,
                            Title = page.Title ?? page.TopText ?? "",
                            PathRef = BuildPathRef(page.Page, page.BodyObj),
                            MatchedKeyword = DocumentValidationRules.DocKeyDespacho,
                            Reason = reason
                        };
                    }
                }

                if (requerimentoCandidate == null &&
                    (DocumentValidationRules.IsRequerimento(top) ||
                     DocumentValidationRules.IsRequerimento(body) ||
                     DocumentValidationRules.IsRequerimento(combined) ||
                     DocumentValidationRules.IsRequerimento(key)))
                {
                    requerimentoCandidate = new DetectionHit
                    {
                        PdfPath = pdfPath,
                        Page = page.Page,
                        Obj = page.BodyObj,
                        TitleKey = DocumentValidationRules.DocKeyRequerimentoHonorarios,
                        Title = page.Title ?? page.TopText ?? "",
                        PathRef = BuildPathRef(page.Page, page.BodyObj),
                        MatchedKeyword = DocumentValidationRules.DocKeyRequerimentoHonorarios,
                        Reason = reason
                    };
                }
            }

            if (requerimentoCandidate == null)
            {
                var fullReq = TryDetectRequerimentoFullText(pdfPath, pages, 3);
                if (fullReq != null)
                    requerimentoCandidate = fullReq;
            }

            if (requerimentoCandidate != null)
            {
                endPage = requerimentoCandidate.Page;
                return requerimentoCandidate;
            }

            if (despachoCandidate != null)
            {
                endPage = ResolveSpanEnd(despachoCandidate.Page, pages);
                return despachoCandidate;
            }

            return DetectionHit.Empty(pdfPath, "title_body_not_found");
        }

        private static DetectionHit? TryDetectRequerimentoFullText(string pdfPath, List<PageClassification> pages, int maxPages)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || pages == null || pages.Count == 0 || maxPages <= 0)
                return null;

            try
            {
                using var doc = new PdfDocument(new PdfReader(pdfPath));
                foreach (var page in pages.OrderBy(p => p.Page).Take(maxPages))
                {
                    if (page.Page <= 0)
                        continue;

                    if (!TryExtractPageText(doc, page.Page, out var text))
                        continue;

                    var norm = NormalizeTitleBody(text);
                    if (!DocumentValidationRules.IsRequerimentoStrong(norm) &&
                        !DocumentValidationRules.IsRequerimento(norm))
                        continue;

                    return new DetectionHit
                    {
                        PdfPath = pdfPath,
                        Page = page.Page,
                        Obj = page.BodyObj,
                        TitleKey = DocumentValidationRules.DocKeyRequerimentoHonorarios,
                        Title = page.Title ?? page.TopText ?? "",
                        PathRef = BuildPathRef(page.Page, page.BodyObj),
                        MatchedKeyword = DocumentValidationRules.DocKeyRequerimentoHonorarios,
                        Reason = "full_text"
                    };
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static List<DetectionSummary> DetectAllDocs(string pdfPath)
        {
            var results = new List<DetectionSummary>();
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                return results;

            var opts = DocumentValidationRules.BuildDefaultDetectionOptions();

            DetectionResult? result;
            try
            {
                result = DocumentTitleDetector.Detect(pdfPath, opts);
            }
            catch
            {
                return results;
            }

            var pages = result.Pages.OrderBy(p => p.Page).ToList();
            string currentKey = "";
            Obj.DocDetector.PageClassification? currentStart = null;

            foreach (var page in pages)
            {
                var pageKey = ClassifyDocKey(page);
                if (string.IsNullOrWhiteSpace(pageKey))
                {
                    var mapped = MapTitleKeyToDoc(page.TitleKey);
                    if (!string.IsNullOrWhiteSpace(mapped))
                        pageKey = mapped;
                }

                if (string.IsNullOrWhiteSpace(pageKey))
                {
                    if (!string.IsNullOrWhiteSpace(currentKey))
                    {
                        results.Add(BuildDetectionSummary(pdfPath, currentKey, currentStart!, page.Page - 1));
                        currentKey = "";
                        currentStart = null;
                    }
                    continue;
                }

                if (string.IsNullOrWhiteSpace(currentKey))
                {
                    currentKey = pageKey;
                    currentStart = page;
                    continue;
                }

                if (!string.Equals(currentKey, pageKey, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(BuildDetectionSummary(pdfPath, currentKey, currentStart!, page.Page - 1));
                    currentKey = pageKey;
                    currentStart = page;
                }
            }

            if (!string.IsNullOrWhiteSpace(currentKey) && currentStart != null)
            {
                results.Add(BuildDetectionSummary(pdfPath, currentKey, currentStart, currentStart.Page <= pages.Last().Page ? pages.Last().Page : currentStart.Page));
            }

            AttachDespachoSubtype(results, pages);
            return results;
        }

        private static DetectionSummary BuildDetectionSummary(string pdfPath, string docKey, PageClassification startPage, int endPage)
        {
            var summary = new DetectionSummary
            {
                TitleKey = docKey,
                Title = startPage.Title ?? startPage.TopText ?? "",
                StartPage = startPage.Page,
                EndPage = endPage > 0 ? endPage : startPage.Page,
                BodyObj = startPage.BodyObj,
                PathRef = BuildPathRef(startPage.Page, startPage.BodyObj)
            };

            if (DocumentValidationRules.IsDocMatch(docKey, DocumentValidationRules.DocKeyDespacho))
            {
                var backPage = startPage.Page + 1;
                var backBody = ContentsStreamPicker.Pick(new StreamPickRequest
                {
                    PdfPath = pdfPath,
                    Page = backPage,
                    RequireMarker = false
                });
                if (backBody.Obj > 0)
                {
                    summary.BackPage = backPage;
                    summary.BackBodyObj = backBody.Obj;
                    summary.EndPage = backPage;
                }

                var backSig = ContentsStreamPicker.PickSecondary(new StreamPickRequest
                {
                    PdfPath = pdfPath,
                    Page = backPage,
                    RequireMarker = false
                });
                summary.BackSignatureObj = backSig.Obj;
            }

            return summary;
        }

        private static void AttachDespachoSubtype(List<DetectionSummary> segments, List<PageClassification> pages)
        {
            if (segments == null || segments.Count == 0 || pages == null || pages.Count == 0)
                return;

            if (!TryLoadDespachoConfig(out var cfg, out _))
                return;

            foreach (var seg in segments)
            {
                if (!DocumentValidationRules.IsDocMatch(seg.TitleKey, DocumentValidationRules.DocKeyDespacho))
                    continue;

                var segPages = pages.Where(p => p.Page >= seg.StartPage && p.Page <= seg.EndPage).ToList();
                if (segPages.Count == 0)
                    continue;

                var side = ClassifyDespachoSubtypeFromPages(segPages, cfg, out _);
                seg.Subtype = side.Subtype;
                seg.SubtypeReason = string.IsNullOrWhiteSpace(side.Reason) ? side.Status : side.Reason;
                seg.SubtypeHints = side.Hints;
                seg.CertidaoExpected = side.Subtype == "despacho_conselho" ? "yes" : "no";
                if (string.IsNullOrWhiteSpace(side.Subtype))
                    seg.CertidaoExpected = "unknown";
            }
        }

        private static SubtypeSide ClassifyDespachoSubtypeFromPages(List<PageClassification> pages, TjpbDespachoConfig cfg, out string sample)
        {
            sample = "";
            if (pages == null || pages.Count == 0)
                return new SubtypeSide { Status = "no_pages" };

            var ordered = pages
                .OrderBy(p => p.Page)
                .ToList();

            var frontPage = ordered[0];
            var backPage = ordered.Count > 1 ? ordered[1] : ordered[0];

            var back = $"{backPage.BodyPrefix} {backPage.BodySuffix}".Trim();
            if (string.IsNullOrWhiteSpace(back))
                back = backPage.BodySuffix ?? backPage.BodyPrefix ?? "";

            var front = frontPage.BodyPrefix ?? "";
            sample = back;
            return ClassifyDespachoSubtypeSide(front, back, DocumentValidationRules.DocKeyDespacho, cfg);
        }

        private static bool TryLoadDespachoConfig(out TjpbDespachoConfig cfg, out string error)
        {
            cfg = new TjpbDespachoConfig();
            error = "";
            try
            {
                var cfgPath = ResolveConfigPath();
                if (string.IsNullOrWhiteSpace(cfgPath) || !File.Exists(cfgPath))
                {
                    error = "config_not_found";
                    return false;
                }
                cfg = TjpbDespachoConfig.Load(cfgPath);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        private static string MapTitleKeyToDoc(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            var norm = NormalizeTitleBody(key);
            return DocumentValidationRules.ResolveDocKeyFromHint(norm);
        }

        private static string ClassifyDocKey(PageClassification page)
        {
            return ClassifyDocKey(page, out _, out _);
        }

        private static string ClassifyDocKey(PageClassification page, out string method, out string matchedText)
        {
            method = "";
            matchedText = "";
            if (page == null)
                return "UNKNOWN";

            var hasTitleSignal = !string.IsNullOrWhiteSpace(page.TitleKey)
                || !string.IsNullOrWhiteSpace(page.Title)
                || !string.IsNullOrWhiteSpace(page.TitleNormalized);

            if (!hasTitleSignal)
            {
                method = "no_title";
                return "UNKNOWN";
            }

            var docKey = DocumentValidationRules.ClassifyDocByPageEvidence(
                page.TitleKey,
                page.Title,
                page.TitleNormalized,
                $"{page.BodyPrefix} {page.BodySuffix}",
                $"{page.TopText} {page.BodyPrefix} {page.BodySuffix} {page.BottomText}",
                out method);

            if (string.Equals(docKey, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            {
                matchedText = "";
                return "UNKNOWN";
            }

            matchedText = string.Equals(method, "body_prefix", StringComparison.OrdinalIgnoreCase)
                ? (page.BodyPrefix ?? page.SourceText ?? "")
                : (page.Title ?? page.TitleKey ?? page.SourceText ?? "");
            return docKey;
        }

        private sealed class ScoredCandidate
        {
            public DetectionSummary Summary { get; set; } = new DetectionSummary();
            public string DocType { get; set; } = "";
            public int Score { get; set; }
            public double SimhashSim { get; set; }
            public double TfidfSim { get; set; }
            public string Text { get; set; } = "";
        }

        private static List<DetectionSummary> SelectBestDocsPerType(string pdfPath, List<DetectionSummary> candidates)
        {
            var output = new List<DetectionSummary>();
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                return output;
            if (candidates == null || candidates.Count == 0)
                return output;

            DetectionResult? det;
            try
            {
                det = DocumentTitleDetector.Detect(pdfPath, DocumentValidationRules.BuildDefaultDetectionOptions());
            }
            catch
            {
                return output;
            }

            var pageMap = det.Pages.ToDictionary(p => p.Page, p => p);
            var modelCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var grouped = candidates
                .Select(c =>
                {
                    var docType = NormalizeDocTypeHint(c.TitleKey);
                    if (string.IsNullOrWhiteSpace(docType))
                        docType = MapTitleKeyToDoc(c.TitleKey);
                    return new { DocType = docType, Summary = c };
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.DocType))
                .GroupBy(x => x.DocType, StringComparer.OrdinalIgnoreCase);

            foreach (var group in grouped)
            {
                var docType = group.Key;
                if (!DocumentValidationRules.IsSupportedDocKey(docType))
                    continue;

                var scored = new List<ScoredCandidate>();
                foreach (var entry in group)
                {
                    if (!pageMap.TryGetValue(entry.Summary.StartPage, out var page))
                        continue;
                    var text = BuildCandidateText(page);
                    var score = ScoreCandidate(docType, entry.Summary, page);
                    scored.Add(new ScoredCandidate
                    {
                        Summary = entry.Summary,
                        DocType = docType,
                        Score = score,
                        Text = text
                    });
                }

                if (scored.Count == 0)
                    continue;

                // Despacho deve ter back_tail; se houver candidatos com back, descarta os sem back.
                if (DocumentValidationRules.IsDocMatch(docType, DocumentValidationRules.DocKeyDespacho) && scored.Any(s => s.Summary.BackBodyObj > 0))
                    scored = scored.Where(s => s.Summary.BackBodyObj > 0).ToList();

                var maxScore = scored.Max(s => s.Score);
                var tied = scored.Where(s => s.Score == maxScore).ToList();

                if (tied.Count > 1)
                {
                    if (!modelCache.TryGetValue(docType, out var modelText))
                    {
                        modelText = ExtractModelTextForDoc(docType);
                        modelCache[docType] = modelText;
                    }

                    if (!string.IsNullOrWhiteSpace(modelText))
                    {
                        var modelTokens = Tokenize(NormalizeTitleBody(modelText));
                        foreach (var cand in tied)
                        {
                            var candTokens = Tokenize(NormalizeTitleBody(cand.Text));
                            cand.SimhashSim = ComputeSimhashSimilarity(modelTokens, candTokens);
                            cand.TfidfSim = ComputeTfidfSimilarity(modelTokens, candTokens);
                        }

                        var best = tied
                            .OrderByDescending(s => (s.SimhashSim + s.TfidfSim) / 2.0)
                            .ThenBy(s => s.Summary.StartPage)
                            .First();
                        output.Add(best.Summary);
                        continue;
                    }
                }

                var chosen = tied.OrderBy(s => s.Summary.StartPage).First();
                output.Add(chosen.Summary);
            }

            return output;
        }

        private static DetectionSummary? SelectBestDocForType(string pdfPath, string docType)
        {
            if (string.IsNullOrWhiteSpace(docType))
                return null;
            var all = DetectAllDocs(pdfPath);
            var best = SelectBestDocsPerType(pdfPath, all);
            return best.FirstOrDefault(x =>
                string.Equals(NormalizeDocTypeHint(x.TitleKey), docType, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildCandidateText(PageClassification page)
        {
            return $"{page.TopText} {page.BodyPrefix} {page.BodySuffix}".Trim();
        }

        private static int ScoreCandidate(string docType, DetectionSummary summary, PageClassification page)
        {
            return DocumentValidationRules.ScoreDocumentCandidate(
                docType,
                hasBackBodyObj: summary.BackBodyObj > 0,
                top: page.TopText,
                body: $"{page.BodyPrefix} {page.BodySuffix}",
                combined: $"{page.TopText} {page.BodyPrefix} {page.BodySuffix} {page.BottomText}");
        }

        private static string ExtractModelTextForDoc(string docType)
        {
            var modelPath = ResolveModelPathForDoc(docType);
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                return "";

            try
            {
                var det = DocumentTitleDetector.Detect(modelPath);
                foreach (var page in det.Pages.OrderBy(p => p.Page))
                {
                    var key = ClassifyDocKey(page);
                    if (string.Equals(key, docType, StringComparison.OrdinalIgnoreCase))
                        return BuildCandidateText(page);
                }

                var first = det.Pages.OrderBy(p => p.Page).FirstOrDefault();
                return first != null ? BuildCandidateText(first) : "";
            }
            catch
            {
                return "";
            }
        }

        private static List<string> Tokenize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();
            var matches = Regex.Matches(text, "[a-z0-9]+");
            return matches.Select(m => m.Value).Where(v => v.Length > 0).ToList();
        }

        private static double ComputeSimhashSimilarity(List<string> tokensA, List<string> tokensB)
        {
            if (tokensA.Count == 0 || tokensB.Count == 0)
                return 0.0;

            var hashA = ComputeSimhash(tokensA);
            var hashB = ComputeSimhash(tokensB);
            var dist = Hamming(hashA, hashB);
            return 1.0 - (dist / 64.0);
        }

        private static ulong ComputeSimhash(List<string> tokens)
        {
            if (tokens.Count == 0)
                return 0;
            var v = new int[64];
            using var md5 = MD5.Create();
            foreach (var tok in tokens)
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(tok));
                ulong x = BitConverter.ToUInt64(bytes, 0);
                for (int i = 0; i < 64; i++)
                {
                    var bit = (x >> i) & 1;
                    v[i] += bit == 1 ? 1 : -1;
                }
            }
            ulong outHash = 0;
            for (int i = 0; i < 64; i++)
            {
                if (v[i] >= 0)
                    outHash |= (1UL << i);
            }
            return outHash;
        }

        private static int Hamming(ulong a, ulong b)
        {
            ulong x = a ^ b;
            int count = 0;
            while (x != 0)
            {
                x &= (x - 1);
                count++;
            }
            return count;
        }

        private static double ComputeTfidfSimilarity(List<string> tokensA, List<string> tokensB)
        {
            if (tokensA.Count == 0 || tokensB.Count == 0)
                return 0.0;

            var df = new Dictionary<string, int>(StringComparer.Ordinal);
            void AddDf(IEnumerable<string> tokens)
            {
                foreach (var t in tokens.Distinct())
                {
                    if (!df.ContainsKey(t)) df[t] = 0;
                    df[t]++;
                }
            }
            AddDf(tokensA);
            AddDf(tokensB);

            const int N = 2;
            var idf = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var kv in df)
                idf[kv.Key] = Math.Log((N + 1.0) / (kv.Value + 1.0)) + 1.0;

            var vecA = BuildTfidfVector(tokensA, idf);
            var vecB = BuildTfidfVector(tokensB, idf);

            var normA = Math.Sqrt(vecA.Values.Sum(v => v * v));
            var normB = Math.Sqrt(vecB.Values.Sum(v => v * v));
            if (normA == 0 || normB == 0)
                return 0.0;

            double dot = 0.0;
            var small = vecA.Count <= vecB.Count ? vecA : vecB;
            var big = vecA.Count <= vecB.Count ? vecB : vecA;
            foreach (var kv in small)
            {
                if (big.TryGetValue(kv.Key, out var v))
                    dot += kv.Value * v;
            }

            return dot / (normA * normB);
        }

        private static Dictionary<string, double> BuildTfidfVector(List<string> tokens, Dictionary<string, double> idf)
        {
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in tokens)
            {
                if (!idf.ContainsKey(t)) continue;
                if (!tf.ContainsKey(t)) tf[t] = 0;
                tf[t]++;
            }
            if (tf.Count == 0) return new Dictionary<string, double>(StringComparer.Ordinal);
            var maxTf = tf.Values.Max();
            var vec = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var kv in tf)
                vec[kv.Key] = (kv.Value / (double)maxTf) * idf[kv.Key];
            return vec;
        }

        private static bool TryExtractPageText(PdfDocument doc, int pageNumber, out string text)
        {
            text = "";
            if (doc == null || pageNumber <= 0 || pageNumber > doc.GetNumberOfPages())
                return false;

            var page = doc.GetPage(pageNumber);
            try
            {
                var pageText = PdfTextExtractor.GetTextFromPage(page, new SimpleTextExtractionStrategy());
                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    text = pageText;
                    return true;
                }
            }
            catch
            {
                // fallback to per-stream extraction
            }

            var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
            var contents = page.GetPdfObject().Get(PdfName.Contents);
            var sb = new StringBuilder();
            foreach (var stream in EnumerateStreams(contents))
            {
                var parts = PdfTextExtraction.CollectTextOperatorTexts(stream, resources);
                var piece = parts.Count == 0 ? "" : string.Join(" ", parts);
                if (string.IsNullOrWhiteSpace(piece))
                    continue;
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(piece);
            }

            text = sb.ToString();
            return !string.IsNullOrWhiteSpace(text);
        }

        private static string NormalizeTitleBody(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var collapsed = TextUtils.CollapseSpacedLettersText(text);
            return TextUtils.NormalizeForMatch(collapsed);
        }

        private static int ResolveSpanEnd(int startPage, List<PageClassification> pages)
        {
            if (pages.Count == 0) return startPage;
            var start = pages.FirstOrDefault(p => p.Page == startPage);
            if (start == null) return startPage;
            var key = start.TitleKey ?? "";
            int end = startPage;
            foreach (var page in pages.Where(p => p.Page >= startPage).OrderBy(p => p.Page))
            {
                if (!string.Equals(page.TitleKey ?? "", key, StringComparison.Ordinal))
                    break;
                end = page.Page;
            }
            return end;
        }

        private static string BuildPathRef(int page, int obj)
        {
            if (page <= 0) return "";
            if (obj > 0)
                return $"page={page}/obj={obj}";
            return $"page={page}";
        }

        private static (int StartPage, int EndPage) ResolveBookmarkRange(string pdfPath, int startPage)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath) || startPage <= 0)
                return (0, 0);

            using var doc = new iText.Kernel.Pdf.PdfDocument(new iText.Kernel.Pdf.PdfReader(pdfPath));
            var outlines = OutlineUtils.GetOutlinePages(doc);
            if (outlines.Count == 0)
                return (startPage, startPage);

            var ordered = outlines.OrderBy(o => o.Page).ToList();
            var next = ordered.FirstOrDefault(o => o.Page > startPage);
            var endPage = next.Page > 0 ? Math.Max(startPage, next.Page - 1) : startPage;
            return (startPage, endPage);
        }

        private static int FindFirstMarkerPage(string pdfPath, int startPage, int endPage)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                return 0;
            if (startPage <= 0) return 0;
            if (endPage < startPage) endPage = startPage;

            for (int p = startPage; p <= endPage; p++)
            {
                var pick = ContentsStreamPicker.Pick(new StreamPickRequest
                {
                    PdfPath = pdfPath,
                    Page = p,
                    RequireMarker = true
                });
                if (pick.Found)
                    return p;
            }

            return 0;
        }

        private static HeaderFooterSummary BuildHeaderFooter(string aPath, string bPath, int pageA, int pageB)
        {
            var summary = new HeaderFooterSummary();
            var backA = pageA + 1;
            var backB = pageB + 1;

            summary.FrontA = ToHeaderFooterPage(HeaderFooterProbe.Probe(aPath, pageA));
            summary.FrontB = ToHeaderFooterPage(HeaderFooterProbe.Probe(bPath, pageB));
            summary.BackA = ToHeaderFooterPage(HeaderFooterProbe.Probe(aPath, backA));
            summary.BackB = ToHeaderFooterPage(HeaderFooterProbe.Probe(bPath, backB));

            return summary;
        }

        private static HeaderFooterPageInfo? ToHeaderFooterPage(HeaderFooterPage? page)
        {
            if (page == null) return null;
            return new HeaderFooterPageInfo
            {
                Page = page.Page,
                PrimaryIndex = page.PrimaryIndex,
                PrimaryObj = page.PrimaryObj,
                PrimaryTextOps = page.PrimaryTextOps,
                PrimaryStreamLen = page.PrimaryStreamLen,
                HeaderText = page.HeaderText ?? "",
                FooterText = page.FooterText ?? "",
                FooterIndex = page.FooterIndex,
                FooterObj = page.FooterObj,
                HeaderKey = page.HeaderKey ?? ""
            };
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

        private static NlpSummary RunNlp(string aPath, string bPath, AlignRangeSummary align)
        {
            var summary = new NlpSummary();
            var baseA = Path.GetFileNameWithoutExtension(aPath);
            var baseB = Path.GetFileNameWithoutExtension(bPath);
            var outDir = Path.Combine("outputs", "objects_pipeline", $"{baseA}__{baseB}", "nlp");

            summary.FrontA = RunNlpSegment(baseA, "front_head_a", align.FrontA.ValueFull, outDir, summary.Errors);
            summary.FrontB = RunNlpSegment(baseB, "front_head_b", align.FrontB.ValueFull, outDir, summary.Errors);
            summary.BackA = RunNlpSegment(baseA, "back_tail_a", align.BackA.ValueFull, outDir, summary.Errors);
            summary.BackB = RunNlpSegment(baseB, "back_tail_b", align.BackB.ValueFull, outDir, summary.Errors);

            return summary;
        }

        private static FieldsSummary RunFields(string aPath, string bPath, AlignRangeSummary align, NlpSummary? nlp, DetectionSummary detA, DetectionSummary detB)
        {
            var summary = new FieldsSummary();
            if (nlp == null)
                return summary;

            var baseA = Path.GetFileNameWithoutExtension(aPath);
            var baseB = Path.GetFileNameWithoutExtension(bPath);
            var outDir = Path.Combine("outputs", "objects_pipeline", $"{baseA}__{baseB}", "fields");
            var docHintA = NormalizeDocTypeHint(detA.TitleKey);
            var docHintB = NormalizeDocTypeHint(detB.TitleKey);

            summary.FrontA = RunFieldsSegment(baseA, "front_head_a", align.FrontA.ValueFull, nlp.FrontA, outDir, docHintA, summary.Errors);
            summary.FrontB = RunFieldsSegment(baseB, "front_head_b", align.FrontB.ValueFull, nlp.FrontB, outDir, docHintB, summary.Errors);
            summary.BackA = RunFieldsSegment(baseA, "back_tail_a", align.BackA.ValueFull, nlp.BackA, outDir, docHintA, summary.Errors);
            summary.BackB = RunFieldsSegment(baseB, "back_tail_b", align.BackB.ValueFull, nlp.BackB, outDir, docHintB, summary.Errors);

            return summary;
        }

        private static string ResolveConfigPath()
        {
            var cwd = Directory.GetCurrentDirectory();
            var candidates = new[]
            {
                Path.Combine(cwd, "configs", "config.yaml"),
                Path.Combine(cwd, "configs", "config.yml"),
                Path.Combine(cwd, "OBJ", "configs", "config.yaml"),
                Path.Combine(cwd, "..", "configs", "config.yaml")
            };
            return candidates.FirstOrDefault(File.Exists) ?? "";
        }

        private static PipelineOptions LoadPipelineOptions()
        {
            var cfgPath = ResolveConfigPath();
            if (string.IsNullOrWhiteSpace(cfgPath) || !File.Exists(cfgPath))
                return new PipelineOptions();

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var cfg = deserializer.Deserialize<PipelineConfig>(File.ReadAllText(cfgPath));
                return cfg?.Pipeline ?? new PipelineOptions();
            }
            catch
            {
                return new PipelineOptions();
            }
        }

        private static FieldsSegment? RunFieldsSegment(string baseName, string label, string rawText, NlpSegment? nlpSeg, string outDir, string docTypeHint, List<string> errors)
        {
            if (nlpSeg == null)
                return null;
            if (string.IsNullOrWhiteSpace(rawText))
                return null;
            if (string.IsNullOrWhiteSpace(nlpSeg.NlpJsonPath) || !File.Exists(nlpSeg.NlpJsonPath))
            {
                errors.Add($"{label}: nlp_json_not_found");
                return new FieldsSegment { Label = label, Status = "error", Error = "nlp_json_not_found" };
            }

            var res = NlpFieldMapper.Run(new NlpFieldMapRequest
            {
                Label = label,
                BaseName = baseName,
                RawText = rawText,
                NlpJsonPath = nlpSeg.NlpJsonPath,
                OutputDir = outDir,
                DocTypeHint = docTypeHint
            });

            var seg = new FieldsSegment
            {
                Label = label,
                Status = res.Success ? "ok" : "error",
                JsonPath = res.JsonPath,
                Count = res.Fields?.Count ?? 0,
                Error = res.Error
            };

            if (!res.Success && !string.IsNullOrWhiteSpace(res.Error))
                errors.Add($"{label}: {res.Error}");

            return seg;
        }

        private static NlpSegment? RunNlpSegment(string baseName, string label, string text, string outDir, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var result = new NlpSegment { Label = label };
            var nlp = NlpRunner.Run(new NlpRequest
            {
                Text = text,
                Label = label,
                BaseName = baseName,
                OutputDir = outDir
            });

            result.TextPath = nlp.TextPath;
            result.NlpJsonPath = nlp.NlpJsonPath;
            result.TypedPath = nlp.TypedPath;
            result.CboOutPath = nlp.CboOutPath;
            result.Error = nlp.Error;
            result.Status = nlp.Success ? "ok" : "error";

            if (!nlp.Success && !string.IsNullOrWhiteSpace(nlp.Error))
                errors.Add($"{label}: {nlp.Error}");

            return result;
        }

        private static JsonElement? TryLoadJson(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.Clone();
            }
            catch
            {
                return null;
            }
        }

        private static Dictionary<string, JsonElement>? CollectNlpData(NlpSummary nlp)
        {
            var data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            AddJsonIfExists(data, "front_head_a", nlp.FrontA?.NlpJsonPath);
            AddJsonIfExists(data, "front_head_b", nlp.FrontB?.NlpJsonPath);
            AddJsonIfExists(data, "back_tail_a", nlp.BackA?.NlpJsonPath);
            AddJsonIfExists(data, "back_tail_b", nlp.BackB?.NlpJsonPath);
            return data.Count == 0 ? null : data;
        }

        private static Dictionary<string, JsonElement>? CollectFieldsData(FieldsSummary fields)
        {
            var data = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            AddJsonIfExists(data, "front_head_a", fields.FrontA?.JsonPath);
            AddJsonIfExists(data, "front_head_b", fields.FrontB?.JsonPath);
            AddJsonIfExists(data, "back_tail_a", fields.BackA?.JsonPath);
            AddJsonIfExists(data, "back_tail_b", fields.BackB?.JsonPath);
            return data.Count == 0 ? null : data;
        }

        private static void AddJsonIfExists(IDictionary<string, JsonElement> target, string key, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;
            var payload = TryLoadJson(path);
            if (payload.HasValue)
                target[key] = payload.Value;
        }

        private static void WriteDebugJson(string dir, string name, object? payload)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                var json = JsonSerializer.Serialize(payload, JsonUtils.Indented);
                File.WriteAllText(path, json);
            }
            catch
            {
                // debug output should not break pipeline
            }
        }

        private static void WriteDebugText(string dir, string name, string? text)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, text ?? "");
            }
            catch
            {
                // debug output should not break pipeline
            }
        }

        private static void WriteModuleJson(string debugDir, string module, string io, string name, object? payload)
        {
            try
            {
                var dir = Path.Combine(debugDir, "modules", module, io);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                var json = JsonSerializer.Serialize(payload, JsonUtils.Indented);
                File.WriteAllText(path, json);
            }
            catch
            {
                // debug output should not break pipeline
            }
        }

        private static void WriteModuleText(string debugDir, string module, string io, string name, string? text)
        {
            try
            {
                var dir = Path.Combine(debugDir, "modules", module, io);
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, text ?? "");
            }
            catch
            {
                // debug output should not break pipeline
            }
        }

        private static void WriteModuleFile(string debugDir, string module, string io, string name, string? sourcePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    return;
                var dir = Path.Combine(debugDir, "modules", module, io);
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, name);
                File.Copy(sourcePath, dest, true);
            }
            catch
            {
                // debug output should not break pipeline
            }
        }

        private static StreamInfo BuildStreamInfoSimple(int page, int obj, string reason)
        {
            return new StreamInfo
            {
                Page = page,
                Obj = obj,
                Reason = reason ?? "",
                PathRef = page > 0 && obj > 0 ? $"page={page}/obj={obj}" : (page > 0 ? $"page={page}" : "")
            };
        }
    }
}
