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

namespace Obj.Commands
{
    internal static class BuildAnchorModelDespacho
    {
        private enum RenderMode
        {
            Placeholder,
            AnchorPhrase,
            MatchedLine
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

        private sealed class PlaceholderRule
        {
            public string Tag { get; set; } = "";
            public string Label { get; set; } = "";
            public int MaxPerPage { get; set; } = 2;
        }

        private sealed class AnchorRule
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

        private static readonly AnchorRule[] Rules = new[]
        {
            new AnchorRule { Label = "ANC_TRIBUNAL", Needles = new[] { "tribunal de justica", "poder judiciario" } },
            new AnchorRule { Label = "ANC_PROCESSO_ADMIN", Needles = new[] { "processo n", "processon", "processo nº" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_REQUERENTE", Needles = new[] { "requerente:" } },
            new AnchorRule { Label = "ANC_INTERESSADO", Needles = new[] { "interessad" } },
            new AnchorRule { Label = "ANC_PERITO", Needles = new[] { "perito", "perita" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_ESPECIALIDADE", Needles = new[] { "assistente social", "engenheiro", "grafotecnico", "especialidade" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_VALOR_JZ", Needles = new[] { "valor de r$", "no valor de" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_CPF_PERITO", Needles = new[] { "cpf" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_PROCESSO_JUDICIAL", Needles = new[] { "pericia nos autos", "acao", "processo n" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_PROMOVENTE", Needles = new[] { "movido por", "autor" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_PROMOVIDO", Needles = new[] { "em face de", "reu" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_VARA", Needles = new[] { "juizo da", "vara" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_COMARCA", Needles = new[] { "comarca" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_ENCAMINHE", Needles = new[] { "encaminhem-se", "encaminhem se" } },
            new AnchorRule { Label = "ANC_DIRETORIA", Needles = new[] { "diretoria especial" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_ASSINATURA", Needles = new[] { "documento assinado eletronicamente", "diretor especial" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_DATA_DESPESA", Needles = new[] { "joao pessoa", "assinado" }, MaxPerPage = 2 },
            new AnchorRule { Label = "ANC_VERIFICADOR", Needles = new[] { "codigo verificador", "crc" } }
        };

        private static readonly PlaceholderRule[] PlaceholderRules = new[]
        {
            new PlaceholderRule { Tag = "<PROCESSO_ADMINISTRATIVO>", Label = "ANC_PROCESSO_ADMIN", MaxPerPage = 1 },
            new PlaceholderRule { Tag = "<PROCESSO_JUDICIAL>", Label = "ANC_PROCESSO_JUDICIAL", MaxPerPage = 2 },
            new PlaceholderRule { Tag = "<VARA>", Label = "ANC_VARA", MaxPerPage = 3 },
            new PlaceholderRule { Tag = "<COMARCA>", Label = "ANC_COMARCA", MaxPerPage = 3 },
            new PlaceholderRule { Tag = "<PERITO>", Label = "ANC_PERITO", MaxPerPage = 3 },
            new PlaceholderRule { Tag = "<CPF_PERITO>", Label = "ANC_CPF_PERITO", MaxPerPage = 2 },
            new PlaceholderRule { Tag = "<PROMOVENTE>", Label = "ANC_PROMOVENTE", MaxPerPage = 2 },
            new PlaceholderRule { Tag = "<PROMOVIDO>", Label = "ANC_PROMOVIDO", MaxPerPage = 2 },
            new PlaceholderRule { Tag = "<ESPECIALIDADE>", Label = "ANC_ESPECIALIDADE", MaxPerPage = 2 },
            new PlaceholderRule { Tag = "<ESPECIE_DA_PERICIA>", Label = "ANC_ESPECIE_PERICIA", MaxPerPage = 2 },
            new PlaceholderRule { Tag = "<VALOR_ARBITRADO_JZ>", Label = "ANC_VALOR_JZ", MaxPerPage = 1 },
            new PlaceholderRule { Tag = "<VALOR_ARBITRADO_DE>", Label = "ANC_VALOR_DE", MaxPerPage = 1 },
            new PlaceholderRule { Tag = "<DATA_DESPESA>", Label = "ANC_DATA_DESPESA", MaxPerPage = 2 }
        };

        public static void Execute(string[] args)
        {
            var modelPath = ResolveDefaultModelPath();
            var outPath = Path.Combine("reference", "models", "tjpb_despacho_anchor_model.pdf");
            var minTextLen = 3;
            var renderMode = RenderMode.Placeholder;

            if (!ParseArgs(args, ref modelPath, ref outPath, ref minTextLen, ref renderMode))
                return;

            if (!File.Exists(modelPath))
            {
                Console.WriteLine($"Modelo não encontrado: {modelPath}");
                return;
            }

            var chunks = ExtractChunks(modelPath, minTextLen);
            if (chunks.Count == 0)
            {
                Console.WriteLine("Nenhum texto útil encontrado no modelo.");
                return;
            }

            var placements = SelectAnchorPlacements(chunks);
            if (placements.Count == 0)
            {
                Console.WriteLine("Nenhuma âncora encontrada com as regras atuais.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
            WriteAnchorModelPdf(modelPath, outPath, placements, renderMode);
            var metaPath = WriteAnchorMetadata(outPath, modelPath, placements);

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

        private static bool ParseArgs(string[] args, ref string modelPath, ref string outPath, ref int minTextLen, ref RenderMode renderMode)
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
            Console.WriteLine("operpdf build-anchor-model-despacho [--model <pdf>] [--out <pdf>] [--min-text-len N]");
            Console.WriteLine("Exemplo:");
            Console.WriteLine("  operpdf build-anchor-model-despacho --model reference/models/tjpb_despacho_model.pdf --out reference/models/tjpb_despacho_anchor_model.pdf --render anchor");
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

        private static List<AnchorPlacement> SelectAnchorPlacements(List<TextChunk> chunks)
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
                    foreach (var rule in PlaceholderRules)
                    {
                        var used = usedByLabel.TryGetValue(rule.Label, out var count) ? count : 0;
                        if (used >= rule.MaxPerPage)
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
                foreach (var rule in Rules)
                {
                    var used = usedByLabel.TryGetValue(rule.Label, out var n) ? n : 0;
                    foreach (var chunk in pageChunks)
                    {
                        if (used >= rule.MaxPerPage)
                            break;

                        var normalized = Normalize(chunk.Text);
                        if (normalized.Length == 0)
                            continue;
                        var matchedNeedle = rule.Needles.FirstOrDefault(nl => normalized.Contains(nl, StringComparison.Ordinal));
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

                    if (!slotsByRow.TryGetValue(rowKey, out var rowSlots))
                    {
                        rowSlots = new List<(float X, float W)>();
                        slotsByRow[rowKey] = rowSlots;
                    }
                    else
                    {
                        foreach (var slot in rowSlots.OrderBy(s => s.X))
                        {
                            var minX = slot.X + slot.W + 6f;
                            if (x < minX && x + widthHint > slot.X - 3f)
                                x = minX;
                        }
                    }

                    rowSlots.Add((x, widthHint));
                    adjusted.Add(new AnchorPlacement
                    {
                        Page = p.Page,
                        Label = p.Label,
                        MatchedText = p.MatchedText,
                        X = x,
                        Y = p.Y,
                        Height = p.Height
                    });
                }
            }

            return adjusted;
        }

        private static void WriteAnchorModelPdf(string modelPath, string outPath, List<AnchorPlacement> placements, RenderMode renderMode)
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

                foreach (var anchor in placements.Where(p => p.Page == pageNum))
                {
                    var text = ResolveAnchorRenderText(anchor, renderMode);
                    var widthHint = Math.Max(32f, Math.Min(280f, text.Length * 4.8f));
                    var x = Clamp(anchor.X, 10f, size.GetWidth() - widthHint - 10f);
                    var y = Clamp(anchor.Y, 10f, size.GetHeight() - 14f);
                    canvas.ShowTextAligned(text, x, y + 0.5f, TextAlignment.LEFT, VerticalAlignment.BOTTOM, 0f);
                }
            }
        }

        private static string ResolveAnchorRenderText(AnchorPlacement anchor, RenderMode renderMode)
        {
            if (renderMode == RenderMode.MatchedLine)
            {
                var raw = TextNormalization.NormalizeWhitespace(anchor?.MatchedText ?? "");
                return string.IsNullOrWhiteSpace(raw) ? ResolveAnchorPhrase(anchor?.Label ?? "") : raw;
            }

            if (renderMode == RenderMode.AnchorPhrase)
                return ResolveAnchorPhrase(anchor?.Label ?? "");

            var label = anchor?.Label ?? "";
            switch (label)
            {
                case "ANC_PROCESSO_ADMIN": return "<PROCESSO_ADMINISTRATIVO>";
                case "ANC_PROCESSO_JUDICIAL": return "<PROCESSO_JUDICIAL>";
                case "ANC_REQUERENTE": return "<REQUERENTE>";
                case "ANC_INTERESSADO": return "<INTERESSADO>";
                case "ANC_VARA": return "<VARA>";
                case "ANC_COMARCA": return "<COMARCA>";
                case "ANC_PERITO": return "<PERITO>";
                case "ANC_CPF_PERITO": return "<CPF_PERITO>";
                case "ANC_PROMOVENTE": return "<PROMOVENTE>";
                case "ANC_PROMOVIDO": return "<PROMOVIDO>";
                case "ANC_ESPECIALIDADE": return "<ESPECIALIDADE>";
                case "ANC_ESPECIE_PERICIA": return "<ESPECIE_DA_PERICIA>";
                case "ANC_VALOR_JZ": return "<VALOR_ARBITRADO_JZ>";
                case "ANC_VALOR_DE": return "<VALOR_ARBITRADO_DE>";
                case "ANC_DATA_DESPESA": return "<DATA_DESPESA>";
                case "ANC_TRIBUNAL": return "<TRIBUNAL>";
                case "ANC_DIRETORIA": return "<DIRETORIA>";
                case "ANC_ASSINATURA": return "<ASSINATURA>";
                case "ANC_ENCAMINHE": return "<ENCAMINHE-SE>";
                case "ANC_VERIFICADOR": return "<VERIFICADOR>";
            }

            var text = (anchor?.MatchedText ?? "").Trim();
            if (text.StartsWith("<", StringComparison.Ordinal) && text.EndsWith(">", StringComparison.Ordinal))
                return text.Trim('<', '>').Replace('_', ' ');

            if (label.StartsWith("ANC_", StringComparison.OrdinalIgnoreCase))
                return "<" + label.Substring(4) + ">";

            return string.IsNullOrWhiteSpace(text) ? "ANCORA" : text;
        }

        private static string ResolveAnchorPhrase(string label)
        {
            switch (label ?? "")
            {
                case "ANC_TRIBUNAL": return "Tribunal de Justiça";
                case "ANC_DIRETORIA": return "Diretoria Especial";
                case "ANC_PROCESSO_ADMIN": return "Processo nº";
                case "ANC_PROCESSO_JUDICIAL": return "processo nº";
                case "ANC_REQUERENTE": return "Requerente:";
                case "ANC_INTERESSADO": return "Interessado:";
                case "ANC_PERITO": return "Perito";
                case "ANC_ESPECIALIDADE": return "Especialidade";
                case "ANC_CPF_PERITO": return "CPF";
                case "ANC_VALOR_JZ":
                case "ANC_VALOR_DE": return "valor de R$";
                case "ANC_PROMOVENTE": return "movido por";
                case "ANC_PROMOVIDO": return "em face de";
                case "ANC_VARA": return "Juízo da";
                case "ANC_COMARCA": return "Comarca de";
                case "ANC_ENCAMINHE": return "encaminhem-se os presentes autos";
                case "ANC_ASSINATURA": return "Documento assinado eletronicamente";
                case "ANC_DATA_DESPESA": return "em João Pessoa";
                case "ANC_VERIFICADOR": return "código verificador";
                default:
                    if (!string.IsNullOrWhiteSpace(label) && label.StartsWith("ANC_", StringComparison.OrdinalIgnoreCase))
                        return label.Substring(4).Replace('_', ' ');
                    return "âncora";
            }
        }

        private static string WriteAnchorMetadata(string outPath, string modelPath, List<AnchorPlacement> placements)
        {
            var metaPath = Path.Combine(
                Path.GetDirectoryName(outPath) ?? ".",
                Path.GetFileNameWithoutExtension(outPath) + ".anchors.json");

            var payload = new
            {
                source_pdf = Path.GetFullPath(modelPath),
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
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
