using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using PdfTextExtraction = Obj.Commands.PdfTextExtraction;

namespace Obj.Align
{
    internal static partial class ObjectsTextOpsDiff
    {
        internal sealed class TemplateFieldMap
        {
            public string Doc { get; set; } = "";
            public List<string> Ops { get; set; } = new List<string>();
            public Dictionary<string, TemplateFieldDef> Fields { get; set; } = new Dictionary<string, TemplateFieldDef>(StringComparer.OrdinalIgnoreCase);
        }

        internal sealed class TemplateFieldDef
        {
            public string Band { get; set; } = "front_head";
            public List<string> Patterns { get; set; } = new List<string>();
            public string? ValueRegex { get; set; }
            public List<TemplateRegexRule> Regex { get; set; } = new List<TemplateRegexRule>();
        }

        internal sealed class TemplateRegexRule
        {
            public string Pattern { get; set; } = "";
            public int Group { get; set; } = 1;
        }

        internal sealed class TemplateFieldResult
        {
            public string Value { get; set; } = "";
            public string ValueFull { get; set; } = "";
            public string ValueRaw { get; set; } = "";
            public string Status { get; set; } = "";
            public string OpRange { get; set; } = "";
            public string Source { get; set; } = "";
            public int Obj { get; set; }
            public TemplateFieldBoundingBox? BBox { get; set; }
        }

        internal sealed class TemplateFieldBoundingBox
        {
            public double X0 { get; set; }
            public double Y0 { get; set; }
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public int Items { get; set; }
        }

        internal static TemplateFieldMap? LoadTemplateFieldMap(string path, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                return deserializer.Deserialize<TemplateFieldMap>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        internal static Dictionary<string, TemplateFieldResult> ExtractTemplateFields(
            string pdfPath,
            int objId,
            int startOp,
            int endOp,
            TemplateFieldMap map,
            string band,
            out string error)
        {
            error = "";
            var output = new Dictionary<string, TemplateFieldResult>(StringComparer.OrdinalIgnoreCase);
            if (map?.Fields == null || map.Fields.Count == 0)
                return output;

            foreach (var kv in map.Fields)
            {
                var fieldName = kv.Key;
                var def = kv.Value;
                if (!string.Equals(NormalizeBand(def?.Band), NormalizeBand(band), StringComparison.OrdinalIgnoreCase))
                    continue;
                output[fieldName] = new TemplateFieldResult
                {
                    Status = "NOT_FOUND",
                    Source = band,
                    Obj = objId
                };
            }

            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath) || objId <= 0)
            {
                error = "invalid_input";
                return output;
            }

            var opFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (map.Ops != null)
            {
                foreach (var op in map.Ops)
                {
                    if (!string.IsNullOrWhiteSpace(op))
                        opFilter.Add(op.Trim());
                }
            }
            if (opFilter.Count == 0)
            {
                opFilter.Add("Tj");
                opFilter.Add("TJ");
            }

            try
            {
                using var doc = new PdfDocument(new PdfReader(pdfPath));
                var found = FindStreamAndResourcesByObjId(doc, objId);
                if (found.Stream == null || found.Resources == null)
                {
                    error = "stream_not_found";
                    return output;
                }

                var full = ExtractFullTextWithOps(found.Stream, found.Resources, opFilter,
                    includeLineBreaks: true,
                    includeTdLineBreaks: true,
                    includeTmLineBreaks: true,
                    lineBreakAsSpace: true);

                if (startOp > 0 && endOp > 0)
                    full = SliceFullTextByOpRange(full, startOp, endOp);

                var text = full.Text ?? "";
                if (string.IsNullOrWhiteSpace(text))
                    return output;

                var entries = CollectTextOpEntriesLite(found.Stream, found.Resources, opFilter);

                foreach (var kv in map.Fields)
                {
                    var fieldName = kv.Key;
                    var def = kv.Value;
                    if (!string.Equals(NormalizeBand(def?.Band), NormalizeBand(band), StringComparison.OrdinalIgnoreCase))
                        continue;

                    var result = output[fieldName];
                    var matched = false;

                    if (def?.Patterns != null && def.Patterns.Count > 0)
                    {
                        foreach (var pattern in def.Patterns)
                        {
                            if (string.IsNullOrWhiteSpace(pattern))
                                continue;

                            var regex = BuildPatternRegex(pattern, def.ValueRegex);
                            if (regex == null)
                                continue;

                            var match = regex.Match(text);
                            if (!match.Success)
                                continue;

                            var group = match.Groups["val"];
                            var value = group.Success ? group.Value : match.Value;
                            var matchStart = match.Index;
                            var matchLen = match.Length;

                            if (group.Success)
                            {
                                matchStart = group.Index;
                                matchLen = group.Length;
                            }

                            if (!TryFindOpRange(full.OpIndexes, matchStart, matchLen, out var start, out var end))
                                continue;

                            var opName = FindOpName(full.OpNames, matchStart, matchLen) ?? "";
                            var bbox = BuildBoundingBox(entries, start, end);

                            result.Status = "OK";
                            result.ValueFull = match.Value;
                            result.ValueRaw = value;
                            result.Value = value;
                            result.OpRange = FormatOpRange(start, end, opName);
                            result.BBox = bbox;
                            output[fieldName] = result;
                            matched = true;
                            break;
                        }
                    }

                    if (matched)
                        continue;

                    if (def?.Regex == null || def.Regex.Count == 0)
                        continue;

                    foreach (var rule in def.Regex)
                    {
                        if (rule == null || string.IsNullOrWhiteSpace(rule.Pattern))
                            continue;

                        var regex = new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                        var match = regex.Match(text);
                        if (!match.Success)
                            continue;

                        var groupIndex = rule.Group;
                        if (groupIndex < 0)
                            groupIndex = 0;
                        if (groupIndex >= match.Groups.Count)
                            groupIndex = 0;

                        var group = match.Groups[groupIndex];
                        var value = group.Success ? group.Value : match.Value;
                        var matchStart = group.Success ? group.Index : match.Index;
                        var matchLen = group.Success ? group.Length : match.Length;

                        if (!TryFindOpRange(full.OpIndexes, matchStart, matchLen, out var start, out var end))
                            continue;

                        var opName = FindOpName(full.OpNames, matchStart, matchLen) ?? "";
                        var bbox = BuildBoundingBox(entries, start, end);

                        result.Status = "OK";
                        result.ValueFull = match.Value;
                        result.ValueRaw = value;
                        result.Value = value;
                        result.OpRange = FormatOpRange(start, end, opName);
                        result.BBox = bbox;
                        output[fieldName] = result;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            return output;
        }

        private static string NormalizeBand(string? band)
        {
            return string.IsNullOrWhiteSpace(band) ? "" : band.Trim().ToLowerInvariant();
        }

        private static Regex? BuildPatternRegex(string pattern, string? valueRegex)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return null;

            var sb = new StringBuilder();
            var token = "{{value}}";
            for (int i = 0; i < pattern.Length; i++)
            {
                if (pattern.AsSpan(i).StartsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    var val = string.IsNullOrWhiteSpace(valueRegex) ? ".+?" : valueRegex;
                    sb.Append("(?<val>");
                    sb.Append(val);
                    sb.Append(")");
                    i += token.Length - 1;
                    continue;
                }

                var ch = pattern[i];
                if (char.IsWhiteSpace(ch))
                {
                    sb.Append("\\s+");
                    continue;
                }

                sb.Append(Regex.Escape(ch.ToString()));
            }

            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        private static bool TryFindOpRange(List<int> opIndexes, int start, int length, out int startOp, out int endOp)
        {
            startOp = 0;
            endOp = 0;
            if (opIndexes == null || opIndexes.Count == 0 || length <= 0)
                return false;
            int end = Math.Min(opIndexes.Count, start + length);
            if (start < 0 || start >= end)
                return false;

            for (int i = start; i < end; i++)
            {
                if (opIndexes[i] > 0)
                {
                    startOp = opIndexes[i];
                    break;
                }
            }

            for (int i = end - 1; i >= start; i--)
            {
                if (opIndexes[i] > 0)
                {
                    endOp = opIndexes[i];
                    break;
                }
            }

            return startOp > 0 && endOp >= startOp;
        }

        private static string? FindOpName(List<string> opNames, int start, int length)
        {
            if (opNames == null || opNames.Count == 0 || length <= 0)
                return null;
            int end = Math.Min(opNames.Count, start + length);
            if (start < 0 || start >= end)
                return null;
            for (int i = start; i < end; i++)
            {
                var name = opNames[i];
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
            return null;
        }

        private sealed class TextOpEntryLite
        {
            public int OpIndex { get; }
            public double X0 { get; }
            public double X1 { get; }
            public double Y0 { get; }
            public double Y1 { get; }
            public bool HasBox { get; }

            public TextOpEntryLite(int opIndex, double x0, double x1, double y0, double y1, bool hasBox)
            {
                OpIndex = opIndex;
                X0 = x0;
                X1 = x1;
                Y0 = y0;
                Y1 = y1;
                HasBox = hasBox;
            }
        }

        private static List<TextOpEntryLite> CollectTextOpEntriesLite(PdfStream stream, PdfResources resources, HashSet<string> opFilter)
        {
            var entries = new List<TextOpEntryLite>();
            if (stream == null || resources == null)
                return entries;

            var bytes = ExtractStreamBytes(stream);
            if (bytes.Length == 0)
                return entries;

            var tokens = TokenizeContent(bytes);
            var textQueue = new Queue<PdfTextExtraction.TextOpItem>(PdfTextExtraction.CollectTextOperatorItems(stream, resources));
            int index = 0;

            foreach (var tok in tokens)
            {
                if (!IsOperatorToken(tok))
                    continue;

                if (IsTextShowingOperator(tok))
                {
                    var item = textQueue.Count > 0 ? textQueue.Dequeue() : new PdfTextExtraction.TextOpItem("", 0, 0, 0, 0, false);
                    if (IsTextOpAllowed(tok, opFilter))
                    {
                        index++;
                        entries.Add(new TextOpEntryLite(index, item.X0, item.X1, item.Y0, item.Y1, item.HasBox));
                    }
                }
            }

            return entries;
        }

        private static TemplateFieldBoundingBox? BuildBoundingBox(List<TextOpEntryLite> entries, int start, int end)
        {
            if (entries.Count == 0 || start <= 0 || end < start)
                return null;

            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;
            int count = 0;

            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.OpIndex < start || e.OpIndex > end)
                    continue;
                if (!e.HasBox)
                    continue;
                minX = Math.Min(minX, Math.Min(e.X0, e.X1));
                maxX = Math.Max(maxX, Math.Max(e.X0, e.X1));
                minY = Math.Min(minY, Math.Min(e.Y0, e.Y1));
                maxY = Math.Max(maxY, Math.Max(e.Y0, e.Y1));
                count++;
            }

            if (count == 0)
                return null;

            return new TemplateFieldBoundingBox
            {
                X0 = minX,
                Y0 = minY,
                X1 = maxX,
                Y1 = maxY,
                StartOp = start,
                EndOp = end,
                Items = count
            };
        }

        internal static TemplateFieldBoundingBox? TryBuildOpRangeBoundingBox(
            string pdfPath,
            int objId,
            int startOp,
            int endOp,
            HashSet<string>? opFilter,
            out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath) || objId <= 0 || startOp <= 0 || endOp <= 0)
                return null;

            if (endOp < startOp)
                (startOp, endOp) = (endOp, startOp);

            var filter = opFilter ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (filter.Count == 0)
            {
                filter.Add("Tj");
                filter.Add("TJ");
            }

            try
            {
                using var doc = new PdfDocument(new PdfReader(pdfPath));
                var found = FindStreamAndResourcesByObjId(doc, objId);
                if (found.Stream == null || found.Resources == null)
                {
                    error = "stream_not_found";
                    return null;
                }

                var entries = CollectTextOpEntriesLite(found.Stream, found.Resources, filter);
                return BuildBoundingBox(entries, startOp, endOp);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static string FormatOpRange(int start, int end, string? op)
        {
            if (start <= 0 || end <= 0)
                return "";
            var label = string.IsNullOrWhiteSpace(op) ? "" : $"[{op}]";
            return $"op{start}-op{end}{label}";
        }
    }
}
