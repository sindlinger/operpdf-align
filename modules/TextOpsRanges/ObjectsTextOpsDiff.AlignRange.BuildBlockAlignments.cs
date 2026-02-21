using System;
using System.Collections.Generic;
using System.Linq;

namespace Obj.Align
{
    internal static partial class ObjectsTextOpsDiff
    {
        private static List<BlockAlignment> BuildBlockAlignments(
            List<SelfBlock> blocksA,
            List<SelfBlock> blocksB,
            out List<string> normA,
            out List<string> normB,
            out List<AnchorPair> anchors,
            double minSim = 0.0,
            int band = 0,
            double minLenRatio = 0.05,
            double lenPenalty = 0.0,
            double anchorMinSim = 0.0,
            double anchorMinLenRatio = 0.0,
            double gapPenalty = -0.35)
        {
            return BuildBlockAlignments(
                blocksA,
                blocksB,
                out normA,
                out normB,
                out anchors,
                out _,
                minSim,
                band,
                minLenRatio,
                lenPenalty,
                anchorMinSim,
                anchorMinLenRatio,
                gapPenalty);
        }

        private static List<BlockAlignment> BuildBlockAlignments(
            List<SelfBlock> blocksA,
            List<SelfBlock> blocksB,
            out List<string> normA,
            out List<string> normB,
            out List<AnchorPair> anchors,
            out AlignHelperDiagnostics helperDiagnostics,
            double minSim = 0.0,
            int band = 0,
            double minLenRatio = 0.05,
            double lenPenalty = 0.0,
            double anchorMinSim = 0.0,
            double anchorMinLenRatio = 0.0,
            double gapPenalty = -0.35)
        {
            normA = blocksA.Select(b => NormalizeForSimilarity(b.Text ?? "")).ToList();
            normB = blocksB.Select(b => NormalizeForSimilarity(b.Text ?? "")).ToList();
            anchors = new List<AnchorPair>();
            helperDiagnostics = new AlignHelperDiagnostics();

            double autoMaxSim = 0.0;
            if (anchorMinSim > 0 || anchorMinLenRatio > 0)
            {
                anchors = BuildAnchorPairsExplicit(normA, normB, anchorMinSim, anchorMinLenRatio);
            }
            else
            {
                // DMP-first mode: no automatic anchor constraints unless explicitly requested.
                anchors = new List<AnchorPair>();
                autoMaxSim = 0.0;
            }

            if (anchors.Count == 0)
            {
                var effectiveMinSim = minSim;
                if (effectiveMinSim <= 0)
                    effectiveMinSim = autoMaxSim > 0 ? Math.Max(0.12, autoMaxSim * 0.6) : 0.12;
                return BuildSegmentAlignments(normA, normB, 0, blocksA.Count, 0, blocksB.Count, effectiveMinSim, band, minLenRatio, lenPenalty, gapPenalty);
            }

            var result = new List<BlockAlignment>();
            int prevA = 0;
            int prevB = 0;
            foreach (var a in anchors)
            {
                if (a.AIndex > prevA || a.BIndex > prevB)
                {
                    result.AddRange(BuildSegmentAlignments(normA, normB, prevA, a.AIndex, prevB, a.BIndex, minSim, band, minLenRatio, lenPenalty, gapPenalty));
                }
                result.Add(new BlockAlignment(a.AIndex, a.BIndex, a.Score));
                prevA = a.AIndex + 1;
                prevB = a.BIndex + 1;
            }
            if (prevA < blocksA.Count || prevB < blocksB.Count)
            {
                result.AddRange(BuildSegmentAlignments(normA, normB, prevA, blocksA.Count, prevB, blocksB.Count, minSim, band, minLenRatio, lenPenalty, gapPenalty));
            }
            return result;
        }

        private sealed class AnchorPair
        {
            public int AIndex { get; set; }
            public int BIndex { get; set; }
            public double Score { get; set; }
        }
    }
}
