using System;
using System.Collections.Generic;
using System.Linq;
using Obj.Models;
using Obj.DocDetector;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Models;
using Obj.TjpbDespachoExtractor.Utils;
using Obj.ValidatorModule;

namespace Obj.TjpbDespachoExtractor.Utils
{
    public class BandSegmentationResult
    {
        public List<BandInfo> Bands { get; set; } = new List<BandInfo>();
        public List<BandSegment> BandSegments { get; set; } = new List<BandSegment>();
        public List<LineSegment> Lines { get; set; } = new List<LineSegment>();
        public List<WordInfo> BodyWords { get; set; } = new List<WordInfo>();
    }

    public static class BandSegmenter
    {
        public static BandSegmentationResult SegmentPage(List<WordInfo> words, int page1, BandsConfig bands, double lineMergeY, double wordGapX)
        {
            var result = new BandSegmentationResult();
            var lines = LineBuilder.BuildLines(words, page1, lineMergeY, wordGapX);
            result.Lines = lines;

            double headerStart = 1.0 - bands.HeaderTopPct;
            double subheaderStart = headerStart - bands.SubheaderPct;
            double footerEnd = bands.FooterBottomPct;
            double bodyStart = bands.BodyStartPct;

            var headerLines = new List<LineSegment>();
            var subheaderLines = new List<LineSegment>();
            var bodyLines = new List<LineSegment>();
            var footerLines = new List<LineSegment>();
            var titleLines = new List<LineSegment>();

            foreach (var line in lines)
            {
                var cy = line.CenterY;
                if (cy >= headerStart)
                {
                    headerLines.Add(line);
                }
                else if (cy >= subheaderStart)
                {
                    subheaderLines.Add(line);
                    var norm = TextUtils.NormalizeForMatch(line.Text);
                    if (DocumentValidationRules.ContainsContentsTitleKeywordsForDoc(norm, null))
                        titleLines.Add(line);
                }
                else if (cy <= footerEnd)
                {
                    footerLines.Add(line);
                }
                else if (cy >= bodyStart)
                {
                    bodyLines.Add(line);
                }
                else
                {
                    bodyLines.Add(line);
                }
            }

            result.BodyWords = bodyLines.SelectMany(l => l.Words).ToList();

            AddBand(result, page1, "header", headerLines);
            AddBand(result, page1, "subheader", subheaderLines);
            AddBand(result, page1, "title", titleLines);
            AddBand(result, page1, "body", bodyLines);
            AddBand(result, page1, "footer", footerLines);

            return result;
        }

        private static void AddBand(BandSegmentationResult result, int page1, string band, List<LineSegment> lines)
        {
            var words = lines.SelectMany(l => l.Words).ToList();
            var text = TextUtils.NormalizeWhitespace(string.Join("\n", lines.Select(l => l.Text)));
            var hash = TextUtils.Sha256Hex(TextUtils.NormalizeForHash(text));
            var bbox = TextUtils.UnionBBox(words);

            result.Bands.Add(new BandInfo
            {
                Page1 = page1,
                Band = band,
                Text = text,
                HashSha256 = hash,
                BBoxN = bbox
            });

            result.BandSegments.Add(new BandSegment
            {
                Page1 = page1,
                Band = band,
                Text = text,
                Words = words,
                BBox = bbox
            });
        }

        // BuildBand removed in favor of AddBand to keep BandInfo + BandSegment in sync.
    }
}
