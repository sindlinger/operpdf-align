using System;
using System.Linq;
using DiffPlex;

namespace Obj.TjpbDespachoExtractor.Extraction
{
    public static class DiffPlexMatcher
    {
        public static double Similarity(string a, string b)
        {
            a = a ?? "";
            b = b ?? "";
            var differ = new Differ();
            var diff = differ.CreateCharacterDiffs(a, b, false, true);
            var edits = diff.DiffBlocks.Sum(d => d.DeleteCountA + d.InsertCountB);
            var maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0) return 0;
            var score = 1.0 - (double)edits / maxLen;
            if (score < 0) score = 0;
            return score;
        }
    }
}
