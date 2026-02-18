using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using System.Text.Json;
using DiffMatchPatch;
using Obj.Models;
using Obj.DocDetector;
using Obj.Utils;
using Obj.TjpbDespachoExtractor.Utils;
using iText.Kernel.Pdf;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.Align
{
    /// <summary>
    /// Compara operadores de texto (Tj/TJ/Td/Tf/Tm/BT/ET) entre varios PDFs para um objeto especifico.
    /// Mostra linhas fixas (iguais) e variaveis (mudam).
    /// Uso:
    ///   operpdf inspect textopsvar --inputs a.pdf,b.pdf --obj 6
    ///   operpdf inspect textopsfixed --inputs a.pdf,b.pdf --obj 6
    ///   operpdf inspect textopsdiff --inputs a.pdf,b.pdf --obj 6
    /// </summary>
    internal static partial class ObjectsTextOpsDiff
    {
        private static readonly object ConsoleLock = new();
        private sealed class InputStats
        {
            public int Requested { get; set; }
            public int Valid { get; set; }
            public int Invalid { get; set; }
            public int MissingStream { get; set; }
            public List<string> InvalidFiles { get; set; } = new();
            public List<string> MissingFiles { get; set; } = new();
        }
        internal enum DiffMode
        {
            Fixed,
            Variations,
            VarFixed,
            Both,
            Align
        }

        internal enum TokenMode
        {
            Text,
            Ops
        }

        public static void Execute(string[] args, DiffMode mode)
        {
            ReturnCapture? capture = null;
            if (ReturnUtils.IsEnabled())
                capture = new ReturnCapture();

            var inputs = new List<string>();
            int objId = 0;
            var opFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool blocks = false;
            bool blocksInline = false;
            string blocksOrder = "block-first";
            (int? Start, int? End) blockRange = default;
            bool blocksSpecified = false;
            bool selfMode = false;
            int selfMinTokenLen = 0;
            int selfPatternMax = 0;
            int minTokenLenFilter = 0;
            int minBlockLenFilter = 0;
            bool minBlockLenSpecified = false;
            int anchorsMinLen = 0;
            int anchorsMaxLen = 0;
            int anchorsMaxWords = 0;
            bool selfAnchors = false;
            string rulesPathArg = "";
            string rulesDoc = "";
            string anchorsOut = "";
            bool anchorsMerge = false;
            bool plainOutput = false;
            bool blockTokens = false;
            bool blockTokensSpecified = false;
            bool wordTokens = false;
            TokenMode tokenMode = TokenMode.Text;
            bool diffLineMode = false;
            bool cleanupSemantic = false;
            bool cleanupLossless = false;
            bool cleanupEfficiency = false;
            bool cleanupSpecified = false;
            bool diffLineModeSpecified = false;
            bool diffFullText = false;
            bool includeLineBreaks = false;
            bool includeTdLineBreaks = false;
            string rangeStartRegex = "";
            string rangeEndRegex = "";
            int? rangeStartOp = null;
            int? rangeEndOp = null;
            bool dumpRangeText = false;
            bool includeTmLineBreaks = false;
            bool lineBreakAsSpace = false;
            bool lineBreaksSpecified = false;
            bool useLargestContents = false;
            int contentsPage = 0;
            int pairCount = 0;
            string runMode = "";
            bool showText = false;
            bool showAlign = false;
            bool showDetails = false;
            int maxShowChars = 0;
            var stats = new InputStats();

            List<Dictionary<string, object?>>? variationsOut = null;
            List<Dictionary<string, object?>>? fixedOut = null;
            List<Dictionary<string, object?>>? blocksOut = null;
            string varText = "";
            string fixedText = "";
            string? alignA = null;
            string? alignB = null;

            try
            {
                if (!ParseOptions(
                    args,
                    out inputs,
                    out objId,
                    out opFilter,
                    out blocks,
                    out blocksInline,
                    out blocksOrder,
                    out blockRange,
                    out blocksSpecified,
                    out selfMode,
                    out selfMinTokenLen,
                    out selfPatternMax,
                    out minTokenLenFilter,
                    out minBlockLenFilter,
                    out minBlockLenSpecified,
                    out anchorsMinLen,
                    out anchorsMaxLen,
                    out anchorsMaxWords,
                    out selfAnchors,
                    out rulesPathArg,
                    out rulesDoc,
                    out anchorsOut,
                    out anchorsMerge,
                    out plainOutput,
                    out blockTokens,
                    out blockTokensSpecified,
                    out wordTokens,
                    out tokenMode,
                    out diffLineMode,
                    out cleanupSemantic,
                    out cleanupLossless,
                    out cleanupEfficiency,
                    out cleanupSpecified,
                    out diffLineModeSpecified,
                    out diffFullText,
                    out includeLineBreaks,
                    out includeTdLineBreaks,
                    out rangeStartRegex,
                    out rangeEndRegex,
                    out rangeStartOp,
                    out rangeEndOp,
                    out dumpRangeText,
                    out includeTmLineBreaks,
                    out lineBreakAsSpace,
                    out lineBreaksSpecified,
                    out useLargestContents,
                    out contentsPage,
                    out pairCount,
                    out runMode,
                    out showText,
                    out showAlign,
                    out showDetails,
                    out maxShowChars))
                    return;

            var rulesPath = ResolveRulesPath(rulesPathArg, rulesDoc);
            var rules = LoadRules(rulesPath);
            if ((!string.IsNullOrWhiteSpace(rulesPathArg) || !string.IsNullOrWhiteSpace(rulesDoc))
                && (string.IsNullOrWhiteSpace(rulesPath) || rules == null))
            {
                Console.WriteLine("Regras de textops nao encontradas ou invalidas.");
            }

            stats.Requested = inputs.Count;

            if (!selfMode && inputs.Count == 1 && mode != DiffMode.Both)
                selfMode = true;

            if (selfMode)
            {
                if (inputs.Count < 1)
                {
                    Console.WriteLine("Informe ao menos um PDF com --input/--inputs ao usar --self.");
                    return;
                }
            }
            else if (inputs.Count < 2)
            {
                Console.WriteLine("Informe ao menos dois PDFs com --inputs ou --input-dir.");
                return;
            }

            if (objId <= 0 && !useLargestContents)
            {
                // fallback automatico: maior stream da pagina detectada
                useLargestContents = true;
                contentsPage = 0;
            }

            if (!selfMode && (mode == DiffMode.Variations || mode == DiffMode.VarFixed) && !blocksSpecified)
            {
                blocks = true;
                blocksInline = true;
                blocksOrder = "block-first";
                if (!plainOutput)
                    plainOutput = true;
            }
            if (!selfMode && (mode == DiffMode.Variations || mode == DiffMode.VarFixed) && !blockTokensSpecified)
                blockTokens = true;

            if (!selfMode && mode == DiffMode.Both)
            {
                diffFullText = true;
                dumpRangeText = true;
                if (!cleanupSpecified)
                {
                    cleanupSemantic = true;
                    cleanupLossless = true;
                    cleanupEfficiency = true;
                }
                if (!diffLineModeSpecified)
                    diffLineMode = true;
                if (!lineBreaksSpecified)
                {
                    includeLineBreaks = true;
                    includeTdLineBreaks = true;
                    includeTmLineBreaks = true;
                    lineBreakAsSpace = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(runMode))
            {
                if (string.Equals(runMode, "triage", StringComparison.OrdinalIgnoreCase))
                {
                    blocks = false;
                    blocksSpecified = true;
                }
                else if (string.Equals(runMode, "enhanced", StringComparison.OrdinalIgnoreCase))
                {
                    blocks = true;
                    blocksSpecified = true;
                    if (!blocksInline)
                        blocksInline = true;
                    blocksOrder = "block-first";
                    if (!plainOutput)
                        plainOutput = true;
                }
            }

            if (selfMode)
            {
                var results = new List<SelfResult>();
                foreach (var path in inputs)
                {
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"Arquivo nao encontrado: {path}");
                        stats.Invalid++;
                        stats.InvalidFiles.Add(path);
                        continue;
                    }

                    using var doc = new PdfDocument(new PdfReader(path));
                    int pageForFile = contentsPage;
                    if (useLargestContents && pageForFile <= 0)
                        pageForFile = ResolveDocPage(path);
                    if (pageForFile <= 0)
                        pageForFile = 1;
                    var found = useLargestContents
                        ? FindLargestStreamOnPage(doc, pageForFile)
                        : FindStreamAndResourcesByObjId(doc, objId);
                    var stream = found.Stream;
                    var resources = found.Resources;
                    if (stream == null || resources == null)
                    {
                        Console.WriteLine(useLargestContents
                            ? $"Contents nao encontrado na pagina {pageForFile}: {path}"
                            : $"Objeto {objId} nao encontrado em: {path}");
                        stats.MissingStream++;
                        stats.MissingFiles.Add(path);
                        continue;
                    }

                    var blocksSelf = ExtractSelfBlocks(stream, resources, opFilter);
                    var classified = ClassifySelfBlocks(blocksSelf, selfMinTokenLen, selfPatternMax, rules);
                    if (mode == DiffMode.Fixed)
                        results.Add(new SelfResult(path, FilterSelfBlocks(classified.Fixed, minTokenLenFilter)));
                    else
                        results.Add(new SelfResult(path, FilterSelfBlocks(classified.Variable, minTokenLenFilter)));
                }

                stats.Valid = results.Count;
                var selfLabel = mode == DiffMode.Fixed ? "FIXOS" : "VARIAVEIS";
                if (results.Count == 0)
                {
                    Console.WriteLine("Nenhum PDF valido para processar (--self).");
                    return;
                }
                PrintSelfSummary(results, selfLabel);

                if (selfAnchors)
                {
                    var merged = new Dictionary<string, TextOpsAnchorConcept>(StringComparer.Ordinal);
                    var sources = new List<string>();

                    for (int i = 0; i < inputs.Count; i++)
                    {
                        var path = inputs[i];
                        int pageForFile = contentsPage;
                        if (useLargestContents && pageForFile <= 0)
                            pageForFile = ResolveDocPage(path);
                        if (pageForFile <= 0)
                            pageForFile = 1;
                        var blocksSelf = useLargestContents
                            ? ExtractSelfBlocksForPathByPage(path, pageForFile, opFilter)
                            : ExtractSelfBlocksForPath(path, objId, opFilter);
                        var classified = ClassifySelfBlocks(blocksSelf, selfMinTokenLen, selfPatternMax, rules);
                        var variableBlocks = FilterSelfBlocks(classified.Variable, minTokenLenFilter);
                        variableBlocks = FilterAnchorBlocks(variableBlocks, anchorsMinLen, anchorsMaxLen, anchorsMaxWords);
                        var anchors = BuildSelfAnchors(blocksSelf, variableBlocks, classified.Fixed);

                        if (anchorsMerge)
                        {
                            sources.Add(Path.GetFileName(path));
                            MergeAnchors(merged, anchors, path);
                        }
                        else
                        {
                            PrintSelfAnchors(path, anchors);
                            if (!string.IsNullOrWhiteSpace(anchorsOut))
                            {
                                var outPath = BuildAnchorsOutPath(anchorsOut, path, objId, i + 1, inputs.Count);
                                WriteAnchorsFile(outPath, rulesDoc, path, objId, anchors);
                            }
                        }
                    }

                    if (anchorsMerge)
                    {
                        var list = merged.Values
                            .OrderByDescending(a => a.Count)
                            .ThenBy(a => a.Prev ?? "", StringComparer.OrdinalIgnoreCase)
                            .ThenBy(a => a.Next ?? "", StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        PrintMergedAnchors(list);
                        if (!string.IsNullOrWhiteSpace(anchorsOut))
                        {
                            var outPath = BuildAnchorsMergeOutPath(anchorsOut, rulesDoc, objId);
                            WriteMergedAnchorsFile(outPath, rulesDoc, objId, sources, list);
                        }
                    }
                    return;
                }

                for (int i = 0; i < results.Count; i++)
                {
                    var result = results[i];
                    PrintSelfVariableBlocks(result, i + 1, blocksInline, blocksOrder, blockRange, selfLabel);
                }
                return;
            }

            if (mode == DiffMode.Align)
            {
                useLargestContents = true;
                var valid = new List<string>();
                foreach (var path in inputs)
                {
                    if (File.Exists(path))
                        valid.Add(path);
                    else
                        Console.WriteLine($"Arquivo nao encontrado: {path}");
                }

                if (valid.Count < 2)
                {
                    Console.WriteLine("Informe ao menos dois PDFs validos para alinhar.");
                    return;
                }

                var aPath = valid[0];
                var bPath = valid[1];
                AlignBlocks(aPath, bPath, objId, opFilter, useLargestContents, contentsPage);
                return;
            }

            var all = new List<List<string>>();
            var tokenLists = new List<List<string>>();
            var tokenOpStartLists = new List<List<int>>();
            var tokenOpEndLists = new List<List<int>>();
            var tokenOpNames = new List<List<string>>();
            var fullResults = new List<FullTextOpsResult>();
            var usedInputs = new List<string>();
                foreach (var path in inputs)
                {
                    if (!File.Exists(path))
                    {
                        Console.WriteLine($"Arquivo nao encontrado: {path}");
                        stats.Invalid++;
                        stats.InvalidFiles.Add(path);
                        continue;
                    }
                using var doc = new PdfDocument(new PdfReader(path));
                int pageForFile = contentsPage;
                if (useLargestContents && pageForFile <= 0)
                    pageForFile = ResolveDocPage(path);
                if (pageForFile <= 0)
                    pageForFile = 1;
                var found = useLargestContents
                    ? FindLargestStreamOnPage(doc, pageForFile)
                    : FindStreamAndResourcesByObjId(doc, objId);
                var stream = found.Stream;
                var resources = found.Resources;
                    if (stream == null || resources == null)
                    {
                        Console.WriteLine(useLargestContents
                            ? $"Contents nao encontrado na pagina {pageForFile}: {path}"
                            : $"Objeto {objId} nao encontrado em: {path}");
                        stats.MissingStream++;
                        stats.MissingFiles.Add(path);
                        continue;
                    }
                    usedInputs.Add(path);
                if (!diffFullText)
                {
                    all.Add(ExtractTextOperatorLines(stream, resources, opFilter, tokenMode));
                    if (blocks && (mode == DiffMode.Variations || mode == DiffMode.Both))
                    {
                        var tokensWithOps = blockTokens
                            ? ExtractTextOperatorBlockTokensWithOps(stream, resources, opFilter, wordTokens)
                            : ExtractTextOperatorTokensWithOps(stream, resources, opFilter, tokenMode, wordTokens);
                        tokenLists.Add(tokensWithOps.Tokens);
                        tokenOpStartLists.Add(tokensWithOps.OpStarts);
                        tokenOpEndLists.Add(tokensWithOps.OpEnds);
                        tokenOpNames.Add(tokensWithOps.OpNames);
                    }
                }
                else
                {
                    var fullText = ExtractFullTextWithOps(stream, resources, opFilter, includeLineBreaks, includeTdLineBreaks, includeTmLineBreaks, lineBreakAsSpace);
                    fullResults.Add(fullText);
                }
            }
            inputs = usedInputs;
            stats.Valid = inputs.Count;
            if (inputs.Count < 2 && !selfMode && mode != DiffMode.Both)
            {
                Console.WriteLine("Informe ao menos dois PDFs validos para comparar.");
                return;
            }
            if (inputs.Count == 0)
            {
                Console.WriteLine("Nenhum PDF valido para processar.");
                return;
            }

            if (diffFullText)
            {
                var roiDoc = ResolveRoiDoc(rulesDoc, objId);
                if (mode == DiffMode.Variations || mode == DiffMode.Both)
                {
                    var report = BuildFullTextReport(inputs, fullResults, diffLineMode, cleanupSemantic, cleanupLossless, cleanupEfficiency, mode == DiffMode.Both, dumpRangeText);
                    if (!ReturnUtils.IsEnabled())
                    {
                        // mantem a saida humana atual
                        PrintFullTextDiffWithRange(inputs, fullResults, diffLineMode, cleanupSemantic, cleanupLossless, cleanupEfficiency, rangeStartRegex, rangeEndRegex, rangeStartOp, rangeEndOp, dumpRangeText, roiDoc, objId, mode == DiffMode.Both);
                    }
                    else
                    {
                        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        });
                        Console.WriteLine(json);
                    }
                }
                return;
            }

            var maxLen = all.Max(l => l.Count);
            var fixedLines = new List<(int idx, string line)>();
            var varLines = new List<(int idx, List<string> lines)>();

            for (int i = 0; i < maxLen; i++)
            {
                var col = new List<string>();
                foreach (var list in all)
                {
                    col.Add(i < list.Count ? list[i] : "(missing)");
                }

                bool allSame = col.All(c => c == col[0]);
                bool hasMissing = col.Any(c => c == "(missing)");

                if (allSame && !hasMissing)
                    fixedLines.Add((i + 1, col[0]));
                else
                    varLines.Add((i + 1, col));
            }

            if (mode == DiffMode.Variations || mode == DiffMode.Both || mode == DiffMode.VarFixed)
            {
                if (blocks)
                {
                    blocksOut = new List<Dictionary<string, object?>>();
                    PrintVariationBlocks(inputs, tokenLists, tokenOpStartLists, tokenOpEndLists, tokenOpNames, blocksInline, blocksOrder, blockRange, minTokenLenFilter, minBlockLenFilter, plainOutput, blockTokens, tokenMode, diffLineMode, cleanupSemantic, cleanupLossless, cleanupEfficiency, blocksOut, stats);
                }
                else
                {
                    variationsOut = new List<Dictionary<string, object?>>();
                    PrintVariations(inputs, varLines, minTokenLenFilter, tokenMode, variationsOut, stats);
                }
            }
            if (mode == DiffMode.Fixed || mode == DiffMode.Both || mode == DiffMode.VarFixed)
            {
                fixedOut = new List<Dictionary<string, object?>>();
                PrintFixed(fixedLines, minTokenLenFilter, tokenMode, fixedOut, stats);
            }

            if (mode == DiffMode.Variations || mode == DiffMode.Both || mode == DiffMode.VarFixed)
            {
                if (blocks && blocksOut != null)
                    varText = BuildVarTextFromBlocks(blocksOut, inputs, 0);
                else
                    varText = BuildVarTextFromLines(varLines, inputs, tokenMode, 0);

                if (showAlign && inputs.Count >= 2)
                {
                    if (blocks && blocksOut != null)
                    {
                        alignA = BuildVarTextFromBlocks(blocksOut, inputs, 0);
                        alignB = BuildVarTextFromBlocks(blocksOut, inputs, 1);
                    }
                    else
                    {
                        alignA = BuildVarTextFromLines(varLines, inputs, tokenMode, 0);
                        alignB = BuildVarTextFromLines(varLines, inputs, tokenMode, 1);
                    }
                }
            }
            if (mode == DiffMode.Fixed || mode == DiffMode.Both || mode == DiffMode.VarFixed)
                fixedText = BuildFixedText(fixedLines, tokenMode);

            if (!ReturnUtils.IsEnabled() && showText)
            {
                if (!string.IsNullOrWhiteSpace(varText))
                {
                    Console.WriteLine("VAR TEXT");
                    Console.WriteLine(varText);
                    Console.WriteLine();
                }
                if (!string.IsNullOrWhiteSpace(fixedText))
                {
                    Console.WriteLine("FIXED TEXT");
                    Console.WriteLine(fixedText);
                    Console.WriteLine();
                }
            }
            if (!ReturnUtils.IsEnabled() && showAlign && alignA != null && alignB != null)
            {
                Console.WriteLine("ALIGN TEXT");
                Console.WriteLine($"A: {alignA}");
                Console.WriteLine($"B: {alignB}");
                Console.WriteLine();
            }
            }
            finally
            {
                if (capture != null)
                {
                    var text = capture.GetText();
                    capture.Dispose();
            var payload = new Dictionary<string, object?>
            {
                ["cmd"] = "textops",
                ["mode"] = mode.ToString().ToLowerInvariant(),
                ["run_mode"] = blocks ? "enhanced" : "triage",
                ["inputs"] = inputs,
                ["obj"] = objId,
                ["use_contents"] = useLargestContents,
                ["page"] = contentsPage > 0 ? contentsPage : (int?)null,
                ["op"] = opFilter.ToList(),
                ["text"] = text,
                ["stats"] = new Dictionary<string, object?>
                {
                    ["requested"] = stats.Requested,
                    ["valid"] = stats.Valid,
                    ["invalid"] = stats.Invalid,
                    ["missing_stream"] = stats.MissingStream,
                    ["invalid_files"] = stats.InvalidFiles,
                    ["missing_files"] = stats.MissingFiles
                }
            };
            if (blocksOut != null && blocksOut.Count > 0)
                payload["blocks"] = blocksOut;
            if (variationsOut != null && variationsOut.Count > 0)
                payload["variations"] = variationsOut;
            if (fixedOut != null && fixedOut.Count > 0)
                payload["fixed"] = fixedOut;
            if (!string.IsNullOrWhiteSpace(varText))
                payload["var_text"] = varText;
            if (!string.IsNullOrWhiteSpace(fixedText))
                payload["fixed_text"] = fixedText;
            if (showAlign && alignA != null && alignB != null)
            {
                payload["align_text"] = new Dictionary<string, object?>
                {
                    ["a"] = alignA,
                    ["b"] = alignB
                };
            }
                    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
                    Console.WriteLine(json);
                }
            }
        }

        private static void PrintVariations(
            List<string> inputs,
            List<(int idx, List<string> lines)> varLines,
            int minTokenLenFilter,
            TokenMode tokenMode,
            List<Dictionary<string, object?>>? collect,
            InputStats stats)
        {
            ReportUtils.WriteSummary(ReportUtils.BlueLabel("TEXTOPS VAR"), new List<(string Key, string Value)>
            {
                ("inputs", inputs.Count.ToString(CultureInfo.InvariantCulture)),
                ("total", varLines.Count.ToString(CultureInfo.InvariantCulture)),
                ("mode", tokenMode == TokenMode.Text ? "text" : "ops"),
                ("min_len", minTokenLenFilter.ToString(CultureInfo.InvariantCulture))
            });
            ReportUtils.WriteSummary(ReportUtils.OrangeLabel("INPUTS"), new List<(string Key, string Value)>
            {
                ("requested", stats.Requested.ToString(CultureInfo.InvariantCulture)),
                ("valid", stats.Valid.ToString(CultureInfo.InvariantCulture)),
                ("invalid", stats.Invalid.ToString(CultureInfo.InvariantCulture)),
                ("missing_stream", stats.MissingStream.ToString(CultureInfo.InvariantCulture))
            });
            Console.WriteLine();

            for (int i = 0; i < varLines.Count; i++)
            {
                var (idx, lines) = varLines[i];
                if (minTokenLenFilter > 0)
                {
                    int maxLen = 0;
                    foreach (var line in lines)
                    {
                        if (tokenMode == TokenMode.Text)
                        {
                            var raw = StripDescription(line);
                            var text = ExtractDecodedTextFromLine(raw);
                            if (!string.IsNullOrWhiteSpace(text))
                                maxLen = Math.Max(maxLen, text.Length);
                        }
                        else
                        {
                            var raw = StripDescription(line);
                            var text = ExtractDecodedTextFromLine(raw);
                            if (!string.IsNullOrWhiteSpace(text))
                                maxLen = Math.Max(maxLen, text.Length);
                        }
                    }
                    if (maxLen < minTokenLenFilter)
                        continue;
                }
                var items = new List<Dictionary<string, object?>>();
                var rows = new List<string[]>();
                for (int j = 0; j < inputs.Count; j++)
                {
                    var line = lines[j];
                    var raw = StripDescription(line);
                    var text = ExtractDecodedTextFromLine(raw);
                    var len = string.IsNullOrWhiteSpace(text) ? 0 : text.Length;

                    if (tokenMode == TokenMode.Text)
                    {
                        if (line.Contains("=>"))
                        {
                            rows.Add(new[] { Path.GetFileName(inputs[j]), line, "", "" });
                        }
                        else if (!string.IsNullOrWhiteSpace(text))
                        {
                            rows.Add(new[] { Path.GetFileName(inputs[j]), line, text, len.ToString(CultureInfo.InvariantCulture) });
                        }
                        else
                        {
                            rows.Add(new[] { Path.GetFileName(inputs[j]), line, "", "" });
                        }
                    }
                    else
                    {
                        rows.Add(new[] { Path.GetFileName(inputs[j]), line, "", "" });
                    }

                    items.Add(new Dictionary<string, object?>
                    {
                        ["file"] = Path.GetFileName(inputs[j]),
                        ["line"] = line,
                        ["text"] = text ?? "",
                        ["len"] = len
                    });
                }

                ReportUtils.WriteTable($"VAR idx {idx}", new[] { "file", "line", "text", "len" }, rows);
                Console.WriteLine();

                collect?.Add(new Dictionary<string, object?>
                {
                    ["idx"] = idx,
                    ["items"] = items
                });
            }
        }

        private static void PrintFixed(
            List<(int idx, string line)> fixedLines,
            int minTokenLenFilter,
            TokenMode tokenMode,
            List<Dictionary<string, object?>>? collect,
            InputStats stats)
        {
            ReportUtils.WriteSummary(ReportUtils.BlueLabel("TEXTOPS FIXED"), new List<(string Key, string Value)>
            {
                ("total", fixedLines.Count.ToString(CultureInfo.InvariantCulture)),
                ("mode", tokenMode == TokenMode.Text ? "text" : "ops"),
                ("min_len", minTokenLenFilter.ToString(CultureInfo.InvariantCulture))
            });
            ReportUtils.WriteSummary(ReportUtils.OrangeLabel("INPUTS"), new List<(string Key, string Value)>
            {
                ("requested", stats.Requested.ToString(CultureInfo.InvariantCulture)),
                ("valid", stats.Valid.ToString(CultureInfo.InvariantCulture)),
                ("invalid", stats.Invalid.ToString(CultureInfo.InvariantCulture)),
                ("missing_stream", stats.MissingStream.ToString(CultureInfo.InvariantCulture))
            });
            Console.WriteLine();

            var rows = new List<string[]>();

            foreach (var (idx, line) in fixedLines)
            {
                if (tokenMode == TokenMode.Text)
                {
                    if (line.Contains("=>"))
                    {
                        rows.Add(new[] { idx.ToString(CultureInfo.InvariantCulture), line, "", "" });
                        collect?.Add(new Dictionary<string, object?>
                        {
                            ["idx"] = idx,
                            ["line"] = line,
                            ["text"] = "",
                            ["len"] = 0
                        });
                        continue;
                    }

                    var raw = StripDescription(line);
                    var text = ExtractDecodedTextFromLine(raw);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        if (minTokenLenFilter > 0 && text.Length < minTokenLenFilter)
                            continue;
                        rows.Add(new[] { idx.ToString(CultureInfo.InvariantCulture), line, text, text.Length.ToString(CultureInfo.InvariantCulture) });
                        collect?.Add(new Dictionary<string, object?>
                        {
                            ["idx"] = idx,
                            ["line"] = line,
                            ["text"] = text,
                            ["len"] = text.Length
                        });
                    }
                    else
                    {
                        rows.Add(new[] { idx.ToString(CultureInfo.InvariantCulture), line, "", "" });
                        collect?.Add(new Dictionary<string, object?>
                        {
                            ["idx"] = idx,
                            ["line"] = line,
                            ["text"] = "",
                            ["len"] = 0
                        });
                    }
                }
                else
                {
                    if (minTokenLenFilter > 0)
                    {
                        var raw = StripDescription(line);
                        var text = ExtractDecodedTextFromLine(raw);
                        if (!string.IsNullOrWhiteSpace(text) && text.Length < minTokenLenFilter)
                            continue;
                    }
                    rows.Add(new[] { idx.ToString(CultureInfo.InvariantCulture), line, "", "" });
                    collect?.Add(new Dictionary<string, object?>
                    {
                        ["idx"] = idx,
                        ["line"] = line,
                        ["text"] = "",
                        ["len"] = 0
                    });
                }
            }

            ReportUtils.WriteTable("FIXED", new[] { "idx", "line", "text", "len" }, rows);
        }

        private static string BuildVarTextFromLines(
            List<(int idx, List<string> lines)> varLines,
            List<string> inputs,
            TokenMode tokenMode,
            int fileIndex)
        {
            if (fileIndex < 0 || fileIndex >= inputs.Count)
                return "";

            var sb = new StringBuilder();
            foreach (var (_, lines) in varLines)
            {
                if (fileIndex >= lines.Count)
                    continue;
                var raw = StripDescription(lines[fileIndex]);
                var text = ExtractDecodedTextFromLine(raw);
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(text.Trim());
            }
            return sb.ToString();
        }

        private static string BuildFixedText(
            List<(int idx, string line)> fixedLines,
            TokenMode tokenMode)
        {
            var sb = new StringBuilder();
            foreach (var (_, line) in fixedLines)
            {
                var raw = StripDescription(line);
                var text = ExtractDecodedTextFromLine(raw);
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (sb.Length > 0)
                    sb.AppendLine();
                sb.Append(text.Trim());
            }
            return sb.ToString();
        }

        private static string BuildVarTextFromBlocks(
            List<Dictionary<string, object?>> blocksOut,
            List<string> inputs,
            int fileIndex)
        {
            if (fileIndex < 0 || fileIndex >= inputs.Count)
                return "";

            var name = Path.GetFileName(inputs[fileIndex]);
            var sb = new StringBuilder();
            foreach (var block in blocksOut)
            {
                if (!block.TryGetValue("items", out var itemsObj))
                    continue;
                if (itemsObj is not IEnumerable<object> items)
                    continue;
                foreach (var itemObj in items)
                {
                    if (itemObj is not Dictionary<string, object?> item)
                        continue;
                    if (!item.TryGetValue("file", out var fileObj) || fileObj is not string file)
                        continue;
                    if (!string.Equals(file, name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!item.TryGetValue("text", out var textObj) || textObj is not string text)
                        continue;
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    if (sb.Length > 0)
                        sb.AppendLine();
                    sb.Append(text.Trim());
                }
            }
            return sb.ToString();
        }

        private static void PrintVariationBlocks(
            List<string> inputs,
            List<List<string>> tokenLists,
            List<List<int>> tokenOpStartLists,
            List<List<int>> tokenOpEndLists,
            List<List<string>> tokenOpNames,
            bool inline,
            string order,
            (int? Start, int? End) range,
            int minTokenLenFilter,
            int minBlockLenFilter,
            bool plainOutput,
            bool blockTokens,
            TokenMode tokenMode,
            bool diffLineMode,
            bool cleanupSemantic,
            bool cleanupLossless,
            bool cleanupEfficiency,
            List<Dictionary<string, object?>>? collect,
            InputStats stats)
        {
            if (tokenLists.Count == 0)
            {
                ReportUtils.WriteSummary(ReportUtils.BlueLabel(
                    tokenMode == TokenMode.Text ? "TEXTOPS BLOCKS" : "TEXTOPS BLOCKS"),
                    new List<(string Key, string Value)>
                    {
                        ("inputs", inputs.Count.ToString(CultureInfo.InvariantCulture)),
                        ("blocks", "0"),
                        ("mode", tokenMode == TokenMode.Text ? "text" : "ops"),
                        ("inline", inline ? "yes" : "no"),
                        ("order", order),
                        ("min_len", minTokenLenFilter.ToString(CultureInfo.InvariantCulture)),
                        ("min_block_len", minBlockLenFilter.ToString(CultureInfo.InvariantCulture))
                    });
                ReportUtils.WriteSummary(ReportUtils.OrangeLabel("INPUTS"), new List<(string Key, string Value)>
                {
                    ("requested", stats.Requested.ToString(CultureInfo.InvariantCulture)),
                    ("valid", stats.Valid.ToString(CultureInfo.InvariantCulture)),
                    ("invalid", stats.Invalid.ToString(CultureInfo.InvariantCulture)),
                    ("missing_stream", stats.MissingStream.ToString(CultureInfo.InvariantCulture))
                });
                Console.WriteLine();
                return;
            }

            var baseTokens = tokenLists[0];
            var alignments = new List<TokenAlignment>();
            for (int i = 1; i < tokenLists.Count; i++)
                alignments.Add(BuildAlignment(baseTokens, tokenLists[i], diffLineMode, cleanupSemantic, cleanupLossless, cleanupEfficiency));

            var varToken = new bool[baseTokens.Count];
            var varGap = new bool[baseTokens.Count + 1];

            for (int i = 0; i < baseTokens.Count; i++)
            {
                foreach (var (alignment, tokens) in alignments.Select((a, idx) => (a, tokenLists[idx + 1])))
                {
                    var otherIdx = alignment.BaseToOther[i];
                    if (otherIdx < 0 || otherIdx >= tokens.Count)
                    {
                        varToken[i] = true;
                        break;
                    }
                    if (!string.Equals(tokens[otherIdx], baseTokens[i], StringComparison.Ordinal))
                    {
                        varToken[i] = true;
                        break;
                    }
                }
            }

            for (int gap = 0; gap < varGap.Length; gap++)
            {
                foreach (var alignment in alignments)
                {
                    if (alignment.Insertions[gap].Count > 0)
                    {
                        varGap[gap] = true;
                        break;
                    }
                }
            }

            var blocks = BuildVariableBlocks(varToken, varGap);
            var blockOpLabels = new List<string>();
            foreach (var block in blocks)
                blockOpLabels.Add(BuildBlockOpLabel(block, 0, tokenOpStartLists, tokenOpEndLists, tokenOpNames, alignments));

            ReportUtils.WriteSummary(ReportUtils.BlueLabel(
                tokenMode == TokenMode.Text ? "TEXTOPS BLOCKS" : "TEXTOPS BLOCKS"),
                new List<(string Key, string Value)>
                {
                    ("inputs", inputs.Count.ToString(CultureInfo.InvariantCulture)),
                    ("blocks", blocks.Count.ToString(CultureInfo.InvariantCulture)),
                    ("mode", tokenMode == TokenMode.Text ? "text" : "ops"),
                    ("inline", inline ? "yes" : "no"),
                    ("order", order),
                    ("min_len", minTokenLenFilter.ToString(CultureInfo.InvariantCulture)),
                    ("min_block_len", minBlockLenFilter.ToString(CultureInfo.InvariantCulture))
                });
            ReportUtils.WriteSummary(ReportUtils.OrangeLabel("INPUTS"), new List<(string Key, string Value)>
            {
                ("requested", stats.Requested.ToString(CultureInfo.InvariantCulture)),
                ("valid", stats.Valid.ToString(CultureInfo.InvariantCulture)),
                ("invalid", stats.Invalid.ToString(CultureInfo.InvariantCulture)),
                ("missing_stream", stats.MissingStream.ToString(CultureInfo.InvariantCulture))
            });
            Console.WriteLine();

            var blockRows = new List<string[]>();
            for (int idx = 0; idx < blocks.Count; idx++)
            {
                var maxLen = GetBlockMaxTokenLen(blocks[idx], baseTokens, tokenLists, alignments);
                var opLabel = BuildBlockOpLabel(blocks[idx], 0, tokenOpStartLists, tokenOpEndLists, tokenOpNames, alignments);
                blockRows.Add(new[]
                {
                    (idx + 1).ToString(CultureInfo.InvariantCulture),
                    maxLen.ToString(CultureInfo.InvariantCulture),
                    opLabel
                });
            }
            if (blockRows.Count > 0)
            {
                ReportUtils.WriteTable("BLOCKS (top 20 by order)", new[] { "block", "max_len", "op" }, blockRows, 20);
                Console.WriteLine();
            }

            if (range.Start.HasValue || range.End.HasValue)
            {
                var startIdx = range.Start ?? 1;
                var endIdx = range.End ?? startIdx;
                if (startIdx < 1) startIdx = 1;
                if (endIdx < startIdx) endIdx = startIdx;
                if (startIdx > blocks.Count)
                    return;
                if (endIdx > blocks.Count)
                    endIdx = blocks.Count;

                var startBlock = blocks[startIdx - 1];
                var endBlock = blocks[endIdx - 1];
                var merged = new VarBlockSlots(startBlock.StartSlot, endBlock.EndSlot);
                var label = FormatBlockLabel(startIdx, endIdx);
                WriteInlineBlock(label, merged, inputs, baseTokens, tokenLists, tokenOpStartLists, tokenOpEndLists, tokenOpNames, alignments, order, plainOutput, blockTokens);

                if (collect != null)
                {
                    var items = new List<Dictionary<string, object?>>();
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        var text = BuildBlockText(merged, i, baseTokens, tokenLists, alignments, blockTokens);
                        var opLabel = BuildBlockOpLabel(merged, i, tokenOpStartLists, tokenOpEndLists, tokenOpNames, alignments);
                        items.Add(new Dictionary<string, object?>
                        {
                            ["file"] = Path.GetFileName(inputs[i]),
                            ["op"] = opLabel,
                            ["text"] = text,
                            ["len"] = text.Length
                        });
                    }
                    collect.Add(new Dictionary<string, object?>
                    {
                        ["block"] = label,
                        ["items"] = items
                    });
                }
                return;
            }

            if (inline)
            {
                for (int idx = 0; idx < blocks.Count; idx++)
                {
                    if (minTokenLenFilter > 0)
                    {
                        var maxLen = GetBlockMaxTokenLen(blocks[idx], baseTokens, tokenLists, alignments);
                        if (maxLen < minTokenLenFilter)
                            continue;
                    }
                    if (minBlockLenFilter > 0)
                    {
                        var maxBlockLen = 0;
                        for (int i = 0; i < inputs.Count; i++)
                        {
                            var textLen = BuildBlockText(blocks[idx], i, baseTokens, tokenLists, alignments, blockTokens).Length;
                            if (textLen > maxBlockLen)
                                maxBlockLen = textLen;
                        }
                        if (maxBlockLen < minBlockLenFilter)
                            continue;
                    }
                    var label = FormatBlockLabel(idx + 1, idx + 1);
                    WriteInlineBlock(label, blocks[idx], inputs, baseTokens, tokenLists, tokenOpStartLists, tokenOpEndLists, tokenOpNames, alignments, order, plainOutput, blockTokens);

                    if (collect != null)
                    {
                        var items = new List<Dictionary<string, object?>>();
                        for (int i = 0; i < inputs.Count; i++)
                        {
                            var text = BuildBlockText(blocks[idx], i, baseTokens, tokenLists, alignments, blockTokens);
                            var opLabel = BuildBlockOpLabel(blocks[idx], i, tokenOpStartLists, tokenOpEndLists, tokenOpNames, alignments);
                            items.Add(new Dictionary<string, object?>
                            {
                                ["file"] = Path.GetFileName(inputs[i]),
                                ["op"] = opLabel,
                                ["text"] = text,
                                ["len"] = text.Length
                            });
                        }
                        collect.Add(new Dictionary<string, object?>
                        {
                            ["block"] = label,
                            ["items"] = items
                        });
                    }
                }
                return;
            }

            int n = 1;
            foreach (var block in blocks)
            {
                if (minTokenLenFilter > 0)
                {
                    var maxLen = GetBlockMaxTokenLen(block, baseTokens, tokenLists, alignments);
                    if (maxLen < minTokenLenFilter)
                        continue;
                }
                if (minBlockLenFilter > 0)
                {
                    var maxBlockLen = 0;
                    for (int i = 0; i < inputs.Count; i++)
                    {
                        var textLen = BuildBlockText(block, i, baseTokens, tokenLists, alignments, blockTokens).Length;
                        if (textLen > maxBlockLen)
                            maxBlockLen = textLen;
                    }
                    if (maxBlockLen < minBlockLenFilter)
                        continue;
                }
                var blockItems = new List<Dictionary<string, object?>>();
                for (int i = 0; i < inputs.Count; i++)
                {
                    var name = Path.GetFileName(inputs[i]);
                    var text = BuildBlockText(block, i, baseTokens, tokenLists, alignments, blockTokens);
                    if (text.Length == 0)
                        continue;

                    var opLabel = BuildBlockOpLabel(block, i, tokenOpStartLists, tokenOpEndLists, tokenOpNames, alignments);
                    var display = EscapeBlockText(text);
                    if (plainOutput)
                    {
                        WritePlainLine(name, opLabel, display, text.Length);
                    }
                    else
                    {
                        if (i == 0)
                            Console.WriteLine($"[block {n}]");
                        Console.WriteLine($"  {name} {opLabel}: \"{display}\" (len={text.Length})");
                    }

                    blockItems.Add(new Dictionary<string, object?>
                    {
                        ["file"] = name,
                        ["op"] = opLabel,
                        ["text"] = text,
                        ["len"] = text.Length
                    });
                }
                if (!plainOutput)
                    Console.WriteLine();

                if (collect != null)
                {
                    collect.Add(new Dictionary<string, object?>
                    {
                        ["block"] = FormatBlockLabel(n, n),
                        ["items"] = blockItems
                    });
                }
                n++;
            }
        }

        private static FullTextDiffReport BuildFullTextReport(
            List<string> inputs,
            List<FullTextOpsResult> fullResults,
            bool diffLineMode,
            bool cleanupSemantic,
            bool cleanupLossless,
            bool cleanupEfficiency,
            bool showEqual,
            bool dumpRangeText)
        {
            var report = new FullTextDiffReport
            {
                Range = "",
                Roi = ""
            };

            var ranges = new List<(string Name, int Start, int End)>();
            for (int i = 0; i < inputs.Count; i++)
                ranges.Add((Path.GetFileName(inputs[i]), 0, fullResults[i].OpIndexes.Count));

            if (ranges.Count > 0)
                report.Range = $"op0-op{ranges.Max(r => r.End)}";

            foreach (var r in ranges)
                report.Ranges.Add((r.Name, $"op{r.Start}-op{r.End}"));

            if (dumpRangeText)
            {
                for (int i = 0; i < inputs.Count; i++)
                    report.RangeTexts.Add((Path.GetFileName(inputs[i]), fullResults[i].Text, fullResults[i].Text.Length));
            }

            var baseName = Path.GetFileName(inputs[0]);
            var baseText = fullResults[0].Text;
            var baseOps = fullResults[0].OpIndexes;
            var baseOpNames = fullResults[0].OpNames;

            for (int i = 1; i < inputs.Count; i++)
            {
                var otherName = Path.GetFileName(inputs[i]);
                var otherText = fullResults[i].Text;
                var otherOps = fullResults[i].OpIndexes;
                var otherOpNames = fullResults[i].OpNames;
                var pair = new FullTextDiffPair { A = baseName, B = otherName };
                report.Pairs.Add(pair);

                var dmp = new diff_match_patch();
                var diffs = dmp.diff_main(baseText, otherText, diffLineMode);
                if (cleanupSemantic) dmp.diff_cleanupSemantic(diffs);
                if (cleanupLossless) dmp.diff_cleanupSemanticLossless(diffs);
                if (cleanupEfficiency) dmp.diff_cleanupEfficiency(diffs);

                int basePos = 0;
                int otherPos = 0;

                foreach (var diff in diffs)
                {
                    var len = diff.text.Length;
                    if (diff.operation == Operation.EQUAL)
                    {
                        if (showEqual && len > 0)
                        {
                            var label = BuildOpRangeLabel(baseOps, baseOpNames, basePos, len);
                            pair.Items.Add(new FullTextDiffItem
                            {
                                Kind = "EQ",
                                File = baseName,
                                OpRange = label,
                                Text = diff.text,
                                Len = len
                            });
                        }
                        basePos += len;
                        otherPos += len;
                        continue;
                    }

                    if (diff.operation == Operation.DELETE)
                    {
                        var label = BuildOpRangeLabel(baseOps, baseOpNames, basePos, len);
                        pair.Items.Add(new FullTextDiffItem
                        {
                            Kind = "DEL",
                            File = baseName,
                            OpRange = label,
                            Text = diff.text,
                            Len = len
                        });
                        basePos += len;
                        continue;
                    }

                    if (diff.operation == Operation.INSERT)
                    {
                        var label = BuildOpRangeLabel(otherOps, otherOpNames, otherPos, len);
                        pair.Items.Add(new FullTextDiffItem
                        {
                            Kind = "INS",
                            File = otherName,
                            OpRange = label,
                            Text = diff.text,
                            Len = len
                        });
                        otherPos += len;
                        continue;
                    }
                }
            }

            return report;
        }

        private static void WriteInlineBlock(string label, VarBlockSlots block, List<string> inputs, List<string> baseTokens, List<List<string>> tokenLists, List<List<int>> tokenOpStartLists, List<List<int>> tokenOpEndLists, List<List<string>> tokenOpNames, List<TokenAlignment> alignments, string order, bool plainOutput, bool blockTokens)
        {
            bool blockFirst = IsBlockFirst(order);
            for (int i = 0; i < inputs.Count; i++)
            {
                var name = Path.GetFileName(inputs[i]);
                var text = BuildBlockText(block, i, baseTokens, tokenLists, alignments, blockTokens);
                if (text.Length == 0)
                    continue;

                var opLabel = BuildBlockOpLabel(block, i, tokenOpStartLists, tokenOpEndLists, tokenOpNames, alignments);
                var labelOut = string.IsNullOrWhiteSpace(opLabel) ? label : opLabel;
                var display = EscapeBlockText(text);
                if (plainOutput)
                {
                    WritePlainLine(name, opLabel, display, text.Length);
                    continue;
                }
                if (blockFirst)
                    Console.WriteLine($"{labelOut}\t{name}\t\"{display}\" (len={text.Length})");
                else
                    Console.WriteLine($"\"{display}\" (len={text.Length})\t{labelOut}\t{name}");
            }
        }

        private static bool IsBlockFirst(string? order)
        {
            if (string.IsNullOrWhiteSpace(order)) return true;
            order = order.Trim().ToLowerInvariant();
            return order == "block-first" || order == "block-text" || order == "block" || order == "b";
        }

        private static string FormatBlockLabel(int startIdx, int endIdx)
        {
            if (startIdx == endIdx)
                return $"b{startIdx}";
            return $"b{startIdx}-{endIdx}";
        }

        private static void WritePlainLine(string name, string opLabel, string display, int length)
        {
            if (string.IsNullOrWhiteSpace(opLabel))
                Console.WriteLine($"{name}\t\"{display}\" (len={length})");
            else
                Console.WriteLine($"{name}\t{opLabel}\t\"{display}\" (len={length})");
        }

        private static string FormatSelfRangeLabel(int startIdx, int endIdx, int startOp, int endOp, string? opsLabel)
        {
            var opRange = startOp == endOp ? $"{startOp}" : $"{startOp}-{endOp}";
            var label = $"op{opRange}";
            if (!string.IsNullOrWhiteSpace(opsLabel))
                label += $"[{opsLabel}]";
            return label;
        }

        private static string FormatSelfBlockLabel(SelfBlock block)
        {
            return FormatSelfRangeLabel(block.Index, block.Index, block.StartOp, block.EndOp, block.OpsLabel);
        }

        private static string BuildBlockText(VarBlockSlots block, int pdfIndex, List<string> baseTokens, List<List<string>> tokenLists, List<TokenAlignment> alignments, bool blockTokens)
        {
            var sb = new StringBuilder();
            var maxSlot = block.EndSlot;
            for (int slot = block.StartSlot; slot <= maxSlot; slot++)
            {
                if (slot % 2 == 0)
                {
                    int gap = slot / 2;
                    if (pdfIndex == 0)
                        continue;
                    var alignment = alignments[pdfIndex - 1];
                    var otherTokens = tokenLists[pdfIndex];
                    foreach (var tokenIdx in alignment.Insertions[gap])
                    {
                        if (tokenIdx >= 0 && tokenIdx < otherTokens.Count)
                            AppendBlockToken(sb, otherTokens[tokenIdx], blockTokens);
                    }
                }
                else
                {
                    int tokenIdx = (slot - 1) / 2;
                    if (tokenIdx < 0 || tokenIdx >= baseTokens.Count)
                        continue;
                    if (pdfIndex == 0)
                    {
                        AppendBlockToken(sb, baseTokens[tokenIdx], blockTokens);
                    }
                    else
                    {
                        var alignment = alignments[pdfIndex - 1];
                        var otherTokens = tokenLists[pdfIndex];
                        var otherIdx = alignment.BaseToOther[tokenIdx];
                        if (otherIdx >= 0 && otherIdx < otherTokens.Count)
                            AppendBlockToken(sb, otherTokens[otherIdx], blockTokens);
                    }
                }
            }

            return sb.ToString();
        }

        private static void AppendBlockToken(StringBuilder sb, string token, bool blockTokens)
        {
            if (string.IsNullOrEmpty(token))
                return;

            if (blockTokens)
            {
                token = NormalizeBlockToken(token);
                if (token.Length == 0)
                    return;
                if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]) && !char.IsWhiteSpace(token[0]))
                    sb.Append(' ');
            }

            sb.Append(token);
        }

        private static string NormalizeBlockToken(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            return TextNormalization.NormalizeFullText(text);
        }

        private static string CollapseSpaces(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            var sb = new StringBuilder(text.Length);
            bool inSpace = false;
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!inSpace && sb.Length > 0)
                        sb.Append(' ');
                    inSpace = true;
                    continue;
                }
                sb.Append(c);
                inSpace = false;
            }
            return sb.ToString().Trim();
        }

        private static (PdfStream? Stream, PdfResources? Resources) FindStreamAndResourcesByObjId(PdfDocument doc, int objId)
        {
            // Prefer content streams with page resources (ToUnicode aware)
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources();
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                foreach (var s in EnumerateStreams(contents))
                {
                    int id = s.GetIndirectReference()?.GetObjNumber() ?? 0;
                    if (id == objId)
                        return (s, resources);
                }

                var xobjects = resources?.GetResource(PdfName.XObject) as PdfDictionary;
                if (xobjects != null)
                {
                    foreach (var name in xobjects.KeySet())
                    {
                        var xs = xobjects.GetAsStream(name);
                        if (xs == null) continue;
                        int id = xs.GetIndirectReference()?.GetObjNumber() ?? 0;
                        if (id == objId)
                        {
                            var xresDict = xs.GetAsDictionary(PdfName.Resources);
                            var xres = xresDict != null ? new PdfResources(xresDict) : resources;
                            return (xs, xres);
                        }
                    }
                }
            }

            // Fallback: direct lookup without resources (less accurate)
            int max = doc.GetNumberOfPdfObjects();
            for (int i = 0; i < max; i++)
            {
                var obj = doc.GetPdfObject(i);
                if (obj is not PdfStream stream)
                    continue;
                int id = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                if (id == objId)
                    return (stream, new PdfResources(new PdfDictionary()));
            }

            return (null, null);
        }

        private static int ResolveDocPage(string pdfPath)
        {
            var hit = BookmarkDetector.Detect(pdfPath);
            if (hit.Found)
                return hit.Page;
            hit = ContentsPrefixDetector.Detect(pdfPath);
            if (hit.Found)
                return hit.Page;
            hit = HeaderLabelDetector.Detect(pdfPath);
            if (hit.Found)
                return hit.Page;
            hit = LargestContentsDetector.Detect(pdfPath);
            if (hit.Found)
                return hit.Page;
            return 0;
        }
}
}
