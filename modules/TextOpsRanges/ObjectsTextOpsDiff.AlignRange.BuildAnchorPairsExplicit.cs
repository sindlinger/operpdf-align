using System.Collections.Generic;
using System.Linq;

namespace Obj.Align
{
    internal static partial class ObjectsTextOpsDiff
    {
        private static List<AnchorPair> BuildAnchorPairsExplicit(List<string> normA, List<string> normB, double minSim, double minLenRatio)
        {
            var anchors = new List<AnchorPair>();
            if (minSim <= 0 && minLenRatio <= 0)
                return anchors;

            var candidates = new List<AnchorPair>();
            var bestSimA = new double[normA.Count];
            var bestIdxA = new int[normA.Count];
            var bestSimB = new double[normB.Count];
            var bestIdxB = new int[normB.Count];
            for (int i = 0; i < bestIdxA.Length; i++) bestIdxA[i] = -1;
            for (int i = 0; i < bestIdxB.Length; i++) bestIdxB[i] = -1;

            for (int i = 0; i < normA.Count; i++)
            {
                for (int j = 0; j < normB.Count; j++)
                {
                    if (ShouldRejectAsAnchorMismatch(normA[i], normB[j]))
                        continue;
                    var sim = ComputeAlignmentSimilarity(normA[i], normB[j]);
                    if (sim > bestSimA[i])
                    {
                        bestSimA[i] = sim;
                        bestIdxA[i] = j;
                    }
                    if (sim > bestSimB[j])
                    {
                        bestSimB[j] = sim;
                        bestIdxB[j] = i;
                    }
                    if (minSim > 0 && sim < minSim)
                        continue;
                    var lenRatio = ComputeLenRatio(normA[i], normB[j]);
                    var anchorCue = IsAnchorModelCue(normA[i]) || IsAnchorModelCue(normB[j]);
                    if (minLenRatio > 0 && lenRatio < minLenRatio && !anchorCue)
                        continue;
                    candidates.Add(new AnchorPair { AIndex = i, BIndex = j, Score = sim });
                }
            }
            if (candidates.Count == 0)
                return anchors;

            // Keep only mutual best pairs to avoid noisy anchors.
            candidates = candidates
                .Where(c => bestIdxA[c.AIndex] == c.BIndex && bestIdxB[c.BIndex] == c.AIndex)
                .OrderBy(c => c.AIndex)
                .ThenBy(c => c.BIndex)
                .ToList();
            if (candidates.Count == 0)
                return anchors;

            var dp = new double[candidates.Count];
            var prev = new int[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                dp[i] = candidates[i].Score;
                prev[i] = -1;
                for (int j = 0; j < i; j++)
                {
                    if (candidates[j].AIndex < candidates[i].AIndex &&
                        candidates[j].BIndex < candidates[i].BIndex)
                    {
                        var cand = dp[j] + candidates[i].Score;
                        if (cand > dp[i])
                        {
                            dp[i] = cand;
                            prev[i] = j;
                        }
                    }
                }
            }

            int best = 0;
            for (int i = 1; i < dp.Length; i++)
            {
                if (dp[i] > dp[best])
                    best = i;
            }

            var stack = new Stack<AnchorPair>();
            int cur = best;
            while (cur >= 0)
            {
                stack.Push(candidates[cur]);
                cur = prev[cur];
            }
            anchors.AddRange(stack);
            return anchors;
        }
    }
}
