using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DiffMatchPatch;
using Obj.Models;
using Obj.TjpbDespachoExtractor.Utils;

namespace Obj.TjpbDespachoExtractor.Extraction
{
    public class FieldCandidate
    {
        public string Value { get; set; } = "";
        public ParagraphSegment Paragraph { get; set; } = new ParagraphSegment();
        public string Snippet { get; set; } = "";
        public double Score { get; set; }
        public int MatchIndex { get; set; } = -1;
        public int MatchLength { get; set; } = 0;
    }

    public class TemplateFieldExtractor
    {
        private readonly diff_match_patch _dmp;

        public TemplateFieldExtractor()
        {
            _dmp = new diff_match_patch();
            _dmp.Match_Threshold = 0.6f;
            _dmp.Match_Distance = 5000;
        }

        public FieldCandidate? ExtractFromParagraphs(IEnumerable<ParagraphSegment> paragraphs, IEnumerable<string> templates, Regex valueRegex)
        {
            if (paragraphs == null || templates == null) return null;
            var tmplList = templates.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (tmplList.Count == 0) return null;

            FieldCandidate? best = null;
            foreach (var para in paragraphs)
            {
                var pText = para.Text ?? "";
                var pMatch = TextUtils.CollapseSpacedLettersText(pText);
                var pNorm = TextUtils.NormalizeForMatch(pMatch);
                if (string.IsNullOrWhiteSpace(pNorm)) continue;

                foreach (var tmpl in tmplList)
                {
                    var anchor = tmpl.Replace("{{value}}", "");
                    var anchorNorm = TextUtils.NormalizeForMatch(anchor);
                    if (string.IsNullOrWhiteSpace(anchorNorm)) continue;

                    var diffs = _dmp.diff_main(anchorNorm, pNorm, false);
                    _dmp.diff_cleanupSemantic(diffs);
                    var dist = _dmp.diff_levenshtein(diffs);
                    var maxLen = Math.Max(anchorNorm.Length, pNorm.Length);
                    var score = maxLen > 0 ? 1.0 - (double)dist / maxLen : 0.0;

                    if (score < 0.5) continue;

                    var match = valueRegex.Match(pText);
                    if (match.Success)
                    {
                        var groupIndex = match.Groups.Count > 1 ? 1 : 0;
                        var group = match.Groups[groupIndex];
                        var value = group.Value;
                        var snippet = TextUtils.SafeSnippet(pText, Math.Max(0, group.Index - 40), Math.Min(pText.Length - Math.Max(0, group.Index - 40), 160));
                        var cand = new FieldCandidate
                        {
                            Value = value,
                            Paragraph = para,
                            Snippet = snippet,
                            Score = score,
                            MatchIndex = group.Index,
                            MatchLength = group.Length
                        };
                        if (best == null || cand.Score > best.Score)
                            best = cand;
                    }
                }
            }
            return best;
        }
    }
}
