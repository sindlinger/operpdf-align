using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Obj.Models;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Models;
using Obj.TjpbDespachoExtractor.Utils;
using Obj.ValidatorModule;

namespace Obj.TjpbDespachoExtractor.Extraction
{
    public static class CertidaoExtraction
    {
        public static int FindCertidaoPage(PDFAnalysisResult analysis, TjpbDespachoConfig cfg)
        {
            if (analysis?.Pages == null || analysis.Pages.Count == 0) return 0;
            var pages = new List<int>();
            if (analysis.Bookmarks?.RootItems != null && analysis.Bookmarks.RootItems.Count > 0)
            {
                void Walk(IEnumerable<BookmarkItem> items)
                {
                    foreach (var item in items)
                    {
                        var page1 = item.Destination?.PageNumber ?? 0;
                        if (page1 > 0 && DocumentValidationRules.IsCertidaoBookmarkTitle(item.Title))
                            pages.Add(page1);
                        if (item.Children != null && item.Children.Count > 0)
                            Walk(item.Children);
                    }
                }
                Walk(analysis.Bookmarks.RootItems);
            }
            if (pages.Count == 0) return 0;

            foreach (var p in pages.Distinct().OrderBy(p => p))
            {
                if (IsCertidaoPage(analysis, p, cfg, out _))
                    return p;
            }
            return 0;
        }

        public static bool IsCertidaoPage(PDFAnalysisResult analysis, int page1, TjpbDespachoConfig cfg, out string reason)
        {
            reason = "";
            if (analysis?.Pages == null || analysis.Pages.Count == 0)
            {
                reason = "no_pages";
                return false;
            }
            if (page1 < 1 || page1 > analysis.Pages.Count)
            {
                reason = "page_out_of_range";
                return false;
            }

            var page = analysis.Pages[page1 - 1];
            var pageText = page.TextInfo?.PageText ?? "";
            var pageNorm = TextUtils.NormalizeForMatch(pageText);

            var words = TextUtils.DeduplicateWords(page.TextInfo?.Words ?? new List<WordInfo>());
            var seg = BandSegmenter.SegmentPage(words, page1, cfg.Thresholds.Bands, cfg.Thresholds.Paragraph.LineMergeY, cfg.Thresholds.Paragraph.WordGapX);
            var headerWords = seg.BandSegments
                .Where(b => b.Band.Equals("header", StringComparison.OrdinalIgnoreCase) ||
                            b.Band.Equals("subheader", StringComparison.OrdinalIgnoreCase) ||
                            b.Band.Equals("title", StringComparison.OrdinalIgnoreCase))
                .SelectMany(b => b.Words ?? new List<WordInfo>())
                .ToList();
            var headerText = TextUtils.BuildTextFromWords(headerWords, cfg.Thresholds.Paragraph.LineMergeY, cfg.TemplateRegions.WordGapX);
            var headerNorm = TextUtils.NormalizeForMatch(headerText);
            var footerWords = seg.BandSegments
                .Where(b => b.Band.Equals("footer", StringComparison.OrdinalIgnoreCase))
                .SelectMany(b => b.Words ?? new List<WordInfo>())
                .ToList();
            var footerText = TextUtils.BuildTextFromWords(footerWords, cfg.Thresholds.Paragraph.LineMergeY, cfg.TemplateRegions.WordGapX);
            var footerNorm = TextUtils.NormalizeForMatch(footerText);

            bool hasHeader = ContainsAny(headerNorm, cfg.Certidao.HeaderHints) || ContainsAny(pageNorm, cfg.Certidao.HeaderHints);

            bool hasTitle = ContainsAny(headerNorm, cfg.Certidao.TitleHints) || ContainsAny(pageNorm, cfg.Certidao.TitleHints);

            bool hasBody = ContainsAny(pageNorm, cfg.Certidao.BodyHints);
            bool hasMoney = Regex.IsMatch(pageText, cfg.Regex.Money, RegexOptions.IgnoreCase) || pageNorm.Contains("r$");

            bool hasRobsonFooter = footerNorm.Contains("robson");
            if (!hasRobsonFooter && analysis.Signatures != null)
            {
                foreach (var s in analysis.Signatures)
                {
                    if (s.Page1 != 0 && s.Page1 != page1) continue;
                    var signer = TextUtils.NormalizeForMatch(s.SignerName ?? s.Name ?? s.Certificate?.Subject ?? "");
                    if (signer.Contains("robson"))
                    {
                        hasRobsonFooter = true;
                        break;
                    }
                }
            }
            return DocumentValidationRules.ValidateCertidaoPageSignals(
                hasHeader,
                hasTitle,
                hasBody,
                hasMoney,
                hasRobsonFooter,
                out reason);
        }

        public static List<RegionSegment> BuildCertidaoRegions(PDFAnalysisResult analysis, int page1, TjpbDespachoConfig cfg)
        {
            var regions = new List<RegionSegment>();
            if (analysis?.Pages == null || analysis.Pages.Count == 0) return regions;
            if (page1 < 1 || page1 > analysis.Pages.Count) return regions;

            var page = analysis.Pages[page1 - 1];
            var words = TextUtils.DeduplicateWords(page.TextInfo?.Words ?? new List<WordInfo>());
            var seg = BandSegmenter.SegmentPage(words, page1, cfg.Thresholds.Bands, cfg.Thresholds.Paragraph.LineMergeY, cfg.Thresholds.Paragraph.WordGapX);

            var footerWords = seg.BandSegments
                .Where(b => b.Band.Equals("footer", StringComparison.OrdinalIgnoreCase))
                .SelectMany(b => b.Words ?? new List<WordInfo>())
                .ToList();

            var bodyWords = new List<WordInfo>();
            if (seg.BodyWords != null) bodyWords.AddRange(seg.BodyWords);
            if (footerWords.Count > 0) bodyWords.AddRange(footerWords);

            var bodyLines = LineBuilder.BuildLines(bodyWords, page1, cfg.Thresholds.Paragraph.LineMergeY, cfg.Thresholds.Paragraph.WordGapX);
            var paragraphs = ParagraphBuilder.BuildParagraphs(bodyLines, cfg.Thresholds.Paragraph.ParagraphGapY);

            var startPara = SelectStartParagraph(paragraphs);
            int startIdx = startPara != null ? paragraphs.IndexOf(startPara) : 0;
            if (startIdx < 0) startIdx = 0;

            var fullWords = paragraphs.Skip(startIdx).SelectMany(p => p.Words ?? new List<WordInfo>()).ToList();
            var fullRegion = BuildRegion("certidao_full", page1, fullWords, cfg);
            if (fullRegion != null)
            {
                if (fullRegion.BBox != null)
                {
                    fullRegion.BBox = new BBoxN
                    {
                        X0 = 0.0,
                        X1 = 1.0,
                        Y0 = Clamp01(fullRegion.BBox.Y0),
                        Y1 = Clamp01(fullRegion.BBox.Y1)
                    };
                }
                regions.Add(fullRegion);
            }

            var valuePara = SelectValueParagraph(paragraphs, cfg);
            var datePara = SelectDateParagraph(paragraphs, cfg);
            var valueDateWords = new List<WordInfo>();
            if (valuePara?.Words != null && valuePara.Words.Count > 0)
            {
                valueDateWords.AddRange(valuePara.Words);
            }
            else
            {
                var moneyRx = new Regex(cfg.Regex.Money, RegexOptions.IgnoreCase);
                var moneyLine = bodyLines.FirstOrDefault(l => moneyRx.IsMatch(l.Text ?? ""));
                if (moneyLine?.Words != null) valueDateWords.AddRange(moneyLine.Words);
            }
            if (datePara?.Words != null && datePara.Words.Count > 0)
            {
                valueDateWords.AddRange(datePara.Words);
            }
            else
            {
                var dateRx = new Regex(cfg.Regex.DatePt, RegexOptions.IgnoreCase);
                LineSegment? dateLine = null;
                foreach (var l in bodyLines)
                {
                    var text = l.Text ?? "";
                    if (!dateRx.IsMatch(text)) continue;
                    var norm = TextUtils.NormalizeForMatch(text);
                    if (ContainsAny(norm, cfg.Certidao.DateHints))
                        dateLine = l;
                }
                if (dateLine == null)
                    dateLine = bodyLines.LastOrDefault(l => dateRx.IsMatch(l.Text ?? ""));
                if (dateLine?.Words != null) valueDateWords.AddRange(dateLine.Words);
            }
            var valueDateRegion = BuildRegion("certidao_value_date", page1, valueDateWords, cfg);
            if (valueDateRegion != null) regions.Add(valueDateRegion);

            return regions;
        }

        private static ParagraphSegment? SelectStartParagraph(List<ParagraphSegment> paragraphs)
        {
            foreach (var p in paragraphs)
            {
                var norm = TextUtils.NormalizeForMatch(p.Text ?? "");
                if (norm.Contains("certifico") && norm.Contains("conselho") && norm.Contains("magistratura"))
                    return p;
            }
            foreach (var p in paragraphs)
            {
                var norm = TextUtils.NormalizeForMatch(p.Text ?? "");
                if (norm.Contains("certifico"))
                    return p;
            }
            foreach (var p in paragraphs)
            {
                var norm = TextUtils.NormalizeForMatch(p.Text ?? "");
                if (norm.Contains("proferiram") && norm.Contains("decis"))
                    return p;
            }
            return paragraphs.FirstOrDefault();
        }

        private static ParagraphSegment? SelectValueParagraph(List<ParagraphSegment> paragraphs, TjpbDespachoConfig cfg)
        {
            var moneyRx = new Regex(cfg.Regex.Money, RegexOptions.IgnoreCase);
            ParagraphSegment? firstMoney = null;
            foreach (var p in paragraphs)
            {
                var text = p.Text ?? "";
                if (!moneyRx.IsMatch(text)) continue;
                if (firstMoney == null) firstMoney = p;
                var norm = TextUtils.NormalizeForMatch(text);
                if (norm.Contains("honor") || norm.Contains("pagamento") || norm.Contains("autorizad"))
                    return p;
            }
            return firstMoney;
        }

        private static ParagraphSegment? SelectDateParagraph(List<ParagraphSegment> paragraphs, TjpbDespachoConfig cfg)
        {
            var dateRx = new Regex(cfg.Regex.DatePt, RegexOptions.IgnoreCase);
            ParagraphSegment? candidate = null;
            ParagraphSegment? fallback = null;
            foreach (var p in paragraphs)
            {
                var text = p.Text ?? "";
                if (!dateRx.IsMatch(text)) continue;
                fallback = p;
                var norm = TextUtils.NormalizeForMatch(text);
                if (ContainsAny(norm, cfg.Certidao.DateHints))
                    candidate = p;
            }
            return candidate ?? fallback;
        }

        private static RegionSegment? BuildRegion(string name, int page1, List<WordInfo> words, TjpbDespachoConfig cfg)
        {
            if (words == null || words.Count == 0) return null;
            var filtered = TextUtils.DeduplicateWords(words)
                .Where(w => w != null)
                .OrderByDescending(w => (w.NormY0 + w.NormY1) / 2.0)
                .ThenBy(w => w.NormX0)
                .ToList();
            if (filtered.Count == 0) return null;
            var text = TextUtils.BuildTextFromWords(filtered, cfg.Thresholds.Paragraph.LineMergeY, cfg.TemplateRegions.WordGapX);
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

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        private static bool ContainsAny(string norm, List<string> hints)
        {
            foreach (var h in hints ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                if (norm.Contains(TextUtils.NormalizeForMatch(h)))
                    return true;
            }
            return false;
        }
    }
}
