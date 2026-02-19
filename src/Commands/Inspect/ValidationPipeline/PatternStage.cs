using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;
using System.Text.Json;
using DiffMatchPatch;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Obj.Align;
using Obj.DocDetector;
using Obj.TjpbDespachoExtractor.Utils;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Reference;
using Obj.Utils;
using Obj.Honorarios;
using Obj.ValidatorModule;
using iText.Kernel.Pdf;

namespace Obj.Commands
{
    internal static partial class ObjectsPattern
    {
        private const string CReset = "\u001b[0m";
        private const string CRed = "\u001b[31m";
        private const string CGreen = "\u001b[32m";
        private const string CYellow = "\u001b[33m";
        private const string CBlue = "\u001b[34m";
        private const string CMagenta = "\u001b[35m";
        private const string CCyan = "\u001b[36m";

        private static bool _dmpLogEnabled = false;
        private static int _dmpLogCount = 0;
        private const int DmpLogMax = 200;
        private static int _winsRaw = 0;
        private static int _winsTyped = 0;
        private static int _winsBoth = 0;
        private static int _winsText = 0;
        private static int _winsRegex = 0;
        private static string _lastValidatorSummary = "off";
        private static string _lastHonorariosSummary = "off";
        private static Dictionary<string, List<RegexRule>>? _regexCatalog;
        private static string? _regexCatalogDoc;

        private static void LogStep(MatchOptions options, string color, string tag, string message)
        {
            if (!options.Log) return;
            Console.Error.WriteLine($"{color}{tag} {message}{CReset}");
        }

        private static void LogStep(bool enabled, string color, string tag, string message)
        {
            if (!enabled) return;
            Console.Error.WriteLine($"{color}{tag} {message}{CReset}");
        }

        private static void EnableDmpLog(bool enabled)
        {
            _dmpLogEnabled = enabled;
            _dmpLogCount = 0;
            _winsRaw = 0;
            _winsTyped = 0;
            _winsBoth = 0;
            _winsText = 0;
            _winsRegex = 0;
            _lastValidatorSummary = "off";
            _lastHonorariosSummary = "off";
        }

        private static void EnsureRegexCatalog(string patternsPath, bool log)
        {
            var doc = ReadDocNameFromPatterns(patternsPath);
            if (_regexCatalog != null && string.Equals(_regexCatalogDoc, doc, StringComparison.OrdinalIgnoreCase))
                return;
            _regexCatalogDoc = doc;
            _regexCatalog = LoadRegexCatalog(doc);
            if (log)
                Console.Error.WriteLine($"{CMagenta}[REGEX] catalog loaded: doc={doc} fields={_regexCatalog.Count}{CReset}");
        }

        private static void LogDmp(string expected, string actual, double score)
        {
            if (!_dmpLogEnabled) return;
            if (_dmpLogCount >= DmpLogMax) return;
            _dmpLogCount++;
            Console.Error.WriteLine($"{CMagenta}[DMP#{_dmpLogCount}] expLen={expected.Length} actLen={actual.Length} score={score:F3}{CReset}");
        }

        private static void TrackWinner(string kind)
        {
            switch (kind)
            {
                case "raw": _winsRaw++; break;
                case "typed": _winsTyped++; break;
                case "both": _winsBoth++; break;
                case "text": _winsText++; break;
                case "regex": _winsRegex++; break;
            }
        }

        private static void PrintWinnerStats(MatchOptions options)
        {
            if (!options.Log) return;
            var total = _winsRaw + _winsTyped + _winsBoth + _winsText + _winsRegex;
            if (total == 0) total = 1;
            double pct(int v) => (double)v * 100.0 / total;
            Console.Error.WriteLine($"{CMagenta}[SOURCES] Fontes usadas (aliadas; nao disputam){CReset}");
            Console.Error.WriteLine($"{CMagenta}  TOTAL : {total}{CReset}");
            Console.Error.WriteLine($"{CMagenta}  RAW   : {_winsRaw} ({pct(_winsRaw):F1}%)  [PAT_RAW]{CReset}");
            Console.Error.WriteLine($"{CMagenta}  TYPED : {_winsTyped} ({pct(_winsTyped):F1}%) [PAT_TYPED]{CReset}");
            Console.Error.WriteLine($"{CMagenta}  BOTH  : {_winsBoth} ({pct(_winsBoth):F1}%) [RAW+TYPED]{CReset}");
            Console.Error.WriteLine($"{CMagenta}  TEXT  : {_winsText} ({pct(_winsText):F1}%) [fallback texto]{CReset}");
            Console.Error.WriteLine($"{CMagenta}  REGEX : {_winsRegex} ({pct(_winsRegex):F1}%) [regex fallback]{CReset}");
            Console.Error.WriteLine($"{CMagenta}  VALIDATOR  : {_lastValidatorSummary}{CReset}");
            Console.Error.WriteLine($"{CMagenta}  HONORARIOS : {_lastHonorariosSummary}{CReset}");
        }

        private static string TrimSnippet(string text, int maxLen = 90)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var t = text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (t.Length <= maxLen) return t;
            return t.Substring(0, maxLen) + "...";
        }

        private static void LogDispute(MatchOptions options, string field, List<FieldMatch> matches, List<FieldReject> rejects)
        {
            if (!options.Log) return;
            if (matches == null || matches.Count == 0)
            {
                LogStep(options, CRed, "[DISPUTE]", $"{field} sem candidatos");
                return;
            }

            var best = matches[0];
            LogStep(options, CBlue, "[DISPUTE]", $"{field} escolhido: kind={best.Kind} score={best.Score:F2} op{best.StartOp}-op{best.EndOp} \"{TrimSnippet(best.ValueText)}\"");

            var alts = matches.Skip(1).Take(3).ToList();
            int idx = 1;
            foreach (var alt in alts)
            {
                LogStep(options, CYellow, "[ALT]", $"{field} #{idx} kind={alt.Kind} score={alt.Score:F2} op{alt.StartOp}-op{alt.EndOp} \"{TrimSnippet(alt.ValueText)}\"");
                idx++;
            }

            if (rejects != null && rejects.Count > 0)
            {
                var showR = Math.Min(3, rejects.Count);
                LogStep(options, CRed, "[REJECTS]", $"{field} reprovados={rejects.Count} (mostrando {showR})");
                foreach (var r in rejects.OrderByDescending(r => r.Score).Take(showR))
                {
                    var detail = $"reason={r.Reason} kind={r.Kind} score={r.Score:F2} op{r.StartOp}-op{r.EndOp} \"{TrimSnippet(r.ValueText)}\"";
                    LogStep(options, CRed, "[REJECT]", detail);
                }
            }
        }

        public static void Execute(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (TryHandleSubcommand(args))
                return;
            if (!ParseOptions(args, out var inputs, out var pageA, out var pageB, out var objA, out var objB, out var opFilter, out var backoff, out var outPath, out var patternsPath, out var fieldsFilter, out var showLegend, out var encodeText, out var decodePattern, out var decodeKind))
            {
                ShowHelp();
                return;
            }

            if (showLegend)
                PrintLegend();

            if (!string.IsNullOrWhiteSpace(encodeText))
            {
                EncodeText(encodeText);
                return;
            }

            if (!string.IsNullOrWhiteSpace(decodePattern))
            {
                DecodePattern(decodePattern, decodeKind);
                return;
            }

            if (!string.IsNullOrWhiteSpace(patternsPath))
            {
                DumpPatterns(patternsPath, fieldsFilter);
                return;
            }

            if (inputs.Count < 2)
            {
                ShowHelp();
                return;
            }

            if (opFilter.Count == 0)
            {
                opFilter.Add("Tj");
                opFilter.Add("TJ");
            }

            var aPath = inputs[0];
            if (!File.Exists(aPath))
            {
                Console.WriteLine("PDF nao encontrado.");
                return;
            }

            if (pageA <= 0)
                pageA = ResolvePage(aPath);
            if (pageA <= 0)
            {
                Console.WriteLine("Pagina nao encontrada.");
                return;
            }

            if (objA <= 0)
            {
                var hitA = ContentsStreamPicker.Pick(new StreamPickRequest { PdfPath = aPath, Page = pageA, RequireMarker = true });
                if (!hitA.Found || hitA.Obj <= 0)
                    hitA = ContentsStreamPicker.PickSecondLargest(new StreamPickRequest { PdfPath = aPath, Page = pageA, RequireMarker = false });
                if (!hitA.Found || hitA.Obj <= 0)
                    hitA = ContentsStreamPicker.Pick(new StreamPickRequest { PdfPath = aPath, Page = pageA, RequireMarker = false });
                objA = hitA.Obj;
            }

            if (objA <= 0)
            {
                Console.WriteLine("Obj do stream nao encontrado.");
                return;
            }

            var reports = new List<ObjectsTextOpsDiff.AlignDebugReport>();
            foreach (var bPath in inputs.Skip(1))
            {
                if (!File.Exists(bPath))
                {
                    Console.WriteLine("PDF nao encontrado.");
                    return;
                }

                var localPageB = pageB > 0 ? pageB : ResolvePage(bPath);
                if (localPageB <= 0)
                {
                    Console.WriteLine("Pagina nao encontrada.");
                    return;
                }

                var localObjB = objB;
                if (localObjB <= 0)
                {
                    var hitB = ContentsStreamPicker.PickSecondLargest(new StreamPickRequest { PdfPath = bPath, Page = localPageB, RequireMarker = false });
                    if (!hitB.Found || hitB.Obj <= 0)
                        hitB = ContentsStreamPicker.Pick(new StreamPickRequest { PdfPath = bPath, Page = localPageB, RequireMarker = true });
                    if (!hitB.Found || hitB.Obj <= 0)
                        hitB = ContentsStreamPicker.Pick(new StreamPickRequest { PdfPath = bPath, Page = localPageB, RequireMarker = false });
                    localObjB = hitB.Obj;
                }

                if (localObjB <= 0)
                {
                    Console.WriteLine("Obj do stream nao encontrado.");
                    return;
                }

                var report = ObjectsTextOpsDiff.ComputeAlignDebugForSelection(
                    aPath,
                    bPath,
                    new ObjectsTextOpsDiff.PageObjSelection { Page = pageA, Obj = objA },
                    new ObjectsTextOpsDiff.PageObjSelection { Page = localPageB, Obj = localObjB },
                    opFilter,
                    backoff,
                    "front_head");

                if (report == null)
                {
                    Console.WriteLine("Falha ao gerar report.");
                    return;
                }

                reports.Add(report);

                if (inputs.Count == 2)
                {
                    var jsonOptions = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    var json = JsonSerializer.Serialize(report, jsonOptions);
                    Console.WriteLine(json);

                    if (string.IsNullOrWhiteSpace(outPath))
                    {
                        var baseA = Path.GetFileNameWithoutExtension(aPath);
                        var baseB = Path.GetFileNameWithoutExtension(bPath);
                        outPath = Path.Combine("outputs", "align_ranges", $"{baseA}__{baseB}__textops_align.json");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
                    File.WriteAllText(outPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    Console.WriteLine("Arquivo salvo: " + outPath);
                }
            }

            if (inputs.Count > 2)
            {
                WriteStackedOutput(aPath, reports, outPath);
            }
        }

        private static int ResolvePage(string pdfPath)
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

        private static bool ParseOptions(
            string[] args,
            out List<string> inputs,
            out int pageA,
            out int pageB,
            out int objA,
            out int objB,
            out HashSet<string> opFilter,
            out int backoff,
            out string outPath,
            out string patternsPath,
            out HashSet<string> fieldsFilter,
            out bool showLegend,
            out string encodeText,
            out string decodePattern,
            out string decodeKind)
        {
            inputs = new List<string>();
            pageA = 0;
            pageB = 0;
            objA = 0;
            objB = 0;
            opFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            backoff = 2;
            outPath = "";
            patternsPath = "";
            fieldsFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            showLegend = false;
            encodeText = "";
            decodePattern = "";
            decodeKind = "pt";

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == "--help" || arg == "-h")
                    return false;

                if (string.Equals(arg, "--pageA", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out pageA);
                    continue;
                }
                if (string.Equals(arg, "--pageB", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out pageB);
                    continue;
                }
                if (string.Equals(arg, "--objA", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out objA);
                    continue;
                }
                if (string.Equals(arg, "--objB", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out objB);
                    continue;
                }
                if ((string.Equals(arg, "--op", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--ops", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    foreach (var raw in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        opFilter.Add(raw.Trim());
                    continue;
                }
                if (string.Equals(arg, "--backoff", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out backoff);
                    continue;
                }
                if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    outPath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--patterns", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    patternsPath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--fields", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    foreach (var raw in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        fieldsFilter.Add(raw.Trim());
                    continue;
                }
                if (string.Equals(arg, "--legend", StringComparison.OrdinalIgnoreCase))
                {
                    showLegend = true;
                    continue;
                }
                if (string.Equals(arg, "--encode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    encodeText = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--decode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    decodePattern = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--kind", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    decodeKind = args[++i].Trim().ToLowerInvariant();
                    continue;
                }

                if (!arg.StartsWith("-", StringComparison.Ordinal))
                    inputs.Add(arg);
            }

            if (!string.IsNullOrWhiteSpace(patternsPath))
                patternsPath = ResolvePatternPath(patternsPath);

            return true;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("operpdf pattern <pdfA> <pdfB|pdfC|...> [--pageA N] [--pageB N] [--objA N] [--objB N] [--ops Tj,TJ] [--backoff N] [--out file]");
            Console.WriteLine("operpdf pattern --patterns <arquivo.json> [--fields a,b]");
            Console.WriteLine("operpdf pattern --legend");
            Console.WriteLine("operpdf pattern --encode \"texto\"  (gera PAT)");
            Console.WriteLine("operpdf pattern --decode \"PADRAO\" [--kind pt|p1]");
            Console.WriteLine("operpdf pattern encode \"texto\"");
            Console.WriteLine("operpdf pattern decode \"PADRAO\" [--kind pt|p1]");
            Console.WriteLine("operpdf pattern legend");
            Console.WriteLine("operpdf pattern doc --input file.pdf --page N --obj N [--op Tj,TJ] [--limit N] [--keep-box] [--blocks-text|--blocks-norm] [--blocks-only] [--out <arquivo>]");
            Console.WriteLine("operpdf pattern translate --input file.pdf --page N --obj N [--limit N]   (alias de doc + blocks-norm + blocks-only)");
            Console.WriteLine("operpdf pattern raw --input file.pdf --page N --obj N [--op-range A-B]");
            Console.WriteLine("operpdf pattern tokens --input file.pdf --page N --obj N [--op-range A-B]");
            Console.WriteLine("operpdf pattern defects --input file.pdf --page N --obj N [--op Tj,TJ]");
            Console.WriteLine("operpdf pattern spaced --input file.pdf [--page N] [--obj N] [--min-run N] [--min-lines N] [--percent] [--out <arquivo>]");
            Console.WriteLine("operpdf pattern spaced --dir <pasta> [--recursive] [--min-run N] [--min-lines N] [--top N] [--list]");
            Console.WriteLine("operpdf pattern anchors --patterns <json> --inputs a.pdf,b.pdf [--fields A,B] --page N --obj N");
            Console.WriteLine("operpdf pattern match --patterns <json> --inputs a.pdf,b.pdf [--fields A,B] --page N --obj N [--require-all] [--timeout SEC] [--threads N]");
            Console.WriteLine("operpdf pattern report --patterns <json> --inputs a.pdf,b.pdf [--fields A,B] --page N --obj N");
            Console.WriteLine("operpdf pattern find --patterns <json> --input <pdf> [--fields A,B]");
        }

        private static void DumpPatterns(string patternsPath, HashSet<string> fieldsFilter)
        {
            if (!File.Exists(patternsPath))
            {
                Console.WriteLine("Arquivo nao encontrado: " + patternsPath);
                return;
            }

            List<FieldPatternEntry> entries;
            try
            {
                var json = File.ReadAllText(patternsPath);
                entries = ParsePatternEntries(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Falha ao ler patterns: " + ex.Message);
                return;
            }

            if (fieldsFilter.Count > 0)
                entries = entries.Where(e => !string.IsNullOrWhiteSpace(e.Field) && fieldsFilter.Contains(e.Field)).ToList();

            if (entries.Count == 0)
            {
                Console.WriteLine("Nenhum pattern encontrado.");
                return;
            }

            var groups = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Field))
                .GroupBy(e => e.Field!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                Console.WriteLine($"FIELD {group.Key}");
                int idx = 1;
                foreach (var entry in group)
                {
                    var prevRaw = BuildPatternSegment(entry.Prev, entry.PrevPatternTyped, entry.PrevPatternRaw, useRaw: true);
                    var prevTyp = BuildPatternSegment(entry.Prev, entry.PrevPatternTyped, entry.PrevPatternRaw, useRaw: false);
                    var midRaw = BuildPatternSegment(entry.Value, entry.ValuePatternTyped, entry.ValuePatternRaw, useRaw: true);
                    var midTyp = BuildPatternSegment(entry.Value, entry.ValuePatternTyped, entry.ValuePatternRaw, useRaw: false);
                    var nextRaw = BuildPatternSegment(entry.Next, entry.NextPatternTyped, entry.NextPatternRaw, useRaw: true);
                    var nextTyp = BuildPatternSegment(entry.Next, entry.NextPatternTyped, entry.NextPatternRaw, useRaw: false);

                    Console.WriteLine($"  [{idx:D2}] PREV RAW={prevRaw.Pattern}");
                    Console.WriteLine($"       PREV TYP={prevTyp.Pattern}");
                    Console.WriteLine($"       PREV TXT=\"{prevTyp.Text}\"");
                    Console.WriteLine($"       VAL  RAW={midRaw.Pattern}");
                    Console.WriteLine($"       VAL  TYP={midTyp.Pattern}");
                    Console.WriteLine($"       NEXT RAW={nextRaw.Pattern}");
                    Console.WriteLine($"       NEXT TYP={nextTyp.Pattern}");
                    Console.WriteLine($"       NEXT TXT=\"{nextTyp.Text}\"");
                    Console.WriteLine($"       ALL  PAT={prevTyp.Pattern}|{midTyp.Pattern}|{nextTyp.Pattern}");
                    if (!string.IsNullOrWhiteSpace(entry.Band) || !string.IsNullOrWhiteSpace(entry.YRange) || !string.IsNullOrWhiteSpace(entry.XRange))
                        Console.WriteLine($"       POS  band={entry.Band ?? ""} y={entry.YRange ?? ""} x={entry.XRange ?? ""}".TrimEnd());
                    idx++;
                }
                Console.WriteLine();
            }
        }

        private static PatternSegment BuildPatternSegment(string? textRaw, string? patternTyped, string? patternRaw, bool useRaw)
        {
            var text = useRaw ? NormalizePatternTextRaw(textRaw ?? "") : NormalizePatternText(textRaw ?? "");
            var pattern = useRaw ? patternRaw : patternTyped;
            if (string.IsNullOrWhiteSpace(pattern))
            {
                if (string.IsNullOrWhiteSpace(text))
                    pattern = "";
                else
                    pattern = useRaw ? BuildSimplePatternSpacedRaw(text) : ObjectsTextOpsDiff.EncodePatternTyped(text);
            }
            return new PatternSegment(text, pattern);
        }

        private static bool ValueMatchesRegexCatalog(string field, string text)
        {
            if (_regexCatalog == null || !_regexCatalog.TryGetValue(field, out var rules) || rules.Count == 0)
                return false;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var norm = NormalizeFullText(text);
            foreach (var rule in rules)
            {
                if (rule.Compiled == null)
                    continue;
                if (rule.Compiled.IsMatch(norm))
                    return true;
            }
            return false;
        }

        private static string? ExtractRegexValueFromCatalog(string field, string text)
        {
            if (_regexCatalog == null || !_regexCatalog.TryGetValue(field, out var rules) || rules.Count == 0)
                return null;
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var norm = NormalizeFullText(text);
            foreach (var rule in rules)
            {
                var rx = rule.Compiled;
                if (rx == null)
                    continue;
                var m = rx.Match(norm);
                if (!m.Success)
                    continue;
                var g = (rule.Group >= 0 && rule.Group < m.Groups.Count) ? m.Groups[rule.Group] : m.Groups[0];
                if (g == null || !g.Success)
                    continue;
                var value = g.Value.Trim();
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                value = NormalizeValueByField(field, value);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return null;
        }

        private static string? ExtractRegexValueFromCatalogLoose(string field, string text)
        {
            if (_regexCatalog == null || !_regexCatalog.TryGetValue(field, out var rules) || rules.Count == 0)
                return null;
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var norm = TextNormalization.NormalizeWhitespace(TextNormalization.FixMissingSpaces(text));
            foreach (var rule in rules)
            {
                var rx = rule.Compiled;
                if (rx == null)
                    continue;
                var m = rx.Match(norm);
                if (!m.Success)
                    continue;
                var g = (rule.Group >= 0 && rule.Group < m.Groups.Count) ? m.Groups[rule.Group] : m.Groups[0];
                if (g == null || !g.Success)
                    continue;
                var value = g.Value.Trim();
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                value = NormalizeValueByField(field, value);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return null;
        }

        private static List<FieldPatternEntry> ParsePatternEntries(string json)
        {
            var entries = new List<FieldPatternEntry>();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                    TryAddEntry(entries, item, null);
                return entries;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGetPropertyIgnoreCase(root, "fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in fieldsEl.EnumerateObject())
                    {
                        var fieldName = prop.Name;
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in prop.Value.EnumerateArray())
                                TryAddEntry(entries, item, fieldName);
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            TryAddEntry(entries, prop.Value, fieldName);
                        }
                    }
                }
                else if (TryGetArrayProperty(root, out var arr))
                {
                    foreach (var item in arr.EnumerateArray())
                        TryAddEntry(entries, item, null);
                }
                else
                {
                    TryAddEntry(entries, root, null);
                }
            }

            return entries;
        }

        private static bool TryGetArrayProperty(JsonElement root, out JsonElement arr)
        {
            if (TryGetPropertyIgnoreCase(root, "patterns", out arr) && arr.ValueKind == JsonValueKind.Array)
                return true;
            if (TryGetPropertyIgnoreCase(root, "items", out arr) && arr.ValueKind == JsonValueKind.Array)
                return true;
            if (TryGetPropertyIgnoreCase(root, "entries", out arr) && arr.ValueKind == JsonValueKind.Array)
                return true;
            arr = default;
            return false;
        }

        private static void TryAddEntry(List<FieldPatternEntry> entries, JsonElement item, string? fallbackField)
        {
            if (item.ValueKind != JsonValueKind.Object)
                return;

            var field = ReadString(item, "field", "campo") ?? fallbackField ?? "";
            var entry = new FieldPatternEntry
            {
                Field = field.Trim(),
                Prev = ReadString(item, "prev_text", "prev", "prev_anchor", "anchor_prev", "ancora_prev", "ancora_anterior"),
                Next = ReadString(item, "next_text", "next", "next_anchor", "anchor_next", "ancora_next", "ancora_posterior"),
                Value = ReadString(item, "value", "field_value", "campo_valor", "var", "mid"),
                PrevPatternTyped = ReadString(item, "prev_typed", "prevPatternTyped", "prev_pt", "prev_pattern", "prev_pat"),
                NextPatternTyped = ReadString(item, "next_typed", "nextPatternTyped", "next_pt", "next_pattern", "next_pat"),
                ValuePatternTyped = ReadString(item, "value_typed", "valuePatternTyped", "field_typed", "mid_typed", "var_typed", "value_pattern", "value_pat"),
                PrevPatternRaw = ReadString(item, "prev_raw", "prev_raw_pat", "prev_pat_raw", "prev_dw1", "prev_p1"),
                NextPatternRaw = ReadString(item, "next_raw", "next_raw_pat", "next_pat_raw", "next_dw1", "next_p1"),
                ValuePatternRaw = ReadString(item, "value_raw", "value_raw_pat", "value_pat_raw", "value_dw1", "value_p1"),
                Band = ReadString(item, "band", "pos", "position", "page_pos"),
                YRange = ReadString(item, "y_range", "yrange", "yRange"),
                XRange = ReadString(item, "x_range", "xrange", "xRange")
            };

            if (string.IsNullOrWhiteSpace(entry.Field))
                return;

            entries.Add(entry);
        }

        private static string? ReadString(JsonElement obj, params string[] names)
        {
            foreach (var name in names)
            {
                if (TryGetPropertyIgnoreCase(obj, name, out var val))
                {
                    if (val.ValueKind == JsonValueKind.String)
                        return val.GetString();
                }
            }
            return null;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
        {
            if (obj.ValueKind != JsonValueKind.Object)
            {
                value = default;
                return false;
            }

            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        private sealed class PatternSegment
        {
            public PatternSegment(string text, string pattern)
            {
                Text = text;
                Pattern = pattern;
            }

            public string Text { get; }
            public string Pattern { get; }
        }

        private sealed class FieldPatternEntry
        {
            public string Field { get; set; } = "";
            public string? Prev { get; set; }
            public string? Value { get; set; }
            public string? Next { get; set; }
            public string? PrevPatternTyped { get; set; }
            public string? ValuePatternTyped { get; set; }
            public string? NextPatternTyped { get; set; }
            public string? PrevPatternRaw { get; set; }
            public string? ValuePatternRaw { get; set; }
            public string? NextPatternRaw { get; set; }
            public string? Band { get; set; }
            public string? YRange { get; set; }
            public string? XRange { get; set; }
        }


        private static string ReadDocNameFromPatterns(string patternsPath)
        {
            if (string.IsNullOrWhiteSpace(patternsPath))
                return "";
            try
            {
                if (!File.Exists(patternsPath))
                    return "";
                using var doc = JsonDocument.Parse(File.ReadAllText(patternsPath));
                var root = doc.RootElement;
                if (TryGetPropertyIgnoreCase(root, "doc", out var docEl) && docEl.ValueKind == JsonValueKind.String)
                    return docEl.GetString() ?? "";
            }
            catch
            {
                return "";
            }
            return "";
        }

        private static void PrintLegend()
        {
            Console.WriteLine("PADRAO (PAT) - GABARITO");
            Console.WriteLine("RAW (texto bruto):");
            Console.WriteLine("  W = token 2+ caracteres");
            Console.WriteLine("  1 = token 1 caractere");
            Console.WriteLine();
            Console.WriteLine("TYPED (texto normalizado):");
            Console.WriteLine("  U = token em MAIUSCULO (ex.: JOAO, MUNICIPIO)");
            Console.WriteLine("  L = token em minusculo (ex.: movido, por)");
            Console.WriteLine("  T = Title Case (ex.: Joao, Cajazeiras)");
            Console.WriteLine("  P = particula (da/de/do/em/no/na/por/pelo/pela/...)");
            Console.WriteLine("  N = numero (so digitos ou digitos com pontuacao)");
            Console.WriteLine("  R = marcador de numero (nº, n°, n.)");
            Console.WriteLine("  F = CPF (11 digitos)");
            Console.WriteLine("  J = CNPJ (14 digitos)");
            Console.WriteLine("  Q = CNJ (16 ou 20 digitos)");
            Console.WriteLine("  V = valor monetario (ex.: R$, 300,00)");
            Console.WriteLine("  A = data");
            Console.WriteLine("  E = email");
            Console.WriteLine("  M = misto (letra+digito)");
            Console.WriteLine("  S = simbolo/pontuacao isolada");
            Console.WriteLine("  : = dois-pontos (token literal)");
            Console.WriteLine();
            Console.WriteLine("Sufixo de tamanho (TYPED):");
            Console.WriteLine("  1 = tamanho 1");
            Console.WriteLine("  2 = tamanho 2 ou mais");
            Console.WriteLine();
            Console.WriteLine("Ordinal (TYPED):");
            Console.WriteLine("  N1a/N2a = ordinal feminino (5ª)");
            Console.WriteLine("  N1o/N2o = ordinal masculino (2º)");
            Console.WriteLine();
            Console.WriteLine("Exemplos rapidos:");
            Console.WriteLine("  \"PROCESSO Nº\" -> U2R2");
            Console.WriteLine("  \"Requerente:\" -> T2:");
            Console.WriteLine("  \"movido por\" -> L2L2");
            Console.WriteLine();
        }

        private static void EncodeText(string text)
        {
            var normalized = NormalizePatternText(text ?? "");
            var pt = ObjectsTextOpsDiff.EncodePatternTyped(normalized);
            Console.WriteLine("TEXT: " + normalized);
            Console.WriteLine("PAT:  " + pt);
            Console.WriteLine();
            var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;
            Console.WriteLine("TOKENS:");
            foreach (var part in parts)
            {
                var ptt = ObjectsTextOpsDiff.EncodePatternTyped(part);
                Console.WriteLine($"  {part}  -> {ptt}");
            }
        }

        private static bool TryHandleSubcommand(string[] args)
        {
            if (args == null || args.Length == 0)
                return false;

            var cmd = args[0].Trim().ToLowerInvariant();
            if (cmd == "match" || cmd == "report")
            {
                var subArgs = args.Skip(1).ToArray();
                if (!TryParseMatchOptions(subArgs, out var options))
                {
                    PrintMatchHelp();
                    return true;
                }
                if (cmd == "report")
                    RunPatternReport(options);
                else
                    RunPatternMatch(options);
                return true;
            }
            if (cmd == "anchors" || cmd == "anchor")
            {
                var subArgs = args.Skip(1).ToArray();
                if (!TryParseAnchorOptions(subArgs, out var options))
                {
                    PrintAnchorsHelp();
                    return true;
                }
                RunPatternAnchors(options);
                return true;
            }
            if (cmd == "find" || cmd == "scan" || cmd == "identify")
            {
                var subArgs = args.Skip(1).ToArray();
                if (!TryParseFindOptions(subArgs, out var options))
                {
                    PrintFindHelp();
                    return true;
                }
                RunPatternFind(options);
                return true;
            }
            if (cmd == "doc" || cmd == "dump" || cmd == "translate")
            {
                var subArgs = args.Skip(1).ToArray();
                if (cmd == "translate")
                {
                    // Implicito: texto normalizado, sem PAT na tela.
                    subArgs = subArgs.Concat(new[] { "--blocks-norm", "--blocks-only" }).ToArray();
                }
                if (!TryParseDocOptions(subArgs, out var options))
                {
                    PrintDocHelp();
                    return true;
                }
                RunPatternDoc(options);
                return true;
            }
            if (cmd == "defects" || cmd == "defect")
            {
                var subArgs = args.Skip(1).ToArray();
                if (!TryParseDefectsOptions(subArgs, out var options))
                {
                    PrintDefectsHelp();
                    return true;
                }
                RunPatternDefects(options);
                return true;
            }
            if (cmd == "spaced" || cmd == "spacedlines" || cmd == "spaced-lines")
            {
                var subArgs = args.Skip(1).ToArray();
                if (!TryParseSpacedOptions(subArgs, out var options))
                {
                    PrintSpacedHelp();
                    return true;
                }
                RunPatternSpaced(options);
                return true;
            }
            if (cmd == "raw")
            {
                var subArgs = args.Skip(1).ToArray();
                if (!TryParseRawOptions(subArgs, out var options))
                {
                    PrintRawHelp();
                    return true;
                }
                RunPatternRaw(options);
                return true;
            }
            if (cmd == "tokens")
            {
                var subArgs = args.Skip(1).ToArray();
                if (!TryParseTokensOptions(subArgs, out var options))
                {
                    PrintTokensHelp();
                    return true;
                }
                RunPatternTokens(options);
                return true;
            }
            if (cmd == "legend")
            {
                PrintLegend();
                return true;
            }
            if (cmd == "encode")
            {
                var text = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "";
                EncodeText(text);
                return true;
            }
            if (cmd == "decode")
            {
                if (args.Length < 2)
                {
                    Console.WriteLine("Uso: pattern decode \"PADRAO\" [--kind pt|p1]");
                    return true;
                }
                var pattern = args[1];
                var kind = "pt";
                for (int i = 2; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--kind", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        kind = args[i + 1].Trim().ToLowerInvariant();
                        i++;
                    }
                }
                DecodePattern(pattern, kind);
                return true;
            }
            if (cmd == "decodefile" || cmd == "decode-file")
            {
                var subArgs = args.Skip(1).ToArray();
                if (!TryParseDecodeFileOptions(subArgs, out var options))
                {
                    PrintDecodeFileHelp();
                    return true;
                }
                RunDecodeFile(options);
                return true;
            }
            return false;
        }

        private class MatchOptions
        {
            public string PatternsPath { get; set; } = "";
            public List<string> Inputs { get; } = new();
            public HashSet<string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> OpFilter { get; } = new(StringComparer.OrdinalIgnoreCase);
            public string? PairsPath { get; set; }
            public string? AlignModelPath { get; set; }
            public int Page { get; set; }
            public int Obj { get; set; }
            public double MinScore { get; set; } = 0.50;
            public double MinStreamRatio { get; set; } = 0.40;
            public int Limit { get; set; } = 5;
            public string? OutPath { get; set; }
            public bool Log { get; set; }
            public int MaxPairs { get; set; } = 200000;
            public int MaxCandidates { get; set; } = 50;
            public bool UseRaw { get; set; } = true;
            public bool UseHonorarios { get; set; } = true;
            public bool UseValidator { get; set; } = true;
            public bool NoShortcut { get; set; } = true;
            public bool RequireAll { get; set; }
            public bool AutoScan { get; set; }
            public int ShortcutTop { get; set; } = 3;
            public double TimeoutSec { get; set; }
            public int Jobs { get; set; } = 1;
        }

        private sealed class AnchorOptions : MatchOptions
        {
            public string Side { get; set; } = "both";
        }

        private sealed class FindOptions
        {
            public string PatternsPath { get; set; } = "";
            public string Input { get; set; } = "";
            public string InputDir { get; set; } = "";
            public int Limit { get; set; }
            public HashSet<string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Page1Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Page2Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> OpFilter { get; } = new(StringComparer.OrdinalIgnoreCase);
            public double MinScore { get; set; } = 0.50;
            public int Top { get; set; } = 5;
            public int Pair { get; set; } = 0;
            public bool Explain { get; set; }
            public bool Json { get; set; }
            public string? OutPath { get; set; }
            public bool Clean { get; set; }
            public bool Log { get; set; }
            public int MaxPairs { get; set; } = 200000;
            public int MaxCandidates { get; set; } = 50;
            public bool UseRaw { get; set; } = true;
        }

        private sealed class DocOptions
        {
            public string Input { get; set; } = "";
            public HashSet<string> OpFilter { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int Page { get; set; }
            public int Obj { get; set; }
            public int Limit { get; set; }
            public bool PrintText { get; set; }
            public bool PrintTextNormalized { get; set; }
            public bool PrintTextOnly { get; set; }
            public bool KeepBoxes { get; set; }
            public string? OutPath { get; set; }
        }

        private sealed class SpacedOptions
        {
            public string Input { get; set; } = "";
            public string? Dir { get; set; }
            public bool Recursive { get; set; }
            public HashSet<string> OpFilter { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int Page { get; set; }
            public int Obj { get; set; }
            public int MinRun { get; set; } = 5;
            public int MinLines { get; set; }
            public bool PercentOnly { get; set; }
            public bool BestStream { get; set; }
            public string BestMetric { get; set; } = "abs";
            public int Top { get; set; } = 10;
            public bool ListFiles { get; set; }
            public bool PrintLinesOnly { get; set; }
            public bool PrintLinesNormalized { get; set; }
            public string? OutPath { get; set; }
        }

        private sealed class DefectsOptions
        {
            public string Input { get; set; } = "";
            public HashSet<string> OpFilter { get; } = new(StringComparer.OrdinalIgnoreCase);
            public int Page { get; set; }
            public int Obj { get; set; }
            public int MaxPerType { get; set; } = 5;
        }

        private sealed class DecodeFileOptions
        {
            public string Input { get; set; } = "";
        }

        private static bool TryParseMatchOptions(string[] args, out MatchOptions options)
        {
            options = new MatchOptions();
            if (args == null || args.Length == 0)
                return false;

            var execDefaults = ExecutionConfig.GetExecDefaults("pattern_match");
            if (execDefaults.Jobs > 0)
                options.Jobs = execDefaults.Jobs;
            if (execDefaults.TimeoutSec > 0)
                options.TimeoutSec = Math.Max(0, execDefaults.TimeoutSec);
            if (execDefaults.Log.HasValue)
                options.Log = execDefaults.Log.Value;

            var matchDefaults = LoadPatternMatchDefaults();
            if (!string.IsNullOrWhiteSpace(matchDefaults.Patterns))
                options.PatternsPath = matchDefaults.Patterns!;
            if (matchDefaults.MinScore.HasValue)
                options.MinScore = matchDefaults.MinScore.Value;
            if (matchDefaults.Limit.HasValue)
                options.Limit = matchDefaults.Limit.Value;
            if (matchDefaults.MaxPairs.HasValue && matchDefaults.MaxPairs.Value > 0)
                options.MaxPairs = matchDefaults.MaxPairs.Value;
            if (matchDefaults.MaxCandidates.HasValue && matchDefaults.MaxCandidates.Value > 0)
                options.MaxCandidates = matchDefaults.MaxCandidates.Value;
            if (matchDefaults.MinStreamRatio.HasValue)
                options.MinStreamRatio = matchDefaults.MinStreamRatio.Value;
            if (matchDefaults.Log.HasValue)
                options.Log = matchDefaults.Log.Value;
            if (matchDefaults.TimeoutSec.HasValue)
                options.TimeoutSec = Math.Max(0, matchDefaults.TimeoutSec.Value);
            if (matchDefaults.Jobs.HasValue && matchDefaults.Jobs.Value > 0)
                options.Jobs = matchDefaults.Jobs.Value;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--patterns", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.PatternsPath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                        options.TimeoutSec = Math.Max(0, t);
                    continue;
                }
                if ((string.Equals(arg, "--threads", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--jobs", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var jobs) && jobs > 0)
                        options.Jobs = jobs;
                    continue;
                }
                if (string.Equals(arg, "--inputs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.Inputs.Add(part.Trim());
                    continue;
                }
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Inputs.Add(args[++i]);
                    continue;
                }
                if (string.Equals(arg, "--pairs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.PairsPath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--align-model", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.AlignModelPath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--raw", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i].Trim().ToLowerInvariant();
                    options.UseRaw = !(raw == "off" || raw == "false" || raw == "0" || raw == "no");
                    continue;
                }
                if ((string.Equals(arg, "--input-dir", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--dir", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    var dir = args[++i];
                    if (Directory.Exists(dir))
                    {
                        foreach (var f in Directory.GetFiles(dir, "*.pdf"))
                            options.Inputs.Add(f);
                    }
                    continue;
                }
                if (string.Equals(arg, "--fields", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.Fields.Add(part.Trim());
                    continue;
                }
                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var page);
                    options.Page = page;
                    continue;
                }
                if (string.Equals(arg, "--obj", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var obj);
                    options.Obj = obj;
                    continue;
                }
                if (string.Equals(arg, "--op", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.OpFilter.Add(part.Trim());
                    continue;
                }
                if (string.Equals(arg, "--min-score", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var score);
                    options.MinScore = score;
                    continue;
                }
                if (string.Equals(arg, "--max-pairs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var maxPairs);
                    if (maxPairs > 0) options.MaxPairs = maxPairs;
                    continue;
                }
                if (string.Equals(arg, "--max-candidates", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var maxCandidates);
                    if (maxCandidates > 0) options.MaxCandidates = maxCandidates;
                    continue;
                }
                if (string.Equals(arg, "--raw", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i].Trim().ToLowerInvariant();
                    options.UseRaw = !(raw == "off" || raw == "false" || raw == "0" || raw == "no");
                    continue;
                }
                if (string.Equals(arg, "--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var limit);
                    options.Limit = limit;
                    continue;
                }
                if ((string.Equals(arg, "--shortcut-top", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--shortcut-pages", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                        options.ShortcutTop = Math.Max(1, n);
                    continue;
                }

                if (string.Equals(arg, "--no-shortcut", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--no-despacho-shortcut", StringComparison.OrdinalIgnoreCase))
                {
                    options.NoShortcut = true;
                    continue;
                }
                if (string.Equals(arg, "--require-all", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--strict", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--strict-detect", StringComparison.OrdinalIgnoreCase))
                {
                    options.RequireAll = true;
                    continue;
                }
                if (string.Equals(arg, "--honorarios", StringComparison.OrdinalIgnoreCase))
                {
                    options.UseHonorarios = true;
                    continue;
                }
                if (string.Equals(arg, "--no-honorarios", StringComparison.OrdinalIgnoreCase))
                {
                    options.UseHonorarios = false;
                    continue;
                }
                if (string.Equals(arg, "--validate", StringComparison.OrdinalIgnoreCase))
                {
                    options.UseValidator = true;
                    continue;
                }
                if (string.Equals(arg, "--no-validate", StringComparison.OrdinalIgnoreCase))
                {
                    // Validator is mandatory in this pipeline.
                    options.UseValidator = true;
                    continue;
                }
                if (string.Equals(arg, "--log", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--verbose", StringComparison.OrdinalIgnoreCase))
                {
                    options.Log = true;
                    continue;
                }
                if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.OutPath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--out-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var dir = args[++i];
                    if (!string.IsNullOrWhiteSpace(dir))
                        options.OutPath = Path.Combine(dir, "pairs_extract.json");
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(options.PatternsPath))
                options.PatternsPath = ResolvePatternPath(options.PatternsPath);

            if (string.IsNullOrWhiteSpace(options.PatternsPath))
                return false;

            if (options.Inputs.Count == 0 && string.IsNullOrWhiteSpace(options.PairsPath))
                return false;

            if (options.OpFilter.Count == 0)
            {
                options.OpFilter.Add("Tj");
                options.OpFilter.Add("TJ");
            }

            if (options.Inputs.Count > 0)
            {
                var invalid = options.Inputs.Where(Preflight.IsInvalid).ToList();
                if (invalid.Count > 0)
                {
                    foreach (var item in invalid)
                        options.Inputs.Remove(item);
                    Console.Error.WriteLine($"[PREFLIGHT] pattern: ignorados {invalid.Count} arquivos invalidos");
                }
            }
            options.Jobs = Math.Max(1, Math.Min(options.Jobs, Math.Max(1, Environment.ProcessorCount)));
            return true;
        }

        private static bool TryParseAnchorOptions(string[] args, out AnchorOptions options)
        {
            options = new AnchorOptions();
            if (args == null || args.Length == 0)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--patterns", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.PatternsPath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--inputs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.Inputs.Add(part.Trim());
                    continue;
                }
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Inputs.Add(args[++i]);
                    continue;
                }
                if ((string.Equals(arg, "--input-dir", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--dir", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    var dir = args[++i];
                    if (Directory.Exists(dir))
                    {
                        foreach (var f in Directory.GetFiles(dir, "*.pdf"))
                            options.Inputs.Add(f);
                    }
                    continue;
                }
                if (string.Equals(arg, "--fields", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.Fields.Add(part.Trim());
                    continue;
                }
                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var page);
                    options.Page = page;
                    continue;
                }
                if (string.Equals(arg, "--obj", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var obj);
                    options.Obj = obj;
                    continue;
                }
                if (string.Equals(arg, "--op", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.OpFilter.Add(part.Trim());
                    continue;
                }
                if (string.Equals(arg, "--min-score", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var score);
                    options.MinScore = score;
                    continue;
                }
                if (string.Equals(arg, "--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var limit);
                    options.Limit = limit;
                    continue;
                }
                if (string.Equals(arg, "--side", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Side = args[++i].Trim().ToLowerInvariant();
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(options.PatternsPath))
                options.PatternsPath = ResolvePatternPath(options.PatternsPath);

            if (string.IsNullOrWhiteSpace(options.PatternsPath))
                return false;
            if (options.Inputs.Count == 0)
                return false;

            if (options.OpFilter.Count == 0)
            {
                options.OpFilter.Add("Tj");
                options.OpFilter.Add("TJ");
            }

            if (string.IsNullOrWhiteSpace(options.Side))
                options.Side = "both";

            return true;
        }

        private static bool TryParseFindOptions(string[] args, out FindOptions options)
        {
            options = new FindOptions();
            if (args == null || args.Length == 0)
                return false;

            bool patternsSet = false;
            bool minScoreSet = false;
            bool topSet = false;
            bool pairSet = false;
            bool explainSet = false;
            bool cleanSet = false;
            bool rawSet = false;
            bool maxPairsSet = false;
            bool maxCandidatesSet = false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--patterns", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.PatternsPath = args[++i];
                    patternsSet = true;
                    continue;
                }
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Input = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--input-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.InputDir = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--fields", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.Fields.Add(part.Trim());
                    continue;
                }
                if ((string.Equals(arg, "--page1-fields", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--p1-fields", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.Page1Fields.Add(part.Trim());
                    continue;
                }
                if ((string.Equals(arg, "--page2-fields", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--p2-fields", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.Page2Fields.Add(part.Trim());
                    continue;
                }
                if (string.Equals(arg, "--op", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.OpFilter.Add(part.Trim());
                    continue;
                }
                if (string.Equals(arg, "--min-score", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var score);
                    options.MinScore = score;
                    minScoreSet = true;
                    continue;
                }
                if (string.Equals(arg, "--max-pairs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var maxPairs);
                    if (maxPairs > 0) options.MaxPairs = maxPairs;
                    continue;
                }
                if (string.Equals(arg, "--max-candidates", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var maxCandidates);
                    if (maxCandidates > 0) options.MaxCandidates = maxCandidates;
                    continue;
                }
                if (string.Equals(arg, "--raw", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i].Trim().ToLowerInvariant();
                    options.UseRaw = !(raw == "off" || raw == "false" || raw == "0" || raw == "no");
                    rawSet = true;
                    continue;
                }
                if (string.Equals(arg, "--top", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var top);
                    options.Top = top;
                    topSet = true;
                    continue;
                }
                if (string.Equals(arg, "--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var limit);
                    options.Limit = limit;
                    continue;
                }
                if (string.Equals(arg, "--pair", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal) &&
                        int.TryParse(args[i + 1], NumberStyles.Any, CultureInfo.InvariantCulture, out var pair))
                    {
                        options.Pair = pair;
                        i++;
                    }
                    else
                    {
                        options.Pair = 2;
                    }
                    pairSet = true;
                    continue;
                }
                if (string.Equals(arg, "per2", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "par2", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "pair2", StringComparison.OrdinalIgnoreCase))
                {
                    options.Pair = 2;
                    pairSet = true;
                    continue;
                }
                if (string.Equals(arg, "--explain", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--detail", StringComparison.OrdinalIgnoreCase))
                {
                    options.Explain = true;
                    explainSet = true;
                    continue;
                }
                if (string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
                {
                    options.Json = true;
                    continue;
                }
                if (string.Equals(arg, "--clean", StringComparison.OrdinalIgnoreCase))
                {
                    options.Clean = true;
                    cleanSet = true;
                    continue;
                }
                if (string.Equals(arg, "--log", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--verbose", StringComparison.OrdinalIgnoreCase))
                {
                    options.Log = true;
                    continue;
                }
                if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.OutPath = args[++i];
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(options.Input))
                {
                    options.Input = arg;
                    continue;
                }
            }

            var defaults = LoadPatternFindDefaults();
            if (!patternsSet && !string.IsNullOrWhiteSpace(defaults.Patterns))
                options.PatternsPath = defaults.Patterns;
            if (!minScoreSet && defaults.MinScore.HasValue)
                options.MinScore = defaults.MinScore.Value;
            if (!topSet && defaults.Top.HasValue)
                options.Top = defaults.Top.Value;
            if (!pairSet && defaults.Pair.HasValue)
                options.Pair = defaults.Pair.Value;
            if (!maxPairsSet && defaults.MaxPairs.HasValue)
                options.MaxPairs = defaults.MaxPairs.Value;
            if (!maxCandidatesSet && defaults.MaxCandidates.HasValue)
                options.MaxCandidates = defaults.MaxCandidates.Value;
            if (!explainSet && defaults.Explain.HasValue)
                options.Explain = defaults.Explain.Value;
            if (!cleanSet && defaults.Clean.HasValue)
                options.Clean = defaults.Clean.Value;
            if (!rawSet && defaults.UseRaw.HasValue)
                options.UseRaw = defaults.UseRaw.Value;

            if (string.IsNullOrWhiteSpace(options.PatternsPath))
                options.PatternsPath = ResolvePatternPath("tjpb_despacho");
            else
                options.PatternsPath = ResolvePatternPath(options.PatternsPath);
            if (string.IsNullOrWhiteSpace(options.Input) && string.IsNullOrWhiteSpace(options.InputDir))
                return false;

            if (options.OpFilter.Count == 0)
            {
                options.OpFilter.Add("Tj");
                options.OpFilter.Add("TJ");
            }

            return true;
        }

        private static bool TryParseDocOptions(string[] args, out DocOptions options)
        {
            options = new DocOptions();
            if (args == null || args.Length == 0)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Input = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var page);
                    options.Page = page;
                    continue;
                }
                if (string.Equals(arg, "--obj", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var obj);
                    options.Obj = obj;
                    continue;
                }
                if (string.Equals(arg, "--op", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.OpFilter.Add(part.Trim());
                    continue;
                }
                if (string.Equals(arg, "--limit", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var limit);
                    options.Limit = limit;
                    continue;
                }
                if (string.Equals(arg, "--blocks-text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--raw-text", StringComparison.OrdinalIgnoreCase))
                {
                    options.PrintText = true;
                    continue;
                }
                if (string.Equals(arg, "--blocks-only", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--text-only", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--only-text", StringComparison.OrdinalIgnoreCase))
                {
                    options.PrintText = true;
                    options.PrintTextOnly = true;
                    continue;
                }
                if (string.Equals(arg, "--blocks-norm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--norm-text", StringComparison.OrdinalIgnoreCase))
                {
                    options.PrintText = true;
                    options.PrintTextNormalized = true;
                    continue;
                }
                if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.OutPath = args[++i];
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(options.Input))
                {
                    options.Input = arg;
                }
            }

            return !string.IsNullOrWhiteSpace(options.Input);
        }

        private static bool TryParseSpacedOptions(string[] args, out SpacedOptions options)
        {
            options = new SpacedOptions();
            if (args == null || args.Length == 0)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Input = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Dir = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--recursive", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--rec", StringComparison.OrdinalIgnoreCase))
                {
                    options.Recursive = true;
                    continue;
                }
                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var page);
                    options.Page = page;
                    continue;
                }
                if (string.Equals(arg, "--obj", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var obj);
                    options.Obj = obj;
                    continue;
                }
                if ((string.Equals(arg, "--min-run", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--min-ones", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var minRun);
                    if (minRun > 0) options.MinRun = minRun;
                    continue;
                }
                if (string.Equals(arg, "--min-lines", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var minLines);
                    if (minLines > 0) options.MinLines = minLines;
                    continue;
                }
                if (string.Equals(arg, "--percent", StringComparison.OrdinalIgnoreCase))
                {
                    options.PercentOnly = true;
                    continue;
                }
                if (string.Equals(arg, "--best-stream", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--best-obj", StringComparison.OrdinalIgnoreCase))
                {
                    options.BestStream = true;
                    continue;
                }
                if ((string.Equals(arg, "--best-by", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--best-metric", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    var metric = args[++i].Trim().ToLowerInvariant();
                    if (metric == "abs" || metric == "absolute" || metric == "count")
                        options.BestMetric = "abs";
                    else if (metric == "percent" || metric == "pct")
                        options.BestMetric = "percent";
                    continue;
                }
                if (string.Equals(arg, "--top", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var top);
                    if (top > 0) options.Top = top;
                    continue;
                }
                if (string.Equals(arg, "--list", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--list-files", StringComparison.OrdinalIgnoreCase))
                {
                    options.ListFiles = true;
                    continue;
                }
                if (string.Equals(arg, "--lines-text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--print-lines", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--text-only", StringComparison.OrdinalIgnoreCase))
                {
                    options.PrintLinesOnly = true;
                    continue;
                }
                if (string.Equals(arg, "--lines-norm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--text-norm", StringComparison.OrdinalIgnoreCase))
                {
                    options.PrintLinesOnly = true;
                    options.PrintLinesNormalized = true;
                    continue;
                }
                if (string.Equals(arg, "--op", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.OpFilter.Add(part.Trim());
                    continue;
                }
                if (string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.OutPath = args[++i];
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(options.Input))
                {
                    options.Input = arg;
                }
            }

            if (options.OpFilter.Count == 0)
            {
                options.OpFilter.Add("Tj");
                options.OpFilter.Add("TJ");
            }

            if (options.MinLines <= 0)
                options.MinLines = options.MinRun > 0 ? options.MinRun : 5;

            return !string.IsNullOrWhiteSpace(options.Input) || !string.IsNullOrWhiteSpace(options.Dir);
        }

        private static bool TryParseDefectsOptions(string[] args, out DefectsOptions options)
        {
            options = new DefectsOptions();
            if (args == null || args.Length == 0)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Input = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var page);
                    options.Page = page;
                    continue;
                }
                if (string.Equals(arg, "--obj", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var obj);
                    options.Obj = obj;
                    continue;
                }
                if (string.Equals(arg, "--op", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i];
                    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        options.OpFilter.Add(part.Trim());
                    continue;
                }
                if ((string.Equals(arg, "--max-per-type", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--max", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var limit);
                    options.MaxPerType = Math.Max(1, limit);
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(options.Input))
                {
                    options.Input = arg;
                }
            }

            if (string.IsNullOrWhiteSpace(options.Input))
                return false;
            if (options.OpFilter.Count == 0)
            {
                options.OpFilter.Add("Tj");
                options.OpFilter.Add("TJ");
            }
            return true;
        }

        private static bool TryParseDecodeFileOptions(string[] args, out DecodeFileOptions options)
        {
            options = new DecodeFileOptions();
            if (args == null || args.Length == 0)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Input = args[++i];
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(options.Input))
                {
                    options.Input = arg;
                }
            }

            if (string.IsNullOrWhiteSpace(options.Input))
                return false;

            return true;
        }

        private class RawOptions
        {
            public string Input { get; set; } = "";
            public int Page { get; set; }
            public int Obj { get; set; }
            public int OpRangeStart { get; set; }
            public int OpRangeEnd { get; set; }
        }

        private class TokensOptions
        {
            public string Input { get; set; } = "";
            public int Page { get; set; }
            public int Obj { get; set; }
            public int OpRangeStart { get; set; }
            public int OpRangeEnd { get; set; }
            public int Max { get; set; } = 20;
            public int MinSeq { get; set; } = 2;
        }

        private static bool TryParseRawOptions(string[] args, out RawOptions options)
        {
            options = new RawOptions();
            if (args == null || args.Length == 0)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Input = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var page);
                    options.Page = page;
                    continue;
                }
                if (string.Equals(arg, "--obj", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var obj);
                    options.Obj = obj;
                    continue;
                }
                if (string.Equals(arg, "--op-range", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i].Trim();
                    if (TryParseOpRange(raw, out var a, out var b))
                    {
                        options.OpRangeStart = a;
                        options.OpRangeEnd = b;
                    }
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(options.Input))
                {
                    options.Input = arg;
                }
            }

            return !string.IsNullOrWhiteSpace(options.Input);
        }

        private static bool TryParseTokensOptions(string[] args, out TokensOptions options)
        {
            options = new TokensOptions();
            if (args == null || args.Length == 0)
                return false;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    options.Input = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var page);
                    options.Page = page;
                    continue;
                }
                if (string.Equals(arg, "--obj", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var obj);
                    options.Obj = obj;
                    continue;
                }
                if (string.Equals(arg, "--op-range", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = args[++i].Trim();
                    if (TryParseOpRange(raw, out var a, out var b))
                    {
                        options.OpRangeStart = a;
                        options.OpRangeEnd = b;
                    }
                    continue;
                }
                if ((string.Equals(arg, "--max", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--limit", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var limit);
                    options.Max = Math.Max(1, limit);
                    continue;
                }
                if ((string.Equals(arg, "--min-seq", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(arg, "--min-seq-len", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var minSeq);
                    options.MinSeq = Math.Max(2, minSeq);
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(options.Input))
                {
                    options.Input = arg;
                }
            }

            return !string.IsNullOrWhiteSpace(options.Input);
        }

        private static void PrintMatchHelp()
        {
            Console.WriteLine("Uso: operpdf pattern match --patterns <json> --inputs a.pdf,b.pdf [--fields A,B] --page N --obj N [--timeout SEC] [--threads N]");
            Console.WriteLine("     operpdf pattern match --patterns <json> --pairs <pairs.json> [--fields A,B] [--timeout SEC] [--threads N]");
            Console.WriteLine("     operpdf pattern report --patterns <json> --inputs a.pdf,b.pdf [--fields A,B] --page N --obj N");
            Console.WriteLine("  opcoes:");
            Console.WriteLine("    --min-score 0.50   score minimo para considerar match");
            Console.WriteLine("    --limit N          limite de matches por campo (saida do match)");
            Console.WriteLine("    --input-dir <dir>  adiciona todos os PDFs do diretorio");
            Console.WriteLine("    --pairs <json>     usa pares (pN/pN+1) vindos do pattern find");
            Console.WriteLine("    --align-model <pdf> aplica op_range por alinhamento (textopsalign)");
            Console.WriteLine("    --op Tj,TJ         filtra operadores de texto (default Tj,TJ)");
            Console.WriteLine("    --raw on|off       liga/desliga PAT_RAW (default on; use off p/ typed)");
            Console.WriteLine("    --max-candidates N limita candidatos por ancora (default 50)");
            Console.WriteLine("    --require-all      exige cobertura total dos campos para detectar pagina");
            Console.WriteLine("    --threads N        paraleliza por PDF (auto desliga em --log)");
            Console.WriteLine("    --out <arquivo>    salva o report em JSON");
        }

        private static void PrintAnchorsHelp()
        {
            Console.WriteLine("Uso: operpdf pattern anchors --patterns <json> --inputs a.pdf,b.pdf [--fields A,B] --page N --obj N");
            Console.WriteLine("  opcoes:");
            Console.WriteLine("    --min-score 0.50   score minimo para considerar anchor");
            Console.WriteLine("    --limit N          limite de hits por anchor");
            Console.WriteLine("    --input-dir <dir>  adiciona todos os PDFs do diretorio");
            Console.WriteLine("    --op Tj,TJ         filtra operadores de texto (default Tj,TJ)");
            Console.WriteLine("    --side prev|next|both   (default both)");
        }

        private static void PrintFindHelp()
        {
            Console.WriteLine("Uso: operpdf pattern find [per2|--pair 2] <pdf> [--fields A,B]");
            Console.WriteLine("     operpdf pattern identify --input-dir <dir> [--limit N] [--pair 2]");
            Console.WriteLine("  opcoes:");
            Console.WriteLine("    --min-score 0.50   score minimo para considerar match");
            Console.WriteLine("    --top N            top N objetos");
            Console.WriteLine("    --pair 2           retorna pares contiguos (pN/pN+1)");
            Console.WriteLine("    --input-dir <dir>  processa todos os PDFs do diretorio");
            Console.WriteLine("    --limit N          limita quantidade de arquivos no input-dir");
            Console.WriteLine("    --page1-fields A,B  campos usados no score da pagina 1 do par");
            Console.WriteLine("    --page2-fields A,B  campos usados no score da pagina 2 do par");
            Console.WriteLine("    --op Tj,TJ         filtra operadores de texto (default Tj,TJ)");
            Console.WriteLine("    --explain          detalha quais anchors/fields bateram");
            Console.WriteLine("    --json             imprime JSON completo (matches + hits)");
            Console.WriteLine("    --out <arquivo>    salva JSON completo em arquivo");
            Console.WriteLine("    --clean            saida limpa (MET/MISS separados, sem prev/next)");
        }

        private static void PrintDocHelp()
        {
            Console.WriteLine("Uso: operpdf pattern doc --input file.pdf --page N --obj N [--op Tj,TJ] [--limit N] [--keep-box] [--blocks-text|--blocks-norm] [--blocks-only] [--out <arquivo>]");
            Console.WriteLine("Uso: operpdf pattern translate --input file.pdf --page N --obj N [--limit N]");
        }

        private static void PrintDefectsHelp()
        {
            Console.WriteLine("Uso: operpdf pattern defects --input file.pdf --page N --obj N [--op Tj,TJ]");
            Console.WriteLine("  opcoes:");
            Console.WriteLine("    --max-per-type N   limite de exemplos por defeito (default 5)");
            Console.WriteLine();
            Console.WriteLine("Defeitos detectados (raw):");
            Console.WriteLine("  1) letras_espacadas        (P O D E R)");
            Console.WriteLine("  2) pontuacao_sem_espaco    (Requerente:Juizo)");
            Console.WriteLine("  3) letra_numero_colado     (Processo2021 / 2021Processo)");
            Console.WriteLine("  4) parenteses_colado       (texto)exemplo / texto(exemplo");
            Console.WriteLine("  5) hifenizacao             (peri- cia)");
            Console.WriteLine("  6) particula_colada_upper  (JUIZODACOMARCA)");
        }

        private static void PrintSpacedHelp()
        {
            Console.WriteLine("Uso: operpdf pattern spaced --input file.pdf [--page N] [--obj N] [--min-run N] [--min-lines N] [--percent] [--best-stream] [--best-by abs|percent] [--lines-text] [--lines-norm] [--out <arquivo>]");
            Console.WriteLine("     operpdf pattern spaced --dir <pasta> [--recursive] [--min-run N] [--min-lines N] [--top N] [--list] [--out <arquivo>]");
        }

        private static void PrintRawHelp()
        {
            Console.WriteLine("Uso: operpdf pattern raw --input file.pdf --page N --obj N [--op-range A-B]");
        }

        private static void PrintTokensHelp()
        {
            Console.WriteLine("Uso: operpdf pattern tokens --input file.pdf --page N --obj N [--op-range A-B]");
            Console.WriteLine("  opcoes:");
            Console.WriteLine("    --max N        limite de exemplos por categoria (default 20)");
            Console.WriteLine("    --min-seq N    minimo de letras unitarias em sequencia (default 2)");
        }

        private static void PrintDecodeFileHelp()
        {
            Console.WriteLine("Uso: operpdf pattern decodefile --input <arquivo>");
        }

        private static void RunPatternDoc(DocOptions options)
        {
            if (!File.Exists(options.Input))
            {
                Console.WriteLine("PDF nao encontrado: " + options.Input);
                return;
            }

            if (options.OpFilter.Count == 0)
            {
                options.OpFilter.Add("Tj");
                options.OpFilter.Add("TJ");
            }

            if (options.Page <= 0 || options.Obj <= 0)
            {
                Console.WriteLine("Informe --page e --obj (use operpdf objdiff para encontrar).");
                return;
            }

            var tokens = ExtractTokensFromPdf(options.Input, options.Page, options.Obj, options.OpFilter);
            if (tokens.Count == 0)
            {
                Console.WriteLine("Sem tokens.");
                return;
            }

            Console.WriteLine($"PDF: {Path.GetFileName(options.Input)}");
            Console.WriteLine($"TOKENS: {tokens.Count}");

            var blocks = ExtractPatternBlocksFromPdf(options.Input, options.Page, options.Obj, options.OpFilter, allowFix: !options.KeepBoxes);
            Console.WriteLine($"BLOCKS: {blocks.Count}");

            int limit = options.Limit > 0 ? options.Limit : blocks.Count;
            for (int i = 0; i < Math.Min(limit, blocks.Count); i++)
            {
                var b = blocks[i];
                var rawText = NormalizePatternTextRaw(b.RawText);
                var rawTokens = GetRawTokens(b);
                var normText = NormalizeFullText(b.RawText ?? "");
                var patRaw = BuildSimplePatternSpacedRawTokens(rawTokens);
                var patNorm = BuildTypedPatternSpaced(normText);
                if (!options.PrintTextOnly)
                    Console.WriteLine($"[{i + 1:D3}] op{b.StartOp}-op{b.EndOp} PAT_RAW: {patRaw} | PAT_NORM: {patNorm}");
                if (options.PrintText)
                {
                    var textToPrint = options.PrintTextNormalized ? normText : (b.RawText ?? "");
                    if (!string.IsNullOrWhiteSpace(textToPrint))
                    {
                        if (options.PrintTextOnly)
                            Console.WriteLine(textToPrint);
                        else
                            Console.WriteLine(textToPrint);
                    }
                }
            }

            if (limit < blocks.Count)
                Console.WriteLine($"... ({blocks.Count - limit} blocos omitidos)");

            if (!string.IsNullOrWhiteSpace(options.OutPath))
            {
                var payload = blocks.Select((b, i) => new
                {
                    idx = i + 1,
                    rawText = NormalizePatternTextRaw(b.RawText),
                    pat_raw = BuildSimplePatternSpacedRawTokens(GetRawTokens(b)),
                    text = CollapseShortUpperRunsText(b.Text),
                    pat_norm = BuildTypedPatternSpaced(CollapseShortUpperRunsText(b.Text)),
                    b.StartOp,
                    b.EndOp,
                    yMin = b.YMin,
                    yMax = b.YMax
                }).ToList();
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                Directory.CreateDirectory(Path.GetDirectoryName(options.OutPath) ?? ".");
                File.WriteAllText(options.OutPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                Console.WriteLine("Arquivo salvo: " + options.OutPath);
            }
        }

        private static void RunPatternSpaced(SpacedOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.Dir))
            {
                RunPatternSpacedDir(options);
                return;
            }
            if (!File.Exists(options.Input))
            {
                Console.WriteLine("PDF nao encontrado: " + options.Input);
                return;
            }

            using var reader = new PdfReader(options.Input);
            using var doc = new PdfDocument(reader);

            var pageStart = options.Page > 0 ? options.Page : 1;
            var pageEnd = options.Page > 0 ? options.Page : doc.GetNumberOfPages();

            var pages = new List<Dictionary<string, object?>>();
            int totalTokens = 0;
            int totalSingles = 0;
            int totalBlocks = 0;
            int totalSpacedBlocks = 0;

            for (int p = pageStart; p <= pageEnd; p++)
            {
                var page = doc.GetPage(p);
                if (page == null) continue;

                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);

                var linesByStream = new Dictionary<int, List<Dictionary<string, object?>>>();
                var streamStats = new Dictionary<int, (int Tokens, int Singles, int Blocks, int SpacedBlocks)>();
                int pageTokens = 0;
                int pageSingles = 0;
                int pageBlocks = 0;
                int pageSpacedBlocks = 0;
                int bestObjId = 0;
                double bestSinglePct = -1;
                int bestAbsSingles = -1;
                int bestTokens = 0;
                int bestSingles = 0;
                int bestBlocks = 0;
                int bestSpacedBlocks = 0;
                int bestObjIdByPct = 0;
                int bestObjIdByAbs = 0;
                int bestTokensByPct = 0;
                int bestSinglesByPct = 0;
                int bestBlocksByPct = 0;
                int bestSpacedByPct = 0;
                int bestTokensByAbs = 0;
                int bestSinglesByAbs = 0;
                int bestBlocksByAbs = 0;
                int bestSpacedByAbs = 0;

                IEnumerable<PdfStream> streams;
                if (options.Obj > 0)
                {
                    var found = FindStreamAndResourcesByObjId(doc, options.Obj);
                    streams = found.Stream != null ? new[] { found.Stream } : Array.Empty<PdfStream>();
                    if (found.Resources != null)
                        resources = found.Resources;
                }
                else
                {
                    streams = EnumerateStreams(contents);
                }

                foreach (var stream in streams)
                {
                    if (stream == null) continue;
                    int objId = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                    var blocks = ObjectsTextOpsDiff.ExtractPatternBlocks(stream, resources, options.OpFilter);
                    int streamTokens = 0;
                    int streamSingles = 0;
                    int streamBlocks = 0;
                    int streamSpacedBlocks = 0;
                    foreach (var block in blocks)
                    {
                        var rawTokens = GetRawTokens(block);
                        if (rawTokens.Count == 0)
                            continue;

                        pageBlocks++;
                        streamBlocks++;
                        totalBlocks++;

                        var rawPatternTokens = rawTokens.Select(ToRawPatternToken).ToList();
                        var maxRun = MaxRunOf(rawPatternTokens, "1");
                        var singleCount = rawTokens.Count(t => t.Length == 1);
                        pageTokens += rawTokens.Count;
                        pageSingles += singleCount;
                        streamTokens += rawTokens.Count;
                        streamSingles += singleCount;
                        totalTokens += rawTokens.Count;
                        totalSingles += singleCount;

                        if (maxRun >= options.MinRun)
                        {
                            pageSpacedBlocks++;
                            streamSpacedBlocks++;
                            totalSpacedBlocks++;
                            if (!linesByStream.TryGetValue(objId, out var list))
                            {
                                list = new List<Dictionary<string, object?>>();
                                linesByStream[objId] = list;
                            }
                            list.Add(new Dictionary<string, object?>
                            {
                                ["index"] = block.Index,
                                ["stream_obj"] = objId,
                                ["op_range"] = $"op{block.StartOp}-op{block.EndOp}",
                                ["raw_text"] = block.RawText ?? "",
                                ["raw_pattern"] = string.Join(" ", rawPatternTokens),
                                ["raw_pattern_compact"] = string.Concat(rawPatternTokens),
                                ["raw_max_run"] = maxRun,
                                ["text"] = block.Text ?? "",
                                ["pattern"] = block.Pattern ?? "",
                                ["pattern_typed"] = block.PatternTyped ?? "",
                                ["y_min"] = block.YMin,
                                ["y_max"] = block.YMax
                            });
                        }
                    }

                    if (streamTokens > 0 || streamBlocks > 0)
                        streamStats[objId] = (streamTokens, streamSingles, streamBlocks, streamSpacedBlocks);

                    if (streamTokens > 0)
                    {
                        var pct = (streamSingles * 100.0) / streamTokens;
                        if (pct > bestSinglePct)
                        {
                            bestSinglePct = pct;
                            bestObjIdByPct = objId;
                            bestTokensByPct = streamTokens;
                            bestSinglesByPct = streamSingles;
                            bestBlocksByPct = streamBlocks;
                            bestSpacedByPct = streamSpacedBlocks;
                        }
                        if (streamSingles > bestAbsSingles)
                        {
                            bestAbsSingles = streamSingles;
                            bestObjIdByAbs = objId;
                            bestTokensByAbs = streamTokens;
                            bestSinglesByAbs = streamSingles;
                            bestBlocksByAbs = streamBlocks;
                            bestSpacedByAbs = streamSpacedBlocks;
                        }
                    }
                }

                var streamList = streamStats
                    .Select(kv => new Dictionary<string, object?>
                    {
                        ["stream_obj"] = kv.Key,
                        ["single_tokens"] = kv.Value.Singles,
                        ["total_tokens"] = kv.Value.Tokens,
                        ["percent_single_tokens"] = kv.Value.Tokens > 0 ? Math.Round((kv.Value.Singles * 100.0) / kv.Value.Tokens, 2) : 0.0,
                        ["spaced_blocks"] = kv.Value.SpacedBlocks,
                        ["total_blocks"] = kv.Value.Blocks,
                        ["percent_spaced_blocks"] = kv.Value.Blocks > 0 ? Math.Round((kv.Value.SpacedBlocks * 100.0) / kv.Value.Blocks, 2) : 0.0
                    })
                    .ToList();

                var topAbs = streamList
                    .OrderByDescending(s => Convert.ToInt32(s["single_tokens"]))
                    .ThenByDescending(s => Convert.ToDouble(s["percent_single_tokens"]))
                    .ThenByDescending(s => Convert.ToInt32(s["total_tokens"]))
                    .Take(2)
                    .Select((s, i) =>
                    {
                        s["rank"] = i + 1;
                        return s;
                    })
                    .ToList();

                var bestAbs = topAbs.FirstOrDefault();

                if (bestAbs != null)
                {
                    bestObjId = Convert.ToInt32(bestAbs["stream_obj"]);
                    bestTokens = Convert.ToInt32(bestAbs["total_tokens"]);
                    bestSingles = Convert.ToInt32(bestAbs["single_tokens"]);
                    bestBlocks = Convert.ToInt32(bestAbs["total_blocks"]);
                    bestSpacedBlocks = Convert.ToInt32(bestAbs["spaced_blocks"]);
                }

                var lines = options.BestStream && bestObjId > 0
                    ? (linesByStream.TryGetValue(bestObjId, out var bestLines) ? bestLines : new List<Dictionary<string, object?>>())
                    : linesByStream.Values.SelectMany(v => v).ToList();

                var pageSummary = new Dictionary<string, object?>
                {
                    ["total_tokens"] = pageTokens,
                    ["single_tokens"] = pageSingles,
                    ["percent_single_tokens"] = pageTokens > 0 ? Math.Round((pageSingles * 100.0) / pageTokens, 2) : 0.0,
                    ["total_blocks"] = pageBlocks,
                    ["spaced_blocks"] = pageSpacedBlocks,
                    ["percent_spaced_blocks"] = pageBlocks > 0 ? Math.Round((pageSpacedBlocks * 100.0) / pageBlocks, 2) : 0.0
                };

                var pageEntry = new Dictionary<string, object?>
                {
                    ["page"] = p,
                    ["scope"] = options.Obj > 0 ? "object" : "page",
                    ["min_run"] = options.MinRun,
                    ["min_lines"] = options.MinLines,
                    ["summary"] = pageSummary,
                    ["best_stream_abs"] = bestObjIdByAbs > 0 ? new Dictionary<string, object?>
                    {
                        ["stream_obj"] = bestObjIdByAbs,
                        ["single_tokens"] = bestSinglesByAbs,
                        ["total_tokens"] = bestTokensByAbs,
                        ["percent_single_tokens"] = bestTokensByAbs > 0 ? Math.Round((bestSinglesByAbs * 100.0) / bestTokensByAbs, 2) : 0.0,
                        ["spaced_blocks"] = bestSpacedByAbs,
                        ["total_blocks"] = bestBlocksByAbs,
                        ["percent_spaced_blocks"] = bestBlocksByAbs > 0 ? Math.Round((bestSpacedByAbs * 100.0) / bestBlocksByAbs, 2) : 0.0
                    } : null,
                    ["top_streams_abs"] = topAbs,
                    ["lines"] = lines
                };
                if (options.Obj > 0)
                    pageEntry["obj"] = options.Obj;

                pages.Add(pageEntry);

                if (options.PercentOnly)
                {
                    var bestAbsLabel = bestObjIdByAbs > 0
                        ? $" best_abs=obj{bestObjIdByAbs} ({bestSinglesByAbs})"
                        : "";
                    Console.WriteLine($"page={p} single_tokens={pageSingles}/{pageTokens} ({pageSummary["percent_single_tokens"]}%) spaced_blocks={pageSpacedBlocks}/{pageBlocks} ({pageSummary["percent_spaced_blocks"]}%)" + bestAbsLabel);
                    if (topAbs.Count > 0)
                    {
                        var topAbsStr = string.Join(" | ", topAbs.Select(s =>
                        {
                            var obj = s["stream_obj"];
                            var singles = s["single_tokens"];
                            var total = s["total_tokens"];
                            var pct = s["percent_single_tokens"];
                            var rank = s["rank"];
                            return $"{rank}) obj{obj} {singles}/{total} ({pct}%)";
                        }));
                        Console.WriteLine("  TOP abs: " + topAbsStr);
                    }
                }

                if (options.PrintLinesOnly && lines.Count > 0)
                {
                    foreach (var line in lines)
                    {
                        var raw = line.TryGetValue("raw_text", out var rawObj) ? rawObj?.ToString() ?? "" : "";
                        var text = options.PrintLinesNormalized ? NormalizeFullText(raw) : raw;
                        if (!string.IsNullOrWhiteSpace(text))
                            Console.WriteLine(text);
                    }
                }
            }

            var overall = new Dictionary<string, object?>
            {
                ["total_tokens"] = totalTokens,
                ["single_tokens"] = totalSingles,
                ["percent_single_tokens"] = totalTokens > 0 ? Math.Round((totalSingles * 100.0) / totalTokens, 2) : 0.0,
                ["total_blocks"] = totalBlocks,
                ["spaced_blocks"] = totalSpacedBlocks,
                ["percent_spaced_blocks"] = totalBlocks > 0 ? Math.Round((totalSpacedBlocks * 100.0) / totalBlocks, 2) : 0.0
            };

            var payload = new Dictionary<string, object?>
            {
                ["input"] = options.Input,
                ["page"] = options.Page > 0 ? options.Page : (int?)null,
                ["obj"] = options.Obj > 0 ? options.Obj : (int?)null,
                ["min_run"] = options.MinRun,
                ["pages"] = pages,
                ["overall"] = overall
            };

            if (options.PercentOnly)
            {
                Console.WriteLine($"overall single_tokens={totalSingles}/{totalTokens} ({overall["percent_single_tokens"]}%) spaced_blocks={totalSpacedBlocks}/{totalBlocks} ({overall["percent_spaced_blocks"]}%)");
            }

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var json = JsonSerializer.Serialize(payload, jsonOptions);

            if (string.IsNullOrWhiteSpace(options.OutPath))
            {
                if (!options.PrintLinesOnly)
                    Console.WriteLine(json);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(options.OutPath) ?? ".");
                File.WriteAllText(options.OutPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                Console.WriteLine("Arquivo salvo: " + options.OutPath);
            }
        }

        private sealed class SpacedFileStats
        {
            public string File { get; set; } = "";
            public int TotalTokens { get; set; }
            public int SingleTokens { get; set; }
            public int TotalBlocks { get; set; }
            public int SpacedBlocks { get; set; }
            public int BestAbsPage { get; set; }
            public int BestAbsObj { get; set; }
            public int BestAbsTokens { get; set; }
            public int BestAbsSingles { get; set; }
            public int BestAbsBlocks { get; set; }
            public int BestAbsSpacedBlocks { get; set; }
            public double PercentSingles => TotalTokens > 0 ? (SingleTokens * 100.0) / TotalTokens : 0.0;
            public double BestAbsPercent => BestAbsTokens > 0 ? (BestAbsSingles * 100.0) / BestAbsTokens : 0.0;
            public bool HasSpaced => SpacedBlocks > 0;
        }

        private sealed class LineInfo
        {
            public int Index { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string Text { get; set; } = "";
            public double? Y { get; set; }
            public bool IsBlank { get; set; }
        }

        private sealed class ParagraphInfo
        {
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public int LineCount { get; set; }
            public string Text { get; set; } = "";
        }

        private static List<ParagraphInfo> BuildParagraphsFromBlocks(List<ObjectsTextOpsDiff.PatternBlock> blocks)
        {
            if (blocks == null || blocks.Count == 0)
                return new List<ParagraphInfo>();

            var ordered = blocks.OrderBy(b => b.Index).ToList();
            var lines = new List<LineInfo>();
            int idx = 0;
            foreach (var b in ordered)
            {
                idx++;
                var text = (b.Text ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ").Trim();
                var isBlank = string.IsNullOrWhiteSpace(text);
                double? y = null;
                if (b.YMin.HasValue && b.YMax.HasValue)
                    y = (b.YMin.Value + b.YMax.Value) / 2.0;
                lines.Add(new LineInfo
                {
                    Index = idx,
                    StartOp = b.StartOp,
                    EndOp = b.EndOp,
                    Text = text,
                    Y = y,
                    IsBlank = isBlank
                });
            }

            var nonBlank = lines.Where(l => !l.IsBlank && l.Y.HasValue).ToList();
            var gaps = new List<double>();
            for (int i = 1; i < nonBlank.Count; i++)
            {
                var dy = nonBlank[i - 1].Y!.Value - nonBlank[i].Y!.Value;
                if (dy > 0) gaps.Add(dy);
            }
            double medianGap = 0;
            if (gaps.Count > 0)
            {
                gaps.Sort();
                medianGap = gaps[gaps.Count / 2];
            }
            var threshold = medianGap > 0 ? medianGap * 1.6 : 0;

            var paras = new List<ParagraphInfo>();
            ParagraphInfo? current = null;
            LineInfo? prev = null;
            foreach (var line in lines)
            {
                bool newPara = false;
                if (line.IsBlank)
                {
                    if (current != null)
                    {
                        paras.Add(current);
                        current = null;
                    }
                    prev = line;
                    continue;
                }

                if (prev != null && !prev.IsBlank && line.Y.HasValue && prev.Y.HasValue && threshold > 0)
                {
                    var dy = prev.Y.Value - line.Y.Value;
                    if (dy > threshold) newPara = true;
                }

                if (current == null || newPara)
                {
                    if (current != null) paras.Add(current);
                    current = new ParagraphInfo
                    {
                        StartOp = line.StartOp,
                        EndOp = line.EndOp,
                        LineCount = 1,
                        Text = line.Text
                    };
                }
                else
                {
                    current.EndOp = line.EndOp;
                    current.LineCount++;
                    current.Text = string.Join("\n", new[] { current.Text, line.Text });
                }

                prev = line;
            }

            if (current != null) paras.Add(current);
            return paras;
        }

        private static void RunPatternSpacedDir(SpacedOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.Dir) || !Directory.Exists(options.Dir))
            {
                Console.WriteLine("Diretorio nao encontrado: " + options.Dir);
                return;
            }

            var search = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.EnumerateFiles(options.Dir, "*.pdf", search)
                .Concat(Directory.EnumerateFiles(options.Dir, "*.PDF", search))
                .ToList();

            var results = new List<SpacedFileStats>();
            var errors = new List<(string File, string Error)>();
            var progress = ProgressReporter.FromConfig("spaced", files.Count);

            foreach (var file in files)
            {
                try
                {
                    var stats = ComputeSpacedStats(file, options);
                    results.Add(stats);
                }
                catch (Exception ex)
                {
                    errors.Add((file, ex.Message));
                }
                progress?.Tick(Path.GetFileName(file));
            }

            int total = results.Count;
            int withSpaced = results.Count(r => r.HasSpaced);
            double pctFiles = total > 0 ? (withSpaced * 100.0 / total) : 0.0;

            Console.WriteLine($"DIR: {options.Dir}");
            Console.WriteLine($"PDFs={total}  com_espacado={withSpaced} ({pctFiles:0.0}%)  min_run={options.MinRun}");

            var top = options.BestMetric == "percent"
                ? results.OrderByDescending(r => r.PercentSingles).ThenByDescending(r => r.SingleTokens)
                : results.OrderByDescending(r => r.SingleTokens).ThenByDescending(r => r.PercentSingles);
            var topList = top.Take(Math.Max(1, options.Top)).ToList();

            Console.WriteLine("TOP (abs):");
            int rank = 1;
            foreach (var r in topList)
            {
                var best = r.BestAbsObj > 0
                    ? $"best_abs=p{r.BestAbsPage} obj{r.BestAbsObj} {r.BestAbsSingles}/{r.BestAbsTokens} ({r.BestAbsPercent:0.0}%)"
                    : "best_abs=none";
                Console.WriteLine($"  {rank}) {Path.GetFileName(r.File)}  1%={r.PercentSingles:0.0}%  spaced_blocks={r.SpacedBlocks}/{r.TotalBlocks}  {best}");
                rank++;
            }

            if (options.ListFiles)
            {
                Console.WriteLine();
                Console.WriteLine("LISTA (arquivo a arquivo):");
                foreach (var r in results)
                {
                    var best = r.BestAbsObj > 0
                        ? $"best_abs=p{r.BestAbsPage} obj{r.BestAbsObj} {r.BestAbsSingles}/{r.BestAbsTokens} ({r.BestAbsPercent:0.0}%)"
                        : "best_abs=none";
                    Console.WriteLine($"{Path.GetFileName(r.File)}  1%={r.PercentSingles:0.0}%  spaced_blocks={r.SpacedBlocks}/{r.TotalBlocks}  {best}");
                }
            }

            if (!string.IsNullOrWhiteSpace(options.OutPath))
            {
                var payload = new
                {
                    dir = options.Dir,
                    total_files = total,
                    with_spaced = withSpaced,
                    percent_files = Math.Round(pctFiles, 2),
                    min_run = options.MinRun,
                    top = topList.Select(r => new
                    {
                        file = r.File,
                        single_tokens = r.SingleTokens,
                        total_tokens = r.TotalTokens,
                        percent_single_tokens = Math.Round(r.PercentSingles, 2),
                        spaced_blocks = r.SpacedBlocks,
                        total_blocks = r.TotalBlocks,
                        best_abs = r.BestAbsObj > 0 ? new
                        {
                            page = r.BestAbsPage,
                            obj = r.BestAbsObj,
                            single_tokens = r.BestAbsSingles,
                            total_tokens = r.BestAbsTokens,
                            percent_single_tokens = Math.Round(r.BestAbsPercent, 2),
                            spaced_blocks = r.BestAbsSpacedBlocks,
                            total_blocks = r.BestAbsBlocks
                        } : null
                    }),
                    files = results.Select(r => new
                    {
                        file = r.File,
                        single_tokens = r.SingleTokens,
                        total_tokens = r.TotalTokens,
                        percent_single_tokens = Math.Round(r.PercentSingles, 2),
                        spaced_blocks = r.SpacedBlocks,
                        total_blocks = r.TotalBlocks,
                        best_abs = r.BestAbsObj > 0 ? new
                        {
                            page = r.BestAbsPage,
                            obj = r.BestAbsObj,
                            single_tokens = r.BestAbsSingles,
                            total_tokens = r.BestAbsTokens,
                            percent_single_tokens = Math.Round(r.BestAbsPercent, 2),
                            spaced_blocks = r.BestAbsSpacedBlocks,
                            total_blocks = r.BestAbsBlocks
                        } : null
                    }),
                    errors = errors.Select(e => new { file = e.File, error = e.Error }).ToList()
                };
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                Directory.CreateDirectory(Path.GetDirectoryName(options.OutPath) ?? ".");
                File.WriteAllText(options.OutPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                Console.WriteLine("Arquivo salvo: " + options.OutPath);
            }
        }

        private static SpacedFileStats ComputeSpacedStats(string file, SpacedOptions options)
        {
            var stats = new SpacedFileStats { File = file };
            using var reader = new PdfReader(file);
            using var doc = new PdfDocument(reader);

            var pageStart = options.Page > 0 ? options.Page : 1;
            var pageEnd = options.Page > 0 ? options.Page : doc.GetNumberOfPages();

            for (int p = pageStart; p <= pageEnd; p++)
            {
                var page = doc.GetPage(p);
                if (page == null) continue;

                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);

                IEnumerable<PdfStream> streams;
                if (options.Obj > 0)
                {
                    var found = FindStreamAndResourcesByObjId(doc, options.Obj);
                    streams = found.Stream != null ? new[] { found.Stream } : Array.Empty<PdfStream>();
                    if (found.Resources != null)
                        resources = found.Resources;
                }
                else
                {
                    streams = EnumerateStreams(contents);
                }

                foreach (var stream in streams)
                {
                    if (stream == null) continue;
                    int objId = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                    var blocks = ObjectsTextOpsDiff.ExtractPatternBlocks(stream, resources, options.OpFilter);
                    var paragraphs = BuildParagraphsFromBlocks(blocks);
                    int streamTokens = 0;
                    int streamSingles = 0;
                    int streamBlocks = paragraphs.Count;
                    int streamSpacedBlocks = paragraphs.Count(p => p.LineCount >= options.MinLines);

                    stats.TotalBlocks += streamBlocks;
                    stats.SpacedBlocks += streamSpacedBlocks;
                    foreach (var block in blocks)
                    {
                        var rawTokens = GetRawTokens(block);
                        if (rawTokens.Count == 0)
                            continue;

                        var singleCount = rawTokens.Count(t => t.Length == 1);
                        stats.TotalTokens += rawTokens.Count;
                        stats.SingleTokens += singleCount;
                        streamTokens += rawTokens.Count;
                        streamSingles += singleCount;
                    }

                    if (streamTokens > 0 && streamSingles > stats.BestAbsSingles)
                    {
                        stats.BestAbsSingles = streamSingles;
                        stats.BestAbsTokens = streamTokens;
                        stats.BestAbsBlocks = streamBlocks;
                        stats.BestAbsSpacedBlocks = streamSpacedBlocks;
                        stats.BestAbsObj = objId;
                        stats.BestAbsPage = p;
                    }
                }
            }

            return stats;
        }

        private static void RunPatternRaw(RawOptions options)
        {
            if (!File.Exists(options.Input))
            {
                Console.WriteLine("PDF nao encontrado: " + options.Input);
                return;
            }
            if (options.Page <= 0 || options.Obj <= 0)
            {
                Console.WriteLine("Informe --page e --obj.");
                return;
            }

            using var reader = new PdfReader(options.Input);
            using var doc = new PdfDocument(reader);
            var found = FindStreamAndResourcesByObjId(doc, options.Obj);
            if (found.Stream == null || found.Resources == null)
            {
                Console.WriteLine("Obj/stream nao encontrado.");
                return;
            }

            var bytes = ExtractStreamBytes(found.Stream);
            if (bytes.Length == 0)
            {
                Console.WriteLine("Stream vazio.");
                return;
            }

            var tokens = TokenizeContent(bytes);
            var textQueue = new Queue<string>(PdfTextExtraction.CollectTextOperatorTexts(found.Stream, found.Resources));
            var operands = new List<string>();
            var rawOps = new List<(int OpIndex, string Raw, string Decoded)>();
            var rawText = new StringBuilder();

            int opIndex = 0;
            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                opIndex++;
                var rawLine = operands.Count > 0 ? $"{string.Join(" ", operands)} {tok}" : tok;
                var decoded = "";
                if (IsTextShowingOperator(tok))
                    decoded = DequeueDecodedText(tok, operands, textQueue);

                if (IsTextShowingOperator(tok) && InRange(opIndex, options.OpRangeStart, options.OpRangeEnd))
                {
                    rawOps.Add((opIndex, rawLine, decoded));
                    rawText.Append(decoded);
                }

                operands.Clear();
            }

            Console.WriteLine($"PDF: {Path.GetFileName(options.Input)}");
            Console.WriteLine($"RAW ops: {rawOps.Count}");
            foreach (var item in rawOps)
            {
                Console.WriteLine($"op{item.OpIndex} {item.Raw} => \"{item.Decoded}\"");
            }

            var rawTextStr = rawText.ToString();
            var rawTokenList = rawOps.Select(o => o.Decoded).ToList();
            Console.WriteLine();
            Console.WriteLine("RAW_TEXT:");
            Console.WriteLine(rawTextStr);
            Console.WriteLine();
            Console.WriteLine("RAW_PAT:");
            Console.WriteLine(BuildSimplePatternSpacedRawTokens(rawTokenList));
            Console.WriteLine();
            var norm = NormalizePatternText(rawTextStr);
            Console.WriteLine("NORM_TEXT:");
            Console.WriteLine(norm);
            Console.WriteLine();
            Console.WriteLine("TYPED_PAT:");
            Console.WriteLine(BuildTypedPatternSpaced(rawTextStr));
        }

        private static void RunPatternTokens(TokensOptions options)
        {
            if (!File.Exists(options.Input))
            {
                Console.WriteLine("PDF nao encontrado: " + options.Input);
                return;
            }
            if (options.Page <= 0 || options.Obj <= 0)
            {
                Console.WriteLine("Informe --page e --obj.");
                return;
            }

            using var reader = new PdfReader(options.Input);
            using var doc = new PdfDocument(reader);
            var found = FindStreamAndResourcesByObjId(doc, options.Obj);
            if (found.Stream == null || found.Resources == null)
            {
                Console.WriteLine("Obj/stream nao encontrado.");
                return;
            }

            var bytes = ExtractStreamBytes(found.Stream);
            if (bytes.Length == 0)
            {
                Console.WriteLine("Stream vazio.");
                return;
            }

            var tokens = TokenizeContent(bytes);
            var textQueue = new Queue<string>(PdfTextExtraction.CollectTextOperatorTexts(found.Stream, found.Resources));
            var operands = new List<string>();
            var rawOps = new List<(int OpIndex, string Decoded)>();

            int opIndex = 0;
            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                {
                    operands.Add(tok);
                    continue;
                }

                opIndex++;
                var decoded = "";
                if (IsTextShowingOperator(tok))
                    decoded = DequeueDecodedText(tok, operands, textQueue);

                if (IsTextShowingOperator(tok) && InRange(opIndex, options.OpRangeStart, options.OpRangeEnd))
                    rawOps.Add((opIndex, decoded));

                operands.Clear();
            }

            var ones = rawOps.Where(t => t.Decoded.Length == 1 && !char.IsWhiteSpace(t.Decoded[0])).ToList();
            var ws = rawOps.Where(t => t.Decoded.Length > 1).ToList();

            Console.WriteLine($"PDF: {Path.GetFileName(options.Input)}");
            Console.WriteLine($"Page={options.Page} Obj={options.Obj}");
            Console.WriteLine($"TOKENS: {rawOps.Count}  (1={ones.Count} | W={ws.Count})");
            Console.WriteLine();

            Console.WriteLine("W tokens (len>=2):");
            if (ws.Count == 0)
            {
                Console.WriteLine("  (nenhum)");
            }
            else
            {
                foreach (var t in ws.Take(options.Max))
                    Console.WriteLine($"  op{t.OpIndex}: \"{t.Decoded}\"");
            }
            Console.WriteLine();

            Console.WriteLine("Sequencias de letras unitarias (espacadas):");
            var seqs = new List<(int StartOp, int EndOp, string Spaced, string Collapsed)>();
            var tokensSeq = new List<string>();
            var startOp = -1;
            var endOp = -1;
            foreach (var t in rawOps)
            {
                var s = t.Decoded;
                if (s.Length == 1 && (char.IsLetterOrDigit(s[0]) || s[0] == 'ª' || s[0] == 'º' || s[0] == '°'))
                {
                    if (tokensSeq.Count == 0) startOp = t.OpIndex;
                    tokensSeq.Add(s);
                    endOp = t.OpIndex;
                    continue;
                }
                if (s.Length == 1 && char.IsWhiteSpace(s[0]) && tokensSeq.Count > 0)
                {
                    // mantem espaco entre letras na sequencia
                    tokensSeq.Add("␠");
                    endOp = t.OpIndex;
                    continue;
                }

                var letterCount = tokensSeq.Count(l => l != "␠");
                if (letterCount >= options.MinSeq)
                {
                    var spaced = string.Join("·", tokensSeq).Trim();
                    var collapsed = string.Concat(tokensSeq.Where(l => l != "␠"));
                    seqs.Add((startOp, endOp, spaced, collapsed));
                }
                tokensSeq.Clear();
                startOp = -1;
                endOp = -1;
            }
            if (tokensSeq.Count(l => l != "␠") >= options.MinSeq)
            {
                var spaced = string.Join("·", tokensSeq).Trim();
                var collapsed = string.Concat(tokensSeq.Where(l => l != "␠"));
                seqs.Add((startOp, endOp, spaced, collapsed));
            }

            if (seqs.Count == 0)
            {
                Console.WriteLine("  (nenhuma)");
            }
            else
            {
                foreach (var s in seqs.Take(options.Max))
                    Console.WriteLine($"  op{s.StartOp}-op{s.EndOp}: \"{s.Spaced}\" -> \"{s.Collapsed}\"");
            }
            Console.WriteLine();
        }

        private sealed class DefectRule
        {
            public string Id { get; set; } = "";
            public string Label { get; set; } = "";
            public System.Text.RegularExpressions.Regex Regex { get; set; } = null!;
        }

        private sealed class DefectHit
        {
            public string Id { get; set; } = "";
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string Snippet { get; set; } = "";
        }

        private static void RunPatternDefects(DefectsOptions options)
        {
            if (!File.Exists(options.Input))
            {
                Console.WriteLine("PDF nao encontrado: " + options.Input);
                return;
            }
            if (options.Page <= 0 || options.Obj <= 0)
            {
                Console.WriteLine("Informe --page e --obj.");
                return;
            }

            var blocks = ExtractPatternBlocksFromPdf(options.Input, options.Page, options.Obj, options.OpFilter);
            if (blocks.Count == 0)
            {
                Console.WriteLine("Sem blocos.");
                return;
            }

            var rules = BuildDefectRules();
            var hitsByRule = rules.ToDictionary(r => r.Id, _ => new List<DefectHit>());

            int pageOnes = 0;
            int pageWs = 0;

            foreach (var block in blocks)
            {
                var raw = block.RawText ?? "";
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                CountPatternTokens(block.Pattern ?? "", out var ones, out var ws);
                pageOnes += ones;
                pageWs += ws;

                foreach (var rule in rules)
                {
                    if (hitsByRule[rule.Id].Count >= options.MaxPerType)
                        continue;

                    foreach (System.Text.RegularExpressions.Match m in rule.Regex.Matches(raw))
                    {
                        if (!m.Success)
                            continue;
                        if (hitsByRule[rule.Id].Count >= options.MaxPerType)
                            break;
                        var snippet = BuildSnippet(raw, m.Index, m.Length);
                        hitsByRule[rule.Id].Add(new DefectHit
                        {
                            Id = rule.Id,
                            StartOp = block.StartOp,
                            EndOp = block.EndOp,
                            Snippet = snippet
                        });
                    }
                }
            }

            Console.WriteLine($"PDF: {Path.GetFileName(options.Input)}");
            Console.WriteLine($"Page={options.Page} Obj={options.Obj} Blocos={blocks.Count}");
            var pageTotal = pageOnes + pageWs;
            var pagePct = pageTotal == 0 ? 0.0 : (pageOnes * 100.0 / pageTotal);
            Console.WriteLine($"RAW tokens (pagina): 1={pageOnes} W={pageWs} 1%={pagePct:0.0}%");
            Console.WriteLine();

            foreach (var rule in rules)
            {
                var hits = hitsByRule[rule.Id];
                Console.WriteLine($"{rule.Id} ({rule.Label}) hits={hits.Count}");
                if (hits.Count == 0)
                {
                    Console.WriteLine("  (nenhum)");
                    Console.WriteLine();
                    continue;
                }
                foreach (var hit in hits)
                {
                    var block = blocks.FirstOrDefault(b => b.StartOp == hit.StartOp && b.EndOp == hit.EndOp);
                    string ratio = "";
                    if (block != null)
                    {
                        CountPatternTokens(block.Pattern ?? "", out var ones, out var ws);
                        var total = ones + ws;
                        var pct = total == 0 ? 0.0 : (ones * 100.0 / total);
                        ratio = $" 1%={pct:0.0}%";
                    }
                    Console.WriteLine($"  op{hit.StartOp}-op{hit.EndOp}{ratio}: \"{hit.Snippet}\"");
                }
                Console.WriteLine();
            }
        }

        private static void CountPatternTokens(string pattern, out int ones, out int ws)
        {
            ones = 0;
            ws = 0;
            if (string.IsNullOrEmpty(pattern)) return;
            var tokens = ParsePatternTokens(pattern);
            foreach (var tok in tokens)
            {
                if (tok == "1") ones++;
                else if (tok == "W") ws++;
            }
        }

        private static List<DefectRule> BuildDefectRules()
        {
            return new List<DefectRule>
            {
                new DefectRule
                {
                    Id = "letras_espacadas",
                    Label = "letras/tokens unitarios com espaco curto (ex.: P O D E R)",
                    Regex = new System.Text.RegularExpressions.Regex(@"\b(?:[\p{L}\d](?:\s[\p{L}\d]){2,})\b")
                },
                new DefectRule
                {
                    Id = "pontuacao_sem_espaco",
                    Label = "pontuacao colada (ex.: Requerente:Juizo)",
                    Regex = new System.Text.RegularExpressions.Regex(@"[:;](?=\S)|,(?=\p{L})")
                },
                new DefectRule
                {
                    Id = "letra_numero_colado",
                    Label = "letra<->numero colados (ex.: Processo2021)",
                    Regex = new System.Text.RegularExpressions.Regex(@"(?:\p{L}\d(?![ªº°])|\d(?![ªº°])\p{L})")
                },
                new DefectRule
                {
                    Id = "parenteses_colado",
                    Label = "parenteses colados (texto)exemplo / texto(exemplo)",
                    Regex = new System.Text.RegularExpressions.Regex(@"(?:\p{L}\(|\)(?=\p{L}))")
                },
                new DefectRule
                {
                    Id = "hifenizacao",
                    Label = "quebra com hifen (peri- cia)",
                    Regex = new System.Text.RegularExpressions.Regex(@"\p{L}-\s+\p{L}")
                },
                new DefectRule
                {
                    Id = "particula_colada_upper",
                    Label = "particulas coladas em MAIUSCULO (JUIZODACOMARCA)",
                    Regex = new System.Text.RegularExpressions.Regex(@"(?<=\p{Lu}{2})(DA|DE|DO|DAS|DOS|EM|NO|NA|NOS|NAS|AO|AOS)(?=\p{Lu}{2})")
                }
            };
        }

        private static string BuildSnippet(string text, int index, int length, int ctx = 40)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var start = Math.Max(0, index - ctx);
            var end = Math.Min(text.Length, index + Math.Max(length, 1) + ctx);
            var slice = text.Substring(start, end - start);
            slice = slice.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            return TextUtils.NormalizeWhitespace(slice);
        }

        private sealed class TokenInfo
        {
            public string Text { get; set; } = "";
            public string Raw { get; set; } = "";
            public string BlockRawText { get; set; } = "";
            public string Pattern { get; set; } = "";
            public int BlockIndex { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public double? YNorm { get; set; }
            public double? XNorm { get; set; }
        }

        private sealed class FieldMatch
        {
            public string Field { get; set; } = "";
            public string Pdf { get; set; } = "";
            public string ValueText { get; set; } = "";
            public string ValuePattern { get; set; } = "";
            public string ValueExpectedPattern { get; set; } = "";
            public double Score { get; set; }
            public double PrevScore { get; set; }
            public double NextScore { get; set; }
            public double ValueScore { get; set; }
            public string PrevText { get; set; } = "";
            public string NextText { get; set; } = "";
            public string PrevExpectedPattern { get; set; } = "";
            public string PrevActualPattern { get; set; } = "";
            public double PrevPatternScore { get; set; }
            public string PrevExpectedText { get; set; } = "";
            public string PrevActualText { get; set; } = "";
            public double PrevTextScore { get; set; }
            public string NextExpectedPattern { get; set; } = "";
            public string NextActualPattern { get; set; } = "";
            public double NextPatternScore { get; set; }
            public string NextExpectedText { get; set; } = "";
            public string NextActualText { get; set; } = "";
            public double NextTextScore { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string Band { get; set; } = "";
            public string YRange { get; set; } = "";
            public string XRange { get; set; } = "";
            public string Kind { get; set; } = "";
            public string PrevMode { get; set; } = "";
            public string NextMode { get; set; } = "";
        }

        private static FieldMatch CloneMatch(FieldMatch m)
        {
            return new FieldMatch
            {
                Field = m.Field,
                Pdf = m.Pdf,
                ValueText = m.ValueText,
                ValuePattern = m.ValuePattern,
                ValueExpectedPattern = m.ValueExpectedPattern,
                Score = m.Score,
                PrevScore = m.PrevScore,
                NextScore = m.NextScore,
                ValueScore = m.ValueScore,
                PrevText = m.PrevText,
                NextText = m.NextText,
                PrevExpectedPattern = m.PrevExpectedPattern,
                PrevActualPattern = m.PrevActualPattern,
                PrevPatternScore = m.PrevPatternScore,
                PrevExpectedText = m.PrevExpectedText,
                PrevActualText = m.PrevActualText,
                PrevTextScore = m.PrevTextScore,
                NextExpectedPattern = m.NextExpectedPattern,
                NextActualPattern = m.NextActualPattern,
                NextPatternScore = m.NextPatternScore,
                NextExpectedText = m.NextExpectedText,
                NextActualText = m.NextActualText,
                NextTextScore = m.NextTextScore,
                StartOp = m.StartOp,
                EndOp = m.EndOp,
                Band = m.Band,
                YRange = m.YRange,
                XRange = m.XRange,
                Kind = m.Kind,
                PrevMode = m.PrevMode,
                NextMode = m.NextMode
            };
        }

        private sealed class FieldReject
        {
            public string Field { get; set; } = "";
            public string Pdf { get; set; } = "";
            public string Reason { get; set; } = "";
            public string ValueText { get; set; } = "";
            public string ValuePattern { get; set; } = "";
            public string ValueExpectedPattern { get; set; } = "";
            public double Score { get; set; }
            public double PrevScore { get; set; }
            public double NextScore { get; set; }
            public double ValueScore { get; set; }
            public string PrevText { get; set; } = "";
            public string NextText { get; set; } = "";
            public string PrevExpectedPattern { get; set; } = "";
            public string PrevActualPattern { get; set; } = "";
            public double PrevPatternScore { get; set; }
            public string PrevExpectedText { get; set; } = "";
            public string PrevActualText { get; set; } = "";
            public double PrevTextScore { get; set; }
            public string NextExpectedPattern { get; set; } = "";
            public string NextActualPattern { get; set; } = "";
            public double NextPatternScore { get; set; }
            public string NextExpectedText { get; set; } = "";
            public string NextActualText { get; set; } = "";
            public double NextTextScore { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string Band { get; set; } = "";
            public string YRange { get; set; } = "";
            public string XRange { get; set; } = "";
            public string Kind { get; set; } = "";
            public string PrevMode { get; set; } = "";
            public string NextMode { get; set; } = "";
        }

        private sealed class FieldStats
        {
            public string Field { get; set; } = "";
            public int Total { get; set; }
            public int Hits { get; set; }
            public int Misses { get; set; }
            public int Multi { get; set; }
            public double Sensitivity { get; set; }
            public double Specificity { get; set; }
        }

        private static void RunPatternMatch(MatchOptions options)
        {
            var prevTextOpsTimeout = PdfTextExtraction.TimeoutSec;
            if (options.TimeoutSec > 0)
                PdfTextExtraction.TimeoutSec = options.TimeoutSec;
            try
            {
            if (options.AutoScan)
            {
                options.Page = 0;
                options.Obj = 0;
            }
            var entries = LoadPatternEntries(options.PatternsPath, options.Fields);
            if (entries.Count == 0)
            {
                Console.WriteLine("Nenhum pattern encontrado.");
                return;
            }
            EnsureRegexCatalog(options.PatternsPath, options.Log);
            EnableDmpLog(options.Log);
            var progress = ProgressReporter.FromConfig("match", options.Inputs.Count);

            if (!string.IsNullOrWhiteSpace(options.PairsPath))
            {
                RunPatternMatchFromPairs(options, entries);
                return;
            }

            if (options.Page <= 0 || options.Obj <= 0)
            {
                RunPatternMatchAuto(options, entries, progress);
                return;
            }

            var groups = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Field))
                .GroupBy(e => e.Field!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var pdf in options.Inputs)
            {
                if (!File.Exists(pdf))
                {
                    Console.WriteLine("PDF nao encontrado: " + pdf);
                    progress?.Tick(Path.GetFileName(pdf));
                    continue;
                }
                var ordered = options.Fields.Count > 0 ? options.Fields.ToList() : groups.Select(g => g.Key).ToList();
                RunPatternMatchForObject(options, pdf, options.Page, options.Obj, groups, ordered, new List<string>(), new List<string>());
                progress?.Tick(Path.GetFileName(pdf));
                // libera memoria entre PDFs
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            }
            finally
            {
                PdfTextExtraction.TimeoutSec = prevTextOpsTimeout;
            }
        }

        internal static void RunPatternMatchForPages(
            string patternsDoc,
            string pdf,
            int page1,
            int obj1,
            int page2,
            int obj2,
            bool log)
        {
            var options = new MatchOptions();
            var defaults = LoadPatternMatchDefaults();
            if (!string.IsNullOrWhiteSpace(defaults.Patterns))
                options.PatternsPath = defaults.Patterns!;
            if (defaults.MinScore.HasValue)
                options.MinScore = defaults.MinScore.Value;
            if (defaults.MaxPairs.HasValue && defaults.MaxPairs.Value > 0)
                options.MaxPairs = defaults.MaxPairs.Value;
            if (defaults.MaxCandidates.HasValue && defaults.MaxCandidates.Value > 0)
                options.MaxCandidates = defaults.MaxCandidates.Value;
            if (defaults.MinStreamRatio.HasValue)
                options.MinStreamRatio = defaults.MinStreamRatio.Value;
            if (defaults.Log.HasValue)
                options.Log = defaults.Log.Value;

            if (!string.IsNullOrWhiteSpace(patternsDoc))
                options.PatternsPath = ResolvePatternPath(patternsDoc);

            options.Log = log;
            options.Inputs.Add(pdf);
            if (options.OpFilter.Count == 0)
            {
                options.OpFilter.Add("Tj");
                options.OpFilter.Add("TJ");
            }

            var entries = LoadPatternEntries(options.PatternsPath, options.Fields);
            if (entries.Count == 0)
            {
                Console.WriteLine("Nenhum pattern encontrado.");
                return;
            }
            EnsureRegexCatalog(options.PatternsPath, options.Log);
            EnableDmpLog(options.Log);

            var optionalFields = ReadFieldListFromPatterns(options.PatternsPath, "optional_fields", "optionalFields", "optional");
            var page1Fields = ReadFieldListFromPatterns(options.PatternsPath, "page1_fields", "page1Fields", "p1_fields", "page1").ToList();
            var page2Fields = ReadFieldListFromPatterns(options.PatternsPath, "page2_fields", "page2Fields", "p2_fields", "page2").ToList();
            var p1TopAnchors = ReadFieldListFromPatterns(options.PatternsPath, "page1_anchor_top", "page1AnchorTop", "p1_anchor_top").ToList();
            var p1BottomAnchors = ReadFieldListFromPatterns(options.PatternsPath, "page1_anchor_bottom", "page1AnchorBottom", "p1_anchor_bottom").ToList();
            var p2TopAnchors = ReadFieldListFromPatterns(options.PatternsPath, "page2_anchor_top", "page2AnchorTop", "p2_anchor_top").ToList();
            var p2BottomAnchors = ReadFieldListFromPatterns(options.PatternsPath, "page2_anchor_bottom", "page2AnchorBottom", "p2_anchor_bottom").ToList();

            var groups = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Field))
                .GroupBy(e => e.Field!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine($"PDF: {Path.GetFileName(pdf)}");
            Console.WriteLine($"OBJ: p{page1} obj={obj1}");
            var orderedA = page1Fields.Count > 0 ? page1Fields : groups.Select(g => g.Key).ToList();
            AppendOptionalFields(orderedA, optionalFields);
            var topA = p1TopAnchors.Count > 0 ? p1TopAnchors : PickTopAnchors(page1Fields);
            var bottomA = p1BottomAnchors.Count > 0 ? p1BottomAnchors : PickBottomAnchors(page1Fields);
            var fieldsA = RunPatternMatchForObject(options, pdf, page1, obj1, groups, orderedA, topA, bottomA, skipHeader: true);

            var fieldsB = new Dictionary<string, List<FieldMatch>>(StringComparer.OrdinalIgnoreCase);
            if (page2 > 0 && obj2 > 0)
            {
                Console.WriteLine($"OBJ: p{page2} obj={obj2}");
                var orderedB = page2Fields.Count > 0 ? page2Fields : groups.Select(g => g.Key).ToList();
                AppendOptionalFields(orderedB, optionalFields);
                var topB = p2TopAnchors.Count > 0 ? p2TopAnchors : PickTopAnchors(page2Fields);
                var bottomB = p2BottomAnchors.Count > 0 ? p2BottomAnchors : PickBottomAnchors(page2Fields);

                Dictionary<string, List<FieldMatch>>? extraB = null;
                if (orderedB.Contains("VALOR_ARBITRADO_DE", StringComparer.OrdinalIgnoreCase))
                {
                    if (fieldsA.TryGetValue("VALOR_ARBITRADO_JZ", out var jzList) && jzList.Count > 0)
                    {
                        var tokensB = ExtractTokensFromPdf(pdf, page2, obj2, options.OpFilter);
                        var rawB = ExtractRawTokensFromPdf(pdf, page2, obj2, options.OpFilter);
                        if (!HasGeorcMention(tokensB, rawB))
                        {
                            var derived = DeriveFieldMatch(jzList[0], "VALOR_ARBITRADO_DE", "derived_from_jz");
                            extraB = new Dictionary<string, List<FieldMatch>>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["VALOR_ARBITRADO_DE"] = new List<FieldMatch> { derived }
                            };
                        }
                    }
                }
                if (orderedB.Contains("PROCESSO_JUDICIAL", StringComparer.OrdinalIgnoreCase))
                {
                    if (fieldsA.TryGetValue("PROCESSO_JUDICIAL", out var pjList) && pjList.Count > 0)
                    {
                        extraB ??= new Dictionary<string, List<FieldMatch>>(StringComparer.OrdinalIgnoreCase);
                        extraB["PROCESSO_JUDICIAL"] = new List<FieldMatch> { DeriveFieldMatch(pjList[0], "PROCESSO_JUDICIAL", "derived_from_p1") };
                    }
                }
                if (orderedB.Contains("PERITO", StringComparer.OrdinalIgnoreCase))
                {
                    if (fieldsA.TryGetValue("PERITO", out var perList) && perList.Count > 0)
                    {
                        extraB ??= new Dictionary<string, List<FieldMatch>>(StringComparer.OrdinalIgnoreCase);
                        extraB["PERITO"] = new List<FieldMatch> { DeriveFieldMatch(perList[0], "PERITO", "derived_from_p1") };
                    }
                }

                fieldsB = RunPatternMatchForObject(options, pdf, page2, obj2, groups, orderedB, topB, bottomB, skipHeader: true, extraFields: extraB);
            }

            var values = BuildValuesFromMatches(orderedA, page2Fields, fieldsA, fieldsB);
            TryBackfillPartiesFromObjectTexts(options, pdf, page1, obj1, page2, obj2, values);
            if (orderedA.Contains("PROCESSO_ADMINISTRATIVO", StringComparer.OrdinalIgnoreCase) ||
                page2Fields.Contains("PROCESSO_ADMINISTRATIVO", StringComparer.OrdinalIgnoreCase))
            {
                TryFillProcessoAdministrativoFromPdfName(pdf, values);
            }
            ApplyHonorariosDerivedFields(options, orderedA, fieldsA, values);
            PrintFieldSummary(options, orderedA, new HashSet<string>(page2Fields, StringComparer.OrdinalIgnoreCase), optionalFields, fieldsA, fieldsB, values);
            PrintValidatorSummary(options, values);
            PrintHonorariosSummary(options, values);
            if (ShouldRejectByValidator(values, optionalFields, options.PatternsPath, out var rejectReason))
            {
                LogStep(options, CRed, "[REJECT]", $"pdf={Path.GetFileName(pdf)} reason={rejectReason}");
                return;
            }
            PrintWinnerStats(options);

            return;
        }

        internal static void RunPatternMatchAutoForDoc(
            string patternsDoc,
            string pdf,
            bool log,
            int shortcutTop = 3,
            bool noShortcut = false,
            bool requireAll = false)
        {
            var options = new MatchOptions();
            var defaults = LoadPatternMatchDefaults();
            if (!string.IsNullOrWhiteSpace(defaults.Patterns))
                options.PatternsPath = defaults.Patterns!;
            if (defaults.MinScore.HasValue)
                options.MinScore = defaults.MinScore.Value;
            if (defaults.MaxPairs.HasValue && defaults.MaxPairs.Value > 0)
                options.MaxPairs = defaults.MaxPairs.Value;
            if (defaults.MaxCandidates.HasValue && defaults.MaxCandidates.Value > 0)
                options.MaxCandidates = defaults.MaxCandidates.Value;
            if (defaults.MinStreamRatio.HasValue)
                options.MinStreamRatio = defaults.MinStreamRatio.Value;
            if (defaults.Log.HasValue)
                options.Log = defaults.Log.Value;

            if (!string.IsNullOrWhiteSpace(patternsDoc))
                options.PatternsPath = ResolvePatternPath(patternsDoc);

            options.Log = log;
            options.Inputs.Add(pdf);
            options.AutoScan = true;
            options.NoShortcut = noShortcut;
            options.RequireAll = requireAll;
            if (shortcutTop > 0)
                options.ShortcutTop = shortcutTop;
            if (options.OpFilter.Count == 0)
            {
                options.OpFilter.Add("Tj");
                options.OpFilter.Add("TJ");
            }

            var entries = LoadPatternEntries(options.PatternsPath, options.Fields);
            if (entries.Count == 0)
            {
                Console.WriteLine("Nenhum pattern encontrado.");
                return;
            }
            EnsureRegexCatalog(options.PatternsPath, options.Log);
            EnableDmpLog(options.Log);
            RunPatternMatchAuto(options, entries, null);
        }

        internal static double ComputePatternSignal(string patternsDoc, string pdf, int page, int obj)
        {
            if (string.IsNullOrWhiteSpace(pdf) || page <= 0 || obj <= 0)
                return 0.0;

            var options = new MatchOptions();
            var defaults = LoadPatternMatchDefaults();
            if (!string.IsNullOrWhiteSpace(defaults.Patterns))
                options.PatternsPath = defaults.Patterns!;
            if (defaults.MinScore.HasValue)
                options.MinScore = defaults.MinScore.Value;
            if (defaults.MaxPairs.HasValue && defaults.MaxPairs.Value > 0)
                options.MaxPairs = defaults.MaxPairs.Value;
            if (defaults.MaxCandidates.HasValue && defaults.MaxCandidates.Value > 0)
                options.MaxCandidates = defaults.MaxCandidates.Value;
            if (defaults.MinStreamRatio.HasValue)
                options.MinStreamRatio = defaults.MinStreamRatio.Value;

            if (!string.IsNullOrWhiteSpace(patternsDoc))
                options.PatternsPath = ResolvePatternPath(patternsDoc);

            options.Log = false;
            if (options.OpFilter.Count == 0)
            {
                options.OpFilter.Add("Tj");
                options.OpFilter.Add("TJ");
            }

            var entries = LoadPatternEntries(options.PatternsPath, options.Fields);
            if (entries.Count == 0)
                return 0.0;

            var optionalFields = ReadFieldListFromPatterns(options.PatternsPath, "optional_fields", "optionalFields", "optional");
            var detectP1List = ReadFieldListFromPatterns(options.PatternsPath, "detect_page1_fields", "detectPage1Fields", "detect_p1_fields", "detect_page1", "detect_p1").ToList();
            var detectP2List = ReadFieldListFromPatterns(options.PatternsPath, "detect_page2_fields", "detectPage2Fields", "detect_p2_fields", "detect_page2", "detect_p2").ToList();

            var allFieldNames = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Field))
                .Select(e => e.Field!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var detectFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (detectP1List.Count == 1 && detectP1List[0] == "*")
            {
                detectFields.UnionWith(allFieldNames);
            }
            else
            {
                foreach (var f in detectP1List)
                    detectFields.Add(f);
            }
            if (detectP2List.Count == 1 && detectP2List[0] == "*")
            {
                detectFields.UnionWith(allFieldNames);
            }
            else
            {
                foreach (var f in detectP2List)
                    detectFields.Add(f);
            }

            if (detectFields.Count == 0)
                detectFields.UnionWith(allFieldNames);

            detectFields = RemoveOptional(detectFields, optionalFields);

            var groups = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Field))
                .GroupBy(e => e.Field!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var ordered = detectFields.ToList();
            Dictionary<string, List<FieldMatch>> matches;
            TextWriter? prevOut = null;
            try
            {
                prevOut = Console.Out;
                Console.SetOut(TextWriter.Null);
                matches = RunPatternMatchForObject(options, pdf, page, obj, groups, ordered, new List<string>(), new List<string>(), skipHeader: true);
            }
            finally
            {
                if (prevOut != null)
                    Console.SetOut(prevOut);
            }
            if (ordered.Count == 0)
                return 0.0;

            int hits = 0;
            foreach (var field in ordered)
            {
                if (matches.TryGetValue(field, out var list) && list.Count > 0)
                    hits++;
            }
            return Math.Min(1.0, hits / (double)ordered.Count);
        }

        private sealed class MatchPick
        {
            public int Page { get; set; }
            public int Obj { get; set; }
        }

        private sealed class MatchPickPair
        {
            public MatchPick A { get; set; } = new MatchPick();
            public MatchPick B { get; set; } = new MatchPick();
            public bool HasPair { get; set; }
        }

        private static void RunPatternMatchAuto(MatchOptions options, List<FieldPatternEntry> entries, ProgressReporter? progress)
        {
            EnableDmpLog(options.Log);
            var docName = ReadDocNameFromPatterns(options.PatternsPath);
            var optionalFields = ReadFieldListFromPatterns(options.PatternsPath, "optional_fields", "optionalFields", "optional");
            var requiredAll = BuildRequiredFieldSet(entries, options.Fields, optionalFields);
            var groupsAll = BuildGroups(entries, requiredAll);
            var rejectFields = ReadFieldListFromPatterns(options.PatternsPath, "reject_fields", "rejectFields", "reject");
            var groupsReject = rejectFields.Count > 0 ? BuildGroups(entries, rejectFields) : new List<IGrouping<string, FieldPatternEntry>>();
            var rejectTexts = ReadFieldListFromPatterns(options.PatternsPath, "reject_texts", "rejectTexts", "reject_text", "rejectText");
            DocumentValidationRules.EnsureDespachoRejectTexts(rejectTexts, docName);

            var page1Fields = ReadFieldListFromPatterns(options.PatternsPath, "page1_fields", "page1Fields", "p1_fields", "page1").ToList();
            var page2Fields = ReadFieldListFromPatterns(options.PatternsPath, "page2_fields", "page2Fields", "p2_fields", "page2").ToList();
            var detectP1List = ReadFieldListFromPatterns(options.PatternsPath, "detect_page1_fields", "detectPage1Fields", "detect_p1_fields", "detect_page1", "detect_p1").ToList();
            var detectP2List = ReadFieldListFromPatterns(options.PatternsPath, "detect_page2_fields", "detectPage2Fields", "detect_p2_fields", "detect_page2", "detect_p2").ToList();
            var page1FieldSet = page1Fields.Count > 0
                ? new HashSet<string>(page1Fields, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var page2FieldSet = page2Fields.Count > 0
                ? new HashSet<string>(page2Fields, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var page1Required = page1FieldSet.Count > 0
                ? RemoveOptional(page1FieldSet, optionalFields)
                : requiredAll;
            var page2Required = page2FieldSet.Count > 0
                ? RemoveOptional(page2FieldSet, optionalFields)
                : requiredAll;
            var groupsP1 = page1Required.Count > 0 ? BuildGroups(entries, page1Required) : groupsAll;
            var groupsP2 = page2Required.Count > 0 ? BuildGroups(entries, page2Required) : groupsAll;

            var p1TopAnchors = ReadFieldListFromPatterns(options.PatternsPath, "page1_anchor_top", "page1Top", "p1_top").ToList();
            var p1BottomAnchors = ReadFieldListFromPatterns(options.PatternsPath, "page1_anchor_bottom", "page1Bottom", "p1_bottom").ToList();
            var p2TopAnchors = ReadFieldListFromPatterns(options.PatternsPath, "page2_anchor_top", "page2Top", "p2_top").ToList();
            var p2BottomAnchors = ReadFieldListFromPatterns(options.PatternsPath, "page2_anchor_bottom", "page2Bottom", "p2_bottom").ToList();
            var allFieldNames = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Field))
                .Select(e => e.Field!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var detectP1Fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (detectP1List.Count > 0)
            {
                if (detectP1List.Count == 1 && detectP1List[0] == "*")
                    detectP1Fields = RemoveOptional(new HashSet<string>(allFieldNames, StringComparer.OrdinalIgnoreCase), optionalFields);
                else
                    detectP1Fields = RemoveOptional(new HashSet<string>(detectP1List, StringComparer.OrdinalIgnoreCase), optionalFields);
            }
            else
            {
                detectP1Fields.UnionWith(p1TopAnchors);
                detectP1Fields.UnionWith(p1BottomAnchors);
                if (detectP1Fields.Count == 0)
                    detectP1Fields = new HashSet<string>(page1Required, StringComparer.OrdinalIgnoreCase);
            }
            var detectP2Fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (detectP2List.Count > 0)
            {
                if (detectP2List.Count == 1 && detectP2List[0] == "*")
                    detectP2Fields = RemoveOptional(new HashSet<string>(allFieldNames, StringComparer.OrdinalIgnoreCase), optionalFields);
                else
                    detectP2Fields = RemoveOptional(new HashSet<string>(detectP2List, StringComparer.OrdinalIgnoreCase), optionalFields);
            }
            else
            {
                detectP2Fields.UnionWith(p2TopAnchors);
                detectP2Fields.UnionWith(p2BottomAnchors);
                if (detectP2Fields.Count == 0)
                    detectP2Fields = new HashSet<string>(page2Required, StringComparer.OrdinalIgnoreCase);
            }
            var groupsDetectP1 = detectP1Fields.Count > 0 ? BuildGroups(entries, detectP1Fields) : groupsP1;
            var groupsDetectP2 = detectP2Fields.Count > 0 ? BuildGroups(entries, detectP2Fields) : groupsP2;
            var useOverallScan = page1Fields.Count == 0 && page2Fields.Count == 0;
            var useDespachoShortcut = docName.Equals("tjpb_despacho", StringComparison.OrdinalIgnoreCase) ||
                                      docName.Equals(DocumentValidationRules.DocKeyDespacho, StringComparison.OrdinalIgnoreCase);
            if (options.NoShortcut || options.RequireAll)
                useDespachoShortcut = false;
            var roiDoc = string.IsNullOrWhiteSpace(docName) ? "tjpb_despacho" : docName;
            var detectDocKey = DocumentValidationRules.ResolveDocKeyForDetection(docName);
            var isDespachoDoc = DocumentValidationRules.IsDocMatch(detectDocKey, DocumentValidationRules.DocKeyDespacho);
            var isCertidaoPattern = DocumentValidationRules.IsDocMatch(detectDocKey, DocumentValidationRules.DocKeyCertidaoConselho);
            var isRequerimentoPattern = DocumentValidationRules.IsDocMatch(detectDocKey, DocumentValidationRules.DocKeyRequerimentoHonorarios);
            // All known doc types must pass validator-based doc confirmation.
            // This prevents accepting the first weak hit from detectdoc.
            var strictDocValidation = isCertidaoPattern || isRequerimentoPattern || isDespachoDoc;
            // Pattern match e rota de extracao de campos; nao executa deteccao documental.
            var runDetectDoc = false;
            options.Jobs = Math.Max(1, Math.Min(options.Jobs, Math.Max(1, Environment.ProcessorCount)));
            var canRunParallel = options.Jobs > 1 && options.Inputs.Count > 1 && !options.Log;
            LogStep(options, CMagenta, "[CONFIG]", $"patterns={Path.GetFileName(options.PatternsPath)} docName={docName} detectDocKey={detectDocKey} strict={strictDocValidation} runDetectDoc={runDetectDoc} p1Fields={page1Fields.Count} p2Fields={page2Fields.Count} optional={optionalFields.Count} p1Top={p1TopAnchors.Count} p1Bottom={p1BottomAnchors.Count} p2Top={p2TopAnchors.Count} p2Bottom={p2BottomAnchors.Count} p1Detect={detectP1Fields.Count} p2Detect={detectP2Fields.Count} overallScan={useOverallScan} minScore={options.MinScore:0.00} maxPairs={options.MaxPairs} maxCandidates={options.MaxCandidates} noShortcut={options.NoShortcut} requireAll={options.RequireAll} jobs={options.Jobs} parallel={canRunParallel}");

            List<Dictionary<string, object?>>? autoResults = null;
            if (!string.IsNullOrWhiteSpace(options.OutPath))
                autoResults = new List<Dictionary<string, object?>>();
            var autoResultsLock = new object();

            void ForceGcBetweenPdfs()
            {
                // Disabled: forced full GC here was causing long stalls/hangs in single-file runs.
                return;
            }

            void AddEmptyAutoResult(string pdf)
            {
                if (autoResults == null)
                    return;

                lock (autoResultsLock)
                {
                    autoResults.Add(new Dictionary<string, object?>
                    {
                        ["pdf"] = pdf,
                        ["page1"] = 0,
                        ["obj1"] = 0,
                        ["page2"] = 0,
                        ["obj2"] = 0,
                        ["values"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    });
                }
            }

            List<string> GetMissingRequiredFields(Dictionary<string, string> values)
            {
                return DocumentValidationRules.GetMissingRequiredFields(
                    strictDocValidation,
                    strictDocValidation,
                    requiredAll,
                    values);
            }

            bool ProcessPick(string pdf, MatchPickPair pick, bool allowFallbackOnReject = false)
            {
                var groups = entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.Field))
                    .GroupBy(e => e.Field!, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (!canRunParallel)
                {
                    Console.WriteLine($"PDF: {Path.GetFileName(pdf)}");
                    Console.WriteLine($"OBJ: p{pick.A.Page} obj={pick.A.Obj}");
                }
                var orderedA = page1Fields.Count > 0 ? page1Fields : groups.Select(g => g.Key).ToList();
                AppendOptionalFields(orderedA, optionalFields);
                var topA = p1TopAnchors.Count > 0 ? p1TopAnchors : PickTopAnchors(page1Fields);
                var bottomA = p1BottomAnchors.Count > 0 ? p1BottomAnchors : PickBottomAnchors(page1Fields);
                var fieldsA = RunPatternMatchForObject(options, pdf, pick.A.Page, pick.A.Obj, groups, orderedA, topA, bottomA, skipHeader: true);
                Dictionary<string, List<FieldMatch>> fieldsB = new(StringComparer.OrdinalIgnoreCase);
                if (pick.HasPair && pick.B.Page > 0 && pick.B.Obj > 0)
                {
                    if (!canRunParallel)
                        Console.WriteLine($"OBJ: p{pick.B.Page} obj={pick.B.Obj}");
                    var orderedB = page2Fields.Count > 0 ? page2Fields : groups.Select(g => g.Key).ToList();
                    AppendOptionalFields(orderedB, optionalFields);
                    var topB = p2TopAnchors.Count > 0 ? p2TopAnchors : PickTopAnchors(page2Fields);
                    var bottomB = p2BottomAnchors.Count > 0 ? p2BottomAnchors : PickBottomAnchors(page2Fields);
                    Dictionary<string, List<FieldMatch>>? extraB = null;
                    if (orderedB.Contains("VALOR_ARBITRADO_DE", StringComparer.OrdinalIgnoreCase))
                    {
                        if (fieldsA.TryGetValue("VALOR_ARBITRADO_JZ", out var jzList) && jzList.Count > 0)
                        {
                            var tokensB = ExtractTokensFromPdf(pdf, pick.B.Page, pick.B.Obj, options.OpFilter);
                            var rawB = ExtractRawTokensFromPdf(pdf, pick.B.Page, pick.B.Obj, options.OpFilter);
                            if (!HasGeorcMention(tokensB, rawB))
                            {
                                var derived = DeriveFieldMatch(jzList[0], "VALOR_ARBITRADO_DE", "derived_from_jz");
                                extraB = new Dictionary<string, List<FieldMatch>>(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["VALOR_ARBITRADO_DE"] = new List<FieldMatch> { derived }
                                };
                            }
                        }
                    }
                    if (orderedB.Contains("PROCESSO_JUDICIAL", StringComparer.OrdinalIgnoreCase))
                    {
                        if (fieldsA.TryGetValue("PROCESSO_JUDICIAL", out var pjList) && pjList.Count > 0)
                        {
                            extraB ??= new Dictionary<string, List<FieldMatch>>(StringComparer.OrdinalIgnoreCase);
                            extraB["PROCESSO_JUDICIAL"] = new List<FieldMatch> { DeriveFieldMatch(pjList[0], "PROCESSO_JUDICIAL", "derived_from_p1") };
                        }
                    }
                    if (orderedB.Contains("PERITO", StringComparer.OrdinalIgnoreCase))
                    {
                        if (fieldsA.TryGetValue("PERITO", out var perList) && perList.Count > 0)
                        {
                            extraB ??= new Dictionary<string, List<FieldMatch>>(StringComparer.OrdinalIgnoreCase);
                            extraB["PERITO"] = new List<FieldMatch> { DeriveFieldMatch(perList[0], "PERITO", "derived_from_p1") };
                        }
                    }
                    fieldsB = RunPatternMatchForObject(options, pdf, pick.B.Page, pick.B.Obj, groups, orderedB, topB, bottomB, skipHeader: true, extraFields: extraB);
                }
                var values = BuildValuesFromMatches(orderedA, page2Fields, fieldsA, fieldsB);
                TryBackfillPartiesFromObjectTexts(options, pdf, pick.A.Page, pick.A.Obj, pick.HasPair ? pick.B.Page : 0, pick.HasPair ? pick.B.Obj : 0, values);
                if (orderedA.Contains("PROCESSO_ADMINISTRATIVO", StringComparer.OrdinalIgnoreCase) ||
                    page2Fields.Contains("PROCESSO_ADMINISTRATIVO", StringComparer.OrdinalIgnoreCase))
                {
                    TryFillProcessoAdministrativoFromPdfName(pdf, values);
                }
                ApplyHonorariosDerivedFields(options, orderedA, fieldsA, values);
                ApplyDocumentWideFallbackValues(options, pdf, orderedA, page2Fields, values);
                ApplyHonorariosDerivedFields(options, orderedA, fieldsA, values);
                PrintFieldSummary(options, orderedA, page2FieldSet, optionalFields, fieldsA, fieldsB, values);
                if (!canRunParallel)
                {
                    PrintValidatorSummary(options, values);
                    PrintHonorariosSummary(options, values);
                }

                if (strictDocValidation && !string.IsNullOrWhiteSpace(detectDocKey))
                {
                    var fullA = BuildFullTextFromBlocks(pdf, pick.A.Page, pick.A.Obj, options.OpFilter);
                    var fullB = pick.HasPair && pick.B.Page > 0 && pick.B.Obj > 0
                        ? BuildFullTextFromBlocks(pdf, pick.B.Page, pick.B.Obj, options.OpFilter)
                        : "";
                    var combined = TextNormalization.NormalizeWhitespace($"{fullA} {fullB}");

                    if (!string.IsNullOrWhiteSpace(combined))
                    {
                        var guardPass = DocumentValidationRules.IsTargetGuardPass(detectDocKey, fullA, combined);
                        var strongPass = DocumentValidationRules.IsTargetStrongPass(detectDocKey, combined);
                        var otherStrong = DocumentValidationRules.IsOtherDocStrongAgainstTarget(detectDocKey, combined, fullA, combined);
                        if (!guardPass && (!strongPass || otherStrong))
                        {
                            var docReason = $"doc_guard_fail:target={detectDocKey};guard={guardPass};strong={strongPass};other={otherStrong}";
                            LogStep(options, CRed, "[REJECT]", $"pdf={Path.GetFileName(pdf)} reason={docReason}");
                            if (allowFallbackOnReject)
                                return false;

                            if (!canRunParallel)
                                Console.WriteLine();
                            AddEmptyAutoResult(pdf);
                            progress?.Tick(Path.GetFileName(pdf));
                            ForceGcBetweenPdfs();
                            return false;
                        }
                    }
                }

                var missingRequired = GetMissingRequiredFields(values);
                if (missingRequired.Count > 0)
                {
                    var missingReason = "missing_required:" + string.Join("|", missingRequired);
                    LogStep(options, CRed, "[REJECT]", $"pdf={Path.GetFileName(pdf)} reason={missingReason}");
                    if (allowFallbackOnReject)
                        return false;

                    if (!canRunParallel)
                        Console.WriteLine();
                    AddEmptyAutoResult(pdf);
                    progress?.Tick(Path.GetFileName(pdf));
                    ForceGcBetweenPdfs();
                    return false;
                }

                if (strictDocValidation &&
                    ShouldRejectByValidator(values, optionalFields, options.PatternsPath, out var rejectReason))
                {
                    LogStep(options, CRed, "[REJECT]", $"pdf={Path.GetFileName(pdf)} reason={rejectReason}");
                    if (allowFallbackOnReject)
                        return false;

                    if (!canRunParallel)
                        Console.WriteLine();
                    AddEmptyAutoResult(pdf);
                    progress?.Tick(Path.GetFileName(pdf));
                    ForceGcBetweenPdfs();
                    return false;
                }
                PrintWinnerStats(options);
                if (!canRunParallel)
                    Console.WriteLine();

                if (autoResults != null)
                {
                    var row = new Dictionary<string, object?>
                    {
                        ["pdf"] = pdf,
                        ["page1"] = pick.A.Page,
                        ["obj1"] = pick.A.Obj,
                        ["page2"] = pick.HasPair ? pick.B.Page : 0,
                        ["obj2"] = pick.HasPair ? pick.B.Obj : 0,
                        ["values"] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase)
                    };
                    lock (autoResultsLock)
                        autoResults.Add(row);
                }
                progress?.Tick(Path.GetFileName(pdf));
                ForceGcBetweenPdfs();
                return true;
            }

            bool IsTimedOut(Stopwatch? sw)
            {
                if (sw == null || options.TimeoutSec <= 0)
                    return false;
                return sw.Elapsed.TotalSeconds > options.TimeoutSec;
            }

            void ProcessPdfInput(string pdf)
            {
                var sw = options.TimeoutSec > 0 ? Stopwatch.StartNew() : null;
                var timedOut = false;

                if (!File.Exists(pdf))
                {
                    Console.WriteLine("PDF nao encontrado: " + pdf);
                    progress?.Tick(Path.GetFileName(pdf));
                    return;
                }

                if (runDetectDoc)
                {
                    try
                    {
                        var weighted = ObjectsDetectionRouter.DetectWeighted(pdf, detectDocKey);
                        var detectedByModel = weighted.Found && weighted.Page1 > 0;

                        if (DocumentValidationRules.ShouldFallbackDetectDoc(
                                strictDocValidation,
                                weighted.Score,
                                detectedByModel,
                                weighted.BlockReason,
                                out var lowReason))
                        {
                            LogStep(options, CYellow, "[DETECTDOC]", $"fallback_scan pdf={Path.GetFileName(pdf)} reason={lowReason}");
                        }

                        if (weighted.Found && weighted.Page1 > 0)
                        {
                            using var wReader = new PdfReader(pdf);
                            using var wDoc = new PdfDocument(wReader);
                            var preferredPage2Obj = weighted.Page2 > 0
                                ? weighted.PageScores.FirstOrDefault(ps => ps.Page == weighted.Page2)?.Obj ?? 0
                                : 0;
                            var p1 = SelectBestStreamOnPage(wDoc, weighted.Page1, options.OpFilter, preferredObjId: weighted.Obj1);
                            var p2 = weighted.Page2 > 0
                                ? SelectBestStreamOnPage(wDoc, weighted.Page2, options.OpFilter, preferredObjId: preferredPage2Obj)
                                : (ObjId: 0, Tokens: new List<TokenInfo>(), RawTokens: new List<TokenInfo>());

                            if (p1.ObjId > 0)
                            {
                                var detectPick = new MatchPickPair
                                {
                                    A = new MatchPick { Page = weighted.Page1, Obj = p1.ObjId },
                                    B = new MatchPick { Page = weighted.Page2, Obj = p2.ObjId },
                                    HasPair = weighted.Page2 > 0 && p2.ObjId > 0
                                };

                                LogStep(options, CCyan, "[DETECTDOC]", $"pdf={Path.GetFileName(pdf)} p{weighted.Page1}/p{weighted.Page2} score={weighted.Score:0.00} signals={weighted.Signals.Count}");
                                if (options.Log)
                                {
                                    foreach (var det in weighted.DetectorScores
                                                 .OrderByDescending(d => d.Score)
                                                 .ThenByDescending(d => d.MatchScore)
                                                 .ThenBy(d => d.Detector, StringComparer.OrdinalIgnoreCase))
                                    {
                                        Console.Error.WriteLine($"[DETECTDOC] detector={det.Detector} hit={det.Hit} p{det.Page} obj={det.Obj} weight={det.Weight:0.00} match={det.MatchScore:0.00} score={det.Score:0.00} reason={det.Reason} kw={det.Keyword} matched={det.MatchedDocKey} notes={det.Notes}");
                                    }
                                    foreach (var pageScore in weighted.PageScores.Take(5))
                                    {
                                        Console.Error.WriteLine($"[DETECTDOC] page_rank p{pageScore.Page} obj={pageScore.Obj} score={pageScore.Score:0.00} detectors={string.Join(",", pageScore.Detectors)}");
                                    }
                                    foreach (var sig in weighted.Signals)
                                    {
                                        Console.Error.WriteLine($"[DETECTDOC] winner {sig.Detector} p{sig.Page} obj={sig.Obj} score={sig.Weight:0.00} reason={sig.Reason} kw={sig.Keyword}");
                                    }
                                }

                                if (ProcessPick(pdf, detectPick, allowFallbackOnReject: strictDocValidation))
                                    return;

                                if (strictDocValidation)
                                {
                                    LogStep(options, CRed, "[DETECTDOC]", $"route_single_detectdoc_only pdf={Path.GetFileName(pdf)} reason=incomplete_or_validator");
                                    AddEmptyAutoResult(pdf);
                                    progress?.Tick(Path.GetFileName(pdf));
                                    return;
                                }
                            }
                            else if (strictDocValidation)
                            {
                                LogStep(options, CRed, "[DETECTDOC]", $"route_single_detectdoc_only pdf={Path.GetFileName(pdf)} reason=no_stream_obj");
                                AddEmptyAutoResult(pdf);
                                progress?.Tick(Path.GetFileName(pdf));
                                return;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (strictDocValidation)
                        {
                            LogStep(options, CRed, "[DETECTDOC]", $"route_single_detectdoc_only pdf={Path.GetFileName(pdf)} reason=detectdoc_error:{ex.GetType().Name}");
                            AddEmptyAutoResult(pdf);
                            progress?.Tick(Path.GetFileName(pdf));
                            return;
                        }
                    }
                }

                if (runDetectDoc)
                {
                    LogStep(options, CRed, "[DETECTDOC]", $"route_single_detectdoc_only pdf={Path.GetFileName(pdf)} reason=no_accept");
                    AddEmptyAutoResult(pdf);
                    progress?.Tick(Path.GetFileName(pdf));
                    return;
                }

                if (useDespachoShortcut &&
                    ObjectsFindDespacho.TryResolveDespachoPair(pdf, roiDoc, out var sp1, out var so1, out var sp2, out var so2) &&
                    sp1 > 0 && so1 > 0)
                {
                    var candidates = ObjectsFindDespacho.GetDespachoCandidates(pdf, roiDoc, options.ShortcutTop > 0 ? options.ShortcutTop : 1);
                    if (candidates.Count == 0)
                    {
                        candidates.Add(new ObjectsFindDespacho.DespachoCandidate
                        {
                            Page1 = sp1,
                            Obj1 = so1,
                            Page2 = sp2,
                            Obj2 = so2,
                            Score = 1.0
                        });
                    }

                    LogStep(options, CCyan, "[SHORTCUT]", $"candidates={candidates.Count} top={options.ShortcutTop}");
                    var shortcutGroupsP1 = options.RequireAll ? groupsP1 : groupsDetectP1;
                    var shortcutGroupsP2 = options.RequireAll ? groupsP2 : groupsDetectP2;
                    var bestCandidate = (Cand: (ObjectsFindDespacho.DespachoCandidate?)null, Score: 0.0);
                    var minHitsP1 = Math.Max(1, Math.Min(4, shortcutGroupsP1.Count));
                    var minCovP1 = shortcutGroupsP1.Count >= 8 ? 0.25 : 0.20;

                    foreach (var cand in candidates)
                    {
                        if (IsTimedOut(sw))
                        {
                            timedOut = true;
                            break;
                        }
                        double scoreP1 = 0, avgP1 = 0, covP1 = 0;
                        int hitsP1 = 0;
                        double scoreP2 = 0, avgP2 = 0, covP2 = 0;
                        int hitsP2 = 0;

                        var tokens1 = ExtractTokensFromPdf(pdf, cand.Page1, cand.Obj1, options.OpFilter);
                        var raw1 = ExtractRawTokensFromPdf(pdf, cand.Page1, cand.Obj1, options.OpFilter);
                        if (tokens1.Count > 0 || raw1.Count > 0)
                        {
                            if (useDespachoShortcut &&
                                (docName.Equals("tjpb_despacho", StringComparison.OrdinalIgnoreCase) ||
                                 docName.Equals(DocumentValidationRules.DocKeyDespacho, StringComparison.OrdinalIgnoreCase)))
                            {
                                var baseCheck = tokens1.Count > 0
                                    ? string.Join(" ", tokens1.Select(t => t.Text))
                                    : string.Join(" ", raw1.Select(t => t.Text));
                                if (DocumentValidationRules.IsBlockedDespacho(baseCheck))
                                {
                                    if (options.Log)
                                    {
                                        var sample = TrimSnippet(baseCheck ?? "", 120);
                                        Console.Error.WriteLine($"[SHORTCUT_CHECK] blocked=true sample=\"{sample}\"");
                                    }
                                    continue;
                                }
                                var loose = TextUtils.NormalizeForMatch(baseCheck ?? "");
                                var looseCompact = loose.Replace(" ", "");
                                var hasDespacho = DocumentValidationRules.ContainsContentsTitleKeywordsForDoc(loose, docName) ||
                                                  DocumentValidationRules.ContainsContentsTitleKeywordsForDoc(looseCompact, docName);
                                var hasOficio = DocumentValidationRules.IsLikelyOficioLoose(loose, looseCompact);
                                if (options.Log)
                                {
                                    var sample = TrimSnippet(baseCheck ?? "", 120);
                                    Console.Error.WriteLine($"[SHORTCUT_CHECK] despacho={hasDespacho} oficio={hasOficio} sample=\"{sample}\"");
                                }
                                if (hasOficio && !hasDespacho)
                                    continue;
                            }
                            if (rejectTexts.Count > 0)
                            {
                                var baseText = tokens1.Count > 0
                                    ? string.Join(" ", tokens1.Select(t => t.Text))
                                    : string.Join(" ", raw1.Select(t => t.Text));
                                if (DocumentValidationRules.HasRejectTextMatchLoose(baseText, rejectTexts))
                                    continue;
                            }
                            var pat1 = tokens1.Select(t => t.Pattern).ToList();
                            var rawPat1 = raw1.Select(t => t.Pattern).ToList();
                            TryEvaluateGroups(shortcutGroupsP1, tokens1, pat1, raw1, rawPat1, options.MinScore, options.MaxPairs, options.MaxCandidates, options.Log, options.UseRaw, out scoreP1, out avgP1, out covP1, out hitsP1);
                        }

                        var hasPair = cand.Page2 > 0 && cand.Obj2 > 0;
                        if (hasPair)
                        {
                            var tokens2 = ExtractTokensFromPdf(pdf, cand.Page2, cand.Obj2, options.OpFilter);
                            var raw2 = ExtractRawTokensFromPdf(pdf, cand.Page2, cand.Obj2, options.OpFilter);
                            if (tokens2.Count > 0 || raw2.Count > 0)
                            {
                                var pat2 = tokens2.Select(t => t.Pattern).ToList();
                                var rawPat2 = raw2.Select(t => t.Pattern).ToList();
                                TryEvaluateGroups(shortcutGroupsP2, tokens2, pat2, raw2, rawPat2, options.MinScore, options.MaxPairs, options.MaxCandidates, options.Log, options.UseRaw, out scoreP2, out avgP2, out covP2, out hitsP2);
                            }
                        }

                        var combined = hasPair ? (scoreP1 + scoreP2) / 2.0 : scoreP1;
                        if (cand.Score > 0)
                            combined = (combined * 0.7) + (cand.Score * 0.3);
                        if (hitsP1 < minHitsP1 || covP1 < minCovP1)
                        {
                            LogStep(options, CYellow, "[SHORTCUT]", $"p{cand.Page1}/p{cand.Page2} obj={cand.Obj1}/{cand.Obj2} skip hits={hitsP1} cov={covP1:0.00}");
                            continue;
                        }
                        LogStep(options, CYellow, "[SHORTCUT]", $"p{cand.Page1}/p{cand.Page2} obj={cand.Obj1}/{cand.Obj2} score={combined:0.00} cand={cand.Score:0.00} hits={hitsP1}+{hitsP2}");
                        if (combined > bestCandidate.Score)
                            bestCandidate = (cand, combined);
                    }

                    if (timedOut)
                    {
                        Console.WriteLine($"[timeout] {Path.GetFileName(pdf)} > {options.TimeoutSec:0.0}s");
                        AddEmptyAutoResult(pdf);
                        progress?.Tick(Path.GetFileName(pdf));
                        return;
                    }

                    if (bestCandidate.Cand != null)
                    {
                        var best = bestCandidate.Cand;
                        var shortcutPick = new MatchPickPair
                        {
                            A = new MatchPick { Page = best.Page1, Obj = best.Obj1 },
                            B = new MatchPick { Page = best.Page2, Obj = best.Obj2 },
                            HasPair = best.Page2 > 0 && best.Obj2 > 0
                        };

                        var ok = ProcessPick(pdf, shortcutPick, allowFallbackOnReject: strictDocValidation);
                        if (ok || !strictDocValidation)
                            return;

                        LogStep(options, CYellow, "[DETECTDOC]", $"fallback_scan pdf={Path.GetFileName(pdf)} reason=incomplete_or_validator_shortcut");
                    }
                }
                else if (useDespachoShortcut && options.TimeoutSec > 0)
                {
                    if (!strictDocValidation)
                    {
                        Console.WriteLine($"Nenhum despacho encontrado (shortcut): {Path.GetFileName(pdf)}");
                        progress?.Tick(Path.GetFileName(pdf));
                        return;
                    }

                    LogStep(options, CYellow, "[DETECTDOC]", $"fallback_scan pdf={Path.GetFileName(pdf)} reason=shortcut_not_found");
                }

                LogStep(options, CCyan, "[START]", $"pdf={Path.GetFileName(pdf)}");

                using var reader = new PdfReader(pdf);
                using var doc = new PdfDocument(reader);
                LogStep(options, CCyan, "[PAGES]", $"total={doc.GetNumberOfPages()}");

                var bestByPageP1 = new Dictionary<int, GroupedHit>();
                var bestByPageP2 = new Dictionary<int, GroupedHit>();
                var bestOverall = (Page: 0, Obj: 0, Score: 0.0);
                var rejectedPages = new HashSet<int>();

                var detectGroupsP1 = options.RequireAll ? groupsP1 : groupsDetectP1;
                var detectGroupsP2 = options.RequireAll ? groupsP2 : groupsDetectP2;
                var requireHitsP1 = options.RequireAll ? Math.Max(0, detectGroupsP1.Count) : 0;
                var requireHitsP2 = options.RequireAll ? Math.Max(0, detectGroupsP2.Count) : 0;
                var requireHitsAll = options.RequireAll ? Math.Max(0, groupsAll.Count) : 0;

                for (int p = 1; p <= doc.GetNumberOfPages(); p++)
                {
                    if (IsTimedOut(sw))
                    {
                        timedOut = true;
                        break;
                    }
                    var maxTokensOnPage = 0;
                    var candP1 = new List<GroupedHit>();
                    var candP2 = new List<GroupedHit>();
                    var page = doc.GetPage(p);
                    var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                    var contents = page.GetPdfObject().Get(PdfName.Contents);
                    var pageHeight = page.GetPageSize().GetHeight();
                    var pageWidth = page.GetPageSize().GetWidth();

                    foreach (var stream in EnumerateStreams(contents))
                    {
                        if (IsTimedOut(sw))
                        {
                            timedOut = true;
                            break;
                        }
                        if (rejectedPages.Contains(p))
                            break;
                        int objId = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                        if (objId <= 0)
                            continue;

                        var tokens = ExtractTokensFromStream(stream, resources, pageHeight, pageWidth, options.OpFilter);
                        var rawTokens = ExtractRawTokensFromStream(stream, resources, pageHeight, pageWidth, options.OpFilter);
                        if (tokens.Count == 0 && rawTokens.Count == 0)
                            continue;

                        if (tokens.Count > maxTokensOnPage)
                            maxTokensOnPage = tokens.Count;

                        var tokenPatterns = tokens.Select(t => t.Pattern).ToList();
                        var rawPatterns = rawTokens.Select(t => t.Pattern).ToList();
                        LogStep(options, CBlue, "[STREAM]", $"p{p} obj={objId} tokens={tokens.Count} raw={rawTokens.Count}");

                        if (rejectTexts.Count > 0)
                        {
                            var baseText = tokens.Count > 0
                                ? string.Join(" ", tokens.Select(t => t.Text))
                                : string.Join(" ", rawTokens.Select(t => t.Text));
                            if (DocumentValidationRules.HasRejectTextMatchLoose(baseText, rejectTexts))
                            {
                                rejectedPages.Add(p);
                                break;
                            }
                        }

                        if (groupsReject.Count > 0 &&
                            TryEvaluateGroups(groupsReject, tokens, tokenPatterns, rawTokens, rawPatterns, options.MinScore, options.MaxPairs, options.MaxCandidates, options.Log, options.UseRaw, out _, out _, out _, out _))
                        {
                            rejectedPages.Add(p);
                            break;
                        }

                        if (useOverallScan &&
                            TryEvaluateGroups(groupsAll, tokens, tokenPatterns, rawTokens, rawPatterns, options.MinScore, options.MaxPairs, options.MaxCandidates, options.Log, options.UseRaw, out var scoreAll, out _, out _, out var hitsAll))
                        {
                            if (options.RequireAll && requireHitsAll > 0 && hitsAll < requireHitsAll)
                            {
                                // strict: precisa cobrir todos os campos exigidos
                            }
                            else
                            {
                                if (scoreAll > bestOverall.Score)
                                    bestOverall = (p, objId, scoreAll);
                            }
                        }

                        if (page1Fields.Count > 0 &&
                            TryEvaluateGroups(detectGroupsP1, tokens, tokenPatterns, rawTokens, rawPatterns, options.MinScore, options.MaxPairs, options.MaxCandidates, options.Log, options.UseRaw, out var scoreP1, out var avgP1, out var covP1, out var hitsP1))
                        {
                            var acceptP1 = !(options.RequireAll && requireHitsP1 > 0 && hitsP1 < requireHitsP1);
                            if (acceptP1)
                            {
                                var gh = new GroupedHit { Page = p, Obj = objId, Score = scoreP1, Hits = hitsP1, Avg = avgP1, Coverage = covP1, Tokens = tokens.Count };
                                candP1.Add(gh);
                                LogStep(options, CYellow, "[P1]", $"p{p} obj={objId} score={scoreP1:0.00} hits={hitsP1} tokens={tokens.Count}");
                                if (!useOverallScan && scoreP1 > bestOverall.Score)
                                    bestOverall = (p, objId, scoreP1);
                            }
                        }
                        if (page2Fields.Count > 0 &&
                            TryEvaluateGroups(detectGroupsP2, tokens, tokenPatterns, rawTokens, rawPatterns, options.MinScore, options.MaxPairs, options.MaxCandidates, options.Log, options.UseRaw, out var scoreP2, out var avgP2, out var covP2, out var hitsP2))
                        {
                            var acceptP2 = !(options.RequireAll && requireHitsP2 > 0 && hitsP2 < requireHitsP2);
                            if (acceptP2)
                            {
                                var gh = new GroupedHit { Page = p, Obj = objId, Score = scoreP2, Hits = hitsP2, Avg = avgP2, Coverage = covP2, Tokens = tokens.Count };
                                candP2.Add(gh);
                                LogStep(options, CYellow, "[P2]", $"p{p} obj={objId} score={scoreP2:0.00} hits={hitsP2} tokens={tokens.Count}");
                                if (!useOverallScan && scoreP2 > bestOverall.Score)
                                    bestOverall = (p, objId, scoreP2);
                            }
                        }
                    }

                    if (timedOut)
                        break;

                    if (maxTokensOnPage > 0)
                    {
                        var minTokens = (int)Math.Round(maxTokensOnPage * options.MinStreamRatio, MidpointRounding.AwayFromZero);
                        if (candP1.Count > 0)
                        {
                            var bestPick = PickBestStream(candP1, minTokens);
                            if (bestPick != null)
                            {
                                bestByPageP1[p] = bestPick;
                                LogStep(options, CGreen, "[P1_BEST]", $"p{p} obj={bestPick.Obj} score={bestPick.Score:0.00} hits={bestPick.Hits} tokens={bestPick.Tokens}/{maxTokensOnPage}");
                            }
                        }
                        if (candP2.Count > 0)
                        {
                            var bestPick = PickBestStream(candP2, minTokens);
                            if (bestPick != null)
                            {
                                bestByPageP2[p] = bestPick;
                                LogStep(options, CGreen, "[P2_BEST]", $"p{p} obj={bestPick.Obj} score={bestPick.Score:0.00} hits={bestPick.Hits} tokens={bestPick.Tokens}/{maxTokensOnPage}");
                            }
                        }
                    }
                }

                if (timedOut)
                {
                    Console.WriteLine($"[timeout] {Path.GetFileName(pdf)} > {options.TimeoutSec:0.0}s");
                    AddEmptyAutoResult(pdf);
                    progress?.Tick(Path.GetFileName(pdf));
                    return;
                }

                var pick = new MatchPickPair();
                if (page1Fields.Count > 0 && page2Fields.Count > 0)
                {
                    var bestPairScore = 0.0;
                    foreach (var kv in bestByPageP1)
                    {
                        var p1 = kv.Key;
                        var p2 = p1 + 1;
                        if (!bestByPageP2.ContainsKey(p2))
                            continue;
                        if (rejectedPages.Contains(p1) || rejectedPages.Contains(p2))
                            continue;
                        var a = bestByPageP1[p1];
                        var b = bestByPageP2[p2];
                        var score = (a.Score + b.Score) / 2.0;
                        if (score > bestPairScore)
                        {
                            bestPairScore = score;
                            pick.A = new MatchPick { Page = p1, Obj = a.Obj };
                            pick.B = new MatchPick { Page = p2, Obj = b.Obj };
                            pick.HasPair = true;
                        }
                    }
                    if (pick.HasPair)
                        LogStep(options, CGreen, "[PAIR]", $"p{pick.A.Page}/p{pick.B.Page} score={bestPairScore:0.00}");
                    else
                        LogStep(options, CRed, "[PAIR]", "nenhum par contiguo");
                }

                if (!pick.HasPair && bestOverall.Page > 0 && bestOverall.Obj > 0)
                {
                    pick.A = new MatchPick { Page = bestOverall.Page, Obj = bestOverall.Obj };
                }

                if (pick.A.Page == 0 || pick.A.Obj == 0)
                {
                    Console.WriteLine("Nenhum match encontrado: " + Path.GetFileName(pdf));
                    if (strictDocValidation)
                        AddEmptyAutoResult(pdf);
                    progress?.Tick(Path.GetFileName(pdf));
                    return;
                }

                if (!strictDocValidation)
                {
                    _ = ProcessPick(pdf, pick);
                    return;
                }

                const int maxPickAttempts = 5;
                var triedAny = false;

                // Try top contiguous pairs first (pN/pN+1). Only accept when validator+coverage pass.
                if (page1Fields.Count > 0 && page2Fields.Count > 0)
                {
                    var scoredPairs = new List<(double Score, int P1, int O1, int P2, int O2)>();
                    foreach (var kv in bestByPageP1)
                    {
                        var p1 = kv.Key;
                        var p2 = p1 + 1;
                        if (!bestByPageP2.ContainsKey(p2))
                            continue;
                        if (rejectedPages.Contains(p1) || rejectedPages.Contains(p2))
                            continue;

                        var a = bestByPageP1[p1];
                        var b = bestByPageP2[p2];
                        var score = (a.Score + b.Score) / 2.0;
                        scoredPairs.Add((score, p1, a.Obj, p2, b.Obj));
                    }

                    foreach (var cand in scoredPairs
                                 .OrderByDescending(p => p.Score)
                                 .ThenBy(p => p.P1)
                                 .Take(maxPickAttempts))
                    {
                        triedAny = true;
                        var candPick = new MatchPickPair
                        {
                            A = new MatchPick { Page = cand.P1, Obj = cand.O1 },
                            B = new MatchPick { Page = cand.P2, Obj = cand.O2 },
                            HasPair = true
                        };

                        if (ProcessPick(pdf, candPick, allowFallbackOnReject: true))
                            return;
                    }
                }

                // Fallback: try best overall single-page hit.
                if (!triedAny && pick.A.Page > 0 && pick.A.Obj > 0)
                {
                    triedAny = true;
                    if (ProcessPick(pdf, pick, allowFallbackOnReject: true))
                        return;
                }

                AddEmptyAutoResult(pdf);
                progress?.Tick(Path.GetFileName(pdf));
                ForceGcBetweenPdfs();
                return;
            }

            if (canRunParallel)
            {
                var parallelOptions = new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = options.Jobs
                };
                System.Threading.Tasks.Parallel.ForEach(options.Inputs, parallelOptions, ProcessPdfInput);
            }
            else
            {
                foreach (var pdf in options.Inputs)
                    ProcessPdfInput(pdf);
            }

            if (autoResults != null)
            {
                var payload = new Dictionary<string, object?>
                {
                    ["patterns"] = Path.GetFileName(options.PatternsPath),
                    ["items"] = autoResults
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                try
                {
                    var outDir = Path.GetDirectoryName(options.OutPath);
                    if (!string.IsNullOrWhiteSpace(outDir))
                        Directory.CreateDirectory(outDir);
                    File.WriteAllText(options.OutPath!, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    Console.WriteLine("Arquivo salvo: " + options.OutPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Falha ao salvar JSON: " + ex.Message);
                }
            }
        }

        private static void PrintFieldSummary(
            MatchOptions options,
            List<string> orderedA,
            HashSet<string> page2Fields,
            HashSet<string> optionalFields,
            Dictionary<string, List<FieldMatch>> fieldsA,
            Dictionary<string, List<FieldMatch>> fieldsB,
            Dictionary<string, string> finalValues)
        {
            if (!options.Log)
                return;
            var optSet = new HashSet<string>(optionalFields, StringComparer.OrdinalIgnoreCase);
            var p1Set = new HashSet<string>(orderedA, StringComparer.OrdinalIgnoreCase);
            var p2Set = new HashSet<string>(page2Fields, StringComparer.OrdinalIgnoreCase);
            LogStep(options, CMagenta, "[SUMMARY]", "status por campo (p1/p2)");
            foreach (var field in orderedA)
            {
                var matches = fieldsA.TryGetValue(field, out var list) ? list : new List<FieldMatch>();
                string? finalVal = null;
                var hasFinal = finalValues != null && finalValues.TryGetValue(field, out finalVal) && !string.IsNullOrWhiteSpace(finalVal);
                var status = matches.Count > 0 || hasFinal
                    ? "OK"
                    : (optSet.Contains(field) ? "MISS(opt)" : "MISS");
                var detail = matches.Count > 0
                    ? $"hits={matches.Count} best=\"{TrimSnippet(matches[0].ValueText)}\""
                    : hasFinal
                        ? $"hits=0 final=\"{TrimSnippet(finalVal)}\""
                        : "hits=0";
                LogStep(options, status == "OK" ? CGreen : CRed, "[P1]", $"{field} => {status} ({detail})");
            }
            if (p2Set.Count > 0)
            {
                foreach (var field in page2Fields)
                {
                    var matches = fieldsB.TryGetValue(field, out var list) ? list : new List<FieldMatch>();
                    string? finalVal = null;
                    var hasFinal = finalValues != null && finalValues.TryGetValue(field, out finalVal) && !string.IsNullOrWhiteSpace(finalVal);
                    var status = matches.Count > 0 || hasFinal
                        ? "OK"
                        : (optSet.Contains(field) ? "MISS(opt)" : "MISS");
                    var detail = matches.Count > 0
                        ? $"hits={matches.Count} best=\"{TrimSnippet(matches[0].ValueText)}\""
                        : hasFinal
                            ? $"hits=0 final=\"{TrimSnippet(finalVal)}\""
                            : "hits=0";
                    LogStep(options, status == "OK" ? CGreen : CRed, "[P2]", $"{field} => {status} ({detail})");
                }
            }
        }

        private static Dictionary<string, string> BuildValuesFromMatches(
            List<string> orderedA,
            IEnumerable<string> orderedB,
            Dictionary<string, List<FieldMatch>> fieldsA,
            Dictionary<string, List<FieldMatch>> fieldsB)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool TryAddField(string field, List<FieldMatch> list)
            {
                if (list == null || list.Count == 0)
                    return false;
                var pick = PickBestMatch(field, list);
                if (pick == null)
                    return false;

                var val = pick.ValueText ?? "";
                if (field.Equals("PERITO", StringComparison.OrdinalIgnoreCase))
                {
                    var cleaned = CleanPeritoValue(val);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                        val = cleaned;
                }

                val = NormalizeValueByField(field, val);
                if (!IsValidFieldFormat(field, val))
                    return false;

                values[field] = val;
                return true;
            }

            foreach (var field in orderedA)
            {
                if (fieldsA.TryGetValue(field, out var list))
                    TryAddField(field, list);
            }
            foreach (var field in orderedB)
            {
                if (values.ContainsKey(field))
                    continue;
                if (fieldsB.TryGetValue(field, out var list))
                    TryAddField(field, list);
            }

            // Compatibility bridge between documentary aliases used by different pages/templates.
            if ((!values.TryGetValue("VALOR_ARBITRADO_DE", out var deVal) || string.IsNullOrWhiteSpace(deVal)) &&
                values.TryGetValue("VALOR_ARBITRADO_JZ", out var jzVal) &&
                !string.IsNullOrWhiteSpace(jzVal) &&
                IsValidFieldFormat("VALOR_ARBITRADO_DE", jzVal))
            {
                values["VALOR_ARBITRADO_DE"] = NormalizeValueByField("VALOR_ARBITRADO_DE", jzVal);
            }

            if ((!values.TryGetValue("VALOR_ARBITRADO_JZ", out var jzVal2) || string.IsNullOrWhiteSpace(jzVal2)) &&
                values.TryGetValue("VALOR_ARBITRADO_DE", out var deVal2) &&
                !string.IsNullOrWhiteSpace(deVal2) &&
                IsValidFieldFormat("VALOR_ARBITRADO_JZ", deVal2))
            {
                values["VALOR_ARBITRADO_JZ"] = NormalizeValueByField("VALOR_ARBITRADO_JZ", deVal2);
            }
            return values;
        }

        private static void TryFillProcessoAdministrativoFromPdfName(string pdf, Dictionary<string, string> values)
        {
            if (values == null || string.IsNullOrWhiteSpace(pdf))
                return;
            if (values.TryGetValue("PROCESSO_ADMINISTRATIVO", out var existing) && !string.IsNullOrWhiteSpace(existing))
                return;

            var stem = Path.GetFileNameWithoutExtension(pdf);
            if (string.IsNullOrWhiteSpace(stem))
                return;

            var m = Regex.Match(stem, @"\b(?:19|20)\d{8}\b");
            if (!m.Success)
                return;

            var candidate = m.Value;
            if (!IsValidFieldFormat("PROCESSO_ADMINISTRATIVO", candidate))
                return;

            values["PROCESSO_ADMINISTRATIVO"] = candidate;
        }

        private static void TryBackfillPartiesFromObjectTexts(
            MatchOptions options,
            string pdf,
            int page1,
            int obj1,
            int page2,
            int obj2,
            Dictionary<string, string> values)
        {
            if (values == null || string.IsNullOrWhiteSpace(pdf) || !File.Exists(pdf))
                return;

            var needPromovente = NeedsDocumentFallbackValue("PROMOVENTE", values.TryGetValue("PROMOVENTE", out var existingPromovente) ? existingPromovente : null);
            var needPromovido = NeedsDocumentFallbackValue("PROMOVIDO", values.TryGetValue("PROMOVIDO", out var existingPromovido) ? existingPromovido : null);
            if (!needPromovente && !needPromovido)
                return;

            var texts = new List<string>();
            void AddTextCandidate(string? candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    return;
                var normalized = TextNormalization.NormalizeWhitespace(candidate);
                if (string.IsNullOrWhiteSpace(normalized))
                    return;
                if (!texts.Any(t => string.Equals(t, normalized, StringComparison.Ordinal)))
                    texts.Add(normalized);
            }

            AddTextCandidate(BuildFullTextFromBlocks(pdf, page1, obj1, options.OpFilter));
            AddTextCandidate(TryExtractFullText(pdf, obj1, options.Log));
            AddTextCandidate(BuildFullTextFromTextOps(pdf, obj1, options.Log));
            var rawTokensA = ExtractRawTokensFromPdf(pdf, page1, obj1, options.OpFilter);
            if (rawTokensA.Count > 0)
                AddTextCandidate(string.Join(" ", rawTokensA.Select(t => t.Text)));
            if (page2 > 0 && obj2 > 0)
            {
                AddTextCandidate(BuildFullTextFromBlocks(pdf, page2, obj2, options.OpFilter));
                AddTextCandidate(TryExtractFullText(pdf, obj2, options.Log));
                AddTextCandidate(BuildFullTextFromTextOps(pdf, obj2, options.Log));
                var rawTokensB = ExtractRawTokensFromPdf(pdf, page2, obj2, options.OpFilter);
                if (rawTokensB.Count > 0)
                    AddTextCandidate(string.Join(" ", rawTokensB.Select(t => t.Text)));
            }

            foreach (var text in texts)
            {
                if (!TryExtractPartiesFromEmFaceContext(text, out var promovente, out var promovido))
                    continue;

                if (needPromovente && !string.IsNullOrWhiteSpace(promovente))
                {
                    var normalizedPromovente = NormalizeValueByField("PROMOVENTE", promovente);
                    normalizedPromovente = TextUtils.NormalizeWhitespace(TextUtils.FixMissingSpaces(normalizedPromovente));
                    if (!string.IsNullOrWhiteSpace(normalizedPromovente) && IsValidFieldFormat("PROMOVENTE", normalizedPromovente))
                    {
                        values["PROMOVENTE"] = normalizedPromovente;
                        needPromovente = false;
                    }
                }

                if (needPromovido && !string.IsNullOrWhiteSpace(promovido))
                {
                    var normalizedPromovido = NormalizeValueByField("PROMOVIDO", promovido);
                    normalizedPromovido = TextUtils.NormalizeWhitespace(TextUtils.FixMissingSpaces(normalizedPromovido));
                    if (!string.IsNullOrWhiteSpace(normalizedPromovido) && IsValidFieldFormat("PROMOVIDO", normalizedPromovido))
                    {
                        values["PROMOVIDO"] = normalizedPromovido;
                        needPromovido = false;
                    }
                }

                if (!needPromovente && !needPromovido)
                    return;
            }
        }

        private static void ApplyDocumentWideFallbackValues(
            MatchOptions options,
            string pdf,
            List<string> orderedA,
            List<string> orderedB,
            Dictionary<string, string> values)
        {
            if (values == null || string.IsNullOrWhiteSpace(pdf) || !File.Exists(pdf))
                return;

            var trackedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (orderedA != null)
                trackedFields.UnionWith(orderedA);
            if (orderedB != null)
                trackedFields.UnionWith(orderedB);
            if (trackedFields.Count == 0)
                return;

            var supportedFallbackFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PROCESSO_JUDICIAL",
                "PROCESSO_ADMINISTRATIVO",
                "PROMOVENTE",
                "PROMOVIDO",
                "PERITO",
                "CPF_PERITO",
                "ESPECIALIDADE",
                "ESPECIE_DA_PERICIA",
                "VALOR_ARBITRADO_JZ",
                "VALOR_ARBITRADO_DE",
                "VALOR_ARBITRADO_CM",
                "DATA_ARBITRADO_FINAL",
                "DATA_REQUISICAO",
                "COMARCA",
                "VARA"
            };

            var missing = trackedFields
                .Where(f => supportedFallbackFields.Contains(f))
                .Where(f => NeedsDocumentFallbackValue(f, values.TryGetValue(f, out var existing) ? existing : null))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (values.TryGetValue("PERITO", out var peritoValue) &&
                values.TryGetValue("PROMOVIDO", out var promovidoValue) &&
                !string.IsNullOrWhiteSpace(peritoValue) &&
                !string.IsNullOrWhiteSpace(promovidoValue))
            {
                var peritoNorm = TextUtils.NormalizeForMatch(peritoValue);
                var promovidoNorm = TextUtils.NormalizeForMatch(promovidoValue);
                if (!string.IsNullOrWhiteSpace(peritoNorm) &&
                    string.Equals(peritoNorm, promovidoNorm, StringComparison.OrdinalIgnoreCase) &&
                    !missing.Contains("PROMOVIDO", StringComparer.OrdinalIgnoreCase))
                {
                    missing.Add("PROMOVIDO");
                }
            }

            if (missing.Count == 0)
                return;

            EnsureRegexCatalog(options.PatternsPath, options.Log);
            var fullTextRaw = TryExtractWholePdfText(pdf, options.Log);
            if (string.IsNullOrWhiteSpace(fullTextRaw))
                return;
            var fullTextNorm = TextNormalization.NormalizePatternText(fullTextRaw);
            if (string.IsNullOrWhiteSpace(fullTextNorm))
                return;

            foreach (var field in missing)
            {
                var candidate = TryExtractFieldFromWholePdfText(field, fullTextNorm, fullTextRaw);
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                var normalized = NormalizeValueByField(field, candidate);
                normalized = CollapseUpperResiduals(normalized);
                normalized = FixDanglingUpperInitial(normalized);
                normalized = TextUtils.NormalizeWhitespace(normalized);
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;
                if (!IsValidFieldFormat(field, normalized))
                    continue;

                values[field] = normalized;
                LogStep(options, CCyan, "[PDF_FALLBACK]", $"{field}=\"{TrimSnippet(normalized, 120)}\"");
            }

            // Maintain compatibility with downstream expectations.
            if ((!values.TryGetValue("VALOR_ARBITRADO_DE", out var de) || string.IsNullOrWhiteSpace(de)) &&
                values.TryGetValue("VALOR_ARBITRADO_JZ", out var jz) &&
                !string.IsNullOrWhiteSpace(jz))
            {
                values["VALOR_ARBITRADO_DE"] = jz;
                LogStep(options, CCyan, "[PDF_FALLBACK]", "VALOR_ARBITRADO_DE=derived_from_VALOR_ARBITRADO_JZ");
            }

            HonorariosBackfill.ApplyProfissaoAsEspecialidade(values);
        }

        private static bool NeedsDocumentFallbackValue(string field, string? currentValue)
        {
            if (string.IsNullOrWhiteSpace(currentValue))
                return true;
            if (!IsValidFieldFormat(field, currentValue))
                return true;
            if (field.Equals("PERITO", StringComparison.OrdinalIgnoreCase) && IsWeakPeritoValue(currentValue))
                return true;
            if ((field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                 field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase)) &&
                IsWeakPartyValue(currentValue))
                return true;
            return false;
        }

        private static bool IsWeakPeritoValue(string? value)
        {
            return ValidatorRules.IsWeakPeritoValue(value);
        }

        private static bool IsWeakPartyValue(string? value)
        {
            return ValidatorRules.IsWeakPartyValue(value);
        }

        private static string TryExtractFieldFromWholePdfText(string field, string fullTextNorm, string fullTextRaw)
        {
            if (string.IsNullOrWhiteSpace(field))
                return "";

            string Pick(params string[] values)
            {
                foreach (var v in values)
                {
                    if (!string.IsNullOrWhiteSpace(v))
                        return v.Trim();
                }
                return "";
            }

            switch (field.ToUpperInvariant())
            {
                case "PROCESSO_JUDICIAL":
                    return Pick(
                        TryExtractProcessoJudicialFromText(fullTextNorm),
                        ExtractRegexValueFromCatalog("PROCESSO_JUDICIAL", fullTextNorm),
                        ExtractRegexValueFromCatalogLoose("PROCESSO_JUDICIAL", fullTextNorm));
                case "PROCESSO_ADMINISTRATIVO":
                    return Pick(
                        TryExtractProcessoAdministrativoFromText(fullTextNorm),
                        ExtractRegexValueFromCatalog("PROCESSO_ADMINISTRATIVO", fullTextNorm),
                        ExtractRegexValueFromCatalogLoose("PROCESSO_ADMINISTRATIVO", fullTextNorm));
                case "CPF_PERITO":
                    return Pick(
                        TryExtractCpfPeritoFromText(fullTextRaw),
                        ExtractRegexValueFromCatalog("CPF_PERITO", fullTextNorm),
                        ExtractRegexValueFromCatalogLoose("CPF_PERITO", fullTextNorm));
                case "VALOR_ARBITRADO_JZ":
                case "VALOR_ARBITRADO_DE":
                case "VALOR_ARBITRADO_CM":
                    return Pick(
                        TryExtractValorArbitradoFromText(fullTextRaw),
                        ExtractRegexValueFromCatalog(field, fullTextNorm),
                        ExtractRegexValueFromCatalogLoose(field, fullTextNorm));
                case "PROMOVENTE":
                    return Pick(
                        TryExtractPromoventeFromEmFaceContext(fullTextRaw),
                        TryExtractPromoventeFromEmFaceContext(fullTextNorm),
                        TryExtractPromoventeFromLine(fullTextNorm),
                        TryExtractPromoventeFromLine(fullTextRaw),
                        ExtractRegexValueFromCatalog("PROMOVENTE", fullTextNorm),
                        ExtractRegexValueFromCatalogLoose("PROMOVENTE", fullTextNorm));
                case "PROMOVIDO":
                    return Pick(
                        TryExtractPromovidoFromEmFaceContext(fullTextRaw),
                        TryExtractPromovidoFromEmFaceContext(fullTextNorm),
                        TryExtractPromovidoFromLine(fullTextNorm),
                        TryExtractPromovidoFromLine(fullTextRaw),
                        ExtractRegexValueFromCatalog("PROMOVIDO", fullTextNorm),
                        ExtractRegexValueFromCatalogLoose("PROMOVIDO", fullTextNorm));
                case "PERITO":
                    return Pick(
                        TryExtractPeritoFromText(fullTextRaw),
                        TryExtractPeritoFromText(fullTextNorm),
                        ExtractRegexValueFromCatalog("PERITO", fullTextNorm),
                        ExtractRegexValueFromCatalogLoose("PERITO", fullTextNorm));
                case "ESPECIALIDADE":
                    return Pick(
                        ExtractRegexValueFromCatalog("ESPECIALIDADE", fullTextNorm),
                        ExtractRegexValueFromCatalogLoose("ESPECIALIDADE", fullTextNorm));
                case "ESPECIE_DA_PERICIA":
                    return Pick(
                        ExtractRegexValueFromCatalog("ESPECIE_DA_PERICIA", fullTextNorm),
                        ExtractRegexValueFromCatalogLoose("ESPECIE_DA_PERICIA", fullTextNorm));
                case "DATA_ARBITRADO_FINAL":
                case "DATA_REQUISICAO":
                    return Pick(
                        ExtractRegexValueFromCatalog(field, fullTextNorm),
                        ExtractRegexValueFromCatalogLoose(field, fullTextNorm));
                case "COMARCA":
                    return Pick(
                        TryExtractComarcaFromText(fullTextRaw),
                        ExtractRegexValueFromCatalog("COMARCA", fullTextNorm),
                        ExtractRegexValueFromCatalogLoose("COMARCA", fullTextNorm));
                case "VARA":
                    return Pick(
                        TryExtractVaraFromText(fullTextRaw),
                        ExtractRegexValueFromCatalog("VARA", fullTextNorm),
                        ExtractRegexValueFromCatalogLoose("VARA", fullTextNorm));
                default:
                    return Pick(
                        ExtractRegexValueFromCatalog(field, fullTextNorm),
                        ExtractRegexValueFromCatalogLoose(field, fullTextNorm));
            }
        }

        private static string TryExtractWholePdfText(string pdfPath, bool log = false)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                return "";
            try
            {
                using var reader = new PdfReader(pdfPath);
                using var doc = new PdfDocument(reader);
                var sb = new StringBuilder();
                var total = doc.GetNumberOfPages();
                for (int p = 1; p <= total; p++)
                {
                    try
                    {
                        var page = doc.GetPage(p);
                        var text = iText.Kernel.Pdf.Canvas.Parser.PdfTextExtractor.GetTextFromPage(
                            page,
                            new iText.Kernel.Pdf.Canvas.Parser.Listener.SimpleTextExtractionStrategy());
                        if (!string.IsNullOrWhiteSpace(text))
                            sb.AppendLine(text);
                    }
                    catch (Exception exPage)
                    {
                        if (log)
                            Console.Error.WriteLine($"[PDF_FALLBACK] page={p} text_extract_error={exPage.GetType().Name}");
                    }
                }

                return TextNormalization.FixLineBreakWordSplits(sb.ToString());
            }
            catch (Exception ex)
            {
                if (log)
                    Console.Error.WriteLine($"[PDF_FALLBACK] whole_pdf_extract_error={ex.GetType().Name}");
                return "";
            }
        }

        private static string TryExtractComarcaFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var re = new Regex(@"(?is)\bcomarca\s+(da|de|do)\s+([A-Za-zÀ-ÿ0-9\.\- ]{3,80})");
            foreach (Match m in re.Matches(text))
            {
                if (!m.Success)
                    continue;
                var prep = (m.Groups[1].Value ?? "").Trim();
                var place = (m.Groups[2].Value ?? "").Trim();
                if (string.IsNullOrWhiteSpace(place))
                    continue;
                place = Regex.Split(place, @"[,\.;\r\n]")[0].Trim();
                if (string.IsNullOrWhiteSpace(place))
                    continue;
                var candidate = $"Comarca {prep} {place}";
                return TextUtils.NormalizeWhitespace(candidate);
            }

            return "";
        }

        private static string TryExtractVaraFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var re = new Regex(@"(?is)\b(\d{1,2}\s*[ªºa]?\s*vara\s+[A-Za-zÀ-ÿ0-9 \.\-]{3,90})");
            foreach (Match m in re.Matches(text))
            {
                if (!m.Success)
                    continue;
                var candidate = (m.Groups[1].Value ?? "").Trim();
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;
                candidate = Regex.Split(candidate, @"[\r\n]")[0].Trim();
                if (candidate.Length < 6)
                    continue;
                return TextUtils.NormalizeWhitespace(candidate);
            }

            return "";
        }

        private static string TryExtractPeritoFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var patterns = new[]
            {
                @"(?is)\binteressad[oa]\s*:\s*([A-ZÁÂÃÀÉÊÍÓÔÕÚÇ][\p{L}\s\.'\-]{5,140})",
                @"(?is)\bperit[oa]\s+(?:nomead[oa]\s+)?([A-ZÁÂÃÀÉÊÍÓÔÕÚÇ][\p{L}\s\.'\-]{5,140}?)(?=,?\s*(?:CPF|CNPJ|por\s+per[ií]cia|nos\s+autos|em\s+raz[aã]o|$))",
                @"(?is)\b(?:ao|a|do|da)\s+perit[oa]\s+([A-ZÁÂÃÀÉÊÍÓÔÕÚÇ][\p{L}\s\.'\-]{5,140}?)(?=,?\s*(?:CPF|CNPJ|por\s+per[ií]cia|nos\s+autos|em\s+raz[aã]o|$))"
            };

            foreach (var pat in patterns)
            {
                foreach (Match m in Regex.Matches(text, pat))
                {
                    if (!m.Success)
                        continue;
                    var candidate = (m.Groups[1].Value ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(candidate))
                        continue;
                    candidate = Regex.Replace(candidate, @"(?is)\b(?:cpf|cnpj)\b.*$", "").Trim();
                    candidate = CleanPeritoValue(candidate);
                    if (string.IsNullOrWhiteSpace(candidate))
                        continue;
                    if (!IsValidPeritoValue(candidate))
                        continue;
                    return candidate;
                }
            }

            return "";
        }

        private static FieldMatch? PickBestMatch(string field, List<FieldMatch> matches)
        {
            if (matches.Count == 0)
                return null;

            // Prefer candidates that pass validator rules (if any).
            var catalog = ValidatorContext.GetPeritoCatalog();
            var validated = matches
                .Where(m => IsValueValidForField(field, m.ValueText ?? "", catalog, out _))
                .ToList();
            if (validated.Count > 0)
                matches = validated;

            if (field.Equals("VALOR_ARBITRADO_JZ", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("VALOR_ARBITRADO_DE", StringComparison.OrdinalIgnoreCase))
            {
                FieldMatch? best = null;
                decimal bestVal = -1m;
                foreach (var m in matches)
                {
                    if (string.IsNullOrWhiteSpace(m.ValueText))
                        continue;
                    if (!TextUtils.TryParseMoney(m.ValueText, out var val))
                        continue;
                    if (val > bestVal)
                    {
                        bestVal = val;
                        best = m;
                    }
                }
                if (best != null)
                    return best;
            }

            if (field.Equals("DATA_ARBITRADO_FINAL", StringComparison.OrdinalIgnoreCase))
            {
                FieldMatch? best = null;
                DateTime? bestDate = null;
                foreach (var m in matches)
                {
                    if (string.IsNullOrWhiteSpace(m.ValueText))
                        continue;
                    if (!TextUtils.TryParseDate(m.ValueText, out var iso))
                        continue;
                    if (!DateTime.TryParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                        continue;
                    if (bestDate == null || dt > bestDate)
                    {
                        bestDate = dt;
                        best = m;
                    }
                }
                if (best != null)
                    return best;
                return matches.OrderByDescending(m => m.StartOp).First();
            }

            if (field.Equals("PERITO", StringComparison.OrdinalIgnoreCase))
            {
                var validPerito = matches
                    .Where(m => IsValidPeritoValue(m.ValueText))
                    .ToList();
                if (validPerito.Count == 0)
                    return null;

                // Prefer title-case names (usually from "Interessado: ..." line).
                var titled = validPerito
                    .Where(m => IsTitleLikeValue(m.ValueText ?? ""))
                    .OrderByDescending(m => (m.ValueText ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
                    .ThenByDescending(m => (m.ValueText ?? "").Length)
                    .ThenByDescending(m => m.Score)
                    .ToList();
                if (titled.Count > 0)
                    return titled[0];

                var best = validPerito
                    .OrderByDescending(m => (m.ValueText ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
                    .ThenByDescending(m => (m.ValueText ?? "").Length)
                    .ThenByDescending(m => m.Score)
                    .FirstOrDefault();
                if (best != null)
                    return best;
            }

            return matches
                .OrderByDescending(m => m.Score)
                .First();
        }
        private static void ApplyHonorariosDerivedFields(
            MatchOptions options,
            List<string> orderedA,
            Dictionary<string, List<FieldMatch>> fieldsA,
            Dictionary<string, string> values)
        {
            if (!options.UseHonorarios)
                return;
            if (!orderedA.Contains("ESPECIALIDADE", StringComparer.OrdinalIgnoreCase) &&
                !orderedA.Contains("ESPECIE_DA_PERICIA", StringComparer.OrdinalIgnoreCase) &&
                !orderedA.Contains("FATOR", StringComparer.OrdinalIgnoreCase) &&
                !orderedA.Contains("VALOR_TABELADO_ANEXO_I", StringComparer.OrdinalIgnoreCase))
                return;

            try
            {
                var backfill = HonorariosBackfill.Apply(values, options.PatternsPath);
                if (!backfill.IsDespacho)
                    return;

                if (backfill.TryGetField("PERITO", out var perito, out var peritoConf))
                    AddDerivedFieldMatch(fieldsA, "PERITO", perito, peritoConf);
                if (backfill.TryGetField("CPF_PERITO", out var cpf, out var cpfConf))
                    AddDerivedFieldMatch(fieldsA, "CPF_PERITO", cpf, cpfConf);
                if (backfill.TryGetField("ESPECIALIDADE", out var especialidade, out var espConf))
                    AddDerivedFieldMatch(fieldsA, "ESPECIALIDADE", especialidade, espConf);
                if (backfill.TryGetField("ESPECIE_DA_PERICIA", out var especie, out var especieConf))
                    AddDerivedFieldMatch(fieldsA, "ESPECIE_DA_PERICIA", especie, especieConf);
                if (backfill.TryGetField("FATOR", out var fator, out var fatorConf))
                    AddDerivedFieldMatch(fieldsA, "FATOR", fator, fatorConf);
                if (backfill.TryGetField("VALOR_TABELADO_ANEXO_I", out var valorTab, out var valorTabConf))
                    AddDerivedFieldMatch(fieldsA, "VALOR_TABELADO_ANEXO_I", valorTab, valorTabConf);
                if (backfill.TryGetField("VALOR_ARBITRADO_JZ", out var valorJz, out var valorJzConf))
                    AddDerivedFieldMatch(fieldsA, "VALOR_ARBITRADO_JZ", valorJz, valorJzConf);
                if (backfill.TryGetField("VALOR_ARBITRADO_FINAL", out var valorFinal, out var valorFinalConf))
                    AddDerivedFieldMatch(fieldsA, "VALOR_ARBITRADO_FINAL", valorFinal, valorFinalConf);

                // Preenche valores derivados quando o campo ainda não estava presente.
                TryFillDerivedValue(values, fieldsA, "PERITO");
                TryFillDerivedValue(values, fieldsA, "CPF_PERITO");
                TryFillDerivedValue(values, fieldsA, "ESPECIALIDADE");
                TryFillDerivedValue(values, fieldsA, "ESPECIE_DA_PERICIA");
                TryFillDerivedValue(values, fieldsA, "FATOR");
                TryFillDerivedValue(values, fieldsA, "VALOR_TABELADO_ANEXO_I");
                TryFillDerivedValue(values, fieldsA, "VALOR_ARBITRADO_JZ");
            }
            catch
            {
                // nao bloquear o match por causa de honorarios
            }
        }

        private static void TryFillDerivedValue(
            Dictionary<string, string> values,
            Dictionary<string, List<FieldMatch>> fields,
            string field)
        {
            if (values == null || fields == null || string.IsNullOrWhiteSpace(field))
                return;
            if (values.TryGetValue(field, out var existing) && !string.IsNullOrWhiteSpace(existing))
                return;
            if (fields.TryGetValue(field, out var list) && list != null && list.Count > 0)
            {
                var pick = list[0];
                if (!string.IsNullOrWhiteSpace(pick.ValueText))
                    values[field] = pick.ValueText;
            }
        }

        private static void AddDerivedFieldMatch(
            Dictionary<string, List<FieldMatch>> fields,
            string field,
            string value,
            double confidence)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            if (fields.TryGetValue(field, out var list) && list.Count > 0)
            {
                if (field.Equals("PERITO", StringComparison.OrdinalIgnoreCase))
                {
                    var cur = list[0].ValueText ?? "";
                    var cand = value ?? "";
                    var curKey = NormalizeForContains(cur).Replace(" ", "");
                    var candKey = NormalizeForContains(cand).Replace(" ", "");
                    var curHasSpace = cur.Contains(' ');
                    var candHasSpace = cand.Contains(' ');
                    if (!string.IsNullOrWhiteSpace(candKey) &&
                        (candHasSpace && !curHasSpace || string.Equals(curKey, candKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        // prefer catalog formatting if it matches the same name without spaces
                        list.Insert(0, new FieldMatch
                        {
                            Field = field,
                            ValueText = value.Trim(),
                            Score = Math.Min(1.0, confidence > 0 ? confidence : 0.90),
                            Kind = "honorarios",
                            StartOp = 0,
                            EndOp = 0
                        });
                    }
                }
                return;
            }
            var score = confidence > 0 ? Math.Min(1.0, confidence) : 0.90;
            fields[field] = new List<FieldMatch>
            {
                new FieldMatch
                {
                    Field = field,
                    ValueText = value.Trim(),
                    Score = score,
                    Kind = "honorarios",
                    StartOp = 0,
                    EndOp = 0
                }
            };
        }
        private static void PrintValidatorSummary(MatchOptions options, Dictionary<string, string> values)
        {
            var issues = ValidatorDiagnostics.CollectSummaryIssues(values);

            if (!options.Log)
            {
                if (issues.Count == 0)
                {
                    _lastValidatorSummary = "ok";
                    Console.WriteLine("[VALIDATOR] ok");
                }
                else
                {
                    _lastValidatorSummary = string.Join("; ", issues);
                    Console.WriteLine("[VALIDATOR] " + string.Join("; ", issues));
                }
                return;
            }

            if (issues.Count == 0)
            {
                _lastValidatorSummary = "ok";
                LogStep(options, CGreen, "[VALIDATOR]", "ok");
            }
            else
            {
                _lastValidatorSummary = string.Join("; ", issues);
                LogStep(options, CRed, "[VALIDATOR]", string.Join("; ", issues));
            }
        }

        private static bool ContainsInstitutional(string? value)
        {
            return ValidatorRules.ContainsInstitutional(value);
        }

        private static bool LooksLikeCpf(string? value)
        {
            return ValidatorRules.LooksLikeCpf(value);
        }

        private static bool ContainsCpfPattern(string? value)
        {
            return ValidatorRules.ContainsCpfPattern(value);
        }

        private static bool ContainsVaraComarca(string? value)
        {
            return ValidatorRules.ContainsVaraComarca(value);
        }

        private static bool IsPeritoNameFromCatalog(string? value, PeritoCatalog? catalog, out double confidence)
        {
            return ValidatorRules.IsPeritoNameFromCatalog(value, catalog, out confidence);
        }

        private static string StripKnownLabelPrefix(string? value)
        {
            return ValidatorRules.StripKnownLabelPrefix(value);
        }

        private static bool IsValidFieldFormat(string field, string value)
        {
            return ValidatorRules.IsValidFieldFormat(
                field,
                value,
                peritoValidator: IsValidPeritoValue,
                partyValidator: LooksLikePartyValue,
                especialidadeValidator: LooksLikeEspecialidadeValue,
                comarcaValidator: LooksLikeComarcaValue,
                varaValidator: LooksLikeVaraValue);
        }

        private static bool ContainsProcessualNoise(string value)
        {
            return ValidatorRules.ContainsProcessualNoise(value);
        }

        private static bool ContainsDocumentBoilerplate(string value)
        {
            return ValidatorRules.ContainsDocumentBoilerplate(value);
        }

        private static bool LooksLikeEspecialidadeValue(string value)
        {
            return ValidatorRules.LooksLikeEspecialidadeValue(value, ContainsEspecialidadeToken);
        }

        private static bool LooksLikeComarcaValue(string value)
        {
            return ValidatorRules.LooksLikeComarcaValue(value);
        }

        private static bool LooksLikeVaraValue(string value)
        {
            return ValidatorRules.LooksLikeVaraValue(value);
        }

        private static bool LooksLikePartyValue(string value)
        {
            return ValidatorRules.LooksLikePartyValue(
                value,
                institutionalCheck: ContainsInstitutional,
                boilerplateCheck: ContainsDocumentBoilerplate,
                processualNoiseCheck: ContainsProcessualNoise);
        }

        private static bool ContainsEmail(string value)
        {
            return ValidatorRules.ContainsEmail(value);
        }

        private static bool LooksLikePersonNameLoose(string text)
        {
            return ValidatorRules.LooksLikePersonNameLoose(text);
        }

        private static bool IsValueValidForField(string field, string value, PeritoCatalog? catalog, out string reason)
        {
            return ValidatorRules.IsValueValidForField(
                field,
                value,
                catalog,
                normalizeValueByField: NormalizeValueByField,
                isValidFieldFormat: IsValidFieldFormat,
                out reason);
        }

        private static void ApplyValidatorFiltersAndReanalysis(
            MatchOptions options,
            Dictionary<string, List<FieldMatch>> fieldMatches,
            List<string> orderedFields,
            string fullNorm,
            string streamNorm,
            string opsNorm)
        {
            if (fieldMatches == null)
                return;

            var catalog = ValidatorContext.GetPeritoCatalog();
            ValidatorRules.ApplyValidatorFiltersAndReanalysis(
                fieldMatches: fieldMatches,
                orderedFields: orderedFields,
                fullNorm: fullNorm,
                streamNorm: streamNorm,
                opsNorm: opsNorm,
                catalog: catalog,
                getValue: m => m.ValueText,
                setValue: (m, v) => m.ValueText = v,
                cloneMatch: (m, newField, kind) => CloneMatch(m, newField, kind),
                createMatch: (field, candidate, score, kind) => new FieldMatch
                {
                    Field = field,
                    ValueText = candidate,
                    Score = score,
                    Kind = kind
                },
                normalizeValueByField: NormalizeValueByField,
                isValueValidForField: IsValueValidForField,
                isPeritoNameFromCatalog: IsPeritoNameFromCatalog,
                containsCpfPattern: ContainsCpfPattern,
                findReanalysisCandidate: (field, full, stream, ops) =>
                {
                    string? candidate = null;

                    if (!string.IsNullOrWhiteSpace(full))
                    {
                        if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase))
                            candidate = TryExtractPromoventeFromEmFaceContext(full);
                        else if (field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                            candidate = TryExtractPromovidoFromEmFaceContext(full);

                        if (string.IsNullOrWhiteSpace(candidate))
                        {
                            if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase))
                                candidate = TryExtractPromoventeFromLine(full);
                            else if (field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                                candidate = TryExtractPromovidoFromLine(full);
                        }

                        if (string.IsNullOrWhiteSpace(candidate))
                            candidate = ExtractRegexValueFromCatalog(field, full) ?? ExtractRegexValueFromCatalogLoose(field, full);
                    }

                    if (string.IsNullOrWhiteSpace(candidate) && !string.IsNullOrWhiteSpace(stream))
                    {
                        if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase))
                            candidate = TryExtractPromoventeFromEmFaceContext(stream);
                        else if (field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                            candidate = TryExtractPromovidoFromEmFaceContext(stream);

                        if (string.IsNullOrWhiteSpace(candidate))
                        {
                            if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase))
                                candidate = TryExtractPromoventeFromLine(stream);
                            else if (field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                                candidate = TryExtractPromovidoFromLine(stream);
                        }

                        if (string.IsNullOrWhiteSpace(candidate))
                            candidate = ExtractRegexValueFromCatalogLoose(field, stream);
                    }

                    if (string.IsNullOrWhiteSpace(candidate) && !string.IsNullOrWhiteSpace(ops))
                    {
                        if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase))
                            candidate = TryExtractPromoventeFromEmFaceContext(ops);
                        else if (field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                            candidate = TryExtractPromovidoFromEmFaceContext(ops);

                        if (string.IsNullOrWhiteSpace(candidate))
                        {
                            if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase))
                                candidate = TryExtractPromoventeFromLine(ops);
                            else if (field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                                candidate = TryExtractPromovidoFromLine(ops);
                        }

                        if (string.IsNullOrWhiteSpace(candidate))
                            candidate = ExtractRegexValueFromCatalogLoose(field, ops);
                    }

                    return candidate;
                });
        }

        private static bool ShouldRejectByValidator(
            Dictionary<string, string> values,
            HashSet<string> optionalFields,
            string patternsPath,
            out string reason)
        {
            var catalog = ValidatorContext.GetPeritoCatalog();
            return ValidatorRules.ShouldRejectByValidator(
                values,
                optionalFields,
                patternsPath,
                catalog,
                IsValueValidForField,
                out reason);
        }
        private static void PrintHonorariosSummary(MatchOptions options, Dictionary<string, string> values)
        {
            if (!options.UseHonorarios)
                return;

            try
            {
                var snapshot = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
                var backfill = HonorariosBackfill.Apply(snapshot, options.PatternsPath);
                var summary = backfill.Summary ?? new HonorariosSummary();
                var side = summary.PdfA;
                var summaryText = $"doc={backfill.DocType} status={side?.Status} especialidade={side?.Especialidade} valor_tabelado={side?.ValorTabeladoAnexoI} fator={side?.Fator}";
                if (!options.Log)
                {
                    _lastHonorariosSummary = summaryText;
                    Console.WriteLine("[HONORARIOS] " + summaryText);
                    if (summary.Errors != null && summary.Errors.Count > 0)
                    {
                        _lastHonorariosSummary = summaryText + " errors=" + string.Join(", ", summary.Errors);
                        Console.WriteLine("[HONORARIOS] errors=" + string.Join(", ", summary.Errors));
                    }
                    return;
                }

                _lastHonorariosSummary = summaryText;
                LogStep(options, CCyan, "[HONORARIOS]", summaryText);
                if (summary.Errors != null && summary.Errors.Count > 0)
                {
                    _lastHonorariosSummary = summaryText + " errors=" + string.Join(", ", summary.Errors);
                    LogStep(options, CRed, "[HONORARIOS]", "errors=" + string.Join(", ", summary.Errors));
                }
            }
            catch (Exception ex)
            {
                _lastHonorariosSummary = "erro=" + ex.Message;
                if (!options.Log)
                    Console.WriteLine("[HONORARIOS] erro=" + ex.Message);
                else
                    LogStep(options, CRed, "[HONORARIOS]", "erro=" + ex.Message);
            }
        }

        private sealed class PairInputItem
        {
            public string File { get; set; } = "";
            public int BestStart { get; set; }
            public int Pair { get; set; } = 2;
        }

        private sealed class AlignModelContext
        {
            public string Path { get; set; } = "";
            public int Page1 { get; set; }
            public int Obj1 { get; set; }
            public int Page2 { get; set; }
            public int Obj2 { get; set; }
            public int Backoff { get; set; } = 2;
            public bool HasPage1 => Page1 > 0 && Obj1 > 0;
            public bool HasPage2 => Page2 > 0 && Obj2 > 0;
        }

        private static AlignModelContext? BuildAlignModelContext(MatchOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.AlignModelPath))
                return null;
            var modelPath = options.AlignModelPath!;
            if (!File.Exists(modelPath))
            {
                Console.WriteLine("Align model nao encontrado: " + modelPath);
                return null;
            }

            try
            {
                using var reader = new PdfReader(modelPath);
                using var doc = new PdfDocument(reader);
                int total = doc.GetNumberOfPages();
                var ctx = new AlignModelContext { Path = modelPath };

                if (total >= 1)
                {
                    var s1 = SelectBestStreamOnPage(doc, 1, options.OpFilter);
                    ctx.Page1 = 1;
                    ctx.Obj1 = s1.ObjId;
                }
                if (total >= 2)
                {
                    var s2 = SelectBestStreamOnPage(doc, 2, options.OpFilter);
                    ctx.Page2 = 2;
                    ctx.Obj2 = s2.ObjId;
                }

                if (!ctx.HasPage1 && !ctx.HasPage2)
                {
                    Console.WriteLine("Align model sem stream valido: " + Path.GetFileName(modelPath));
                    return null;
                }

                LogStep(options, CMagenta, "[ALIGN_MODEL]", $"pdf={Path.GetFileName(modelPath)} p1=o{ctx.Obj1} p2=o{ctx.Obj2}");
                return ctx;
            }
            catch
            {
                Console.WriteLine("Falha ao abrir align model: " + modelPath);
                return null;
            }
        }

        private static void RunPatternMatchFromPairs(MatchOptions options, List<FieldPatternEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(options.PairsPath))
                return;
            if (!File.Exists(options.PairsPath))
            {
                Console.WriteLine("Arquivo de pares nao encontrado: " + options.PairsPath);
                return;
            }

            var pairs = LoadPairsFromJson(options.PairsPath);
            if (pairs.Count == 0)
            {
                Console.WriteLine("Nenhum par encontrado no JSON.");
                return;
            }

            LogStep(options, CMagenta, "[PAIRS]", $"path={Path.GetFileName(options.PairsPath)} total={pairs.Count}");

            var optionalFields = ReadFieldListFromPatterns(options.PatternsPath, "optional_fields", "optionalFields", "optional");
            var requiredAll = BuildRequiredFieldSet(entries, options.Fields, optionalFields);
            var groupsAll = BuildGroups(entries, requiredAll);
            var page1Fields = ReadFieldListFromPatterns(options.PatternsPath, "page1_fields", "page1Fields", "p1_fields", "page1").ToList();
            var page2Fields = ReadFieldListFromPatterns(options.PatternsPath, "page2_fields", "page2Fields", "p2_fields", "page2").ToList();
            var groups = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Field))
                .GroupBy(e => e.Field!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var results = new List<Dictionary<string, object?>>();
            var alignModel = BuildAlignModelContext(options);
            int processed = 0;
            int skipped = 0;

            foreach (var item in pairs)
            {
                if (string.IsNullOrWhiteSpace(item.File))
                {
                    skipped++;
                    continue;
                }
                if (!File.Exists(item.File))
                {
                    LogStep(options, CRed, "[SKIP]", $"pdf=not_found file={item.File}");
                    skipped++;
                    continue;
                }

                using var reader = new PdfReader(item.File);
                using var doc = new PdfDocument(reader);
                int totalPages = doc.GetNumberOfPages();
                int pair = item.Pair > 0 ? item.Pair : 2;
                int start = item.BestStart;
                if (start <= 0 || start + pair - 1 > totalPages)
                {
                    LogStep(options, CRed, "[SKIP]", $"pdf={Path.GetFileName(item.File)} pair=p{start}-p{start + pair - 1} totalPages={totalPages}");
                    skipped++;
                    continue;
                }

                var page1 = start;
                var page2 = start + 1;

                LogStep(options, CCyan, "[ITEM]", $"pdf={Path.GetFileName(item.File)} pair=p{page1}-p{page2} pages={totalPages}");

                var p1 = SelectBestStreamOnPage(doc, page1, options.OpFilter);
                var p2 = SelectBestStreamOnPage(doc, page2, options.OpFilter);

                if (p1.ObjId == 0 && p2.ObjId == 0)
                {
                    LogStep(options, CRed, "[SKIP]", $"pdf={Path.GetFileName(item.File)} sem_stream");
                    skipped++;
                    continue;
                }

                var orderedA = page1Fields.Count > 0 ? page1Fields : groupsAll.Select(g => g.Key).ToList();
                AppendOptionalFields(orderedA, optionalFields);
                var orderedB = page2Fields.Count > 0 ? page2Fields : groupsAll.Select(g => g.Key).ToList();
                AppendOptionalFields(orderedB, optionalFields);
                var topA = PickTopAnchors(page1Fields);
                var bottomA = PickBottomAnchors(page1Fields);
                var topB = PickTopAnchors(page2Fields);
                var bottomB = PickBottomAnchors(page2Fields);

                var fullTokensA = p1.Tokens;
                var fullRawA = p1.RawTokens;
                var fullTokensB = p2.Tokens;
                var fullRawB = p2.RawTokens;

                var tokensA = fullTokensA;
                var rawA = fullRawA;
                var tokensB = fullTokensB;
                var rawB = fullRawB;

                List<TokenInfo>? expandTokensA = null;
                List<TokenInfo>? expandRawA = null;
                List<TokenInfo>? expandTokensB = null;
                List<TokenInfo>? expandRawB = null;

                LogStep(options, CBlue, "[STREAM]", $"p{page1} obj={p1.ObjId} tokens={tokensA.Count} raw={rawA.Count}");
                LogStep(options, CBlue, "[STREAM]", $"p{page2} obj={p2.ObjId} tokens={tokensB.Count} raw={rawB.Count}");

                if (alignModel?.HasPage1 == true && p1.ObjId > 0)
                {
                    LogStep(options, CCyan, "[TEXTOPSALIGN]", $"model=p{alignModel.Page1} o{alignModel.Obj1} -> target=p{page1} o{p1.ObjId}");
                    var range = ObjectsTextOpsAlign.ComputeRangeForSelections(
                        alignModel.Path,
                        item.File,
                        alignModel.Page1,
                        alignModel.Obj1,
                        page1,
                        p1.ObjId,
                        options.OpFilter,
                        alignModel.Backoff);

                    if (range.HasValue)
                    {
                        var ft = FilterTokensByOpRange(fullTokensA, range.StartOp, range.EndOp);
                        var fr = FilterTokensByOpRange(fullRawA, range.StartOp, range.EndOp);
                        if (ft.Count > 0) tokensA = ft;
                        if (fr.Count > 0) rawA = fr;
                    }

                    if (options.Log)
                        LogStep(options, CMagenta, "[ALIGN]", $"p{page1} o{p1.ObjId} op{range.StartOp}-op{range.EndOp} has={range.HasValue}");

                    var expandRange = ObjectsTextOpsAlign.ComputeRangeForSelections(
                        alignModel.Path,
                        item.File,
                        alignModel.Page1,
                        alignModel.Obj1,
                        page1,
                        p1.ObjId,
                        options.OpFilter,
                        alignModel.Backoff + 40);

                    if (expandRange.HasValue)
                    {
                        var ft = FilterTokensByOpRange(fullTokensA, expandRange.StartOp, expandRange.EndOp);
                        var fr = FilterTokensByOpRange(fullRawA, expandRange.StartOp, expandRange.EndOp);
                        if (ft.Count > 0) expandTokensA = ft;
                        if (fr.Count > 0) expandRawA = fr;
                    }
                }

                if (alignModel?.HasPage2 == true && p2.ObjId > 0)
                {
                    LogStep(options, CCyan, "[TEXTOPSALIGN]", $"model=p{alignModel.Page2} o{alignModel.Obj2} -> target=p{page2} o{p2.ObjId}");
                    var range = ObjectsTextOpsAlign.ComputeRangeForSelections(
                        alignModel.Path,
                        item.File,
                        alignModel.Page2,
                        alignModel.Obj2,
                        page2,
                        p2.ObjId,
                        options.OpFilter,
                        alignModel.Backoff);

                    if (range.HasValue)
                    {
                        var ft = FilterTokensByOpRange(fullTokensB, range.StartOp, range.EndOp);
                        var fr = FilterTokensByOpRange(fullRawB, range.StartOp, range.EndOp);
                        if (ft.Count > 0) tokensB = ft;
                        if (fr.Count > 0) rawB = fr;
                    }

                    if (options.Log)
                        LogStep(options, CMagenta, "[ALIGN]", $"p{page2} o{p2.ObjId} op{range.StartOp}-op{range.EndOp} has={range.HasValue}");

                    var expandRange = ObjectsTextOpsAlign.ComputeRangeForSelections(
                        alignModel.Path,
                        item.File,
                        alignModel.Page2,
                        alignModel.Obj2,
                        page2,
                        p2.ObjId,
                        options.OpFilter,
                        alignModel.Backoff + 40);

                    if (expandRange.HasValue)
                    {
                        var ft = FilterTokensByOpRange(fullTokensB, expandRange.StartOp, expandRange.EndOp);
                        var fr = FilterTokensByOpRange(fullRawB, expandRange.StartOp, expandRange.EndOp);
                        if (ft.Count > 0) expandTokensB = ft;
                        if (fr.Count > 0) expandRawB = fr;
                    }
                }

                var (fieldsA, rejectsA) = BuildFieldMatchesForTokensWithRejects(
                    options, tokensA, rawA, groups, orderedA, topA, bottomA, item.File,
                    expandTokensA, expandRawA, fullTokensA, fullRawA);
                var (fieldsB, rejectsB) = BuildFieldMatchesForTokensWithRejects(
                    options, tokensB, rawB, groups, orderedB, topB, bottomB, item.File,
                    expandTokensB, expandRawB, fullTokensB, fullRawB);

                // Fulltext + validação/reanálise (p1)
                if (p1.ObjId > 0)
                {
                    var fullBlocksA = ExtractPatternBlocksFromPdf(item.File, page1, p1.ObjId, options.OpFilter);
                    var fullTextStreamA = TryExtractFullText(item.File, p1.ObjId, options.Log);
                    var fullTextBlocksA = BuildFullTextFromBlocks(fullBlocksA);
                    var fullTextOpsA = BuildFullTextFromTextOps(item.File, p1.ObjId, options.Log);
                    var fullTextA = PickBestFullText(fullTextOpsA, fullTextBlocksA, fullTextStreamA);
                    if (!string.IsNullOrWhiteSpace(fullTextA))
                    {
                        ApplyFullTextOverrides(fieldsA, fullTextA, fullBlocksA, fullTextStreamA, fullTextOpsA, options.Log);
                        var fullNormA = TextNormalization.NormalizePatternText(fullTextA);
                        var streamNormA = TextNormalization.NormalizeWhitespace(TextNormalization.FixMissingSpaces(fullTextStreamA ?? ""));
                        var opsNormA = TextNormalization.NormalizePatternText(fullTextOpsA ?? "");
                        ApplyValidatorFiltersAndReanalysis(options, fieldsA, orderedA, fullNormA, streamNormA, opsNormA);
                    }
                }

                // Fulltext + validação/reanálise (p2)
                if (p2.ObjId > 0)
                {
                    var fullBlocksB = ExtractPatternBlocksFromPdf(item.File, page2, p2.ObjId, options.OpFilter);
                    var fullTextStreamB = TryExtractFullText(item.File, p2.ObjId, options.Log);
                    var fullTextBlocksB = BuildFullTextFromBlocks(fullBlocksB);
                    var fullTextOpsB = BuildFullTextFromTextOps(item.File, p2.ObjId, options.Log);
                    var fullTextB = PickBestFullText(fullTextOpsB, fullTextBlocksB, fullTextStreamB);
                    if (!string.IsNullOrWhiteSpace(fullTextB))
                    {
                        ApplyFullTextOverrides(fieldsB, fullTextB, fullBlocksB, fullTextStreamB, fullTextOpsB, options.Log);
                        var fullNormB = TextNormalization.NormalizePatternText(fullTextB);
                        var streamNormB = TextNormalization.NormalizeWhitespace(TextNormalization.FixMissingSpaces(fullTextStreamB ?? ""));
                        var opsNormB = TextNormalization.NormalizePatternText(fullTextOpsB ?? "");
                        ApplyValidatorFiltersAndReanalysis(options, fieldsB, orderedB, fullNormB, streamNormB, opsNormB);
                    }
                }

                if (orderedB.Contains("VALOR_ARBITRADO_DE", StringComparer.OrdinalIgnoreCase))
                {
                    if (fieldsA.TryGetValue("VALOR_ARBITRADO_JZ", out var jzList) && jzList.Count > 0)
                    {
                        if (!HasGeorcMention(tokensB, rawB))
                        {
                            var derived = DeriveFieldMatch(jzList[0], "VALOR_ARBITRADO_DE", "derived_from_jz");
                            fieldsB["VALOR_ARBITRADO_DE"] = new List<FieldMatch> { derived };
                        }
                    }
                }
                if (orderedB.Contains("PROCESSO_JUDICIAL", StringComparer.OrdinalIgnoreCase))
                {
                    if (!fieldsB.TryGetValue("PROCESSO_JUDICIAL", out var pjB) || pjB.Count == 0)
                    {
                        if (fieldsA.TryGetValue("PROCESSO_JUDICIAL", out var pjA) && pjA.Count > 0)
                        {
                            fieldsB["PROCESSO_JUDICIAL"] = new List<FieldMatch>
                            {
                                DeriveFieldMatch(pjA[0], "PROCESSO_JUDICIAL", "derived_from_p1")
                            };
                        }
                    }
                }
                if (orderedB.Contains("PERITO", StringComparer.OrdinalIgnoreCase))
                {
                    if (!fieldsB.TryGetValue("PERITO", out var perB) || perB.Count == 0)
                    {
                        if (fieldsA.TryGetValue("PERITO", out var perA) && perA.Count > 0)
                        {
                            fieldsB["PERITO"] = new List<FieldMatch>
                            {
                                DeriveFieldMatch(perA[0], "PERITO", "derived_from_p1")
                            };
                        }
                    }
                }

                var values = BuildValuesFromMatches(orderedA, orderedB, fieldsA, fieldsB);
                if (orderedA.Contains("PROCESSO_ADMINISTRATIVO", StringComparer.OrdinalIgnoreCase) ||
                    orderedB.Contains("PROCESSO_ADMINISTRATIVO", StringComparer.OrdinalIgnoreCase))
                {
                    TryFillProcessoAdministrativoFromPdfName(item.File, values);
                }
                ApplyHonorariosDerivedFields(options, orderedA, fieldsA, values);
                PrintValidatorSummary(options, values);
                PrintHonorariosSummary(options, values);

                if (ShouldRejectByValidator(values, optionalFields, options.PatternsPath, out var rejectReason))
                {
                    LogStep(options, CRed, "[REJECT]", $"pdf={Path.GetFileName(item.File)} reason={rejectReason}");
                    skipped++;
                    continue;
                }

                var hitsA = fieldsA.Values.Count(v => v != null && v.Count > 0);
                var hitsB = fieldsB.Values.Count(v => v != null && v.Count > 0);
                LogStep(options, CGreen, "[RESULT]", $"p{page1} hits={hitsA}/{fieldsA.Count}  p{page2} hits={hitsB}/{fieldsB.Count}");

                results.Add(new Dictionary<string, object?>
                {
                    ["pdf"] = item.File,
                    ["pair"] = $"p{page1}-p{page2}",
                    ["values"] = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase),
                    ["page1"] = new Dictionary<string, object?>
                    {
                        ["page"] = page1,
                        ["obj"] = p1.ObjId,
                        ["fields"] = SerializeFieldMatches(fieldsA, options.Limit),
                        ["rejected"] = SerializeFieldRejects(rejectsA, 5)
                    },
                    ["page2"] = new Dictionary<string, object?>
                    {
                        ["page"] = page2,
                        ["obj"] = p2.ObjId,
                        ["fields"] = SerializeFieldMatches(fieldsB, options.Limit),
                        ["rejected"] = SerializeFieldRejects(rejectsB, 5)
                    }
                });
                processed++;
            }

            LogStep(options, CMagenta, "[DONE]", $"processed={processed} skipped={skipped} total={pairs.Count}");

            var payload = new Dictionary<string, object?>
            {
                ["pairs"] = options.PairsPath,
                ["patterns"] = Path.GetFileName(options.PatternsPath),
                ["items"] = results
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            if (!string.IsNullOrWhiteSpace(options.OutPath))
            {
                try
                {
                    var outDir = Path.GetDirectoryName(options.OutPath);
                    if (!string.IsNullOrWhiteSpace(outDir))
                        Directory.CreateDirectory(outDir);
                    File.WriteAllText(options.OutPath, json);
                    LogStep(options, CGreen, "[OUT]", $"saved={options.OutPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Falha ao salvar JSON: " + ex.Message);
                    Console.WriteLine(json);
                }
            }
            else
            {
                Console.WriteLine(json);
            }
        }

        private static Dictionary<string, object?> SerializeFieldMatches(Dictionary<string, List<FieldMatch>> matches, int limit)
        {
            var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in matches)
            {
                var list = kv.Value ?? new List<FieldMatch>();
                var items = list.Take(limit > 0 ? limit : list.Count).Select(m => new Dictionary<string, object?>
                {
                    ["score"] = m.Score,
                    ["op_range"] = $"op{m.StartOp}-op{m.EndOp}",
                    ["value"] = m.ValueText,
                    ["kind"] = m.Kind
                }).ToList();
                output[kv.Key] = items;
            }
            return output;
        }

        private static Dictionary<string, object?> SerializeFieldRejects(Dictionary<string, List<FieldReject>> rejects, int limit)
        {
            var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in rejects)
            {
                var list = kv.Value ?? new List<FieldReject>();
                var items = list
                    .OrderByDescending(r => r.Score)
                    .Take(limit > 0 ? limit : list.Count)
                    .Select(r => new Dictionary<string, object?>
                    {
                        ["score"] = r.Score,
                        ["reason"] = r.Reason,
                        ["kind"] = r.Kind,
                        ["prev_score"] = r.PrevScore,
                        ["next_score"] = r.NextScore,
                        ["value_score"] = r.ValueScore,
                        ["op_range"] = r.StartOp > 0 || r.EndOp > 0 ? $"op{r.StartOp}-op{r.EndOp}" : "",
                        ["value"] = r.ValueText
                    }).ToList();
                output[kv.Key] = items;
            }
            return output;
        }

        private static Dictionary<string, List<FieldMatch>> BuildFieldMatchesForTokens(
            MatchOptions options,
            List<TokenInfo> tokens,
            List<TokenInfo> rawTokens,
            List<IGrouping<string, FieldPatternEntry>> groups,
            List<string> orderedFields,
            List<string> topAnchors,
            List<string> bottomAnchors,
            string pdf)
        {
            var tokenPatterns = tokens.Select(t => t.Pattern).ToList();
            var rawPatterns = rawTokens.Select(t => t.Pattern).ToList();

            var fieldMatches = new Dictionary<string, List<FieldMatch>>(StringComparer.OrdinalIgnoreCase);
            var groupsByField = groups.ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            bool FieldHasRoi(string field)
            {
                if (!groupsByField.TryGetValue(field, out var entries))
                    return false;
                return entries.Any(e =>
                    !string.IsNullOrWhiteSpace(e.Band) ||
                    !string.IsNullOrWhiteSpace(e.YRange) ||
                    !string.IsNullOrWhiteSpace(e.XRange));
            }

            List<FieldMatch> FindFieldMatches(string field, List<TokenInfo> tks, List<string> tps, List<TokenInfo> rtk, List<string> rps)
            {
                if (!groupsByField.TryGetValue(field, out var entries))
                    return new List<FieldMatch>();
                var minScore = AdjustMinScore(field, options.MinScore);
                var matches = new List<FieldMatch>();
                foreach (var entry in entries)
                {
                    matches.AddRange(FindMatchesOrdered(tks, tps, rtk, rps, entry, minScore, pdf, options.MaxPairs, options.MaxCandidates, HasAdiantamento(tks, rtk), options.Log, options.UseRaw));
                }
                // Regex apenas quando DMP nao encontrou (ou campo depende de regex).
                bool alwaysRegex =
                    field.Equals("DATA_ARBITRADO_FINAL", StringComparison.OrdinalIgnoreCase) ||
                    field.Equals("VALOR_ARBITRADO_DE", StringComparison.OrdinalIgnoreCase) ||
                    field.Equals("VALOR_ARBITRADO_JZ", StringComparison.OrdinalIgnoreCase);
                if (matches.Count == 0 || alwaysRegex)
                {
                    foreach (var entry in entries)
                    {
                        var rx = FindRegexMatchesWithRoi(tks, entry, minScore, field, options.MaxCandidates, options.Log, null);
                        if (rx.Count == 0)
                            rx = FindRegexMatchesNoRoi(tks, entry, minScore, field, options.MaxCandidates, options.Log, null);
                        if (rx.Count > 0)
                            rx = FilterAdiantamento(field, rx, null, HasAdiantamento(tks, rtk));
                        matches = MergeMatches(matches, rx);
                    }
                }
                return matches
                    .OrderByDescending(m => m.Score)
                    .ThenBy(m => m.StartOp)
                    .ToList();
            }

            FieldMatch? BestMatchForFields(List<string> fields)
            {
                FieldMatch? best = null;
                foreach (var f in fields)
                {
                    var matches = FindFieldMatches(f, tokens, tokenPatterns, rawTokens, rawPatterns);
                    if (matches.Count == 0) continue;
                    var top = matches[0];
                    if (best == null || top.Score > best.Score)
                        best = top;
                    fieldMatches[f] = matches;
                }
                return best;
            }

            var topMatch = BestMatchForFields(topAnchors);
            var bottomMatch = BestMatchForFields(bottomAnchors);

            int corridorStart = topMatch?.EndOp ?? 0;
            int corridorEnd = bottomMatch?.StartOp ?? int.MaxValue;
            if (corridorStart > 0 && corridorEnd > 0 && corridorStart >= corridorEnd)
            {
                corridorStart = 0;
                corridorEnd = int.MaxValue;
            }
            if (corridorStart > 0 && corridorEnd < int.MaxValue)
            {
                var span = corridorEnd - corridorStart;
                if (span < 40)
                {
                    corridorStart = 0;
                    corridorEnd = int.MaxValue;
                }
            }

            foreach (var field in orderedFields)
            {
                if (fieldMatches.ContainsKey(field))
                    continue;
                if (!groupsByField.ContainsKey(field))
                    continue;

                var tks = tokens;
                var rtk = rawTokens;
                var applyCorridor = (corridorStart > 0 || corridorEnd < int.MaxValue) && !FieldHasRoi(field);
                if (applyCorridor)
                {
                    tks = FilterTokensByOpRange(tokens, corridorStart, corridorEnd);
                    rtk = FilterTokensByOpRange(rawTokens, corridorStart, corridorEnd);
                }
                var tps = tks.Select(t => t.Pattern).ToList();
                var rps = rtk.Select(t => t.Pattern).ToList();
                var matches = FindFieldMatches(field, tks, tps, rtk, rps);
                fieldMatches[field] = matches;

                if (applyCorridor && matches.Count > 0)
                {
                    var best = matches[0];
                    if (best.EndOp > corridorStart)
                        corridorStart = best.EndOp;
                    if (corridorStart >= corridorEnd)
                        break;
                }
            }

            ApplyComarcaWithinVara(fieldMatches);
            ApplyPeritoDerivedFields(fieldMatches);
            ApplyPeritoDerivedFields(fieldMatches);
            return fieldMatches;
        }

        private static (Dictionary<string, List<FieldMatch>> Matches, Dictionary<string, List<FieldReject>> Rejects) BuildFieldMatchesForTokensWithRejects(
            MatchOptions options,
            List<TokenInfo> tokens,
            List<TokenInfo> rawTokens,
            List<IGrouping<string, FieldPatternEntry>> groups,
            List<string> orderedFields,
            List<string> topAnchors,
            List<string> bottomAnchors,
            string pdf,
            List<TokenInfo>? expandTokens,
            List<TokenInfo>? expandRawTokens,
            List<TokenInfo>? fullTokens,
            List<TokenInfo>? fullRawTokens)
        {
            var tokenPatterns = tokens.Select(t => t.Pattern).ToList();
            var rawPatterns = rawTokens.Select(t => t.Pattern).ToList();
            var expandPatterns = expandTokens?.Select(t => t.Pattern).ToList();
            var expandRawPatterns = expandRawTokens?.Select(t => t.Pattern).ToList();
            var fullPatterns = fullTokens?.Select(t => t.Pattern).ToList();
            var fullRawPatterns = fullRawTokens?.Select(t => t.Pattern).ToList();

            var fieldMatches = new Dictionary<string, List<FieldMatch>>(StringComparer.OrdinalIgnoreCase);
            var fieldRejects = new Dictionary<string, List<FieldReject>>(StringComparer.OrdinalIgnoreCase);
            var groupsByField = groups.ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            bool FieldHasRoi(string field)
            {
                if (!groupsByField.TryGetValue(field, out var entries))
                    return false;
                return entries.Any(e =>
                    !string.IsNullOrWhiteSpace(e.Band) ||
                    !string.IsNullOrWhiteSpace(e.YRange) ||
                    !string.IsNullOrWhiteSpace(e.XRange));
            }

            List<FieldMatch> FindFieldMatches(string field, List<TokenInfo> tks, List<string> tps, List<TokenInfo> rtk, List<string> rps, List<FieldReject> rejects)
            {
                if (!groupsByField.TryGetValue(field, out var entries))
                    return new List<FieldMatch>();
                var minScore = AdjustMinScore(field, options.MinScore);
                var matches = new List<FieldMatch>();
                foreach (var entry in entries)
                {
                    matches.AddRange(FindMatchesOrdered(tks, tps, rtk, rps, entry, minScore, pdf, options.MaxPairs, options.MaxCandidates, HasAdiantamento(tks, rtk), options.Log, options.UseRaw, rejects));
                }
                // Regex entra como apoio (nao apenas fallback).
                foreach (var entry in entries)
                {
                    var rx = FindRegexMatchesWithRoi(tks, entry, minScore, field, options.MaxCandidates, options.Log, rejects);
                    if (rx.Count == 0)
                        rx = FindRegexMatchesNoRoi(tks, entry, minScore, field, options.MaxCandidates, options.Log, rejects);
                    if (rx.Count > 0)
                        rx = FilterAdiantamento(field, rx, rejects, HasAdiantamento(tks, rtk));
                    matches = MergeMatches(matches, rx);
                }
                return matches
                    .OrderByDescending(m => m.Score)
                    .ThenBy(m => m.StartOp)
                    .ToList();
            }

            FieldMatch? BestMatchForFields(List<string> fields)
            {
                FieldMatch? best = null;
                foreach (var f in fields)
                {
                    var rejects = new List<FieldReject>();
                    var matches = FindFieldMatches(f, tokens, tokenPatterns, rawTokens, rawPatterns, rejects);
                    if (matches.Count == 0 && expandTokens != null && expandRawTokens != null && expandPatterns != null && expandRawPatterns != null)
                    {
                        LogStep(options, CYellow, "[FALLBACK]", $"{f} -> expand_backoff");
                        matches = FindFieldMatches(f, expandTokens, expandPatterns, expandRawTokens, expandRawPatterns, rejects);
                    }
                    if (matches.Count == 0 && fullTokens != null && fullRawTokens != null && fullPatterns != null && fullRawPatterns != null)
                    {
                        LogStep(options, CYellow, "[FALLBACK]", $"{f} -> full_tokens");
                        matches = FindFieldMatches(f, fullTokens, fullPatterns, fullRawTokens, fullRawPatterns, rejects);
                    }
                    fieldRejects[f] = rejects;
                    if (matches.Count == 0) continue;
                    var top = matches[0];
                    if (best == null || top.Score > best.Score)
                        best = top;
                    fieldMatches[f] = matches;
                }
                return best;
            }

            var topMatch = BestMatchForFields(topAnchors);
            var bottomMatch = BestMatchForFields(bottomAnchors);

            int corridorStart = topMatch?.EndOp ?? 0;
            int corridorEnd = bottomMatch?.StartOp ?? int.MaxValue;
            if (corridorStart > 0 && corridorEnd > 0 && corridorStart >= corridorEnd)
            {
                corridorStart = 0;
                corridorEnd = int.MaxValue;
            }

            if (options.Log && corridorStart > 0 && corridorEnd < int.MaxValue)
                LogStep(options, CCyan, "[CORRIDOR]", $"op{corridorStart}-op{corridorEnd}");

            foreach (var field in orderedFields)
            {
                if (fieldMatches.ContainsKey(field))
                    continue;
                if (!groupsByField.ContainsKey(field))
                    continue;

                var tks = tokens;
                var rtk = rawTokens;
                var applyCorridor = (corridorStart > 0 || corridorEnd < int.MaxValue) && !FieldHasRoi(field);
                if (applyCorridor)
                {
                    tks = FilterTokensByOpRange(tokens, corridorStart, corridorEnd);
                    rtk = FilterTokensByOpRange(rawTokens, corridorStart, corridorEnd);
                }
                var tps = tks.Select(t => t.Pattern).ToList();
                var rps = rtk.Select(t => t.Pattern).ToList();
                var rejects = new List<FieldReject>();
                var matches = FindFieldMatches(field, tks, tps, rtk, rps, rejects);
                if (matches.Count == 0 && applyCorridor && expandTokens != null && expandRawTokens != null && expandPatterns != null && expandRawPatterns != null)
                {
                    LogStep(options, CYellow, "[FALLBACK]", $"{field} -> expand_backoff");
                    matches = FindFieldMatches(field, expandTokens, expandPatterns, expandRawTokens, expandRawPatterns, rejects);
                }
                if (matches.Count == 0 && applyCorridor && fullTokens != null && fullRawTokens != null && fullPatterns != null && fullRawPatterns != null)
                {
                    LogStep(options, CYellow, "[FALLBACK]", $"{field} -> full_tokens");
                    matches = FindFieldMatches(field, fullTokens, fullPatterns, fullRawTokens, fullRawPatterns, rejects);
                }
                fieldMatches[field] = matches;
                fieldRejects[field] = rejects;

                if (matches.Count > 0 && applyCorridor)
                {
                    var best = matches[0];
                    if (best.EndOp > corridorStart)
                        corridorStart = best.EndOp;
                    if (corridorStart >= corridorEnd)
                        break;
                }
            }

            // Fallback final (regex full): para campos ainda vazios.
            foreach (var field in orderedFields)
            {
                if (!groupsByField.ContainsKey(field))
                    continue;
                fieldMatches.TryGetValue(field, out var existing);
                if (existing != null && existing.Count > 0)
                    continue;

                var rxMatches = new List<FieldMatch>();
                foreach (var entry in groupsByField[field])
                {
                    var rx = FindRegexMatchesNoRoi(tokens, entry, options.MinScore, field, options.MaxCandidates, options.Log, null);
                    if (rx.Count > 0)
                        rx = FilterAdiantamento(field, rx, null, HasAdiantamento(tokens, rawTokens));
                    rxMatches.AddRange(rx);
                }
                if (rxMatches.Count > 0)
                    fieldMatches[field] = rxMatches.OrderByDescending(m => m.Score).ThenBy(m => m.StartOp).ToList();
            }

            ApplyComarcaWithinVara(fieldMatches);
            ApplyPeritoDerivedFields(fieldMatches);
            return (fieldMatches, fieldRejects);
        }

        private static List<PairInputItem> LoadPairsFromJson(string path)
        {
            var list = new List<PairInputItem>();
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int defaultPair = 2;
            if (TryGetPropertyIgnoreCase(root, "Pair", out var pairEl) && pairEl.ValueKind == JsonValueKind.Number)
                defaultPair = pairEl.GetInt32();

            IEnumerable<JsonElement> items;
            if (root.ValueKind == JsonValueKind.Array)
                items = root.EnumerateArray();
            else if (TryGetPropertyIgnoreCase(root, "Results", out var resultsEl) && resultsEl.ValueKind == JsonValueKind.Array)
                items = resultsEl.EnumerateArray();
            else if (TryGetPropertyIgnoreCase(root, "items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                items = itemsEl.EnumerateArray();
            else
                items = Array.Empty<JsonElement>();

            foreach (var it in items)
            {
                string file = "";
                if (TryGetPropertyIgnoreCase(it, "path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
                    file = pathEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(file) && TryGetPropertyIgnoreCase(it, "File", out var fileEl) && fileEl.ValueKind == JsonValueKind.String)
                    file = fileEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(file) && TryGetPropertyIgnoreCase(it, "file", out fileEl) && fileEl.ValueKind == JsonValueKind.String)
                    file = fileEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(file) && TryGetPropertyIgnoreCase(it, "pdf", out var pdfEl) && pdfEl.ValueKind == JsonValueKind.String)
                    file = pdfEl.GetString() ?? "";

                int bestStart = 0;
                if (TryGetPropertyIgnoreCase(it, "best_start", out var bs) && bs.ValueKind == JsonValueKind.Number)
                    bestStart = bs.GetInt32();
                if (bestStart <= 0 && TryGetPropertyIgnoreCase(it, "BestStart", out var bs2) && bs2.ValueKind == JsonValueKind.Number)
                    bestStart = bs2.GetInt32();

                int pair = defaultPair;
                if (TryGetPropertyIgnoreCase(it, "pair", out var pairEl2))
                {
                    if (pairEl2.ValueKind == JsonValueKind.Number)
                        pair = pairEl2.GetInt32();
                    else if (pairEl2.ValueKind == JsonValueKind.String)
                    {
                        var p = ParsePairString(pairEl2.GetString() ?? "");
                        if (p > 0) pair = p;
                    }
                }

                if (bestStart <= 0 && TryGetPropertyIgnoreCase(it, "pair", out var pairStr) && pairStr.ValueKind == JsonValueKind.String)
                {
                    var (start, count) = ParsePairStart(pairStr.GetString() ?? "");
                    if (start > 0) bestStart = start;
                    if (count > 0) pair = count;
                }

                if (string.IsNullOrWhiteSpace(file) || bestStart <= 0)
                    continue;

                list.Add(new PairInputItem { File = file, BestStart = bestStart, Pair = pair });
            }

            return list;
        }

        private static int ParsePairString(string value)
        {
            var (start, count) = ParsePairStart(value);
            return count;
        }

        private static (int Start, int Count) ParsePairStart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return (0, 0);
            var m = Regex.Match(value, @"p(?<a>\d+)\s*[-/]\s*p?(?<b>\d+)", RegexOptions.IgnoreCase);
            if (!m.Success)
                return (0, 0);
            if (!int.TryParse(m.Groups["a"].Value, out var a))
                return (0, 0);
            if (!int.TryParse(m.Groups["b"].Value, out var b))
                return (a, 0);
            var count = b >= a ? (b - a + 1) : 0;
            return (a, count);
        }

        private static (int ObjId, List<TokenInfo> Tokens, List<TokenInfo> RawTokens) SelectBestStreamOnPage(PdfDocument doc, int pageNumber, HashSet<string> opFilter, int preferredObjId = 0)
        {
            var page = doc.GetPage(pageNumber);
            if (page == null)
                return (0, new List<TokenInfo>(), new List<TokenInfo>());
            var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
            var contents = page.GetPdfObject().Get(PdfName.Contents);
            var pageHeight = page.GetPageSize().GetHeight();
            var pageWidth = page.GetPageSize().GetWidth();

            int bestObj = 0;
            int bestCount = 0;
            List<TokenInfo> bestTokens = new();
            List<TokenInfo> bestRaw = new();

            foreach (var stream in EnumerateStreams(contents))
            {
                int objId = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                var tokens = ExtractTokensFromStream(stream, resources, pageHeight, pageWidth, opFilter);
                var rawTokens = ExtractRawTokensFromStream(stream, resources, pageHeight, pageWidth, opFilter);
                int count = tokens.Count > 0 ? tokens.Count : rawTokens.Count;

                // Preserve detector-selected stream when available.
                if (preferredObjId > 0 && objId == preferredObjId && count > 0)
                    return (objId, tokens, rawTokens);

                if (count > bestCount)
                {
                    bestCount = count;
                    bestObj = objId;
                    bestTokens = tokens;
                    bestRaw = rawTokens;
                }
            }

            return (bestObj, bestTokens, bestRaw);
        }

        private static Dictionary<string, List<FieldMatch>> RunPatternMatchForObject(MatchOptions options, string pdf, int page, int obj, List<IGrouping<string, FieldPatternEntry>> groups, List<string> orderedFields, List<string> topAnchors, List<string> bottomAnchors, bool skipHeader = false, Dictionary<string, List<FieldMatch>>? extraFields = null)
        {
            var tokens = ExtractTokensFromPdf(pdf, page, obj, options.OpFilter);
            var rawTokens = ExtractRawTokensFromPdf(pdf, page, obj, options.OpFilter);
            if (tokens.Count == 0 && rawTokens.Count == 0)
            {
                Console.WriteLine("Sem tokens: " + pdf);
                return new Dictionary<string, List<FieldMatch>>(StringComparer.OrdinalIgnoreCase);
            }

            var tokenPatterns = tokens.Select(t => t.Pattern).ToList();
            var rawPatterns = rawTokens.Select(t => t.Pattern).ToList();

            if (!skipHeader)
                Console.WriteLine($"PDF: {Path.GetFileName(pdf)}");
            LogStep(options, CMagenta, "[ORDER]", $"fields={string.Join(",", orderedFields)}");
            LogStep(options, CMagenta, "[ANCHORS]", $"top={string.Join(",", topAnchors)} bottom={string.Join(",", bottomAnchors)}");

            var fieldMatches = new Dictionary<string, List<FieldMatch>>(StringComparer.OrdinalIgnoreCase);
            var groupsByField = groups.ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            bool FieldHasRoi(string field)
            {
                if (!groupsByField.TryGetValue(field, out var entries))
                    return false;
                return entries.Any(e =>
                    !string.IsNullOrWhiteSpace(e.Band) ||
                    !string.IsNullOrWhiteSpace(e.YRange) ||
                    !string.IsNullOrWhiteSpace(e.XRange));
            }

            List<FieldMatch> FindFieldMatches(string field, List<TokenInfo> tks, List<string> tps, List<TokenInfo> rtk, List<string> rps, List<FieldReject>? rejects = null)
            {
                if (!groupsByField.TryGetValue(field, out var entries))
                    return new List<FieldMatch>();
                var minScore = AdjustMinScore(field, options.MinScore);
                var matches = new List<FieldMatch>();
                foreach (var entry in entries)
                    matches.AddRange(FindMatchesOrdered(tks, tps, rtk, rps, entry, minScore, pdf, options.MaxPairs, options.MaxCandidates, HasAdiantamento(tks, rtk), options.Log, options.UseRaw, rejects));
                if (options.Log && field.Equals("PERITO", StringComparison.OrdinalIgnoreCase))
                    LogStep(options, CMagenta, "[PERITO_DMP]", $"count={matches.Count}");
                foreach (var entry in entries)
                {
                    var rx = FindRegexMatchesWithRoi(tks, entry, minScore, field, options.MaxCandidates, options.Log, rejects);
                    if (rx.Count == 0)
                        rx = FindRegexMatchesNoRoi(tks, entry, minScore, field, options.MaxCandidates, options.Log, rejects);
                    if (rx.Count > 0)
                        rx = FilterAdiantamento(field, rx, rejects, HasAdiantamento(tks, rtk));
                    matches = MergeMatches(matches, rx);
                }
                if (options.Log && field.Equals("PERITO", StringComparison.OrdinalIgnoreCase))
                    LogStep(options, CMagenta, "[PERITO_MERGED]", $"count={matches.Count}");
                matches = PreferRegexIfAvailable(field, matches);
                if (field.Equals("PERITO", StringComparison.OrdinalIgnoreCase))
                {
                    if (entries.Count > 0)
                    {
                        var fallback = FindPeritoFallbackFromTokens(tks, entries[0], minScore);
                        if (fallback.Count > 0)
                            matches = MergeMatches(matches, fallback);
                    }
                    // O valor deve vir do texto normalizado (typed). RAW só localiza.
                    foreach (var m in matches)
                    {
                        var spanTokens = FilterTokensByOpRange(tks, m.StartOp, m.EndOp);
                        if (spanTokens.Count > 0)
                        {
                            var rawText = BuildRawTokenTextPreserve(spanTokens, 0, spanTokens.Count - 1);
                            if (!string.IsNullOrWhiteSpace(rawText))
                                m.ValueText = rawText;
                        }
                    }
                    if (options.Log && matches.Count > 0)
                    {
                        var preview = matches.Take(5)
                            .Select(m =>
                            {
                                var clean = CleanPeritoValue(m.ValueText);
                                return $"{TrimSnippet(m.ValueText)} => clean:{TrimSnippet(clean)} => {ExplainPeritoReject(clean)}";
                            });
                        LogStep(options, CYellow, "[PERITO_CAND]", string.Join(" | ", preview));
                    }
                    var cleaned = new List<FieldMatch>(matches.Count);
                    foreach (var m in matches)
                    {
                        var cleanVal = CleanPeritoValue(m.ValueText);
                        var reason = ExplainPeritoReject(cleanVal);
                        if (string.IsNullOrWhiteSpace(cleanVal))
                            cleanVal = NormalizeValueByField("PERITO", m.ValueText);
                        m.ValueText = cleanVal;
                        if (reason != "ok")
                            m.Score *= 0.85;
                        cleaned.Add(m);
                    }
                    matches = cleaned;
                }
                return matches
                    .OrderByDescending(m => m.Score)
                    .ThenBy(m => m.StartOp)
                    .ToList();
            }

            FieldMatch? BestMatchForFields(List<string> fields)
            {
                FieldMatch? best = null;
                foreach (var f in fields)
                {
                    var matches = FindFieldMatches(f, tokens, tokenPatterns, rawTokens, rawPatterns);
                    if (matches.Count == 0) continue;
                    var top = matches[0];
                    if (best == null || top.Score > best.Score)
                        best = top;
                    fieldMatches[f] = matches;
                }
                return best;
            }

            var topMatch = BestMatchForFields(topAnchors);
            var bottomMatch = BestMatchForFields(bottomAnchors);

            int corridorStart = topMatch?.EndOp ?? 0;
            int corridorEnd = bottomMatch?.StartOp ?? int.MaxValue;
            if (corridorStart > 0 && corridorEnd > 0 && corridorStart >= corridorEnd)
            {
                corridorStart = 0;
                corridorEnd = int.MaxValue;
            }

            if (options.Log && corridorStart > 0 && corridorEnd < int.MaxValue)
                LogStep(options, CCyan, "[CORRIDOR]", $"op{corridorStart}-op{corridorEnd}");

            foreach (var field in orderedFields)
            {
                if (fieldMatches.ContainsKey(field))
                    continue;
                if (!groupsByField.ContainsKey(field))
                    continue;

                var tks = tokens;
                var rtk = rawTokens;
                var applyCorridor = (corridorStart > 0 || corridorEnd < int.MaxValue) && !FieldHasRoi(field);
                if (applyCorridor)
                {
                    tks = FilterTokensByOpRange(tokens, corridorStart, corridorEnd);
                    rtk = FilterTokensByOpRange(rawTokens, corridorStart, corridorEnd);
                }
                var tps = tks.Select(t => t.Pattern).ToList();
                var rps = rtk.Select(t => t.Pattern).ToList();
                var rejects = new List<FieldReject>();
                var matches = FindFieldMatches(field, tks, tps, rtk, rps, rejects);
                if (matches.Count == 0 && applyCorridor)
                {
                    if (options.Log)
                        LogStep(options, CCyan, "[CORRIDOR_FALLBACK]", $"{field} => retry full page (corridor vazio)");
                    tks = tokens;
                    rtk = rawTokens;
                    tps = tks.Select(t => t.Pattern).ToList();
                    rps = rtk.Select(t => t.Pattern).ToList();
                    matches = FindFieldMatches(field, tks, tps, rtk, rps, rejects);
                }
                // Regex (apoio) ja foi mesclado acima.
                fieldMatches[field] = matches;
                LogStep(options, CGreen, "[FIELD]", $"{field} matches={matches.Count}");
                if (options.Log)
                {
                    if (matches.Count == 0)
                    {
                        LogStep(options, CRed, "[HITS]", $"{field} nenhum hit");
                    }
                    else
                    {
                        var showN = options.Limit > 0 ? Math.Min(options.Limit, matches.Count) : matches.Count;
                        LogStep(options, CYellow, "[HITS]", $"{field} candidatos={matches.Count} (mostrando {showN})");
                        for (int i = 0; i < showN; i++)
                        {
                            var m = matches[i];
                            var label = i == 0 ? "[CHOSEN]" : "[HIT]";
                            var color = i == 0 ? CGreen : CYellow;
                            var detail = $"kind={m.Kind} score={m.Score:F2} prev={m.PrevScore:F2} val={m.ValueScore:F2} next={m.NextScore:F2} op{m.StartOp}-op{m.EndOp} \"{TrimSnippet(m.ValueText)}\"";
                            LogStep(options, color, label, detail);
                        }
                        if (matches.Count > showN)
                            LogStep(options, CMagenta, "[HITS]", $"{field} preteridos={matches.Count - showN}");
                        // mostra candidatos com menor score (ainda aceitos)
                        var worst = matches.OrderBy(m => m.Score).ThenBy(m => m.StartOp).Take(4).ToList();
                        if (worst.Count > 0)
                        {
                            LogStep(options, CMagenta, "[LOW]", $"{field} candidatos(piores)={worst.Count}");
                            foreach (var m in worst)
                            {
                            var detail = $"field={field} kind={m.Kind} score={m.Score:F2} prev={m.PrevScore:F2} val={m.ValueScore:F2} next={m.NextScore:F2} op{m.StartOp}-op{m.EndOp} \"{TrimSnippet(m.ValueText)}\"";
                            LogStep(options, CMagenta, "[LOW]", detail);
                        }
                    }
                }
                    if (rejects.Count > 0)
                    {
                        var showR = Math.Min(4, rejects.Count);
                        LogStep(options, CRed, "[REJECTS]", $"{field} reprovados={rejects.Count} (mostrando {showR})");
                        foreach (var r in rejects.OrderByDescending(r => r.Score).Take(showR))
                        {
                            var detail = $"reason={r.Reason} kind={r.Kind} score={r.Score:F2} prev={r.PrevScore:F2} val={r.ValueScore:F2} next={r.NextScore:F2} op{r.StartOp}-op{r.EndOp} \"{TrimSnippet(r.ValueText)}\"";
                            LogStep(options, CRed, "[REJECT]", detail);
                        }
                    }
                    LogDispute(options, field, matches, rejects);
                }

                if (matches.Count > 0)
                {
                    var best = matches[0];
                    if (best.EndOp > corridorStart)
                        corridorStart = best.EndOp;
                    if (corridorStart >= corridorEnd)
                    {
                        // desliga o corredor para nao bloquear campos restantes
                        corridorStart = 0;
                        corridorEnd = int.MaxValue;
                    }
                }
            }

            ApplyComarcaWithinVara(fieldMatches);
            ApplyPeritoDerivedFields(fieldMatches);
            if (!fieldMatches.TryGetValue("PERITO", out var peritoAfter) || peritoAfter.Count == 0)
            {
                if (groupsByField.TryGetValue("PERITO", out var perEntries) && perEntries.Count > 0)
                {
                    var fallback = FindPeritoFallbackFromTokens(tokens, perEntries[0], options.MinScore);
                    fallback = fallback.Where(m => IsValidPeritoValue(m.ValueText)).ToList();
                    if (fallback.Count > 0)
                        fieldMatches["PERITO"] = fallback;
                }
            }
            if (extraFields != null)
            {
                foreach (var kv in extraFields)
                    fieldMatches[kv.Key] = kv.Value;
            }
            NormalizeMatchValues(fieldMatches);
            var fullBlocks = ExtractPatternBlocksFromPdf(pdf, page, obj, options.OpFilter);
            var fullTextStream = TryExtractFullText(pdf, obj, options.Log);
            var fullTextBlocks = BuildFullTextFromBlocks(fullBlocks);
            var fullTextOps = BuildFullTextFromTextOps(pdf, obj, options.Log);
            var fullText = PickBestFullText(fullTextOps, fullTextBlocks, fullTextStream);
            if (options.Log)
                LogStep(options, CYellow, "[FULLTEXT_SRC]", $"ops={fullTextOps.Length} blocks={fullTextBlocks.Length} stream={fullTextStream.Length}");
            if (!string.IsNullOrWhiteSpace(fullText))
            {
                if (options.Log)
                    LogStep(options, CCyan, "[FULLTEXT]", $"len={fullText.Length} sample=\"{TrimSnippet(fullText, 140)}\"");
                ApplyFullTextOverrides(fieldMatches, fullText, fullBlocks, fullTextStream, fullTextOps, options.Log);
            }

            // Validação forte + reanálise de campos vazios/contaminados
            if (!string.IsNullOrWhiteSpace(fullText))
            {
                var fullNorm = TextNormalization.NormalizePatternText(fullText);
                var streamNorm = TextNormalization.NormalizeWhitespace(TextNormalization.FixMissingSpaces(fullTextStream ?? ""));
                var opsNorm = TextNormalization.NormalizePatternText(fullTextOps ?? "");
                ApplyValidatorFiltersAndReanalysis(options, fieldMatches, orderedFields, fullNorm, streamNorm, opsNorm);
            }

            if (!options.Log)
                return fieldMatches;

            var printed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in orderedFields)
            {
                // Permite imprimir campos derivados (ex.: honorarios) mesmo sem grupo de padrões.
                if (!groupsByField.ContainsKey(field) && !fieldMatches.ContainsKey(field))
                    continue;
                fieldMatches.TryGetValue(field, out var matches);
                matches ??= new List<FieldMatch>();
                var hasSuggest = matches.Any(m => string.Equals(m.Kind, "honorarios", StringComparison.OrdinalIgnoreCase));
                var status = matches.Count > 0 ? "OK" : "MISS";
                var color = matches.Count > 0 ? (hasSuggest ? CCyan : CGreen) : CRed;
                var extra = hasSuggest ? " (SUGERIDO)" : "";
                Console.WriteLine($"{color}  FIELD {field} -> {matches.Count} match(es) [{status}]{extra}{CReset}");
                foreach (var match in matches.Take(options.Limit))
                {
                    var display = NormalizeValueByField(field, match.ValueText ?? "");
                    if (string.IsNullOrWhiteSpace(display))
                        display = match.ValueText ?? "";
                    display = CollapseUpperResiduals(display);
                    display = FixDanglingUpperInitial(display);
                    display = TextUtils.NormalizeWhitespace(display);
                    var roi = "";
                    if (!string.IsNullOrWhiteSpace(match.Band) || !string.IsNullOrWhiteSpace(match.YRange) || !string.IsNullOrWhiteSpace(match.XRange))
                        roi = $" roi={match.Band} y={match.YRange} x={match.XRange}".TrimEnd();
                    Console.WriteLine($"    score={match.Score:F2} op{match.StartOp}-op{match.EndOp}{roi} \"{display}\"");
                }
                printed.Add(field);
            }

            foreach (var group in groups)
            {
                if (printed.Contains(group.Key))
                    continue;
                if (!orderedFields.Any(f => f.Equals(group.Key, StringComparison.OrdinalIgnoreCase)))
                    continue;
                fieldMatches.TryGetValue(group.Key, out var matches);
                matches ??= new List<FieldMatch>();
                var hasSuggest = matches.Any(m => string.Equals(m.Kind, "honorarios", StringComparison.OrdinalIgnoreCase));
                var status = matches.Count > 0 ? "OK" : "MISS";
                var color = matches.Count > 0 ? (hasSuggest ? CCyan : CGreen) : CRed;
                var extra = hasSuggest ? " (SUGERIDO)" : "";
                Console.WriteLine($"{color}  FIELD {group.Key} -> {matches.Count} match(es) [{status}]{extra}{CReset}");
                foreach (var match in matches.Take(options.Limit))
                {
                    var display = NormalizeValueByField(group.Key, match.ValueText ?? "");
                    if (string.IsNullOrWhiteSpace(display))
                        display = match.ValueText ?? "";
                    display = CollapseUpperResiduals(display);
                    display = FixDanglingUpperInitial(display);
                    display = TextUtils.NormalizeWhitespace(display);
                    var roi = "";
                    if (!string.IsNullOrWhiteSpace(match.Band) || !string.IsNullOrWhiteSpace(match.YRange) || !string.IsNullOrWhiteSpace(match.XRange))
                        roi = $" roi={match.Band} y={match.YRange} x={match.XRange}".TrimEnd();
                    Console.WriteLine($"    score={match.Score:F2} op{match.StartOp}-op{match.EndOp}{roi} \"{display}\"");
                }
            }
            return fieldMatches;
        }

        private static List<TokenInfo> FilterTokensByOpRange(List<TokenInfo> tokens, int startOp, int endOp)
        {
            if (startOp <= 0 && endOp >= int.MaxValue)
                return tokens;
            var min = startOp > 0 ? startOp : int.MinValue;
            var max = endOp < int.MaxValue ? endOp : int.MaxValue;
            return tokens.Where(t => t.StartOp >= min && t.EndOp <= max).ToList();
        }

        private static List<FieldMatch> MergeMatches(List<FieldMatch> baseMatches, List<FieldMatch> extra)
        {
            if (extra == null || extra.Count == 0)
                return baseMatches;
            if (baseMatches == null || baseMatches.Count == 0)
                return extra;

            var dict = new Dictionary<string, FieldMatch>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in baseMatches.Concat(extra))
            {
                var key = $"{m.StartOp}-{m.EndOp}:{NormalizePatternText(m.ValueText ?? "")}";
                if (!dict.TryGetValue(key, out var existing) || m.Score > existing.Score)
                    dict[key] = m;
            }
            return dict.Values
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.StartOp)
                .ToList();
        }

        private static void NormalizeMatchValues(Dictionary<string, List<FieldMatch>> fieldMatches)
        {
            if (fieldMatches == null || fieldMatches.Count == 0)
                return;
            foreach (var kv in fieldMatches)
            {
                var field = kv.Key;
                var matches = kv.Value;
                if (matches == null || matches.Count == 0)
                    continue;
                var remove = new List<FieldMatch>();
                foreach (var m in matches)
                {
                    if (string.IsNullOrWhiteSpace(m.ValueText))
                        continue;
                    var norm = field.Equals("PERITO", StringComparison.OrdinalIgnoreCase)
                        ? CleanPeritoValue(m.ValueText)
                        : NormalizeValueByField(field, m.ValueText);
                    if (string.IsNullOrWhiteSpace(norm))
                        norm = NormalizeValueByField(field, m.ValueText);
                    if (string.IsNullOrWhiteSpace(norm))
                    {
                        if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                            field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                            remove.Add(m);
                        continue;
                    }
                    m.ValueText = norm;
                }
                if (remove.Count > 0)
                {
                    foreach (var m in remove)
                        matches.Remove(m);
                }
            }
        }

        private static List<FieldMatch> PreferRegexIfAvailable(string field, List<FieldMatch> matches)
        {
            if (matches == null || matches.Count == 0)
                return matches;
            if (!field.Equals("DATA_ARBITRADO_FINAL", StringComparison.OrdinalIgnoreCase))
                return matches;
            var rx = matches.Where(m => m.Kind == "regex" || m.Kind == "regex_roi").ToList();
            return rx.Count > 0 ? rx : matches;
        }

        private static List<string> PickTopAnchors(List<string> fields)
        {
            if (fields == null || fields.Count == 0)
                return new List<string>();
            if (fields.Contains("PROCESSO_ADMINISTRATIVO", StringComparer.OrdinalIgnoreCase))
                return new List<string> { "PROCESSO_ADMINISTRATIVO" };
            if (fields.Contains("PROCESSO_JUDICIAL", StringComparer.OrdinalIgnoreCase))
                return new List<string> { "PROCESSO_JUDICIAL" };
            return new List<string> { fields[0] };
        }

        private static void AppendOptionalFields(List<string> ordered, HashSet<string> optional)
        {
            if (ordered == null || optional == null || optional.Count == 0)
                return;
            foreach (var f in optional)
            {
                if (!ordered.Contains(f, StringComparer.OrdinalIgnoreCase))
                    ordered.Add(f);
            }
        }

        private static List<string> PickBottomAnchors(List<string> fields)
        {
            if (fields == null || fields.Count == 0)
                return new List<string>();
            var bottom = new List<string>();
            if (fields.Contains("VARA", StringComparer.OrdinalIgnoreCase)) bottom.Add("VARA");
            if (fields.Contains("COMARCA", StringComparer.OrdinalIgnoreCase)) bottom.Add("COMARCA");
            if (bottom.Count > 0) return bottom;
            if (fields.Contains("DATA_ARBITRADO_FINAL", StringComparer.OrdinalIgnoreCase))
                return new List<string> { "DATA_ARBITRADO_FINAL" };
            if (fields.Contains("VALOR_ARBITRADO_DE", StringComparer.OrdinalIgnoreCase))
                return new List<string> { "VALOR_ARBITRADO_DE" };
            return new List<string> { fields[^1] };
        }

        private static List<FieldMatch> FindMatchesOrdered(
            List<TokenInfo> tokens,
            List<string> tokenPatterns,
            List<TokenInfo> rawTokens,
            List<string> rawPatterns,
            FieldPatternEntry entry,
            double minScore,
            string field,
            int maxPairs,
            int maxCandidates,
            bool hasAdiantamento,
            bool log,
            bool useRaw,
            List<FieldReject>? rejects = null)
        {
            var fieldName = string.IsNullOrWhiteSpace(entry.Field) ? field : entry.Field ?? "";
            if (IsAdiantamentoDependentField(fieldName) && !hasAdiantamento)
                return new List<FieldMatch>();
            if (maxPairs > 0 && maxPairs != int.MaxValue && maxPairs > 20000)
                maxPairs = 20000;
            var rawMatches = new List<FieldMatch>();
            var typedMatches = new List<FieldMatch>();

            if (useRaw && HasRawPatterns(entry) && rawTokens.Count > 0 && ShouldUseRaw(rawTokens) && !IsRawPatternTooGeneric(entry))
                rawMatches = FindMatchesWithRoi(rawTokens, rawPatterns, entry, minScore, field, useRaw: true, textOnly: false, maxPairs: maxPairs, maxCandidates: maxCandidates, log: log, rejects: rejects);
            if (rawMatches.Count > 0)
                rawMatches = FilterAdiantamento(fieldName, rawMatches, rejects, hasAdiantamento);

            if (HasTypedPatterns(entry) && tokens.Count > 0)
            {
                typedMatches = FindMatchesWithRoi(tokens, tokenPatterns, entry, minScore, field, useRaw: false, textOnly: false, maxPairs: maxPairs, maxCandidates: maxCandidates, log: log, rejects: rejects);
            }
            if (typedMatches.Count > 0)
                typedMatches = FilterAdiantamento(fieldName, typedMatches, rejects, hasAdiantamento);

            // 1) DMP (RAW + TYPED) primeiro.
            var combined = new List<FieldMatch>();
            if (rawMatches.Count > 0 && typedMatches.Count > 0)
            {
                foreach (var t in typedMatches)
                {
                    FieldMatch? bestRaw = null;
                    foreach (var r in rawMatches)
                    {
                        if (!OpRangesOverlap(t.StartOp, t.EndOp, r.StartOp, r.EndOp))
                            continue;
                        if (bestRaw == null || r.Score > bestRaw.Score)
                            bestRaw = r;
                    }
                    if (bestRaw != null)
                        combined.Add(CombineRawTypedMatch(t, bestRaw));
                }
            }

            var dmpWinners = new List<FieldMatch>();
            if (combined.Count > 0)
                dmpWinners.AddRange(combined);
            else
                dmpWinners.AddRange(typedMatches.Concat(rawMatches));

            // 2) Regex participa quando DMP nao achou (ou quando campo depende de regex).
            var regexMatches = new List<FieldMatch>();
            bool hasRegex = HasRegexRules(fieldName);
            bool alwaysRegex =
                fieldName.Equals("DATA_ARBITRADO_FINAL", StringComparison.OrdinalIgnoreCase) ||
                fieldName.Equals("VALOR_ARBITRADO_DE", StringComparison.OrdinalIgnoreCase) ||
                fieldName.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                fieldName.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase) ||
                fieldName.Equals("PERITO", StringComparison.OrdinalIgnoreCase) ||
                fieldName.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase) ||
                fieldName.Equals("ESPECIALIDADE", StringComparison.OrdinalIgnoreCase);
            if (hasRegex && (dmpWinners.Count == 0 || alwaysRegex))
            {
                regexMatches = FindRegexMatchesWithRoi(tokens, entry, minScore, fieldName, maxCandidates, log, rejects);
                if (regexMatches.Count == 0)
                    regexMatches = FindRegexMatchesNoRoi(tokens, entry, minScore, fieldName, maxCandidates, log, rejects);
                if (regexMatches.Count > 0)
                    regexMatches = FilterAdiantamento(fieldName, regexMatches, rejects, hasAdiantamento);
            }

            if (dmpWinners.Count > 0 || regexMatches.Count > 0)
            {
                var merged = MergeDmpAndRegex(dmpWinners, regexMatches);
                var orderedAll = merged.OrderByDescending(m => m.Score).ThenBy(m => m.StartOp).ToList();
                orderedAll = FilterPeritoCpfEspec(fieldName, orderedAll);
                if (maxCandidates > 0 && orderedAll.Count > maxCandidates)
                    orderedAll = orderedAll.Take(maxCandidates).ToList();
                TrackWinner(orderedAll[0].Kind switch
                {
                    "both" => "both",
                    "typed" => "typed",
                    "raw" => "raw",
                    _ when orderedAll[0].Kind.StartsWith("regex", StringComparison.OrdinalIgnoreCase) => "regex",
                    _ => "typed"
                });
                if (log)
                    Console.Error.WriteLine($"[MATCH] field={entry.Field} rawCount={rawMatches.Count} typedCount={typedMatches.Count} bothCount={combined.Count} regexCount={regexMatches.Count} textCount=0");
                return orderedAll;
            }

            // 3) AnchorText por último.
            var textMatches = new List<FieldMatch>();
            if (HasAnchorText(entry) && tokens.Count > 0)
            {
                textMatches = FindMatchesWithRoi(tokens, tokenPatterns, entry, minScore, field, useRaw: false, textOnly: true, maxPairs: maxPairs, maxCandidates: maxCandidates, log: log, rejects: rejects);
                if (textMatches.Count > 0)
                    textMatches = FilterAdiantamento(fieldName, textMatches, rejects, hasAdiantamento);
            }

            if (log)
                Console.Error.WriteLine($"[MATCH] field={entry.Field} rawCount=0 typedCount=0 bothCount=0 regexCount=0 textCount={textMatches.Count}");

            if (textMatches.Count == 0)
                return new List<FieldMatch>();

            var orderedText = textMatches.OrderByDescending(m => m.Score).ThenBy(m => m.StartOp).ToList();
            TrackWinner("text");
            return orderedText;
        }

        private static List<FieldMatch> FindRegexMatchesWithRoi(
            List<TokenInfo> tokens,
            FieldPatternEntry entry,
            double minScore,
            string field,
            int maxCandidates,
            bool log,
            List<FieldReject>? rejects)
        {
            if (_regexCatalog == null || !_regexCatalog.TryGetValue(field, out var rules) || rules.Count == 0)
                return new List<FieldMatch>();
            if (tokens.Count == 0)
                return new List<FieldMatch>();

            var roiSpecified = !string.IsNullOrWhiteSpace(entry.Band) || !string.IsNullOrWhiteSpace(entry.YRange) || !string.IsNullOrWhiteSpace(entry.XRange);
            if (!roiSpecified)
                return new List<FieldMatch>();

            var (roiTokens, _, applied) = ApplyRoi(tokens, entry);
            if (!applied || roiTokens.Count == 0)
                return new List<FieldMatch>();

            var (textNorm, starts, ends) = BuildNormalizedTokenText(roiTokens);
            if (string.IsNullOrWhiteSpace(textNorm))
                return new List<FieldMatch>();

            var matches = new List<FieldMatch>();
            foreach (var rule in rules)
            {
                if (!BandMatches(rule.Band, entry.Band))
                    continue;
                var rx = rule.Compiled;
                if (rx == null)
                    continue;
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(textNorm))
                {
                    if (!m.Success)
                        continue;
                    var g = (rule.Group >= 0 && rule.Group < m.Groups.Count) ? m.Groups[rule.Group] : m.Groups[0];
                    if (g == null || !g.Success)
                        continue;
                    var value = g.Value.Trim();
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    var matchStart = g.Index;
                    var matchEnd = g.Index + g.Length;
                    var startIdx = -1;
                    var endIdx = -1;
                    for (int i = 0; i < roiTokens.Count; i++)
                    {
                        if (starts[i] < matchEnd && ends[i] > matchStart)
                        {
                            if (startIdx == -1)
                                startIdx = i;
                            endIdx = i;
                        }
                    }
                    if (startIdx == -1 || endIdx == -1)
                        continue;

                    if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                    {
                        var expanded = ExpandPartySpan(roiTokens, startIdx, endIdx);
                        startIdx = expanded.Start;
                        endIdx = expanded.End;
                    }

                    var startOp = roiTokens[startIdx].StartOp;
                    var endOp = roiTokens[endIdx].EndOp;

                    var valueRaw = BuildRawTokenText(roiTokens, startIdx, endIdx);
                    var valueRawPreserve = BuildValueTextForField(field, roiTokens, startIdx, endIdx);
                    if (string.IsNullOrWhiteSpace(valueRaw))
                        valueRaw = value;
                    if (string.IsNullOrWhiteSpace(valueRawPreserve))
                        valueRawPreserve = value;
                    var valuePattern = ObjectsTextOpsDiff.EncodePatternTyped(NormalizePatternText(valueRaw));
                    var (score, prevScore, nextScore, valueScore,
                        prevExpected, prevActual,
                        valueExpected, nextExpected, nextActual) =
                        ScoreRegexCandidateDmp(entry, roiTokens, startIdx, endIdx, valuePattern);
                    // Promovente/Promovido: regex é forte, não punir âncora ausente demais.
                    if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                    {
                        if (prevScore < minScore || nextScore < minScore)
                            score = Math.Max(score, 0.85);
                    }
                    // Regex so participa se as ancoras (prev/next) existirem quando definidas,
                    // exceto para campos onde o regex ja e suficientemente preciso.
                    bool allowRegexWithoutAnchors =
                        field.Equals("DATA_ARBITRADO_FINAL", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("VALOR_ARBITRADO_JZ", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("VALOR_ARBITRADO_DE", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("PERITO", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("ESPECIALIDADE", StringComparison.OrdinalIgnoreCase);
                    if (!allowRegexWithoutAnchors &&
                        !string.IsNullOrWhiteSpace(prevExpected) && !string.IsNullOrWhiteSpace(nextExpected) &&
                        (string.IsNullOrWhiteSpace(prevActual) || string.IsNullOrWhiteSpace(nextActual)))
                    {
                        if (rejects != null)
                        {
                            rejects.Add(new FieldReject
                            {
                                Kind = "regex",
                                Reason = "anchors_missing",
                                Score = score,
                                PrevScore = prevScore,
                                NextScore = nextScore,
                                ValueScore = valueScore,
                                PrevMode = "",
                                NextMode = "",
                                PrevText = "",
                                NextText = "",
                                PrevExpectedPattern = prevExpected,
                                PrevActualPattern = prevActual,
                                PrevPatternScore = prevScore,
                                PrevExpectedText = "",
                                PrevActualText = "",
                                PrevTextScore = 0,
                                NextExpectedPattern = nextExpected,
                                NextActualPattern = nextActual,
                                NextPatternScore = nextScore,
                                NextExpectedText = "",
                                NextActualText = "",
                                NextTextScore = 0,
                                ValueText = valueRawPreserve,
                                ValuePattern = valuePattern,
                                ValueExpectedPattern = valueExpected,
                                StartOp = startOp,
                                EndOp = endOp,
                                Band = entry.Band ?? "",
                                YRange = entry.YRange ?? ""
                            });
                        }
                        continue;
                    }
                    var adjustedScore = score;
                    if (score < minScore)
                    {
                        if (rejects != null)
                        {
                            rejects.Add(new FieldReject
                            {
                                Kind = "regex",
                                Reason = "score_below_min",
                                Score = score,
                                PrevScore = prevScore,
                                NextScore = nextScore,
                                ValueScore = valueScore,
                                PrevMode = "",
                                NextMode = "",
                                PrevText = "",
                                NextText = "",
                                PrevExpectedPattern = prevExpected,
                                PrevActualPattern = prevActual,
                                PrevPatternScore = prevScore,
                                PrevExpectedText = "",
                                PrevActualText = "",
                                PrevTextScore = 0,
                                NextExpectedPattern = nextExpected,
                                NextActualPattern = nextActual,
                                NextPatternScore = nextScore,
                                NextExpectedText = "",
                                NextActualText = "",
                                NextTextScore = 0,
                                ValueText = valueRawPreserve,
                                ValuePattern = valuePattern,
                                ValueExpectedPattern = valueExpected,
                                StartOp = startOp,
                                EndOp = endOp,
                                Band = entry.Band ?? "",
                                YRange = entry.YRange ?? ""
                            });
                        }
                        adjustedScore = minScore; // promove regex para não perder recall
                    }

                    value = NormalizeValueByField(field, valueRawPreserve);
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    matches.Add(new FieldMatch
                    {
                        Field = field,
                        Pdf = "",
                        ValueText = value,
                        ValuePattern = valuePattern,
                        ValueExpectedPattern = valueExpected,
                        Score = adjustedScore,
                        PrevScore = prevScore,
                        NextScore = nextScore,
                        ValueScore = valueScore,
                        PrevText = "",
                        NextText = "",
                        PrevExpectedPattern = prevExpected,
                        PrevActualPattern = prevActual,
                        PrevPatternScore = prevScore,
                        PrevExpectedText = "",
                        PrevActualText = "",
                        PrevTextScore = 0,
                        NextExpectedPattern = nextExpected,
                        NextActualPattern = nextActual,
                        NextPatternScore = nextScore,
                        NextExpectedText = "",
                        NextActualText = "",
                        NextTextScore = 0,
                        StartOp = startOp,
                        EndOp = endOp,
                        Band = entry.Band ?? "",
                        YRange = entry.YRange ?? "",
                        Kind = (score < minScore ? "regex+clamp:" : "regex+dmp:") + rule.Source,
                        PrevMode = "",
                        NextMode = ""
                    });
                    if (maxCandidates > 0 && matches.Count >= maxCandidates)
                        break;
                }
                if (maxCandidates > 0 && matches.Count >= maxCandidates)
                    break;
            }

            if (log && matches.Count > 0)
                Console.Error.WriteLine($"[REGEX] field={field} hits={matches.Count} roiTokens={roiTokens.Count}");

            return matches.OrderByDescending(m => m.Score).ThenBy(m => m.StartOp).ToList();
        }

        private static List<FieldMatch> FindRegexMatchesNoRoi(
            List<TokenInfo> tokens,
            FieldPatternEntry entry,
            double minScore,
            string field,
            int maxCandidates,
            bool log,
            List<FieldReject>? rejects)
        {
            if (_regexCatalog == null || !_regexCatalog.TryGetValue(field, out var rules) || rules.Count == 0)
                return new List<FieldMatch>();
            if (tokens.Count == 0)
                return new List<FieldMatch>();

            var (textNorm, starts, ends) = BuildNormalizedTokenText(tokens);
            if (string.IsNullOrWhiteSpace(textNorm))
                return new List<FieldMatch>();

            var matches = new List<FieldMatch>();
            foreach (var rule in rules)
            {
                var rx = rule.Compiled;
                if (rx == null)
                    continue;
                foreach (System.Text.RegularExpressions.Match m in rx.Matches(textNorm))
                {
                    if (!m.Success)
                        continue;
                    var g = (rule.Group >= 0 && rule.Group < m.Groups.Count) ? m.Groups[rule.Group] : m.Groups[0];
                    if (g == null || !g.Success)
                        continue;
                    var value = g.Value.Trim();
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    var matchStart = g.Index;
                    var matchEnd = g.Index + g.Length;
                    var startIdx = -1;
                    var endIdx = -1;
                    for (int i = 0; i < tokens.Count; i++)
                    {
                        if (starts[i] < matchEnd && ends[i] > matchStart)
                        {
                            if (startIdx == -1)
                                startIdx = i;
                            endIdx = i;
                        }
                    }
                    if (startIdx == -1 || endIdx == -1)
                        continue;

                    if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                    {
                        var expanded = ExpandPartySpan(tokens, startIdx, endIdx);
                        startIdx = expanded.Start;
                        endIdx = expanded.End;
                    }

                    var startOp = tokens[startIdx].StartOp;
                    var endOp = tokens[endIdx].EndOp;

                    var valueRaw = BuildRawTokenText(tokens, startIdx, endIdx);
                    var valueRawPreserve = BuildValueTextForField(field, tokens, startIdx, endIdx);
                    if (string.IsNullOrWhiteSpace(valueRaw))
                        valueRaw = value;
                    if (string.IsNullOrWhiteSpace(valueRawPreserve))
                        valueRawPreserve = value;
                    var valuePattern = ObjectsTextOpsDiff.EncodePatternTyped(NormalizePatternText(valueRaw));
                    var (score, prevScore, nextScore, valueScore,
                        prevExpected, prevActual,
                        valueExpected, nextExpected, nextActual) =
                        ScoreRegexCandidateDmp(entry, tokens, startIdx, endIdx, valuePattern);
                    // Promovente/Promovido: regex é forte, não punir âncora ausente demais.
                    if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                    {
                        if (prevScore < minScore || nextScore < minScore)
                            score = Math.Max(score, 0.85);
                    }
                    // Regex so participa se as ancoras (prev/next) existirem quando definidas.
                    // Para PERITO/CPF_PERITO/ESPECIALIDADE, nao bloquear por ancora ausente (sao campos mais flexiveis).
                    var allowMissingAnchors =
                        field.Equals("DATA_ARBITRADO_FINAL", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("VALOR_ARBITRADO_JZ", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("VALOR_ARBITRADO_DE", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("PERITO", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("ESPECIALIDADE", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase);
                    if (!allowMissingAnchors &&
                        !string.IsNullOrWhiteSpace(prevExpected) && !string.IsNullOrWhiteSpace(nextExpected) &&
                        (string.IsNullOrWhiteSpace(prevActual) || string.IsNullOrWhiteSpace(nextActual)))
                    {
                        if (rejects != null)
                        {
                            rejects.Add(new FieldReject
                            {
                                Kind = "regex",
                                Reason = "anchors_missing",
                                Score = score,
                                PrevScore = prevScore,
                                NextScore = nextScore,
                                ValueScore = valueScore,
                                PrevMode = "",
                                NextMode = "",
                                PrevText = "",
                                NextText = "",
                                PrevExpectedPattern = prevExpected,
                                PrevActualPattern = prevActual,
                                PrevPatternScore = prevScore,
                                PrevExpectedText = "",
                                PrevActualText = "",
                                PrevTextScore = 0,
                                NextExpectedPattern = nextExpected,
                                NextActualPattern = nextActual,
                                NextPatternScore = nextScore,
                                NextExpectedText = "",
                                NextActualText = "",
                                NextTextScore = 0,
                                ValueText = NormalizeValueByField(field, valueRawPreserve),
                                ValuePattern = valuePattern,
                                ValueExpectedPattern = valueExpected,
                                StartOp = startOp,
                                EndOp = endOp,
                                Band = entry.Band ?? "",
                                YRange = entry.YRange ?? ""
                            });
                        }
                        continue;
                    }
                    var adjustedScore = score;
                    if (score < minScore)
                    {
                        if (rejects != null)
                        {
                            rejects.Add(new FieldReject
                            {
                                Kind = "regex",
                                Reason = "score_below_min",
                                Score = score,
                                PrevScore = prevScore,
                                NextScore = nextScore,
                                ValueScore = valueScore,
                                PrevMode = "",
                                NextMode = "",
                                PrevText = "",
                                NextText = "",
                                PrevExpectedPattern = prevExpected,
                                PrevActualPattern = prevActual,
                                PrevPatternScore = prevScore,
                                PrevExpectedText = "",
                                PrevActualText = "",
                                PrevTextScore = 0,
                                NextExpectedPattern = nextExpected,
                                NextActualPattern = nextActual,
                                NextPatternScore = nextScore,
                                NextExpectedText = "",
                                NextActualText = "",
                                NextTextScore = 0,
                                ValueText = NormalizeValueByField(field, valueRawPreserve),
                                ValuePattern = valuePattern,
                                ValueExpectedPattern = valueExpected,
                                StartOp = startOp,
                                EndOp = endOp,
                                Band = entry.Band ?? "",
                                YRange = entry.YRange ?? ""
                            });
                        }
                        adjustedScore = minScore; // promove regex para não perder recall
                    }

                    var normalized = NormalizeValueByField(field, valueRawPreserve);
                    if (string.IsNullOrWhiteSpace(normalized))
                        continue;
                    matches.Add(new FieldMatch
                    {
                        Field = field,
                        Pdf = "",
                        ValueText = normalized,
                        ValuePattern = valuePattern,
                        ValueExpectedPattern = valueExpected,
                        Score = adjustedScore,
                        PrevScore = prevScore,
                        NextScore = nextScore,
                        ValueScore = valueScore,
                        PrevText = "",
                        NextText = "",
                        PrevExpectedPattern = prevExpected,
                        PrevActualPattern = prevActual,
                        PrevPatternScore = prevScore,
                        PrevExpectedText = "",
                        PrevActualText = "",
                        PrevTextScore = 0,
                        NextExpectedPattern = nextExpected,
                        NextActualPattern = nextActual,
                        NextPatternScore = nextScore,
                        NextExpectedText = "",
                        NextActualText = "",
                        NextTextScore = 0,
                        StartOp = startOp,
                        EndOp = endOp,
                        Band = entry.Band ?? "",
                        YRange = entry.YRange ?? "",
                        Kind = (score < minScore ? "regex+clamp:" : "regex+dmp:") + rule.Source,
                        PrevMode = "",
                        NextMode = ""
                    });
                    if (maxCandidates > 0 && matches.Count >= maxCandidates)
                        break;
                }
            }

            if (log && matches.Count > 0)
                Console.Error.WriteLine($"[REGEX] field={field} hits={matches.Count} (no ROI)");
            return matches.OrderByDescending(m => m.Score).ThenBy(m => m.StartOp).ToList();
        }
        private static List<FieldMatch> FindPeritoFallbackFromTokens(
            List<TokenInfo> tokens,
            FieldPatternEntry entry,
            double minScore)
        {
            // Fallbacks específicos foram removidos para centralizar padrões no YAML/JSON.
            // A extração de PERITO deve ocorrer via regex catalog + DMP usando registry/template_fields/*.yml
            return new List<FieldMatch>();
        }


        private static string ExtractPeritoFromInterested(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var idx = text.IndexOf("Interessad", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return "";
            var colon = text.IndexOf(':', idx);
            if (colon < 0)
                return "";
            var rest = text[(colon + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(rest))
                return "";

            var cut = rest.Length;
            var dashIdx = rest.IndexOfAny(new[] { '–', '—', '-' });
            if (dashIdx >= 0)
                cut = Math.Min(cut, dashIdx);
            var perIdx = rest.IndexOf("perit", StringComparison.OrdinalIgnoreCase);
            if (perIdx >= 0)
                cut = Math.Min(cut, perIdx);
            if (cut < rest.Length)
                rest = rest[..cut];

            rest = TextNormalization.NormalizePatternText(rest);
            var value = NormalizeValueByField("PERITO", rest);
            return value;
        }

        private static (string Text, List<int> Starts, List<int> Ends) BuildNormalizedTokenText(List<TokenInfo> tokens)
        {
            var starts = new List<int>();
            var ends = new List<int>();
            var sb = new StringBuilder();
            if (tokens.Count == 0)
                return (string.Empty, starts, ends);

            var normTokens = tokens
                .Select(t => NormalizeFullText(t.Text))
                .ToList();

            static bool IsWordToken(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                foreach (var ch in token)
                {
                    if (!char.IsLetter(ch))
                        return false;
                }
                return token.Length > 0;
            }

            static bool IsUpperWord(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                bool hasLetter = false;
                foreach (var ch in token)
                {
                    if (!char.IsLetter(ch))
                        return false;
                    hasLetter = true;
                    if (!char.IsUpper(ch))
                        return false;
                }
                return hasLetter;
            }

            static bool IsConnector(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return false;
                var t = token.ToUpperInvariant();
                return t is "DE" or "DA" or "DO" or "DOS" or "DAS" or "E" or "EM" or "NO" or "NA" or "NOS" or "NAS" or "AO" or "AOS" or "POR" or "PELO" or "PELA";
            }

            var shortRun = new bool[normTokens.Count];
            int iRun = 0;
            while (iRun < normTokens.Count)
            {
                if (!IsWordToken(normTokens[iRun]) || normTokens[iRun].Length > 2)
                {
                    iRun++;
                    continue;
                }
                int j = iRun;
                while (j < normTokens.Count && IsWordToken(normTokens[j]) && normTokens[j].Length <= 2)
                    j++;
                if (j - iRun >= 3)
                {
                    for (int k = iRun; k < j; k++)
                        shortRun[k] = true;
                }
                iRun = j;
            }

            bool ShouldJoin(int index)
            {
                if (index <= 0 || index >= normTokens.Count)
                    return false;
                var prev = normTokens[index - 1];
                var cur = normTokens[index];
                if (string.IsNullOrWhiteSpace(prev) || string.IsNullOrWhiteSpace(cur))
                    return false;

                if (shortRun[index - 1] && shortRun[index])
                    return true;

                if (IsWordToken(prev) && IsWordToken(cur))
                {
                    if (!IsConnector(prev) && !IsConnector(cur) &&
                        (prev.Length <= 2 || cur.Length <= 2))
                        return true;
                }

                if (IsUpperWord(prev) && IsUpperWord(cur) &&
                    prev.Length >= 8 && cur.Length <= 4)
                    return true;

                return false;
            }

            for (int i = 0; i < normTokens.Count; i++)
            {
                var norm = normTokens[i];
                if (string.IsNullOrEmpty(norm))
                {
                    starts.Add(sb.Length);
                    ends.Add(sb.Length);
                    continue;
                }
                if (sb.Length > 0 && !ShouldJoin(i))
                    sb.Append(' ');
                var start = sb.Length;
                sb.Append(norm);
                var end = sb.Length;
                starts.Add(start);
                ends.Add(end);
            }
            return (sb.ToString(), starts, ends);
        }

        private static string BuildRawTokenText(List<TokenInfo> tokens, int startIdx, int endIdx)
        {
            if (tokens.Count == 0 || startIdx < 0 || endIdx < startIdx || startIdx >= tokens.Count)
                return "";
            endIdx = Math.Min(endIdx, tokens.Count - 1);
            var sb = new StringBuilder();
            for (int i = startIdx; i <= endIdx; i++)
            {
                var t = tokens[i].Raw;
                if (string.IsNullOrEmpty(t))
                    t = tokens[i].Text ?? "";
                if (t.Length == 0)
                    continue;
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(t);
            }
            var raw = sb.ToString();
            return TextNormalization.NormalizePatternText(raw);
        }

        private static string BuildRawTokenTextPreserve(List<TokenInfo> tokens, int startIdx, int endIdx)
        {
            if (tokens.Count == 0 || startIdx < 0 || endIdx < startIdx || startIdx >= tokens.Count)
                return "";
            endIdx = Math.Min(endIdx, tokens.Count - 1);
            var sb = new StringBuilder();
            for (int i = startIdx; i <= endIdx; i++)
            {
                var t = tokens[i].Text ?? "";
                if (t.Length == 0)
                    continue;
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(t);
            }
            var raw = sb.ToString();
            return TextNormalization.NormalizeFullText(raw);
        }

        private static string BuildValueTextForField(string field, List<TokenInfo> tokens, int startIdx, int endIdx)
        {
            var preserved = BuildRawTokenTextPreserve(tokens, startIdx, endIdx);
            if (!(field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                  field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase)))
                return preserved;

            var markers = field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase)
                ? PromoventeStartMarkers.Concat(PromoventeCutMarkers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : PromovidoStartMarkers;
            var rawLine = FindRawLineByMarkers(tokens, startIdx, endIdx, markers);
            if (string.IsNullOrWhiteSpace(rawLine))
                rawLine = ExtractCommonBlockRawText(tokens, startIdx, endIdx);
            if (!string.IsNullOrWhiteSpace(rawLine))
            {
                if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase))
                {
                    var extracted = TryExtractPromoventeFromLine(rawLine);
                    if (!string.IsNullOrWhiteSpace(extracted))
                        return extracted;
                }
                if (field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                {
                    var extracted = TryExtractPromovidoFromLine(rawLine);
                    if (!string.IsNullOrWhiteSpace(extracted))
                        return extracted;
                }
            }

            return preserved;
        }

        private static string ExtractCommonBlockRawText(List<TokenInfo> tokens, int startIdx, int endIdx)
        {
            if (tokens.Count == 0 || startIdx < 0 || endIdx < startIdx || startIdx >= tokens.Count)
                return "";
            endIdx = Math.Min(endIdx, tokens.Count - 1);
            var direct = tokens[startIdx].BlockRawText;
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            var slice = tokens
                .Skip(startIdx)
                .Take(endIdx - startIdx + 1)
                .Where(t => !string.IsNullOrWhiteSpace(t.BlockRawText))
                .ToList();
            if (slice.Count == 0)
                return "";

            var best = slice
                .GroupBy(t => t.BlockIndex)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            if (best == null)
                return "";
            var raw = best.First().BlockRawText;
            return raw ?? "";
        }

        private static string FindRawLineByMarkers(List<TokenInfo> tokens, int startIdx, int endIdx, string[] markers)
        {
            if (tokens.Count == 0 || startIdx < 0 || endIdx < startIdx || startIdx >= tokens.Count)
                return "";
            endIdx = Math.Min(endIdx, tokens.Count - 1);
            var seen = new HashSet<int>();
            for (int i = startIdx; i <= endIdx; i++)
            {
                var t = tokens[i];
                if (!seen.Add(t.BlockIndex))
                    continue;
                var raw = t.BlockRawText;
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var lower = raw.ToLowerInvariant();
                foreach (var m in markers)
                {
                    if (lower.Contains(m))
                        return raw;
                }
            }
            return "";
        }

        private static string TryExtractPromoventeFromEmFaceContext(string text)
        {
            return TryExtractPartiesFromEmFaceContext(text, out var promovente, out _)
                ? promovente
                : "";
        }

        private static string TryExtractPromovidoFromEmFaceContext(string text)
        {
            return TryExtractPartiesFromEmFaceContext(text, out _, out var promovido)
                ? promovido
                : "";
        }

        private static bool TryExtractPartiesFromEmFaceContext(string text, out string promovente, out string promovido)
        {
            promovente = "";
            promovido = "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var norm = TextNormalization.NormalizeWhitespace(text);

            // Prefer the common despacho form: "... movido por <PROMOVENTE>, CPF ..., em face do/da <PROMOVIDO> ..."
            var match = Regex.Match(
                norm,
                @"(?is)\bmovid[oa]\s+por\s+(?<promovente>[\p{L}\d\s\.'\-]{3,180}?)\s*,?\s*(?:CPF|CNPJ)\s*[\d\.\-/\s]{11,24}\s*,?\s*em\s*face\s*d(?:e|o|a)\s+(?<promovido>[\p{L}\d\s\.'\-/&]{3,220}?)(?=\s*,?\s*(?:CPF|CNPJ)|\s*perante|\s*ju[ií]zo|,|$)");

            if (!match.Success)
            {
                // Fallback: same structure but without "movido por" marker in the text span we matched.
                match = Regex.Match(
                    norm,
                    @"(?is)\b(?!movid[oa]\s+por\b)(?<promovente>[\p{L}\d\s\.'\-]{3,180}?)\s*,?\s*(?:CPF|CNPJ)\s*[\d\.\-/\s]{11,24}\s*,?\s*em\s*face\s*d(?:e|o|a)\s+(?<promovido>[\p{L}\d\s\.'\-/&]{3,220}?)(?=\s*,?\s*(?:CPF|CNPJ)|\s*perante|\s*ju[ií]zo|,|$)");
            }

            if (!match.Success)
                return false;

            var rawPromovente = match.Groups["promovente"].Value;
            var rawPromovido = match.Groups["promovido"].Value;

            rawPromovente = Regex.Replace(rawPromovente, @"(?is)^\s*movid[oa]\s+por\s+", "");
            rawPromovido = Regex.Replace(rawPromovido, @"(?is)^\s*em\s*face\s*d(?:e|o|a)\s+", "");

            var cleanPromovente = ValidatorRules.CleanPartyName(rawPromovente).Trim().TrimEnd(',', ';', '–', '—', '-');
            var cleanPromovido = ValidatorRules.CleanPartyName(rawPromovido).Trim().TrimEnd(',', ';', '–', '—', '-');

            if (string.IsNullOrWhiteSpace(cleanPromovente) || string.IsNullOrWhiteSpace(cleanPromovido))
                return false;

            promovente = cleanPromovente;
            promovido = cleanPromovido;
            return true;
        }

        private static string TryExtractPromoventeFromLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return "";

            var candidate = line.Trim();
            var promoted = Regex.Match(candidate, @"(?is)\bpromovente\s*:\s*([^\n,;]{3,160})");
            if (promoted.Success)
                return promoted.Groups[1].Value.Trim().TrimEnd(',', ';', '–', '—', '-');
            var autor = Regex.Match(candidate, @"(?is)\bautor(?:\s*\(es\))?\s*:\s*([^\n,;]{3,160})");
            if (autor.Success)
                return autor.Groups[1].Value.Trim().TrimEnd(',', ';', '–', '—', '-');
            var emFaceWithDoc = Regex.Match(
                candidate,
                @"(?is)\b([A-ZÁÉÍÓÚÂÊÔÃÕÇ0-9][\p{L}\d\s\.'/\-&]{3,180}?)\s*,?\s*(?:CPF|CNPJ)\s*[\d\.\-/\s]{11,24}\s*,?\s*em\s*face\s*d(?:e|o|a)\b");
            if (emFaceWithDoc.Success)
            {
                var clean = ValidatorRules.CleanPartyName(emFaceWithDoc.Groups[1].Value);
                return clean.Trim().TrimEnd(',', ';', '–', '—', '-');
            }

            var movedBy = Regex.Match(candidate, @"(?is)\bmovid[oa]\s+por\s+([\p{L}\s\.'\-]{3,140}?)(?=,\s*(?:CPF|CNPJ)|\s*em\s*face\s*d(?:e|o|a)|\s*perante|\.)");
            if (movedBy.Success)
                return movedBy.Groups[1].Value.Trim().TrimEnd(',', ';', '–', '—', '-');

            var lower = candidate.ToLowerInvariant();
            var idxMovido = FindFirstIndex(lower, PromoventeStartMarkers, out var movMarker);
            if (idxMovido < 0 || string.IsNullOrWhiteSpace(movMarker))
                return "";

            candidate = candidate[(idxMovido + movMarker.Length)..].Trim();
            lower = candidate.ToLowerInvariant();
            var cutIdx = FindFirstIndex(lower, PromoventeCutMarkers, out _);
            if (cutIdx > 0)
                candidate = candidate.Substring(0, cutIdx).Trim();
            candidate = Regex.Replace(candidate, @"(?is)\b(?:cpf|cnpj)\b.*$", "").Trim();
            candidate = candidate.Trim().TrimEnd(',', ';', '–', '—', '-');
            return candidate;
        }

        private static string TryExtractPromovidoFromLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return "";
            var promoted = Regex.Match(line, @"(?is)\bpromovid[oa]\s*:\s*([^\n,;]{3,160})");
            if (promoted.Success)
                return promoted.Groups[1].Value.Trim().TrimEnd(',', ';', '–', '—', '-');
            var reu = Regex.Match(line, @"(?is)\br[eé]u(?:\s*\(s\))?\s*:\s*([^\n,;]{3,160})");
            if (reu.Success)
                return reu.Groups[1].Value.Trim().TrimEnd(',', ';', '–', '—', '-');
            var emFace = Regex.Match(
                line,
                @"(?is)\bem\s*face\s*d(?:e|o|a)\s+([\p{L}\d\s\.'/\-&]{3,180}?)(?=\s*,?\s*(?:CPF|CNPJ)|\s*perante|\s*ju[ií]zo|\.|,|$)");
            if (emFace.Success)
            {
                var clean = ValidatorRules.CleanPartyName(emFace.Groups[1].Value);
                return clean.Trim().TrimEnd(',', ';', '–', '—', '-');
            }

            var lower = line.ToLowerInvariant();
            var idx = FindFirstIndex(lower, PromovidoStartMarkers, out var marker);
            if (idx < 0 || string.IsNullOrEmpty(marker))
                return "";
            var candidate = line.Substring(idx + marker.Length).Trim();
            candidate = Regex.Replace(candidate, @"(?is)\b(?:cpf|cnpj)\b.*$", "").Trim();
            candidate = candidate.Trim().TrimEnd(',', ';', '–', '—', '-');
            return candidate;
        }

        private static int FindFirstIndex(string text, string[] markers, out string? marker)
        {
            marker = null;
            var best = -1;
            foreach (var m in markers)
            {
                var idx = text.IndexOf(m, StringComparison.Ordinal);
                if (idx < 0)
                    continue;
                if (best < 0 || idx < best)
                {
                    best = idx;
                    marker = m;
                }
            }
            return best;
        }

        private static string TryExtractFullText(string pdfPath, int objId, bool log = false)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || objId <= 0)
                return "";
            if (!File.Exists(pdfPath))
                return "";
            try
            {
                using var reader = new PdfReader(pdfPath);
                using var doc = new PdfDocument(reader);
                var found = FindStreamAndResourcesByObjId(doc, objId);
                if (found.Stream == null || found.Resources == null)
                {
                    if (log)
                        Console.Error.WriteLine($"[FULLTEXT] stream/resource nao encontrado obj={objId}");
                    return "";
                }
                if (PdfTextExtraction.TryExtractStreamText(found.Stream, found.Resources, out var fullText, out _))
                {
                    var raw = fullText ?? "";
                    return TextNormalization.FixLineBreakWordSplits(raw);
                }
                if (log)
                    Console.Error.WriteLine($"[FULLTEXT] falha ao extrair texto obj={objId}");
            }
            catch
            {
                if (log)
                    Console.Error.WriteLine($"[FULLTEXT] excecao ao extrair texto obj={objId}");
                return "";
            }
            return "";
        }

        private static void ApplyFullTextOverrides(Dictionary<string, List<FieldMatch>> fieldMatches, string fullText, List<ObjectsTextOpsDiff.PatternBlock> blocks, string fullTextStream, string fullTextOps, bool log = false)
        {
            if (fieldMatches == null || string.IsNullOrWhiteSpace(fullText))
                return;
            var fields = new[] { "PROMOVENTE", "PROMOVIDO" };
            var fullNorm = TextNormalization.NormalizePatternText(fullText);
            var streamNorm = TextNormalization.NormalizeWhitespace(TextNormalization.FixMissingSpaces(fullTextStream ?? ""));
            var opsNorm = TextNormalization.NormalizePatternText(fullTextOps ?? "");
            var catalog = ValidatorContext.GetPeritoCatalog();
            foreach (var field in fields)
            {
                // Do not override a field that already has at least one validator-approved candidate.
                if (fieldMatches.TryGetValue(field, out var existing) &&
                    existing != null &&
                    existing.Count > 0 &&
                    existing.Any(m => IsValueValidForField(field, m.ValueText ?? "", catalog, out _)))
                {
                    continue;
                }

                var candidates = new List<string>();
                if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefer the anchored "movido por ... CPF ... em face ..." context when available.
                    candidates.Add(TryExtractPromoventeFromEmFaceContext(fullNorm));
                    if (!string.IsNullOrWhiteSpace(streamNorm))
                        candidates.Add(TryExtractPromoventeFromEmFaceContext(streamNorm));
                    if (!string.IsNullOrWhiteSpace(opsNorm))
                        candidates.Add(TryExtractPromoventeFromEmFaceContext(opsNorm));

                    candidates.Add(TryExtractPromoventeFromLine(fullNorm));
                    if (!string.IsNullOrWhiteSpace(streamNorm))
                        candidates.Add(TryExtractPromoventeFromLine(streamNorm));
                    if (!string.IsNullOrWhiteSpace(opsNorm))
                        candidates.Add(TryExtractPromoventeFromLine(opsNorm));
                }
                else if (field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                {
                    // Prefer the anchored "movido por ... CPF ... em face ..." context when available.
                    candidates.Add(TryExtractPromovidoFromEmFaceContext(fullNorm));
                    if (!string.IsNullOrWhiteSpace(streamNorm))
                        candidates.Add(TryExtractPromovidoFromEmFaceContext(streamNorm));
                    if (!string.IsNullOrWhiteSpace(opsNorm))
                        candidates.Add(TryExtractPromovidoFromEmFaceContext(opsNorm));

                    candidates.Add(TryExtractPromovidoFromLine(fullNorm));
                    if (!string.IsNullOrWhiteSpace(streamNorm))
                        candidates.Add(TryExtractPromovidoFromLine(streamNorm));
                    if (!string.IsNullOrWhiteSpace(opsNorm))
                        candidates.Add(TryExtractPromovidoFromLine(opsNorm));
                }

                // Catalog/blocks as last resort.
                if (!string.IsNullOrWhiteSpace(fullNorm))
                    candidates.Add(ExtractRegexValueFromCatalogLoose(field, fullNorm) ?? "");
                if (!string.IsNullOrWhiteSpace(streamNorm))
                    candidates.Add(ExtractRegexValueFromCatalogLoose(field, streamNorm) ?? "");
                if (!string.IsNullOrWhiteSpace(opsNorm))
                    candidates.Add(ExtractRegexValueFromCatalogLoose(field, opsNorm) ?? "");
                if (blocks != null && blocks.Count > 0)
                    candidates.Add(ExtractRegexValueFromBlocks(field, blocks));

                string extracted = "";
                foreach (var cand in candidates)
                {
                    if (string.IsNullOrWhiteSpace(cand))
                        continue;
                    var norm = NormalizeNameExtract(cand);
                    if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase))
                        norm = Regex.Replace(norm, @"(?is)^\s*movid[oa]\s+por\s+", "");
                    else
                        norm = NormalizeValueByField(field, norm);
                    if (string.IsNullOrWhiteSpace(norm))
                        continue;
                    if (IsValueValidForField(field, norm, catalog, out _))
                    {
                        extracted = norm;
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(extracted))
                {
                    extracted = PickBestNameCandidate(candidates);
                    extracted = NormalizeNameExtract(extracted);
                    if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase))
                        extracted = Regex.Replace(extracted, @"(?is)^\s*movid[oa]\s+por\s+", "");
                    else
                        extracted = NormalizeValueByField(field, extracted);
                }

                if (string.IsNullOrWhiteSpace(extracted))
                    continue;

                if (log)
                    Console.Error.WriteLine($"[FULLTEXT] field={field} extracted=\"{TrimSnippet(extracted, 140)}\"");
                if (!fieldMatches.TryGetValue(field, out var matches) || matches == null || matches.Count == 0)
                {
                    fieldMatches[field] = new List<FieldMatch>
                    {
                        new FieldMatch
                        {
                            Field = field,
                            ValueText = extracted,
                            Score = 0.9,
                            Kind = "fulltext"
                        }
                    };
                    continue;
                }
                foreach (var m in matches)
                    m.ValueText = extracted;
            }

            // Prefer normalized full text for value fallbacks (handles espaçamento e tokens quebrados)
            ApplyFullTextValueFallback(fieldMatches, "PROCESSO_JUDICIAL", fullNorm, log, TryExtractProcessoJudicialFromText);
            if (!string.IsNullOrWhiteSpace(streamNorm))
                ApplyFullTextValueFallback(fieldMatches, "PROCESSO_JUDICIAL", streamNorm, log, TryExtractProcessoJudicialFromText);
            if (!string.IsNullOrWhiteSpace(opsNorm))
                ApplyFullTextValueFallback(fieldMatches, "PROCESSO_JUDICIAL", opsNorm, log, TryExtractProcessoJudicialFromText);

            ApplyFullTextValueFallback(fieldMatches, "PROCESSO_ADMINISTRATIVO", fullNorm, log, TryExtractProcessoAdministrativoFromText);
            if (!string.IsNullOrWhiteSpace(streamNorm))
                ApplyFullTextValueFallback(fieldMatches, "PROCESSO_ADMINISTRATIVO", streamNorm, log, TryExtractProcessoAdministrativoFromText);
            if (!string.IsNullOrWhiteSpace(opsNorm))
                ApplyFullTextValueFallback(fieldMatches, "PROCESSO_ADMINISTRATIVO", opsNorm, log, TryExtractProcessoAdministrativoFromText);

            ApplyFullTextValueFallback(fieldMatches, "VALOR_ARBITRADO_JZ", fullNorm, log, TryExtractValorArbitradoFromText);
            if (!string.IsNullOrWhiteSpace(streamNorm))
                ApplyFullTextValueFallback(fieldMatches, "VALOR_ARBITRADO_JZ", streamNorm, log, TryExtractValorArbitradoFromText);
            if (!string.IsNullOrWhiteSpace(opsNorm))
                ApplyFullTextValueFallback(fieldMatches, "VALOR_ARBITRADO_JZ", opsNorm, log, TryExtractValorArbitradoFromText);

            // Reuse the same value extractor for DE/CM variants so page2-only values can still feed final consolidation.
            ApplyFullTextValueFallback(fieldMatches, "VALOR_ARBITRADO_DE", fullNorm, log, TryExtractValorArbitradoFromText);
            if (!string.IsNullOrWhiteSpace(streamNorm))
                ApplyFullTextValueFallback(fieldMatches, "VALOR_ARBITRADO_DE", streamNorm, log, TryExtractValorArbitradoFromText);
            if (!string.IsNullOrWhiteSpace(opsNorm))
                ApplyFullTextValueFallback(fieldMatches, "VALOR_ARBITRADO_DE", opsNorm, log, TryExtractValorArbitradoFromText);

            ApplyFullTextValueFallback(fieldMatches, "VALOR_ARBITRADO_CM", fullNorm, log, TryExtractValorArbitradoFromText);
            if (!string.IsNullOrWhiteSpace(streamNorm))
                ApplyFullTextValueFallback(fieldMatches, "VALOR_ARBITRADO_CM", streamNorm, log, TryExtractValorArbitradoFromText);
            if (!string.IsNullOrWhiteSpace(opsNorm))
                ApplyFullTextValueFallback(fieldMatches, "VALOR_ARBITRADO_CM", opsNorm, log, TryExtractValorArbitradoFromText);

            ApplyFullTextValueFallback(fieldMatches, "CPF_PERITO", fullNorm, log, TryExtractCpfPeritoFromText);
            if (!string.IsNullOrWhiteSpace(streamNorm))
                ApplyFullTextValueFallback(fieldMatches, "CPF_PERITO", streamNorm, log, TryExtractCpfPeritoFromText);
            if (!string.IsNullOrWhiteSpace(opsNorm))
                ApplyFullTextValueFallback(fieldMatches, "CPF_PERITO", opsNorm, log, TryExtractCpfPeritoFromText);

            ApplyFullTextValueFallback(fieldMatches, "COMARCA", fullNorm, log, TryExtractComarcaFromText);
            if (!string.IsNullOrWhiteSpace(streamNorm))
                ApplyFullTextValueFallback(fieldMatches, "COMARCA", streamNorm, log, TryExtractComarcaFromText);
            if (!string.IsNullOrWhiteSpace(opsNorm))
                ApplyFullTextValueFallback(fieldMatches, "COMARCA", opsNorm, log, TryExtractComarcaFromText);
        }

        private static void ApplyFullTextValueFallback(
            Dictionary<string, List<FieldMatch>> fieldMatches,
            string field,
            string fullText,
            bool log,
            Func<string, string> extractor)
        {
            if (fieldMatches.TryGetValue(field, out var matches) && matches != null && matches.Count > 0)
            {
                // Only skip fallback when there is already at least one valid candidate.
                if (HasAnyValidFieldMatch(field, matches))
                    return;
            }
            var extracted = extractor(fullText);
            if (string.IsNullOrWhiteSpace(extracted))
                return;
            if (log)
                Console.Error.WriteLine($"[FULLTEXT] field={field} extracted=\"{TrimSnippet(extracted, 140)}\"");
            fieldMatches[field] = new List<FieldMatch>
            {
                new FieldMatch
                {
                    Field = field,
                    ValueText = extracted,
                    Score = 0.85,
                    Kind = "fulltext"
                }
            };
        }

        private static bool HasAnyValidFieldMatch(string field, List<FieldMatch> matches)
        {
            if (matches == null || matches.Count == 0)
                return false;
            foreach (var match in matches)
            {
                var val = match?.ValueText ?? "";
                if (string.IsNullOrWhiteSpace(val))
                    continue;
                val = NormalizeValueByField(field, val);
                if (string.IsNullOrWhiteSpace(val))
                    continue;
                if (IsValidFieldFormat(field, val))
                    return true;
            }
            return false;
        }
        private static string TryExtractProcessoJudicialFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var candidates = new List<string>();

            // tenta primeiro no contexto "processo/proc" para reduzir falsos positivos
            var reCtx = new Regex(@"(?is)processo\s*(?:judicial|n[ºo]?|proc\.?|de\s*n[ºo]?)\s*[:\-]?\s*([0-9][0-9\.\-\/\s]{8,40})");
            foreach (Match m in reCtx.Matches(text))
            {
                if (!m.Success)
                    continue;
                var rawCtx = m.Groups[1].Value;
                var cleanedCtx = Regex.Replace(rawCtx, @"\s+", "");
                var numCtx = Regex.Match(cleanedCtx, @"[0-9][0-9\.\-\/]{6,35}");
                if (numCtx.Success)
                    candidates.Add(numCtx.Value);
            }

            // fallback CNJ (standard + tribunal segment compact form)
            foreach (Match m in Regex.Matches(text, @"\b(\d{7}\s*-\s*\d{2}\s*\.\s*\d{4}\s*\.\s*(?:\d\s*\.\s*\d{2}|\d{3})\s*\.\s*\d{4})\b"))
            {
                if (m.Success)
                    candidates.Add(Regex.Replace(m.Groups[1].Value, @"\s+", ""));
            }

            // fallback formato antigo (ex.: 200.2003.515.780-5)
            foreach (Match m in Regex.Matches(text, @"\b(\d{3}\s*\.\s*\d{4}\s*\.\s*\d{3}\s*\.\s*\d{3}\s*-\s*\d)\b"))
            {
                if (m.Success)
                    candidates.Add(Regex.Replace(m.Groups[1].Value, @"\s+", ""));
            }

            foreach (var candidate in candidates
                         .Select(c => Regex.Replace(c ?? "", @"\s+", ""))
                         .Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                if (IsValidFieldFormat("PROCESSO_JUDICIAL", candidate))
                    return candidate;
            }

            return "";
        }

        private static string TryExtractProcessoAdministrativoFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var re = new Regex(@"(?is)processo\s+administrativo(?:\s+eletr[oô]nico)?[^\n]{0,80}?(?:n[ºo]?\s*)?([0-9][0-9\.\-\/\s]{5,30})");
            var m = re.Match(text);
            if (!m.Success)
                return "";
            var raw = m.Groups[1].Value;
            var cleaned = Regex.Replace(raw, @"\s+", "");
            var num = Regex.Match(cleaned, @"[0-9][0-9\.\-\/]{5,30}");
            return num.Success ? num.Value : cleaned;
        }

        private static string TryExtractCpfPeritoFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var re = new Regex(@"\b(\d{3}\s*\.?\s*\d{3}\s*\.?\s*\d{3}\s*-?\s*\d{2})\b");
            var best = "";
            var bestScore = -1;
            foreach (Match m in re.Matches(text))
            {
                var cpf = m.Groups[1].Value;
                var start = Math.Max(0, m.Index - 80);
                var end = Math.Min(text.Length, m.Index + m.Length + 80);
                var ctx = text.Substring(start, end - start).ToLowerInvariant();
                var score = 0;
                if (ctx.Contains("perit")) score += 2;
                if (ctx.Contains("interessad")) score += 1;
                if (ctx.Contains("cpf")) score += 1;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = cpf;
                }
            }
            if (string.IsNullOrWhiteSpace(best))
                return "";
            var digits = Regex.Replace(best, @"\D", "");
            if (digits.Length == 11)
                return $"{digits.Substring(0, 3)}.{digits.Substring(3, 3)}.{digits.Substring(6, 3)}-{digits.Substring(9, 2)}";
            return best;
        }

        private static string TryExtractValorArbitradoFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var re = new Regex(@"R\$\s*([0-9]{1,3}(?:\.[0-9]{3})*,\s*\d{2})");
            string bestVal = "";
            var bestScore = -1;
            var bestNum = 0.0;
            foreach (Match m in re.Matches(text))
            {
                var val = m.Groups[1].Value;
                var start = Math.Max(0, m.Index - 120);
                var end = Math.Min(text.Length, m.Index + m.Length + 120);
                var ctx = text.Substring(start, end - start).ToLowerInvariant();
                var score = 0;
                if (ctx.Contains("arbitr")) score += 2;
                if (ctx.Contains("honor")) score += 2;
                if (ctx.Contains("valor")) score += 1;
                if (ctx.Contains("fix")) score += 1;
                var num = 0.0;
                var clean = val.Replace(".", "").Replace(",", ".");
                double.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out num);
                if (score > bestScore || (score == bestScore && num > bestNum))
                {
                    bestScore = score;
                    bestNum = num;
                    bestVal = val;
                }
            }
            if (!string.IsNullOrWhiteSpace(bestVal))
                return bestVal;

            // Fallback para despachos com arbitramento em "vezes" do anexo I
            // (ex.: "valor de 1,5 (uma vez e meia) dos honorarios ... anexo I").
            var factorRe = new Regex(
                @"(?is)\b(?:no\s+)?valor\s+de\s*([0-9]{1,2}(?:\s*[.,]\s*[0-9]{1,2})?)\s*(?:\([^)]+\)\s*)*\)*\s*,?\s*(?:dos?\s+)?honor\w+\s+periciais?(?:\s+constantes?\s+do\s+anexo\s*i?)?");
            var factorMatch = factorRe.Match(text);
            if (!factorMatch.Success)
                return "";

            var rawFactor = Regex.Replace(factorMatch.Groups[1].Value, @"\s+", "");
            if (string.IsNullOrWhiteSpace(rawFactor))
                return "";

            var normalized = rawFactor.Replace(",", ".");
            if (!decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var fator))
                return "";
            if (fator <= 0m || fator > 20m)
                return "";

            var fatorMoney = fator.ToString("0.00", CultureInfo.InvariantCulture).Replace(".", ",");
            return fatorMoney;
        }
        private static string PickBestNameCandidate(List<string> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return "";
            var filtered = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim())
                .ToList();
            if (filtered.Count == 0)
                return "";

            var saCandidates = filtered
                .Where(c => System.Text.RegularExpressions.Regex.IsMatch(c, @"\sS\/A\b|\sS\.A\.?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .ToList();
            var pool = saCandidates.Count > 0 ? saCandidates : filtered;

            string best = "";
            var bestScore = int.MaxValue;
            foreach (var cand in pool)
            {
                var score = ScoreNameCandidate(cand);
                if (score < bestScore || (score == bestScore && cand.Length > best.Length))
                {
                    best = cand;
                    bestScore = score;
                }
            }
            return best;
        }

        private static int ScoreNameCandidate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return int.MaxValue;
            var t = text;
            var score = 0;
            score += System.Text.RegularExpressions.Regex.Matches(t, @"\b\p{Lu}{3,}\s+\p{Lu}{1,3}\b").Count;
            score += System.Text.RegularExpressions.Regex.Matches(t, @"\b\p{Lu}{1,3}\s+\p{Lu}{4,}\b").Count;
            score += System.Text.RegularExpressions.Regex.Matches(t, @"\b(DOS|DAS|DE|DO|DA|E|EM|NO|NA|NOS|NAS)\p{Lu}", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
            return score;
        }

        private static string NormalizeNameExtract(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text ?? "";
            var t = TextNormalization.NormalizeWhitespace(text);
            if (LooksLikeSpacedLetters(t) || TextUtils.ComputeWeirdSpacingRatio(t) >= 0.45)
            {
                var heavy = NormalizeValueByField("PROMOVENTE", t);
                if (!string.IsNullOrWhiteSpace(heavy))
                    t = heavy;
            }
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*/\s*", "/");
            t = TrimAfterSentence(t);
            t = t.Trim().TrimEnd(',', '.', ';', '–', '—', '-');
            return t;
        }

        private static string TrimAfterSentence(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text ?? "";
            var idx = text.IndexOf(". ", StringComparison.Ordinal);
            if (idx > 0)
                return text.Substring(0, idx).Trim();
            idx = text.IndexOf("; ", StringComparison.Ordinal);
            if (idx > 0)
                return text.Substring(0, idx).Trim();
            return text;
        }

        private static bool BandMatches(string? ruleBand, string? entryBand)
        {
            if (string.IsNullOrWhiteSpace(ruleBand))
                return true;
            if (string.IsNullOrWhiteSpace(entryBand))
                return true;
            var r = ruleBand.Trim();
            var e = entryBand.Trim();
            if (string.Equals(r, e, StringComparison.OrdinalIgnoreCase))
                return true;
            // aceita front_head ~ front, back_tail ~ back
            if (r.StartsWith("front", StringComparison.OrdinalIgnoreCase) &&
                e.StartsWith("front", StringComparison.OrdinalIgnoreCase))
                return true;
            if (r.StartsWith("back", StringComparison.OrdinalIgnoreCase) &&
                e.StartsWith("back", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static (double Score, double PrevScore, double NextScore, double ValueScore,
            string PrevExpected, string PrevActual, string ValueExpected, string NextExpected, string NextActual)
            ScoreRegexCandidateDmp(FieldPatternEntry entry, List<TokenInfo> tokens, int startIdx, int endIdx, string valuePattern)
        {
            var expectedPrev = entry.PrevPatternTyped ?? "";
            var expectedNext = entry.NextPatternTyped ?? "";
            var expectedValue = entry.ValuePatternTyped ?? "";

            var prevCount = CountPatternTokens(expectedPrev);
            var nextCount = CountPatternTokens(expectedNext);

            var prevActual = prevCount > 0 && startIdx - prevCount >= 0
                ? string.Join(" ", tokens.Skip(startIdx - prevCount).Take(prevCount).Select(t => t.Pattern))
                : "";
            var nextActual = nextCount > 0 && endIdx + nextCount < tokens.Count
                ? string.Join(" ", tokens.Skip(endIdx + 1).Take(nextCount).Select(t => t.Pattern))
                : "";

            double prevScore = string.IsNullOrWhiteSpace(expectedPrev) ? 1.0 : PatternSimilarity(expectedPrev, prevActual);
            double nextScore = string.IsNullOrWhiteSpace(expectedNext) ? 1.0 : PatternSimilarity(expectedNext, nextActual);
            double valueScore = string.IsNullOrWhiteSpace(expectedValue) ? 1.0 : PatternSimilarity(expectedValue, valuePattern);

            // Regex encontrou o candidato; agora o DMP valida o TRIO (prev|value|next).
            var expectedParts = new List<string>();
            var actualParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(expectedPrev))
            {
                expectedParts.Add(expectedPrev);
                actualParts.Add(prevActual);
            }
            if (!string.IsNullOrWhiteSpace(expectedValue))
            {
                expectedParts.Add(expectedValue);
                actualParts.Add(valuePattern);
            }
            if (!string.IsNullOrWhiteSpace(expectedNext))
            {
                expectedParts.Add(expectedNext);
                actualParts.Add(nextActual);
            }

            var dmpScore = expectedParts.Count == 0
                ? 0.85
                : PatternSimilarity(string.Join("|", expectedParts), string.Join("|", actualParts));

            // Se valor diverge, ainda tenta validar só âncoras (prev+next).
            if (!string.IsNullOrWhiteSpace(expectedPrev) && !string.IsNullOrWhiteSpace(expectedNext))
            {
                var anchorsScore = PatternSimilarity(
                    string.Join("|", new[] { expectedPrev, expectedNext }),
                    string.Join("|", new[] { prevActual, nextActual }));
                if (anchorsScore > dmpScore)
                    dmpScore = anchorsScore;
            }

            // Se âncoras falharem, ainda tenta validar pelo valor puro.
            if (!string.IsNullOrWhiteSpace(expectedValue))
            {
                var valueOnlyScore = PatternSimilarity(expectedValue, valuePattern);
                if (valueOnlyScore > dmpScore)
                    dmpScore = valueOnlyScore;
            }

            // Regex conta como evidência de que o trecho é candidato.
            const double regexHitScore = 1.0;
            var finalScore = (dmpScore + regexHitScore) / 2.0;

            return (finalScore, prevScore, nextScore, valueScore, expectedPrev, prevActual, expectedValue, expectedNext, nextActual);
        }

        private static int CountPatternTokens(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return 0;
            return pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static bool IsRawPatternTooGeneric(FieldPatternEntry entry)
        {
            var prevGeneric = IsPatternGeneric(entry.PrevPatternRaw);
            var nextGeneric = IsPatternGeneric(entry.NextPatternRaw);
            return prevGeneric && nextGeneric;
        }

        private static bool ShouldUseRaw(List<TokenInfo> rawTokens)
        {
            if (rawTokens.Count == 0)
                return false;
            int single = 0;
            int run = 0;
            int maxRun = 0;
            foreach (var t in rawTokens)
            {
                if (t.Pattern == "1")
                {
                    single++;
                    run++;
                    if (run > maxRun) maxRun = run;
                }
                else
                {
                    run = 0;
                }
            }
            double pct = single / (double)rawTokens.Count;
            // ativa RAW se houver run longo de letras espacadas OU alta proporcao de singles
            return maxRun >= 5 || pct >= 0.12;
        }

        private static bool IsTypedPatternTooGeneric(FieldPatternEntry entry)
        {
            var prevGeneric = IsPatternGenericTyped(entry.PrevPatternTyped);
            var nextGeneric = IsPatternGenericTyped(entry.NextPatternTyped);
            return prevGeneric && nextGeneric;
        }

        private static bool IsPatternGeneric(string? pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return true;
            var tokens = ParsePatternTokens(pattern);
            if (tokens.Count == 0)
                return true;
            bool allW = true;
            foreach (var tok in tokens)
            {
                if (tok != "W")
                {
                    allW = false;
                    break;
                }
            }
            if (allW)
                return true;
            return tokens.Count <= 2;
        }

        private static bool IsPatternGenericTyped(string? pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return true;
            var tokens = ParsePatternTokens(pattern);
            if (tokens.Count == 0)
                return true;
            foreach (var tok in tokens)
            {
                var head = tok[0];
                // qualquer coisa que nao seja T/L/U/P/S/: eh informativa
                if (head != 'T' && head != 'L' && head != 'U' && head != 'P' && head != 'S' && head != ':')
                    return false;
            }
            return true;
        }

        private static void ApplyComarcaWithinVara(Dictionary<string, List<FieldMatch>> fieldMatches)
        {
            if (!fieldMatches.TryGetValue("COMARCA", out var comarcaMatches))
                return;

            if (!fieldMatches.TryGetValue("VARA", out var varaMatches) || varaMatches.Count == 0)
                return;

            var derived = new List<FieldMatch>();
            foreach (var vara in varaMatches)
            {
                var comarca = ExtractComarcaFromVaraText(vara.ValueText);
                if (string.IsNullOrWhiteSpace(comarca))
                    continue;
                derived.Add(new FieldMatch
                {
                    Field = "COMARCA",
                    Pdf = vara.Pdf,
                    ValueText = comarca,
                    ValuePattern = vara.ValuePattern,
                    ValueExpectedPattern = vara.ValueExpectedPattern,
                    Score = vara.Score,
                    PrevScore = vara.PrevScore,
                    NextScore = vara.NextScore,
                    ValueScore = vara.ValueScore,
                    PrevText = vara.PrevText,
                    NextText = vara.NextText,
                    PrevExpectedPattern = vara.PrevExpectedPattern,
                    PrevActualPattern = vara.PrevActualPattern,
                    PrevPatternScore = vara.PrevPatternScore,
                    PrevExpectedText = vara.PrevExpectedText,
                    PrevActualText = vara.PrevActualText,
                    PrevTextScore = vara.PrevTextScore,
                    NextExpectedPattern = vara.NextExpectedPattern,
                    NextActualPattern = vara.NextActualPattern,
                    NextPatternScore = vara.NextPatternScore,
                    NextExpectedText = vara.NextExpectedText,
                    NextActualText = vara.NextActualText,
                    NextTextScore = vara.NextTextScore,
                    StartOp = vara.StartOp,
                    EndOp = vara.EndOp,
                    Band = vara.Band,
                    YRange = vara.YRange,
                    XRange = vara.XRange,
                    Kind = "derived_from_vara",
                    PrevMode = vara.PrevMode,
                    NextMode = vara.NextMode
                });
            }

            var comarcaInsideVara = comarcaMatches
                .Where(c => varaMatches.Any(v => c.StartOp >= v.StartOp && c.EndOp <= v.EndOp))
                .ToList();

            // If we can't derive or anchor comarca inside vara, keep the original comarca matches.
            if (derived.Count == 0 && comarcaInsideVara.Count == 0)
                return;

            var dedup = new Dictionary<string, FieldMatch>(StringComparer.OrdinalIgnoreCase);
            foreach (var match in derived.Concat(comarcaInsideVara))
            {
                var key = NormalizePatternText(match.ValueText);
                if (string.IsNullOrWhiteSpace(key))
                    continue;
                if (!dedup.TryGetValue(key, out var existing) || match.Score > existing.Score)
                    dedup[key] = match;
            }

            fieldMatches["COMARCA"] = dedup.Values
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.StartOp)
                .ToList();
        }

        private static void ApplyPeritoDerivedFields(Dictionary<string, List<FieldMatch>> fieldMatches)
        {
            if (!fieldMatches.TryGetValue("PERITO", out var peritoMatches) || peritoMatches.Count == 0)
                return;

            fieldMatches.TryGetValue("CPF_PERITO", out var cpfMatches);
            fieldMatches.TryGetValue("ESPECIALIDADE", out var especMatches);

            // Drop CPF candidates that are just a prefix/slice of the CNJ process number (common false positive).
            if (cpfMatches != null && cpfMatches.Count > 0 &&
                fieldMatches.TryGetValue("PROCESSO_JUDICIAL", out var procMatches) && procMatches != null && procMatches.Count > 0)
            {
                var procDigits = Regex.Replace(procMatches[0].ValueText ?? "", @"\D", "");
                if (procDigits.Length >= 20)
                {
                    var filteredCpf = cpfMatches
                        .Where(m =>
                        {
                            var cpfDigits = Regex.Replace(m.ValueText ?? "", @"\D", "");
                            if (cpfDigits.Length != 11)
                                return false;
                            return !procDigits.Contains(cpfDigits, StringComparison.Ordinal);
                        })
                        .ToList();

                    if (filteredCpf.Count != cpfMatches.Count)
                    {
                        cpfMatches = filteredCpf;
                        if (cpfMatches.Count == 0)
                            fieldMatches.Remove("CPF_PERITO");
                        else
                            fieldMatches["CPF_PERITO"] = cpfMatches;
                    }
                }
            }

            // Centraliza validações em regex do catálogo (YAML/JSON).

            // Remove PERITO falsos positivos (ex.: "nomeada", "nos autos do processo ...")
            peritoMatches = peritoMatches
                .Where(m => IsValidPeritoValue(m.ValueText))
                .OrderByDescending(m => m.Score)
                .ThenBy(m => m.StartOp)
                .ToList();
            if (peritoMatches.Count > 1)
            {
                var best = peritoMatches
                    .OrderByDescending(m => (m.ValueText ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
                    .ThenByDescending(m => (m.ValueText ?? "").Length)
                    .ThenByDescending(m => m.Score)
                    .First();
                var bestKey = NormalizeForContains(best.ValueText ?? "").Replace(" ", "");
                if (!string.IsNullOrWhiteSpace(bestKey))
                {
                    foreach (var m in peritoMatches)
                    {
                        var key = NormalizeForContains(m.ValueText ?? "").Replace(" ", "");
                        if (key == bestKey)
                            m.ValueText = best.ValueText;
                    }
                }
            }
            fieldMatches["PERITO"] = peritoMatches;
            if (peritoMatches.Count == 0)
                return;

            var cpfMissing = cpfMatches == null || cpfMatches.Count == 0 || !cpfMatches.Any(m => !string.IsNullOrWhiteSpace(m.ValueText) && ValueMatchesRegexCatalog("CPF_PERITO", m.ValueText));
            var especMissing = especMatches == null || especMatches.Count == 0 || !especMatches.Any(m => !string.IsNullOrWhiteSpace(m.ValueText) && ValueMatchesRegexCatalog("ESPECIALIDADE", m.ValueText));

            // Prefer the first CPF/ESPECIALIDADE that appears after the PERITO match.
            if (cpfMatches != null && cpfMatches.Count > 0)
            {
                var cpfValid = cpfMatches
                    .Where(m => !string.IsNullOrWhiteSpace(m.ValueText) && ValueMatchesRegexCatalog("CPF_PERITO", m.ValueText))
                    .ToList();
                var cpfAfter = FindFirstAfter(peritoMatches, cpfValid);
                if (cpfAfter == null)
                    cpfAfter = FindClosestPeritoRelated(peritoMatches, cpfValid, maxOpDistance: 8);
                if (cpfAfter != null)
                {
                    fieldMatches["CPF_PERITO"] = new List<FieldMatch>
                    {
                        CloneMatch(cpfAfter, "CPF_PERITO", "derived_near_perito")
                    };
                    cpfMissing = false;
                }
            }

            if (especMatches != null && especMatches.Count > 0)
            {
                var especValid = especMatches
                    .Where(m => !string.IsNullOrWhiteSpace(m.ValueText) && ValueMatchesRegexCatalog("ESPECIALIDADE", m.ValueText))
                    .ToList();
                var especAfter = FindFirstAfter(peritoMatches, especValid);
                if (especAfter == null)
                    especAfter = FindClosestPeritoRelated(peritoMatches, especValid, maxOpDistance: 10);
                if (especAfter != null)
                {
                    fieldMatches["ESPECIALIDADE"] = new List<FieldMatch>
                    {
                        CloneMatch(especAfter, "ESPECIALIDADE", "derived_near_perito")
                    };
                    especMissing = false;
                }
            }

            var derivedCpf = new List<FieldMatch>();
            var derivedEspec = new List<FieldMatch>();

            foreach (var p in peritoMatches)
            {
                if (cpfMissing)
                {
                    var v = ExtractRegexValueFromCatalog("CPF_PERITO", p.ValueText ?? "");
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        derivedCpf.Add(new FieldMatch
                        {
                            Field = "CPF_PERITO",
                            Pdf = p.Pdf,
                            ValueText = v!,
                            ValuePattern = p.ValuePattern,
                            ValueExpectedPattern = p.ValueExpectedPattern,
                            Score = p.Score,
                            PrevScore = p.PrevScore,
                            NextScore = p.NextScore,
                            ValueScore = p.ValueScore,
                            StartOp = p.StartOp,
                            EndOp = p.EndOp,
                            Band = p.Band,
                            YRange = p.YRange,
                            XRange = p.XRange,
                            Kind = "derived_from_perito",
                            PrevMode = p.PrevMode,
                            NextMode = p.NextMode
                        });
                    }
                }

                if (especMissing)
                {
                    var v = ExtractRegexValueFromCatalog("ESPECIALIDADE", p.ValueText ?? "");
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        derivedEspec.Add(new FieldMatch
                        {
                            Field = "ESPECIALIDADE",
                            Pdf = p.Pdf,
                            ValueText = v!,
                            ValuePattern = p.ValuePattern,
                            ValueExpectedPattern = p.ValueExpectedPattern,
                            Score = p.Score,
                            PrevScore = p.PrevScore,
                            NextScore = p.NextScore,
                            ValueScore = p.ValueScore,
                            StartOp = p.StartOp,
                            EndOp = p.EndOp,
                            Band = p.Band,
                            YRange = p.YRange,
                            XRange = p.XRange,
                            Kind = "derived_from_perito",
                            PrevMode = p.PrevMode,
                            NextMode = p.NextMode
                        });
                    }
                }
            }

            if (cpfMissing && derivedCpf.Count > 0)
                fieldMatches["CPF_PERITO"] = derivedCpf;
            if (especMissing && derivedEspec.Count > 0)
                fieldMatches["ESPECIALIDADE"] = derivedEspec;
        }

        private static bool IsValidPeritoValue(string? value)
        {
            return ExplainPeritoReject(value) == "ok";
        }

        private static string ExplainPeritoReject(string? value)
        {
            return ValidatorRules.ExplainPeritoReject(
                value,
                cleanPeritoValue: CleanPeritoValue,
                normalizePatternText: NormalizePatternText,
                containsInstitutional: ContainsInstitutional,
                containsProcessualNoise: ContainsProcessualNoise,
                containsDocumentBoilerplate: ContainsDocumentBoilerplate,
                looksLikePersonNameLoose: LooksLikePersonNameLoose,
                normalizeToken: NormalizeToken,
                isEspecialidadeToken: IsEspecialidadeToken);
        }

        private static string CleanPeritoValue(string? value)
        {
            return ValidatorRules.CleanPeritoValue(
                value,
                normalizeToken: NormalizeToken,
                isPeritoStopwordToken: ValidatorRules.IsPeritoStopwordToken,
                stripPeritoTrailingContext: ValidatorRules.StripPeritoTrailingContext,
                looksLikeUpperGlue: LooksLikeUpperGlue,
                splitUpperByCommonNames: SplitUpperByCommonNames,
                fixDanglingUpperInitial: FixDanglingUpperInitial,
                containsEspecialidadeToken: ContainsEspecialidadeToken,
                looksLikePersonNameLoose: LooksLikePersonNameLoose,
                extractLeadingNameCandidate: t => ValidatorRules.ExtractLeadingNameCandidate(t, NormalizeToken),
                isLikelyOrganization: IsLikelyOrganization);
        }

        private static FieldMatch? FindFirstAfter(List<FieldMatch> peritos, List<FieldMatch> candidates)
        {
            if (peritos.Count == 0 || candidates.Count == 0)
                return null;

            foreach (var per in peritos.OrderBy(p => p.StartOp))
            {
                var cand = candidates
                    .Where(c => c.StartOp >= per.EndOp && string.Equals(c.Band, per.Band, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => c.StartOp)
                    .FirstOrDefault();
                if (cand != null)
                    return cand;
            }

            return null;
        }

        private static FieldMatch? FindClosestPeritoRelated(List<FieldMatch> peritos, List<FieldMatch> candidates, int maxOpDistance)
        {
            if (peritos.Count == 0 || candidates.Count == 0)
                return null;

            FieldMatch? best = null;
            var bestDist = int.MaxValue;
            foreach (var per in peritos)
            {
                foreach (var cand in candidates)
                {
                    if (!string.Equals(cand.Band, per.Band, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var dist = Math.Abs(cand.StartOp - per.StartOp);
                    if (dist <= maxOpDistance && dist < bestDist)
                    {
                        bestDist = dist;
                        best = cand;
                    }
                }
            }
            return best;
        }

        private static FieldMatch CloneMatch(FieldMatch src, string field, string kind)
        {
            var valueText = src.ValueText;
            if (field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(valueText))
            {
                var m = System.Text.RegularExpressions.Regex.Match(valueText, @"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b");
                if (m.Success)
                    valueText = m.Value;
            }
            return new FieldMatch
            {
                Field = field,
                Pdf = src.Pdf,
                ValueText = valueText,
                ValuePattern = src.ValuePattern,
                ValueExpectedPattern = src.ValueExpectedPattern,
                Score = src.Score,
                PrevScore = src.PrevScore,
                NextScore = src.NextScore,
                ValueScore = src.ValueScore,
                PrevText = src.PrevText,
                NextText = src.NextText,
                PrevExpectedPattern = src.PrevExpectedPattern,
                PrevActualPattern = src.PrevActualPattern,
                PrevPatternScore = src.PrevPatternScore,
                PrevExpectedText = src.PrevExpectedText,
                PrevActualText = src.PrevActualText,
                PrevTextScore = src.PrevTextScore,
                NextExpectedPattern = src.NextExpectedPattern,
                NextActualPattern = src.NextActualPattern,
                NextPatternScore = src.NextPatternScore,
                NextExpectedText = src.NextExpectedText,
                NextActualText = src.NextActualText,
                NextTextScore = src.NextTextScore,
                StartOp = src.StartOp,
                EndOp = src.EndOp,
                Band = src.Band,
                YRange = src.YRange,
                XRange = src.XRange,
                Kind = kind,
                PrevMode = src.PrevMode,
                NextMode = src.NextMode
            };
        }

        private static string? ExtractComarcaFromVaraText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var normalized = NormalizePatternText(text);
            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            // Primary path: explicit "Comarca de/da/do ...".
            var match = System.Text.RegularExpressions.Regex.Match(
                normalized,
                @"(?i)\bComarca\s+(de|da|do)\s+([^,;\.\)\-]+)");
            if (!match.Success)
            {
                // Common form in vara text: "3ª Vara de Família de Campina Grande".
                var tailCity = System.Text.RegularExpressions.Regex.Match(
                    normalized,
                    @"(?i)\bVara\b[\p{L}\p{N}\sºª\.\-\/]{0,120}?\bde\b\s+([\p{L}]{2,}(?:\s+[\p{L}]{2,}){0,4})\s*$");
                if (!tailCity.Success)
                    return null;

                var city = tailCity.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(city))
                    return null;
                city = Regex.Replace(city, @"\s{2,}", " ");
                city = Regex.Replace(city, @"(?i)\b(?:pb|pe|rn|ce|ba|al|se)\b$", "").Trim();
                if (string.IsNullOrWhiteSpace(city))
                    return null;
                return $"Comarca de {city}";
            }
            var value = match.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(value))
                return null;
            value = Regex.Replace(value, @"\s{2,}", " ");
            return $"Comarca de {value}";
        }

        private static void RunPatternReport(MatchOptions options)
        {
            var entries = LoadPatternEntries(options.PatternsPath, options.Fields);
            if (entries.Count == 0)
            {
                Console.WriteLine("Nenhum pattern encontrado.");
                return;
            }
            EnsureRegexCatalog(options.PatternsPath, options.Log);

            if (options.Page <= 0 || options.Obj <= 0)
            {
                Console.WriteLine("Informe --page e --obj (use operpdf objdiff para encontrar).");
                return;
            }

            var groups = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Field))
                .GroupBy(e => e.Field!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var stats = groups.ToDictionary(g => g.Key, g => new FieldStats { Field = g.Key }, StringComparer.OrdinalIgnoreCase);
            var progress = ProgressReporter.FromConfig("report", options.Inputs.Count);

            foreach (var pdf in options.Inputs)
            {
                if (!File.Exists(pdf))
                {
                    progress?.Tick(Path.GetFileName(pdf));
                    continue;
                }
                var tokens = ExtractTokensFromPdf(pdf, options.Page, options.Obj, options.OpFilter);
                var rawTokens = ExtractRawTokensFromPdf(pdf, options.Page, options.Obj, options.OpFilter);
                if (tokens.Count == 0 && rawTokens.Count == 0)
                {
                    progress?.Tick(Path.GetFileName(pdf));
                    continue;
                }
                var tokenPatterns = tokens.Select(t => t.Pattern).ToList();
                var rawPatterns = rawTokens.Select(t => t.Pattern).ToList();

                foreach (var group in groups)
                {
                    var fieldStats = stats[group.Key];
                    fieldStats.Total++;

                    var matches = new List<FieldMatch>();
                        foreach (var entry in group)
                        {
                            var localMatches = FindMatchesOrdered(tokens, tokenPatterns, rawTokens, rawPatterns, entry, options.MinScore, pdf, options.MaxPairs, options.MaxCandidates, HasAdiantamento(tokens, rawTokens), options.Log, options.UseRaw, null);
                            matches.AddRange(localMatches);
                        }

                    if (matches.Count == 0)
                        fieldStats.Misses++;
                    else if (matches.Count == 1)
                        fieldStats.Hits++;
                    else
                        fieldStats.Multi++;
                }
                progress?.Tick(Path.GetFileName(pdf));
                // libera memoria entre PDFs
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Console.WriteLine("RELATORIO (sensibilidade/especificidade por campo)");
            foreach (var kv in stats.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
            {
                var st = kv.Value;
                st.Sensitivity = st.Total == 0 ? 0 : (double)st.Hits / st.Total;
                var denom = st.Hits + st.Multi;
                st.Specificity = denom == 0 ? 0 : (double)st.Hits / denom;
                Console.WriteLine($"FIELD {st.Field}");
                Console.WriteLine($"  total={st.Total} hit={st.Hits} miss={st.Misses} multi={st.Multi}");
                Console.WriteLine($"  sens={st.Sensitivity:F2} spec={st.Specificity:F2}");
            }

            var totalPdfs = stats.Count > 0 ? stats.Values.Max(s => s.Total) : 0;
            var avgSens = stats.Count > 0 ? stats.Values.Average(s => s.Sensitivity) : 0;
            var avgSpec = stats.Count > 0 ? stats.Values.Average(s => s.Specificity) : 0;
            ReportUtils.WriteSummary("RESUMO (pattern report)", new List<(string, string)>
            {
                ("total_pdfs", totalPdfs.ToString()),
                ("campos", stats.Count.ToString()),
                ("sens_avg", ReportUtils.F(avgSens, 4)),
                ("spec_avg", ReportUtils.F(avgSpec, 4))
            });

            var topSens = stats.Values
                .OrderByDescending(s => s.Sensitivity)
                .ThenBy(s => s.Field, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(s => new[] { s.Field, ReportUtils.F(s.Sensitivity, 3), ReportUtils.F(s.Specificity, 3), s.Total.ToString() });
            ReportUtils.WriteTable("TOP SENS (10)", new[] { "field", "sens", "spec", "total" }, topSens);

            var bottomSens = stats.Values
                .OrderBy(s => s.Sensitivity)
                .ThenBy(s => s.Field, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(s => new[] { s.Field, ReportUtils.F(s.Sensitivity, 3), ReportUtils.F(s.Specificity, 3), s.Total.ToString() });
            ReportUtils.WriteTable("BOTTOM SENS (10)", new[] { "field", "sens", "spec", "total" }, bottomSens);

            if (!string.IsNullOrWhiteSpace(options.OutPath))
            {
                var json = JsonSerializer.Serialize(stats.Values.OrderBy(s => s.Field, StringComparer.OrdinalIgnoreCase), new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                Directory.CreateDirectory(Path.GetDirectoryName(options.OutPath) ?? ".");
                File.WriteAllText(options.OutPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                Console.WriteLine("Arquivo salvo: " + options.OutPath);
            }
        }

        private static void RunPatternAnchors(AnchorOptions options)
        {
            var entries = LoadPatternEntries(options.PatternsPath, options.Fields);
            if (entries.Count == 0)
            {
                Console.WriteLine("Nenhum pattern encontrado.");
                return;
            }

            if (options.Page <= 0 || options.Obj <= 0)
            {
                Console.WriteLine("Informe --page e --obj (use operpdf objdiff para encontrar).");
                return;
            }

            var groups = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Field))
                .GroupBy(e => e.Field!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var pdf in options.Inputs)
            {
                if (!File.Exists(pdf))
                {
                    Console.WriteLine("PDF nao encontrado: " + pdf);
                    continue;
                }

                var tokens = ExtractTokensFromPdf(pdf, options.Page, options.Obj, options.OpFilter);
                var rawTokens = ExtractRawTokensFromPdf(pdf, options.Page, options.Obj, options.OpFilter);
                if (tokens.Count == 0 && rawTokens.Count == 0)
                {
                    Console.WriteLine("Sem tokens: " + pdf);
                    continue;
                }

                var tokenPatterns = tokens.Select(t => t.Pattern).ToList();
                var rawPatterns = rawTokens.Select(t => t.Pattern).ToList();

                Console.WriteLine($"PDF: {Path.GetFileName(pdf)}");

                foreach (var group in groups)
                {
                    Console.WriteLine($"  FIELD {group.Key}");
                    int idx = 1;
                    foreach (var entry in group)
                    {
                        var useRaw = HasRawPatterns(entry) && !HasTypedPatterns(entry) && rawTokens.Count > 0;
                        var baseTokens = useRaw ? rawTokens : tokens;
                        var basePatterns = useRaw ? rawPatterns : tokenPatterns;

                        var (roiTokens, roiPatterns, applied) = ApplyRoi(baseTokens, entry);

                        var prevSeg = BuildPatternSegment(entry.Prev, entry.PrevPatternTyped, entry.PrevPatternRaw, useRaw);
                        var nextSeg = BuildPatternSegment(entry.Next, entry.NextPatternTyped, entry.NextPatternRaw, useRaw);
                        var prevTokens = ParsePatternTokens(prevSeg.Pattern);
                        var nextTokens = ParsePatternTokens(nextSeg.Pattern);

                        if ((options.Side == "both" || options.Side == "prev") && prevTokens.Count > 0)
                        {
                            var prevHits = FindAnchorHits(roiPatterns, roiTokens, prevTokens, prevSeg.Text, 0, roiTokens.Count, options.MinScore);
                            var usedTokens = roiTokens;
                            if (prevHits.Count == 0 && applied)
                            {
                                prevHits = FindAnchorHits(basePatterns, baseTokens, prevTokens, prevSeg.Text, 0, baseTokens.Count, options.MinScore);
                                usedTokens = baseTokens;
                            }
                            if (prevHits.Count == 0 && useRaw)
                            {
                                var (roiTokens2, roiPatterns2, applied2) = ApplyRoi(tokens, entry);
                                prevHits = FindAnchorHits(roiPatterns2, roiTokens2, prevTokens, prevSeg.Text, 0, roiTokens2.Count, options.MinScore);
                                usedTokens = roiTokens2;
                                if (prevHits.Count == 0 && applied2)
                                {
                                    prevHits = FindAnchorHits(tokenPatterns, tokens, prevTokens, prevSeg.Text, 0, tokens.Count, options.MinScore);
                                    usedTokens = tokens;
                                }
                            }

                            Console.WriteLine($"    [{idx:D2}] PREV PAT={prevSeg.Pattern} \"{prevSeg.Text}\"");
                            PrintAnchorHits(prevHits, usedTokens, options.Limit);
                        }

                        if ((options.Side == "both" || options.Side == "next") && nextTokens.Count > 0)
                        {
                            var nextHits = FindAnchorHits(roiPatterns, roiTokens, nextTokens, nextSeg.Text, 0, roiTokens.Count, options.MinScore);
                            var usedTokens = roiTokens;
                            if (nextHits.Count == 0 && applied)
                            {
                                nextHits = FindAnchorHits(basePatterns, baseTokens, nextTokens, nextSeg.Text, 0, baseTokens.Count, options.MinScore);
                                usedTokens = baseTokens;
                            }
                            if (nextHits.Count == 0 && useRaw)
                            {
                                var (roiTokens2, roiPatterns2, applied2) = ApplyRoi(tokens, entry);
                                nextHits = FindAnchorHits(roiPatterns2, roiTokens2, nextTokens, nextSeg.Text, 0, roiTokens2.Count, options.MinScore);
                                usedTokens = roiTokens2;
                                if (nextHits.Count == 0 && applied2)
                                {
                                    nextHits = FindAnchorHits(tokenPatterns, tokens, nextTokens, nextSeg.Text, 0, tokens.Count, options.MinScore);
                                    usedTokens = tokens;
                                }
                            }

                            Console.WriteLine($"    [{idx:D2}] NEXT PAT={nextSeg.Pattern} \"{nextSeg.Text}\"");
                            PrintAnchorHits(nextHits, usedTokens, options.Limit);
                        }

                        idx++;
                    }
                }
                Console.WriteLine();
            }
        }

        private sealed class ScanHit
        {
            public int Page { get; set; }
            public int Obj { get; set; }
            public double Score { get; set; }
            public int Hits { get; set; }
            public double Avg { get; set; }
            public double Coverage { get; set; }
        }

        private sealed class GroupedHit
        {
            public int Page { get; set; }
            public int Obj { get; set; }
            public double Score { get; set; }
            public double BaseScore { get; set; }
            public double SignalScore { get; set; }
            public bool HeaderHit { get; set; }
            public bool TitleHit { get; set; }
            public double StructureScore { get; set; }
            public int StructureLines { get; set; }
            public double StructureStep { get; set; }
            public int Hits { get; set; }
            public double Avg { get; set; }
            public double Coverage { get; set; }
            public int Tokens { get; set; }
        }

        private static GroupedHit? PickBestStream(List<GroupedHit> candidates, int minTokens)
        {
            var filtered = candidates.Where(c => c.Tokens >= minTokens).ToList();
            if (filtered.Count == 0)
                filtered = candidates;
            return filtered
                .OrderByDescending(c => c.Score)
                .ThenByDescending(c => c.Tokens)
                .FirstOrDefault();
        }

        private static void RunPatternFind(FindOptions options)
        {
            if (!string.IsNullOrWhiteSpace(options.InputDir))
            {
                if (!Directory.Exists(options.InputDir))
                {
                    Console.WriteLine("Diretorio nao encontrado: " + options.InputDir);
                    return;
                }

                var files = Directory.GetFiles(options.InputDir, "*.pdf")
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (options.Limit > 0 && files.Count > options.Limit)
                    files = files.Take(options.Limit).ToList();

                if (files.Count == 0)
                {
                    Console.WriteLine("Nenhum PDF encontrado.");
                    return;
                }

                for (int i = 0; i < files.Count; i++)
                {
                    var file = files[i];
                    var local = CloneFindOptions(options);
                    local.Input = file;
                    local.InputDir = "";
                    if (!string.IsNullOrWhiteSpace(options.OutPath))
                        local.OutPath = BuildFindOutPath(options.OutPath!, file, files.Count > 1);
                    RunPatternFind(local);
                }
                return;
            }

            if (!File.Exists(options.Input))
            {
                Console.WriteLine("PDF nao encontrado: " + options.Input);
                return;
            }

            var entries = LoadPatternEntries(options.PatternsPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (entries.Count == 0)
            {
                Console.WriteLine("Nenhum pattern encontrado.");
                return;
            }
            EnsureRegexCatalog(options.PatternsPath, options.Log);

            var docName = ReadDocNameFromPatterns(options.PatternsPath);
            var optionalFields = ReadFieldListFromPatterns(options.PatternsPath, "optional_fields", "optionalFields", "optional");
            var requiredAll = BuildRequiredFieldSet(entries, options.Fields, optionalFields);
            var groupsAll = BuildGroups(entries, requiredAll);
            var rejectFields = ReadFieldListFromPatterns(options.PatternsPath, "reject_fields", "rejectFields", "reject");
            var groupsReject = rejectFields.Count > 0 ? BuildGroups(entries, rejectFields) : new List<IGrouping<string, FieldPatternEntry>>();
            var rejectTexts = ReadFieldListFromPatterns(options.PatternsPath, "reject_texts", "rejectTexts", "reject_text", "rejectText");
            DocumentValidationRules.EnsureDespachoRejectTexts(rejectTexts, docName);

            var page1Fields = options.Page1Fields.Count > 0
                ? new HashSet<string>(options.Page1Fields, StringComparer.OrdinalIgnoreCase)
                : ReadFieldListFromPatterns(options.PatternsPath, "page1_fields", "page1Fields", "p1_fields", "page1");
            var page2Fields = options.Page2Fields.Count > 0
                ? new HashSet<string>(options.Page2Fields, StringComparer.OrdinalIgnoreCase)
                : ReadFieldListFromPatterns(options.PatternsPath, "page2_fields", "page2Fields", "p2_fields", "page2");

            var page1Required = page1Fields.Count > 0 ? RemoveOptional(page1Fields, optionalFields) : requiredAll;
            var page2Required = page2Fields.Count > 0 ? RemoveOptional(page2Fields, optionalFields) : requiredAll;
            var groupsP1 = page1Required.Count > 0 ? BuildGroups(entries, page1Required) : groupsAll;
            var groupsP2 = page2Required.Count > 0 ? BuildGroups(entries, page2Required) : groupsAll;

            using var reader = new PdfReader(options.Input);
            using var doc = new PdfDocument(reader);

            var hits = new List<ScanHit>();
            var hitsP1List = new List<ScanHit>();
            var hitsP2List = new List<ScanHit>();
            var bestByPageP1 = new Dictionary<int, GroupedHit>();
            var bestByPageP2 = new Dictionary<int, GroupedHit>();
            var rejectedPages = new HashSet<int>();
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                var pageHeight = page.GetPageSize().GetHeight();
                var pageWidth = page.GetPageSize().GetWidth();

                foreach (var stream in EnumerateStreams(contents))
                {
                    if (rejectedPages.Contains(p))
                        break;
                    int objId = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                    if (objId <= 0)
                        continue;

                    var tokens = ExtractTokensFromStream(stream, resources, pageHeight, pageWidth, options.OpFilter);
                    var rawTokens = ExtractRawTokensFromStream(stream, resources, pageHeight, pageWidth, options.OpFilter);
                    if (tokens.Count == 0 && rawTokens.Count == 0)
                        continue;

                    var tokenPatterns = tokens.Select(t => t.Pattern).ToList();
                    var rawPatterns = rawTokens.Select(t => t.Pattern).ToList();

                    if (rejectTexts.Count > 0)
                    {
                        var baseText = tokens.Count > 0
                            ? string.Join(" ", tokens.Select(t => t.Text))
                            : string.Join(" ", rawTokens.Select(t => t.Text));
                        if (DocumentValidationRules.HasRejectTextMatch(baseText, rejectTexts))
                            rejectedPages.Add(p);
                        if (rejectedPages.Contains(p))
                            break;
                    }

                    if (groupsReject.Count > 0 &&
                        TryEvaluateGroups(groupsReject, tokens, tokenPatterns, rawTokens, rawPatterns, options.MinScore, options.MaxPairs, options.MaxCandidates, options.Log, options.UseRaw, out _, out _, out _, out _))
                    {
                        rejectedPages.Add(p);
                        break;
                    }

                    if (TryEvaluateGroups(groupsAll, tokens, tokenPatterns, rawTokens, rawPatterns, options.MinScore, options.MaxPairs, options.MaxCandidates, options.Log, options.UseRaw, out var scoreAll, out var avgAll, out var covAll, out var hitsAll))
                    {
                        hits.Add(new ScanHit
                        {
                            Page = p,
                            Obj = objId,
                            Score = scoreAll,
                            Hits = hitsAll,
                            Avg = avgAll,
                            Coverage = covAll
                        });
                    }

                    if (options.Pair == 2)
                    {
                        if (TryEvaluateGroups(groupsP1, tokens, tokenPatterns, rawTokens, rawPatterns, options.MinScore, options.MaxPairs, options.MaxCandidates, options.Log, options.UseRaw, out var scoreP1, out var avgP1, out var covP1, out var hitsP1))
                        {
                            var gh = new GroupedHit { Page = p, Obj = objId, Score = scoreP1, Hits = hitsP1, Avg = avgP1, Coverage = covP1 };
                            if (!bestByPageP1.TryGetValue(p, out var best) || gh.Score > best.Score)
                                bestByPageP1[p] = gh;
                            hitsP1List.Add(new ScanHit
                            {
                                Page = p,
                                Obj = objId,
                                Score = scoreP1,
                                Hits = hitsP1,
                                Avg = avgP1,
                                Coverage = covP1
                            });
                        }
                        if (TryEvaluateGroups(groupsP2, tokens, tokenPatterns, rawTokens, rawPatterns, options.MinScore, options.MaxPairs, options.MaxCandidates, options.Log, options.UseRaw, out var scoreP2, out var avgP2, out var covP2, out var hitsP2))
                        {
                            var gh = new GroupedHit { Page = p, Obj = objId, Score = scoreP2, Hits = hitsP2, Avg = avgP2, Coverage = covP2 };
                            if (!bestByPageP2.TryGetValue(p, out var best) || gh.Score > best.Score)
                                bestByPageP2[p] = gh;
                            hitsP2List.Add(new ScanHit
                            {
                                Page = p,
                                Obj = objId,
                                Score = scoreP2,
                                Hits = hitsP2,
                                Avg = avgP2,
                                Coverage = covP2
                            });
                        }
                    }
                }
            }

            if (hits.Count == 0)
            {
                Console.WriteLine("Nenhum match encontrado.");
                return;
            }

            var top = hits.OrderByDescending(h => h.Score).ThenBy(h => h.Page).ThenBy(h => h.Obj).Take(Math.Max(1, options.Top)).ToList();

            Console.WriteLine($"PDF: {Path.GetFileName(options.Input)}");

            var topPairs = new List<(int P1, int P2, GroupedHit A, GroupedHit B, double Score)>();
            var topP1 = new List<ScanHit>();
            var topP2 = new List<ScanHit>();
            if (options.Pair == 2)
            {
                var pairHits = new List<(int P1, int P2, GroupedHit A, GroupedHit B, double Score)>();
                foreach (var kv in bestByPageP1)
                {
                    var p1 = kv.Key;
                    var p2 = p1 + 1;
                    if (!bestByPageP2.ContainsKey(p2))
                        continue;
                    if (rejectedPages.Contains(p1) || rejectedPages.Contains(p2))
                        continue;
                    var a = bestByPageP1[p1];
                    var b = bestByPageP2[p2];
                    var score = (a.Score + b.Score) / 2.0;
                    pairHits.Add((p1, p2, a, b, score));
                }

                topPairs = pairHits.OrderByDescending(p => p.Score).Take(Math.Max(1, options.Top)).ToList();
                Console.WriteLine("TOP PARES CONTIGUOS (page1_fields/page2_fields)");
                foreach (var pair in topPairs)
                {
                    Console.WriteLine($"  p{pair.P1}/p{pair.P2} score={pair.Score:F2}");
                    Console.WriteLine($"    p{pair.P1} obj={pair.A.Obj} score={pair.A.Score:F2} hits={pair.A.Hits}");
                    Console.WriteLine($"    p{pair.P2} obj={pair.B.Obj} score={pair.B.Score:F2} hits={pair.B.Hits}");
                }
            }

            if (options.Pair == 2)
            {
                topP1 = hitsP1List.OrderByDescending(h => h.Score).ThenBy(h => h.Page).ThenBy(h => h.Obj).Take(Math.Max(1, options.Top)).ToList();
                topP2 = hitsP2List.OrderByDescending(h => h.Score).ThenBy(h => h.Page).ThenBy(h => h.Obj).Take(Math.Max(1, options.Top)).ToList();

                Console.WriteLine("TOP OBJETOS (page1_fields)");
                foreach (var h in topP1)
                {
                    if (rejectedPages.Contains(h.Page))
                        continue;
                    Console.WriteLine($"  p{h.Page} obj={h.Obj} score={h.Score:F2} avg={h.Avg:F2} cov={h.Coverage:F2} hits={h.Hits}");
                }

                Console.WriteLine("TOP OBJETOS (page2_fields)");
                foreach (var h in topP2)
                {
                    if (rejectedPages.Contains(h.Page))
                        continue;
                    Console.WriteLine($"  p{h.Page} obj={h.Obj} score={h.Score:F2} avg={h.Avg:F2} cov={h.Coverage:F2} hits={h.Hits}");
                }
            }
            else
            {
                Console.WriteLine("TOP OBJETOS (padrao)");
                foreach (var h in top)
                {
                    if (rejectedPages.Contains(h.Page))
                        continue;
                    Console.WriteLine($"  p{h.Page} obj={h.Obj} score={h.Score:F2} avg={h.Avg:F2} cov={h.Coverage:F2} hits={h.Hits}");
                }
            }

            if (options.Explain)
            {
                foreach (var h in top)
                {
                    Console.WriteLine($"DETAIL p{h.Page} obj={h.Obj} score={h.Score:F2}");
                    var pageObj = doc.GetPage(h.Page);
                    var pageHeight = pageObj.GetPageSize().GetHeight();
                    var pageWidth = pageObj.GetPageSize().GetWidth();
                    var found = FindStreamAndResourcesByObjId(doc, h.Obj);
                    if (found.Stream == null)
                    {
                        Console.WriteLine("  (stream nao encontrado)");
                        continue;
                    }
                    var resources = found.Resources ?? pageObj.GetResources() ?? new PdfResources(new PdfDictionary());
                    var tokens = ExtractTokensFromStream(found.Stream, resources, pageHeight, pageWidth, options.OpFilter);
                    var rawTokens = ExtractRawTokensFromStream(found.Stream, resources, pageHeight, pageWidth, options.OpFilter);
                    var tokenPatterns = tokens.Select(t => t.Pattern).ToList();
                    var rawPatterns = rawTokens.Select(t => t.Pattern).ToList();

                    var miss = new List<string>();
                    var met = new List<(string Field, FieldMatch Match)>();
                    foreach (var group in groupsAll)
                    {
                        FieldMatch? best = null;
                        foreach (var entry in group)
                        {
                            var match = BestFieldMatch(tokens, tokenPatterns, rawTokens, rawPatterns, entry, options.MinScore, options.MaxPairs, options.Log, options.UseRaw);
                            if (match != null && (best == null || match.Score > best.Score))
                                best = match;
                        }
                        if (best == null)
                            miss.Add(group.Key);
                        else
                            met.Add((group.Key, best));
                    }

                    if (options.Clean)
                    {
                        if (miss.Count > 0)
                        {
                            var missLabel = Colorize("MISS", "31");
                            Console.WriteLine($"  {missLabel}:");
                            foreach (var f in miss)
                                Console.WriteLine($"    - {f}");
                        }
                        if (met.Count > 0)
                        {
                            var metLabel = Colorize("MET", "32");
                            Console.WriteLine($"  {metLabel}:");
                            foreach (var item in met)
                            {
                                var best = item.Match;
                                var fallback = (best.PrevMode.Contains("text-only", StringComparison.OrdinalIgnoreCase) ||
                                                best.NextMode.Contains("text-only", StringComparison.OrdinalIgnoreCase))
                                    ? Colorize("[TEXT_FALLBACK]", "33")
                                    : "";
                                Console.WriteLine($"    - {item.Field}: MET[{best.Kind}] {fallback} score={best.Score:F2} (p={best.PrevScore:F2} n={best.NextScore:F2} v={best.ValueScore:F2})");
                                Console.WriteLine($"      value=\"{best.ValueText}\"");
                            }
                        }
                    }
                    else
                    {
                        foreach (var item in miss)
                        {
                            var missLabel = Colorize("MISS", "31");
                            Console.WriteLine($"  {item}: {missLabel}");
                        }
                        foreach (var item in met)
                        {
                            var best = item.Match;
                            var metLabel = Colorize($"MET[{best.Kind}]", "32");
                            var fallback = (best.PrevMode.Contains("text-only", StringComparison.OrdinalIgnoreCase) ||
                                            best.NextMode.Contains("text-only", StringComparison.OrdinalIgnoreCase))
                                ? Colorize("[TEXT_FALLBACK]", "33")
                                : "";
                            Console.WriteLine($"  {item.Field}: {metLabel} {fallback} score={best.Score:F2} (p={best.PrevScore:F2} n={best.NextScore:F2} v={best.ValueScore:F2})");
                            Console.WriteLine($"    prev({best.PrevMode})=\"{best.PrevText}\"");
                            Console.WriteLine($"    next({best.NextMode})=\"{best.NextText}\"");
                            Console.WriteLine($"    value=\"{best.ValueText}\"");
                        }
                    }
                }
            }

            if (options.Json || !string.IsNullOrWhiteSpace(options.OutPath))
            {
                var metrics = new Dictionary<(int Page, int Obj), ScanHit>();
                void AddMetrics(IEnumerable<ScanHit> list)
                {
                    foreach (var h in list)
                    {
                        var key = (h.Page, h.Obj);
                        if (!metrics.TryGetValue(key, out var existing) || h.Score > existing.Score)
                            metrics[key] = h;
                    }
                }
                AddMetrics(hits);
                AddMetrics(hitsP1List);
                AddMetrics(hitsP2List);

                var detailKeys = new HashSet<(int Page, int Obj)>();
                foreach (var h in top)
                    detailKeys.Add((h.Page, h.Obj));
                foreach (var h in topP1)
                    detailKeys.Add((h.Page, h.Obj));
                foreach (var h in topP2)
                    detailKeys.Add((h.Page, h.Obj));
                foreach (var p in topPairs)
                {
                    detailKeys.Add((p.P1, p.A.Obj));
                    detailKeys.Add((p.P2, p.B.Obj));
                }

                var details = new List<object>();
                if (!options.Clean)
                {
                    foreach (var key in detailKeys)
                    {
                    var pageObj = doc.GetPage(key.Page);
                    var pageHeight = pageObj.GetPageSize().GetHeight();
                    var pageWidth = pageObj.GetPageSize().GetWidth();
                    var found = FindStreamAndResourcesByObjId(doc, key.Obj);
                    if (found.Stream == null)
                        continue;

                    var resources = found.Resources ?? pageObj.GetResources() ?? new PdfResources(new PdfDictionary());
                    var tokens = ExtractTokensFromStream(found.Stream, resources, pageHeight, pageWidth, options.OpFilter);
                    var rawTokens = ExtractRawTokensFromStream(found.Stream, resources, pageHeight, pageWidth, options.OpFilter);
                    var tokenPatterns = tokens.Select(t => t.Pattern).ToList();
                    var rawPatterns = rawTokens.Select(t => t.Pattern).ToList();

                    var fieldDetails = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (var group in groupsAll)
                    {
                        var matchItems = new List<(FieldMatch Match, string Kind)>();
                        var typedRejects = new List<FieldReject>();
                        var rawRejects = new List<FieldReject>();
                        foreach (var entry in group)
                        {
                            var typedMatches = new List<FieldMatch>();
                            var rawMatches = new List<FieldMatch>();

                            if (options.UseRaw && HasRawPatterns(entry) && rawTokens.Count > 0)
                                rawMatches = FindMatchesWithRoi(rawTokens, rawPatterns, entry, options.MinScore, entry.Field ?? "", useRaw: true, textOnly: false, maxPairs: options.MaxPairs, maxCandidates: options.MaxCandidates, log: options.Log, rejects: rawRejects);

                            if (HasTypedPatterns(entry) && tokens.Count > 0)
                                typedMatches = FindMatchesWithRoi(tokens, tokenPatterns, entry, options.MinScore, entry.Field ?? "", useRaw: false, textOnly: false, maxPairs: options.MaxPairs, maxCandidates: options.MaxCandidates, log: options.Log, rejects: typedRejects);

                            var all = rawMatches.Concat(typedMatches).ToList();
                            if (all.Count == 0 && HasAnchorText(entry) && tokens.Count > 0)
                                all = FindMatchesWithRoi(tokens, tokenPatterns, entry, options.MinScore, entry.Field ?? "", useRaw: false, textOnly: true, maxPairs: options.MaxPairs, maxCandidates: options.MaxCandidates, log: options.Log);

                            foreach (var m in all)
                                matchItems.Add((m, m.Kind));
                        }

                        var allMatches = matchItems.Select(item => new
                        {
                            kind = item.Kind,
                            score = item.Match.Score,
                            prev_score = item.Match.PrevScore,
                            next_score = item.Match.NextScore,
                            value_score = item.Match.ValueScore,
                            prev_mode = item.Match.PrevMode,
                            next_mode = item.Match.NextMode,
                            prev_text = item.Match.PrevText,
                            next_text = item.Match.NextText,
                            prev_expected_pattern = item.Match.PrevExpectedPattern,
                            prev_actual_pattern = item.Match.PrevActualPattern,
                            prev_pattern_score = item.Match.PrevPatternScore,
                            prev_expected_text = item.Match.PrevExpectedText,
                            prev_actual_text = item.Match.PrevActualText,
                            prev_text_score = item.Match.PrevTextScore,
                            next_expected_pattern = item.Match.NextExpectedPattern,
                            next_actual_pattern = item.Match.NextActualPattern,
                            next_pattern_score = item.Match.NextPatternScore,
                            next_expected_text = item.Match.NextExpectedText,
                            next_actual_text = item.Match.NextActualText,
                            next_text_score = item.Match.NextTextScore,
                            value_text = item.Match.ValueText,
                            value_full = item.Match.ValueText,
                            value_pattern = item.Match.ValuePattern,
                            value_expected_pattern = item.Match.ValueExpectedPattern,
                            value_actual_pattern = item.Match.ValuePattern,
                            start_op = item.Match.StartOp,
                            end_op = item.Match.EndOp,
                            op_range = $"op{item.Match.StartOp}-op{item.Match.EndOp}",
                            band = item.Match.Band,
                            y_range = item.Match.YRange,
                            x_range = item.Match.XRange
                        }).ToList();

                        var allRejects = rawRejects
                            .Concat(typedRejects)
                            .OrderByDescending(r => r.Score)
                            .Take(5)
                            .Select(r => new
                            {
                                kind = r.Kind,
                                reason = r.Reason,
                                score = r.Score,
                                prev_score = r.PrevScore,
                                next_score = r.NextScore,
                                value_score = r.ValueScore,
                                prev_mode = r.PrevMode,
                                next_mode = r.NextMode,
                                prev_text = r.PrevText,
                                next_text = r.NextText,
                                prev_expected_pattern = r.PrevExpectedPattern,
                                prev_actual_pattern = r.PrevActualPattern,
                                prev_pattern_score = r.PrevPatternScore,
                                prev_expected_text = r.PrevExpectedText,
                                prev_actual_text = r.PrevActualText,
                                prev_text_score = r.PrevTextScore,
                                next_expected_pattern = r.NextExpectedPattern,
                                next_actual_pattern = r.NextActualPattern,
                                next_pattern_score = r.NextPatternScore,
                                next_expected_text = r.NextExpectedText,
                                next_actual_text = r.NextActualText,
                                next_text_score = r.NextTextScore,
                                value_text = r.ValueText,
                                value_pattern = r.ValuePattern,
                                value_expected_pattern = r.ValueExpectedPattern,
                                value_actual_pattern = r.ValuePattern,
                                start_op = r.StartOp,
                                end_op = r.EndOp,
                                op_range = r.StartOp > 0 || r.EndOp > 0 ? $"op{r.StartOp}-op{r.EndOp}" : "",
                                band = r.Band,
                                y_range = r.YRange,
                                x_range = r.XRange
                            }).ToList();

                        var bestItem = matchItems.OrderByDescending(m => m.Match.Score).FirstOrDefault();
                        object? best = null;
                        if (bestItem.Match != null)
                        {
                            best = new
                            {
                                kind = bestItem.Kind,
                                score = bestItem.Match.Score,
                                prev_score = bestItem.Match.PrevScore,
                                next_score = bestItem.Match.NextScore,
                                value_score = bestItem.Match.ValueScore,
                                prev_mode = bestItem.Match.PrevMode,
                                next_mode = bestItem.Match.NextMode,
                                prev_text = bestItem.Match.PrevText,
                                next_text = bestItem.Match.NextText,
                                prev_expected_pattern = bestItem.Match.PrevExpectedPattern,
                                prev_actual_pattern = bestItem.Match.PrevActualPattern,
                                prev_pattern_score = bestItem.Match.PrevPatternScore,
                                prev_expected_text = bestItem.Match.PrevExpectedText,
                                prev_actual_text = bestItem.Match.PrevActualText,
                                prev_text_score = bestItem.Match.PrevTextScore,
                                next_expected_pattern = bestItem.Match.NextExpectedPattern,
                                next_actual_pattern = bestItem.Match.NextActualPattern,
                                next_pattern_score = bestItem.Match.NextPatternScore,
                                next_expected_text = bestItem.Match.NextExpectedText,
                                next_actual_text = bestItem.Match.NextActualText,
                                next_text_score = bestItem.Match.NextTextScore,
                                value_text = bestItem.Match.ValueText,
                                value_full = bestItem.Match.ValueText,
                                value_pattern = bestItem.Match.ValuePattern,
                                value_expected_pattern = bestItem.Match.ValueExpectedPattern,
                                value_actual_pattern = bestItem.Match.ValuePattern,
                                start_op = bestItem.Match.StartOp,
                                end_op = bestItem.Match.EndOp,
                                op_range = $"op{bestItem.Match.StartOp}-op{bestItem.Match.EndOp}",
                                band = bestItem.Match.Band,
                                y_range = bestItem.Match.YRange,
                                x_range = bestItem.Match.XRange
                            };
                        }

                        fieldDetails[group.Key] = new
                        {
                            hits = allMatches.Count,
                            best,
                            matches = allMatches,
                            rejected = allRejects
                        };
                    }

                    metrics.TryGetValue(key, out var metric);
                        details.Add(new
                        {
                            page = key.Page,
                            obj = key.Obj,
                            score = metric?.Score ?? 0.0,
                            avg = metric?.Avg ?? 0.0,
                            coverage = metric?.Coverage ?? 0.0,
                            hits = metric?.Hits ?? 0,
                            fields = fieldDetails
                        });
                    }
                }

                var payload = new
                {
                    pdf = Path.GetFileName(options.Input),
                    input = options.Input,
                    min_score = options.MinScore,
                    pair = options.Pair,
                    top = options.Top,
                    page1_fields = page1Fields.ToArray(),
                    page2_fields = page2Fields.ToArray(),
                    rejected_pages = rejectedPages.OrderBy(p => p).ToArray(),
                    top_pairs = topPairs.Select(p => new
                    {
                        page1 = p.P1,
                        page2 = p.P2,
                        score = p.Score,
                        page1_obj = p.A.Obj,
                        page2_obj = p.B.Obj,
                        page1_score = p.A.Score,
                        page2_score = p.B.Score,
                        page1_hits = p.A.Hits,
                        page2_hits = p.B.Hits
                    }).ToList(),
                    top_objects = new
                    {
                        all = top.Select(h => new { page = h.Page, obj = h.Obj, score = h.Score, avg = h.Avg, coverage = h.Coverage, hits = h.Hits }).ToList(),
                        page1 = topP1.Select(h => new { page = h.Page, obj = h.Obj, score = h.Score, avg = h.Avg, coverage = h.Coverage, hits = h.Hits }).ToList(),
                        page2 = topP2.Select(h => new { page = h.Page, obj = h.Obj, score = h.Score, avg = h.Avg, coverage = h.Coverage, hits = h.Hits }).ToList()
                    },
                    details
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

                if (options.Json)
                    Console.WriteLine(json);
                if (!string.IsNullOrWhiteSpace(options.OutPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(options.OutPath) ?? ".");
                    File.WriteAllText(options.OutPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    Console.WriteLine("Arquivo salvo: " + options.OutPath);
                }
            }
        }

        internal sealed class PatternPairPick
        {
            public int Page1 { get; set; }
            public int Obj1 { get; set; }
            public double Score1 { get; set; }
            public int Page2 { get; set; }
            public int Obj2 { get; set; }
            public double Score2 { get; set; }
            public double Score { get; set; }
        }

        internal static bool TryFindBestPairByPatterns(string patternsDoc, string pdfPath, out PatternPairPick pick)
        {
            pick = new PatternPairPick();
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                return false;

            var defaults = LoadPatternFindDefaults();
            var patternsPath = !string.IsNullOrWhiteSpace(patternsDoc) ? ResolvePatternPath(patternsDoc) : "";
            if (string.IsNullOrWhiteSpace(patternsPath) && !string.IsNullOrWhiteSpace(defaults.Patterns))
                patternsPath = ResolvePatternPath(defaults.Patterns);
            if (string.IsNullOrWhiteSpace(patternsPath) || !File.Exists(patternsPath))
                return false;

            var minScore = defaults.MinScore ?? 0.0;
            var maxPairs = defaults.MaxPairs ?? 200000;
            var maxCandidates = defaults.MaxCandidates ?? 50;
            var useRaw = defaults.UseRaw ?? false;

            var opFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            opFilter.Add("Tj");
            opFilter.Add("TJ");

            var entries = LoadPatternEntries(patternsPath, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (entries.Count == 0)
                return false;
            EnsureRegexCatalog(patternsPath, log: false);

            var docName = ReadDocNameFromPatterns(patternsPath);
            var optionalFields = ReadFieldListFromPatterns(patternsPath, "optional_fields", "optionalFields", "optional");
            var requiredAll = BuildRequiredFieldSet(entries, new HashSet<string>(StringComparer.OrdinalIgnoreCase), optionalFields);
            var groupsAll = BuildGroups(entries, requiredAll);
            var rejectFields = ReadFieldListFromPatterns(patternsPath, "reject_fields", "rejectFields", "reject");
            var groupsReject = rejectFields.Count > 0 ? BuildGroups(entries, rejectFields) : new List<IGrouping<string, FieldPatternEntry>>();
            var rejectTexts = ReadFieldListFromPatterns(patternsPath, "reject_texts", "rejectTexts", "reject_text", "rejectText");
            DocumentValidationRules.EnsureDespachoRejectTexts(rejectTexts, docName);

            var page1Fields = ReadFieldListFromPatterns(patternsPath, "page1_fields", "page1Fields", "p1_fields", "page1");
            var page2Fields = ReadFieldListFromPatterns(patternsPath, "page2_fields", "page2Fields", "p2_fields", "page2");
            var page1Required = page1Fields.Count > 0 ? RemoveOptional(page1Fields, optionalFields) : requiredAll;
            var page2Required = page2Fields.Count > 0 ? RemoveOptional(page2Fields, optionalFields) : requiredAll;
            var groupsP1 = page1Required.Count > 0 ? BuildGroups(entries, page1Required) : groupsAll;
            var groupsP2 = page2Required.Count > 0 ? BuildGroups(entries, page2Required) : groupsAll;

            using var reader = new PdfReader(pdfPath);
            using var doc = new PdfDocument(reader);

            var bestByPageP1 = new Dictionary<int, GroupedHit>();
            var bestByPageP2 = new Dictionary<int, GroupedHit>();
            var rejectedPages = new HashSet<int>();

            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                var pageHeight = page.GetPageSize().GetHeight();
                var pageWidth = page.GetPageSize().GetWidth();

                foreach (var stream in EnumerateStreams(contents))
                {
                    if (rejectedPages.Contains(p))
                        break;
                    int objId = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                    if (objId <= 0)
                        continue;

                    var tokens = ExtractTokensFromStream(stream, resources, pageHeight, pageWidth, opFilter);
                    var rawTokens = ExtractRawTokensFromStream(stream, resources, pageHeight, pageWidth, opFilter);
                    if (tokens.Count == 0 && rawTokens.Count == 0)
                        continue;

                    if (rejectTexts.Count > 0)
                    {
                        var baseText = tokens.Count > 0
                            ? string.Join(" ", tokens.Select(t => t.Text))
                            : string.Join(" ", rawTokens.Select(t => t.Text));
                        if (DocumentValidationRules.HasRejectTextMatch(baseText, rejectTexts))
                        {
                            rejectedPages.Add(p);
                            break;
                        }
                    }

                    var tokenPatterns = tokens.Select(t => t.Pattern).ToList();
                    var rawPatterns = rawTokens.Select(t => t.Pattern).ToList();

                    if (groupsReject.Count > 0 &&
                        TryEvaluateGroups(groupsReject, tokens, tokenPatterns, rawTokens, rawPatterns, minScore, maxPairs, maxCandidates, log: false, useRaw, out _, out _, out _, out _))
                    {
                        rejectedPages.Add(p);
                        break;
                    }

                    if (TryEvaluateGroups(groupsP1, tokens, tokenPatterns, rawTokens, rawPatterns, minScore, maxPairs, maxCandidates, log: false, useRaw, out var scoreP1, out var avgP1, out var covP1, out var hitsP1))
                    {
                        var gh = new GroupedHit { Page = p, Obj = objId, Score = scoreP1, Hits = hitsP1, Avg = avgP1, Coverage = covP1 };
                        if (!bestByPageP1.TryGetValue(p, out var best) || gh.Score > best.Score)
                            bestByPageP1[p] = gh;
                    }
                    if (TryEvaluateGroups(groupsP2, tokens, tokenPatterns, rawTokens, rawPatterns, minScore, maxPairs, maxCandidates, log: false, useRaw, out var scoreP2, out var avgP2, out var covP2, out var hitsP2))
                    {
                        var gh = new GroupedHit { Page = p, Obj = objId, Score = scoreP2, Hits = hitsP2, Avg = avgP2, Coverage = covP2 };
                        if (!bestByPageP2.TryGetValue(p, out var best) || gh.Score > best.Score)
                            bestByPageP2[p] = gh;
                    }
                }
            }

            double bestScore = 0.0;
            PatternPairPick? bestPick = null;
            foreach (var kv in bestByPageP1)
            {
                var p1 = kv.Key;
                var p2 = p1 + 1;
                if (!bestByPageP2.TryGetValue(p2, out var b))
                    continue;
                if (rejectedPages.Contains(p1) || rejectedPages.Contains(p2))
                    continue;
                var a = kv.Value;
                var score = (a.Score + b.Score) / 2.0;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPick = new PatternPairPick
                    {
                        Page1 = p1,
                        Obj1 = a.Obj,
                        Score1 = a.Score,
                        Page2 = p2,
                        Obj2 = b.Obj,
                        Score2 = b.Score,
                        Score = score
                    };
                }
            }

            if (bestPick == null || bestPick.Page1 <= 0 || bestPick.Obj1 <= 0)
                return false;

            pick = bestPick;
            return true;
        }

        private static FindOptions CloneFindOptions(FindOptions options)
        {
            var clone = new FindOptions
            {
                PatternsPath = options.PatternsPath,
                Input = options.Input,
                InputDir = options.InputDir,
                MinScore = options.MinScore,
                Top = options.Top,
                Pair = options.Pair,
                Explain = options.Explain,
                Json = options.Json,
                OutPath = options.OutPath,
                Clean = options.Clean,
                Log = options.Log,
                MaxPairs = options.MaxPairs,
                MaxCandidates = options.MaxCandidates,
                UseRaw = options.UseRaw,
                Limit = options.Limit
            };
            foreach (var f in options.Fields)
                clone.Fields.Add(f);
            foreach (var f in options.Page1Fields)
                clone.Page1Fields.Add(f);
            foreach (var f in options.Page2Fields)
                clone.Page2Fields.Add(f);
            foreach (var f in options.OpFilter)
                clone.OpFilter.Add(f);
            return clone;
        }

        private static string BuildFindOutPath(string outPath, string inputFile, bool multiple)
        {
            if (!multiple)
                return outPath;

            var fileStem = Path.GetFileNameWithoutExtension(inputFile);
            if (string.IsNullOrWhiteSpace(fileStem))
                fileStem = "find";

            if (Directory.Exists(outPath) || outPath.EndsWith(Path.DirectorySeparatorChar))
                return Path.Combine(outPath, fileStem + ".json");

            var dir = Path.GetDirectoryName(outPath);
            if (string.IsNullOrWhiteSpace(dir))
                dir = ".";
            return Path.Combine(dir, fileStem + ".json");
        }

        private static List<IGrouping<string, FieldPatternEntry>> BuildGroups(List<FieldPatternEntry> entries, HashSet<string> fieldsFilter)
        {
            var filtered = fieldsFilter.Count > 0
                ? entries.Where(e => !string.IsNullOrWhiteSpace(e.Field) && fieldsFilter.Contains(e.Field)).ToList()
                : entries;

            return filtered
                .Where(e => !string.IsNullOrWhiteSpace(e.Field))
                .GroupBy(e => e.Field!, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static HashSet<string> BuildRequiredFieldSet(
            List<FieldPatternEntry> entries,
            HashSet<string> requested,
            HashSet<string> optional)
        {
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (requested.Count > 0)
            {
                foreach (var f in requested)
                {
                    if (optional.Contains(f))
                        continue;
                    required.Add(f);
                }
            }
            else
            {
                foreach (var e in entries)
                {
                    if (string.IsNullOrWhiteSpace(e.Field))
                        continue;
                    if (optional.Contains(e.Field))
                        continue;
                    required.Add(e.Field);
                }
            }
            return required;
        }

        private static HashSet<string> RemoveOptional(HashSet<string> fields, HashSet<string> optional)
        {
            if (fields.Count == 0 || optional.Count == 0)
                return fields;
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in fields)
            {
                if (optional.Contains(f))
                    continue;
                required.Add(f);
            }
            return required;
        }

        private static bool TryEvaluateGroups(
            List<IGrouping<string, FieldPatternEntry>> groups,
            List<TokenInfo> tokens,
            List<string> tokenPatterns,
            List<TokenInfo> rawTokens,
            List<string> rawPatterns,
            double minScore,
            int maxPairs,
            int maxCandidates,
            bool log,
            bool useRaw,
            out double score,
            out double avg,
            out double coverage,
            out int hits)
        {
            score = 0;
            avg = 0;
            coverage = 0;
            hits = 0;
            if (groups.Count == 0)
                return false;

            var fieldScores = new List<double>();
            foreach (var group in groups)
            {
                double best = 0;
                bool found = false;
                foreach (var entry in group)
                {
                    var s = BestFieldScore(tokens, tokenPatterns, rawTokens, rawPatterns, entry, minScore, maxPairs, maxCandidates, log, useRaw);
                    if (s.HasValue && s.Value > best)
                    {
                        best = s.Value;
                        found = true;
                    }
                }
                if (found)
                    fieldScores.Add(best);
            }

            if (fieldScores.Count == 0)
                return false;

            avg = fieldScores.Average();
            coverage = (double)fieldScores.Count / Math.Max(1, groups.Count);
            score = avg * coverage;
            hits = fieldScores.Count;
            return true;
        }

        private static double? BestFieldScore(
            List<TokenInfo> tokens,
            List<string> tokenPatterns,
            List<TokenInfo> rawTokens,
            List<string> rawPatterns,
            FieldPatternEntry entry,
            double minScore,
            int maxPairs,
            int maxCandidates,
            bool log,
            bool useRaw)
        {
            var limit = maxCandidates > 0 ? maxCandidates : 10;
            var matches = FindMatchesOrdered(tokens, tokenPatterns, rawTokens, rawPatterns, entry, minScore, entry.Field ?? "", maxPairs, limit, HasAdiantamento(tokens, rawTokens), log, useRaw, null);
            var best = matches.OrderByDescending(m => m.Score).FirstOrDefault();
            return best?.Score;
        }

        private static FieldMatch? BestFieldMatch(
            List<TokenInfo> tokens,
            List<string> tokenPatterns,
            List<TokenInfo> rawTokens,
            List<string> rawPatterns,
            FieldPatternEntry entry,
            double minScore,
            int maxPairs,
            bool log,
            bool useRaw)
        {
            var matches = FindMatchesOrdered(tokens, tokenPatterns, rawTokens, rawPatterns, entry, minScore, entry.Field ?? "", maxPairs, 0, HasAdiantamento(tokens, rawTokens), log, useRaw, null);
            return matches.OrderByDescending(m => m.Score).FirstOrDefault();
        }

        private static FieldMatch? BestCombinedMatch(List<FieldMatch> typedMatches, List<FieldMatch> rawMatches)
        {
            if ((typedMatches == null || typedMatches.Count == 0) && (rawMatches == null || rawMatches.Count == 0))
                return null;

            var candidates = new List<FieldMatch>();
            if (typedMatches != null)
                candidates.AddRange(typedMatches);
            if (rawMatches != null)
                candidates.AddRange(rawMatches);

            if (typedMatches != null && rawMatches != null && typedMatches.Count > 0 && rawMatches.Count > 0)
            {
                foreach (var t in typedMatches)
                {
                    foreach (var r in rawMatches)
                    {
                        if (!OpRangesOverlap(t.StartOp, t.EndOp, r.StartOp, r.EndOp))
                            continue;
                        var combined = CombineRawTypedMatch(t, r);
                        candidates.Add(combined);
                    }
                }
            }

            return candidates.OrderByDescending(m => m.Score).FirstOrDefault();
        }

        private static bool OpRangesOverlap(int aStart, int aEnd, int bStart, int bEnd)
        {
            return Math.Max(aStart, bStart) <= Math.Min(aEnd, bEnd);
        }

        private const double RawWeight = 0.45;
        private const double TypedWeight = 0.55;
        private const double DmpWeight = 0.7;
        private const double RegexWeight = 0.3;

        private static double CombineWeighted(double typed, double raw)
        {
            var w = TypedWeight + RawWeight;
            if (w <= 0) return 0;
            return (typed * TypedWeight + raw * RawWeight) / w;
        }

        private static double CombineTwo(double a, double b, double wa, double wb)
        {
            var w = wa + wb;
            if (w <= 0) return 0;
            return (a * wa + b * wb) / w;
        }

        private static FieldMatch CombineRawTypedMatch(FieldMatch typed, FieldMatch raw)
        {
            var combined = new FieldMatch
            {
                Field = typed.Field,
                Pdf = typed.Pdf,
                ValueText = !string.IsNullOrWhiteSpace(typed.ValueText) ? typed.ValueText : raw.ValueText,
                ValuePattern = !string.IsNullOrWhiteSpace(typed.ValuePattern) ? typed.ValuePattern : raw.ValuePattern,
                ValueExpectedPattern = !string.IsNullOrWhiteSpace(typed.ValueExpectedPattern) ? typed.ValueExpectedPattern : raw.ValueExpectedPattern,
                Score = CombineWeighted(typed.Score, raw.Score),
                PrevScore = CombineWeighted(typed.PrevScore, raw.PrevScore),
                NextScore = CombineWeighted(typed.NextScore, raw.NextScore),
                ValueScore = CombineWeighted(typed.ValueScore, raw.ValueScore),
                PrevText = !string.IsNullOrWhiteSpace(typed.PrevText) ? typed.PrevText : raw.PrevText,
                NextText = !string.IsNullOrWhiteSpace(typed.NextText) ? typed.NextText : raw.NextText,
                PrevExpectedPattern = !string.IsNullOrWhiteSpace(typed.PrevExpectedPattern) ? typed.PrevExpectedPattern : raw.PrevExpectedPattern,
                PrevActualPattern = !string.IsNullOrWhiteSpace(typed.PrevActualPattern) ? typed.PrevActualPattern : raw.PrevActualPattern,
                PrevPatternScore = typed.PrevPatternScore > 0 ? typed.PrevPatternScore : raw.PrevPatternScore,
                PrevExpectedText = !string.IsNullOrWhiteSpace(typed.PrevExpectedText) ? typed.PrevExpectedText : raw.PrevExpectedText,
                PrevActualText = !string.IsNullOrWhiteSpace(typed.PrevActualText) ? typed.PrevActualText : raw.PrevActualText,
                PrevTextScore = typed.PrevTextScore > 0 ? typed.PrevTextScore : raw.PrevTextScore,
                NextExpectedPattern = !string.IsNullOrWhiteSpace(typed.NextExpectedPattern) ? typed.NextExpectedPattern : raw.NextExpectedPattern,
                NextActualPattern = !string.IsNullOrWhiteSpace(typed.NextActualPattern) ? typed.NextActualPattern : raw.NextActualPattern,
                NextPatternScore = typed.NextPatternScore > 0 ? typed.NextPatternScore : raw.NextPatternScore,
                NextExpectedText = !string.IsNullOrWhiteSpace(typed.NextExpectedText) ? typed.NextExpectedText : raw.NextExpectedText,
                NextActualText = !string.IsNullOrWhiteSpace(typed.NextActualText) ? typed.NextActualText : raw.NextActualText,
                NextTextScore = typed.NextTextScore > 0 ? typed.NextTextScore : raw.NextTextScore,
                StartOp = Math.Min(typed.StartOp, raw.StartOp),
                EndOp = Math.Max(typed.EndOp, raw.EndOp),
                Band = !string.IsNullOrWhiteSpace(typed.Band) ? typed.Band : raw.Band,
                YRange = !string.IsNullOrWhiteSpace(typed.YRange) ? typed.YRange : raw.YRange,
                XRange = !string.IsNullOrWhiteSpace(typed.XRange) ? typed.XRange : raw.XRange,
                Kind = "both",
                PrevMode = string.Join("|", new[] { typed.PrevMode, raw.PrevMode }.Where(s => !string.IsNullOrWhiteSpace(s))),
                NextMode = string.Join("|", new[] { typed.NextMode, raw.NextMode }.Where(s => !string.IsNullOrWhiteSpace(s)))
            };
            return combined;
        }

        private static List<FieldMatch> MergeDmpAndRegex(List<FieldMatch> dmp, List<FieldMatch> regex)
        {
            if (dmp.Count == 0)
                return regex;
            if (regex.Count == 0)
                return dmp;

            var merged = new List<FieldMatch>();
            var usedRegex = new HashSet<int>();

            for (int i = 0; i < dmp.Count; i++)
            {
                var d = dmp[i];
                FieldMatch? bestRx = null;
                var bestIdx = -1;
                for (int j = 0; j < regex.Count; j++)
                {
                    if (usedRegex.Contains(j))
                        continue;
                    var r = regex[j];
                    if (!OpRangesOverlap(d.StartOp, d.EndOp, r.StartOp, r.EndOp))
                        continue;
                    if (bestRx == null || r.Score > bestRx.Score)
                    {
                        bestRx = r;
                        bestIdx = j;
                    }
                }

                if (bestRx != null)
                {
                    var boosted = CloneMatch(d);
                    boosted.Score = CombineTwo(d.Score, bestRx.Score, DmpWeight, RegexWeight);
                    boosted.Kind = d.Kind + "+regex";
                    merged.Add(boosted);
                    if (bestIdx >= 0)
                        usedRegex.Add(bestIdx);
                }
                else
                {
                    merged.Add(d);
                }
            }

            for (int j = 0; j < regex.Count; j++)
            {
                if (!usedRegex.Contains(j))
                    merged.Add(regex[j]);
            }

            return merged;
        }

        private static List<FieldMatch> FindMatchesWithRoi(
            List<TokenInfo> tokens,
            List<string> patterns,
            FieldPatternEntry entry,
            double minScore,
            string field,
            bool useRaw,
            bool textOnly,
            int maxPairs,
            int maxCandidates,
            bool log,
            List<FieldReject>? rejects = null)
        {
            var modeLabel = textOnly ? "text" : (useRaw ? "raw" : "typed");
            LogStep(log, CCyan, "[MATCH_BEGIN]", $"field={entry.Field} mode={modeLabel} tokens={tokens.Count} maxPairs={maxPairs} maxCand={maxCandidates}");
            var (roiTokens, roiPatterns, applied) = ApplyRoi(tokens, entry);
            LogStep(log, CMagenta, "[ROI]", $"field={entry.Field} applied={applied} tokens={roiTokens.Count}/{tokens.Count}");
            if (log && applied && roiTokens.Count > 0)
            {
                var roiSample = string.Join(" ", roiTokens.Take(40).Select(t => t.Text));
                LogStep(log, CBlue, "[ROI_TEXT]", $"field={entry.Field} sample=\"{TrimSnippet(roiSample, 140)}\"");
            }
            var localMatches = FindMatches(roiTokens, roiPatterns, entry, minScore, field, useRaw, textOnly, maxPairs, maxCandidates, log, rejects);
            if (!applied)
                return localMatches;

            if (localMatches.Count == 0)
                return FindMatches(tokens, patterns, entry, minScore, field, useRaw, textOnly, maxPairs, maxCandidates, log, rejects);

            var bestLocal = localMatches[0].Score;
            if (bestLocal >= minScore)
                return localMatches;

            // ROI can miss true anchors in PDFs with odd operator ordering/coords.
            // Evaluate full tokens only when ROI score is below the minimum.
            var fullMatches = FindMatches(tokens, patterns, entry, minScore, field, useRaw, textOnly, maxPairs, maxCandidates, log, rejects);
            if (fullMatches.Count == 0)
                return localMatches;

            var bestFull = fullMatches[0].Score;
            LogStep(log, CMagenta, "[ROI_PICK]", $"field={entry.Field} bestLocal={bestLocal:F2} bestFull={bestFull:F2} pick={(bestFull > bestLocal ? "full" : "roi")}");
            return bestFull > bestLocal ? fullMatches : localMatches;
        }

        private static List<FieldMatch> FindMatchesWithRoi(
            List<TokenInfo> tokens,
            List<string> patterns,
            FieldPatternEntry entry,
            double minScore,
            string field,
            bool useRaw,
            bool textOnly,
            List<FieldReject>? rejects = null)
        {
            return FindMatchesWithRoi(tokens, patterns, entry, minScore, field, useRaw, textOnly, int.MaxValue, 0, false, rejects);
        }

        private static List<TokenInfo> ExtractTokensFromStream(PdfStream stream, PdfResources resources, double pageHeight, double pageWidth, HashSet<string> opFilter)
        {
            var tokens = new List<TokenInfo>();
            var timeoutSec = PdfTextExtraction.TimeoutSec;
            var blocks = ObjectsTextOpsDiff.ExtractPatternBlocks(stream, resources, opFilter, allowFix: true);
            foreach (var block in blocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text) && string.IsNullOrWhiteSpace(block.RawText))
                    continue;
                // TYPED/TEXT devem usar o texto com espaçamento reconstruído quando disponível.
                var sourceText = !string.IsNullOrWhiteSpace(block.Text) ? block.Text : block.RawText;
                var normText = NormalizeFullText(sourceText);
                if (string.IsNullOrWhiteSpace(normText))
                    continue;
                var parts = normText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                List<string>? rawParts = null;
                if (block.RawTokens != null && block.RawTokens.Count == parts.Length)
                    rawParts = block.RawTokens;
                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i];
                    var rawPart = rawParts != null ? rawParts[i] : part;
                    var pt = ObjectsTextOpsDiff.EncodePatternTyped(part);
                    double? yNorm = null;
                    if (pageHeight > 0 && block.YMin.HasValue && block.YMax.HasValue)
                    {
                        var yMid = (block.YMin.Value + block.YMax.Value) / 2.0;
                        yNorm = yMid / pageHeight;
                    }
                    double? xNorm = null;
                    if (pageWidth > 0 && block.XMin.HasValue && block.XMax.HasValue)
                    {
                        var xMid = (block.XMin.Value + block.XMax.Value) / 2.0;
                        xNorm = xMid / pageWidth;
                    }
                    tokens.Add(new TokenInfo
                    {
                        Text = part,
                        Raw = rawPart ?? "",
                        BlockRawText = !string.IsNullOrWhiteSpace(block.Text) ? block.Text : (block.RawText ?? ""),
                        Pattern = pt,
                        BlockIndex = block.Index,
                        StartOp = block.StartOp,
                        EndOp = block.EndOp,
                        YNorm = yNorm,
                        XNorm = xNorm
                    });
                }
            }
            var merged = MergeShortUpperTokens(tokens);
            merged = CollapseSpacedLetterTokens(merged);
            return merged;
        }

        private static List<TokenInfo> ExtractRawTokensFromStream(PdfStream stream, PdfResources resources, double pageHeight, double pageWidth, HashSet<string> opFilter)
        {
            var tokens = new List<TokenInfo>();
            var timeoutSec = PdfTextExtraction.TimeoutSec;
            var blocks = ObjectsTextOpsDiff.ExtractPatternBlocks(stream, resources, opFilter, allowFix: true);
            foreach (var block in blocks)
            {
                var parts = GetRawTokens(block);
                if (parts.Count == 0)
                    continue;
                foreach (var part in parts)
                {
                    var pat = ToRawPatternToken(part);
                    double? yNorm = null;
                    if (pageHeight > 0 && block.YMin.HasValue && block.YMax.HasValue)
                    {
                        var yMid = (block.YMin.Value + block.YMax.Value) / 2.0;
                        yNorm = yMid / pageHeight;
                    }
                    double? xNorm = null;
                    if (pageWidth > 0 && block.XMin.HasValue && block.XMax.HasValue)
                    {
                        var xMid = (block.XMin.Value + block.XMax.Value) / 2.0;
                        xNorm = xMid / pageWidth;
                    }
                    tokens.Add(new TokenInfo
                    {
                        Text = part,
                        Raw = part,
                        BlockRawText = block.RawText ?? "",
                        Pattern = pat,
                        BlockIndex = block.Index,
                        StartOp = block.StartOp,
                        EndOp = block.EndOp,
                        YNorm = yNorm,
                        XNorm = xNorm
                    });
                }
            }
            return tokens;
        }

        private static void PrintAnchorHits(List<AnchorHit> hits, List<TokenInfo> tokens, int limit)
        {
            if (hits.Count == 0)
            {
                Console.WriteLine("      (0 hits)");
                return;
            }
            foreach (var hit in hits.Take(limit))
            {
                var slice = tokens.Skip(hit.Index).Take(hit.Length).ToList();
                if (slice.Count == 0)
                    continue;
                var text = string.Join(" ", slice.Select(t => t.Text));
                var startOp = slice.Min(t => t.StartOp);
                var endOp = slice.Max(t => t.EndOp);
                Console.WriteLine($"      score={hit.Score:F2} op{startOp}-op{endOp} \"{text}\"");
            }
        }

        private static List<FieldPatternEntry> LoadPatternEntries(string patternsPath, HashSet<string> fieldsFilter)
        {
            if (!File.Exists(patternsPath))
            {
                Console.WriteLine($"Arquivo de patterns nao encontrado: {patternsPath}");
                return new List<FieldPatternEntry>();
            }
            var json = File.ReadAllText(patternsPath);
            var entries = ParsePatternEntries(json);
            if (fieldsFilter != null && fieldsFilter.Count > 0)
                entries = entries.Where(e => !string.IsNullOrWhiteSpace(e.Field) && fieldsFilter.Contains(e.Field)).ToList();
            return entries;
        }

        private static HashSet<string> ReadFieldListFromPatterns(string patternsPath, params string[] names)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(patternsPath) || !File.Exists(patternsPath) || names == null || names.Length == 0)
                return result;

            try
            {
                var json = File.ReadAllText(patternsPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return result;

                foreach (var name in names)
                {
                    if (!TryGetPropertyIgnoreCase(root, name, out var el))
                        continue;
                    ExtractFieldNames(el, result);
                    if (result.Count > 0)
                        break;
                }
            }
            catch
            {
                return result;
            }

            return result;
        }

        private static void ExtractFieldNames(JsonElement element, HashSet<string> output)
        {
            if (output == null)
                return;

            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    var raw = element.GetString() ?? "";
                    foreach (var part in raw.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var name = part.Trim();
                        if (!string.IsNullOrWhiteSpace(name))
                            output.Add(name);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        ExtractFieldNames(item, output);
                    break;
                case JsonValueKind.Object:
                    if (TryGetPropertyIgnoreCase(element, "field", out var fieldEl) && fieldEl.ValueKind == JsonValueKind.String)
                    {
                        var name = fieldEl.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(name))
                            output.Add(name.Trim());
                        break;
                    }
                    if (TryGetPropertyIgnoreCase(element, "fields", out var fieldsEl))
                    {
                        ExtractFieldNames(fieldsEl, output);
                        break;
                    }
                    break;
            }
        }

        private static List<TokenInfo> ExtractTokensFromPdf(string pdfPath, int page, int objId, HashSet<string> opFilter)
        {
            var tokens = new List<TokenInfo>();
            if (page <= 0 || objId <= 0)
                return tokens;

            try
            {
                using var reader = new PdfReader(pdfPath);
                using var doc = new PdfDocument(reader);

                var found = FindStreamAndResourcesByObjId(doc, objId);
                if (found.Stream == null || found.Resources == null)
                    return tokens;

                double pageHeight = 0;
                double pageWidth = 0;
                try
                {
                    var pageObj = doc.GetPage(page);
                    pageHeight = pageObj?.GetPageSize()?.GetHeight() ?? 0;
                    pageWidth = pageObj?.GetPageSize()?.GetWidth() ?? 0;
                }
                catch
                {
                    pageHeight = 0;
                    pageWidth = 0;
                }

                var blocks = ObjectsTextOpsDiff.ExtractPatternBlocks(found.Stream, found.Resources, opFilter);
                foreach (var block in blocks)
                {
                    if (string.IsNullOrWhiteSpace(block.Text) && string.IsNullOrWhiteSpace(block.RawText))
                        continue;
                    // TYPED/TEXT devem usar texto normalizado; RAW usa o fluxo cru separadamente.
                    var sourceText = !string.IsNullOrWhiteSpace(block.RawText) ? block.RawText : block.Text;
                    var normText = NormalizeFullText(sourceText);
                    if (string.IsNullOrWhiteSpace(normText))
                        continue;
                    var parts = normText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    List<string>? rawParts = null;
                    if (block.RawTokens != null && block.RawTokens.Count == parts.Length)
                        rawParts = block.RawTokens;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        var part = parts[i];
                        var rawPart = rawParts != null ? rawParts[i] : part;
                        var pt = ObjectsTextOpsDiff.EncodePatternTyped(part);
                        double? yNorm = null;
                        if (pageHeight > 0 && block.YMin.HasValue && block.YMax.HasValue)
                        {
                            var yMid = (block.YMin.Value + block.YMax.Value) / 2.0;
                            yNorm = yMid / pageHeight;
                        }
                        double? xNorm = null;
                        if (pageWidth > 0 && block.XMin.HasValue && block.XMax.HasValue)
                        {
                            var xMid = (block.XMin.Value + block.XMax.Value) / 2.0;
                            xNorm = xMid / pageWidth;
                        }
                        tokens.Add(new TokenInfo
                        {
                            Text = part,
                            Raw = rawPart ?? "",
                            BlockRawText = block.RawText ?? "",
                            Pattern = pt,
                            BlockIndex = block.Index,
                            StartOp = block.StartOp,
                            EndOp = block.EndOp,
                            YNorm = yNorm,
                            XNorm = xNorm
                        });
                    }
                }

                return MergeShortUpperTokens(tokens);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PDF invalido: " + pdfPath + " (" + ex.Message + ")");
                return tokens;
            }
        }

        private static List<TokenInfo> ExtractRawTokensFromPdf(string pdfPath, int page, int objId, HashSet<string> opFilter)
        {
            var tokens = new List<TokenInfo>();
            if (page <= 0 || objId <= 0)
                return tokens;

            try
            {
                using var reader = new PdfReader(pdfPath);
                using var doc = new PdfDocument(reader);

                var found = FindStreamAndResourcesByObjId(doc, objId);
                if (found.Stream == null || found.Resources == null)
                    return tokens;

                double pageHeight = 0;
                double pageWidth = 0;
                try
                {
                    var pageObj = doc.GetPage(page);
                    pageHeight = pageObj?.GetPageSize()?.GetHeight() ?? 0;
                    pageWidth = pageObj?.GetPageSize()?.GetWidth() ?? 0;
                }
                catch
                {
                    pageHeight = 0;
                    pageWidth = 0;
                }

                var blocks = ObjectsTextOpsDiff.ExtractPatternBlocks(found.Stream, found.Resources, opFilter);
                foreach (var block in blocks)
                {
                    var parts = GetRawTokens(block);
                    if (parts.Count == 0)
                        continue;
                    foreach (var part in parts)
                    {
                        var pat = ToRawPatternToken(part);
                        double? yNorm = null;
                        if (pageHeight > 0 && block.YMin.HasValue && block.YMax.HasValue)
                        {
                            var yMid = (block.YMin.Value + block.YMax.Value) / 2.0;
                            yNorm = yMid / pageHeight;
                        }
                        double? xNorm = null;
                        if (pageWidth > 0 && block.XMin.HasValue && block.XMax.HasValue)
                        {
                            var xMid = (block.XMin.Value + block.XMax.Value) / 2.0;
                            xNorm = xMid / pageWidth;
                        }
                        tokens.Add(new TokenInfo
                        {
                            Text = part,
                            Raw = part,
                            BlockRawText = block.RawText ?? "",
                            Pattern = pat,
                            BlockIndex = block.Index,
                            StartOp = block.StartOp,
                            EndOp = block.EndOp,
                            YNorm = yNorm,
                            XNorm = xNorm
                        });
                    }
                }

                return tokens;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PDF invalido: " + pdfPath + " (" + ex.Message + ")");
                return tokens;
            }
        }

        private static List<ObjectsTextOpsDiff.PatternBlock> ExtractPatternBlocksFromPdf(string pdfPath, int page, int objId, HashSet<string> opFilter, bool allowFix = true)
        {
            if (!File.Exists(pdfPath))
                return new List<ObjectsTextOpsDiff.PatternBlock>();

            using var reader = new PdfReader(pdfPath);
            using var doc = new PdfDocument(reader);

            if (page <= 0 || objId <= 0)
                return new List<ObjectsTextOpsDiff.PatternBlock>();

            var found = FindStreamAndResourcesByObjId(doc, objId);
            if (found.Stream == null || found.Resources == null)
                return new List<ObjectsTextOpsDiff.PatternBlock>();

            return ObjectsTextOpsDiff.ExtractPatternBlocks(found.Stream, found.Resources, opFilter, allowFix);
        }

        private static string BuildFullTextFromBlocks(List<ObjectsTextOpsDiff.PatternBlock> blocks)
        {
            if (blocks == null || blocks.Count == 0)
                return "";
            var parts = new List<string>(blocks.Count);
            foreach (var b in blocks)
            {
                var t = !string.IsNullOrWhiteSpace(b.RawText) ? b.RawText : b.Text;
                if (!string.IsNullOrWhiteSpace(t))
                    parts.Add(t);
            }
            if (parts.Count == 0)
                return "";
            return TextNormalization.NormalizeWhitespace(string.Join(" ", parts));
        }

        private static string BuildFullTextFromBlocks(string pdfPath, int page, int objId, HashSet<string> opFilter)
        {
            var blocks = ExtractPatternBlocksFromPdf(pdfPath, page, objId, opFilter);
            return BuildFullTextFromBlocks(blocks);
        }

        private static string BuildFullTextFromTextOps(string pdfPath, int objId, bool log = false)
        {
            if (!File.Exists(pdfPath) || objId <= 0)
                return "";
            try
            {
                using var reader = new PdfReader(pdfPath);
                using var doc = new PdfDocument(reader);
                var found = FindStreamAndResourcesByObjId(doc, objId);
                if (found.Stream == null || found.Resources == null)
                {
                    if (log)
                        Console.Error.WriteLine($"[FULLTEXT] textops stream/resource nao encontrado obj={objId}");
                    return "";
                }
                var parts = PdfTextExtraction.CollectTextOperatorTexts(found.Stream, found.Resources);
                if (parts == null || parts.Count == 0)
                    return "";
                var joined = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
                return TextNormalization.NormalizePatternText(joined);
            }
            catch
            {
                if (log)
                    Console.Error.WriteLine($"[FULLTEXT] excecao ao extrair textops obj={objId}");
                return "";
            }
        }

        private static string PickBestFullText(string textOps, string blocks, string stream)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(textOps)) candidates.Add(textOps);
            if (!string.IsNullOrWhiteSpace(stream)) candidates.Add(stream);
            if (!string.IsNullOrWhiteSpace(blocks)) candidates.Add(blocks);
            if (candidates.Count == 0) return "";

            string best = candidates[0];
            var bestBreaks = ComputeUpperSplitScore(best);
            double bestScore = TextUtils.ComputeWeirdSpacingRatio(best);
            foreach (var c in candidates.Skip(1))
            {
                var breaks = ComputeUpperSplitScore(c);
                var score = TextUtils.ComputeWeirdSpacingRatio(c);
                if (breaks < bestBreaks || (breaks == bestBreaks && score < bestScore))
                {
                    best = c;
                    bestBreaks = breaks;
                    bestScore = score;
                }
            }
            return best;
        }

        private static int ComputeUpperSplitScore(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;
            var t = text;
            var score = 0;
            score += System.Text.RegularExpressions.Regex.Matches(t, @"\b\p{Lu}{3,}\s+\p{Lu}{1,3}\b").Count;
            score += System.Text.RegularExpressions.Regex.Matches(t, @"\b\p{Lu}{1,3}\s+\p{Lu}{4,}\b").Count;
            return score;
        }

        private static string ExtractRegexValueFromBlocks(string field, List<ObjectsTextOpsDiff.PatternBlock> blocks)
        {
            if (_regexCatalog == null || !_regexCatalog.TryGetValue(field, out var rules) || rules.Count == 0)
                return "";
            if (blocks == null || blocks.Count == 0)
                return "";

            foreach (var b in blocks)
            {
                var raw = !string.IsNullOrWhiteSpace(b.RawText) ? b.RawText : b.Text;
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var norm = TextNormalization.NormalizeWhitespace(TextNormalization.FixMissingSpaces(raw));
                foreach (var rule in rules)
                {
                    var rx = rule.Compiled;
                    if (rx == null)
                        continue;
                    var m = rx.Match(norm);
                    if (!m.Success)
                        continue;
                    var g = (rule.Group >= 0 && rule.Group < m.Groups.Count) ? m.Groups[rule.Group] : m.Groups[0];
                    if (g == null || !g.Success)
                        continue;
                    var value = g.Value.Trim();
                    if (string.IsNullOrWhiteSpace(value))
                        continue;
                    if (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                        field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                    {
                        var cleaned = TextNormalization.NormalizeWhitespace(value);
                        cleaned = cleaned.Trim().TrimEnd(',', '.', ';', '–', '—', '-');
                        if (!string.IsNullOrWhiteSpace(cleaned))
                            return cleaned;
                    }

                    value = NormalizeValueByField(field, value);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            return "";
        }

        private static string BuildTypedPatternSpaced(string text)
        {
            var normalized = NormalizePatternText(text ?? "");
            if (string.IsNullOrWhiteSpace(normalized))
                return "";
            var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(ObjectsTextOpsDiff.EncodePatternTyped(parts[i]));
            }
            return sb.ToString();
        }

        private static string BuildSimplePatternSpacedRaw(string text)
        {
            // RAW de verdade: nao colapsa letras espacadas nem corrige defeitos.
            // Apenas separa por whitespace e marca tokens de 1 char como "1".
            var tokens = SplitRawTokens(text ?? "");
            if (tokens.Count == 0) return "";
            return string.Concat(tokens.Select(ToRawPatternToken));
        }

        private static string BuildRawPatternFromLetters(string text)
        {
            // Variante RAW para letras espaçadas: cada caractere vira "1".
            var baseText = PickAnchorTextPrimary(text);
            var tokens = SplitRawTokens(baseText ?? "");
            if (tokens.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                var t = tokens[i];
                if (t == ":")
                {
                    sb.Append(':');
                    continue;
                }
                foreach (var ch in t)
                {
                    if (ch == ':')
                        sb.Append(':');
                    else
                        sb.Append('1');
                }
            }
            return sb.ToString();
        }

        private static string BuildSimplePatternSpacedRawTokens(IEnumerable<string> tokens)
        {
            if (tokens == null)
                return "";
            var list = tokens.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (list.Count == 0) return "";
            return string.Concat(list.Select(ToRawPatternToken));
        }

        private static List<string> GetRawTokens(ObjectsTextOpsDiff.PatternBlock block)
        {
            if (block.RawTokens != null && block.RawTokens.Count > 0)
                return block.RawTokens;
            return SplitRawTokens(block.RawText ?? "");
        }

        private static List<string> SplitRawTokens(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();
            var normalized = TextUtils.NormalizeWhitespace(raw);
            if (string.IsNullOrWhiteSpace(normalized))
                return new List<string>();
            return System.Text.RegularExpressions.Regex
                .Split(normalized, @"\s+")
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
        }

        private static string ToRawPatternToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return "";
            return token == ":" ? ":" : (token.Length == 1 ? "1" : "W");
        }

        private static int MaxRunOf(List<string> tokens, string target)
        {
            int max = 0;
            int cur = 0;
            foreach (var t in tokens)
            {
                if (t == target)
                {
                    cur++;
                    if (cur > max) max = cur;
                }
                else
                {
                    cur = 0;
                }
            }
            return max;
        }

        private static bool HasRawPatterns(FieldPatternEntry entry)
        {
            return !string.IsNullOrWhiteSpace(entry.PrevPatternRaw) ||
                   !string.IsNullOrWhiteSpace(entry.ValuePatternRaw) ||
                   !string.IsNullOrWhiteSpace(entry.NextPatternRaw);
        }

        private static bool HasTypedPatterns(FieldPatternEntry entry)
        {
            return !string.IsNullOrWhiteSpace(entry.PrevPatternTyped) ||
                   !string.IsNullOrWhiteSpace(entry.ValuePatternTyped) ||
                   !string.IsNullOrWhiteSpace(entry.NextPatternTyped);
        }

        private static bool HasAnchorText(FieldPatternEntry entry)
        {
            return !string.IsNullOrWhiteSpace(entry.Prev) && !string.IsNullOrWhiteSpace(entry.Next);
        }

        private static bool HasRegexRules(string fieldName)
        {
            return _regexCatalog != null &&
                   _regexCatalog.TryGetValue(fieldName, out var rules) &&
                   rules != null && rules.Count > 0;
        }

        private static bool IsGeorcDependentField(string field)
        {
            return field.Equals("VALOR_ARBITRADO_DE", StringComparison.OrdinalIgnoreCase)
                || field.Equals("DATA_ARBITRADO_FINAL", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasGeorcMention(List<TokenInfo> tokens, List<TokenInfo> rawTokens)
        {
            var norm = NormalizeForContains(string.Join(" ", tokens.Select(t => t.Text)));
            if (ContainsGeorc(norm))
                return true;
            var raw = NormalizeForContains(string.Join(" ", rawTokens.Select(t => t.Text)));
            return ContainsGeorc(raw);
        }

        private static bool ContainsGeorc(string text)
        {
            return ValidatorRules.ContainsGeorc(text);
        }

        private static List<FieldMatch> FilterPeritoCpfEspec(string field, List<FieldMatch> matches)
        {
            if (matches == null || matches.Count == 0)
                return matches;
            if (!field.Equals("PERITO", StringComparison.OrdinalIgnoreCase) &&
                !field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase) &&
                !field.Equals("ESPECIALIDADE", StringComparison.OrdinalIgnoreCase))
                return matches;

            if (field.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase))
            {
                var filtered = matches.Where(m => !string.IsNullOrWhiteSpace(m.ValueText) && ValueMatchesRegexCatalog("CPF_PERITO", m.ValueText)).ToList();
                return filtered.Count > 0 ? filtered : matches;
            }

            var stopwords = new[]
            {
                "TRATA-SE", "TRATA", "TRATAM", "OS PRESENTES", "REQUISICAO", "REQUISIÇÃO", "AUTOS", "HONORARIOS",
                "HONORÁRIOS", "PAGAMENTO", "REQUERENTE", "INTERESSADO", "INTERESSADA", "VALOR"
            };

            var filteredNames = new List<FieldMatch>();
            var applyStopwords = !field.Equals("PERITO", StringComparison.OrdinalIgnoreCase);
            foreach (var m in matches)
            {
                if (string.IsNullOrWhiteSpace(m.ValueText))
                    continue;
                var norm = NormalizeForContains(m.ValueText);
            if (!field.Equals("PERITO", StringComparison.OrdinalIgnoreCase) && norm.Length > 140)
                continue;
                if (applyStopwords && stopwords.Any(sw => norm.Contains(sw)))
                    continue;
                if (field.Equals("ESPECIALIDADE", StringComparison.OrdinalIgnoreCase))
                {
                    var words = m.ValueText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length > 8)
                        continue;
                }
                filteredNames.Add(m);
            }

            return filteredNames.Count > 0 ? filteredNames : matches;
        }

        private static FieldMatch DeriveFieldMatch(FieldMatch source, string field, string kind)
        {
            return new FieldMatch
            {
                Field = field,
                Pdf = source.Pdf,
                ValueText = source.ValueText,
                ValuePattern = source.ValuePattern,
                ValueExpectedPattern = source.ValueExpectedPattern,
                Score = source.Score,
                PrevScore = source.PrevScore,
                NextScore = source.NextScore,
                ValueScore = source.ValueScore,
                PrevText = source.PrevText,
                NextText = source.NextText,
                PrevExpectedPattern = source.PrevExpectedPattern,
                PrevActualPattern = source.PrevActualPattern,
                PrevPatternScore = source.PrevPatternScore,
                PrevExpectedText = source.PrevExpectedText,
                PrevActualText = source.PrevActualText,
                PrevTextScore = source.PrevTextScore,
                NextExpectedPattern = source.NextExpectedPattern,
                NextActualPattern = source.NextActualPattern,
                NextPatternScore = source.NextPatternScore,
                NextExpectedText = source.NextExpectedText,
                NextActualText = source.NextActualText,
                NextTextScore = source.NextTextScore,
                StartOp = source.StartOp,
                EndOp = source.EndOp,
                Band = source.Band,
                YRange = source.YRange,
                XRange = source.XRange,
                Kind = kind,
                PrevMode = source.PrevMode,
                NextMode = source.NextMode
            };
        }

        private static double AdjustMinScore(string field, double baseScore)
        {
            if (string.IsNullOrWhiteSpace(field))
                return baseScore;
            switch (field.Trim().ToUpperInvariant())
            {
                case "PROMOVENTE":
                case "PROMOVIDO":
                case "VALOR_ARBITRADO_JZ":
                    return Math.Min(baseScore, 0.55);
                default:
                    return baseScore;
            }
        }

        private static bool HasRawSpacing(FieldPatternEntry entry)
        {
            static bool HasOneToken(string? pattern)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                    return false;
                var tokens = ParsePatternTokens(pattern);
                return tokens.Any(t => t == "1");
            }

            return HasOneToken(entry.PrevPatternRaw) || HasOneToken(entry.NextPatternRaw) || HasOneToken(entry.ValuePatternRaw);
        }

        private sealed class AnchorHit
        {
            public int Index { get; set; }
            public int Length { get; set; }
            public double Score { get; set; }
            public string Mode { get; set; } = "pattern";
            public string PatternExpected { get; set; } = "";
            public string PatternActual { get; set; } = "";
            public double PatternScore { get; set; }
            public string TextExpected { get; set; } = "";
            public string TextActual { get; set; } = "";
            public double TextScore { get; set; }
        }

        private static int LowerBoundByIndex(List<AnchorHit> hits, int target)
        {
            int lo = 0;
            int hi = hits.Count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (hits[mid].Index < target)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }

        private static List<FieldMatch> FindMatches(
            List<TokenInfo> tokens,
            List<string> tokenPatterns,
            FieldPatternEntry entry,
            double minScore,
            string field,
            bool useRaw,
            bool textOnly,
            int maxPairs,
            int maxCandidates,
            bool log,
            List<FieldReject>? rejects)
        {
            var matches = new List<FieldMatch>();
            var prev = BuildPatternSegment(entry.Prev, entry.PrevPatternTyped, entry.PrevPatternRaw, useRaw);
            var mid = BuildPatternSegment(entry.Value, entry.ValuePatternTyped, entry.ValuePatternRaw, useRaw);
            var next = BuildPatternSegment(entry.Next, entry.NextPatternTyped, entry.NextPatternRaw, useRaw);
            var modeLabel = textOnly ? "text" : (useRaw ? "raw" : "typed");
            LogStep(log, CYellow, "[ANCHORS]", $"field={entry.Field} mode={modeLabel} prev=\"{TrimSnippet(prev.Text)}\" next=\"{TrimSnippet(next.Text)}\"");
            LogStep(log, CMagenta, "[DMP]", $"field={entry.Field} mode={modeLabel} scoring");

            if (textOnly)
            {
                if (string.IsNullOrWhiteSpace(prev.Text) || string.IsNullOrWhiteSpace(next.Text))
                    return matches;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(prev.Pattern) || string.IsNullOrWhiteSpace(next.Pattern))
                    return matches;
            }

            var prevTokens = ParsePatternTokens(prev.Pattern);
            var nextTokens = ParsePatternTokens(next.Pattern);
            var expectedTokens = ParsePatternTokens(mid.Pattern);

            List<string>? prevTokensAlt = null;
            List<string>? nextTokensAlt = null;
            if (useRaw && !textOnly)
            {
                var prevAlt = BuildRawPatternFromLetters(prev.Text);
                var nextAlt = BuildRawPatternFromLetters(next.Text);
                if (!string.IsNullOrWhiteSpace(prevAlt))
                    prevTokensAlt = ParsePatternTokens(prevAlt);
                if (!string.IsNullOrWhiteSpace(nextAlt))
                    nextTokensAlt = ParsePatternTokens(nextAlt);
            }

            (string Text, List<int> Starts, List<int> Ends)? normCache = textOnly ? null : BuildNormalizedTokenText(tokens);
            List<AnchorHit> prevHits;
            bool skipPatternFallback = !string.IsNullOrWhiteSpace(entry.Field) &&
                (entry.Field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                 entry.Field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase));

            if (textOnly)
            {
                prevHits = FindAnchorHitsTextOnly(tokens, prev.Text, 0, tokens.Count, minScore);
            }
            else
            {
                // prioriza regex por texto (mais preciso), cai para pattern se necessario
                prevHits = !string.IsNullOrWhiteSpace(prev.Text)
                    ? FindAnchorHitsRegexText(tokens, prev.Text, 0, tokens.Count, minScore, normCache)
                    : new List<AnchorHit>();
                if (prevHits.Count == 0 && !skipPatternFallback)
                    prevHits = FindAnchorHitsPattern(tokenPatterns, tokens, prevTokens, useRaw ? null : prev.Text, 0, tokens.Count, minScore);
                if (useRaw && prevHits.Count == 0 && prevTokensAlt != null && prevTokensAlt.Count > 0)
                    prevHits = FindAnchorHitsPattern(tokenPatterns, tokens, prevTokensAlt, null, 0, tokens.Count, minScore);
            }
            LogStep(log, CYellow, "[ANCHOR_PREV]", $"field={entry.Field} mode={modeLabel} hits={prevHits.Count}");
            if (prevHits.Count == 0)
                return matches;

            // Computa os NEXT uma vez só (evita recalcular a cada prevHit).
            List<AnchorHit> nextHitsAll;
            if (textOnly)
            {
                nextHitsAll = FindAnchorHitsTextOnly(tokens, next.Text, 0, tokens.Count, minScore);
            }
            else
            {
                nextHitsAll = !string.IsNullOrWhiteSpace(next.Text)
                    ? FindAnchorHitsRegexText(tokens, next.Text, 0, tokens.Count, minScore, normCache)
                    : new List<AnchorHit>();
                if (nextHitsAll.Count == 0 && !skipPatternFallback)
                    nextHitsAll = FindAnchorHitsPattern(tokenPatterns, tokens, nextTokens, useRaw ? null : next.Text, 0, tokens.Count, minScore);
                if (useRaw && nextHitsAll.Count == 0 && nextTokensAlt != null && nextTokensAlt.Count > 0)
                    nextHitsAll = FindAnchorHitsPattern(tokenPatterns, tokens, nextTokensAlt, null, 0, tokens.Count, minScore);
            }
            LogStep(log, CYellow, "[ANCHOR_NEXT]", $"field={entry.Field} mode={modeLabel} hits={nextHitsAll.Count}");
            if (nextHitsAll.Count == 0)
                return matches;

            if (maxCandidates > 0)
            {
                prevHits = prevHits
                    .OrderByDescending(h => h.Score)
                    .ThenBy(h => h.Index)
                    .Take(maxCandidates)
                    .ToList();
                nextHitsAll = nextHitsAll
                    .OrderByDescending(h => h.Score)
                    .ThenBy(h => h.Index)
                    .Take(maxCandidates)
                    .ToList();
            }
            var nextHitsByIndex = nextHitsAll.OrderBy(h => h.Index).ToList();

            long pairCount = 0;
            int? maxValueTokens = null;
            int maxNextPerPrev = int.MaxValue;
            if (log)
                Console.Error.WriteLine($"[MATCH] field={entry.Field} mode={modeLabel} prevHits={prevHits.Count} nextHits={nextHitsAll.Count}");

            if (!string.IsNullOrWhiteSpace(entry.Field))
            {
                if (entry.Field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                    entry.Field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                {
                    maxValueTokens ??= 25;
                    maxNextPerPrev = Math.Min(maxNextPerPrev, 2);
                }
                else if (entry.Field.Equals("PROCESSO_JUDICIAL", StringComparison.OrdinalIgnoreCase))
                {
                    maxValueTokens ??= 30;
                    maxNextPerPrev = Math.Min(maxNextPerPrev, 2);
                }
                else if (entry.Field.Equals("VALOR_ARBITRADO_JZ", StringComparison.OrdinalIgnoreCase))
                {
                    maxValueTokens ??= 15;
                    maxNextPerPrev = Math.Min(maxNextPerPrev, 2);
                }
                else if (entry.Field.Equals("PERITO", StringComparison.OrdinalIgnoreCase) ||
                         entry.Field.Equals("ESPECIALIDADE", StringComparison.OrdinalIgnoreCase))
                {
                    maxValueTokens ??= 15;
                    maxNextPerPrev = Math.Min(maxNextPerPrev, 2);
                }
            }

            if (maxPairs > 0)
            {
                var estPairs = (long)prevHits.Count * (long)nextHitsAll.Count;
                if (estPairs > maxPairs)
                {
                    if (log)
                        Console.Error.WriteLine($"[MATCH] field={entry.Field} mode={modeLabel} pairs_est={estPairs} > {maxPairs} -> limit(proximity)");
                    // aplica limite por proximidade para reduzir combinacoes excessivas
                    maxValueTokens = Math.Min(maxValueTokens ?? 40, 40);
                    maxNextPerPrev = Math.Min(maxNextPerPrev, 2);
                }
            }

            LogStep(log, CMagenta, "[PAIRING]", $"field={entry.Field} mode={modeLabel} prevHits={prevHits.Count} nextHits={nextHitsAll.Count} maxPairs={maxPairs} maxCandidates={maxCandidates}");

            foreach (var prevHit in prevHits)
            {
                var startValue = prevHit.Index + prevHit.Length;
                if (startValue >= tokens.Count)
                    continue;

                int startIdx = LowerBoundByIndex(nextHitsByIndex, startValue + 1);
                var remaining = nextHitsByIndex.Count - startIdx;
                if (remaining <= 0)
                    continue;
                int take = remaining;

                if (maxValueTokens.HasValue)
                {
                    var maxIndex = startValue + maxValueTokens.Value;
                    int limitIdx = startIdx;
                    while (limitIdx < nextHitsByIndex.Count && nextHitsByIndex[limitIdx].Index <= maxIndex)
                        limitIdx++;
                    take = Math.Min(take, limitIdx - startIdx);
                }

                if (maxNextPerPrev < int.MaxValue)
                    take = Math.Min(take, maxNextPerPrev);

                if (take <= 0)
                    continue;
                if (maxPairs > 0)
                {
                    int allowed = (int)Math.Max(0, maxPairs - pairCount);
                    if (allowed == 0)
                    {
                        if (log)
                            Console.Error.WriteLine($"[MATCH] field={entry.Field} mode={modeLabel} pairs>{maxPairs} -> limit");
                        break;
                    }
                    if (allowed < take)
                    {
                        take = allowed;
                        if (log)
                            Console.Error.WriteLine($"[MATCH] field={entry.Field} mode={modeLabel} pairs>{maxPairs} -> limit");
                    }
                }
                pairCount += take;
                if (log && take > 0)
                    Console.Error.WriteLine($"[MATCH] field={entry.Field} mode={modeLabel} prevIdx={prevHit.Index} startIdx={startIdx} take={take} pairs={pairCount}");
                for (int i = startIdx; i < startIdx + take; i++)
                {
                    var nextHit = nextHitsByIndex[i];
                    if (nextHit.Index <= startValue)
                        continue;

                    var valueTokens = tokens.Skip(startValue).Take(nextHit.Index - startValue).ToList();
                    if (valueTokens.Count == 0)
                    {
                        if (log)
                            Console.Error.WriteLine($"[MATCH] field={entry.Field} mode={modeLabel} skip=empty_value prevIdx={prevHit.Index} nextIdx={nextHit.Index}");
                        if (rejects != null && rejects.Count < 200)
                        {
                            rejects.Add(new FieldReject
                            {
                                Field = entry.Field ?? "",
                                Pdf = field,
                                Reason = "empty_value",
                                ValueText = "",
                                ValuePattern = "",
                                ValueExpectedPattern = "",
                                Score = 0,
                                PrevScore = prevHit.Score,
                                NextScore = nextHit.Score,
                                ValueScore = 0,
                                PrevText = SliceTokensText(tokens, prevHit.Index, prevHit.Length),
                                NextText = SliceTokensText(tokens, nextHit.Index, nextHit.Length),
                                PrevExpectedPattern = prevHit.PatternExpected,
                                PrevActualPattern = prevHit.PatternActual,
                                PrevPatternScore = prevHit.PatternScore,
                                PrevExpectedText = prevHit.TextExpected,
                                PrevActualText = prevHit.TextActual,
                                PrevTextScore = prevHit.TextScore,
                                NextExpectedPattern = nextHit.PatternExpected,
                                NextActualPattern = nextHit.PatternActual,
                                NextPatternScore = nextHit.PatternScore,
                                NextExpectedText = nextHit.TextExpected,
                                NextActualText = nextHit.TextActual,
                                NextTextScore = nextHit.TextScore,
                                StartOp = 0,
                                EndOp = 0,
                                Band = entry.Band ?? "",
                                YRange = entry.YRange ?? "",
                                XRange = entry.XRange ?? "",
                                Kind = useRaw ? "raw" : "typed",
                                PrevMode = prevHit.Mode,
                                NextMode = nextHit.Mode
                            });
                        }
                        continue;
                    }

                    var valuePatternTokens = valueTokens.Select(t => t.Pattern).ToList();
                    var valuePattern = string.Join("|", valuePatternTokens);
                    var expectedPattern = string.Join("|", expectedTokens);
                    var valueText = string.Join(" ", valueTokens.Select(t => t.Text));
                    if (useRaw && !textOnly)
                        valueText = TextNormalization.NormalizePatternText(valueText);

                    double valueScore;
                    if (expectedTokens.Count == 1 && valuePatternTokens.All(t => t == expectedTokens[0]))
                        valueScore = 1.0;
                    else
                        valueScore = PatternSimilarity(expectedPattern, valuePattern);

                    var ignoreValue = false;
                    if (!string.IsNullOrWhiteSpace(entry.Field))
                    {
                        // Para PROMOVENTE/PROMOVIDO, o valor pode vir com CPF/pontuação e variar muito.
                        // Usa somente as âncoras para decidir e não penaliza pelo padrão do valor.
                        if (entry.Field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                            entry.Field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase))
                        {
                            ignoreValue = true;
                        }
                    }
                    var totalScore = CombineScores(prevHit.Score, nextHit.Score, valueScore, expectedTokens.Count > 0 && !ignoreValue);
                    if (IsUppercaseValue(valueText) &&
                        (field.Equals("PROMOVENTE", StringComparison.OrdinalIgnoreCase) ||
                         field.Equals("PROMOVIDO", StringComparison.OrdinalIgnoreCase)))
                    {
                        totalScore = Math.Min(1.0, totalScore + 0.05);
                    }
                    if (totalScore < minScore)
                    {
                        if (log)
                            Console.Error.WriteLine($"[MATCH] field={entry.Field} mode={modeLabel} skip=min_score score={totalScore:F2} prev={prevHit.Score:F2} next={nextHit.Score:F2} val={valueScore:F2} prevIdx={prevHit.Index} nextIdx={nextHit.Index}");
                        if (rejects != null && rejects.Count < 200)
                        {
                            var prevTextR = SliceTokensText(tokens, prevHit.Index, prevHit.Length);
                            var nextTextR = SliceTokensText(tokens, nextHit.Index, nextHit.Length);
                            var textR = valueText;
                            var startOpR = valueTokens.Min(t => t.StartOp);
                            var endOpR = valueTokens.Max(t => t.EndOp);
                            rejects.Add(new FieldReject
                            {
                                Field = entry.Field ?? "",
                                Pdf = field,
                                Reason = "min_score",
                                ValueText = textR,
                                ValuePattern = valuePattern,
                                ValueExpectedPattern = expectedPattern,
                                Score = totalScore,
                                PrevScore = prevHit.Score,
                                NextScore = nextHit.Score,
                                ValueScore = valueScore,
                                PrevText = prevTextR,
                                NextText = nextTextR,
                                PrevExpectedPattern = prevHit.PatternExpected,
                                PrevActualPattern = prevHit.PatternActual,
                                PrevPatternScore = prevHit.PatternScore,
                                PrevExpectedText = prevHit.TextExpected,
                                PrevActualText = prevHit.TextActual,
                                PrevTextScore = prevHit.TextScore,
                                NextExpectedPattern = nextHit.PatternExpected,
                                NextActualPattern = nextHit.PatternActual,
                                NextPatternScore = nextHit.PatternScore,
                                NextExpectedText = nextHit.TextExpected,
                                NextActualText = nextHit.TextActual,
                                NextTextScore = nextHit.TextScore,
                                StartOp = startOpR,
                                EndOp = endOpR,
                                Band = entry.Band ?? "",
                                YRange = entry.YRange ?? "",
                                XRange = entry.XRange ?? "",
                                Kind = useRaw ? "raw" : "typed",
                                PrevMode = prevHit.Mode,
                                NextMode = nextHit.Mode
                            });
                        }
                        continue;
                    }

                    var prevText = SliceTokensText(tokens, prevHit.Index, prevHit.Length);
                    var nextText = SliceTokensText(tokens, nextHit.Index, nextHit.Length);
                    var text = NormalizeValueByField(entry.Field ?? "", valueText);
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    var startOp = valueTokens.Min(t => t.StartOp);
                    var endOp = valueTokens.Max(t => t.EndOp);

                    matches.Add(new FieldMatch
                    {
                        Field = entry.Field,
                        Pdf = field,
                        ValueText = text,
                        ValuePattern = valuePattern,
                        ValueExpectedPattern = expectedPattern,
                        Score = totalScore,
                        PrevScore = prevHit.Score,
                        NextScore = nextHit.Score,
                        ValueScore = valueScore,
                        PrevText = prevText,
                        NextText = nextText,
                        PrevExpectedPattern = prevHit.PatternExpected,
                        PrevActualPattern = prevHit.PatternActual,
                        PrevPatternScore = prevHit.PatternScore,
                        PrevExpectedText = prevHit.TextExpected,
                        PrevActualText = prevHit.TextActual,
                        PrevTextScore = prevHit.TextScore,
                        NextExpectedPattern = nextHit.PatternExpected,
                        NextActualPattern = nextHit.PatternActual,
                        NextPatternScore = nextHit.PatternScore,
                        NextExpectedText = nextHit.TextExpected,
                        NextActualText = nextHit.TextActual,
                        NextTextScore = nextHit.TextScore,
                        StartOp = startOp,
                        EndOp = endOp,
                        Band = entry.Band ?? "",
                        YRange = entry.YRange ?? "",
                        XRange = entry.XRange ?? "",
                        Kind = textOnly ? "text" : (useRaw ? "raw" : "typed"),
                        PrevMode = prevHit.Mode,
                        NextMode = nextHit.Mode
                    });
                }
            }
            return matches;
        }

        private static List<string> ParsePatternTokens(string pattern)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(pattern))
                return tokens;
            int i = 0;
            while (i < pattern.Length)
            {
                var ch = pattern[i];
                if (char.IsWhiteSpace(ch) || ch == '|')
                {
                    i++;
                    continue;
                }

                // RAW tokens: 1 / W
                if (ch == '1')
                {
                    tokens.Add("1");
                    i++;
                    continue;
                }
                if (ch == 'W')
                {
                    tokens.Add("W");
                    i++;
                    continue;
                }
                // Compat RAW antigo: IN / IW
                if (ch == 'I' && i + 1 < pattern.Length &&
                    (pattern[i + 1] == 'N' || pattern[i + 1] == 'W'))
                {
                    tokens.Add(pattern[i + 1] == 'N' ? "1" : "W");
                    i += 2;
                    continue;
                }

                var sb = new StringBuilder();
                sb.Append(ch);
                i++;

                if (i < pattern.Length && char.IsDigit(pattern[i]))
                {
                    sb.Append(pattern[i]);
                    i++;
                }
                if (ch == 'N' && i < pattern.Length && (pattern[i] == 'a' || pattern[i] == 'o'))
                {
                    sb.Append(pattern[i]);
                    i++;
                }
                if (i < pattern.Length && pattern[i] == ':')
                {
                    sb.Append(':');
                    i++;
                }

                tokens.Add(sb.ToString());
            }
            return tokens;
        }

        private static bool HasAdiantamento(List<TokenInfo> tokens, List<TokenInfo> rawTokens)
        {
            static bool hasTerm(IEnumerable<TokenInfo> list)
            {
                foreach (var t in list)
                {
                    var norm = NormalizePatternText(t.Text ?? "");
                    if (norm.Contains("adiantamento", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }
            return hasTerm(tokens) || hasTerm(rawTokens);
        }

        private static bool IsAdiantamentoDependentField(string field)
        {
            if (string.IsNullOrWhiteSpace(field))
                return false;
            return field.Equals("ADIANTAMENTO", StringComparison.OrdinalIgnoreCase) ||
                   field.Equals("PERCENTUAL", StringComparison.OrdinalIgnoreCase) ||
                   field.Equals("PARCELA", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAdiantamentoField(string field)
        {
            return IsAdiantamentoDependentField(field);
        }

        private static bool ContainsAdiantamento(string text)
        {
            return ValidatorRules.ContainsAdiantamento(text, NormalizePatternText);
        }

        private static List<FieldMatch> FilterAdiantamento(string field, List<FieldMatch> matches, List<FieldReject>? rejects, bool hasAdiantamento)
        {
            if (matches.Count == 0)
                return matches;
            // flexibilizado: nao bloquear candidatos por "adiantamento".
            // deixa o DMP/regex decidir pelo score e pelo ROI.
            return matches;
        }

        private static string SliceTokensText(List<TokenInfo> tokens, int index, int length)
        {
            if (tokens.Count == 0 || index < 0 || length <= 0)
                return "";
            var slice = tokens.Skip(index).Take(length).Select(t => t.Text).ToList();
            if (slice.Count == 0)
                return "";
            return string.Join(" ", slice);
        }

        private static List<AnchorHit> FindAnchorHitsPattern(List<string> tokenPatterns, List<TokenInfo> tokens, List<string> anchorTokens, string? anchorText, int start, int end, double minScore)
        {
            var hits = new List<AnchorHit>();
            if (anchorTokens.Count == 0)
                return hits;

            var anchorPattern = string.Join("|", anchorTokens);
            var anchorTextNorm = string.IsNullOrWhiteSpace(anchorText) ? "" : NormalizeAnchorText(anchorText);
            int maxStart = Math.Max(start, 0);
            int maxEnd = Math.Min(end, tokenPatterns.Count);
            int window = anchorTokens.Count;

            for (int i = maxStart; i <= maxEnd - window; i++)
            {
                var candidateTokens = tokenPatterns.Skip(i).Take(window).ToList();
                int tokenMatches = 0;
                for (int k = 0; k < window; k++)
                {
                    if (anchorTokens[k] == candidateTokens[k])
                        tokenMatches++;
                }
                var tokenScore = window == 0 ? 0 : (double)tokenMatches / window;
                if (tokenScore < minScore)
                    continue;

                var candidate = string.Join("|", candidateTokens);
                double patternScore;
                if (tokenScore >= 0.99 && string.IsNullOrWhiteSpace(anchorTextNorm))
                {
                    patternScore = 1.0;
                }
                else
                {
                    patternScore = PatternSimilarity(anchorPattern, candidate);
                }
                var score = patternScore;
                if (score < minScore)
                    continue;
                var mode = "pattern";
                string textExpected = "";
                string textActual = "";
                double textScore = 0;
                if (!string.IsNullOrWhiteSpace(anchorTextNorm))
                {
                    var candText = string.Join(" ", tokens.Skip(i).Take(window).Select(t => t.Text));
                    var candNorm = NormalizeAnchorText(candText);
                    textScore = PatternSimilarity(anchorTextNorm, candNorm);
                    if (textScore < minScore)
                        continue;
                    score = (score + textScore) / 2.0;
                    mode = "pattern+text";
                    textExpected = anchorTextNorm;
                    textActual = candNorm;
                }
                hits.Add(new AnchorHit
                {
                    Index = i,
                    Length = window,
                    Score = score,
                    Mode = mode,
                    PatternExpected = anchorPattern,
                    PatternActual = candidate,
                    PatternScore = patternScore,
                    TextExpected = textExpected,
                    TextActual = textActual,
                    TextScore = textScore
                });
            }

            return hits
                .OrderByDescending(h => h.Score)
                .ThenBy(h => h.Index)
                .ToList();
        }

        private static List<AnchorHit> FindAnchorHitsRegexText(
            List<TokenInfo> tokens,
            string? anchorText,
            int start,
            int end,
            double minScore,
            (string Text, List<int> Starts, List<int> Ends)? cache = null)
        {
            var hits = new List<AnchorHit>();
            if (string.IsNullOrWhiteSpace(anchorText) || tokens.Count == 0)
                return hits;

            var options = ExpandAnchorTextOptions(anchorText);
            if (options.Count > 1)
            {
                foreach (var opt in options)
                    hits.AddRange(FindAnchorHitsRegexText(tokens, opt, start, end, minScore, cache));
                return MergeAnchorHits(hits);
            }

            var anchorNorm = NormalizeAnchorText(anchorText);
            if (string.IsNullOrWhiteSpace(anchorNorm))
                return hits;

            var parts = anchorNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return hits;

            var rxPattern = string.Join(@"(?:\\s+|\\s*[,;:\\-–]\\s*)", parts.Select(Regex.Escape));
            var rx = new Regex(rxPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            var (textNorm, starts, ends) = cache ?? BuildNormalizedTokenText(tokens);
            if (string.IsNullOrWhiteSpace(textNorm))
                return hits;

            int maxStart = Math.Max(start, 0);
            int maxEnd = Math.Min(end, tokens.Count);

            foreach (Match m in rx.Matches(textNorm))
            {
                if (!m.Success)
                    continue;
                var matchStart = m.Index;
                var matchEnd = m.Index + m.Length;
                var startIdx = FindFirstEndGreaterThan(ends, matchStart, maxStart, maxEnd);
                var endIdx = FindLastStartLessThan(starts, matchEnd, maxStart, maxEnd);
                if (startIdx == -1 || endIdx == -1)
                    continue;

                hits.Add(new AnchorHit
                {
                    Index = startIdx,
                    Length = (endIdx - startIdx) + 1,
                    Score = Math.Max(minScore, 0.80),
                    Mode = "regex-anchor",
                    PatternExpected = "",
                    PatternActual = "",
                    PatternScore = 0,
                    TextExpected = anchorNorm,
                    TextActual = textNorm.Substring(matchStart, m.Length),
                    TextScore = Math.Max(minScore, 0.80)
                });
            }

            return hits
                .OrderByDescending(h => h.Score)
                .ThenBy(h => h.Index)
                .ToList();
        }

        private static int FindFirstEndGreaterThan(List<int> ends, int value, int lo, int hi)
        {
            int l = lo;
            int r = hi - 1;
            int ans = -1;
            while (l <= r)
            {
                int mid = l + ((r - l) / 2);
                if (ends[mid] > value)
                {
                    ans = mid;
                    r = mid - 1;
                }
                else
                {
                    l = mid + 1;
                }
            }
            return ans;
        }

        private static int FindLastStartLessThan(List<int> starts, int value, int lo, int hi)
        {
            int l = lo;
            int r = hi - 1;
            int ans = -1;
            while (l <= r)
            {
                int mid = l + ((r - l) / 2);
                if (starts[mid] < value)
                {
                    ans = mid;
                    l = mid + 1;
                }
                else
                {
                    r = mid - 1;
                }
            }
            return ans;
        }

        // Compat: usado apenas por comandos de debug de âncoras.
        private static List<AnchorHit> FindAnchorHits(List<string> tokenPatterns, List<TokenInfo> tokens, List<string> anchorTokens, string? anchorText, int start, int end, double minScore)
        {
            var hits = FindAnchorHitsPattern(tokenPatterns, tokens, anchorTokens, anchorText, start, end, minScore);
            if (hits.Count == 0 && !string.IsNullOrWhiteSpace(anchorText))
                hits = FindAnchorHitsTextOnly(tokens, anchorText, start, end, minScore);
            return hits;
        }

        private static List<AnchorHit> FindAnchorHitsTextOnly(List<TokenInfo> tokens, string? anchorText, int start, int end, double minScore)
        {
            var hits = new List<AnchorHit>();
            if (string.IsNullOrWhiteSpace(anchorText))
                return hits;

            var options = ExpandAnchorTextOptions(anchorText);
            if (options.Count > 1)
            {
                foreach (var opt in options)
                    hits.AddRange(FindAnchorHitsTextOnly(tokens, opt, start, end, minScore));
                return MergeAnchorHits(hits);
            }

            var anchorTextNorm = NormalizeAnchorText(anchorText);
            if (string.IsNullOrWhiteSpace(anchorTextNorm))
                return hits;
            var anchorNoSpace = anchorTextNorm.Replace(" ", "");

            int maxStart = Math.Max(start, 0);
            int maxEnd = Math.Min(end, tokens.Count);
            var parts = anchorTextNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int window = parts.Length > 0 ? parts.Length : 1;

            for (int i = maxStart; i <= maxEnd - window; i++)
            {
                var candText = string.Join(" ", tokens.Skip(i).Take(window).Select(t => t.Text));
                var candNorm = NormalizeAnchorText(candText);
                var score = PatternSimilarity(anchorTextNorm, candNorm);
                var candNoSpace = candNorm.Replace(" ", "");
                // Handle spaced-letter PDFs where anchors collapse into a single token without spaces.
                var noSpaceHit = !string.IsNullOrEmpty(anchorNoSpace) &&
                    candNoSpace.Contains(anchorNoSpace, StringComparison.Ordinal);
                if (noSpaceHit)
                {
                    score = Math.Max(score, 0.99);
                }
                if (score < minScore)
                    continue;
                var hitLength = noSpaceHit ? 1 : window;
                hits.Add(new AnchorHit
                {
                    Index = i,
                    Length = hitLength,
                    Score = score,
                    Mode = noSpaceHit ? "text-only-nospace" : "text-only",
                    PatternExpected = "",
                    PatternActual = "",
                    PatternScore = 0,
                    TextExpected = anchorTextNorm,
                    TextActual = candNorm,
                    TextScore = score
                });
            }

            return hits
                .OrderByDescending(h => h.Score)
                .ThenBy(h => h.Index)
                .ToList();
        }

        private static double CombineScores(double prev, double next, double value, bool hasValue)
        {
            if (!hasValue)
                return (prev + next) / 2.0;
            return (prev * 0.4) + (next * 0.4) + (value * 0.2);
        }

        private static bool TryParseOpRange(string raw, out int start, out int end)
        {
            start = 0;
            end = 0;
            if (string.IsNullOrWhiteSpace(raw))
                return false;
            var parts = raw.Split('-', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1)
            {
                return int.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out start);
            }
            if (parts.Length == 2)
            {
                int.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out start);
                int.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out end);
                if (end == 0) end = start;
                return start > 0 || end > 0;
            }
            return false;
        }

        private static bool InRange(int opIndex, int start, int end)
        {
            if (start <= 0 && end <= 0)
                return true;
            if (start > 0 && end <= 0)
                return opIndex >= start;
            if (start <= 0 && end > 0)
                return opIndex <= end;
            return opIndex >= start && opIndex <= end;
        }

        private static (List<TokenInfo> Tokens, List<string> Patterns, bool Applied) ApplyRoi(List<TokenInfo> tokens, FieldPatternEntry entry)
        {
            if (tokens.Count == 0)
                return (tokens, tokens.Select(t => t.Pattern).ToList(), false);

            var roiSpecified = !string.IsNullOrWhiteSpace(entry.Band) || !string.IsNullOrWhiteSpace(entry.YRange) || !string.IsNullOrWhiteSpace(entry.XRange);
            if (!roiSpecified)
                return (tokens, tokens.Select(t => t.Pattern).ToList(), false);

            var (minY, maxY) = ResolveRoiRange(entry.Band, entry.YRange);
            var (minX, maxX) = ResolveAxisRange(entry.XRange);

            var useY = minY >= 0 && maxY >= 0;
            var useX = minX >= 0 && maxX >= 0;

            var hasY = tokens.Any(t => t.YNorm.HasValue);
            var hasX = tokens.Any(t => t.XNorm.HasValue);
            if (useY && !hasY)
            {
                // fallback: use block order as a rough vertical position (0..1)
                var maxBlock = tokens.Max(t => t.BlockIndex);
                if (maxBlock <= 0)
                    useY = false;
            }
            if (useX && !hasX)
            {
                // no reliable X coordinates -> ignore X ROI
                useX = false;
            }

            if (!useY && !useX)
                return (tokens, tokens.Select(t => t.Pattern).ToList(), roiSpecified);

            var maxBlockIndex = useY && !hasY ? tokens.Max(t => t.BlockIndex) : 0;
            var filtered = tokens
                .Where(t =>
                    (!useY || (((t.YNorm ?? (maxBlockIndex > 0 ? (double)t.BlockIndex / maxBlockIndex : (double?)null)) is double y) && y >= minY && y <= maxY)) &&
                    (!useX || (t.XNorm.HasValue && t.XNorm.Value >= minX && t.XNorm.Value <= maxX)))
                .ToList();

            if (filtered.Count == 0)
            {
                // sem Y disponivel -> ignora ROI
                return (tokens, tokens.Select(t => t.Pattern).ToList(), roiSpecified);
            }

            return (filtered, filtered.Select(t => t.Pattern).ToList(), roiSpecified);
        }

        private static (double Min, double Max) ResolveAxisRange(string? range)
        {
            if (string.IsNullOrWhiteSpace(range))
                return (-1, -1);
            var parts = range.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var a) &&
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var b))
            {
                var min = Math.Min(a, b);
                var max = Math.Max(a, b);
                return (min, max);
            }
            return (-1, -1);
        }

        private static (double Min, double Max) ResolveRoiRange(string? band, string? yRange)
        {
            if (!string.IsNullOrWhiteSpace(yRange))
            {
                var parts = yRange.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var a) &&
                    double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var b))
                {
                    var min = Math.Min(a, b);
                    var max = Math.Max(a, b);
                    return (min, max);
                }
            }

            if (string.IsNullOrWhiteSpace(band))
                return (-1, -1);

            var norm = band.Trim().ToLowerInvariant();
            if (norm == "front")
                return (0.70, 1.00);
            if (norm == "middle" || norm == "mid")
                return (0.35, 0.70);
            if (norm == "back" || norm == "tail")
                return (0.00, 0.35);

            return (-1, -1);
        }

        private static double PatternSimilarity(string expected, string actual)
        {
            if (string.IsNullOrWhiteSpace(expected))
                return 1.0;
            if (string.IsNullOrWhiteSpace(actual))
                return 0.0;
            var dmp = new diff_match_patch();
            var diffs = dmp.diff_main(expected, actual, false);
            dmp.diff_cleanupSemantic(diffs);
            var dist = dmp.diff_levenshtein(diffs);
            var maxLen = Math.Max(expected.Length, actual.Length);
            if (maxLen == 0) return 1.0;
            var score = 1.0 - (double)dist / maxLen;
            LogDmp(expected, actual, score);
            return score;
        }

        // --- ROL helpers (raw op bytes + decoded text) ---

        private static byte[] ExtractStreamBytes(PdfStream stream)
        {
            try { return stream.GetBytes(); } catch { return Array.Empty<byte>(); }
        }

        private static string DequeueDecodedText(string op, List<string> operands, Queue<string> textQueue)
        {
            if (textQueue.Count == 0) return "";
            if (op == "TJ")
            {
                var operandsText = operands.Count > 0 ? string.Join(" ", operands) : "";
                var arrayToken = ExtractArrayToken(operandsText);
                var needed = CountTextChunksInArray(arrayToken);
                if (needed <= 1)
                    return textQueue.Count > 0 ? textQueue.Dequeue() : "";

                var sb = new StringBuilder();
                for (int i = 0; i < needed && textQueue.Count > 0; i++)
                    sb.Append(textQueue.Dequeue());
                return sb.ToString();
            }
            return textQueue.Count > 0 ? textQueue.Dequeue() : "";
        }

        private static bool IsTextShowingOperator(string op)
        {
            return op == "Tj" || op == "TJ" || op == "'" || op == "\"";
        }

        private static List<string> TokenizeContent(byte[] bytes)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c))
                {
                    i++;
                    continue;
                }
                if (c == '%')
                {
                    i = SkipToEol(bytes, i);
                    continue;
                }
                if (c == '(')
                {
                    tokens.Add(ReadLiteralString(bytes, ref i));
                    continue;
                }
                if (c == '<')
                {
                    if (i + 1 < bytes.Length && bytes[i + 1] == '<')
                    {
                        tokens.Add(ReadBalanced(bytes, ref i, "<<", ">>"));
                        continue;
                    }
                    tokens.Add(ReadHexString(bytes, ref i));
                    continue;
                }
                if (c == '[')
                {
                    tokens.Add(ReadArray(bytes, ref i));
                    continue;
                }
                if (c == '/')
                {
                    tokens.Add(ReadName(bytes, ref i));
                    continue;
                }
                tokens.Add(ReadToken(bytes, ref i));
            }
            return tokens;
        }

        private static string ReadToken(byte[] bytes, ref int i)
        {
            int start = i;
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c) || IsDelimiter(c)) break;
                i++;
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadName(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // skip '/'
            while (i < bytes.Length)
            {
                char c = (char)bytes[i];
                if (IsWhite(c) || IsDelimiter(c)) break;
                i++;
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ExtractArrayToken(string operands)
        {
            if (string.IsNullOrWhiteSpace(operands)) return "";
            int start = operands.IndexOf('[');
            int end = operands.LastIndexOf(']');
            if (start >= 0 && end > start)
                return operands.Substring(start, end - start + 1);
            return "";
        }

        private static int CountTextChunksInArray(string? arrayToken)
        {
            if (string.IsNullOrWhiteSpace(arrayToken)) return 0;
            int count = 0;
            int i = 0;
            while (i < arrayToken.Length)
            {
                char c = arrayToken[i];
                if (c == '(')
                {
                    ReadLiteralString(arrayToken, ref i);
                    count++;
                    continue;
                }
                if (c == '<')
                {
                    ReadHexString(arrayToken, ref i);
                    count++;
                    continue;
                }
                i++;
            }
            return count;
        }

        private static string ReadLiteralString(string text, ref int i)
        {
            int start = i;
            i++; // '('
            int depth = 1;
            while (i < text.Length && depth > 0)
            {
                char c = text[i];
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == '(') depth++;
                if (c == ')') depth--;
                i++;
            }
            return text.Substring(start, i - start);
        }

        private static string ReadHexString(string text, ref int i)
        {
            int start = i;
            i++; // '<'
            while (i < text.Length && text[i] != '>') i++;
            if (i < text.Length) i++;
            return text.Substring(start, i - start);
        }

        private static string ReadLiteralString(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '('
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                byte b = bytes[i++];
                if (b == '\\')
                {
                    if (i < bytes.Length) i++;
                    continue;
                }
                if (b == '(') depth++;
                else if (b == ')') depth--;
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadHexString(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '<'
            while (i < bytes.Length && bytes[i] != '>')
                i++;
            if (i < bytes.Length) i++;
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadBalanced(byte[] bytes, ref int i, string startToken, string endToken)
        {
            int start = i;
            i += startToken.Length;
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                if (Match(bytes, i, startToken))
                {
                    depth++;
                    i += startToken.Length;
                    continue;
                }
                if (Match(bytes, i, endToken))
                {
                    depth--;
                    i += endToken.Length;
                    continue;
                }
                i++;
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static string ReadArray(byte[] bytes, ref int i)
        {
            int start = i;
            i++; // '['
            int depth = 1;
            while (i < bytes.Length && depth > 0)
            {
                byte b = bytes[i];
                if (b == '[') depth++;
                else if (b == ']') depth--;
                i++;
            }
            return Encoding.GetEncoding("ISO-8859-1").GetString(bytes, start, i - start);
        }

        private static bool Match(byte[] bytes, int idx, string token)
        {
            if (idx + token.Length > bytes.Length) return false;
            for (int j = 0; j < token.Length; j++)
                if (bytes[idx + j] != token[j]) return false;
            return true;
        }

        private static string Colorize(string text, string colorCode)
        {
            if (Console.IsOutputRedirected)
                return text;
            return $"\u001b[{colorCode}m{text}\u001b[0m";
        }

        private sealed class PatternFindDefaults
        {
            public string? Patterns { get; set; }
            public double? MinScore { get; set; }
            public int? Top { get; set; }
            public int? Pair { get; set; }
            public int? MaxPairs { get; set; }
            public int? MaxCandidates { get; set; }
            public bool? Clean { get; set; }
            public bool? Explain { get; set; }
            public bool? UseRaw { get; set; }
        }

        private sealed class PatternMatchDefaults
        {
            public string? Patterns { get; set; }
            public double? MinScore { get; set; }
            public int? Limit { get; set; }
            public int? MaxPairs { get; set; }
            public int? MaxCandidates { get; set; }
            public double? MinStreamRatio { get; set; }
            public bool? Log { get; set; }
            public double? TimeoutSec { get; set; }
            public int? Jobs { get; set; }
        }

        private static PatternFindDefaults LoadPatternFindDefaults()
        {
            var defaults = new PatternFindDefaults();
            var path = Path.Combine("configs", "operpdf.json");
            if (!File.Exists(path))
                return defaults;

            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (!TryGetPropertyIgnoreCase(doc.RootElement, "pattern_find", out var node) &&
                    !TryGetPropertyIgnoreCase(doc.RootElement, "patternFind", out node))
                    return defaults;

                if (TryGetPropertyIgnoreCase(node, "patterns", out var v) && v.ValueKind == JsonValueKind.String)
                    defaults.Patterns = v.GetString();
                if (TryGetPropertyIgnoreCase(node, "min_score", out v) || TryGetPropertyIgnoreCase(node, "minScore", out v))
                    if (v.ValueKind == JsonValueKind.Number) defaults.MinScore = v.GetDouble();
                if (TryGetPropertyIgnoreCase(node, "top", out v) && v.ValueKind == JsonValueKind.Number)
                    defaults.Top = v.GetInt32();
                if (TryGetPropertyIgnoreCase(node, "pair", out v))
                {
                    if (v.ValueKind == JsonValueKind.Number)
                        defaults.Pair = v.GetInt32();
                    else if (v.ValueKind == JsonValueKind.True)
                        defaults.Pair = 2;
                    else if (v.ValueKind == JsonValueKind.False)
                        defaults.Pair = 0;
                }
                if (TryGetPropertyIgnoreCase(node, "max_pairs", out v) || TryGetPropertyIgnoreCase(node, "maxPairs", out v))
                    if (v.ValueKind == JsonValueKind.Number) defaults.MaxPairs = v.GetInt32();
                if (TryGetPropertyIgnoreCase(node, "max_candidates", out v) || TryGetPropertyIgnoreCase(node, "maxCandidates", out v))
                    if (v.ValueKind == JsonValueKind.Number) defaults.MaxCandidates = v.GetInt32();
                if (TryGetPropertyIgnoreCase(node, "clean", out v) &&
                    (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                    defaults.Clean = v.GetBoolean();
                if (TryGetPropertyIgnoreCase(node, "explain", out v) &&
                    (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                    defaults.Explain = v.GetBoolean();
                if (TryGetPropertyIgnoreCase(node, "raw", out v) &&
                    (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                    defaults.UseRaw = v.GetBoolean();
            }
            catch
            {
                return defaults;
            }

            return defaults;
        }

        private static PatternMatchDefaults LoadPatternMatchDefaults()
        {
            var defaults = new PatternMatchDefaults();
            var path = Path.Combine("configs", "operpdf.json");
            if (!File.Exists(path))
                return defaults;

            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (!TryGetPropertyIgnoreCase(doc.RootElement, "pattern_match", out var node) &&
                    !TryGetPropertyIgnoreCase(doc.RootElement, "patternMatch", out node))
                    return defaults;

                if (TryGetPropertyIgnoreCase(node, "patterns", out var v) && v.ValueKind == JsonValueKind.String)
                    defaults.Patterns = v.GetString();
                if (TryGetPropertyIgnoreCase(node, "min_score", out v) || TryGetPropertyIgnoreCase(node, "minScore", out v))
                    if (v.ValueKind == JsonValueKind.Number) defaults.MinScore = v.GetDouble();
                if (TryGetPropertyIgnoreCase(node, "limit", out v) && v.ValueKind == JsonValueKind.Number)
                    defaults.Limit = v.GetInt32();
                if (TryGetPropertyIgnoreCase(node, "max_pairs", out v) || TryGetPropertyIgnoreCase(node, "maxPairs", out v))
                    if (v.ValueKind == JsonValueKind.Number) defaults.MaxPairs = v.GetInt32();
                if (TryGetPropertyIgnoreCase(node, "max_candidates", out v) || TryGetPropertyIgnoreCase(node, "maxCandidates", out v))
                    if (v.ValueKind == JsonValueKind.Number) defaults.MaxCandidates = v.GetInt32();
                if (TryGetPropertyIgnoreCase(node, "min_stream_ratio", out v) || TryGetPropertyIgnoreCase(node, "minStreamRatio", out v))
                    if (v.ValueKind == JsonValueKind.Number) defaults.MinStreamRatio = v.GetDouble();
                if (TryGetPropertyIgnoreCase(node, "log", out v) &&
                    (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                    defaults.Log = v.GetBoolean();
                if (TryGetPropertyIgnoreCase(node, "timeout_sec", out v) || TryGetPropertyIgnoreCase(node, "timeoutSec", out v))
                    if (v.ValueKind == JsonValueKind.Number) defaults.TimeoutSec = v.GetDouble();
                if (TryGetPropertyIgnoreCase(node, "jobs", out v) || TryGetPropertyIgnoreCase(node, "threads", out v))
                    if (v.ValueKind == JsonValueKind.Number) defaults.Jobs = v.GetInt32();
            }
            catch
            {
                return defaults;
            }

            return defaults;
        }

        private static string ResolvePatternPath(string? patternsPath)
        {
            if (string.IsNullOrWhiteSpace(patternsPath))
                return "";
            if (File.Exists(patternsPath))
                return patternsPath;
            // registry direct lookup
            var regDirect = Obj.Utils.PatternRegistry.FindFile("patterns", patternsPath);
            if (!string.IsNullOrWhiteSpace(regDirect))
                return regDirect;
            if (!Path.HasExtension(patternsPath))
            {
                var regByName = Obj.Utils.PatternRegistry.FindFile("patterns", patternsPath + ".json");
                if (!string.IsNullOrWhiteSpace(regByName))
                    return regByName;
            }
            // compat: map old paths like configs/patterns/NAME.json to registry by basename
            var baseName = Path.GetFileName(patternsPath);
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                var regByBase = Obj.Utils.PatternRegistry.FindFile("patterns", baseName);
                if (!string.IsNullOrWhiteSpace(regByBase))
                    return regByBase;
                if (!Path.HasExtension(baseName))
                {
                    var regByBaseName = Obj.Utils.PatternRegistry.FindFile("patterns", baseName + ".json");
                    if (!string.IsNullOrWhiteSpace(regByBaseName))
                        return regByBaseName;
                }
                else
                {
                    var stem = Path.GetFileNameWithoutExtension(baseName);
                    if (!string.IsNullOrWhiteSpace(stem))
                    {
                        var regByStem = Obj.Utils.PatternRegistry.FindFile("patterns", stem + ".json");
                        if (!string.IsNullOrWhiteSpace(regByStem))
                            return regByStem;
                    }
                }
            }
            var cfg = Path.Combine("configs", "operpdf.json");
            if (!File.Exists(cfg))
                return patternsPath;
            try
            {
                var json = File.ReadAllText(cfg);
                using var doc = JsonDocument.Parse(json);
                if (!TryGetPropertyIgnoreCase(doc.RootElement, "patterns", out var node) ||
                    node.ValueKind != JsonValueKind.Object)
                    return patternsPath;
                foreach (var prop in node.EnumerateObject())
                {
                    if (string.Equals(prop.Name, patternsPath, StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var v = prop.Value.GetString();
                        if (string.IsNullOrWhiteSpace(v))
                            return patternsPath;
                        if (File.Exists(v))
                            return v;
                        var regMapped = Obj.Utils.PatternRegistry.FindFile("patterns", v);
                        if (!string.IsNullOrWhiteSpace(regMapped))
                            return regMapped;
                        if (!Path.HasExtension(v))
                        {
                            var regMappedByName = Obj.Utils.PatternRegistry.FindFile("patterns", v + ".json");
                            if (!string.IsNullOrWhiteSpace(regMappedByName))
                                return regMappedByName;
                        }
                        return v;
                    }
                }
            }
            catch
            {
                return patternsPath;
            }
            return patternsPath;
        }

        private static int SkipToEol(byte[] bytes, int i)
        {
            while (i < bytes.Length)
            {
                byte b = bytes[i++];
                if (b == '\n' || b == '\r') break;
            }
            return i;
        }

        private static bool IsWhite(char c)
        {
            return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';
        }

        private static bool IsDelimiter(char c)
        {
            return c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';
        }

        private static bool IsOperatorToken(string token)
        {
            return Operators.Contains(token);
        }

        private static readonly HashSet<string> Operators = new HashSet<string>
        {
            "q","Q","cm","w","J","j","M","d","ri","i","gs",
            "m","l","c","v","y","h","re","rg","RG","S","s","f","F","f*","B","B*","b","b*","n",
            "W","W*",
            "BT","ET","Tc","Tw","Tz","TL","Tf","Tr","Ts",
            "Td","TD","Tm","T*","Tj","TJ","'","\"",
            "Do","BI","ID","EI"
        };

        private static (PdfStream? Stream, PdfResources? Resources) FindStreamAndResourcesByObjId(PdfDocument doc, int objId)
        {
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

        private static IEnumerable<PdfStream> EnumerateStreams(PdfObject? obj)
        {
            if (obj == null) yield break;
            if (obj is PdfStream s)
            {
                yield return s;
                yield break;
            }
            if (obj is PdfArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is PdfStream ss) yield return ss;
                }
            }
        }

        private static void DecodePattern(string pattern, string kind)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return;
            var useP1 = string.Equals(kind, "p1", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine("DECODE (" + (useP1 ? "P1" : "PT") + "):");
            var sb = new StringBuilder();
            int i = 0;
            while (i < pattern.Length)
            {
                var ch = pattern[i];
                if (ch == '|')
                {
                    sb.Append(" | ");
                    i++;
                    continue;
                }
                if (ch == ' ')
                {
                    sb.Append(' ');
                    i++;
                    continue;
                }

            if (useP1)
            {
                var tokens = ParsePatternTokens(pattern);
                foreach (var tok in tokens)
                {
                    sb.Append(DecodeP1Token(tok));
                    sb.Append(' ');
                }
                Console.WriteLine(sb.ToString().Trim());
                return;
            }

                // PT: base + optional ordinal + optional size + optional colon
                var baseCode = ch;
                i++;
                char? ord = null;
                char? size = null;
                if (i < pattern.Length && char.IsDigit(pattern[i]))
                {
                    size = pattern[i];
                    i++;
                }
                if (baseCode == 'N' && i < pattern.Length && (pattern[i] == 'a' || pattern[i] == 'o'))
                {
                    ord = pattern[i];
                    i++;
                }
                bool hasColon = false;
                if (i < pattern.Length && pattern[i] == ':')
                {
                    hasColon = true;
                    i++;
                }

                sb.Append(DecodePt(baseCode, ord));
                if (size != null)
                    sb.Append($"(len={size})");
                if (hasColon)
                    sb.Append("[:]");
                sb.Append(' ');
            }

            Console.WriteLine(sb.ToString().Trim());
        }

        private static void RunDecodeFile(DecodeFileOptions options)
        {
            if (!File.Exists(options.Input))
            {
                Console.WriteLine("Arquivo nao encontrado: " + options.Input);
                return;
            }

            int lineNo = 0;
            foreach (var line in File.ReadLines(options.Input))
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (TryExtractPattern(line, "PAT_RAW:", out var rawPat))
                {
                    Console.WriteLine($"LINE {lineNo} PAT_RAW: {rawPat}");
                    DecodePattern(rawPat, "p1");
                }

                if (TryExtractPattern(line, "PAT_NORM:", out var normPat))
                {
                    Console.WriteLine($"LINE {lineNo} PAT_NORM: {normPat}");
                    DecodePattern(normPat, "pt");
                }
            }
        }

        private static bool TryExtractPattern(string line, string marker, out string pattern)
        {
            pattern = "";
            var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            var rest = line.Substring(idx + marker.Length).Trim();
            var pipeIdx = rest.IndexOf('|');
            if (pipeIdx >= 0)
                rest = rest.Substring(0, pipeIdx).Trim();
            if (string.IsNullOrWhiteSpace(rest)) return false;
            pattern = rest;
            return true;
        }

        private static string DecodeP1Token(string tok)
        {
            return tok switch
            {
                "W" => "[W token>=2]",
                "1" => "[1 token]",
                ":" => "[:]",
                _ => $"[{tok}]"
            };
        }

        private static string DecodePt(char ch, char? ord = null)
        {
            return ch switch
            {
                'U' => "[MAIUSCULO]",
                'L' => "[minusculo]",
                'T' => "[TitleCase]",
                'P' => "[particula]",
                'N' => ord == null ? "[digitos]" : (ord == 'a' ? "[ordinal-fem]" : "[ordinal-masc]"),
                'R' => "[marcador-numero]",
                'F' => "[CPF]",
                'J' => "[CNPJ]",
                'Q' => "[CNJ]",
                'V' => "[valor]",
                'A' => "[data]",
                'E' => "[email]",
                'M' => "[misto]",
                'S' => "[simbolo]",
                '1' => "[1 char]",
                ':' => "[:]",
                _ => $"[{ch}]"
            };
        }

        private static void WriteStackedOutput(string aPath, List<ObjectsTextOpsDiff.AlignDebugReport> reports, string outPath)
        {
            var baseA = Path.GetFileNameWithoutExtension(aPath);
            if (string.IsNullOrWhiteSpace(outPath))
                outPath = Path.Combine("outputs", "align_ranges", $"{baseA}__STACK__textops_align.txt");

            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");

            var lines = new List<string>();
            var blocksA = reports[0].BlocksA;
            lines.Add($"MODEL: {Path.GetFileName(aPath)}");
            lines.Add($"TARGETS: {string.Join(" | ", reports.Select(r => r.PdfB))}");
            lines.Add("");

            for (int i = 0; i < blocksA.Count; i++)
            {
                var aBlock = blocksA[i];
                lines.Add($"[{i + 1:D3}] A({aBlock.Index}) op{aBlock.StartOp}-{aBlock.EndOp} | {aBlock.Text}");

                foreach (var report in reports)
                {
                    var match = report.Alignments.FirstOrDefault(p => p.AIndex == i);
                    var bText = match?.B?.Text ?? "<gap>";
                    var bIndex = match?.B != null ? match.B.Index.ToString(CultureInfo.InvariantCulture) : "-";
                    var kind = match?.Kind ?? "gap";
                    lines.Add($"    {report.PdfB} | B({bIndex}) | {kind} | {bText}");
                }

                lines.Add("");
            }

            File.WriteAllLines(outPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine(string.Join(Environment.NewLine, lines));
            Console.WriteLine("Arquivo salvo: " + outPath);
        }
    }
}
