using System;
using System.Collections.Generic;
using System.Linq;
using Obj.Models;
using Obj.TjpbDespachoExtractor.Models;
using Obj.TjpbDespachoExtractor.Utils;

namespace Obj.Models
{
    public class LineSegment
    {
        public int Page1 { get; set; }
        public List<WordInfo> Words { get; set; } = new List<WordInfo>();
        public string Text { get; set; } = "";
        public BBoxN? BBox { get; set; }
        public double CenterY { get; set; }
    }

    public class ParagraphSegment
    {
        public int Page1 { get; set; }
        public int Index { get; set; }
        public List<WordInfo> Words { get; set; } = new List<WordInfo>();
        public string Text { get; set; } = "";
        public BBoxN? BBox { get; set; }
        public double TopY { get; set; }
        public double BottomY { get; set; }
    }

    public class BandSegment
    {
        public int Page1 { get; set; }
        public string Band { get; set; } = "";
        public List<WordInfo> Words { get; set; } = new List<WordInfo>();
        public string Text { get; set; } = "";
        public BBoxN? BBox { get; set; }
    }

    public class RegionSegment
    {
        public int Page1 { get; set; }
        public string Name { get; set; } = "";
        public List<WordInfo> Words { get; set; } = new List<WordInfo>();
        public string Text { get; set; } = "";
        public BBoxN? BBox { get; set; }
    }

    public static class LineBuilder
    {
        public static List<LineSegment> BuildLines(IEnumerable<WordInfo> words, int page1, double lineMergeY, double wordGapX)
        {
            var list = words
                .OrderByDescending(w => (w.NormY0 + w.NormY1) / 2.0)
                .ThenBy(w => w.NormX0)
                .ToList();

            var lines = new List<LineSegment>();
            LineSegment? current = null;
            foreach (var w in list)
            {
                var cy = (w.NormY0 + w.NormY1) / 2.0;
                if (current == null || Math.Abs(cy - current.CenterY) > lineMergeY)
                {
                    if (current != null)
                        FinalizeLine(current, lines, wordGapX);
                    current = new LineSegment { Page1 = page1, CenterY = cy };
                }
                current.Words.Add(w);
            }
            if (current != null)
                FinalizeLine(current, lines, wordGapX);
            return lines;
        }

        private static void FinalizeLine(LineSegment line, List<LineSegment> lines, double wordGapX)
        {
            line.Words = line.Words.OrderBy(w => w.NormX0).ToList();
            line.Text = TextUtils.BuildLineText(line.Words, wordGapX);
            line.BBox = TextUtils.UnionBBox(line.Words);
            lines.Add(line);
        }
    }

    public static class ParagraphBuilder
    {
        public static List<ParagraphSegment> BuildParagraphs(IEnumerable<LineSegment> lines, double paragraphGapY)
        {
            var ordered = lines
                .OrderByDescending(l => l.CenterY)
                .ToList();

            var paragraphs = new List<ParagraphSegment>();
            ParagraphSegment? current = null;
            int index = 0;
            foreach (var line in ordered)
            {
                var lineTop = line.BBox?.Y1 ?? line.CenterY;
                var lineBottom = line.BBox?.Y0 ?? line.CenterY;

                if (current == null)
                {
                    current = new ParagraphSegment
                    {
                        Page1 = line.Page1,
                        Index = index++
                    };
                    current.TopY = lineTop;
                    current.BottomY = lineBottom;
                }
                else
                {
                    var gap = current.BottomY - lineTop;
                    if (gap > paragraphGapY)
                    {
                        FinalizeParagraph(current, paragraphs);
                        current = new ParagraphSegment
                        {
                            Page1 = line.Page1,
                            Index = index++
                        };
                        current.TopY = lineTop;
                        current.BottomY = lineBottom;
                    }
                    else
                    {
                        current.BottomY = Math.Min(current.BottomY, lineBottom);
                    }
                }

                current.Words.AddRange(line.Words);
            }

            if (current != null)
                FinalizeParagraph(current, paragraphs);

            return paragraphs;
        }

        private static void FinalizeParagraph(ParagraphSegment paragraph, List<ParagraphSegment> paragraphs)
        {
            paragraph.Words = paragraph.Words
                .OrderByDescending(w => (w.NormY0 + w.NormY1) / 2.0)
                .ThenBy(w => w.NormX0)
                .ToList();
            paragraph.Text = TextUtils.NormalizeWhitespace(string.Join(" ", paragraph.Words.Select(w => w.Text)));
            paragraph.BBox = TextUtils.UnionBBox(paragraph.Words);
            paragraphs.Add(paragraph);
        }
    }
}
