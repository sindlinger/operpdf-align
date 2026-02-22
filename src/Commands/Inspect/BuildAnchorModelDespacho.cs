using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using Obj.Utils;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.Commands
{
    internal static class BuildAnchorModelDespacho
    {
        private enum RenderMode
        {
            Placeholder,
            AnchorPhrase,
            MatchedLine,
            Masked
        }

        private sealed class TextChunk
        {
            public int Page { get; set; }
            public string Text { get; set; } = "";
            public float X { get; set; }
            public float Y { get; set; }
            public float Width { get; set; }
            public float Height { get; set; }
        }

        public sealed class PlaceholderRule
        {
            public string Tag { get; set; } = "";
            public string Label { get; set; } = "";
            public int MaxPerPage { get; set; } = 2;
        }

        public sealed class AnchorRule
        {
            public string Label { get; set; } = "";
            public string[] Needles { get; set; } = Array.Empty<string>();
            public int MaxPerPage { get; set; } = 1;
        }

        private sealed class AnchorPlacement
        {
            public int Page { get; set; }
            public string Label { get; set; } = "";
            public string MatchedText { get; set; } = "";
            public float X { get; set; }
            public float Y { get; set; }
            public float Height { get; set; }
        }

        public sealed class RenderTextProfile
        {
            public Dictionary<string, string> Placeholder { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> AnchorPhrase { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> Masked { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public List<string> MaskedSequence { get; set; } = new List<string>();
        }

        public sealed class DefaultsProfile
        {
            public string PlaceholderText { get; set; } = "ANCORA";
            public string AnchorPhraseText { get; set; } = "âncora";
            public string MaskedText { get; set; } = "PPPPP 0000";
        }

        public sealed class AnchorModelProfile
        {
            public List<AnchorRule> AnchorRules { get; set; } = new List<AnchorRule>();
            public List<PlaceholderRule> PlaceholderRules { get; set; } = new List<PlaceholderRule>();
            public RenderTextProfile RenderText { get; set; } = new RenderTextProfile();
            public DefaultsProfile Defaults { get; set; } = new DefaultsProfile();
        }

        private sealed class TextChunkCollector : IEventListener
        {
            private readonly int _pageNumber;
            public List<TextChunk> Chunks { get; } = new List<TextChunk>();

            public TextChunkCollector(int pageNumber)
            {
                _pageNumber = pageNumber;
            }

            public ICollection<EventType> GetSupportedEvents()
            {
                return null!;
            }

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type != EventType.RENDER_TEXT)
                    return;
                if (data is not TextRenderInfo tri)
                    return;

                var text = tri.GetText() ?? "";
                text = text.Trim();
                if (text.Length == 0)
                    return;

                var ascent = tri.GetAscentLine();
                var descent = tri.GetDescentLine();
                var x = descent.GetStartPoint().Get(0);
                var x2 = descent.GetEndPoint().Get(0);
                var y = descent.GetStartPoint().Get(1);
                var yTop = ascent.GetStartPoint().Get(1);
                var width = Math.Max(6f, Math.Abs(x2 - x));
                var height = Math.Max(6f, Math.Abs(yTop - y));

                Chunks.Add(new TextChunk
                {
                    Page = _pageNumber,
                    Text = text,
                    X = x,
                    Y = y,
                    Width = width,
                    Height = height
                });
            }
        }

        public static void Execute(string[] args)
        {
            var modelPath = ResolveDefaultModelPath();
            var outPath = Path.Combine("reference", "models", "tjpb_despacho_anchor_model.pdf");
            var profilePath = ResolveDefaultProfilePath();
            var minTextLen = 3;
            var renderMode = RenderMode.Placeholder;

            if (!ParseArgs(args, ref modelPath, ref outPath, ref profilePath, ref minTextLen, ref renderMode))
                return;

            if (!File.Exists(modelPath))
            {
                Console.WriteLine($"Modelo não encontrado: {modelPath}");
                return;
            }
            if (!File.Exists(profilePath))
            {
                Console.WriteLine($"Perfil YAML não encontrado: {profilePath}");
                return;
            }

            var profile = LoadProfile(profilePath, out var profileError);
            if (profile == null)
            {
                Console.WriteLine($"Erro ao carregar perfil YAML: {profileError}");
                return;
            }

            var chunks = ExtractChunks(modelPath, minTextLen);
            if (chunks.Count == 0)
            {
                Console.WriteLine("Nenhum texto útil encontrado no modelo.");
                return;
            }

            var placements = SelectAnchorPlacements(chunks, profile);
            if (placements.Count == 0)
            {
                Console.WriteLine("Nenhuma âncora encontrada com as regras do perfil YAML.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
            WriteAnchorModelPdf(modelPath, outPath, placements, renderMode, profile);
            var metaPath = WriteAnchorMetadata(outPath, modelPath, placements, profilePath);

            Console.WriteLine($"Arquivo anchor-model salvo: {Path.GetFullPath(outPath)}");
            Console.WriteLine($"Arquivo metadados salvo:   {metaPath}");
            foreach (var group in placements.GroupBy(p => p.Page).OrderBy(g => g.Key))
            {
                var labels = string.Join(", ", group.Select(x => x.Label).Distinct(StringComparer.OrdinalIgnoreCase));
                Console.WriteLine($"  página {group.Key}: anchors={group.Count()} [{labels}]");
            }
            Console.WriteLine($"Total anchors: {placements.Count}");
        }

        private static string ResolveDefaultModelPath()
        {
            var env = Environment.GetEnvironmentVariable("OBJPDF_MODEL_DESPACHO");
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();
            return Path.Combine("reference", "models", "tjpb_despacho_model.pdf");
        }

        private static string ResolveDefaultProfilePath()
        {
            var env = Environment.GetEnvironmentVariable("OBJPDF_ANCHOR_MODEL_DESPACHO_PROFILE");
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();
            return Path.Combine("modules", "PatternModules", "registry", "anchor_model_profiles", "tjpb_despacho.yml");
        }

        private static bool ParseArgs(string[] args, ref string modelPath, ref string outPath, ref string profilePath, ref int minTextLen, ref RenderMode renderMode)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var arg = (args[i] ?? "").Trim();
                if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
                {
                    ShowHelp();
                    return false;
                }
                if ((arg.Equals("--model", StringComparison.OrdinalIgnoreCase) || arg.Equals("--input", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    modelPath = args[++i];
                    continue;
                }
                if (arg.StartsWith("--model=", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("--input=", StringComparison.OrdinalIgnoreCase))
                {
                    modelPath = arg.Split('=', 2)[1];
                    continue;
                }
                if (arg.Equals("--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    outPath = args[++i];
                    continue;
                }
                if (arg.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
                {
                    outPath = arg.Split('=', 2)[1];
                    continue;
                }
                if ((arg.Equals("--profile", StringComparison.OrdinalIgnoreCase) || arg.Equals("--config", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    profilePath = args[++i];
                    continue;
                }
                if (arg.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
                {
                    profilePath = arg.Split('=', 2)[1];
                    continue;
                }
                if (arg.Equals("--min-text-len", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v > 0)
                        minTextLen = v;
                    continue;
                }
                if (arg.Equals("--render", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var mode = (args[++i] ?? "").Trim().ToLowerInvariant();
                    renderMode = mode switch
                    {
                        "anchor" or "anchors" or "phrase" => RenderMode.AnchorPhrase,
                        "line" or "matched" or "text" => RenderMode.MatchedLine,
                        "masked" or "mask" or "anon" or "anonymized" => RenderMode.Masked,
                        _ => RenderMode.Placeholder
                    };
                    continue;
                }
                if (arg.StartsWith("--render=", StringComparison.OrdinalIgnoreCase))
                {
                    var mode = arg.Split('=', 2)[1].Trim().ToLowerInvariant();
                    renderMode = mode switch
                    {
                        "anchor" or "anchors" or "phrase" => RenderMode.AnchorPhrase,
                        "line" or "matched" or "text" => RenderMode.MatchedLine,
                        "masked" or "mask" or "anon" or "anonymized" => RenderMode.Masked,
                        _ => RenderMode.Placeholder
                    };
                    continue;
                }
                if (!arg.StartsWith("-", StringComparison.Ordinal))
                    modelPath = arg;
            }
            return true;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("operpdf build-anchor-model-despacho [--model <pdf>] [--out <pdf>] [--profile <yaml>] [--min-text-len N]");
            Console.WriteLine("Exemplo:");
            Console.WriteLine("  operpdf build-anchor-model-despacho --model reference/models/tjpb_despacho_model.pdf --profile modules/PatternModules/registry/anchor_model_profiles/tjpb_despacho.yml --out reference/models/tjpb_despacho_anchor_model.pdf --render anchor");
            Console.WriteLine("  operpdf build-anchor-model-despacho --model reference/models/tjpb_despacho_model.pdf --profile modules/PatternModules/registry/anchor_model_profiles/tjpb_despacho.yml --out models/aliases/masked/tjpb_despacho_masked_model.pdf --render masked");
        }

        private static List<TextChunk> ExtractChunks(string modelPath, int minTextLen)
        {
            var chunks = new List<TextChunk>();
            using var doc = new PdfDocument(new PdfReader(modelPath));
            var pages = doc.GetNumberOfPages();
            for (var pageNum = 1; pageNum <= pages; pageNum++)
            {
                var collector = new TextChunkCollector(pageNum);
                var processor = new PdfCanvasProcessor(collector);
                processor.ProcessPageContent(doc.GetPage(pageNum));
                chunks.AddRange(collector.Chunks.Where(c => c.Text.Trim().Length >= minTextLen));
            }
            return chunks;
        }

        private static List<AnchorPlacement> SelectAnchorPlacements(List<TextChunk> chunks, AnchorModelProfile profile)
        {
            var placements = new List<AnchorPlacement>();
            foreach (var pageGroup in chunks.GroupBy(c => c.Page).OrderBy(g => g.Key))
            {
                var page = pageGroup.Key;
                var pageChunks = pageGroup.OrderByDescending(c => c.Y).ThenBy(c => c.X).ToList();

                var usedByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                // 1) Primeiro prioriza placeholders do modelo (<VARA>, <COMARCA>, ...)
                // para posicionar âncoras no ponto real do campo.
                foreach (var chunk in pageChunks)
                {
                    foreach (var rule in profile.PlaceholderRules)
                    {
                        if (string.IsNullOrWhiteSpace(rule.Tag) || string.IsNullOrWhiteSpace(rule.Label))
                            continue;
                        var maxPerPage = Math.Max(1, rule.MaxPerPage);
                        var used = usedByLabel.TryGetValue(rule.Label, out var count) ? count : 0;
                        if (used >= maxPerPage)
                            continue;

                        var idx = chunk.Text.IndexOf(rule.Tag, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0)
                            continue;

                        var x = chunk.X + EstimateOffsetByIndex(chunk.Text, chunk.Width, idx);
                        if (HasNearPlacement(placements, page, rule.Label, x, chunk.Y))
                            continue;

                        placements.Add(new AnchorPlacement
                        {
                            Page = page,
                            Label = rule.Label,
                            MatchedText = rule.Tag,
                            X = x,
                            Y = chunk.Y,
                            Height = chunk.Height
                        });

                        usedByLabel[rule.Label] = used + 1;
                    }
                }

                // 2) Completa com regras textuais para âncoras estruturais.
                foreach (var rule in profile.AnchorRules)
                {
                    if (string.IsNullOrWhiteSpace(rule.Label))
                        continue;
                    var needles = (rule.Needles ?? Array.Empty<string>())
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .ToArray();
                    if (needles.Length == 0)
                        continue;
                    var maxPerPage = Math.Max(1, rule.MaxPerPage);
                    var used = usedByLabel.TryGetValue(rule.Label, out var n) ? n : 0;
                    foreach (var chunk in pageChunks)
                    {
                        if (used >= maxPerPage)
                            break;

                        var normalized = Normalize(chunk.Text);
                        if (normalized.Length == 0)
                            continue;
                        var matchedNeedle = needles.FirstOrDefault(nl => normalized.Contains(nl, StringComparison.Ordinal));
                        if (string.IsNullOrWhiteSpace(matchedNeedle))
                            continue;

                        var x = chunk.X;
                        if (HasNearPlacement(placements, page, rule.Label, x, chunk.Y))
                            continue;

                        placements.Add(new AnchorPlacement
                        {
                            Page = page,
                            Label = rule.Label,
                            MatchedText = chunk.Text,
                            X = x,
                            Y = chunk.Y,
                            Height = chunk.Height
                        });
                        used++;
                    }

                    usedByLabel[rule.Label] = used;
                }
            }
            return ResolveOverlaps(placements)
                .OrderBy(p => p.Page)
                .ThenByDescending(p => p.Y)
                .ThenBy(p => p.X)
                .ToList();
        }

        private static bool HasNearPlacement(List<AnchorPlacement> placements, int page, string label, float x, float y)
        {
            return placements.Any(p =>
                p.Page == page &&
                string.Equals(p.Label, label, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(p.Y - y) < 4f &&
                Math.Abs(p.X - x) < 24f);
        }

        private static float EstimateOffsetByIndex(string text, float chunkWidth, int index)
        {
            if (string.IsNullOrEmpty(text) || chunkWidth <= 0f || index <= 0)
                return 0f;
            var len = Math.Max(1, text.Length);
            var ratio = Math.Min(1f, Math.Max(0f, index / (float)len));
            return chunkWidth * ratio;
        }

        private static List<AnchorPlacement> ResolveOverlaps(List<AnchorPlacement> placements)
        {
            var adjusted = new List<AnchorPlacement>(placements.Count);
            foreach (var pageGroup in placements.GroupBy(p => p.Page).OrderBy(g => g.Key))
            {
                var slotsByRow = new Dictionary<int, List<(float X, float W)>>();
                var ordered = pageGroup.OrderByDescending(p => p.Y).ThenBy(p => p.X).ToList();

                foreach (var p in ordered)
                {
                    var widthHint = Math.Max(58f, Math.Min(220f, ("[" + p.Label + "]").Length * 4.4f));
                    var rowKey = (int)Math.Round(p.Y / 6f);
                    var x = p.X;
                    var y = p.Y;
                    List<(float X, float W)> rowSlots;

                    while (true)
                    {
                        if (!slotsByRow.TryGetValue(rowKey, out rowSlots))
                        {
                            rowSlots = new List<(float X, float W)>();
                            slotsByRow[rowKey] = rowSlots;
                            break;
                        }

                        var overlaps = rowSlots.Any(slot =>
                            x < slot.X + slot.W + 3f &&
                            x + widthHint > slot.X - 3f);

                        if (!overlaps)
                            break;

                        // Se houver colisão horizontal na mesma linha, desce uma linha visual.
                        rowKey -= 2;
                        y -= 12f;
                    }

                    rowSlots.Add((x, widthHint));
                    adjusted.Add(new AnchorPlacement
                    {
                        Page = p.Page,
                        Label = p.Label,
                        MatchedText = p.MatchedText,
                        X = x,
                        Y = y,
                        Height = p.Height
                    });
                }
            }

            return adjusted;
        }

        private static void WriteAnchorModelPdf(string modelPath, string outPath, List<AnchorPlacement> placements, RenderMode renderMode, AnchorModelProfile profile)
        {
            using var src = new PdfDocument(new PdfReader(modelPath));
            using var dst = new PdfDocument(new PdfWriter(outPath));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
            var textColor = new DeviceRgb(18, 72, 120);

            for (var pageNum = 1; pageNum <= src.GetNumberOfPages(); pageNum++)
            {
                var srcPage = src.GetPage(pageNum);
                var size = srcPage.GetPageSize();
                var dstPage = dst.AddNewPage(new iText.Kernel.Geom.PageSize(size));
                var pdfCanvas = new PdfCanvas(dstPage);
                using var canvas = new Canvas(pdfCanvas, size);
                canvas.SetFont(font).SetFontSize(8).SetFontColor(textColor);

                if (renderMode == RenderMode.Masked && profile.RenderText.MaskedSequence.Count > 0)
                {
                    if (pageNum == 1)
                        DrawMaskedSequence(canvas, size, profile.RenderText.MaskedSequence);
                    continue;
                }

                foreach (var anchor in placements.Where(p => p.Page == pageNum))
                {
                    var text = ResolveAnchorRenderText(anchor, renderMode, profile);
                    var widthHint = Math.Max(32f, text.Length * 4.8f);
                    var maxWidth = Math.Max(32f, size.GetWidth() - 20f);
                    if (widthHint > maxWidth)
                        widthHint = maxWidth;
                    var x = Clamp(anchor.X, 10f, size.GetWidth() - widthHint - 10f);
                    var y = Clamp(anchor.Y, 10f, size.GetHeight() - 14f);
                    canvas.ShowTextAligned(text, x, y + 0.5f, TextAlignment.LEFT, VerticalAlignment.BOTTOM, 0f);
                }
            }
        }

        private static void DrawMaskedSequence(Canvas canvas, iText.Kernel.Geom.Rectangle pageSize, IReadOnlyList<string> lines)
        {
            var x = 36f;
            var y = pageSize.GetHeight() - 90f;
            const float lineHeight = 12f;

            foreach (var raw in lines)
            {
                if (y < 24f)
                    break;

                var line = raw ?? "";
                if (line.Length == 0)
                {
                    y -= lineHeight;
                    continue;
                }

                canvas.ShowTextAligned(line, x, y, TextAlignment.LEFT, VerticalAlignment.BOTTOM, 0f);
                y -= lineHeight;
            }
        }

        private static string ResolveAnchorRenderText(AnchorPlacement anchor, RenderMode renderMode, AnchorModelProfile profile)
        {
            if (renderMode == RenderMode.MatchedLine)
            {
                var raw = TextNormalization.NormalizeWhitespace(anchor?.MatchedText ?? "");
                return string.IsNullOrWhiteSpace(raw)
                    ? ResolveRenderTextByMode(profile, RenderMode.AnchorPhrase, anchor?.Label ?? "", anchor?.MatchedText ?? "")
                    : raw;
            }

            return ResolveRenderTextByMode(profile, renderMode, anchor?.Label ?? "", anchor?.MatchedText ?? "");
        }

        private static string ResolveRenderTextByMode(AnchorModelProfile profile, RenderMode mode, string label, string matchedText)
        {
            var map = mode switch
            {
                RenderMode.AnchorPhrase => profile.RenderText.AnchorPhrase,
                RenderMode.Masked => profile.RenderText.Masked,
                _ => profile.RenderText.Placeholder
            };

            if (!string.IsNullOrWhiteSpace(label) &&
                map.TryGetValue(label, out var mapped) &&
                !string.IsNullOrWhiteSpace(mapped))
            {
                return mapped;
            }

            if (mode == RenderMode.Placeholder)
            {
                var text = (matchedText ?? "").Trim();
                if (text.StartsWith("<", StringComparison.Ordinal) && text.EndsWith(">", StringComparison.Ordinal))
                    return text.Trim('<', '>').Replace('_', ' ');

                if (!string.IsNullOrWhiteSpace(label) && label.StartsWith("ANC_", StringComparison.OrdinalIgnoreCase))
                    return "<" + label.Substring(4) + ">";

                return string.IsNullOrWhiteSpace(profile.Defaults.PlaceholderText)
                    ? "ANCORA"
                    : profile.Defaults.PlaceholderText;
            }

            if (mode == RenderMode.AnchorPhrase)
            {
                if (!string.IsNullOrWhiteSpace(label) && label.StartsWith("ANC_", StringComparison.OrdinalIgnoreCase))
                    return label.Substring(4).Replace('_', ' ');
                return string.IsNullOrWhiteSpace(profile.Defaults.AnchorPhraseText)
                    ? "âncora"
                    : profile.Defaults.AnchorPhraseText;
            }

            return string.IsNullOrWhiteSpace(profile.Defaults.MaskedText)
                ? "PPPPP 0000"
                : profile.Defaults.MaskedText;
        }

        private static string WriteAnchorMetadata(string outPath, string modelPath, List<AnchorPlacement> placements, string profilePath)
        {
            var metaPath = Path.Combine(
                Path.GetDirectoryName(outPath) ?? ".",
                Path.GetFileNameWithoutExtension(outPath) + ".anchors.json");

            var payload = new
            {
                source_pdf = Path.GetFullPath(modelPath),
                source_profile = Path.GetFullPath(profilePath),
                output_pdf = Path.GetFullPath(outPath),
                generated_at_utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                anchors_total = placements.Count,
                pages = placements.GroupBy(p => p.Page)
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        page = g.Key,
                        count = g.Count(),
                        anchors = g.Select(a => new
                        {
                            label = a.Label,
                            text = a.MatchedText,
                            x = a.X,
                            y = a.Y,
                            height = a.Height
                        })
                    })
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return Path.GetFullPath(metaPath);
        }

        private static AnchorModelProfile? LoadProfile(string profilePath, out string error)
        {
            error = "";
            try
            {
                var yaml = File.ReadAllText(profilePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var profile = deserializer.Deserialize<AnchorModelProfile>(yaml) ?? new AnchorModelProfile();

                profile.AnchorRules ??= new List<AnchorRule>();
                profile.PlaceholderRules ??= new List<PlaceholderRule>();
                profile.RenderText ??= new RenderTextProfile();
                profile.Defaults ??= new DefaultsProfile();

                profile.RenderText.Placeholder = NormalizeMap(profile.RenderText.Placeholder);
                profile.RenderText.AnchorPhrase = NormalizeMap(profile.RenderText.AnchorPhrase);
                profile.RenderText.Masked = NormalizeMap(profile.RenderText.Masked);
                profile.RenderText.MaskedSequence = (profile.RenderText.MaskedSequence ?? new List<string>())
                    .Select(v => v ?? "")
                    .ToList();
                profile.AnchorRules = profile.AnchorRules
                    .Where(v => !string.IsNullOrWhiteSpace(v.Label))
                    .Select(v => new AnchorRule
                    {
                        Label = (v.Label ?? "").Trim(),
                        Needles = (v.Needles ?? Array.Empty<string>())
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .Select(n => n.Trim())
                            .ToArray(),
                        MaxPerPage = v.MaxPerPage
                    })
                    .Where(v => v.Needles.Length > 0)
                    .ToList();
                profile.PlaceholderRules = profile.PlaceholderRules
                    .Where(v => !string.IsNullOrWhiteSpace(v.Label) && !string.IsNullOrWhiteSpace(v.Tag))
                    .Select(v => new PlaceholderRule
                    {
                        Label = (v.Label ?? "").Trim(),
                        Tag = (v.Tag ?? "").Trim(),
                        MaxPerPage = v.MaxPerPage
                    })
                    .ToList();

                if (profile.AnchorRules.Count == 0 && profile.PlaceholderRules.Count == 0)
                {
                    error = "perfil sem regras (anchor_rules/placeholder_rules).";
                    return null;
                }
                return profile;
            }
            catch (Exception ex)
            {
                error = ex.ToString();
                return null;
            }
        }

        private static Dictionary<string, string> NormalizeMap(Dictionary<string, string>? map)
        {
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (map == null)
                return normalized;

            foreach (var kv in map)
            {
                var key = kv.Key?.Trim() ?? "";
                if (key.Length == 0)
                    continue;
                normalized[key] = kv.Value ?? "";
            }

            return normalized;
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var formD = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(formD.Length);
            foreach (var c in formD)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.NonSpacingMark)
                    continue;
                var lc = char.ToLowerInvariant(c);
                if (char.IsLetterOrDigit(lc))
                    sb.Append(lc);
                else
                    sb.Append(' ');
            }
            return string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        private static float Clamp(float value, float min, float max)
        {
            if (max < min)
                max = min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
