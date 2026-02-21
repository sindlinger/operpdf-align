using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Obj.Align;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Obj.TjpbDespachoExtractor.Utils;

namespace Obj.Commands
{
    internal static class ObjectsMapFields
    {
        private static readonly string DefaultOutDir = Path.Combine("outputs", "fields");

        public static void Execute(string[] args)
        {
            if (!ParseOptions(args, out var alignrangePath, out var mapPath, out var outDir, out var useFront, out var useBack, out var side))
            {
                ShowHelp();
                return;
            }

            if (string.IsNullOrWhiteSpace(alignrangePath))
            {
                ShowHelp();
                return;
            }

            alignrangePath = ResolveExistingPath(alignrangePath);
            if (!File.Exists(alignrangePath))
            {
                Console.WriteLine("Alignrange nao encontrado: " + alignrangePath);
                return;
            }

            if (string.IsNullOrWhiteSpace(mapPath))
            {
                ShowHelp();
                return;
            }

            mapPath = ResolveMapPath(mapPath);
            if (!File.Exists(mapPath))
            {
                Console.WriteLine("Mapa YAML nao encontrado: " + mapPath);
                return;
            }

            AlignRangeFile? alignFile;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                alignFile = deserializer.Deserialize<AlignRangeFile>(File.ReadAllText(alignrangePath));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Falha ao ler alignrange: " + ex.Message);
                return;
            }

            if (alignFile == null)
            {
                Console.WriteLine("Alignrange vazio: " + alignrangePath);
                return;
            }

            AlignRangeFieldMap? map;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                map = deserializer.Deserialize<AlignRangeFieldMap>(File.ReadAllText(mapPath));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Falha ao ler map YAML: " + ex.Message);
                return;
            }

            if (map == null || map.Fields == null || map.Fields.Count == 0)
            {
                Console.WriteLine("Mapa vazio: " + mapPath);
                return;
            }

            if (!useFront && !useBack)
            {
                useFront = true;
                useBack = true;
            }

            var output = BuildOutput(alignFile, map, useFront, useBack, side, alignrangePath);

            var json = JsonConvert.SerializeObject(output, Formatting.Indented);
            Console.WriteLine(json);

            if (string.IsNullOrWhiteSpace(outDir))
                outDir = DefaultOutDir;
            Directory.CreateDirectory(outDir);

            var baseName = Path.GetFileNameWithoutExtension(alignrangePath);
            var modeSuffix = useFront && useBack ? "both" : useFront ? "front" : "back";
            var sideSuffix = side == MapSide.Both ? "ab" : side == MapSide.A ? "a" : "b";
            var outFile = Path.Combine(outDir, $"{baseName}__mapfields_{modeSuffix}_{sideSuffix}.json");
            File.WriteAllText(outFile, json);
            Console.WriteLine("Arquivo salvo: " + outFile);
        }

        internal sealed class CompactFieldOutput
        {
            public string ValueRaw { get; set; } = "";
            public string Value { get; set; } = "";
            public string Source { get; set; } = "";
            public string Module { get; set; } = "parser";
            public string OpRange { get; set; } = "";
            public int Obj { get; set; }
            public string Status { get; set; } = "";
            public CompactBoundingBox? BBox { get; set; }
        }

        internal sealed class CompactBoundingBox
        {
            public double X0 { get; set; }
            public double Y0 { get; set; }
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public int Items { get; set; }
        }

        internal sealed class CompactSideOutput
        {
            public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, CompactFieldOutput> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        internal sealed class CompactExtractionOutput
        {
            public string MapPath { get; set; } = "";
            public string Doc { get; set; } = "";
            public string Band { get; set; } = "";
            public CompactSideOutput PdfA { get; set; } = new();
            public CompactSideOutput PdfB { get; set; } = new();
        }

        internal static bool TryExtractFromInlineSegments(
            string mapPath,
            string band,
            string valueFullA,
            string valueFullB,
            string opRangeA,
            string opRangeB,
            int objA,
            int objB,
            string pdfPathA,
            string pdfPathB,
            out CompactExtractionOutput? output,
            out string error)
        {
            output = null;
            error = "";
            if (string.IsNullOrWhiteSpace(mapPath))
            {
                error = "map_empty";
                return false;
            }

            var resolvedMapPath = ResolveMapPath(mapPath);
            if (!File.Exists(resolvedMapPath))
            {
                error = "map_not_found";
                return false;
            }

            AlignRangeFieldMap? map;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                map = deserializer.Deserialize<AlignRangeFieldMap>(File.ReadAllText(resolvedMapPath));
            }
            catch (Exception ex)
            {
                error = "map_load_failed:" + ex.Message;
                return false;
            }

            if (map == null || map.Fields == null || map.Fields.Count == 0)
            {
                error = "map_empty";
                return false;
            }

            var normBand = NormalizeBand(band);
            var emptyFront = BuildSegment("front_head", "", "", 0, "");
            var emptyBack = BuildSegment("back_tail", "", "", 0, "");
            var emptySig = BuildSegment("back_signature", "", "", 0, "");

            var segA = BuildSegment(normBand, opRangeA ?? "", valueFullA ?? "", objA, pdfPathA ?? "");
            var segB = BuildSegment(normBand, opRangeB ?? "", valueFullB ?? "", objB, pdfPathB ?? "");
            var isSinglePage = string.Equals(normBand, "single_page", StringComparison.OrdinalIgnoreCase);

            var segmentsA = new Dictionary<string, AlignSegment>(StringComparer.OrdinalIgnoreCase)
            {
                ["front_head"] = (isSinglePage || string.Equals(normBand, "front_head", StringComparison.OrdinalIgnoreCase)) ? segA : emptyFront,
                ["back_tail"] = (isSinglePage || string.Equals(normBand, "back_tail", StringComparison.OrdinalIgnoreCase)) ? segA : emptyBack,
                ["back_signature"] = (isSinglePage || string.Equals(normBand, "back_signature", StringComparison.OrdinalIgnoreCase)) ? segA : emptySig
            };

            var segmentsB = new Dictionary<string, AlignSegment>(StringComparer.OrdinalIgnoreCase)
            {
                ["front_head"] = (isSinglePage || string.Equals(normBand, "front_head", StringComparison.OrdinalIgnoreCase)) ? segB : emptyFront,
                ["back_tail"] = (isSinglePage || string.Equals(normBand, "back_tail", StringComparison.OrdinalIgnoreCase)) ? segB : emptyBack,
                ["back_signature"] = (isSinglePage || string.Equals(normBand, "back_signature", StringComparison.OrdinalIgnoreCase)) ? segB : emptySig
            };

            var allowedBands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (isSinglePage)
            {
                allowedBands.Add("front_head");
                allowedBands.Add("back_tail");
                allowedBands.Add("back_signature");
            }
            else
            {
                allowedBands.Add(normBand);
            }

            var fieldsA = ExtractFieldsForSide(map, segmentsA, allowedBands, "pdf_a");
            var fieldsB = ExtractFieldsForSide(map, segmentsB, allowedBands, "pdf_b");
            ApplyDerivedFields(fieldsA);
            ApplyDerivedFields(fieldsB);

            output = new CompactExtractionOutput
            {
                MapPath = Path.GetFullPath(resolvedMapPath),
                Doc = map.Doc ?? "",
                Band = normBand,
                PdfA = ToCompactSideOutput(fieldsA),
                PdfB = ToCompactSideOutput(fieldsB)
            };

            return true;
        }

        private static CompactSideOutput ToCompactSideOutput(Dictionary<string, FieldOutput> fields)
        {
            var side = new CompactSideOutput();
            foreach (var kv in fields)
            {
                var name = kv.Key;
                var field = kv.Value;
                var value = field?.Value ?? "";
                side.Values[name] = value;
                side.Fields[name] = new CompactFieldOutput
                {
                    ValueRaw = field?.ValueRaw ?? "",
                    Value = value,
                    Source = field?.Source ?? "",
                    Module = "parser",
                    OpRange = field?.OpRange ?? "",
                    Obj = field?.Obj ?? 0,
                    Status = string.IsNullOrWhiteSpace(value) ? "NOT_FOUND" : "OK",
                    BBox = field?.BBox == null
                        ? null
                        : new CompactBoundingBox
                        {
                            X0 = field.BBox.X0,
                            Y0 = field.BBox.Y0,
                            X1 = field.BBox.X1,
                            Y1 = field.BBox.Y1,
                            StartOp = field.BBox.StartOp,
                            EndOp = field.BBox.EndOp,
                            Items = field.BBox.Items
                        }
                };
            }
            return side;
        }

        private static object BuildOutput(AlignRangeFile alignFile, AlignRangeFieldMap map, bool useFront, bool useBack, MapSide side, string alignrangePath)
        {
            var front = alignFile.FrontHead;
            var back = alignFile.BackTail;
            var backSig = alignFile.BackSignature;

            var frontA = BuildSegment(
                "front_head",
                front?.OpRangeA,
                front?.ValueFullA,
                front?.ObjA ?? 0,
                ResolvePdfPath(front?.PdfAPath, front?.PdfA, alignrangePath));
            var frontB = BuildSegment(
                "front_head",
                front?.OpRangeB,
                front?.ValueFullB,
                front?.ObjB ?? 0,
                ResolvePdfPath(front?.PdfBPath, front?.PdfB, alignrangePath));
            var backA = BuildSegment(
                "back_tail",
                back?.OpRangeA,
                back?.ValueFullA,
                back?.ObjA ?? 0,
                ResolvePdfPath(back?.PdfAPath, back?.PdfA, alignrangePath));
            var backB = BuildSegment(
                "back_tail",
                back?.OpRangeB,
                back?.ValueFullB,
                back?.ObjB ?? 0,
                ResolvePdfPath(back?.PdfBPath, back?.PdfB, alignrangePath));
            var sigA = BuildSegment(
                "back_signature",
                backSig?.OpRangeA,
                backSig?.ValueFullA,
                backSig?.ObjA ?? 0,
                ResolvePdfPath(backSig?.PdfAPath, backSig?.PdfA, alignrangePath));
            var sigB = BuildSegment(
                "back_signature",
                backSig?.OpRangeB,
                backSig?.ValueFullB,
                backSig?.ObjB ?? 0,
                ResolvePdfPath(backSig?.PdfBPath, backSig?.PdfB, alignrangePath));

            var segmentsA = new Dictionary<string, AlignSegment>
            {
                ["front_head"] = frontA,
                ["back_tail"] = backA,
                ["back_signature"] = sigA
            };
            var segmentsB = new Dictionary<string, AlignSegment>
            {
                ["front_head"] = frontB,
                ["back_tail"] = backB,
                ["back_signature"] = sigB
            };

            var filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (useFront) filter.Add("front_head");
            if (useBack)
            {
                filter.Add("back_tail");
                filter.Add("back_signature");
            }

            if (side == MapSide.A)
            {
                var result = ExtractFieldsForSide(map, segmentsA, filter, "pdf_a");
                ApplyDerivedFields(result);
                return result;
            }
            if (side == MapSide.B)
            {
                var result = ExtractFieldsForSide(map, segmentsB, filter, "pdf_b");
                ApplyDerivedFields(result);
                return result;
            }

            var output = new Dictionary<string, object>
            {
                ["pdf_a"] = ExtractFieldsForSide(map, segmentsA, filter, "pdf_a"),
                ["pdf_b"] = ExtractFieldsForSide(map, segmentsB, filter, "pdf_b")
            };

            if (output.TryGetValue("pdf_a", out var aObj) && aObj is Dictionary<string, FieldOutput> aFields)
                ApplyDerivedFields(aFields);
            if (output.TryGetValue("pdf_b", out var bObj) && bObj is Dictionary<string, FieldOutput> bFields)
                ApplyDerivedFields(bFields);

            return output;
        }

        private static Dictionary<string, FieldOutput> ExtractFieldsForSide(
            AlignRangeFieldMap map,
            Dictionary<string, AlignSegment> segments,
            HashSet<string> allowedBands,
            string label)
        {
            var output = new Dictionary<string, FieldOutput>(StringComparer.OrdinalIgnoreCase);

            var nlpCache = new Dictionary<string, List<NlpEntity>>(StringComparer.OrdinalIgnoreCase);
            foreach (var band in segments.Keys)
            {
                var seg = segments[band];
                if (!allowedBands.Contains(band))
                    continue;
                if (seg == null || string.IsNullOrWhiteSpace(seg.ValueFull))
                    continue;
                nlpCache[band] = NlpLite.Annotate(seg.WorkText);
            }

            foreach (var kv in map.Fields)
            {
                var fieldName = kv.Key;
                var field = kv.Value;
                var result = new FieldOutput();

                var sources = field?.Sources?.Count > 0
                    ? field.Sources
                    : new List<AlignRangeSource> { new AlignRangeSource { Band = "front_head" } };

                string? chosenValue = null;
                string? chosenValueRaw = null;
                string? chosenValueFull = null;
                string? chosenBand = null;
                AlignSegment? chosenSeg = null;

                foreach (var source in sources)
                {
                    var band = NormalizeBand(source.Band);
                    if (!allowedBands.Contains(band))
                        continue;

                    if (!segments.TryGetValue(band, out var seg))
                        continue;

                    if (seg == null || string.IsNullOrWhiteSpace(seg.ValueFull))
                    {
                        Console.WriteLine($"[{label}] Sem ValueFull em {band} para {fieldName}.");
                        if (chosenValueFull == null)
                        {
                            chosenValueFull = seg?.ValueFull ?? "";
                            chosenBand = band;
                            chosenSeg = seg;
                        }
                        continue;
                    }

                    if (chosenValueFull == null)
                    {
                        chosenValueFull = seg.ValueFull;
                        chosenBand = band;
                        chosenSeg = seg;
                    }

                    if (TryExtractFromNlp(source, nlpCache.GetValueOrDefault(band), out var nlpValue))
                    {
                        chosenValue = nlpValue;
                        chosenValueRaw = nlpValue;
                        chosenValueFull = seg.ValueFull;
                        chosenBand = band;
                        chosenSeg = seg;
                        break;
                    }

                    if (TryExtractFromRegex(source, seg.WorkText, out var rxValue)
                        || (!string.IsNullOrWhiteSpace(seg.RawText) && seg.RawText != seg.WorkText
                            && TryExtractFromRegex(source, seg.RawText, out rxValue))
                        || (!string.IsNullOrWhiteSpace(seg.ValueFull) && seg.ValueFull != seg.WorkText
                            && TryExtractFromRegex(source, seg.ValueFull, out rxValue)))
                    {
                        chosenValue = rxValue;
                        chosenValueRaw = rxValue;
                        chosenValueFull = seg.ValueFull;
                        chosenBand = band;
                        chosenSeg = seg;
                        break;
                    }
                }

                result.ValueFull = chosenValueFull ?? "";
                result.ValueRaw = chosenValueRaw ?? "";
                result.Value = NormalizeFieldValue(fieldName, chosenValue ?? "");
                result.Source = chosenBand ?? "";
                result.OpRange = chosenSeg?.OpRange ?? "";
                result.Obj = chosenSeg?.Obj ?? 0;
                result.BBox = chosenSeg?.BBox;
                output[fieldName] = result;
            }

            return output;
        }

        private static AlignSegment BuildSegment(string band, string? opRange, string? valueFull, int obj, string pdfPath)
        {
            var raw = valueFull ?? "";
            var rawNormalized = TextUtils.NormalizeWhitespace(raw.Replace('\r', ' ').Replace('\n', ' '));
            var normalized = TextUtils.CollapseSpacedLettersText(rawNormalized);
            normalized = FixSplitWords(normalized);
            var segment = new AlignSegment
            {
                Band = band,
                OpRange = opRange ?? "",
                ValueFull = raw,
                RawText = rawNormalized,
                WorkText = normalized,
                Obj = obj,
                PdfPath = pdfPath
            };

            if (TryParseOpRange(segment.OpRange, out var startOp, out var endOp))
                segment.BBox = TryBuildSegmentBBox(pdfPath, obj, startOp, endOp);

            return segment;
        }

        private static bool TryParseOpRange(string opRange, out int startOp, out int endOp)
        {
            startOp = 0;
            endOp = 0;
            if (string.IsNullOrWhiteSpace(opRange)) return false;
            var raw = opRange.Trim();
            if (!raw.StartsWith("op", StringComparison.OrdinalIgnoreCase))
                return false;
            raw = raw.Substring(2);
            var bracketIdx = raw.IndexOf('[', StringComparison.Ordinal);
            if (bracketIdx >= 0)
                raw = raw.Substring(0, bracketIdx);
            if (raw.Contains('-', StringComparison.Ordinal))
            {
                var parts = raw.Split('-', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out startOp)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out endOp))
                    return startOp > 0 && endOp >= startOp;
                return false;
            }
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out startOp) && startOp > 0
                   && (endOp = startOp) > 0;
        }

        private static FieldBoundingBox? TryBuildSegmentBBox(string pdfPath, int obj, int startOp, int endOp)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || obj <= 0 || startOp <= 0 || endOp <= 0)
                return null;

            var opFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tj", "TJ" };
            var bbox = ObjectsTextOpsDiff.TryBuildOpRangeBoundingBox(pdfPath, obj, startOp, endOp, opFilter, out _);
            if (bbox == null)
                return null;

            return new FieldBoundingBox
            {
                X0 = bbox.X0,
                Y0 = bbox.Y0,
                X1 = bbox.X1,
                Y1 = bbox.Y1,
                StartOp = bbox.StartOp,
                EndOp = bbox.EndOp,
                Items = bbox.Items
            };
        }

        private static string ResolvePdfPath(string? rawPath, string? pdfName, string alignrangePath)
        {
            if (!string.IsNullOrWhiteSpace(rawPath) && File.Exists(rawPath))
                return rawPath;

            var baseName = pdfName ?? "";
            if (!string.IsNullOrWhiteSpace(baseName))
            {
                var alignDir = Path.GetDirectoryName(alignrangePath) ?? "";
                if (!string.IsNullOrWhiteSpace(alignDir))
                {
                    var candidate = Path.Combine(alignDir, baseName);
                    if (File.Exists(candidate))
                        return candidate;
                }

                var cwd = Directory.GetCurrentDirectory();
                var cwdCandidate = Path.Combine(cwd, baseName);
                if (File.Exists(cwdCandidate))
                    return cwdCandidate;
            }

            return rawPath ?? baseName;
        }

        private static string FixSplitWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "da","de","do","das","dos","em","no","na","nos","nas","e","ou","a","o"
            };
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var outParts = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                var cur = parts[i];
                if (IsShortJoinable(cur, stop) && i + 1 < parts.Length && IsWord(parts[i + 1]))
                {
                    var merged = cur + parts[i + 1];
                    i++;
                    while (i + 1 < parts.Length && IsVeryShort(parts[i + 1]))
                    {
                        merged += parts[i + 1];
                        i++;
                    }
                    outParts.Add(merged);
                    continue;
                }
                outParts.Add(cur);
            }
            return string.Join(" ", outParts);
        }

        private static bool IsWord(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            return Regex.IsMatch(token, "^\\p{L}+$");
        }

        private static bool IsVeryShort(string token)
        {
            return IsWord(token) && token.Length <= 2;
        }

        private static bool IsShortJoinable(string token, HashSet<string> stop)
        {
            if (!IsWord(token)) return false;
            if (token.Length > 3) return false;
            return !stop.Contains(token);
        }

        private static bool TryExtractFromNlp(AlignRangeSource source, List<NlpEntity>? entities, out string value)
        {
            value = "";
            if (source?.NlpLabels == null || source.NlpLabels.Count == 0 || entities == null || entities.Count == 0)
                return false;

            foreach (var label in source.NlpLabels)
            {
                var hit = entities.FirstOrDefault(e => e.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
                if (hit == null)
                    continue;
                value = hit.Text?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(value))
                    return true;
            }

            return false;
        }

        private static bool TryExtractFromRegex(AlignRangeSource source, string text, out string value)
        {
            value = "";
            if (source?.Regex == null || source.Regex.Count == 0)
                return false;

            foreach (var rule in source.Regex)
            {
                if (string.IsNullOrWhiteSpace(rule.Pattern))
                    continue;

                var pattern = rule.Pattern;
                if (pattern.Contains("\\\\", StringComparison.Ordinal))
                    pattern = pattern.Replace("\\\\", "\\");

                Regex rx;
                try
                {
                    rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                }
                catch
                {
                    continue;
                }

                var m = rx.Match(text ?? "");
                if (!m.Success)
                    continue;

                var groupIndex = rule.Group ?? (m.Groups.Count > 1 ? 1 : 0);
                if (groupIndex < 0 || groupIndex >= m.Groups.Count)
                    continue;

                var g = m.Groups[groupIndex];
                if (!g.Success)
                    continue;

                value = g.Value?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(value))
                    return true;
            }

            return false;
        }

        private static string NormalizeFieldValue(string fieldName, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var trimmed = value.Trim().Trim(',', ';', '.', ':');

            if (fieldName.StartsWith("PROCESSO_", StringComparison.OrdinalIgnoreCase))
            {
                return Regex.Replace(trimmed, "\\s+", "");
            }

            if (fieldName.Contains("CPF", StringComparison.OrdinalIgnoreCase))
            {
                return FormatCpf(trimmed);
            }

            if (fieldName.Equals("PERITO", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizePeritoValue(trimmed);
            }

            if (fieldName.StartsWith("VALOR_", StringComparison.OrdinalIgnoreCase))
            {
                var norm = TextUtils.NormalizeWhitespace(trimmed);
                if (!string.IsNullOrWhiteSpace(norm) && !norm.StartsWith("R$", StringComparison.OrdinalIgnoreCase))
                    norm = "R$ " + norm;
                return norm;
            }

            if (fieldName.Equals("ESPECIALIDADE", StringComparison.OrdinalIgnoreCase))
            {
                var norm = TextUtils.NormalizeWhitespace(trimmed);
                norm = Regex.Replace(norm, "(?i)^perit[oa]\\s+", "");
                norm = Regex.Replace(norm, "\\s*[-–]\\s*[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}.*$", "");
                norm = TextUtils.NormalizeWhitespace(norm);
                if (string.Equals(norm, "perito", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(norm, "perita", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(norm, "nomeado", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(norm, "nomeada", StringComparison.OrdinalIgnoreCase))
                    return "";
                return norm;
            }

            if (fieldName.Equals("VARA", StringComparison.OrdinalIgnoreCase))
            {
                var norm = TextUtils.NormalizeWhitespace(trimmed);
                norm = Regex.Replace(norm, "(?<=[a-záâãàéêíóôõúç])(?=[A-ZÁÂÃÀÉÊÍÓÔÕÚÇ])", " ");
                norm = Regex.Replace(norm, "(?i)\\bjuiz\\s*ado\\b", "Juizado");
                norm = Regex.Replace(norm, "(?i)\\bjuizad\\s*o\\b", "Juizado");
                norm = Regex.Replace(norm, "(?i)\\bju[ií]zo\\s+do\\s+", "");
                norm = Regex.Replace(norm, "(?i)\\bju[ií]zo\\s+da\\s+", "");
                norm = Regex.Replace(norm, "(?i)\\bvara\\s*de\\b", "Vara de");
                return TextUtils.NormalizeWhitespace(norm);
            }

            return TextUtils.NormalizeWhitespace(trimmed);
        }

        private static string NormalizePeritoValue(string raw)
        {
            var norm = TextUtils.NormalizeWhitespace(raw ?? "");
            if (string.IsNullOrWhiteSpace(norm))
                return "";

            norm = Regex.Replace(norm, "(?i)^(?:interessad[oa]\\s*:\\s*|perit[oa]\\s*[:\\-]?\\s*)", "");
            norm = TextUtils.NormalizeWhitespace(norm);

            var clipped = ClipPeritoAtNoise(norm);
            if (!string.IsNullOrWhiteSpace(clipped))
                norm = clipped;

            norm = Regex.Replace(norm, @"\s*[-–]\s*[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}.*$", "", RegexOptions.IgnoreCase);
            norm = Regex.Replace(norm, "(?i)\\s*,\\s*(?:perit[oa]|m[eé]dic[oa]|engenheir[oa]|grafocopista|psiquiatr[ao]|contador(?:a)?|assistente\\s+t[eé]cnico).*$", "");
            norm = TextUtils.NormalizeWhitespace(norm).Trim(',', ';', '.', ':', '-', '–');

            var candidate = ExtractPeritoNameCandidate(norm);
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;

            // Evita falso positivo com narrativa longa sem nome confiável.
            if (norm.Length > 80 || ContainsPeritoNoise(norm))
                return "";

            return norm;
        }

        private static string ClipPeritoAtNoise(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value ?? "";

            var stopHints = new[]
            {
                " adiantamento",
                " no valor",
                " correspondente",
                " do valor",
                " em favor",
                " autos do processo",
                " autos do",
                " processo nº",
                " processo n",
                " os presentes",
                " o presente",
                " trata-se",
                " considerando",
                " movido por",
                " movida por",
                " em face",
                " para realização",
                " para realizacao"
            };

            var best = value.Length;
            foreach (var hint in stopHints)
            {
                var idx = value.IndexOf(hint, StringComparison.OrdinalIgnoreCase);
                if (idx > 0 && idx < best)
                    best = idx;
            }

            if (best < value.Length)
                return TextUtils.NormalizeWhitespace(value.Substring(0, best)).Trim(',', ';', '.', ':', '-', '–');

            return value;
        }

        private static bool ContainsPeritoNoise(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return Regex.IsMatch(
                value,
                "(?i)\\b(?:r\\$|processo|autos?|valor|honor[aá]rios|reserva|adiantamento|correspondente|movid[oa]|em\\s+face|tribunal|diretoria|comarca)\\b");
        }

        private static string ExtractPeritoNameCandidate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            var matches = Regex.Matches(
                value,
                "(?i)\\b(?:d(?:r|ra)\\.?\\s+)?[A-ZÁÂÃÀÉÊÍÓÔÕÚÇ][A-Za-zÁÂÃÀÉÊÍÓÔÕÚÇ'`.-]+(?:\\s+(?:de|da|do|dos|das|e))?(?:\\s+[A-ZÁÂÃÀÉÊÍÓÔÕÚÇ][A-Za-zÁÂÃÀÉÊÍÓÔÕÚÇ'`.-]+){1,6}\\b");

            foreach (Match m in matches)
            {
                var candidate = TextUtils.NormalizeWhitespace(m.Value ?? "").Trim(',', ';', '.', ':', '-', '–');
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;
                if (LooksInstitutionalName(candidate))
                    continue;
                return candidate;
            }

            return "";
        }

        private static bool LooksInstitutionalName(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return true;

            return Regex.IsMatch(candidate, "(?i)\\b(?:tribunal|justi[cç]a|diretoria|comarca|vara|ju[ií]zo|processo|conselho|documento|estado|para[ií]ba)\\b");
        }

        private static void ApplyDerivedFields(Dictionary<string, FieldOutput> output)
        {
            if (output == null || output.Count == 0) return;

            var valCm = GetField(output, "VALOR_ARBITRADO_CM");
            var valDe = GetField(output, "VALOR_ARBITRADO_DE");
            var valJz = GetField(output, "VALOR_ARBITRADO_JZ");

            var finalValue = FirstNonEmpty(valCm, valDe, valJz);
            if (!string.IsNullOrWhiteSpace(finalValue?.Value))
            {
                SetDerived(output, "VALOR_ARBITRADO_FINAL", finalValue, "VALOR_ARBITRADO_FINAL");
            }

            var dataDespesa = GetField(output, "DATA_DESPESA");
            if (!string.IsNullOrWhiteSpace(dataDespesa?.Value))
            {
                SetDerived(output, "DATA_ARBITRADO_FINAL", dataDespesa, "DATA_ARBITRADO_FINAL");
            }
        }

        private static FieldOutput? GetField(Dictionary<string, FieldOutput> output, string name)
        {
            return output.TryGetValue(name, out var value) ? value : null;
        }

        private static FieldOutput? FirstNonEmpty(params FieldOutput?[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (candidate == null) continue;
                if (!string.IsNullOrWhiteSpace(candidate.Value)) return candidate;
            }
            return null;
        }

        private static void SetDerived(Dictionary<string, FieldOutput> output, string targetField, FieldOutput source, string normalizeAs)
        {
            if (output.TryGetValue(targetField, out var existing) && !string.IsNullOrWhiteSpace(existing.Value))
                return;

            var raw = source.ValueRaw;
            if (string.IsNullOrWhiteSpace(raw))
                raw = source.Value;

            output[targetField] = new FieldOutput
            {
                ValueFull = source.ValueFull ?? "",
                ValueRaw = raw ?? "",
                Value = NormalizeFieldValue(normalizeAs, raw ?? ""),
                Source = string.IsNullOrWhiteSpace(source.Source) ? "derived" : $"derived:{source.Source}",
                OpRange = source.OpRange ?? "",
                Obj = source.Obj,
                BBox = source.BBox
            };
        }

        private static string FormatCpf(string raw)
        {
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Length != 11) return raw;
            return $"{digits.Substring(0, 3)}.{digits.Substring(3, 3)}.{digits.Substring(6, 3)}-{digits.Substring(9, 2)}";
        }

        private static string NormalizeBand(string band)
        {
            if (string.IsNullOrWhiteSpace(band)) return "front_head";
            var norm = band.Trim().ToLowerInvariant();
            if (norm == "front" || norm == "head") return "front_head";
            if (norm == "back" || norm == "tail") return "back_tail";
            if (norm == "back_signature" || norm == "signature" || norm == "backsig") return "back_signature";
            return norm;
        }

        private static bool ParseOptions(
            string[] args,
            out string alignrangePath,
            out string mapPath,
            out string outDir,
            out bool useFront,
            out bool useBack,
            out MapSide side)
        {
            alignrangePath = "";
            mapPath = "";
            outDir = "";
            useFront = false;
            useBack = false;
            side = MapSide.Both;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg == "--help" || arg == "-h")
                    return false;

                if (string.Equals(arg, "--alignrange", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    alignrangePath = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--map", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    mapPath = args[++i];
                    continue;
                }
                if ((string.Equals(arg, "--out", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "--out-dir", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    outDir = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--front", StringComparison.OrdinalIgnoreCase))
                {
                    useFront = true;
                    continue;
                }
                if (string.Equals(arg, "--back", StringComparison.OrdinalIgnoreCase))
                {
                    useBack = true;
                    continue;
                }
                if (string.Equals(arg, "--both", StringComparison.OrdinalIgnoreCase))
                {
                    useFront = true;
                    useBack = true;
                    continue;
                }
                if (string.Equals(arg, "--side", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    side = ParseSide(args[++i]);
                    continue;
                }

                if (!arg.StartsWith("-", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(alignrangePath))
                        alignrangePath = arg;
                    else if (string.IsNullOrWhiteSpace(mapPath))
                        mapPath = arg;
                }
            }

            return true;
        }

        private static MapSide ParseSide(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return MapSide.Both;
            var t = raw.Trim().ToLowerInvariant();
            return t switch
            {
                "a" => MapSide.A,
                "b" => MapSide.B,
                "both" => MapSide.Both,
                "ab" => MapSide.Both,
                _ => MapSide.Both
            };
        }

        private static void ShowHelp()
        {
            Console.WriteLine("operpdf inspect mapfields --alignrange <arquivo.txt> --map <map.yml> [--front|--back|--both] [--side a|b|both]");
            Console.WriteLine("  --out <dir>    (opcional, default outputs/fields)");
        }

        private static string ResolveMapPath(string mapPath)
        {
            if (File.Exists(mapPath)) return mapPath;
            var cwd = Directory.GetCurrentDirectory();
            var regAlign = Obj.Utils.PatternRegistry.FindDir("alignrange_fields");
            var regTextOps = Obj.Utils.PatternRegistry.FindDir("textops_fields");
            var regExtract = Obj.Utils.PatternRegistry.FindDir("extract_fields");
            var candidates = new List<string>
            {
                Path.Combine(cwd, mapPath)
            };
            if (!string.IsNullOrWhiteSpace(regAlign))
                candidates.Add(Path.Combine(regAlign, mapPath));
            if (!string.IsNullOrWhiteSpace(regTextOps))
                candidates.Add(Path.Combine(regTextOps, mapPath));
            if (!string.IsNullOrWhiteSpace(regExtract))
                candidates.Add(Path.Combine(regExtract, mapPath));

            if (!mapPath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && !mapPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(regAlign))
                {
                    candidates.Add(Path.Combine(regAlign, mapPath + ".yml"));
                    candidates.Add(Path.Combine(regAlign, mapPath + ".yaml"));
                }
                if (!string.IsNullOrWhiteSpace(regTextOps))
                {
                    candidates.Add(Path.Combine(regTextOps, mapPath + ".yml"));
                    candidates.Add(Path.Combine(regTextOps, mapPath + ".yaml"));
                }
                if (!string.IsNullOrWhiteSpace(regExtract))
                {
                    candidates.Add(Path.Combine(regExtract, mapPath + ".yml"));
                    candidates.Add(Path.Combine(regExtract, mapPath + ".yaml"));
                }
            }

            var baseName = Path.GetFileName(mapPath);
            if (!string.IsNullOrWhiteSpace(baseName) && !string.Equals(baseName, mapPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(regAlign))
                    candidates.Add(Path.Combine(regAlign, baseName));
                if (!string.IsNullOrWhiteSpace(regTextOps))
                    candidates.Add(Path.Combine(regTextOps, baseName));
                if (!string.IsNullOrWhiteSpace(regExtract))
                    candidates.Add(Path.Combine(regExtract, baseName));
                if (!baseName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && !baseName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(regAlign))
                    {
                        candidates.Add(Path.Combine(regAlign, baseName + ".yml"));
                        candidates.Add(Path.Combine(regAlign, baseName + ".yaml"));
                    }
                    if (!string.IsNullOrWhiteSpace(regTextOps))
                    {
                        candidates.Add(Path.Combine(regTextOps, baseName + ".yml"));
                        candidates.Add(Path.Combine(regTextOps, baseName + ".yaml"));
                    }
                    if (!string.IsNullOrWhiteSpace(regExtract))
                    {
                        candidates.Add(Path.Combine(regExtract, baseName + ".yml"));
                        candidates.Add(Path.Combine(regExtract, baseName + ".yaml"));
                    }
                }
            }

            foreach (var c in candidates)
            {
                if (File.Exists(c))
                    return c;
            }

            return mapPath;
        }

        private static string ResolveExistingPath(string path)
        {
            if (File.Exists(path)) return path;
            var cwd = Directory.GetCurrentDirectory();
            var full = Path.Combine(cwd, path);
            return File.Exists(full) ? full : path;
        }

        private enum MapSide
        {
            A,
            B,
            Both
        }

        private sealed class AlignRangeFile
        {
            public AlignRangeSection? FrontHead { get; set; }
            public AlignRangeSection? BackTail { get; set; }
            public AlignRangeSection? BackSignature { get; set; }
        }

        private sealed class AlignRangeSection
        {
            public string PdfA { get; set; } = "";
            public string PdfAPath { get; set; } = "";
            public int ObjA { get; set; }
            public string OpRangeA { get; set; } = "";
            public string ValueFullA { get; set; } = "";
            public string PdfB { get; set; } = "";
            public string PdfBPath { get; set; } = "";
            public int ObjB { get; set; }
            public string OpRangeB { get; set; } = "";
            public string ValueFullB { get; set; } = "";
        }

        private sealed class AlignSegment
        {
            public string Band { get; set; } = "";
            public string OpRange { get; set; } = "";
            public string ValueFull { get; set; } = "";
            public string RawText { get; set; } = "";
            public string WorkText { get; set; } = "";
            public string PdfPath { get; set; } = "";
            public int Obj { get; set; }
            public FieldBoundingBox? BBox { get; set; }
        }

        private sealed class AlignRangeFieldMap
        {
            public int Version { get; set; } = 1;
            public string Doc { get; set; } = "";
            public Dictionary<string, AlignRangeField> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class AlignRangeField
        {
            public List<AlignRangeSource> Sources { get; set; } = new();
        }

        private sealed class AlignRangeSource
        {
            public string Band { get; set; } = "front_head";
            public List<string> NlpLabels { get; set; } = new();
            public List<RegexRule> Regex { get; set; } = new();
        }

        private sealed class RegexRule
        {
            public string Pattern { get; set; } = "";
            public int? Group { get; set; }
        }

        private sealed class FieldOutput
        {
            public string ValueFull { get; set; } = "";
            public string ValueRaw { get; set; } = "";
            public string Value { get; set; } = "";
            public string Source { get; set; } = "";
            public string OpRange { get; set; } = "";
            public int Obj { get; set; }
            public FieldBoundingBox? BBox { get; set; }
        }

        private sealed class FieldBoundingBox
        {
            public double X0 { get; set; }
            public double Y0 { get; set; }
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public int Items { get; set; }
        }

        private sealed class NlpEntity
        {
            public NlpEntity(string label, int start, int end, string text)
            {
                Label = label;
                Start = start;
                End = end;
                Text = text;
            }

            public string Label { get; }
            public int Start { get; }
            public int End { get; }
            public string Text { get; }
        }

        private static class NlpLite
        {
            private static readonly Regex CpfRegex = new(@"\b\d{3}\.?\d{3}\.?\d{3}-?\d{2}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
            private static readonly Regex CnpjRegex = new(@"\b\d{2}\.?\d{3}\.?\d{3}/\d{4}-?\d{2}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
            private static readonly Regex CnjLooseRegex = new(@"\b\d{7}-?\d{2}[.\-]?\d{4}[.\-]?\d[.\-]?\d{2}(?:[.\-]?\d{4})?\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
            private static readonly Regex MoneyRegex = new(@"\b(?:R\$\s*)?\d{1,3}(?:\.\d{3})*,\d{2}\b|\b(?:R\$\s*)?\d+,\d{2}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
            private static readonly Regex DateRegex = new(@"\b(?:\d{1,2}/\d{1,2}/\d{4}|\d{1,2}\s+de\s+(?:janeiro|fevereiro|março|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\s+de\s+\d{4})\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            private static readonly Regex EmailRegex = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

            public static List<NlpEntity> Annotate(string text)
            {
                var entities = new List<NlpEntity>();
                if (string.IsNullOrWhiteSpace(text))
                    return entities;

                AddMatches(entities, "CPF", CpfRegex, text);
                AddMatches(entities, "CNPJ", CnpjRegex, text);
                AddCnjMatches(entities, text);
                AddMatches(entities, "VALOR", MoneyRegex, text);
                AddMatches(entities, "DATA", DateRegex, text);
                AddMatches(entities, "EMAIL", EmailRegex, text);

                entities = entities
                    .OrderBy(e => e.Start)
                    .ThenByDescending(e => e.End - e.Start)
                    .ToList();

                var filtered = new List<NlpEntity>();
                var lastEnd = -1;
                foreach (var entity in entities)
                {
                    if (entity.Start < lastEnd)
                        continue;
                    filtered.Add(entity);
                    lastEnd = entity.End;
                }

                return filtered;
            }

            private static void AddMatches(List<NlpEntity> entities, string label, Regex regex, string text)
            {
                foreach (Match match in regex.Matches(text))
                {
                    if (!match.Success)
                        continue;
                    entities.Add(new NlpEntity(label, match.Index, match.Index + match.Length, match.Value));
                }
            }

            private static void AddCnjMatches(List<NlpEntity> entities, string text)
            {
                foreach (Match match in CnjLooseRegex.Matches(text))
                {
                    if (!match.Success)
                        continue;
                    var digits = new string(match.Value.Where(char.IsDigit).ToArray());
                    var label = digits.Length >= 20 ? "CNJ" : "CNJ_PARTIAL";
                    entities.Add(new NlpEntity(label, match.Index, match.Index + match.Length, match.Value));
                }
            }
        }
    }
}
