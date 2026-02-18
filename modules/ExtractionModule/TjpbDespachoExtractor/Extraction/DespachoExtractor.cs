using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DiffMatchPatch;
using Obj.Models;
using Obj.DocDetector;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Models;
using Obj.TjpbDespachoExtractor.Utils;
using Obj.ValidatorModule;

namespace Obj.TjpbDespachoExtractor.Extraction
{
    public class ExtractionOptions
    {
        public bool Dump { get; set; }
        public string? DumpDir { get; set; }
        public bool Verbose { get; set; }
        public string? BookmarkContains { get; set; }
        public string? ProcessNumber { get; set; }
        public List<string> FooterSigners { get; set; } = new List<string>();
        public string? FooterSignatureRaw { get; set; }
    }

    public class DespachoExtractor
    {
        private readonly TjpbDespachoConfig _cfg;
        private readonly diff_match_patch _dmp;
        private static readonly HashSet<string> CertidaoFieldWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VALOR_ARBITRADO_CM","ADIANTAMENTO","PERCENTUAL","PARCELA","DATA"
        };

        public DespachoExtractor(TjpbDespachoConfig cfg)
        {
            _cfg = cfg;
            _dmp = new diff_match_patch();
            _dmp.Match_Threshold = 0.6f;
            _dmp.Match_Distance = 5000;
        }

        public ExtractionResult Extract(PDFAnalysisResult analysis, string sourcePath, ExtractionOptions options, Action<LogEntry>? logFn = null)
        {
            var startedAt = DateTime.UtcNow;
            var result = new ExtractionResult();
            var fileName = Path.GetFileName(sourcePath);
            var totalPages = analysis.DocumentInfo?.TotalPages ?? analysis.Pages.Count;

            result.Pdf = new PdfInfo
            {
                FileName = fileName,
                FilePath = sourcePath,
                Pages = totalPages,
                Sha256 = ComputeFileSha256(sourcePath)
            };

            result.Run = new RunInfo
            {
                StartedAt = startedAt.ToString("o"),
                ConfigVersion = _cfg.Version,
                ToolVersions = new Dictionary<string, string>
                {
                    { "fpdf", Obj.Version.Current },
                    { "diff_match_patch", "1" },
                    { "diffplex", "1" }
                }
            };

            var bookmarks = FlattenBookmarks(analysis.Bookmarks);
            result.Bookmarks.AddRange(bookmarks);
            Log(result, logFn, "info", "bookmarks_loaded", new Dictionary<string, object> { { "count", bookmarks.Count } });

            var density = ComputeDensities(analysis);
            var bookmarkRanges = BuildBookmarkRanges(analysis.Bookmarks, totalPages);
            var bookmarkCandidates = bookmarkRanges
                .Where(b => IsDespachoBookmark(b.Title))
                .Where(b => string.IsNullOrWhiteSpace(options.BookmarkContains) ||
                            (!string.IsNullOrWhiteSpace(b.Title) && b.Title.Contains(options.BookmarkContains, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var candidates = bookmarkCandidates.Count > 0
                ? bookmarkCandidates.Select(b => (b.StartPage1, b.EndPage1, b.Title, b.Level, true)).ToList()
                : BuildCandidateWindows(bookmarks, density, totalPages).Select(c => (c.startPage1, c.endPage1, "", 0, false)).ToList();
            var scored = new List<(CandidateWindowInfo info, double score)>();

            foreach (var c in candidates)
            {
                var info = ScoreCandidate(analysis, c.Item1, c.Item2, density,
                    bookmarkTitle: c.Item5 ? c.Item3 : null,
                    bookmarkLevel: c.Item5 ? c.Item4 : (int?)null,
                    source: c.Item5 ? "bookmark" : "heuristic");
                result.Candidates.Add(info);
                var matchScore = Math.Max(info.ScoreDmp, info.ScoreDiffPlex);
                scored.Add((info, matchScore));
            }

            if (scored.Count == 0)
            {
                result.Errors.Add("no_candidates_found");
                result.Run.FinishedAt = DateTime.UtcNow.ToString("o");
                return result;
            }

            var best = scored
                .OrderByDescending(s => s.score)
                .ThenByDescending(s => s.info.AnchorsHit.Count)
                .First();

            if (best.score < _cfg.Thresholds.Match.DocScoreMin)
            {
                result.Errors.Add("best_score_below_threshold");
            }

            var range = best.info.Signals.TryGetValue("source", out var src) && string.Equals(src?.ToString(), "bookmark", StringComparison.OrdinalIgnoreCase)
                ? (startPage1: best.info.StartPage1, endPage1: best.info.EndPage1)
                : AdjustRange(analysis, best.info.StartPage1, best.info.EndPage1, density);
            Log(result, logFn, "info", "range_final", new Dictionary<string, object>
            {
                { "startPage1", range.startPage1 },
                { "endPage1", range.endPage1 }
            });

            var pageCount = range.endPage1 - range.startPage1 + 1;
            if (pageCount < _cfg.Thresholds.MinPages)
            {
                result.Errors.Add("range_below_min_pages");
                Log(result, logFn, "warn", "range_below_min_pages", new Dictionary<string, object>
                {
                    { "startPage1", range.startPage1 },
                    { "endPage1", range.endPage1 },
                    { "minPages", _cfg.Thresholds.MinPages }
                });
                result.Run.FinishedAt = DateTime.UtcNow.ToString("o");
                return result;
            }

            var despachoRegions = BuildTemplateRegions(analysis, range.startPage1, range.endPage1);
            var certidaoRegions = new List<RegionSegment>();
            var certidaoPage1 = CertidaoExtraction.FindCertidaoPage(analysis, _cfg);
            if (certidaoPage1 > 0)
            {
                certidaoRegions = CertidaoExtraction.BuildCertidaoRegions(analysis, certidaoPage1, _cfg);
                Log(result, logFn, "info", "certidao_found", new Dictionary<string, object>
                {
                    { "page1", certidaoPage1 },
                    { "regions", certidaoRegions.Count }
                });
            }
            if (options.Verbose || options.Dump)
            {
                foreach (var r in despachoRegions)
                {
                    Log(result, logFn, "info", "region_text", new Dictionary<string, object>
                    {
                        { "docType", "despacho" },
                        { "name", r.Name },
                        { "page1", r.Page1 },
                        { "bboxN", r.BBox ?? new BBoxN() },
                        { "text", Truncate(r.Text, 2000) }
                    });
                }
                foreach (var r in certidaoRegions)
                {
                    Log(result, logFn, "info", "region_text", new Dictionary<string, object>
                    {
                        { "docType", "certidao_cm" },
                        { "name", r.Name },
                        { "page1", r.Page1 },
                        { "bboxN", r.BBox ?? new BBoxN() },
                        { "text", Truncate(r.Text, 2000) }
                    });
                }
            }

            var doc = BuildDocument(analysis, range.startPage1, range.endPage1, best.score, despachoRegions,
                options.ProcessNumber ?? "", options.FooterSigners, options.FooterSignatureRaw, "despacho");
            result.Documents.Add(doc);

            if (certidaoRegions.Count > 0)
            {
                var certDoc = BuildDocument(analysis, certidaoPage1, certidaoPage1, 1.0, certidaoRegions,
                    options.ProcessNumber ?? "", options.FooterSigners, options.FooterSignatureRaw, "certidao_cm",
                    fieldWhitelist: CertidaoFieldWhitelist, includeProcessWarnings: false);
                result.Documents.Add(certDoc);
                Log(result, logFn, "info", "certidao_document_built", new Dictionary<string, object>
                {
                    { "page1", certidaoPage1 },
                    { "docType", "certidao_cm" }
                });
            }

            LogVariationSnippets(result, logFn, despachoRegions);

            result.Run.FinishedAt = DateTime.UtcNow.ToString("o");
            return result;
        }

        private void LogVariationSnippets(ExtractionResult result, Action<LogEntry>? logFn, List<RegionSegment> regions)
        {
            if (regions == null || regions.Count == 0) return;
            var autorHints = _cfg.DespachoType.AutorizacaoHints.Concat(_cfg.DespachoType.GeorcHints).ToList();
            var consHints = _cfg.DespachoType.ConselhoHints;

            foreach (var r in regions)
            {
                if (string.IsNullOrWhiteSpace(r.Text)) continue;
                if (r.Name.Equals("second_bottom", StringComparison.OrdinalIgnoreCase))
                {
                    var hits = CollectHintSnippets(r.Text, autorHints, 220);
                    if (hits.Count > 0)
                    {
                        Log(result, logFn, "info", "autorizacao_variations", new Dictionary<string, object>
                        {
                            { "page1", r.Page1 },
                            { "region", r.Name },
                            { "snippets", hits }
                        });
                    }
                }
                if (r.Name.StartsWith("last_bottom", StringComparison.OrdinalIgnoreCase))
                {
                    var hits = CollectHintSnippets(r.Text, consHints, 220);
                    if (hits.Count > 0)
                    {
                        Log(result, logFn, "info", "conselho_variations", new Dictionary<string, object>
                        {
                            { "page1", r.Page1 },
                            { "region", r.Name },
                            { "snippets", hits }
                        });
                    }
                }
            }
        }

        private List<string> CollectHintSnippets(string text, List<string> hints, int window)
        {
            var snippets = new List<string>();
            if (string.IsNullOrWhiteSpace(text) || hints == null || hints.Count == 0) return snippets;
            var normText = TextUtils.NormalizeForMatch(text);
            foreach (var h in hints)
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                var nh = TextUtils.NormalizeForMatch(h);
                if (string.IsNullOrWhiteSpace(nh)) continue;
                var idx = normText.IndexOf(nh, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var start = Math.Max(0, idx - window / 2);
                var len = Math.Min(normText.Length - start, window);
                var snip = normText.Substring(start, len);
                if (!snippets.Contains(snip))
                    snippets.Add(snip);
            }
            return snippets;
        }

        private DespachoDocumentInfo BuildDocument(PDFAnalysisResult analysis, int startPage1, int endPage1, double matchScore,
            List<RegionSegment> regions, string processNumber, List<string> footerSigners, string? footerSignatureRaw,
            string docType, ISet<string>? fieldWhitelist = null, bool includeProcessWarnings = true)
        {
            var doc = new DespachoDocumentInfo
            {
                DocType = docType,
                StartPage1 = startPage1,
                EndPage1 = endPage1,
                MatchScore = matchScore
            };

            var paragraphs = new List<ParagraphSegment>();
            var bands = new List<BandInfo>();
            var bandSegments = new List<BandSegment>();
            var pages = new List<PageTextInfo>();

            var pagesForExtraction = new HashSet<int> { startPage1, endPage1 };
            var secondPage1 = startPage1 + 1;
            if (secondPage1 <= endPage1)
                pagesForExtraction.Add(secondPage1);
            if (endPage1 - startPage1 >= 2)
                pagesForExtraction.Add(endPage1 - 1);

            ParagraphSegment? firstPara = null;
            ParagraphSegment? lastPara = null;
            var paragraphKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddParagraph(ParagraphSegment? p)
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Text)) return;
                var key = $"{p.Page1}:{TextUtils.NormalizeWhitespace(p.Text)}";
                if (paragraphKeys.Add(key))
                    paragraphs.Add(p);
            }

            for (int p = startPage1; p <= endPage1; p++)
            {
                var page = analysis.Pages[p - 1];
                if (!pagesForExtraction.Contains(p))
                    continue;

                pages.Add(new PageTextInfo { Page1 = p, Text = page.TextInfo.PageText ?? "" });

                var words = TextUtils.DeduplicateWords(page.TextInfo.Words ?? new List<WordInfo>());
                var bandSeg = BandSegmenter.SegmentPage(words, p, _cfg.Thresholds.Bands, _cfg.Thresholds.Paragraph.LineMergeY, _cfg.Thresholds.Paragraph.WordGapX);
                bands.AddRange(bandSeg.Bands.Where(b => !string.Equals(b.Band, "body", StringComparison.OrdinalIgnoreCase)));
                bandSegments.AddRange(bandSeg.BandSegments.Where(b => !string.Equals(b.Band, "body", StringComparison.OrdinalIgnoreCase)));

                // Body paragraphs (full page body band) for better field recall
                var bodySeg = bandSeg.BandSegments.FirstOrDefault(b => string.Equals(b.Band, "body", StringComparison.OrdinalIgnoreCase));
                if (bodySeg != null && bodySeg.Words != null && bodySeg.Words.Count > 0)
                {
                    AddBodyBand(bands, bandSegments, p, bodySeg.Words);
                    var linesBody = LineBuilder.BuildLines(bodySeg.Words, p, _cfg.Thresholds.Paragraph.LineMergeY, _cfg.Thresholds.Paragraph.WordGapX);
                    var parasBody = ParagraphBuilder.BuildParagraphs(linesBody, _cfg.Thresholds.Paragraph.ParagraphGapY);
                    foreach (var para in parasBody)
                        AddParagraph(para);
                }

                // Top region (primeira metade da primeira pagina)
                if (p == startPage1)
                {
                    var topWords = regions
                        .Where(r => r.Page1 == p && r.Name.Equals("first_top", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(r => r.Words ?? new List<WordInfo>())
                        .ToList();
                    if (topWords.Count > 0)
                    {
                        AddBodyBand(bands, bandSegments, p, topWords);
                        var lines = LineBuilder.BuildLines(topWords, p, _cfg.Thresholds.Paragraph.LineMergeY, _cfg.Thresholds.Paragraph.WordGapX);
                        var paras = ParagraphBuilder.BuildParagraphs(lines, _cfg.Thresholds.Paragraph.ParagraphGapY);
                        var hints = new List<string>();
                        hints.AddRange(_cfg.Priorities.ProcessoAdminLabels);
                        hints.AddRange(_cfg.Priorities.PeritoLabels);
                        hints.AddRange(_cfg.Priorities.VaraLabels);
                        hints.AddRange(_cfg.Priorities.ComarcaLabels);
                        var cnjRx = new Regex(_cfg.Regex.ProcessoCnj, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                        firstPara ??= SelectParagraphByHints(paras, hints, cnjRx) ?? paras.FirstOrDefault();
                    }
                }

                // Bottom region (ultima metade da ultima pagina; inclui last_bottom_prev quando existir)
                if (p == endPage1 || (p == endPage1 - 1 && p != secondPage1))
                {
                    var bottomWords = regions
                        .Where(r => r.Page1 == p && r.Name.StartsWith("last_bottom", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(r => r.Words ?? new List<WordInfo>())
                        .ToList();
                    if (bottomWords.Count > 0)
                    {
                        AddBodyBand(bands, bandSegments, p, bottomWords);
                        var lines = LineBuilder.BuildLines(bottomWords, p, _cfg.Thresholds.Paragraph.LineMergeY, _cfg.Thresholds.Paragraph.WordGapX);
                        var paras = ParagraphBuilder.BuildParagraphs(lines, _cfg.Thresholds.Paragraph.ParagraphGapY);
                        var candidate = SelectParagraphByHints(paras, _cfg.Anchors.Footer) ?? paras.LastOrDefault();
                        if (candidate != null)
                            lastPara = candidate;
                    }
                }

                // Bottom region da segunda pagina (para valor do DE)
                if (p == secondPage1)
                {
                    var bottomWords = regions
                        .Where(r => r.Page1 == p && (r.Name.Equals("second_bottom", StringComparison.OrdinalIgnoreCase) ||
                                                    r.Name.StartsWith("last_bottom", StringComparison.OrdinalIgnoreCase)))
                        .SelectMany(r => r.Words ?? new List<WordInfo>())
                        .ToList();
                    if (bottomWords.Count > 0)
                    {
                        AddBodyBand(bands, bandSegments, p, bottomWords);
                        var lines = LineBuilder.BuildLines(bottomWords, p, _cfg.Thresholds.Paragraph.LineMergeY, _cfg.Thresholds.Paragraph.WordGapX);
                        var paras = ParagraphBuilder.BuildParagraphs(lines, _cfg.Thresholds.Paragraph.ParagraphGapY);
                        var hints = new List<string>();
                        hints.AddRange(_cfg.DespachoType.GeorcHints);
                        hints.AddRange(_cfg.DespachoType.AutorizacaoHints);
                        foreach (var para in paras)
                            AddParagraph(para);
                    }
                }
            }

            AddParagraph(firstPara);
            AddParagraph(lastPara);
            // reindex
            for (int i = 0; i < paragraphs.Count; i++)
                paragraphs[i].Index = i;

            doc.Bands = bands;
            doc.Paragraphs = paragraphs.Select(p => new ParagraphInfo
            {
                Page1 = p.Page1,
                Index = p.Index,
                Text = p.Text,
                HashSha256 = TextUtils.Sha256Hex(TextUtils.NormalizeForHash(p.Text)),
                BBoxN = p.BBox
            }).ToList();

            var fullText = string.Join("\n", pages.Select(p => p.Text));
            var ctx = new DespachoContext
            {
                FullText = fullText,
                Paragraphs = paragraphs,
                Bands = bands,
                BandSegments = bandSegments,
                Regions = regions ?? new List<RegionSegment>(),
                Pages = pages,
                FileName = Path.GetFileName(analysis.FilePath ?? ""),
                FilePath = analysis.FilePath ?? "",
                Config = _cfg,
                StartPage1 = startPage1,
                EndPage1 = endPage1,
                ProcessNumber = processNumber ?? "",
                FooterSigners = footerSigners ?? new List<string>(),
                FooterSignatureRaw = footerSignatureRaw
            };

            var fieldExtractor = new FieldExtractor(_cfg);
            doc.Fields = fieldExtractor.ExtractAll(ctx);
            if (fieldWhitelist != null && fieldWhitelist.Count > 0)
                doc.Fields = FilterFields(doc.Fields, fieldWhitelist);

            var warnings = new List<string>();
            if (includeProcessWarnings)
            {
                if (doc.Fields.TryGetValue("PROCESSO_ADMINISTRATIVO", out var procAdmin) &&
                    doc.Fields.TryGetValue("PROCESSO_JUDICIAL", out var procJud))
                {
                    if (procAdmin.Method == "not_found" && procJud.Method == "not_found")
                        warnings.Add("missing_process_numbers");
                }
            }
            doc.Warnings = warnings;

            return doc;
        }

        private Dictionary<string, FieldInfo> FilterFields(Dictionary<string, FieldInfo> fields, ISet<string> whitelist)
        {
            var filtered = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in whitelist)
            {
                if (fields != null && fields.TryGetValue(key, out var info) && info != null)
                {
                    filtered[key] = info;
                }
                else
                {
                    filtered[key] = new FieldInfo
                    {
                        Value = "-",
                        Confidence = 0.0,
                        Method = "not_found",
                        Evidence = null
                    };
                }
            }
            return filtered;
        }

        private ParagraphSegment? SelectParagraphByHints(List<ParagraphSegment> paras, List<string> hints, Regex? primaryRegex = null)
        {
            if (paras == null || paras.Count == 0) return null;
            if (hints == null || hints.Count == 0) return null;
            if (primaryRegex != null)
            {
                foreach (var p in paras)
                {
                    var raw = p.Text ?? "";
                    var collapsed = TextUtils.CollapseSpacedLettersText(raw);
                    if (primaryRegex.IsMatch(collapsed) || primaryRegex.IsMatch(raw))
                        return p;
                }
            }
            var normHints = hints.Where(h => !string.IsNullOrWhiteSpace(h))
                                 .Select(h => TextUtils.NormalizeForMatch(h))
                                 .ToList();
            foreach (var p in paras)
            {
                var norm = TextUtils.NormalizeForMatch(TextUtils.CollapseSpacedLettersText(p.Text ?? ""));
                foreach (var h in normHints)
                {
                    if (norm.Contains(h))
                        return p;
                }
            }
            return null;
        }

        private void AddBodyBand(List<BandInfo> bands, List<BandSegment> bandSegments, int page1, List<WordInfo> words)
        {
            if (words == null || words.Count == 0) return;
            var text = TextUtils.BuildTextFromWords(words, _cfg.Thresholds.Paragraph.LineMergeY, _cfg.TemplateRegions.WordGapX);
            var bbox = TextUtils.UnionBBox(words);
            var hash = TextUtils.Sha256Hex(TextUtils.NormalizeForHash(text));

            bands.Add(new BandInfo
            {
                Page1 = page1,
                Band = "body",
                Text = text,
                HashSha256 = hash,
                BBoxN = bbox
            });

            bandSegments.Add(new BandSegment
            {
                Page1 = page1,
                Band = "body",
                Text = text,
                Words = words,
                BBox = bbox
            });
        }

        private List<RegionSegment> BuildTemplateRegions(PDFAnalysisResult analysis, int startPage1, int endPage1)
        {
            var regions = new List<RegionSegment>();
            if (analysis.Pages == null || analysis.Pages.Count == 0) return regions;

            var firstPage = analysis.Pages[Math.Max(0, startPage1 - 1)];
            var lastPage = analysis.Pages[Math.Max(0, endPage1 - 1)];

            var topCfg = _cfg.TemplateRegions.FirstPageTop;
            if (topCfg.Templates.Count > 0)
            {
                var r = BuildRegion("first_top", firstPage.TextInfo?.Words, startPage1, topCfg.MinY, topCfg.MaxY);
                if (r != null) regions.Add(r);
            }

            var bottomCfg = _cfg.TemplateRegions.LastPageBottom;
            if (bottomCfg.Templates.Count > 0)
            {
                var r = BuildRegion("last_bottom", lastPage.TextInfo?.Words, endPage1, bottomCfg.MinY, bottomCfg.MaxY);
                if (r != null) regions.Add(r);
                if (endPage1 - startPage1 >= 2)
                {
                    var secondPage = analysis.Pages[Math.Max(0, startPage1)];
                    var rSecond = BuildRegion("second_bottom", secondPage.TextInfo?.Words, startPage1 + 1, bottomCfg.MinY, bottomCfg.MaxY);
                    if (rSecond != null) regions.Add(rSecond);
                }
                if (endPage1 > startPage1)
                {
                    var prevPage = analysis.Pages[Math.Max(0, endPage1 - 2)];
                    var rPrev = BuildRegion("last_bottom_prev", prevPage.TextInfo?.Words, endPage1 - 1, bottomCfg.MinY, bottomCfg.MaxY);
                    if (rPrev != null) regions.Add(rPrev);
                }
            }

            return regions;
        }

        private RegionSegment? BuildRegion(string name, List<WordInfo>? words, int page1, double minY, double maxY)
        {
            if (words == null || words.Count == 0) return null;
            minY = Math.Max(0, Math.Min(1, minY));
            maxY = Math.Max(0, Math.Min(1, maxY));
            if (minY > maxY) (minY, maxY) = (maxY, minY);

            var filtered = TextUtils.DeduplicateWords(words)
                .Where(w => w != null && w.NormY1 >= minY && w.NormY0 <= maxY)
                .OrderByDescending(w => (w.NormY0 + w.NormY1) / 2.0)
                .ThenBy(w => w.NormX0)
                .ToList();

            if (filtered.Count == 0) return null;
            var text = TextUtils.BuildTextFromWords(filtered, _cfg.Thresholds.Paragraph.LineMergeY, _cfg.TemplateRegions.WordGapX);
            var bbox = TextUtils.UnionBBox(filtered);

            return new RegionSegment
            {
                Name = name,
                Page1 = page1,
                Words = filtered,
                Text = text,
                BBox = bbox
            };
        }

        private List<BookmarkInfo> FlattenBookmarks(BookmarkStructure? bookmarks)
        {
            var list = new List<BookmarkInfo>();
            if (bookmarks?.RootItems == null) return list;

            void Walk(IEnumerable<BookmarkItem> items)
            {
                foreach (var item in items)
                {
                    var page1 = item.Destination?.PageNumber ?? 0;
                    if (page1 > 0)
                    {
                        list.Add(new BookmarkInfo
                        {
                            Title = item.Title ?? "",
                            Page1 = page1,
                            Page0 = page1 - 1
                        });
                    }
                    if (item.Children != null && item.Children.Count > 0)
                        Walk(item.Children);
                }
            }

            Walk(bookmarks.RootItems);
            return list;
        }

        private Dictionary<int, double> ComputeDensities(PDFAnalysisResult analysis)
        {
            var dict = new Dictionary<int, double>();
            for (int i = 0; i < analysis.Pages.Count; i++)
            {
                var page = analysis.Pages[i];
                var area = Math.Max(1.0, page.Size.Width * page.Size.Height);
                double wordsArea = 0;
                foreach (var w in page.TextInfo.Words)
                {
                    var wArea = Math.Max(0, (w.X1 - w.X0) * (w.Y1 - w.Y0));
                    wordsArea += wArea;
                }
                dict[i + 1] = wordsArea / area;
            }
            return dict;
        }

        private List<(int startPage1, int endPage1)> BuildCandidateWindows(List<BookmarkInfo> bookmarks, Dictionary<int, double> density, int totalPages)
        {
            var candidates = new HashSet<(int, int)>();

            foreach (var bm in bookmarks)
            {
                if (DocumentValidationRules.ContainsBookmarkKeywordsForDoc(bm.Title, null))
                {
                    AddWindows(candidates, bm.Page1, totalPages);
                }
            }

            return candidates.Select(c => (c.Item1, c.Item2)).ToList();
        }

        private void AddWindows(HashSet<(int, int)> set, int page1, int totalPages)
        {
            int[] sizes = { 2, 3, 4 };
            foreach (var s in sizes)
            {
                var end = Math.Min(totalPages, page1 + s - 1);
                if (end >= page1)
                    set.Add((page1, end));
            }
        }

        private CandidateWindowInfo ScoreCandidate(PDFAnalysisResult analysis, int startPage1, int endPage1, Dictionary<int, double> density, string? bookmarkTitle = null, int? bookmarkLevel = null, string? source = null)
        {
            var text = string.Join("\n", Enumerable.Range(startPage1, endPage1 - startPage1 + 1)
                .Select(p => analysis.Pages[p - 1].TextInfo.PageText ?? ""));
            var norm = TextUtils.NormalizeForMatch(text);

            var anchorsHit = new List<string>();
            if (ContainsAny(norm, _cfg.Anchors.Header)) anchorsHit.Add("HEADER_TJPB");
            if (ContainsAny(norm, _cfg.Anchors.Subheader)) anchorsHit.Add("DIRETORIA_ESPECIAL");
            if (ContainsAny(norm, _cfg.Anchors.Title)) anchorsHit.Add("DESPACHO_TITULO");
            if (ContainsAny(norm, _cfg.Anchors.Footer)) anchorsHit.Add("ASSINATURA_ELETRONICA");

            var hasRobson = ContainsAny(norm, _cfg.Anchors.SignerHints);
            var hasCrc = norm.Contains("crc");

            var template = BuildTemplateText();
            var anchorText = BuildAnchorText(text);
            var scoreDmp = ComputeDmpScore(template, anchorText);
            var scoreDiffPlex = scoreDmp < _cfg.Thresholds.Match.DocScoreMin
                ? DiffPlexMatcher.Similarity(TextUtils.NormalizeForMatch(template), TextUtils.NormalizeForMatch(anchorText))
                : scoreDmp;

            var densityMap = new Dictionary<string, double>();
            for (int p = startPage1; p <= endPage1; p++)
                densityMap[$"p{p}"] = density.TryGetValue(p, out var d) ? d : 0;

            var signals = new Dictionary<string, object>
            {
                { "hasRobson", hasRobson },
                { "hasCRC", hasCrc },
                { "hasDiretoriaEspecial", anchorsHit.Contains("DIRETORIA_ESPECIAL") }
            };
            if (!string.IsNullOrWhiteSpace(source))
                signals["source"] = source;
            if (!string.IsNullOrWhiteSpace(bookmarkTitle))
                signals["bookmarkTitle"] = bookmarkTitle;
            if (bookmarkLevel.HasValue)
                signals["bookmarkLevel"] = bookmarkLevel.Value;

            return new CandidateWindowInfo
            {
                StartPage1 = startPage1,
                EndPage1 = endPage1,
                ScoreDmp = scoreDmp,
                ScoreDiffPlex = scoreDiffPlex,
                AnchorsHit = anchorsHit,
                Density = densityMap,
                Signals = signals
            };
        }

        private class BookmarkRange
        {
            public string Title { get; set; } = "";
            public int Level { get; set; }
            public int StartPage1 { get; set; }
            public int EndPage1 { get; set; }
        }

        private List<BookmarkRange> BuildBookmarkRanges(BookmarkStructure? bookmarks, int totalPages)
        {
            var list = new List<BookmarkRange>();
            if (bookmarks?.RootItems == null || bookmarks.RootItems.Count == 0) return list;

            var flat = new List<BookmarkItem>();
            void Walk(IEnumerable<BookmarkItem> items)
            {
                foreach (var item in items)
                {
                    flat.Add(item);
                    if (item.Children != null && item.Children.Count > 0)
                        Walk(item.Children);
                }
            }

            Walk(bookmarks.RootItems);

            var ordered = flat
                .Where(b => b.Destination?.PageNumber > 0)
                .Select(b => new { b.Title, b.Level, Page1 = b.Destination.PageNumber })
                .OrderBy(b => b.Page1)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                var start = ordered[i].Page1;
                var end = (i + 1 < ordered.Count) ? Math.Max(start, ordered[i + 1].Page1 - 1) : totalPages;
                list.Add(new BookmarkRange
                {
                    Title = ordered[i].Title ?? "",
                    Level = ordered[i].Level,
                    StartPage1 = start,
                    EndPage1 = end
                });
            }

            return list;
        }

        private bool IsDespachoBookmark(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return false;
            var norm = TextUtils.NormalizeForMatch(title);
            if (DocumentValidationRules.ContainsBookmarkKeywordsForDoc(norm, "despacho")) return true;
            foreach (var t in _cfg.Anchors.Title)
            {
                if (!string.IsNullOrWhiteSpace(t) && norm.Contains(TextUtils.NormalizeForMatch(t)))
                    return true;
            }
            return false;
        }

        private (int startPage1, int endPage1) AdjustRange(PDFAnalysisResult analysis, int startPage1, int endPage1, Dictionary<int, double> density)
        {
            int total = analysis.DocumentInfo?.TotalPages ?? analysis.Pages.Count;
            int start = startPage1;
            int end = endPage1;

            if (start > 1)
            {
                var prevText = TextUtils.NormalizeForMatch(analysis.Pages[start - 2].TextInfo.PageText ?? "");
                if (ContainsAny(prevText, _cfg.Anchors.Header))
                    start -= 1;
            }

            bool footerFound = WindowHasFooter(analysis, start, end);

            while (end - start + 1 < _cfg.Thresholds.MinPages && end < total)
            {
                end++;
                footerFound = footerFound || PageHasFooter(analysis, end);
            }

            while (!footerFound && end < total && end - start + 1 < _cfg.Thresholds.MaxPages)
            {
                end++;
                footerFound = PageHasFooter(analysis, end) || footerFound;
            }

            if (end - start + 1 > _cfg.Thresholds.MaxPages)
                end = start + _cfg.Thresholds.MaxPages - 1;

            return (start, end);
        }

        private bool WindowHasFooter(PDFAnalysisResult analysis, int startPage1, int endPage1)
        {
            for (int p = startPage1; p <= endPage1; p++)
                if (PageHasFooter(analysis, p))
                    return true;
            return false;
        }

        private bool PageHasFooter(PDFAnalysisResult analysis, int page1)
        {
            var text = TextUtils.NormalizeForMatch(analysis.Pages[page1 - 1].TextInfo.PageText ?? "");
            return ContainsAny(text, _cfg.Anchors.Footer);
        }

        private bool ContainsAny(string textNorm, List<string> anchors)
        {
            if (anchors == null || anchors.Count == 0) return false;
            foreach (var a in anchors)
            {
                if (string.IsNullOrWhiteSpace(a)) continue;
                if (textNorm.Contains(TextUtils.NormalizeForMatch(a)))
                    return true;
            }
            return false;
        }

        private string BuildTemplateText()
        {
            var parts = new List<string>();
            parts.AddRange(_cfg.Anchors.Header);
            parts.AddRange(_cfg.Anchors.Subheader);
            parts.AddRange(_cfg.Anchors.Title);
            parts.AddRange(_cfg.Anchors.Footer);
            return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        private string BuildAnchorText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var normAnchors = _cfg.Anchors.Header
                .Concat(_cfg.Anchors.Subheader)
                .Concat(_cfg.Anchors.Title)
                .Concat(_cfg.Anchors.Footer)
                .Select(TextUtils.NormalizeForMatch)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            var lines = text.Split('\n');
            var chosen = new List<string>();
            foreach (var line in lines)
            {
                var ln = TextUtils.NormalizeForMatch(line);
                if (normAnchors.Any(a => ln.Contains(a)))
                    chosen.Add(line);
            }
            if (chosen.Count == 0)
            {
                var clean = TextUtils.NormalizeWhitespace(text);
                return clean.Length > 2000 ? clean.Substring(0, 2000) : clean;
            }
            return string.Join("\n", chosen);
        }

        private double ComputeDmpScore(string template, string text)
        {
            var tNorm = TextUtils.NormalizeForMatch(template);
            var xNorm = TextUtils.NormalizeForMatch(text);
            if (string.IsNullOrWhiteSpace(tNorm) || string.IsNullOrWhiteSpace(xNorm)) return 0;
            var diffs = _dmp.diff_main(tNorm, xNorm, false);
            _dmp.diff_cleanupSemantic(diffs);
            var dist = _dmp.diff_levenshtein(diffs);
            var maxLen = Math.Max(tNorm.Length, xNorm.Length);
            if (maxLen == 0) return 0;
            var score = 1.0 - (double)dist / maxLen;
            if (score < 0) score = 0;
            return score;
        }

        private string ComputeFileSha256(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(path);
                var hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        private string Truncate(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= maxLen) return text;
            return text.Substring(0, maxLen);
        }

        private void Log(ExtractionResult result, Action<LogEntry>? logFn, string level, string message, Dictionary<string, object> data)
        {
            var entry = new LogEntry
            {
                Level = level,
                Message = message,
                Data = data,
                At = DateTime.UtcNow.ToString("o")
            };
            result.Logs.Add(entry);
            logFn?.Invoke(entry);
        }

        private List<SignatureInfo> MergeSignatures(List<SignatureInfo> digital, List<SignatureInfo> text)
        {
            var all = new List<SignatureInfo>();
            if (digital != null) all.AddRange(digital);
            if (text != null) all.AddRange(text);
            return all
                .GroupBy(s => $"{s.Method}|{s.FieldName}|{s.Page1}|{s.SignerName}")
                .Select(g => g.First())
                .ToList();
        }

        private List<SignatureInfo> BuildFieldSignatures(DespachoDocumentInfo doc)
        {
            var list = new List<SignatureInfo>();
            if (doc == null || doc.Fields == null) return list;
            if (!doc.Fields.TryGetValue("ASSINANTE", out var ass) || ass.Method == "not_found")
                return list;

            var sig = new SignatureInfo
            {
                Method = "field_assinante",
                FieldName = "",
                SignerName = ass.Value ?? "",
                Page1 = ass.Evidence?.Page1 ?? 0,
                BBoxN = ass.Evidence?.BBoxN,
                Snippet = ass.Evidence?.Snippet ?? ""
            };
            if (doc.Fields.TryGetValue("DATA", out var data) && data.Method != "not_found")
                sig.SignDate = data.Value;
            list.Add(sig);
            return list;
        }
    }
}
