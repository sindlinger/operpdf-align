using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using iText.Kernel.Pdf;
using Obj.Align;
using Obj.DocDetector;
using Obj.FrontBack;
using Obj.Honorarios;
using Obj.Models;
using Obj.TjpbDespachoExtractor.Config;
using Obj.Utils;
using Obj.TjpbDespachoExtractor.Extraction;
using Obj.TjpbDespachoExtractor.Models;
using Obj.TjpbDespachoExtractor.Reference;
using Obj.TjpbDespachoExtractor.Utils;
using Obj.ValidatorModule;

namespace Obj.Commands
{
    internal static partial class ObjectsPipeline
    {
        private static bool SignatureFooterProbeEnabled = true;
        private static readonly string FinalizeDefaultOutDir = Path.Combine("outputs", "objects_finalize");
        private static readonly string[] FinalFieldNames =
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
            "VALOR_ARBITRADO_DE",
            "VALOR_ARBITRADO_CM",
            "VALOR_ARBITRADO_FINAL",
            "DATA_ARBITRADO_FINAL",
            "DATA_REQUISICAO",
            "ADIANTAMENTO",
            "PERCENTUAL",
            "PARCELA",
            "FATOR"
        };
        private static readonly Dictionary<string, HashSet<string>> FieldAllowedDocs =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Campos "de perito" aparecem em mais de um tipo documental (redundancia por design).
                { "PERITO", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DESPACHO", "REQUERIMENTO_HONORARIOS", "CERTIDAO_CM" } },
                { "CPF_PERITO", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DESPACHO", "REQUERIMENTO_HONORARIOS", "CERTIDAO_CM" } },
                { "ESPECIALIDADE", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DESPACHO", "REQUERIMENTO_HONORARIOS" } },
                { "ESPECIE_DA_PERICIA", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DESPACHO", "REQUERIMENTO_HONORARIOS" } },
                { "ADIANTAMENTO", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CERTIDAO_CM" } },
                { "PERCENTUAL", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CERTIDAO_CM" } },
                { "PARCELA", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CERTIDAO_CM" } },
                { "FATOR", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CERTIDAO_CM", "DESPACHO" } },
                { "VALOR_ARBITRADO_CM", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CERTIDAO_CM" } },
                { "VALOR_ARBITRADO_DE", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DESPACHO" } },
                { "VALOR_ARBITRADO_JZ", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DESPACHO", "REQUERIMENTO_HONORARIOS" } },
                { "DATA_REQUISICAO", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "REQUERIMENTO_HONORARIOS" } },
                { "DATA_ARBITRADO_FINAL", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DESPACHO", "CERTIDAO_CM" } },
                { "VALOR_ARBITRADO_FINAL", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { } }
            };
        private static readonly Dictionary<string, string[]> PrimaryFieldsByDocType =
            new(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "DESPACHO",
                    new[]
                    {
                        "PROCESSO_ADMINISTRATIVO",
                        "PROCESSO_JUDICIAL",
                        "PERITO",
                        "CPF_PERITO",
                        "VALOR_ARBITRADO_JZ",
                        "VALOR_ARBITRADO_DE",
                        "DATA_ARBITRADO_FINAL"
                    }
                },
                {
                    "CERTIDAO_CM",
                    new[]
                    {
                        "PROCESSO_ADMINISTRATIVO",
                        "PROCESSO_JUDICIAL",
                        "VALOR_ARBITRADO_CM",
                        "DATA_ARBITRADO_FINAL",
                        "ADIANTAMENTO",
                        "PERCENTUAL"
                    }
                },
                {
                    "REQUERIMENTO_HONORARIOS",
                    new[]
                    {
                        "PROCESSO_ADMINISTRATIVO",
                        "PROCESSO_JUDICIAL",
                        "VALOR_ARBITRADO_JZ",
                        "DATA_REQUISICAO",
                        "PROMOVENTE",
                        "PROMOVIDO"
                    }
                }
            };

        private sealed class FinalizeOptions
        {
            public bool Strict { get; set; }
            public string OutPath { get; set; } = "";
            public string ArtifactsDir { get; set; } = "";
        }

        internal sealed class FinalizeMeta
        {
            public string PdfPath { get; set; } = "";
            public string PdfName { get; set; } = "";
            public int TotalPages { get; set; }
            public bool Strict { get; set; }
            public string GeneratedAt { get; set; } = "";
        }

        internal sealed class FinalizeOutput
        {
            public FinalizeMeta Meta { get; set; } = new FinalizeMeta();
            public List<FinalizeDocument> Documents { get; set; } = new List<FinalizeDocument>();
            public Dictionary<string, FinalFieldValue> FinalFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public List<PipelineError> Errors { get; set; } = new List<PipelineError>();
        }

        internal sealed class FinalizeDocument
        {
            public string DocType { get; set; } = "";
            public int PageStart { get; set; }
            public int PageEnd { get; set; }
            public bool PrimaryValidatorPass { get; set; }
            public string PrimaryValidatorReason { get; set; } = "";
            public int PrimaryScore { get; set; }
            public int PrimaryHits { get; set; }
            public int PrimaryAllowedHits { get; set; }
            public List<DetectionEvidence> DetectionEvidence { get; set; } = new List<DetectionEvidence>();
            public Dictionary<string, FinalFieldValue> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public List<PipelineError> Errors { get; set; } = new List<PipelineError>();
        }

        internal sealed class DocSegment
        {
            public string DocType { get; set; } = "";
            public int PageStart { get; set; }
            public int PageEnd { get; set; }
            public int BodyObj { get; set; }
            public List<DetectionEvidence> Evidence { get; set; } = new List<DetectionEvidence>();
        }

        internal sealed class DetectionEvidence
        {
            public string Method { get; set; } = "";
            public string DocType { get; set; } = "";
            public int Page { get; set; }
            public string MatchedText { get; set; } = "";
            public string PathRef { get; set; } = "";
            public string Reason { get; set; } = "";
            public string OpRange { get; set; } = "";
            public int Obj { get; set; }
        }

        internal sealed class PipelineError
        {
            public string Code { get; set; } = "";
            public string Field { get; set; } = "";
            public string Message { get; set; } = "";
            public List<string> Tried { get; set; } = new List<string>();
            public List<FieldCandidate> Candidates { get; set; } = new List<FieldCandidate>();
        }

        internal sealed class SignatureCheck
        {
            public int Page { get; set; }
            public int Obj { get; set; }
            public string Source { get; set; } = "";
            public string PathRef { get; set; } = "";
            public string Status { get; set; } = "";
            public string DateText { get; set; } = "";
            public string DateIso { get; set; } = "";
            public string TextSample { get; set; } = "";
            public bool HasSignature { get; set; }
            public bool HasDate { get; set; }
        }

        internal sealed class BandInfo
        {
            public string Band { get; set; } = "";
            public string ValueFull { get; set; } = "";
            public string OpRange { get; set; } = "";
            public int Obj { get; set; }
            public int Page { get; set; }
        }

        internal sealed class FieldCandidate
        {
            public string Field { get; set; } = "";
            public string Value { get; set; } = "";
            public string ValueRaw { get; set; } = "";
            public string ValueFull { get; set; } = "";
            public string Source { get; set; } = "";
            public string OpRange { get; set; } = "";
            public int Obj { get; set; }
            public FinalBBox? BBox { get; set; }
            public int? Page { get; set; }
            public string Method { get; set; } = "";
            public string DocType { get; set; } = "";
            public double Confidence { get; set; }
        }

        internal sealed class FinalFieldValue
        {
            public string ValueFull { get; set; } = "";
            public string? ValueRaw { get; set; }
            public string? Value { get; set; }
            public string Source { get; set; } = "";
            public string OpRange { get; set; } = "";
            public int Obj { get; set; }
            public FinalBBox? BBox { get; set; }
            public int? Page { get; set; }
            public string DocType { get; set; } = "";
            public double? Confidence { get; set; }
            public string Method { get; set; } = "";
            public List<string> AnchorsMatched { get; set; } = new List<string>();
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? ValorTabelado { get; set; }
        }

        internal sealed class FinalBBox
        {
            public double X0 { get; set; }
            public double Y0 { get; set; }
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public int Items { get; set; }
        }

        private static void ExecuteFinalize(string[] args)
        {
            if (!ParseFinalizeOptions(args, out var inputs, out var options))
            {
                ShowFinalizeHelp();
                return;
            }

            if (inputs.Count == 0)
            {
                ShowFinalizeHelp();
                return;
            }

            var outPath = options.OutPath;
            var multiple = inputs.Count > 1 || (!string.IsNullOrWhiteSpace(outPath) && Directory.Exists(outPath));

            foreach (var input in inputs)
            {
                if (!File.Exists(input))
                {
                    Console.WriteLine($"PDF nao encontrado: {input}");
                    continue;
                }

                var artifactsDir = options.ArtifactsDir;
                if (string.IsNullOrWhiteSpace(artifactsDir))
                {
                    var baseName = Path.GetFileNameWithoutExtension(input);
                    artifactsDir = Path.Combine(FinalizeDefaultOutDir, baseName, "artifacts");
                }

                var runOptions = new FinalizeOptions
                {
                    Strict = options.Strict,
                    OutPath = options.OutPath,
                    ArtifactsDir = artifactsDir
                };

                FinalizeOutput result;
                try
                {
                    result = RunFinalizePipeline(input, runOptions);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Finalize erro: {input} -> {ex.GetType().Name}: {ex.Message}");
                    result = BuildFailureOutput(input, runOptions.Strict, ex);
                }
                var finalizeOptions = new JsonSerializerOptions(JsonUtils.Indented)
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.Never
                };
                var json = JsonSerializer.Serialize(result, finalizeOptions);

                var target = ResolveFinalizeOutPath(input, outPath, multiple);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    var dir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(target, json);
                    Console.WriteLine($"Finalize salvo: {target}");
                }
                else
                {
                    Console.WriteLine(json);
                }
            }
        }

        private static FinalizeOutput BuildFailureOutput(string pdfPath, bool strict, Exception ex)
        {
            var output = new FinalizeOutput();
            output.Meta = new FinalizeMeta
            {
                PdfPath = pdfPath,
                PdfName = Path.GetFileName(pdfPath),
                TotalPages = 0,
                Strict = strict,
                GeneratedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

            foreach (var field in FinalFieldNames)
                output.FinalFields[field] = BuildEmptyField(field, null, "", "");

            output.Errors.Add(new PipelineError
            {
                Code = "FINALIZE_EXCEPTION",
                Message = $"{ex.GetType().Name}: {ex.Message}"
            });

            return output;
        }

        private static bool ParseFinalizeOptions(string[] args, out List<string> inputs, out FinalizeOptions options)
        {
            inputs = new List<string>();
            options = new FinalizeOptions();

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == "--help" || arg == "-h")
                    return false;

                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputs.Add(args[++i]);
                    continue;
                }

                if (string.Equals(arg, "--inputs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    foreach (var raw in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        inputs.Add(raw.Trim());
                    continue;
                }

                if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.OutPath = args[++i];
                    continue;
                }

                if (string.Equals(arg, "--strict", StringComparison.OrdinalIgnoreCase))
                {
                    options.Strict = true;
                    continue;
                }

                if (string.Equals(arg, "--artifacts", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.ArtifactsDir = args[++i];
                    continue;
                }

                if (!arg.StartsWith("-", StringComparison.Ordinal))
                    inputs.Add(arg);
            }

            inputs = ExpandInputs(inputs);
            return true;
        }

        private static void ShowFinalizeHelp()
        {
            Console.WriteLine("operpdf inspect pipeline finalize --input <pdf|dir> [--out <arquivo|dir>] [--strict]");
            Console.WriteLine("  --inputs a.pdf,b.pdf   (opcional)");
            Console.WriteLine("  --artifacts <dir>      (opcional; salva YAMLs/mapfields)");
        }

        private static List<string> ExpandInputs(List<string> inputs)
        {
            var output = new List<string>();
            foreach (var raw in inputs)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                if (Directory.Exists(raw))
                {
                    output.AddRange(Directory.GetFiles(raw, "*.pdf", SearchOption.TopDirectoryOnly));
                    continue;
                }
                output.Add(raw);
            }

            return output.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ResolveFinalizeOutPath(string input, string outPath, bool multiple)
        {
            if (string.IsNullOrWhiteSpace(outPath))
            {
                var baseName = Path.GetFileNameWithoutExtension(input);
                return Path.Combine(FinalizeDefaultOutDir, $"{baseName}__final.json");
            }

            if (multiple || Directory.Exists(outPath))
            {
                var baseName = Path.GetFileNameWithoutExtension(input);
                var dir = Directory.Exists(outPath) ? outPath : Path.GetDirectoryName(outPath) ?? outPath;
                return Path.Combine(dir, $"{baseName}__final.json");
            }

            return outPath;
        }

        private static FinalizeOutput RunFinalizePipeline(string pdfPath, FinalizeOptions options)
        {
            var output = new FinalizeOutput();

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

            output.Meta = new FinalizeMeta
            {
                PdfPath = pdfPath,
                PdfName = Path.GetFileName(pdfPath),
                TotalPages = totalPages,
                Strict = options.Strict,
                GeneratedAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

            var errors = new List<PipelineError>();
            var segments = SegmentPdf(pdfPath, errors);
            if (segments.Count == 0)
            {
                errors.Add(new PipelineError
                {
                    Code = "NO_SEGMENTS",
                    Message = "Nenhum segmento identificado."
                });
            }

            var documents = new List<FinalizeDocument>();
            foreach (var segment in segments)
            {
                var docOutput = ExtractSegment(pdfPath, segment, options, errors);
                documents.Add(docOutput);
            }

            output.Documents = documents;
            var finalErrors = new List<PipelineError>();
            output.FinalFields = AggregateFinalFields(documents, options.Strict, finalErrors);
            var honorariosDerived = ComputeHonorariosDerivedFields(documents, options.Strict, errors);
            ApplyDerivedFinalFields(output.FinalFields, honorariosDerived);
            ValidateFinalNameFields(output.FinalFields, options.Strict, errors);
            output.Errors = errors.Concat(finalErrors).ToList();

            return output;
        }

        private static List<DocSegment> SegmentPdf(string pdfPath, List<PipelineError> errors)
        {
            DetectionResult? detection = null;

            var opts = DocumentValidationRules.BuildDefaultDetectionOptions();

            try
            {
                detection = DocumentTitleDetector.Detect(pdfPath, opts);
            }
            catch (Exception ex)
            {
                errors.Add(new PipelineError
                {
                    Code = "TITLE_DETECT_FAILED",
                    Message = ex.GetType().Name
                });
                return new List<DocSegment>();
            }

            var pages = detection.Pages.OrderBy(p => p.Page).ToList();
            var bookmarkMap = BuildBookmarkMap(pdfPath);
            var prefixHit = ContentsPrefixDetector.Detect(pdfPath);
            var headerHit = HeaderLabelDetector.Detect(pdfPath);
            var largestHit = LargestContentsDetector.Detect(pdfPath);

            var despachoConfirm = BuildDespachoConfirmationMap(pdfPath, pages);
            var segments = SegmentPages(pages, bookmarkMap, prefixHit, headerHit, largestHit, despachoConfirm, errors);
            ValidateDespachoSegments(pdfPath, segments, errors);
            ValidateSegmentLengths(segments, errors);
            SelectBestSegmentsByType(segments, pages, errors);
            return segments;
        }

        internal static string BuildDetectDocsIoJson(string pdfPath)
        {
            var errors = new List<PipelineError>();
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                var err = new { Error = "pdf_not_found", PdfPath = pdfPath ?? "" };
                return JsonSerializer.Serialize(err, JsonUtils.Indented);
            }

            var opts = DocumentValidationRules.BuildDefaultDetectionOptions();

            DetectionResult? detection;
            try
            {
                detection = DocumentTitleDetector.Detect(pdfPath, opts);
            }
            catch (Exception ex)
            {
                var err = new { Error = "title_detect_failed", PdfPath = pdfPath, Message = ex.GetType().Name };
                return JsonSerializer.Serialize(err, JsonUtils.Indented);
            }

            var pages = detection.Pages.OrderBy(p => p.Page).ToList();
            var bookmarkMap = BuildBookmarkMap(pdfPath);
            var prefixHit = ContentsPrefixDetector.Detect(pdfPath);
            var headerHit = HeaderLabelDetector.Detect(pdfPath);
            var largestHit = LargestContentsDetector.Detect(pdfPath);
            var despachoConfirm = BuildDespachoConfirmationMap(pdfPath, pages);

            var segments = SegmentPages(pages, bookmarkMap, prefixHit, headerHit, largestHit, despachoConfirm, errors, out var pageInfos);
            ValidateDespachoSegments(pdfPath, segments, errors);
            ValidateSegmentLengths(segments, errors);
            SelectBestSegmentsByType(segments, pages, errors);

            var subtypeMap = new Dictionary<int, SubtypeSide>();
            if (TryLoadDespachoConfig(out var cfg, out _))
            {
                foreach (var seg in segments)
                {
                    if (!string.Equals(seg.DocType, "DESPACHO", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var segPages = pages.Where(p => p.Page >= seg.PageStart && p.Page <= seg.PageEnd).ToList();
                    if (segPages.Count == 0)
                        continue;
                    var side = ClassifyDespachoSubtypeFromPages(segPages, cfg, out _);
                    subtypeMap[seg.PageStart] = side;
                }
            }

            var payload = new
            {
                Meta = new
                {
                    PdfPath = pdfPath,
                    PageCount = pages.Count,
                    Options = new
                    {
                        opts.PrefixOpCount,
                        opts.SuffixOpCount,
                        opts.CarryForward,
                        opts.TopBandPct,
                        opts.BottomBandPct,
                        opts.UseTopTextFallback,
                        Keywords = opts.Keywords
                    }
                },
                Signals = new
                {
                    Bookmarks = bookmarkMap.Values.ToList(),
                    PrefixHit = prefixHit,
                    HeaderHit = headerHit,
                    LargestHit = largestHit,
                    DespachoConfirm = despachoConfirm.Values.ToList()
                },
                Pages = pageInfos.Select(info => new
                {
                    info.Page.Page,
                    DocType = info.DocType,
                    info.Conflict,
                    info.Evidence,
                    info.Page.TitleKey,
                    info.Page.Title,
                    info.Page.TopText,
                    info.Page.BottomText,
                    info.Page.BodyPrefix,
                    info.Page.BodySuffix,
                    info.Page.BodyObj,
                    info.Page.BodyIndex,
                    info.Page.BodyTextOps,
                    info.Page.BodyStreamLen,
                    info.Page.PathRef,
                    info.Page.OpRange,
                    info.Page.SourceText,
                    info.Page.MatchedKeyword,
                    info.Page.BodyMatchedKeyword,
                    info.Page.StreamPrefixes
                }).ToList(),
                Segments = segments.Select(s => new
                {
                    s.DocType,
                    s.PageStart,
                    s.PageEnd,
                    s.BodyObj,
                    s.Evidence,
                    Subtype = subtypeMap.TryGetValue(s.PageStart, out var side) ? side.Subtype : "",
                    SubtypeReason = subtypeMap.TryGetValue(s.PageStart, out side) ? side.Reason : "",
                    SubtypeHints = subtypeMap.TryGetValue(s.PageStart, out side) ? side.Hints : new List<string>(),
                    CertidaoExpected = subtypeMap.TryGetValue(s.PageStart, out side) && side.Subtype == "despacho_conselho" ? "yes" : (subtypeMap.ContainsKey(s.PageStart) ? "no" : "")
                }).ToList(),
                Errors = errors
            };

            return JsonSerializer.Serialize(payload, JsonUtils.Indented);
        }

        internal static bool SetSignatureFooterProbeEnabled(bool enabled)
        {
            var prev = SignatureFooterProbeEnabled;
            SignatureFooterProbeEnabled = enabled;
            return prev;
        }

        private static List<DocSegment> SegmentPages(
            List<PageClassification> pages,
            Dictionary<int, DetectionEvidence> bookmarkMap,
            DetectionHit prefixHit,
            DetectionHit headerHit,
            DetectionHit largestHit,
            Dictionary<int, SignatureCheck> despachoConfirm,
            List<PipelineError> errors)
        {
            return SegmentPages(pages, bookmarkMap, prefixHit, headerHit, largestHit, despachoConfirm, errors, out _);
        }

        private static List<DocSegment> SegmentPages(
            List<PageClassification> pages,
            Dictionary<int, DetectionEvidence> bookmarkMap,
            DetectionHit prefixHit,
            DetectionHit headerHit,
            DetectionHit largestHit,
            Dictionary<int, SignatureCheck> despachoConfirm,
            List<PipelineError> errors,
            out List<PageDocInfo> pageInfos)
        {
            if (pages == null || pages.Count == 0)
            {
                pageInfos = new List<PageDocInfo>();
                return new List<DocSegment>();
            }

            var safeBookmarkMap = bookmarkMap ?? new Dictionary<int, DetectionEvidence>();

            if (safeBookmarkMap.Count > 0)
            {
                pageInfos = BuildPageInfos(pages, safeBookmarkMap, prefixHit, headerHit, largestHit, despachoConfirm, errors);
                return SegmentPagesByBookmarks(
                    pages,
                    safeBookmarkMap,
                    prefixHit,
                    headerHit,
                    largestHit,
                    despachoConfirm,
                    errors);
            }

            return SegmentPagesByClassification(
                pages,
                safeBookmarkMap,
                prefixHit,
                headerHit,
                largestHit,
                despachoConfirm,
                errors,
                out pageInfos);
        }

        private static List<DocSegment> SegmentPagesByBookmarks(
            List<PageClassification> pages,
            Dictionary<int, DetectionEvidence> bookmarkMap,
            DetectionHit prefixHit,
            DetectionHit headerHit,
            DetectionHit largestHit,
            Dictionary<int, SignatureCheck> despachoConfirm,
            List<PipelineError> errors)
        {
            var segments = new List<DocSegment>();
            if (pages == null || pages.Count == 0 || bookmarkMap == null || bookmarkMap.Count == 0)
                return segments;

            var orderedBookmarks = bookmarkMap
                .Where(kv => kv.Key > 0)
                .OrderBy(kv => kv.Key)
                .ToList();

            if (orderedBookmarks.Count == 0)
                return segments;

            var minPage = pages.Min(p => p.Page);
            var maxPage = pages.Max(p => p.Page);

            if (orderedBookmarks[0].Key > minPage)
            {
                var leadingPages = pages
                    .Where(p => p.Page < orderedBookmarks[0].Key)
                    .OrderBy(p => p.Page)
                    .ToList();

                if (leadingPages.Count > 0)
                {
                    segments.AddRange(SegmentPagesByClassification(
                        leadingPages,
                        bookmarkMap,
                        prefixHit,
                        headerHit,
                        largestHit,
                        despachoConfirm,
                        errors));
                }
            }

            for (var idx = 0; idx < orderedBookmarks.Count; idx++)
            {
                var start = orderedBookmarks[idx].Key;
                var end = idx + 1 < orderedBookmarks.Count ? orderedBookmarks[idx + 1].Key - 1 : maxPage;
                if (end < start)
                    end = start;

                var segmentPages = pages
                    .Where(p => p.Page >= start && p.Page <= end)
                    .OrderBy(p => p.Page)
                    .ToList();

                if (segmentPages.Count == 0)
                    continue;

                var bookmarkEvidence = orderedBookmarks[idx].Value;
                var docType = string.IsNullOrWhiteSpace(bookmarkEvidence.DocType) ? "UNKNOWN" : bookmarkEvidence.DocType;

                var segment = new DocSegment
                {
                    DocType = docType,
                    PageStart = start,
                    PageEnd = end
                };

                segment.Evidence.Add(new DetectionEvidence
                {
                    Method = "bookmark_segment",
                    DocType = docType,
                    Page = start,
                    MatchedText = bookmarkEvidence.MatchedText,
                    PathRef = bookmarkEvidence.PathRef,
                    Reason = "bookmark_range",
                    OpRange = bookmarkEvidence.OpRange,
                    Obj = bookmarkEvidence.Obj
                });

                foreach (var page in segmentPages)
                {
                    if (segment.BodyObj <= 0 && page.BodyObj > 0)
                        segment.BodyObj = page.BodyObj;
                }

                segments.Add(segment);
            }

            return segments;
        }

        private static List<DocSegment> SegmentPagesByClassification(
            List<PageClassification> pages,
            Dictionary<int, DetectionEvidence> bookmarkMap,
            DetectionHit prefixHit,
            DetectionHit headerHit,
            DetectionHit largestHit,
            Dictionary<int, SignatureCheck> despachoConfirm,
            List<PipelineError> errors)
        {
            return SegmentPagesByClassification(
                pages,
                bookmarkMap,
                prefixHit,
                headerHit,
                largestHit,
                despachoConfirm,
                errors,
                out _);
        }

        private static List<DocSegment> SegmentPagesByClassification(
            List<PageClassification> pages,
            Dictionary<int, DetectionEvidence> bookmarkMap,
            DetectionHit prefixHit,
            DetectionHit headerHit,
            DetectionHit largestHit,
            Dictionary<int, SignatureCheck> despachoConfirm,
            List<PipelineError> errors,
            out List<PageDocInfo> pageInfos)
        {
            var segments = new List<DocSegment>();
            if (pages == null || pages.Count == 0)
            {
                pageInfos = new List<PageDocInfo>();
                return segments;
            }

            var infos = BuildPageInfos(pages, bookmarkMap, prefixHit, headerHit, largestHit, despachoConfirm, errors);
            pageInfos = infos;

            DocSegment? current = null;
            var currentLen = 0;
            foreach (var info in infos)
            {
                var maxPages = current != null ? GetMaxPagesForDoc(current.DocType) : GetMaxPagesForDoc(info.DocType);
                var sameType = current != null
                    && (string.Equals(current.DocType, info.DocType, StringComparison.OrdinalIgnoreCase)
                        || (string.Equals(info.DocType, "UNKNOWN", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(current.DocType, "UNKNOWN", StringComparison.OrdinalIgnoreCase)));
                var hitMax = maxPages > 0 && currentLen >= maxPages;

                if (current == null || !sameType || hitMax)
                {
                    if (current != null)
                        segments.Add(current);

                    current = new DocSegment
                    {
                        DocType = info.DocType,
                        PageStart = info.Page.Page,
                        PageEnd = info.Page.Page,
                        BodyObj = info.Page.BodyObj
                    };
                    currentLen = 1;
                }
                else
                {
                    current.PageEnd = info.Page.Page;
                    currentLen++;
                }

                current.Evidence.AddRange(info.Evidence);
                if (current.BodyObj <= 0 && info.Page.BodyObj > 0)
                    current.BodyObj = info.Page.BodyObj;
            }

            if (current != null)
                segments.Add(current);

            return segments;
        }

        private static List<PageDocInfo> BuildPageInfos(
            List<PageClassification> pages,
            Dictionary<int, DetectionEvidence> bookmarkMap,
            DetectionHit prefixHit,
            DetectionHit headerHit,
            DetectionHit largestHit,
            Dictionary<int, SignatureCheck> despachoConfirm,
            List<PipelineError> errors)
        {
            var infos = new List<PageDocInfo>();
            if (pages == null || pages.Count == 0)
                return infos;

            var lastConfirmedDespachoStart = 0;
            foreach (var page in pages)
            {
                var evidence = BuildEvidenceForPage(page, bookmarkMap, prefixHit, headerHit, largestHit);
                var resolved = ResolveDocType(evidence, out var conflict, out var strong);
                var strongDoc = GetStrongDocType(evidence);
                if (!string.IsNullOrWhiteSpace(strongDoc))
                    resolved = strongDoc;
                var hasStrongEvidence = !string.IsNullOrWhiteSpace(strongDoc);
                var followupDespacho = lastConfirmedDespachoStart > 0 && page.Page == lastConfirmedDespachoStart + 1;
                var hasConfirm = false;
                SignatureCheck? confirm = null;
                if (despachoConfirm != null)
                    despachoConfirm.TryGetValue(page.Page, out confirm);
                if (confirm != null && confirm.HasSignature && confirm.HasDate)
                    hasConfirm = true;

                if (followupDespacho && string.Equals(resolved, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                {
                    resolved = "DESPACHO";
                    evidence.Add(new DetectionEvidence
                    {
                        Method = "despacho_followup_page",
                        DocType = "DESPACHO",
                        Page = page.Page,
                        Reason = "confirmed_next_page",
                        Obj = page.BodyObj
                    });
                }

                if (hasConfirm && !string.Equals(resolved, "DESPACHO", StringComparison.OrdinalIgnoreCase))
                {
                    resolved = "DESPACHO";
                    evidence.Add(new DetectionEvidence
                    {
                        Method = "despacho_confirm",
                        DocType = "DESPACHO",
                        Page = page.Page,
                        MatchedText = confirm?.DateText ?? "",
                        PathRef = confirm?.PathRef ?? "",
                        Reason = confirm?.Source ?? "signature_confirm",
                        Obj = confirm?.Obj ?? page.BodyObj
                    });
                    lastConfirmedDespachoStart = page.Page;
                }

                if (conflict)
                {
                    if (!strong)
                    {
                        errors.Add(new PipelineError
                        {
                            Code = "DOC_TYPE_CONFLICT",
                            Message = $"Conflito de tipo na pagina {page.Page}"
                        });
                    }
                    else
                    {
                        evidence.Add(new DetectionEvidence
                        {
                            Method = "doc_type_conflict",
                            DocType = resolved,
                            Page = page.Page,
                            Reason = "conflict_ignored_strong_title",
                            Obj = page.BodyObj
                        });
                    }
                }

                if (string.Equals(resolved, "DESPACHO", StringComparison.OrdinalIgnoreCase))
                {
                    if (confirm != null)
                    {
                        if (hasConfirm)
                        {
                            evidence.Add(new DetectionEvidence
                            {
                                Method = "despacho_confirm",
                                DocType = "DESPACHO",
                                Page = page.Page,
                                MatchedText = confirm.DateText,
                                PathRef = confirm.PathRef,
                                Reason = confirm.Source,
                                Obj = confirm.Obj
                            });
                            lastConfirmedDespachoStart = page.Page;
                        }
                        else if (!followupDespacho)
                        {
                            evidence.Add(new DetectionEvidence
                            {
                                Method = "despacho_confirm",
                                DocType = hasStrongEvidence ? "DESPACHO" : "UNKNOWN",
                                Page = page.Page,
                                MatchedText = confirm.TextSample,
                                PathRef = confirm.PathRef,
                                Reason = confirm.Status,
                                Obj = confirm.Obj
                            });
                            if (!hasStrongEvidence)
                                resolved = "UNKNOWN";
                        }
                    }
                    else if (!followupDespacho)
                    {
                        evidence.Add(new DetectionEvidence
                        {
                            Method = "despacho_confirm",
                            DocType = hasStrongEvidence ? "DESPACHO" : "UNKNOWN",
                            Page = page.Page,
                            Reason = "despacho_not_confirmed",
                            Obj = page.BodyObj
                        });
                        if (!hasStrongEvidence)
                            resolved = "UNKNOWN";
                    }
                }

                infos.Add(new PageDocInfo
                {
                    Page = page,
                    DocType = resolved,
                    Evidence = evidence,
                    Conflict = conflict
                });
            }

            FillUnknownGaps(infos);
            return infos;
        }

        private sealed class PageDocInfo
        {
            public PageClassification Page { get; set; } = new PageClassification();
            public string DocType { get; set; } = "";
            public List<DetectionEvidence> Evidence { get; set; } = new List<DetectionEvidence>();
            public bool Conflict { get; set; }
        }

        private static void FillUnknownGaps(List<PageDocInfo> infos)
        {
            if (infos == null || infos.Count == 0)
                return;

            var i = 0;
            while (i < infos.Count)
            {
                if (!string.Equals(infos[i].DocType, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    continue;
                }

                var start = i;
                while (i < infos.Count && string.Equals(infos[i].DocType, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                    i++;
                var end = i - 1;

                var prevIndex = start - 1;
                var nextIndex = i;
                var prevType = prevIndex >= 0 ? infos[prevIndex].DocType : "";
                var nextType = nextIndex < infos.Count ? infos[nextIndex].DocType : "";

                if (string.IsNullOrWhiteSpace(prevType) || string.IsNullOrWhiteSpace(nextType))
                    continue;
                if (string.Equals(prevType, "UNKNOWN", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(nextType, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.Equals(prevType, nextType, StringComparison.OrdinalIgnoreCase))
                    continue;

                var maxPages = GetMaxPagesForDoc(prevType);
                if (maxPages > 0)
                {
                    var totalLen = nextIndex - prevIndex + 1;
                    if (totalLen > maxPages)
                        continue;
                }

                for (var j = start; j <= end; j++)
                {
                    infos[j].DocType = prevType;
                    infos[j].Evidence.Add(new DetectionEvidence
                    {
                        Method = "doc_gap_fill",
                        DocType = prevType,
                        Page = infos[j].Page.Page,
                        Reason = "between_same_type",
                        Obj = infos[j].Page.BodyObj
                    });
                }
            }
        }

        private static int GetMaxPagesForDoc(string docType)
        {
            if (string.Equals(docType, "DESPACHO", StringComparison.OrdinalIgnoreCase))
                return 3;
            if (string.Equals(docType, "REQUERIMENTO_HONORARIOS", StringComparison.OrdinalIgnoreCase))
                return 3;
            if (string.Equals(docType, "CERTIDAO_CM", StringComparison.OrdinalIgnoreCase))
                return 2;
            return 0;
        }

        private static Dictionary<int, SignatureCheck> BuildDespachoConfirmationMap(string pdfPath, List<PageClassification> pages)
        {
            var map = new Dictionary<int, SignatureCheck>();
            if (pages == null || pages.Count == 0)
                return map;

            var maxPage = pages.Max(p => p.Page);
            var pageMap = pages
                .Where(p => p.Page > 0)
                .GroupBy(p => p.Page)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var page in pages)
            {
                var docKey = ClassifyDocKey(page);
                var mapped = string.IsNullOrWhiteSpace(docKey) ? "" : MapDocKeyToOutput(docKey);
                if (!string.Equals(mapped, "DESPACHO", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (page.Page >= maxPage)
                    continue;

                if (pageMap.TryGetValue(page.Page + 1, out var nextPage))
                {
                    var nextKey = ClassifyDocKey(nextPage);
                    var nextMapped = string.IsNullOrWhiteSpace(nextKey) ? "" : MapDocKeyToOutput(nextKey);
                    if (!string.IsNullOrWhiteSpace(nextMapped)
                        && !string.Equals(nextMapped, "DESPACHO", StringComparison.OrdinalIgnoreCase))
                    {
                        map[page.Page] = new SignatureCheck
                        {
                            Page = page.Page + 1,
                            Status = $"next_page_doc_conflict:{nextMapped}"
                        };
                        continue;
                    }
                }

                var signature = FindDespachoSignature(pdfPath, page.Page + 1);
                map[page.Page] = signature;
            }

            return map;
        }

        private static void ValidateDespachoSegments(string pdfPath, List<DocSegment> segments, List<PipelineError> errors)
        {
            if (segments == null || segments.Count == 0)
                return;

            foreach (var segment in segments)
            {
                if (!string.Equals(segment.DocType, "DESPACHO", StringComparison.OrdinalIgnoreCase))
                    continue;

                var pageCount = segment.PageEnd - segment.PageStart + 1;
                if (pageCount < 2)
                {
                    segment.Evidence.Add(new DetectionEvidence
                    {
                        Method = "despacho_segment",
                        DocType = "UNKNOWN",
                        Page = segment.PageStart,
                        Reason = "despacho_requires_two_pages",
                        Obj = segment.BodyObj
                    });
                    errors.Add(new PipelineError
                    {
                        Code = "DESPACHO_TOO_SHORT",
                        Message = $"Despacho com apenas {pageCount} pagina(s)."
                    });
                    segment.DocType = "UNKNOWN";
                    continue;
                }

                SignatureCheck signature = new SignatureCheck { Page = segment.PageStart, Status = "signature_not_checked" };
                var signatureFound = false;
                for (var p = segment.PageStart; p <= segment.PageEnd; p++)
                {
                    var candidate = FindDespachoSignature(pdfPath, p);
                    if (candidate.HasSignature && candidate.HasDate)
                    {
                        signature = candidate;
                        signatureFound = true;
                        break;
                    }
                    if (!signatureFound && !string.IsNullOrWhiteSpace(candidate.TextSample))
                        signature = candidate;
                }
                if (!signatureFound)
                {
                    segment.Evidence.Add(new DetectionEvidence
                    {
                        Method = "signature_footer",
                        DocType = "DESPACHO",
                        Page = signature.Page > 0 ? signature.Page : segment.PageEnd,
                        MatchedText = signature.TextSample,
                        PathRef = signature.PathRef,
                        Reason = string.IsNullOrWhiteSpace(signature.Status) ? "signature_missing" : signature.Status,
                        Obj = signature.Obj
                    });
                    errors.Add(new PipelineError
                    {
                        Code = "DESPACHO_SIGNATURE_MISSING",
                        Message = $"Despacho sem assinatura/data no intervalo {segment.PageStart}-{segment.PageEnd}."
                    });
                    continue;
                }

                segment.Evidence.Add(new DetectionEvidence
                {
                    Method = "signature_footer",
                    DocType = "DESPACHO",
                    Page = signature.Page,
                    MatchedText = signature.DateText,
                    PathRef = signature.PathRef,
                    Reason = signature.Source,
                    Obj = signature.Obj
                });
            }
        }

        private static void ValidateSegmentLengths(List<DocSegment> segments, List<PipelineError> errors)
        {
            if (segments == null || segments.Count == 0)
                return;

            foreach (var segment in segments)
            {
                if (string.IsNullOrWhiteSpace(segment.DocType))
                    continue;
                if (string.Equals(segment.DocType, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                    continue;

                var maxPages = GetMaxPagesForDoc(segment.DocType);
                if (maxPages <= 0)
                    continue;

                var len = segment.PageEnd - segment.PageStart + 1;
                if (len <= maxPages)
                    continue;

                segment.Evidence.Add(new DetectionEvidence
                {
                    Method = "segment_length",
                    DocType = segment.DocType,
                    Page = segment.PageStart,
                    Reason = $"max_pages={maxPages}",
                    Obj = segment.BodyObj
                });
                errors.Add(new PipelineError
                {
                    Code = "DOC_TOO_LONG",
                    Message = $"{segment.DocType} com {len} paginas (max {maxPages}) no intervalo {segment.PageStart}-{segment.PageEnd}."
                });
            }
        }

        private static void SelectBestSegmentsByType(
            List<DocSegment> segments,
            List<PageClassification> pages,
            List<PipelineError> errors)
        {
            if (segments == null || segments.Count == 0)
                return;
            if (pages == null || pages.Count == 0)
                return;

            var pageMap = pages
                .Where(p => p.Page > 0)
                .GroupBy(p => p.Page)
                .ToDictionary(g => g.Key, g => g.First());

            var docTypes = new[] { "DESPACHO", "REQUERIMENTO_HONORARIOS", "CERTIDAO_CM" };
            foreach (var docType in docTypes)
            {
                var candidates = segments
                    .Where(s => string.Equals(s.DocType, docType, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (candidates.Count <= 1)
                    continue;

                var scored = candidates
                    .Select(s =>
                    {
                        var score = ComputeSegmentScore(s, pageMap, out var avgDensity, out var avgWeird);
                        return new { Segment = s, Score = score, AvgDensity = avgDensity, AvgWeird = avgWeird };
                    })
                    .OrderByDescending(x => x.Score)
                    .ToList();

                var winner = scored.First();
                winner.Segment.Evidence.Add(new DetectionEvidence
                {
                    Method = "doc_score",
                    DocType = winner.Segment.DocType,
                    Page = winner.Segment.PageStart,
                    Reason = $"pages={winner.Segment.PageEnd - winner.Segment.PageStart + 1};density={winner.AvgDensity:F2};weird={winner.AvgWeird:F2};score={winner.Score:F1}",
                    Obj = winner.Segment.BodyObj
                });

                foreach (var loser in scored.Skip(1))
                {
                    loser.Segment.Evidence.Add(new DetectionEvidence
                    {
                        Method = "doc_candidate",
                        DocType = loser.Segment.DocType,
                        Page = loser.Segment.PageStart,
                        Reason = $"winner={winner.Segment.PageStart}-{winner.Segment.PageEnd}|score={winner.Score:F1};self={loser.Score:F1}",
                        Obj = loser.Segment.BodyObj
                    });
                }
            }
        }

        private static double ComputeSegmentScore(
            DocSegment segment,
            Dictionary<int, PageClassification> pageMap,
            out double avgDensity,
            out double avgWeird)
        {
            var pageCount = Math.Max(1, segment.PageEnd - segment.PageStart + 1);
            var densities = new List<double>();
            var weirds = new List<double>();

            for (var p = segment.PageStart; p <= segment.PageEnd; p++)
            {
                if (!pageMap.TryGetValue(p, out var page))
                    continue;
                var density = page.BodyStreamLen > 0
                    ? (page.BodyTextOps * 1000.0) / page.BodyStreamLen
                    : 0.0;
                densities.Add(density);

                var sample = $"{page.BodyPrefix} {page.BodySuffix}".Trim();
                weirds.Add(TextUtils.ComputeWeirdSpacingRatio(sample));
            }

            avgDensity = densities.Count > 0 ? densities.Average() : 0.0;
            avgWeird = weirds.Count > 0 ? weirds.Average() : 0.0;

            var score = pageCount * 1000.0;
            score += avgDensity * 10.0;
            score -= avgWeird * 200.0;

            if (segment.Evidence.Any(e => string.Equals(e.Method, "bookmark_segment", StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(e.Method, "bookmark", StringComparison.OrdinalIgnoreCase)))
                score += 50.0;
            if (segment.Evidence.Any(e => string.Equals(e.Method, "contents_title", StringComparison.OrdinalIgnoreCase)))
                score += 30.0;
            if (segment.Evidence.Any(e => string.Equals(e.Method, "signature_footer", StringComparison.OrdinalIgnoreCase)
                                          || string.Equals(e.Method, "despacho_confirm", StringComparison.OrdinalIgnoreCase)))
                score += 200.0;

            return score;
        }

        private static Dictionary<int, DetectionEvidence> BuildBookmarkMap(string pdfPath)
        {
            var output = new Dictionary<int, DetectionEvidence>();
            try
            {
                using var doc = new PdfDocument(new PdfReader(pdfPath));
                var bookmarks = OutlineUtils.GetOutlinePages(doc);
                foreach (var bm in bookmarks)
                {
                    var docType = ClassifyTitleToDoc(bm.Title);
                    if (string.IsNullOrWhiteSpace(docType))
                        continue;

                    output[bm.Page] = new DetectionEvidence
                    {
                        Method = "bookmark",
                        DocType = docType,
                        Page = bm.Page,
                        MatchedText = bm.Title,
                        Reason = "bookmark"
                    };
                }
            }
            catch
            {
                return output;
            }

            return output;
        }

        private static List<DetectionEvidence> BuildEvidenceForPage(
            PageClassification page,
            Dictionary<int, DetectionEvidence> bookmarkMap,
            DetectionHit prefixHit,
            DetectionHit headerHit,
            DetectionHit largestHit)
        {
            var evidence = new List<DetectionEvidence>();

            if (bookmarkMap.TryGetValue(page.Page, out var bm))
                evidence.Add(bm);

            var docKey = ClassifyDocKey(page, out var method, out var matchedText);
            var mapped = string.IsNullOrWhiteSpace(docKey) ? "" : MapDocKeyToOutput(docKey);
            if (!string.IsNullOrWhiteSpace(mapped))
            {
                var resolvedMethod = string.IsNullOrWhiteSpace(method) ? "contents_title" : method;
                if (string.Equals(resolvedMethod, "title_top_bottom", StringComparison.OrdinalIgnoreCase))
                    resolvedMethod = "contents_title";
                var resolvedText = !string.IsNullOrWhiteSpace(matchedText)
                    ? matchedText
                    : page.Title ?? page.TitleKey ?? page.SourceText ?? "";
                var reason = page.MatchedKeyword ?? "title";
                if (resolvedMethod.StartsWith("body_", StringComparison.OrdinalIgnoreCase))
                    reason = string.IsNullOrWhiteSpace(page.BodyMatchedKeyword) ? resolvedMethod : page.BodyMatchedKeyword;

                evidence.Add(new DetectionEvidence
                {
                    Method = resolvedMethod,
                    DocType = mapped,
                    Page = page.Page,
                    MatchedText = resolvedText,
                    PathRef = page.PathRef ?? "",
                    Reason = reason,
                    OpRange = page.OpRange ?? "",
                    Obj = page.BodyObj
                });
            }

            if (prefixHit.Found && prefixHit.Page == page.Page)
            {
                evidence.Add(new DetectionEvidence
                {
                    Method = "contentsprefix",
                    DocType = "DESPACHO",
                    Page = page.Page,
                    MatchedText = prefixHit.Title ?? "",
                    PathRef = prefixHit.PathRef ?? "",
                    Reason = prefixHit.Reason ?? "contentsprefix",
                    Obj = prefixHit.Obj
                });
            }

            if (headerHit.Found && headerHit.Page == page.Page)
            {
                evidence.Add(new DetectionEvidence
                {
                    Method = "headerlabel",
                    DocType = "DESPACHO",
                    Page = page.Page,
                    MatchedText = headerHit.Title ?? "",
                    PathRef = headerHit.PathRef ?? "",
                    Reason = headerHit.Reason ?? "headerlabel",
                    Obj = headerHit.Obj
                });
            }

            if (largestHit.Found && largestHit.Page == page.Page)
            {
                evidence.Add(new DetectionEvidence
                {
                    Method = "largestcontents",
                    DocType = "UNKNOWN",
                    Page = page.Page,
                    MatchedText = "",
                    PathRef = largestHit.PathRef ?? "",
                    Reason = largestHit.Reason ?? "largestcontents",
                    Obj = largestHit.Obj
                });
            }

            if (page.BodyObj <= 0)
            {
                evidence.Add(new DetectionEvidence
                {
                    Method = "contents_missing",
                    DocType = "UNKNOWN",
                    Page = page.Page,
                    Reason = "no_contents",
                    Obj = 0
                });
            }

            return evidence;
        }

        private static string ResolveDocType(List<DetectionEvidence> evidence, out bool conflict, out bool strong)
        {
            conflict = false;
            strong = false;
            var types = evidence
                .Select(e => e.DocType ?? "")
                .Where(t => !string.IsNullOrWhiteSpace(t) && !string.Equals(t, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (types.Count == 0)
                return "UNKNOWN";
            if (types.Count > 1)
            {
                var strongEvidence = PickStrongEvidence(evidence);
                if (strongEvidence != null)
                {
                    strong = true;
                    return strongEvidence.DocType ?? "UNKNOWN";
                }

                conflict = true;
                return "UNKNOWN";
            }
            return types[0];
        }

        private static DetectionEvidence? PickStrongEvidence(List<DetectionEvidence> evidence)
        {
            if (evidence == null || evidence.Count == 0)
                return null;

            var strong = evidence
                .Where(e => IsStrongEvidence(e))
                .OrderBy(e => StrongEvidencePriority(e))
                .FirstOrDefault();

            return strong;
        }

        private static string GetStrongDocType(List<DetectionEvidence> evidence)
        {
            var strong = PickStrongEvidence(evidence);
            if (strong == null)
                return "";
            return strong.DocType ?? "";
        }

        private static bool IsStrongEvidence(DetectionEvidence evidence)
        {
            if (evidence == null)
                return false;

            var method = (evidence.Method ?? "").Trim().ToLowerInvariant();
            if (method == "bookmark" || method == "bookmark_segment")
                return true;
            if (method == "contents_title" || method == "title_top_bottom")
                return true;
            return false;
        }

        private static int StrongEvidencePriority(DetectionEvidence evidence)
        {
            var method = (evidence.Method ?? "").Trim().ToLowerInvariant();
            if (method == "bookmark" || method == "bookmark_segment")
                return 0;
            if (method == "contents_title" || method == "title_top_bottom")
                return 1;
            return 5;
        }

        private static string ClassifyTitleToDoc(string title)
        {
            var norm = NormalizeTitleBody(title);
            var docKey = DocumentValidationRules.ResolveDocKeyFromHint(norm);
            if (string.IsNullOrWhiteSpace(docKey))
                docKey = DocumentValidationRules.ClassifyDocByPageEvidence(norm, norm, norm, norm, norm, out _);
            return DocumentValidationRules.MapDocKeyToOutputType(docKey);
        }

        private static string MapDocKeyToOutput(string docKey)
        {
            return DocumentValidationRules.MapDocKeyToOutputType(docKey);
        }

        private static string MapOutputToDocHint(string docType)
        {
            return DocumentValidationRules.MapOutputTypeToDocKey(docType);
        }

        private static FinalizeDocument ExtractSegment(string pdfPath, DocSegment segment, FinalizeOptions options, List<PipelineError> pipelineErrors)
        {
            var docOutput = new FinalizeDocument
            {
                DocType = segment.DocType,
                PageStart = segment.PageStart,
                PageEnd = segment.PageEnd,
                DetectionEvidence = segment.Evidence
            };

            foreach (var field in FinalFieldNames)
                docOutput.Fields[field] = BuildEmptyField(field, segment, "", segment.DocType);

            if (string.Equals(segment.DocType, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
            {
                docOutput.Errors.Add(new PipelineError
                {
                    Code = "DOC_TYPE_UNKNOWN",
                    Message = $"Segmento {segment.PageStart}-{segment.PageEnd} sem tipo identificado."
                });
                return docOutput;
            }

            if (segment.BodyObj <= 0)
            {
                docOutput.Errors.Add(new PipelineError
                {
                    Code = "CONTENTS_MISSING",
                    Message = $"/Contents ausente no segmento {segment.PageStart}-{segment.PageEnd}."
                });
                return docOutput;
            }

            var docHint = MapOutputToDocHint(segment.DocType);
            if (string.IsNullOrWhiteSpace(docHint))
            {
                docOutput.Errors.Add(new PipelineError
                {
                    Code = "DOC_HINT_MISSING",
                    Message = $"DocType sem hint: {segment.DocType}"
                });
                return docOutput;
            }

            var modelPath = ResolveModelPathForDoc(docHint);
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                docOutput.Errors.Add(new PipelineError
                {
                    Code = "MODEL_NOT_FOUND",
                    Message = $"Modelo nao encontrado para {docHint}."
                });
                return docOutput;
            }

            var opFilter = BuildDefaultOpFilter();
            var pageB = ResolveModelPage(modelPath, docHint);
            var backPageOverride = segment.PageEnd > segment.PageStart ? segment.PageEnd : 0;

            var frontBack = FrontBackResolver.Resolve(new FrontBackRequest
            {
                PdfA = pdfPath,
                PdfB = modelPath,
                PageA = segment.PageStart,
                PageB = pageB,
                FrontObjOverride = segment.BodyObj,
                BackPageAOverride = backPageOverride,
                OpFilter = opFilter,
                Backoff = DefaultBackoff,
                FrontRequireMarker = DocumentValidationRules.IsDocMatch(docHint, DocumentValidationRules.DocKeyDespacho)
            });

            if (frontBack.Errors.Count > 0 || frontBack.AlignRange == null)
            {
                var fatal = frontBack.Errors
                    .Where(e => !e.StartsWith("back_page", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (fatal.Count > 0 || frontBack.AlignRange == null)
                {
                    docOutput.Errors.Add(new PipelineError
                    {
                        Code = "ALIGNRANGE_FAILED",
                        Message = string.Join(";", fatal.Count > 0 ? fatal : frontBack.Errors)
                    });
                    return docOutput;
                }
            }

            var alignSummary = new AlignRangeSummary
            {
                FrontA = new RangeValue
                {
                    Page = frontBack.AlignRange.FrontA.Page,
                    StartOp = frontBack.AlignRange.FrontA.StartOp,
                    EndOp = frontBack.AlignRange.FrontA.EndOp,
                    ValueFull = frontBack.AlignRange.FrontA.ValueFull
                },
                FrontB = new RangeValue
                {
                    Page = frontBack.AlignRange.FrontB.Page,
                    StartOp = frontBack.AlignRange.FrontB.StartOp,
                    EndOp = frontBack.AlignRange.FrontB.EndOp,
                    ValueFull = frontBack.AlignRange.FrontB.ValueFull
                },
                BackA = new RangeValue
                {
                    Page = frontBack.AlignRange.BackA.Page,
                    StartOp = frontBack.AlignRange.BackA.StartOp,
                    EndOp = frontBack.AlignRange.BackA.EndOp,
                    ValueFull = frontBack.AlignRange.BackA.ValueFull
                },
                BackB = new RangeValue
                {
                    Page = frontBack.AlignRange.BackB.Page,
                    StartOp = frontBack.AlignRange.BackB.StartOp,
                    EndOp = frontBack.AlignRange.BackB.EndOp,
                    ValueFull = frontBack.AlignRange.BackB.ValueFull
                }
            };

            var bandDefaults = BuildBandDefaults(alignSummary, frontBack);
            var candidates = InitCandidates();
            var baseTried = new List<string> { "alignrange" };

            var mapFields = RunMapFieldsAlignRangeFinalize(pdfPath, modelPath, alignSummary, docHint, frontBack, options.ArtifactsDir);
            if (!string.IsNullOrWhiteSpace(mapFields.JsonPath) && File.Exists(mapFields.JsonPath))
            {
                var mapData = TryLoadJson(mapFields.JsonPath);
                AddMapFieldCandidates(candidates, mapData, segment.DocType, alignSummary, bandDefaults);
            }
            else
            {
                docOutput.Errors.Add(new PipelineError
                {
                    Code = "MAPFIELDS_MISSING",
                    Message = "Mapfields alignrange nao gerado."
                });
            }

            var templatePath = ResolveTemplateMapPathForDoc(docHint);
            if (!string.IsNullOrWhiteSpace(templatePath) && File.Exists(templatePath))
            {
                AddTemplateCandidates(candidates, pdfPath, alignSummary, frontBack, templatePath, bandDefaults, segment.DocType, options.ArtifactsDir, docOutput.Errors);
                baseTried.Add("template");
            }

            if (string.Equals(segment.DocType, "DESPACHO", StringComparison.OrdinalIgnoreCase))
            {
                AddTextOpsCandidates(candidates, pdfPath, frontBack, options.ArtifactsDir, docOutput.Errors, segment.DocType);
                baseTried.Add("textops");
                AddSignatureDateCandidate(candidates, pdfPath, segment, docOutput.Errors);
                baseTried.Add("signature_footer");
            }

            AddStrategyCandidates(candidates, segment.DocType, bandDefaults, segment.PageStart, docOutput.Errors);
            baseTried.Add("regex");

            foreach (var field in FinalFieldNames)
            {
                if (!IsFieldAllowedForDoc(field, segment.DocType))
                {
                    docOutput.Fields[field] = BuildEmptyField(field, segment, "", segment.DocType);
                    continue;
                }
                var fieldCandidates = candidates.TryGetValue(field, out var list) ? list : new List<FieldCandidate>();
                var defaultBand = PickDefaultBand(field, bandDefaults);
                var tried = BuildTriedList(fieldCandidates, baseTried);
                var resolved = ResolveFieldValue(field, fieldCandidates, defaultBand, options.Strict, docOutput.Errors, tried, segment.DocType);
                docOutput.Fields[field] = resolved;
            }

            return docOutput;
        }

        private static Dictionary<string, List<FieldCandidate>> InitCandidates()
        {
            var map = new Dictionary<string, List<FieldCandidate>>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in FinalFieldNames)
                map[field] = new List<FieldCandidate>();
            return map;
        }

        private static List<string> BuildTriedList(List<FieldCandidate> candidates, List<string> baseMethods)
        {
            var merged = new HashSet<string>(baseMethods ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var method in candidates.Select(c => c.Method))
            {
                if (!string.IsNullOrWhiteSpace(method))
                    merged.Add(method);
            }

            return merged
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static HashSet<string> BuildDefaultOpFilter()
        {
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

            return opFilter;
        }

        private static int ResolveModelPage(string modelPath, string docHint)
        {
            var best = SelectBestDocForType(modelPath, docHint);
            if (best != null && best.StartPage > 0)
                return best.StartPage;

            var hit = DetectDoc(modelPath, out _);
            if (hit.Found)
                return hit.Page;

            return 1;
        }

        private static MapFieldsSummary RunMapFieldsAlignRangeFinalize(
            string aPath,
            string bPath,
            AlignRangeSummary align,
            string docHint,
            FrontBackResult frontBack,
            string artifactsDir)
        {
            var summary = new MapFieldsSummary { Mode = "alignrange" };
            if (align == null)
                return summary;

            var baseA = Path.GetFileNameWithoutExtension(aPath);
            var baseB = Path.GetFileNameWithoutExtension(bPath);
            var outDir = string.IsNullOrWhiteSpace(artifactsDir)
                ? Path.Combine(FinalizeDefaultOutDir, baseA, "mapfields")
                : Path.Combine(artifactsDir, "mapfields");
            Directory.CreateDirectory(outDir);

            var mapPath = ResolveMapPathForDoc(docHint);
            if (string.IsNullOrWhiteSpace(mapPath) || !File.Exists(mapPath))
            {
                summary.Errors.Add("map_not_found");
                summary.MapPath = mapPath;
                return summary;
            }

            var frontObjA = frontBack.FrontA?.Obj ?? 0;
            var frontObjB = frontBack.FrontB?.Obj ?? 0;
            var backObjA = frontBack.BackBodyA?.Obj ?? 0;
            var backObjB = frontBack.BackBodyB?.Obj ?? 0;

            var alignPath = Path.Combine(outDir, $"{baseA}__{baseB}__{docHint}.yml");
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

        private static Dictionary<string, BandInfo> BuildBandDefaults(AlignRangeSummary align, FrontBackResult frontBack)
        {
            var bands = new Dictionary<string, BandInfo>(StringComparer.OrdinalIgnoreCase);
            bands["front_head"] = new BandInfo
            {
                Band = "front_head",
                ValueFull = align.FrontA.ValueFull ?? "",
                OpRange = FormatOpRange(align.FrontA.StartOp, align.FrontA.EndOp),
                Obj = frontBack.FrontA?.Obj ?? 0,
                Page = align.FrontA.Page
            };
            bands["back_tail"] = new BandInfo
            {
                Band = "back_tail",
                ValueFull = align.BackA.ValueFull ?? "",
                OpRange = FormatOpRange(align.BackA.StartOp, align.BackA.EndOp),
                Obj = frontBack.BackBodyA?.Obj ?? 0,
                Page = align.BackA.Page
            };

            return bands;
        }

        private static void AddMapFieldCandidates(
            Dictionary<string, List<FieldCandidate>> candidates,
            JsonElement? mapData,
            string docType,
            AlignRangeSummary align,
            Dictionary<string, BandInfo> bands)
        {
            if (mapData == null)
                return;

            JsonElement root = mapData.Value;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("pdf_a", out var side))
                root = side;

            if (root.ValueKind != JsonValueKind.Object)
                return;

            foreach (var prop in root.EnumerateObject())
            {
                var field = prop.Name;
                if (!candidates.ContainsKey(field))
                    continue;

                var obj = prop.Value;
                var value = ReadString(obj, "Value") ?? "";
                var valueRaw = ReadString(obj, "ValueRaw") ?? value;
                var valueFull = ReadString(obj, "ValueFull") ?? "";
                var source = ReadString(obj, "Source") ?? "";
                var opRange = ReadString(obj, "OpRange") ?? "";
                var objId = ReadInt(obj, "Obj");
                var bbox = ReadBBox(obj, "BBox");

                if (!string.IsNullOrWhiteSpace(value))
                {
                    var page = ResolvePageForSource(source, align, bands);
                    candidates[field].Add(new FieldCandidate
                    {
                        Field = field,
                        Value = value,
                        ValueRaw = valueRaw,
                        ValueFull = valueFull,
                        Source = source,
                        OpRange = opRange,
                        Obj = objId,
                        BBox = bbox,
                        Page = page,
                        Method = "alignrange",
                        DocType = docType,
                        Confidence = 0.9
                    });
                }
            }
        }

        private static void AddTemplateCandidates(
            Dictionary<string, List<FieldCandidate>> candidates,
            string pdfPath,
            AlignRangeSummary align,
            FrontBackResult frontBack,
            string templatePath,
            Dictionary<string, BandInfo> bands,
            string docType,
            string artifactsDir,
            List<PipelineError> errors)
        {
            var map = ObjectsTextOpsDiff.LoadTemplateFieldMap(templatePath, out var mapErr);
            if (map == null)
            {
                errors.Add(new PipelineError
                {
                    Code = "TEMPLATE_MAP_ERROR",
                    Message = string.IsNullOrWhiteSpace(mapErr) ? "template_map_error" : mapErr
                });
                return;
            }

            var outDir = string.IsNullOrWhiteSpace(artifactsDir)
                ? Path.Combine(FinalizeDefaultOutDir, Path.GetFileNameWithoutExtension(pdfPath), "template")
                : Path.Combine(artifactsDir, "template");
            Directory.CreateDirectory(outDir);

            var frontObj = frontBack.FrontA?.Obj ?? 0;
            var backObj = frontBack.BackBodyA?.Obj ?? 0;

            if (frontObj > 0)
            {
                var front = ObjectsTextOpsDiff.ExtractTemplateFields(
                    pdfPath,
                    frontObj,
                    align.FrontA.StartOp,
                    align.FrontA.EndOp,
                    map,
                    "front_head",
                    out var frontErr);
                if (!string.IsNullOrWhiteSpace(frontErr))
                {
                    errors.Add(new PipelineError { Code = "TEMPLATE_FRONT_ERROR", Message = frontErr });
                }
                WriteTemplateJson(outDir, "front_head", front);
                AddTemplateCandidatesFromMap(candidates, front, "front_head", docType, align, bands);
            }

            if (backObj > 0)
            {
                var back = ObjectsTextOpsDiff.ExtractTemplateFields(
                    pdfPath,
                    backObj,
                    align.BackA.StartOp,
                    align.BackA.EndOp,
                    map,
                    "back_tail",
                    out var backErr);
                if (!string.IsNullOrWhiteSpace(backErr))
                {
                    errors.Add(new PipelineError { Code = "TEMPLATE_BACK_ERROR", Message = backErr });
                }
                WriteTemplateJson(outDir, "back_tail", back);
                AddTemplateCandidatesFromMap(candidates, back, "back_tail", docType, align, bands);
            }
        }

        private static void WriteTemplateJson(string outDir, string band, Dictionary<string, ObjectsTextOpsDiff.TemplateFieldResult> data)
        {
            try
            {
                var path = Path.Combine(outDir, $"template_{band}.json");
                var json = JsonSerializer.Serialize(data, JsonUtils.Indented);
                File.WriteAllText(path, json);
            }
            catch
            {
                // ignore debug artifacts failures
            }
        }

        private static void AddTemplateCandidatesFromMap(
            Dictionary<string, List<FieldCandidate>> candidates,
            Dictionary<string, ObjectsTextOpsDiff.TemplateFieldResult> data,
            string band,
            string docType,
            AlignRangeSummary align,
            Dictionary<string, BandInfo> bands)
        {
            if (data == null || data.Count == 0)
                return;

            foreach (var kv in data)
            {
                var field = kv.Key;
                if (!candidates.ContainsKey(field))
                    continue;

                var result = kv.Value;
                if (!string.Equals(result.Status, "OK", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = result.Value ?? "";
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                candidates[field].Add(new FieldCandidate
                {
                    Field = field,
                    Value = value,
                    ValueRaw = string.IsNullOrWhiteSpace(result.ValueRaw) ? value : result.ValueRaw,
                    ValueFull = result.ValueFull ?? "",
                    Source = band,
                    OpRange = result.OpRange ?? "",
                    Obj = result.Obj,
                    BBox = ToFinalBBox(result.BBox),
                    Page = ResolvePageForSource(band, align, bands),
                    Method = "template",
                    DocType = docType,
                    Confidence = 0.9
                });
            }
        }

        private static FinalBBox? ToFinalBBox(ObjectsTextOpsDiff.TemplateFieldBoundingBox? bbox)
        {
            if (bbox == null) return null;
            return new FinalBBox
            {
                X0 = bbox.X0,
                Y0 = bbox.Y0,
                X1 = bbox.X1,
                Y1 = bbox.Y1,
                StartOp = bbox.StartOp,
                EndOp = bbox.EndOp,
                Items = bbox.Items
            };
        }

        private static void AddTextOpsCandidates(
            Dictionary<string, List<FieldCandidate>> candidates,
            string pdfPath,
            FrontBackResult frontBack,
            string artifactsDir,
            List<PipelineError> errors,
            string docType)
        {
            var frontMapBase = ResolveMapPath("tjpb_despacho_obj6_fields.draft.yml");
            var backMapBase = ResolveMapPath("tjpb_despacho_obj_p2_fields.draft.yml");

            if (string.IsNullOrWhiteSpace(frontMapBase) || !File.Exists(frontMapBase))
                errors.Add(new PipelineError { Code = "TEXTOPS_FRONT_MAP_MISSING", Message = "Mapa textops front ausente." });
            if (string.IsNullOrWhiteSpace(backMapBase) || !File.Exists(backMapBase))
                errors.Add(new PipelineError { Code = "TEXTOPS_BACK_MAP_MISSING", Message = "Mapa textops back ausente." });

            var frontObj = frontBack.FrontA?.Obj ?? 0;
            var backObj = frontBack.BackBodyA?.Obj ?? 0;

            var outDir = string.IsNullOrWhiteSpace(artifactsDir)
                ? Path.Combine(FinalizeDefaultOutDir, Path.GetFileNameWithoutExtension(pdfPath), "textops")
                : Path.Combine(artifactsDir, "textops");
            Directory.CreateDirectory(outDir);

            if (frontObj > 0 && File.Exists(frontMapBase))
            {
                var adjusted = BuildAdjustedMap(frontMapBase, frontObj, outDir, "front_map.yml");
                if (string.IsNullOrWhiteSpace(adjusted))
                {
                    errors.Add(new PipelineError { Code = "TEXTOPS_MAP_ADJUST_FAILED", Message = "Falha ao ajustar mapa front." });
                }
                else
                {
                    var outJson = Path.Combine(outDir, "front_fields.json");
                    var localErrors = new List<string>();
                    RunTextOpsExtract(pdfPath, adjusted, outJson, localErrors);
                    foreach (var err in localErrors)
                        errors.Add(new PipelineError { Code = "TEXTOPS_ERROR", Message = err });
                    AddTextOpsCandidatesFromFile(candidates, outJson, docType, "textops_front");
                }
            }

            if (backObj > 0 && File.Exists(backMapBase))
            {
                var adjusted = BuildAdjustedMap(backMapBase, backObj, outDir, "back_map.yml");
                if (string.IsNullOrWhiteSpace(adjusted))
                {
                    errors.Add(new PipelineError { Code = "TEXTOPS_MAP_ADJUST_FAILED", Message = "Falha ao ajustar mapa back." });
                }
                else
                {
                    var outJson = Path.Combine(outDir, "back_fields.json");
                    var localErrors = new List<string>();
                    RunTextOpsExtract(pdfPath, adjusted, outJson, localErrors);
                    foreach (var err in localErrors)
                        errors.Add(new PipelineError { Code = "TEXTOPS_ERROR", Message = err });
                    AddTextOpsCandidatesFromFile(candidates, outJson, docType, "textops_back");
                }
            }
        }

        private static void AddTextOpsCandidatesFromFile(
            Dictionary<string, List<FieldCandidate>> candidates,
            string jsonPath,
            string docType,
            string method)
        {
            if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
                return;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = doc.RootElement;
                if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
                    return;

                foreach (var entry in fields.EnumerateArray())
                {
                    var field = ReadString(entry, "Field") ?? "";
                    if (string.IsNullOrWhiteSpace(field) || !candidates.ContainsKey(field))
                        continue;

                    var value = ReadString(entry, "Value") ?? "";
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    var valueRaw = ReadString(entry, "ValueRaw") ?? value;
                    var valueFull = ReadString(entry, "ValueFull") ?? "";
                    var opRange = ReadString(entry, "OpRange") ?? "";
                    var objId = ReadInt(entry, "Obj");
                    var bbox = ReadBBox(entry, "BBox");

                    candidates[field].Add(new FieldCandidate
                    {
                        Field = field,
                        Value = value,
                        ValueRaw = valueRaw,
                        ValueFull = valueFull,
                        Source = "textops",
                        OpRange = opRange,
                        Obj = objId,
                        BBox = bbox,
                        Method = method,
                        DocType = docType,
                        Confidence = 0.75
                    });
                }
            }
            catch
            {
                return;
            }
        }

        private static void AddSignatureDateCandidate(
            Dictionary<string, List<FieldCandidate>> candidates,
            string pdfPath,
            DocSegment segment,
            List<PipelineError> errors)
        {
            if (segment == null || !string.Equals(segment.DocType, "DESPACHO", StringComparison.OrdinalIgnoreCase))
                return;

            var signaturePage = segment.PageStart + 1;
            var signature = FindDespachoSignature(pdfPath, signaturePage);
            if (!signature.HasSignature || !signature.HasDate || string.IsNullOrWhiteSpace(signature.DateText))
            {
                errors.Add(new PipelineError
                {
                    Code = "SIGNATURE_DATE_NOT_FOUND",
                    Field = "DATA_ARBITRADO_FINAL",
                    Message = $"Assinatura/data nao encontrada na pagina {signaturePage}."
                });
                return;
            }

            if (!candidates.ContainsKey("DATA_ARBITRADO_FINAL"))
                return;

            candidates["DATA_ARBITRADO_FINAL"].Add(new FieldCandidate
            {
                Field = "DATA_ARBITRADO_FINAL",
                Value = signature.DateText,
                ValueRaw = signature.DateText,
                ValueFull = signature.DateText,
                Source = "signature_footer",
                OpRange = "",
                Obj = signature.Obj,
                BBox = null,
                Page = signature.Page,
                Method = "signature_footer",
                DocType = segment.DocType,
                Confidence = 0.85
            });
        }

        private static void AddStrategyCandidates(
            Dictionary<string, List<FieldCandidate>> candidates,
            string docType,
            Dictionary<string, BandInfo> bands,
            int pageStart,
            List<PipelineError> errors)
        {
            var frontText = bands.TryGetValue("front_head", out var front) ? front.ValueFull ?? "" : "";
            var backText = bands.TryGetValue("back_tail", out var back) ? back.ValueFull ?? "" : "";
            var fullText = string.Join(" ", new[] { frontText, backText }.Where(t => !string.IsNullOrWhiteSpace(t)));

            if (string.IsNullOrWhiteSpace(fullText))
            {
                errors.Add(new PipelineError { Code = "STRATEGY_TEXT_EMPTY", Message = "Texto vazio para regras." });
                return;
            }

            TjpbDespachoConfig? cfg = null;
            try
            {
                cfg = TjpbDespachoConfig.Load(Path.GetFullPath("configs/config.yaml"));
            }
            catch (Exception ex)
            {
                errors.Add(new PipelineError { Code = "STRATEGY_CONFIG_ERROR", Message = ex.Message });
                return;
            }

            var engine = new FieldStrategyEngine(cfg);
            var ctx = new DespachoContext
            {
                FullText = fullText,
                StartPage1 = pageStart,
                EndPage1 = pageStart,
                Regions = new List<RegionSegment>
                {
                    new RegionSegment { Page1 = pageStart, Name = "front_head", Text = frontText },
                    new RegionSegment { Page1 = pageStart, Name = "back_tail", Text = backText }
                }
            };

            var extracted = engine.Extract(ctx);
            foreach (var kv in extracted)
            {
                var field = kv.Key;
                if (!candidates.ContainsKey(field))
                    continue;

                var info = kv.Value;
                if (info == null || string.IsNullOrWhiteSpace(info.Value))
                    continue;

                candidates[field].Add(new FieldCandidate
                {
                    Field = field,
                    Value = info.Value,
                    ValueRaw = info.Value,
                    ValueFull = fullText,
                    Source = "strategy",
                    OpRange = "",
                    Obj = 0,
                    Method = info.Method ?? "strategy",
                    DocType = docType,
                    Confidence = info.Confidence
                });
            }
        }

        private static void AddHonorariosCandidates(
            Dictionary<string, List<FieldCandidate>> candidates,
            string mapFieldsPath,
            string docType,
            List<PipelineError> errors)
        {
            if (!IsFieldAllowedForDoc("ESPECIALIDADE", docType))
                return;
            if (string.IsNullOrWhiteSpace(mapFieldsPath) || !File.Exists(mapFieldsPath))
                return;

            try
            {
                var summary = HonorariosEnricher.Run(mapFieldsPath, docType, null);
                var side = summary?.PdfA;
                if (side == null)
                    return;

                if (!string.Equals(side.Status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new PipelineError
                    {
                        Code = "HONORARIOS_NO_MATCH",
                        Field = "ESPECIE_DA_PERICIA",
                        Message = $"Honorarios status: {side.Status}"
                    });
                    return;
                }

                AddHonorariosField(candidates, "ESPECIALIDADE", side.Especialidade, docType);
                AddHonorariosField(candidates, "ESPECIE_DA_PERICIA", side.EspecieDaPericia, docType);
            }
            catch (Exception ex)
            {
                errors.Add(new PipelineError
                {
                    Code = "HONORARIOS_ERROR",
                    Message = ex.Message
                });
            }
        }

        private static void AddHonorariosField(
            Dictionary<string, List<FieldCandidate>> candidates,
            string field,
            string value,
            string docType)
        {
            if (string.IsNullOrWhiteSpace(value) || !candidates.ContainsKey(field))
                return;
            if (!IsFieldAllowedForDoc(field, docType))
                return;

            candidates[field].Add(new FieldCandidate
            {
                Field = field,
                Value = value,
                ValueRaw = value,
                ValueFull = value,
                Source = "honorarios",
                OpRange = "",
                Obj = 0,
                Method = "honorarios",
                DocType = docType,
                Confidence = 0.8
            });
        }

        private static FinalFieldValue ResolveFieldValue(
            string field,
            List<FieldCandidate> candidates,
            BandInfo? fallbackBand,
            bool strict,
            List<PipelineError> errors,
            List<string> tried,
            string docType)
        {
            var resolved = BuildEmptyField(field, null, fallbackBand?.Band ?? "", docType);

            var nonEmpty = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.Value))
                .ToList();

            var valid = nonEmpty
                .Where(c => IsCandidateAccepted(field, c))
                .ToList();

            if (valid.Count == 0)
            {
                var code = nonEmpty.Count > 0 ? "INVALID_FORMAT" : "NOT_FOUND";
                var message = nonEmpty.Count > 0 ? "Campo encontrado com formato invalido" : "Campo nao encontrado";
                errors.Add(new PipelineError
                {
                    Code = code,
                    Field = field,
                    Message = message,
                    Tried = tried,
                    Candidates = nonEmpty.Count > 0 ? nonEmpty : new List<FieldCandidate>()
                });

                if (fallbackBand != null)
                    ApplyFallbackBand(resolved, fallbackBand);

                return resolved;
            }

            var distinct = valid
                .GroupBy(c => NormalizeCandidateValue(field, c.Value))
                .ToList();

            if (strict && distinct.Count > 1)
            {
                errors.Add(new PipelineError
                {
                    Code = "AMBIGUOUS_MATCH",
                    Field = field,
                    Message = "Valores conflitantes",
                    Candidates = valid
                });

                if (fallbackBand != null)
                    ApplyFallbackBand(resolved, fallbackBand);

                resolved.Value = null;
                resolved.ValueRaw = null;
                return resolved;
            }

            var chosen = valid
                .OrderByDescending(c => CandidatePriority(c.Method))
                .ThenByDescending(c => c.Confidence)
                .First();

            resolved.ValueFull = string.IsNullOrWhiteSpace(chosen.ValueFull) ? (fallbackBand?.ValueFull ?? "") : chosen.ValueFull;
            resolved.ValueRaw = string.IsNullOrWhiteSpace(chosen.ValueRaw) ? chosen.Value : chosen.ValueRaw;
            resolved.Value = chosen.Value;
            resolved.Source = chosen.Source;
            resolved.OpRange = chosen.OpRange;
            resolved.Obj = chosen.Obj;
            resolved.BBox = chosen.BBox;
            resolved.Page = chosen.Page;
            resolved.DocType = chosen.DocType;
            resolved.Confidence = chosen.Confidence;
            resolved.Method = chosen.Method;

            return resolved;
        }

        private static double CandidatePriority(string method)
        {
            if (string.IsNullOrWhiteSpace(method)) return 0.0;
            if (method.StartsWith("alignrange", StringComparison.OrdinalIgnoreCase)) return 3.0;
            if (method.StartsWith("textops", StringComparison.OrdinalIgnoreCase)) return 2.0;
            return 1.0;
        }

        private static string NormalizeCandidateValue(string field, string value)
        {
            var raw = TextUtils.NormalizeWhitespace(value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return "";

            if (field.Equals("PROCESSO_ADMINISTRATIVO", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("PROCESSO_JUDICIAL", StringComparison.OrdinalIgnoreCase))
            {
                return Regex.Replace(raw, "[^0-9]", "");
            }

            if (field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase))
            {
                return Regex.Replace(raw, "[^0-9]", "");
            }

            if (field.StartsWith("VALOR_", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("ADIANTAMENTO", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("PARCELA", StringComparison.OrdinalIgnoreCase))
            {
                var money = TextUtils.NormalizeMoney(raw);
                return string.IsNullOrWhiteSpace(money) ? raw.ToLowerInvariant() : money;
            }

            if (field.Equals("PERCENTUAL", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("FATOR", StringComparison.OrdinalIgnoreCase))
            {
                var compact = Regex.Replace(raw, "\\s+", "");
                compact = compact.Replace(".", ",");
                return compact.EndsWith("%", StringComparison.Ordinal) ? compact : compact + "%";
            }

            if (field.StartsWith("DATA_", StringComparison.OrdinalIgnoreCase))
            {
                if (TextUtils.TryParseDate(raw, out var iso))
                    return iso;
                return TextUtils.RemoveDiacritics(raw).ToLowerInvariant();
            }

            return TextUtils.RemoveDiacritics(raw).ToLowerInvariant();
        }


        private static bool IsCpfPeritoContextAccepted(FieldCandidate candidate, string cpfDigits)
        {
            // CPF aparece muitas vezes (autor/reu etc.). Para evitar falso positivo,
            // validamos o contexto local do CPF escolhido.
            if (candidate == null)
                return false;

            if (string.IsNullOrWhiteSpace(cpfDigits))
                cpfDigits = TextUtils.NormalizeCpf(candidate.Value ?? "");

            if (string.IsNullOrWhiteSpace(cpfDigits))
                return true;

            var ctx = candidate.ValueFull ?? "";
            if (string.IsNullOrWhiteSpace(ctx))
                ctx = candidate.ValueRaw ?? candidate.Value ?? "";

            if (string.IsNullOrWhiteSpace(ctx))
                return true;

            var rx = new Regex(@"\b\d{3}\s*\.?\s*\d{3}\s*\.?\s*\d{3}\s*-?\s*\d{2}\b", RegexOptions.CultureInvariant);
            foreach (Match m in rx.Matches(ctx))
            {
                if (m == null || !m.Success)
                    continue;

                var digits = TextUtils.NormalizeCpf(m.Value);
                if (string.IsNullOrWhiteSpace(digits) || !string.Equals(digits, cpfDigits, StringComparison.Ordinal))
                    continue;

                var start = Math.Max(0, m.Index - 120);
                var end = Math.Min(ctx.Length, m.Index + m.Length + 120);
                var win = ctx.Substring(start, end - start);

                var norm = TextUtils.RemoveDiacritics(win).ToLowerInvariant();
                norm = Regex.Replace(norm, @"\s+", " ").Trim();

                var hasPerito = Regex.IsMatch(norm, @"\b(perit|interessad|parte\s*:|assistente\s+social)\b");
                var hasParty = norm.Contains("cpf/cnpj", StringComparison.OrdinalIgnoreCase) ||
                               Regex.IsMatch(norm, @"\b(autor|r[eé]u|reu|movid[oa]\s+por|em\s+face|promovent|promovid)\b");

                if (hasParty && !hasPerito)
                    return false;
            }

            return true;
        }

        private static bool IsCandidateAccepted(string field, FieldCandidate candidate)
        {
            if (candidate == null)
                return false;

            var value = TextUtils.NormalizeWhitespace(candidate.Value ?? "");
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var maxLen = GetMaxCandidateLengthForField(field);
            if (maxLen > 0 && value.Length > maxLen)
                return false;

            if (!ValidatorRules.IsValidFieldFormat(field, value))
                return false;

            if (!ValidatorRules.IsValueValidForField(field, value, null, null, null, out _))
                return false;

            if (field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase))
            {
                var cpfDigits = TextUtils.NormalizeCpf(value);
                if (!IsCpfPeritoContextAccepted(candidate, cpfDigits))
                    return false;
            }

            if ((field.Equals("PERITO", StringComparison.OrdinalIgnoreCase) ||
                 field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                 field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase) ||
                 field.Equals("ESPECIALIDADE", StringComparison.OrdinalIgnoreCase) ||
                 field.Equals("ESPECIE_DA_PERICIA", StringComparison.OrdinalIgnoreCase) ||
                 field.Equals("COMARCA", StringComparison.OrdinalIgnoreCase) ||
                 field.Equals("VARA", StringComparison.OrdinalIgnoreCase)) &&
                ValidatorRules.ContainsDocumentBoilerplate(value))
                return false;

            if (field.Equals("PERITO", StringComparison.OrdinalIgnoreCase))
            {
                if (!ValidatorRules.LooksLikePersonNameLoose(value))
                    return false;

                var peritoNorm = TextUtils.RemoveDiacritics(value);
                if (ValidatorRules.ContainsProcessualNoise(peritoNorm) || ValidatorRules.ContainsDocumentBoilerplate(peritoNorm))
                    return false;
                if (Regex.IsMatch(peritoNorm, @"(?i)\b(para|caso|autos|processo|epigrafe|judicial|movido|promovente|promovido|requerente|interessad[oa]|vara|comarca)\b"))
                    return false;

                var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var upperInitials = tokens.Count(t => t.Length > 1 && char.IsLetter(t[0]) && char.IsUpper(t[0]));
                if (upperInitials < 2)
                    return false;
            }

            if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
            {
                if (ValidatorRules.ContainsProcessualNoise(value) || ValidatorRules.ContainsDocumentBoilerplate(value))
                    return false;
                if (Regex.IsMatch(TextUtils.RemoveDiacritics(value), @"(?i)\b(caso|processo|autos|juizo|vara|comarca|face|epigrafe)\b"))
                    return false;

                var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2)
                    return false;

                var alphaTokens = tokens.Count(t => Regex.IsMatch(t, @"[A-Za-zÀ-ÿ]{2,}"));
                if (alphaTokens < 2)
                    return false;

                var upperInitials = tokens.Count(t => t.Length > 1 && char.IsLetter(t[0]) && char.IsUpper(t[0]));
                var allUpperTokens = tokens.Count(t => Regex.IsMatch(t, @"^[A-ZÀ-Ÿ]{2,}$"));
                if (upperInitials < 1 && allUpperTokens < 2)
                    return false;
            }

            return true;
        }

        private static int GetMaxCandidateLengthForField(string field)
        {
            if (string.IsNullOrWhiteSpace(field))
                return 0;

            if (field.Equals("PROCESSO_ADMINISTRATIVO", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("PROCESSO_JUDICIAL", StringComparison.OrdinalIgnoreCase))
                return 40;
            if (field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase))
                return 20;
            if (field.StartsWith("VALOR_", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("ADIANTAMENTO", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("PERCENTUAL", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("PARCELA", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("FATOR", StringComparison.OrdinalIgnoreCase))
                return 30;
            if (field.StartsWith("DATA_", StringComparison.OrdinalIgnoreCase))
                return 40;
            if (field.Equals("COMARCA", StringComparison.OrdinalIgnoreCase))
                return 80;
            if (field.Equals("VARA", StringComparison.OrdinalIgnoreCase))
                return 120;
            if (field.Equals("PERITO", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("ESPECIALIDADE", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("ESPECIE_DA_PERICIA", StringComparison.OrdinalIgnoreCase))
                return 120;
            if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                return 180;

            return 200;
        }

        private static BandInfo? PickDefaultBand(string field, Dictionary<string, BandInfo> bands)
        {
            if (bands.TryGetValue("front_head", out var front))
                return front;
            if (bands.TryGetValue("back_tail", out var back))
                return back;
            return null;
        }

        private static FinalFieldValue BuildEmptyField(string field, DocSegment? segment, string sourceBand, string docType)
        {
            return new FinalFieldValue
            {
                ValueFull = "",
                ValueRaw = null,
                Value = null,
                Source = sourceBand,
                OpRange = "",
                Obj = segment?.BodyObj ?? 0,
                BBox = null,
                Page = segment?.PageStart,
                DocType = !string.IsNullOrWhiteSpace(docType) ? docType : (segment?.DocType ?? "")
            };
        }

        private static void ApplyFallbackBand(FinalFieldValue target, BandInfo band)
        {
            if (target == null || band == null)
                return;
            if (string.IsNullOrWhiteSpace(target.ValueFull))
                target.ValueFull = band.ValueFull ?? "";
            if (string.IsNullOrWhiteSpace(target.OpRange))
                target.OpRange = band.OpRange ?? "";
            if (target.Obj == 0)
                target.Obj = band.Obj;
            if (!target.Page.HasValue || target.Page.Value == 0)
                target.Page = band.Page;
            if (string.IsNullOrWhiteSpace(target.Source))
                target.Source = band.Band;
        }

        private static Dictionary<string, FinalFieldValue> AggregateFinalFields(
            List<FinalizeDocument> docs,
            bool strict,
            List<PipelineError> errors)
        {
            var output = new Dictionary<string, FinalFieldValue>(StringComparer.OrdinalIgnoreCase);

            var byDoc = BuildPrimaryDocIndex(docs, errors);

            var docOrderCommon = new[] { "DESPACHO", "REQUERIMENTO_HONORARIOS", "CERTIDAO_CM" };
            var docOrderParties = new[] { "REQUERIMENTO_HONORARIOS", "DESPACHO" };
            var docOrderPerito = new[] { "DESPACHO", "REQUERIMENTO_HONORARIOS", "CERTIDAO_CM" };
            var docOrderPeritoDetails = new[] { "DESPACHO", "REQUERIMENTO_HONORARIOS" };

            // Campos comuns do processo (redundantes nos tipos).
            foreach (var field in new[] { "PROCESSO_ADMINISTRATIVO", "PROCESSO_JUDICIAL", "COMARCA", "VARA" })
                output[field] = PickFieldByDocOrder(byDoc, field, docOrderCommon, strict, errors);

            // Partes aparecem mais confiavelmente no requerimento; despacho pode servir de fallback.
            foreach (var field in new[] { "PROMOVENTE", "PROMOVIDO" })
                output[field] = PickFieldByDocOrder(byDoc, field, docOrderParties, strict, errors);

            // Perito/CPF podem aparecer em despacho, requerimento e (as vezes) certidao; nao restringir a um unico tipo.
            foreach (var field in new[] { "PERITO", "CPF_PERITO" })
                output[field] = PickFieldByDocOrder(byDoc, field, docOrderPerito, strict, errors);

            foreach (var field in new[] { "ESPECIALIDADE", "ESPECIE_DA_PERICIA" })
                output[field] = PickFieldByDocOrder(byDoc, field, docOrderPeritoDetails, strict, errors);

            output["VALOR_ARBITRADO_JZ"] = PickFieldByDocOrder(byDoc, "VALOR_ARBITRADO_JZ", new[] { "DESPACHO", "REQUERIMENTO_HONORARIOS" }, strict, errors);
            output["VALOR_ARBITRADO_DE"] = PickFieldByDocOrder(byDoc, "VALOR_ARBITRADO_DE", new[] { "DESPACHO" }, strict, errors);
            output["VALOR_ARBITRADO_CM"] = PickFieldByDocOrder(byDoc, "VALOR_ARBITRADO_CM", new[] { "CERTIDAO_CM" }, strict, errors);

            output["DATA_REQUISICAO"] = PickFieldByDocOrder(byDoc, "DATA_REQUISICAO", new[] { "REQUERIMENTO_HONORARIOS" }, strict, errors);

            output["ADIANTAMENTO"] = PickFieldByDocOrder(byDoc, "ADIANTAMENTO", new[] { "CERTIDAO_CM" }, strict, errors);
            output["PERCENTUAL"] = PickFieldByDocOrder(byDoc, "PERCENTUAL", new[] { "CERTIDAO_CM" }, strict, errors);
            output["PARCELA"] = PickFieldByDocOrder(byDoc, "PARCELA", new[] { "CERTIDAO_CM" }, strict, errors);
            output["FATOR"] = PickFieldByDocOrder(byDoc, "FATOR", new[] { "CERTIDAO_CM", "DESPACHO" }, strict, errors);

            ApplyFinalArbitradoRules(byDoc, output, strict, errors);

            foreach (var field in FinalFieldNames)
            {
                if (!output.ContainsKey(field))
                    output[field] = new FinalFieldValue { ValueFull = "", ValueRaw = null, Value = null };
            }

            return output;
        }

        private static void ValidateFinalNameFields(
            Dictionary<string, FinalFieldValue> finalFields,
            bool strict,
            List<PipelineError> errors)
        {
            if (finalFields == null || finalFields.Count == 0)
                return;

            var perito = GetFinalValue(finalFields, "PERITO");
            var cpf = GetFinalValue(finalFields, "CPF_PERITO");
            var promovente = GetFinalValue(finalFields, "PROMOVENTE");
            var promovido = GetFinalValue(finalFields, "PROMOVIDO");

            var peritoKey = NormalizeNameKey(perito?.Value ?? perito?.ValueRaw ?? perito?.ValueFull ?? "");
            var promoventeKey = NormalizeNameKey(promovente?.Value ?? promovente?.ValueRaw ?? promovente?.ValueFull ?? "");
            var promovidoKey = NormalizeNameKey(promovido?.Value ?? promovido?.ValueRaw ?? promovido?.ValueFull ?? "");

            if (!string.IsNullOrWhiteSpace(promoventeKey)
                && !string.IsNullOrWhiteSpace(promovidoKey)
                && string.Equals(promoventeKey, promovidoKey, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new PipelineError
                {
                    Code = "NAME_COLLISION",
                    Field = "PROMOVENTE",
                    Message = "PROMOVENTE e PROMOVIDO com o mesmo nome.",
                    Candidates = BuildCandidates("PROMOVENTE", promovente, "PROMOVIDO", promovido)
                });

                if (strict)
                {
                    InvalidateField(promovente);
                    InvalidateField(promovido);
                }
            }

            if (!string.IsNullOrWhiteSpace(peritoKey))
            {
                if (!string.IsNullOrWhiteSpace(promoventeKey)
                    && string.Equals(peritoKey, promoventeKey, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new PipelineError
                    {
                        Code = "NAME_COLLISION",
                        Field = "PROMOVENTE",
                        Message = "PROMOVENTE igual ao PERITO.",
                        Candidates = BuildCandidates("PROMOVENTE", promovente, "PERITO", perito)
                    });

                    if (strict)
                        InvalidateField(promovente);
                }

                if (!string.IsNullOrWhiteSpace(promovidoKey)
                    && string.Equals(peritoKey, promovidoKey, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new PipelineError
                    {
                        Code = "NAME_COLLISION",
                        Field = "PROMOVIDO",
                        Message = "PROMOVIDO igual ao PERITO.",
                        Candidates = BuildCandidates("PROMOVIDO", promovido, "PERITO", perito)
                    });

                    if (strict)
                        InvalidateField(promovido);
                }
            }

            var catalog = TryLoadPeritoCatalog(errors);
            if (catalog == null)
                return;

            ValidateNonPeritoName("PROMOVENTE", promovente, catalog, strict, errors);
            ValidateNonPeritoName("PROMOVIDO", promovido, catalog, strict, errors);
            ValidatePeritoName(perito, cpf, catalog, errors);
            ValidatePeritoEspecialidade(finalFields, perito, cpf, catalog, strict, errors);
        }

        private static FinalFieldValue? GetFinalValue(Dictionary<string, FinalFieldValue> fields, string key)
        {
            if (fields.TryGetValue(key, out var value))
                return value;
            return null;
        }

        private static void InvalidateField(FinalFieldValue? field)
        {
            if (field == null)
                return;
            field.Value = null;
            field.ValueRaw = null;
        }

        private static List<FieldCandidate> BuildCandidates(
            string fieldA,
            FinalFieldValue? valueA,
            string fieldB,
            FinalFieldValue? valueB)
        {
            var list = new List<FieldCandidate>();
            var candA = BuildCandidateFromFinal(fieldA, valueA);
            if (candA != null)
                list.Add(candA);
            var candB = BuildCandidateFromFinal(fieldB, valueB);
            if (candB != null)
                list.Add(candB);
            return list;
        }

        private static FieldCandidate? BuildCandidateFromFinal(string field, FinalFieldValue? value)
        {
            if (value == null)
                return null;
            var val = value.Value ?? value.ValueRaw ?? value.ValueFull ?? "";
            if (string.IsNullOrWhiteSpace(val))
                return null;

            return new FieldCandidate
            {
                Field = field,
                Value = val,
                ValueRaw = value.ValueRaw ?? val,
                ValueFull = string.IsNullOrWhiteSpace(value.ValueFull) ? val : value.ValueFull,
                Source = value.Source,
                OpRange = value.OpRange,
                Obj = value.Obj,
                BBox = value.BBox,
                Page = value.Page,
                Method = string.IsNullOrWhiteSpace(value.Method) ? "final" : value.Method,
                DocType = value.DocType,
                Confidence = value.Confidence ?? 0.0
            };
        }

        private static string NormalizeNameKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            var v = TextUtils.RemoveDiacritics(value).ToUpperInvariant();
            v = Regex.Replace(v, "[^A-Z0-9 ]+", " ");
            v = Regex.Replace(v, "\\s+", " ").Trim();
            return v;
        }

        private static PeritoCatalog? TryLoadPeritoCatalog(List<PipelineError> errors)
        {
            var catalog = ValidatorContext.GetPeritoCatalog();
            if (catalog != null)
                return catalog;

            errors.Add(new PipelineError
            {
                Code = "PERITO_CATALOG_ERROR",
                Message = "Falha ao carregar catalogo de peritos."
            });
            return null;
        }

        private static void ValidateNonPeritoName(
            string field,
            FinalFieldValue? value,
            PeritoCatalog catalog,
            bool strict,
            List<PipelineError> errors)
        {
            if (value == null)
                return;

            var name = value.Value ?? value.ValueRaw ?? value.ValueFull ?? "";
            if (string.IsNullOrWhiteSpace(name))
                return;

            if (catalog.TryResolve(name, null, out var info, out var confidence))
            {
                errors.Add(new PipelineError
                {
                    Code = "NAME_IN_PERITO_CATALOG",
                    Field = field,
                    Message = $"{field} coincide com catalogo de peritos ({info.Name}).",
                    Candidates = BuildCandidates(field, value, "PERITO_CATALOG", new FinalFieldValue
                    {
                        Value = info.Name,
                        ValueRaw = info.Name,
                        ValueFull = info.Name,
                        Source = "perito_catalog",
                        Confidence = confidence
                    })
                });

                if (strict)
                    InvalidateField(value);
            }
        }

        private static void ValidatePeritoName(
            FinalFieldValue? perito,
            FinalFieldValue? cpf,
            PeritoCatalog catalog,
            List<PipelineError> errors)
        {
            if (perito == null && cpf == null)
                return;

            var name = perito?.Value ?? perito?.ValueRaw ?? perito?.ValueFull ?? "";
            var cpfValue = cpf?.Value ?? cpf?.ValueRaw ?? cpf?.ValueFull ?? "";

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(cpfValue))
                return;

            if (!catalog.TryResolve(name, cpfValue, out _, out var confidence))
            {
                errors.Add(new PipelineError
                {
                    Code = "PERITO_NOT_IN_CATALOG",
                    Field = "PERITO",
                    Message = "Perito/CPF nao encontrado no catalogo de peritos.",
                    Candidates = BuildCandidates("PERITO", perito, "CPF_PERITO", cpf)
                });
                return;
            }

            if (perito != null && perito.Confidence == null)
                perito.Confidence = confidence;
        }

        private static void ValidatePeritoEspecialidade(
            Dictionary<string, FinalFieldValue> fields,
            FinalFieldValue? perito,
            FinalFieldValue? cpf,
            PeritoCatalog catalog,
            bool strict,
            List<PipelineError> errors)
        {
            if (perito == null && cpf == null)
                return;

            var name = perito?.Value ?? perito?.ValueRaw ?? perito?.ValueFull ?? "";
            var cpfValue = cpf?.Value ?? cpf?.ValueRaw ?? cpf?.ValueFull ?? "";
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(cpfValue))
                return;

            if (!catalog.TryResolve(name, cpfValue, out var info, out _))
                return;

            if (!fields.TryGetValue("ESPECIALIDADE", out var especialidade) || especialidade == null)
                return;

            var espValue = especialidade.Value ?? especialidade.ValueRaw ?? especialidade.ValueFull ?? "";
            if (string.IsNullOrWhiteSpace(espValue) || string.IsNullOrWhiteSpace(info.Especialidade))
                return;

            var espKey = NormalizeNameKey(espValue);
            var infoKey = NormalizeNameKey(info.Especialidade);
            if (string.IsNullOrWhiteSpace(espKey) || string.IsNullOrWhiteSpace(infoKey))
                return;

            if (!string.Equals(espKey, infoKey, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new PipelineError
                {
                    Code = "PERITO_ESPECIALIDADE_MISMATCH",
                    Field = "ESPECIALIDADE",
                    Message = $"Especialidade do despacho ({espValue}) difere do catalogo ({info.Especialidade}).",
                    Candidates = BuildCandidates("ESPECIALIDADE", especialidade, "PERITO_CATALOG", new FinalFieldValue
                    {
                        Value = info.Especialidade,
                        ValueRaw = info.Especialidade,
                        ValueFull = info.Especialidade,
                        Source = "perito_catalog"
                    })
                });

                if (strict)
                    InvalidateField(especialidade);
            }
        }

        private static Dictionary<string, FinalFieldValue> ComputeHonorariosDerivedFields(
            List<FinalizeDocument> docs,
            bool strict,
            List<PipelineError> errors)
        {
            var derived = new Dictionary<string, FinalFieldValue>(StringComparer.OrdinalIgnoreCase);
            if (docs == null || docs.Count == 0)
                return derived;

            var byDoc = BuildPrimaryDocIndex(docs, errors);

            if (!byDoc.ContainsKey("DESPACHO"))
                return derived;

            var perito = PickFieldByDocOrder(byDoc, "PERITO", new[] { "DESPACHO" }, strict, errors);
            var cpf = PickFieldByDocOrder(byDoc, "CPF_PERITO", new[] { "DESPACHO" }, strict, errors);
            var especialidade = PickFieldByDocOrder(byDoc, "ESPECIALIDADE", new[] { "DESPACHO" }, strict, errors);
            var valorJz = PickFieldByDocOrder(byDoc, "VALOR_ARBITRADO_JZ", new[] { "DESPACHO", "REQUERIMENTO_HONORARIOS" }, strict, errors);

            var hasPerito = !string.IsNullOrWhiteSpace(perito.Value);
            var hasCpf = !string.IsNullOrWhiteSpace(cpf.Value);
            var hasEspecialidade = !string.IsNullOrWhiteSpace(especialidade.Value);
            var hasValorJz = !string.IsNullOrWhiteSpace(valorJz.Value);

            if (!hasValorJz)
            {
                errors.Add(new PipelineError
                {
                    Code = "HONORARIOS_MISSING_JZ",
                    Field = "VALOR_ARBITRADO_JZ",
                    Message = "Valor arbitrado JZ ausente para honorarios."
                });
                return derived;
            }

            if (!hasPerito && !hasCpf && !hasEspecialidade)
            {
                errors.Add(new PipelineError
                {
                    Code = "HONORARIOS_MISSING_PERITO",
                    Field = "PERITO",
                    Message = "Perito/especialidade ausente no despacho para honorarios."
                });
                return derived;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            AddValueIfPresent(values, "PERITO", perito.Value);
            AddValueIfPresent(values, "CPF_PERITO", cpf.Value);
            AddValueIfPresent(values, "ESPECIALIDADE", especialidade.Value);
            AddValueIfPresent(values, "VALOR_ARBITRADO_JZ", valorJz.Value);

            HonorariosSummary? summary;
            try
            {
                summary = HonorariosEnricher.RunFromValues(values, "HONORARIOS_DERIVED", null);
            }
            catch (Exception ex)
            {
                errors.Add(new PipelineError
                {
                    Code = "HONORARIOS_ERROR",
                    Message = ex.Message
                });
                return derived;
            }

            if (summary.Errors.Count > 0)
            {
                errors.Add(new PipelineError
                {
                    Code = "HONORARIOS_CONFIG_ERROR",
                    Message = string.Join(";", summary.Errors)
                });
            }

            var side = summary.PdfA;
            if (side == null)
            {
                errors.Add(new PipelineError
                {
                    Code = "HONORARIOS_ERROR",
                    Message = "Honorarios sem retorno."
                });
                return derived;
            }

            if (!string.Equals(side.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new PipelineError
                {
                    Code = "HONORARIOS_NO_MATCH",
                    Field = "ESPECIE_DA_PERICIA",
                    Message = $"Honorarios status: {side.Status}"
                });
                return derived;
            }

            var baseEspecialidade = PickHonorariosEvidence(especialidade, perito, cpf);
            var baseValor = PickHonorariosEvidence(valorJz, baseEspecialidade ?? especialidade, perito, cpf);
            var peritoAnchor = BuildAnchor("PERITO", perito.DocType);
            var cpfAnchor = BuildAnchor("CPF_PERITO", cpf.DocType);
            var especialidadeAnchor = BuildAnchor("ESPECIALIDADE", especialidade.DocType);
            var valorJzAnchor = BuildAnchor("VALOR_ARBITRADO_JZ", valorJz.DocType);

            if (!string.IsNullOrWhiteSpace(side.ValorNormalized))
            {
                derived["VALOR_ARBITRADO_JZ"] = BuildDerivedFinalField(
                    "VALOR_ARBITRADO_JZ",
                    side.ValorNormalized,
                    baseValor ?? baseEspecialidade,
                    side.Confidence,
                    new List<string> { valorJzAnchor },
                    side.ValorTabeladoAnexoI);
            }

            if (!string.IsNullOrWhiteSpace(side.Especialidade))
            {
                derived["ESPECIALIDADE"] = BuildDerivedFinalField(
                    "ESPECIALIDADE",
                    side.Especialidade,
                    baseEspecialidade,
                    side.Confidence,
                    new List<string> { peritoAnchor, cpfAnchor },
                    null);
            }

            if (!string.IsNullOrWhiteSpace(side.EspecieDaPericia))
            {
                derived["ESPECIE_DA_PERICIA"] = BuildDerivedFinalField(
                    "ESPECIE_DA_PERICIA",
                    side.EspecieDaPericia,
                    baseValor ?? baseEspecialidade,
                    side.Confidence,
                    new List<string> { valorJzAnchor, especialidadeAnchor },
                    null);
            }

            if (!string.IsNullOrWhiteSpace(side.Fator))
            {
                derived["FATOR"] = BuildDerivedFinalField(
                    "FATOR",
                    side.Fator,
                    baseValor ?? baseEspecialidade,
                    side.Confidence,
                    new List<string> { valorJzAnchor },
                    side.ValorTabeladoAnexoI);
            }

            return derived;
        }

        private static void AddValueIfPresent(Dictionary<string, string> values, string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                return;
            values[key] = value;
        }

        private static FinalFieldValue? PickHonorariosEvidence(params FinalFieldValue[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (candidate == null)
                    continue;
                if (!string.IsNullOrWhiteSpace(candidate.Value) || !string.IsNullOrWhiteSpace(candidate.ValueFull))
                    return candidate;
            }

            return candidates.FirstOrDefault();
        }

        private static string BuildAnchor(string field, string docType)
        {
            if (string.IsNullOrWhiteSpace(docType))
                return field ?? "";
            return $"{field}@{docType}";
        }

        private static FinalFieldValue BuildDerivedFinalField(
            string field,
            string value,
            FinalFieldValue? baseEvidence,
            double confidence,
            List<string> anchors,
            string? valorTabelado)
        {
            var derived = new FinalFieldValue
            {
                Value = value,
                ValueRaw = value,
                ValueFull = value,
                Source = "honorarios",
                OpRange = baseEvidence?.OpRange ?? "",
                Obj = baseEvidence?.Obj ?? 0,
                BBox = baseEvidence?.BBox,
                Page = baseEvidence?.Page,
                DocType = "DERIVED",
                Confidence = confidence,
                Method = "honorarios",
                AnchorsMatched = anchors ?? new List<string>()
            };

            if (!string.IsNullOrWhiteSpace(valorTabelado))
                derived.ValorTabelado = valorTabelado;

            return derived;
        }

                private static void ApplyDerivedFinalFields(
            Dictionary<string, FinalFieldValue> finalFields,
            Dictionary<string, FinalFieldValue> derived)
        {
            if (finalFields == null || derived == null || derived.Count == 0)
                return;

            static bool LooksLikeMultiplier(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                var v = value.Trim();
                if (v.Contains("R$", StringComparison.OrdinalIgnoreCase)) return false;
                if (!TextUtils.TryParseMoney(v, out var parsed)) return false;
                return parsed > 0m && parsed <= 20m;
            }

            static bool LooksLikeEspecialidadeNoise(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return true;
                var v = value.Trim();
                // Common leak: email user glued after especialidade (ex.: "Assistente Social mauridetegb").
                var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    var last = parts[^1];
                    if (last.Length >= 6 && last.All(ch => char.IsLetterOrDigit(ch)) && last.ToLowerInvariant() == last)
                        return true;
                }
                return v.Contains("@", StringComparison.OrdinalIgnoreCase);
            }

            foreach (var kv in derived)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null)
                    continue;

                var key = kv.Key;
                var newVal = kv.Value;

                if (!finalFields.TryGetValue(key, out var existing) || existing == null || string.IsNullOrWhiteSpace(existing.Value))
                {
                    finalFields[key] = newVal;
                    continue;
                }

                // Upgrade multiplier -> money for VALOR_ARBITRADO_JZ when honorarios derived a proper amount.
                if (key.Equals("VALOR_ARBITRADO_JZ", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(newVal.Value)
                    && newVal.Value.Contains("R$", StringComparison.OrdinalIgnoreCase)
                    && LooksLikeMultiplier(existing.Value))
                {
                    finalFields[key] = newVal;
                    continue;
                }

                // Prefer honorarios-normalized especialidade over noisy extraction.
                if (key.Equals("ESPECIALIDADE", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(newVal.Value)
                    && LooksLikeEspecialidadeNoise(existing.Value))
                {
                    finalFields[key] = newVal;
                    continue;
                }
            }
        }


        private static bool IsFieldAllowedForDoc(string field, string docType)
        {
            if (string.IsNullOrWhiteSpace(docType) || string.IsNullOrWhiteSpace(field))
                return false;

            if (FieldAllowedDocs.TryGetValue(field, out var allowed))
            {
                if (allowed == null || allowed.Count == 0)
                    return false;
                return allowed.Contains(docType);
            }

            return true;
        }

        private static Dictionary<string, List<FinalizeDocument>> BuildPrimaryDocIndex(
            List<FinalizeDocument> docs,
            List<PipelineError> errors)
        {
            var output = new Dictionary<string, List<FinalizeDocument>>(StringComparer.OrdinalIgnoreCase);
            if (docs == null || docs.Count == 0)
                return output;

            var grouped = docs
                .Where(d => d != null
                            && !string.IsNullOrWhiteSpace(d.DocType)
                            && !string.Equals(d.DocType, "UNKNOWN", StringComparison.OrdinalIgnoreCase))
                .GroupBy(d => d.DocType, StringComparer.OrdinalIgnoreCase);

            var catalog = TryLoadPeritoCatalogSilent();

            foreach (var group in grouped)
            {
                var rankedRaw = group
                    .Select(doc =>
                    {
                        var validatorPass = PassesPrimaryDocValidator(doc, catalog, out var validatorReason);
                        var primaryHits = CountFilledFields(doc, GetPrimaryFields(doc.DocType));
                        var allowedHits = CountAllowedFilledFields(doc);
                        var score = ScorePrimaryDocument(doc, validatorPass);
                        doc.PrimaryValidatorPass = validatorPass;
                        doc.PrimaryValidatorReason = validatorReason;
                        doc.PrimaryHits = primaryHits;
                        doc.PrimaryAllowedHits = allowedHits;
                        doc.PrimaryScore = score;
                        return new
                        {
                            Doc = doc,
                            Score = score,
                            PrimaryHits = primaryHits,
                            AllowedHits = allowedHits,
                            ValidatorPass = validatorPass,
                            ValidatorReason = validatorReason
                        };
                    })
                    .ToList();

                var hasValidatorPass = rankedRaw.Any(x => x.ValidatorPass);
                var ranked = hasValidatorPass
                    ? rankedRaw
                        .OrderByDescending(x => x.ValidatorPass)
                        .ThenByDescending(x => x.PrimaryHits)
                        .ThenByDescending(x => x.AllowedHits)
                        .ThenByDescending(x => x.Score)
                        .ThenBy(x => x.Doc.PageStart)
                        .ToList()
                    : rankedRaw
                        .OrderByDescending(x => x.Score)
                        .ThenByDescending(x => x.PrimaryHits)
                        .ThenByDescending(x => x.AllowedHits)
                        .ThenBy(x => x.Doc.PageStart)
                        .ToList();

                var winner = ranked.First();
                output[group.Key] = ranked.Select(x => x.Doc).ToList();

                if (ranked.Count > 1)
                {
                    var selected = winner.Doc;
                    selected.DetectionEvidence.Add(new DetectionEvidence
                    {
                        Method = "doc_primary_selected",
                        DocType = selected.DocType,
                        Page = selected.PageStart,
                        Reason = $"score={winner.Score};validator={(winner.ValidatorPass ? "ok" : "fail")};validator_reason={winner.ValidatorReason};primary={winner.PrimaryHits};allowed={winner.AllowedHits}",
                        Obj = selected.Fields.Values.FirstOrDefault()?.Obj ?? 0
                    });

                    foreach (var loser in ranked.Skip(1))
                    {
                        loser.Doc.DetectionEvidence.Add(new DetectionEvidence
                        {
                            Method = "doc_primary_skipped",
                            DocType = loser.Doc.DocType,
                            Page = loser.Doc.PageStart,
                            Reason = $"winner={selected.PageStart}-{selected.PageEnd}|winner_score={winner.Score};self_score={loser.Score};self_validator={(loser.ValidatorPass ? "ok" : "fail")};self_validator_reason={loser.ValidatorReason};self_primary={loser.PrimaryHits};self_allowed={loser.AllowedHits}",
                            Obj = loser.Doc.Fields.Values.FirstOrDefault()?.Obj ?? 0
                        });
                    }

                    errors.Add(new PipelineError
                    {
                        Code = "DOC_PRIMARY_SELECTED",
                        Field = group.Key,
                        Message = $"{group.Key}: selecionado segmento {selected.PageStart}-{selected.PageEnd} entre {ranked.Count} candidatos."
                    });
                }
            }

            return output;
        }

        private static int ScorePrimaryDocument(FinalizeDocument doc, bool validatorPass)
        {
            if (doc == null || string.IsNullOrWhiteSpace(doc.DocType))
                return int.MinValue;

            var primaryHits = CountFilledFields(doc, GetPrimaryFields(doc.DocType));
            var allowedHits = CountAllowedFilledFields(doc);
            var errorPenalty = Math.Max(0, doc.Errors?.Count ?? 0) * 5;
            var pagePenalty = Math.Max(0, doc.PageStart);
            var validatorBonus = validatorPass ? 10000 : -1000;

            // Cobertura de campos domina; validador e pagina inicial desempata.
            return (primaryHits * 1000) + (allowedHits * 100) + validatorBonus - errorPenalty - pagePenalty;
        }

        private static PeritoCatalog? TryLoadPeritoCatalogSilent()
        {
            return ValidatorContext.GetPeritoCatalog();
        }
        private static bool PassesPrimaryDocValidator(FinalizeDocument doc, PeritoCatalog? catalog, out string reason)
        {
            reason = "";
            if (doc == null || string.IsNullOrWhiteSpace(doc.DocType))
            {
                reason = "doc_empty";
                return false;
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in doc.Fields)
            {
                if (!HasFieldValue(kv.Value))
                    continue;
                var raw = kv.Value.Value ?? kv.Value.ValueRaw ?? "";
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                values[kv.Key] = TextUtils.NormalizeWhitespace(raw);
            }

            return ValidatorRules.PassesDocumentValidator(values, doc.DocType, catalog, out reason);
        }


        private static IReadOnlyList<string> GetPrimaryFields(string docType)
        {
            if (PrimaryFieldsByDocType.TryGetValue(docType ?? "", out var fields) && fields != null && fields.Length > 0)
                return fields;

            return new[]
            {
                "PROCESSO_ADMINISTRATIVO",
                "PROCESSO_JUDICIAL",
                "COMARCA",
                "VARA"
            };
        }

        private static int CountAllowedFilledFields(FinalizeDocument doc)
        {
            if (doc?.Fields == null || string.IsNullOrWhiteSpace(doc.DocType))
                return 0;

            var count = 0;
            foreach (var kv in doc.Fields)
            {
                if (!IsFieldAllowedForDoc(kv.Key, doc.DocType))
                    continue;
                if (!HasFieldValue(kv.Value))
                    continue;
                if (TryBuildCandidate(kv.Key, kv.Value, doc.DocType, out var candidate) && IsCandidateAccepted(kv.Key, candidate))
                    count++;
            }

            return count;
        }

        private static int CountFilledFields(FinalizeDocument doc, IEnumerable<string> fields)
        {
            if (doc?.Fields == null || fields == null)
                return 0;

            var count = 0;
            foreach (var field in fields)
            {
                if (string.IsNullOrWhiteSpace(field))
                    continue;
                if (!doc.Fields.TryGetValue(field, out var value))
                    continue;
                if (!HasFieldValue(value))
                    continue;
                if (TryBuildCandidate(field, value, doc.DocType, out var candidate) && IsCandidateAccepted(field, candidate))
                    count++;
            }

            return count;
        }

        private static bool HasFieldValue(FinalFieldValue? value)
        {
            var raw = value?.Value ?? value?.ValueRaw ?? "";
            return !string.IsNullOrWhiteSpace(raw);
        }

        private static bool TryBuildCandidate(string field, FinalFieldValue? value, string docType, out FieldCandidate candidate)
        {
            candidate = new FieldCandidate();
            if (value == null)
                return false;

            var raw = TextUtils.NormalizeWhitespace(value.Value ?? value.ValueRaw ?? "");
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            candidate = new FieldCandidate
            {
                Field = field ?? "",
                Value = raw,
                ValueRaw = string.IsNullOrWhiteSpace(value.ValueRaw) ? raw : value.ValueRaw ?? raw,
                ValueFull = value.ValueFull ?? "",
                Source = value.Source ?? "",
                OpRange = value.OpRange ?? "",
                Obj = value.Obj,
                BBox = value.BBox,
                Page = value.Page,
                Method = value.Method ?? "",
                DocType = !string.IsNullOrWhiteSpace(value.DocType) ? value.DocType! : (docType ?? ""),
                Confidence = value.Confidence ?? 0.0
            };

            return true;
        }

        private static FinalFieldValue PickFieldByDocOrder(
            Dictionary<string, List<FinalizeDocument>> byDoc,
            string field,
            string[] order,
            bool strict,
            List<PipelineError> errors)
        {
            foreach (var docType in order)
            {
                if (!byDoc.TryGetValue(docType, out var docs) || docs == null || docs.Count == 0)
                    continue;

                var preferred = docs.Where(d => d != null && d.PrimaryValidatorPass).ToList();
                var fallback = docs.Where(d => d != null && !d.PrimaryValidatorPass).ToList();

                if (preferred.Count > 0 &&
                    TryResolveFieldFromDocumentsSequential(field, docType, preferred, strict, errors, out var preferredValue))
                {
                    return preferredValue;
                }

                if (TryResolveFieldFromDocumentsSequential(field, docType, fallback, strict, errors, out var fallbackValue))
                {
                    if (preferred.Count > 0)
                    {
                        errors.Add(new PipelineError
                        {
                            Code = "DOC_FIELD_FALLBACK_NONVALIDATED",
                            Field = field,
                            Message = $"{field}: fallback para segmento nao validado em {docType}."
                        });
                    }

                    return fallbackValue;
                }
            }

            return new FinalFieldValue { ValueFull = "", ValueRaw = null, Value = null };
        }

        private static bool TryResolveFieldFromDocumentsSequential(
            string field,
            string docType,
            List<FinalizeDocument> docs,
            bool strict,
            List<PipelineError> errors,
            out FinalFieldValue value)
        {
            value = new FinalFieldValue { ValueFull = "", ValueRaw = null, Value = null };
            if (docs == null || docs.Count == 0)
                return false;

            var accepted = new List<FieldCandidate>();
            var values = new List<FinalFieldValue>();

            foreach (var doc in docs)
            {
                if (doc?.Fields == null)
                    continue;

                if (!doc.Fields.TryGetValue(field, out var val))
                    continue;

                if (!TryBuildCandidate(field, val, doc.DocType, out var candidate))
                    continue;

                if (!IsCandidateAccepted(field, candidate))
                    continue;

                accepted.Add(candidate);
                values.Add(new FinalFieldValue
                {
                    ValueFull = string.IsNullOrWhiteSpace(val.ValueFull) ? candidate.Value : val.ValueFull,
                    ValueRaw = string.IsNullOrWhiteSpace(val.ValueRaw) ? candidate.Value : val.ValueRaw,
                    Value = candidate.Value,
                    Source = val.Source,
                    OpRange = val.OpRange,
                    Obj = val.Obj,
                    BBox = val.BBox,
                    Page = val.Page,
                    DocType = string.IsNullOrWhiteSpace(val.DocType) ? doc.DocType : val.DocType,
                    Confidence = val.Confidence,
                    Method = val.Method
                });
            }

            if (values.Count == 0)
                return false;

            if (strict && values.Count > 1)
            {
                var distinct = accepted
                    .Select(c => NormalizeCandidateValue(field, c.Value))
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (distinct.Count > 1)
                {
                    errors.Add(new PipelineError
                    {
                        Code = "AMBIGUOUS_MATCH_RESOLVED",
                        Field = field,
                        Message = $"Valores conflitantes em {docType}; usando melhor candidato por ranking de segmento.",
                        Candidates = accepted
                    });
                }
            }

            value = values.First();
            return true;
        }

        private static void ApplyFinalArbitradoRules(
            Dictionary<string, List<FinalizeDocument>> byDoc,
            Dictionary<string, FinalFieldValue> output,
            bool strict,
            List<PipelineError> errors)
        {
            var certValor = output.GetValueOrDefault("VALOR_ARBITRADO_CM");
            var certData = PickFieldByDocOrder(byDoc, "DATA_ARBITRADO_FINAL", new[] { "CERTIDAO_CM" }, strict, errors);

            if (!string.IsNullOrWhiteSpace(certValor?.Value) || !string.IsNullOrWhiteSpace(certData.Value))
            {
                output["VALOR_ARBITRADO_FINAL"] = BuildDerived("VALOR_ARBITRADO_FINAL", certValor, "CERTIDAO_CM", "VALOR_ARBITRADO_CM");
                output["DATA_ARBITRADO_FINAL"] = BuildDerived("DATA_ARBITRADO_FINAL", certData, "CERTIDAO_CM", "DATA_ARBITRADO_FINAL");
                return;
            }

            var deValor = output.GetValueOrDefault("VALOR_ARBITRADO_DE");
            var deData = PickFieldByDocOrder(byDoc, "DATA_ARBITRADO_FINAL", new[] { "DESPACHO" }, strict, errors);

            if (!string.IsNullOrWhiteSpace(deValor?.Value) || !string.IsNullOrWhiteSpace(deData.Value))
            {
                output["VALOR_ARBITRADO_FINAL"] = BuildDerived("VALOR_ARBITRADO_FINAL", deValor, "DESPACHO", "VALOR_ARBITRADO_DE");
                output["DATA_ARBITRADO_FINAL"] = BuildDerived("DATA_ARBITRADO_FINAL", deData, "DESPACHO", "DATA_ARBITRADO_FINAL");
                return;
            }
            errors.Add(new PipelineError
            {
                Code = "NOT_FOUND",
                Field = "VALOR_ARBITRADO_FINAL",
                Message = "Sem VALOR_ARBITRADO_CM ou VALOR_ARBITRADO_DE para derivar FINAL."
            });
            output["VALOR_ARBITRADO_FINAL"] = new FinalFieldValue { ValueFull = "", ValueRaw = null, Value = null };
            output["DATA_ARBITRADO_FINAL"] = new FinalFieldValue { ValueFull = "", ValueRaw = null, Value = null };
        }

        private static SignatureCheck FindDespachoSignature(string pdfPath, int page)
        {
            var empty = new SignatureCheck { Page = page, Status = "signature_not_checked" };
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                empty.Status = "signature_pdf_not_found";
                return empty;
            }
            if (page <= 0)
            {
                empty.Status = "signature_page_invalid";
                return empty;
            }

            var second = ContentsStreamPicker.PickSecondLargest(new StreamPickRequest
            {
                PdfPath = pdfPath,
                Page = page,
                RequireMarker = false
            });
            var secondText = ExtractStreamTextByObjId(pdfPath, second.Obj);
            var secondCheck = EvaluateSignatureText(secondText, page, second.Obj, "stream_second_largest");
            if (secondCheck.HasSignature && secondCheck.HasDate)
                return secondCheck;

            var body = ContentsStreamPicker.Pick(new StreamPickRequest
            {
                PdfPath = pdfPath,
                Page = page,
                RequireMarker = false
            });
            var bodyText = ExtractStreamTextByObjId(pdfPath, body.Obj);
            var tail = TakeTail(bodyText, 800);
            var tailCheck = EvaluateSignatureText(tail, page, body.Obj, "stream_largest_tail");
            if (tailCheck.HasSignature && tailCheck.HasDate)
                return tailCheck;

            SignatureCheck? footerCheck = null;
            SignatureCheck? footerObjCheck = null;
            if (SignatureFooterProbeEnabled)
            {
                var footerProbe = HeaderFooterProbe.Probe(pdfPath, page, maxPieces: 12, footerMaxOps: 10);
                if (footerProbe != null)
                {
                    footerCheck = EvaluateSignatureText(footerProbe.FooterText ?? "", page, footerProbe.FooterObj, "footer_probe_text");
                    if (footerCheck.HasSignature && footerCheck.HasDate)
                        return footerCheck;

                    if (footerProbe.FooterObj > 0)
                    {
                        var footerObjText = ExtractStreamTextByObjId(pdfPath, footerProbe.FooterObj);
                        footerObjCheck = EvaluateSignatureText(footerObjText, page, footerProbe.FooterObj, "footer_probe_obj");
                        if (footerObjCheck.HasSignature && footerObjCheck.HasDate)
                            return footerObjCheck;
                    }
                }
            }

            return PickBestSignature(secondCheck, tailCheck, footerCheck, footerObjCheck);
        }

        private static SignatureCheck EvaluateSignatureText(string text, int page, int objId, string source)
        {
            var result = new SignatureCheck
            {
                Page = page,
                Obj = objId,
                Source = source,
                PathRef = objId > 0 ? $"page={page}/obj={objId}" : ""
            };
            if (string.IsNullOrWhiteSpace(text))
            {
                result.Status = "signature_text_empty";
                return result;
            }

            result.TextSample = text.Length > 160 ? text.Substring(0, 160) : text;
            var norm = TextUtils.NormalizeForMatch(TextUtils.CollapseSpacedLettersText(text));
            var hasRobson = norm.Contains("robson", StringComparison.Ordinal);
            var hasDiretor = norm.Contains("diretor", StringComparison.Ordinal);
            result.HasSignature = hasRobson && hasDiretor;
            if (!result.HasSignature)
            {
                result.Status = "signature_name_missing";
                return result;
            }

            var match = Regex.Match(text, @"\b(\d{1,2}\s+de\s+[A-Za-zçãáàéêíóôõú]+?\s+de\s+\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                result.Status = "signature_date_missing";
                return result;
            }

            result.DateText = match.Groups[1].Value.Trim();
            if (TextUtils.TryParseDate(result.DateText, out var iso))
                result.DateIso = iso;
            result.HasDate = true;
            result.Status = "ok";
            return result;
        }

        private static SignatureCheck PickBestSignature(params SignatureCheck?[] checks)
        {
            SignatureCheck? best = null;
            var bestScore = -1;
            foreach (var check in checks)
            {
                if (check == null)
                    continue;
                var score = (check.HasSignature ? 2 : 0) + (check.HasDate ? 1 : 0);
                if (score > bestScore)
                {
                    best = check;
                    bestScore = score;
                }
            }

            return best ?? new SignatureCheck { Status = "signature_not_checked" };
        }

        private static string TakeTail(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || maxChars <= 0)
                return text ?? "";
            if (text.Length <= maxChars)
                return text;
            return text.Substring(text.Length - maxChars);
        }


        private static FinalFieldValue BuildDerived(string field, FinalFieldValue? source, string docType, string sourceField)
        {
            if (source == null || string.IsNullOrWhiteSpace(source.Value))
            {
                return new FinalFieldValue { ValueFull = "", ValueRaw = null, Value = null };
            }

            return new FinalFieldValue
            {
                ValueFull = source.ValueFull ?? "",
                ValueRaw = source.ValueRaw,
                Value = source.Value,
                Source = source.Source,
                OpRange = source.OpRange,
                Obj = source.Obj,
                BBox = source.BBox,
                Page = source.Page,
                DocType = docType,
                Confidence = source.Confidence,
                Method = "derived"
            };
        }

        private static int ResolvePageForSource(string source, AlignRangeSummary align, Dictionary<string, BandInfo> bands)
        {
            if (string.IsNullOrWhiteSpace(source))
                return align.FrontA.Page;
            if (bands.TryGetValue(source, out var band))
                return band.Page;
            return align.FrontA.Page;
        }

        private static string? ReadString(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object)
                return null;
            if (!obj.TryGetProperty(name, out var prop))
                return null;
            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
        }

        private static int ReadInt(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object)
                return 0;
            if (!obj.TryGetProperty(name, out var prop))
                return 0;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val))
                return val;
            if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var sval))
                return sval;
            return 0;
        }

        private static FinalBBox? ReadBBox(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object)
                return null;
            if (!obj.TryGetProperty(name, out var prop))
                return null;
            if (prop.ValueKind != JsonValueKind.Object)
                return null;

            return new FinalBBox
            {
                X0 = ReadDouble(prop, "X0"),
                Y0 = ReadDouble(prop, "Y0"),
                X1 = ReadDouble(prop, "X1"),
                Y1 = ReadDouble(prop, "Y1"),
                StartOp = ReadInt(prop, "StartOp"),
                EndOp = ReadInt(prop, "EndOp"),
                Items = ReadInt(prop, "Items")
            };
        }

        private static double ReadDouble(JsonElement obj, string name)
        {
            if (obj.ValueKind != JsonValueKind.Object)
                return 0;
            if (!obj.TryGetProperty(name, out var prop))
                return 0;
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var val))
                return val;
            if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var sval))
                return sval;
            return 0;
        }

        internal sealed class SegmentDescriptor
        {
            public string DocType { get; set; } = "";
            public int PageStart { get; set; }
            public int PageEnd { get; set; }
        }

        internal static List<SegmentDescriptor> SegmentPagesForTest(List<PageClassification> pages)
        {
            return SegmentPagesForTest(pages, null, null);
        }

        internal static List<SegmentDescriptor> SegmentPagesForTest(
            List<PageClassification> pages,
            Dictionary<int, SignatureCheck>? despachoConfirm)
        {
            return SegmentPagesForTest(pages, despachoConfirm, null);
        }

        internal static List<SegmentDescriptor> SegmentPagesForTest(
            List<PageClassification> pages,
            Dictionary<int, SignatureCheck>? despachoConfirm,
            Dictionary<int, DetectionEvidence>? bookmarkMap)
        {
            var errors = new List<PipelineError>();
            var segments = SegmentPages(
                pages ?? new List<PageClassification>(),
                bookmarkMap ?? new Dictionary<int, DetectionEvidence>(),
                DetectionHit.Empty("", "test"),
                DetectionHit.Empty("", "test"),
                DetectionHit.Empty("", "test"),
                despachoConfirm ?? new Dictionary<int, SignatureCheck>(),
                errors);

            return segments.Select(s => new SegmentDescriptor
            {
                DocType = s.DocType,
                PageStart = s.PageStart,
                PageEnd = s.PageEnd
            }).ToList();
        }


        internal static Dictionary<string, FinalFieldValue> AggregateFinalFieldsForTest(
            List<FinalizeDocument> docs,
            bool strict,
            List<PipelineError> errors)
        {
            return AggregateFinalFields(docs ?? new List<FinalizeDocument>(), strict, errors ?? new List<PipelineError>());
        }

        internal static FinalizeOutput RunFinalizeForTest(string pdfPath, bool strict, string artifactsDir)
        {
            var options = new FinalizeOptions
            {
                Strict = strict,
                ArtifactsDir = artifactsDir ?? ""
            };
            return RunFinalizePipeline(pdfPath, options);
        }
    }
}
