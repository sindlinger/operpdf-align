using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Obj.TjpbDespachoExtractor.Utils;
using iText.Kernel.Pdf;

namespace Obj.Align
{
    internal static partial class ObjectsTextOpsDiff
    {
        internal sealed class AlignDebugBlock
        {
            public int Index { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string Text { get; set; } = "";
            public string Pattern { get; set; } = "";
            public int MaxTokenLen { get; set; }
            public string OpsLabel { get; set; } = "";
        }

        internal sealed class AlignDebugPair
        {
            public int AIndex { get; set; }
            public int BIndex { get; set; }
            public double Score { get; set; }
            public string Kind { get; set; } = ""; // fixed|variable|gap_a|gap_b
            public AlignDebugBlock? A { get; set; }
            public AlignDebugBlock? B { get; set; }
        }

        internal sealed class AlignDebugRange
        {
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public bool HasValue { get; set; }
        }

        internal sealed class AlignDebugReport
        {
            public string Label { get; set; } = "";
            public string PdfA { get; set; } = "";
            public string PdfB { get; set; } = "";
            public string RoleA { get; set; } = "";
            public string RoleB { get; set; } = "";
            public int PageA { get; set; }
            public int PageB { get; set; }
            public int ObjA { get; set; }
            public int ObjB { get; set; }
            public int Backoff { get; set; }
            public double MinSim { get; set; }
            public int Band { get; set; }
            public double MinLenRatio { get; set; }
            public double LenPenalty { get; set; }
            public double AnchorMinSim { get; set; }
            public double AnchorMinLenRatio { get; set; }
            public double GapPenalty { get; set; }
            public List<AlignDebugPair> Anchors { get; set; } = new List<AlignDebugPair>();
            public AlignHelperDiagnostics? HelperDiagnostics { get; set; }
            public List<AlignDebugBlock> BlocksA { get; set; } = new List<AlignDebugBlock>();
            public List<AlignDebugBlock> BlocksB { get; set; } = new List<AlignDebugBlock>();
            public List<AlignDebugPair> Alignments { get; set; } = new List<AlignDebugPair>();
            public List<AlignDebugBlock> VariableBlocksA { get; set; } = new List<AlignDebugBlock>();
            public List<AlignDebugBlock> VariableBlocksB { get; set; } = new List<AlignDebugBlock>();
            public List<AlignDebugPair> FixedPairs { get; set; } = new List<AlignDebugPair>();
            public AlignDebugRange RangeA { get; set; } = new AlignDebugRange();
            public AlignDebugRange RangeB { get; set; } = new AlignDebugRange();
            public object? Extraction { get; set; }
            public List<Dictionary<string, object>>? PipelineStages { get; set; }
            public Dictionary<string, object>? ReturnInfo { get; set; }
            public Dictionary<string, object>? ReturnView { get; set; }
        }

        internal static AlignDebugReport? ComputeAlignDebugForSelection(
            string aPath,
            string bPath,
            PageObjSelection selA,
            PageObjSelection selB,
            HashSet<string> opFilter,
            int backoff,
            string label,
            double minSim = 0.0,
            int band = 0,
            double minLenRatio = 0.05,
            double lenPenalty = 0.0,
            double anchorMinSim = 0.0,
            double anchorMinLenRatio = 0.0,
            double gapPenalty = -0.35)
        {
            if (string.IsNullOrWhiteSpace(aPath) || string.IsNullOrWhiteSpace(bPath))
                return null;

            using var docA = new PdfDocument(new PdfReader(aPath));
            using var docB = new PdfDocument(new PdfReader(bPath));

            var foundA = FindStreamAndResourcesByObjId(docA, selA.Obj);
            var foundB = FindStreamAndResourcesByObjId(docB, selB.Obj);
            if (foundA.Stream == null || foundA.Resources == null) return null;
            if (foundB.Stream == null || foundB.Resources == null) return null;

            var blocksA = ExtractSelfBlocks(foundA.Stream, foundA.Resources, opFilter);
            var blocksB = ExtractSelfBlocks(foundB.Stream, foundB.Resources, opFilter);

            if (NeedsSpacingFix(blocksA))
                blocksA = ExtractSelfBlocksForPathByPage(aPath, selA.Page, opFilter);
            if (NeedsSpacingFix(blocksB))
                blocksB = ExtractSelfBlocksForPathByPage(bPath, selB.Page, opFilter);

            if (NeedsSpacingFix(blocksB))
            {
                var spacingAnchors = BuildAnchorSet(blocksA);
                blocksB = ApplyAnchorsToBlocks(blocksB, spacingAnchors);
            }
            if (blocksA.Count == 0 || blocksB.Count == 0)
                return null;

            var alignments = BuildBlockAlignments(blocksA, blocksB, out var normA, out var normB, out var anchors, out var helperDiagnostics, minSim, band, minLenRatio, lenPenalty, anchorMinSim, anchorMinLenRatio, gapPenalty);

            var report = new AlignDebugReport
            {
                Label = label ?? "",
                PdfA = Path.GetFileName(aPath),
                PdfB = Path.GetFileName(bPath),
                PageA = selA.Page,
                PageB = selB.Page,
                ObjA = selA.Obj,
                ObjB = selB.Obj,
                Backoff = backoff,
                MinSim = minSim,
                Band = band,
                MinLenRatio = minLenRatio,
                LenPenalty = lenPenalty,
                AnchorMinSim = anchorMinSim,
                AnchorMinLenRatio = anchorMinLenRatio,
                GapPenalty = gapPenalty,
                HelperDiagnostics = helperDiagnostics,
                BlocksA = blocksA.Select(ToDebugBlock).ToList(),
                BlocksB = blocksB.Select(ToDebugBlock).ToList()
            };

            var variableRangeA = new VariableRange();
            var variableRangeB = new VariableRange();

            foreach (var ap in anchors)
            {
                if (ap.AIndex >= 0 && ap.BIndex >= 0 && ap.AIndex < blocksA.Count && ap.BIndex < blocksB.Count)
                {
                    report.Anchors.Add(new AlignDebugPair
                    {
                        AIndex = ap.AIndex,
                        BIndex = ap.BIndex,
                        Score = ap.Score,
                        Kind = "anchor",
                        A = ToDebugBlock(blocksA[ap.AIndex]),
                        B = ToDebugBlock(blocksB[ap.BIndex])
                    });
                }
            }

            foreach (var align in alignments)
            {
                if (align.AIndex >= 0 && align.BIndex >= 0)
                {
                    var same = string.Equals(
                        NormalizeForFixedComparison(blocksA[align.AIndex].Text),
                        NormalizeForFixedComparison(blocksB[align.BIndex].Text),
                        StringComparison.OrdinalIgnoreCase);
                    if (same)
                    {
                        report.FixedPairs.Add(new AlignDebugPair
                        {
                            AIndex = align.AIndex,
                            BIndex = align.BIndex,
                            Score = align.Score,
                            Kind = "fixed",
                            A = ToDebugBlock(blocksA[align.AIndex]),
                            B = ToDebugBlock(blocksB[align.BIndex])
                        });
                        report.Alignments.Add(report.FixedPairs[^1]);
                    }
                    else
                    {
                        AddVariable(blocksA[align.AIndex], ref variableRangeA, report.VariableBlocksA);
                        AddVariable(blocksB[align.BIndex], ref variableRangeB, report.VariableBlocksB);
                        report.Alignments.Add(new AlignDebugPair
                        {
                            AIndex = align.AIndex,
                            BIndex = align.BIndex,
                            Score = align.Score,
                            Kind = "variable",
                            A = ToDebugBlock(blocksA[align.AIndex]),
                            B = ToDebugBlock(blocksB[align.BIndex])
                        });
                    }

                }
                else if (align.AIndex >= 0)
                {
                    AddVariable(blocksA[align.AIndex], ref variableRangeA, report.VariableBlocksA);
                    report.Alignments.Add(new AlignDebugPair
                    {
                        AIndex = align.AIndex,
                        BIndex = -1,
                        Score = align.Score,
                        Kind = "gap_b",
                        A = ToDebugBlock(blocksA[align.AIndex])
                    });
                }
                else if (align.BIndex >= 0)
                {
                    AddVariable(blocksB[align.BIndex], ref variableRangeB, report.VariableBlocksB);
                    report.Alignments.Add(new AlignDebugPair
                    {
                        AIndex = -1,
                        BIndex = align.BIndex,
                        Score = align.Score,
                        Kind = "gap_a",
                        B = ToDebugBlock(blocksB[align.BIndex])
                    });
                }
            }

            if (!variableRangeA.HasValue)
                variableRangeA = FallbackRange(blocksA);
            if (!variableRangeB.HasValue)
                variableRangeB = FallbackRange(blocksB);

            var (startA, endA) = ApplyBackoff(variableRangeA, backoff);
            var (startB, endB) = ApplyBackoff(variableRangeB, backoff);

            report.RangeA = new AlignDebugRange { StartOp = startA, EndOp = endA, HasValue = startA > 0 && endA > 0 };
            report.RangeB = new AlignDebugRange { StartOp = startB, EndOp = endB, HasValue = startB > 0 && endB > 0 };

            return report;
        }

        private static AlignDebugBlock ToDebugBlock(SelfBlock block)
        {
            var text = TextUtils.CollapseSpacedLettersText(block.Text ?? "");
            text = TextUtils.NormalizeWhitespace(TextUtils.FixMissingSpaces(text));
            return new AlignDebugBlock
            {
                Index = block.Index,
                StartOp = block.StartOp,
                EndOp = block.EndOp,
                Text = text,
                Pattern = block.Pattern ?? "",
                MaxTokenLen = block.MaxTokenLen,
                OpsLabel = block.OpsLabel ?? ""
            };
        }

        private static string NormalizeForFixedComparison(string? text)
        {
            var normalized = CollapseSpaces(NormalizeBlockToken(text ?? ""));
            if (string.IsNullOrWhiteSpace(normalized))
                return "";
            return TextUtils.NormalizeWhitespace(TextUtils.FixMissingSpaces(normalized));
        }

        private static void AddVariable(SelfBlock block, ref VariableRange range, List<AlignDebugBlock> list)
        {
            AddBlockToRange(ref range, block);
            list.Add(ToDebugBlock(block));
        }
    }
}
