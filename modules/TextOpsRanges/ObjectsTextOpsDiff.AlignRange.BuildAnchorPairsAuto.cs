using System;
using System.Collections.Generic;
using System.Linq;

namespace Obj.Align
{
    internal static partial class ObjectsTextOpsDiff
    {
        private static List<AnchorPair> BuildAnchorPairsAuto(List<string> normA, List<string> normB, double minLenRatio, out double maxSim)
        {
            var anchors = new List<AnchorPair>();
            maxSim = 0.0;
            if (normA.Count == 0 || normB.Count == 0)
                return anchors;

            var bestSimA = new double[normA.Count];
            var bestIdxA = new int[normA.Count];
            var bestSimB = new double[normB.Count];
            var bestIdxB = new int[normB.Count];
            for (int i = 0; i < bestIdxA.Length; i++) bestIdxA[i] = -1;
            for (int i = 0; i < bestIdxB.Length; i++) bestIdxB[i] = -1;

            maxSim = 0.0;
            for (int i = 0; i < normA.Count; i++)
            {
                for (int j = 0; j < normB.Count; j++)
                {
                    if (ShouldRejectAsAnchorMismatch(normA[i], normB[j]))
                        continue;
                    var sim = ComputeAlignmentSimilarity(normA[i], normB[j]);
                    if (sim > maxSim) maxSim = sim;
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
                }
            }

            var anchorCueRateA = normA.Count == 0
                ? 0.0
                : normA.Count(IsAnchorModelCue) / (double)normA.Count;
            var anchorMode = anchorCueRateA >= 0.25;
            var threshold = anchorMode
                ? Math.Max(0.10, maxSim - 0.30)
                : Math.Max(0.35, maxSim - 0.15);
            var candidates = new List<AnchorPair>();
            for (int i = 0; i < normA.Count; i++)
            {
                var j = bestIdxA[i];
                if (j < 0 || j >= normB.Count)
                    continue;
                if (bestIdxB[j] != i)
                    continue;
                var sim = bestSimA[i];
                var anchorCue = IsAnchorModelCue(normA[i]) || IsAnchorModelCue(normB[j]);
                // Do not relax similarity floor for cue labels; very low-sim auto anchors
                // tend to over-constrain segmentation and inflate artificial gaps.
                var localThreshold = threshold;
                if (sim < localThreshold)
                    continue;
                var lenRatio = ComputeLenRatio(normA[i], normB[j]);
                if (lenRatio < minLenRatio && !anchorCue)
                    continue;
                candidates.Add(new AnchorPair { AIndex = i, BIndex = j, Score = sim });
            }

            if (candidates.Count == 0)
                return anchors;

            candidates = candidates
                .OrderBy(c => c.AIndex)
                .ThenBy(c => c.BIndex)
                .ToList();

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
