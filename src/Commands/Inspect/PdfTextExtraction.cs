using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Canvas.Parser.Util;

namespace Obj.Commands
{
    internal static class PdfTextExtraction
    {
        internal static double TimeoutSec { get; set; }

        internal readonly struct TextItem
        {
            public TextItem(string text, double x0, double x1, double y, double charWidth)
            {
                Text = text;
                X0 = x0;
                X1 = x1;
                Y = y;
                CharWidth = charWidth;
            }

            public string Text { get; }
            public double X0 { get; }
            public double X1 { get; }
            public double Y { get; }
            public double CharWidth { get; }
        }

        internal readonly struct TextOpItem
        {
            public TextOpItem(string text, double x0, double x1, double y0, double y1, bool hasBox)
            {
                Text = text;
                X0 = x0;
                X1 = x1;
                Y0 = y0;
                Y1 = y1;
                HasBox = hasBox;
            }

            public string Text { get; }
            public double X0 { get; }
            public double X1 { get; }
            public double Y0 { get; }
            public double Y1 { get; }
            public bool HasBox { get; }
        }

        internal static bool TryExtractStreamText(PdfStream stream, PdfResources resources, out string text, out string? error)
        {
            text = "";
            error = null;
            try
            {
                var strategy = new LocationTextExtractionStrategy();
                var processor = new PdfCanvasProcessor(strategy);
                processor.RegisterXObjectDoHandler(PdfName.Form, new FormXObjectDoHandler(resources));
                processor.ProcessContent(stream.GetBytes(), resources ?? new PdfResources(new PdfDictionary()));
                text = strategy.GetResultantText();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static List<TextItem> CollectTextItems(PdfStream stream, PdfResources resources)
        {
            var items = new List<TextItem>();
            try
            {
                var collector = new TextItemCollector(items);
                var processor = new PdfCanvasProcessor(collector);
                processor.RegisterXObjectDoHandler(PdfName.Form, new FormXObjectDoHandler(resources));
                if (!RunWithTimeout(() => processor.ProcessContent(stream.GetBytes(), resources ?? new PdfResources(new PdfDictionary()))))
                    return items;
            }
            catch
            {
                return items;
            }
            return items;
        }

        internal static List<string> CollectTextPieces(PdfStream stream, PdfResources resources)
        {
            var items = CollectTextItems(stream, resources);
            var list = new List<string>(items.Count);
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.Text))
                    list.Add(item.Text);
            }
            return list;
        }

        internal static List<string> CollectTextOperatorTexts(PdfStream stream, PdfResources resources)
        {
            var texts = new List<string>();
            try
            {
                var collector = new TextRenderInfoCollector(texts);
                var processor = new PdfCanvasProcessor(collector);
                processor.RegisterXObjectDoHandler(PdfName.Form, new FormXObjectDoHandler(resources));
                if (!RunWithTimeout(() => processor.ProcessContent(stream.GetBytes(), resources ?? new PdfResources(new PdfDictionary()))))
                    return texts;
            }
            catch
            {
                return texts;
            }
            return texts;
        }

        internal static List<TextOpItem> CollectTextOperatorItems(PdfStream stream, PdfResources resources)
        {
            var items = new List<TextOpItem>();
            try
            {
                var collector = new TextOpItemCollector(items);
                var processor = new PdfCanvasProcessor(collector);
                processor.RegisterXObjectDoHandler(PdfName.Form, new FormXObjectDoHandler(resources));
                if (!RunWithTimeout(() => processor.ProcessContent(stream.GetBytes(), resources ?? new PdfResources(new PdfDictionary()))))
                    return items;
            }
            catch
            {
                return items;
            }
            return items;
        }

        private static bool RunWithTimeout(Action action)
        {
            if (TimeoutSec <= 0)
            {
                action();
                return true;
            }

            try
            {
                var task = Task.Run(action);
                if (task.Wait(TimeSpan.FromSeconds(TimeoutSec)))
                    return true;
            }
            catch
            {
                return false;
            }

            Console.Error.WriteLine($"[timeout] textops > {TimeoutSec:0.0}s");
            return false;
        }

        internal static bool TryFindResourcesForObjId(PdfDocument doc, int objId, out PdfResources resources)
        {
            resources = new PdfResources(new PdfDictionary());
            if (objId <= 0) return false;

            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var pageResources = page.GetResources() ?? new PdfResources(new PdfDictionary());
                var contents = page.GetPdfObject().Get(PdfName.Contents);
                foreach (var stream in EnumerateStreams(contents))
                {
                    var id = stream.GetIndirectReference()?.GetObjNumber() ?? 0;
                    if (id == objId)
                    {
                        resources = pageResources;
                        return true;
                    }
                }

                var xobjects = pageResources.GetResource(PdfName.XObject) as PdfDictionary;
                if (xobjects == null) continue;
                foreach (var name in xobjects.KeySet())
                {
                    var xs = xobjects.GetAsStream(name);
                    if (xs == null) continue;
                    var id = xs.GetIndirectReference()?.GetObjNumber() ?? 0;
                    if (id == objId)
                    {
                        var xresDict = xs.GetAsDictionary(PdfName.Resources);
                        resources = xresDict != null ? new PdfResources(xresDict) : pageResources;
                        return true;
                    }
                }
            }

            return false;
        }

        private sealed class TextItemCollector : IEventListener
        {
            private readonly List<TextItem> _items;

            public TextItemCollector(List<TextItem> items)
            {
                _items = items;
            }

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type != EventType.RENDER_TEXT) return;
                if (data is not TextRenderInfo tri) return;
                var text = tri.GetText();
                if (string.IsNullOrEmpty(text)) return;
                var baseLine = tri.GetBaseline();
                var start = baseLine.GetStartPoint();
                var end = baseLine.GetEndPoint();
                double x0 = start.Get(Vector.I1);
                double x1 = end.Get(Vector.I1);
                double y = start.Get(Vector.I2);
                double width = Math.Abs(x1 - x0);
                double charWidth = width / Math.Max(1, text.Length);
                _items.Add(new TextItem(text, x0, x1, y, charWidth));
            }

            public ICollection<EventType> GetSupportedEvents()
            {
                return new HashSet<EventType> { EventType.RENDER_TEXT };
            }
        }

        private sealed class TextRenderInfoCollector : IEventListener
        {
            private readonly List<string> _texts;

            public TextRenderInfoCollector(List<string> texts)
            {
                _texts = texts;
            }

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type != EventType.RENDER_TEXT) return;
                if (data is not TextRenderInfo tri) return;
                string decoded = tri.GetText() ?? "";
                if (string.IsNullOrEmpty(decoded))
                {
                    try
                    {
                        var pdfString = tri.GetPdfString();
                        var font = tri.GetFont();
                        if (pdfString != null && font != null)
                            decoded = font.Decode(pdfString) ?? "";
                    }
                    catch
                    {
                        decoded = "";
                    }
                }

                _texts.Add(decoded ?? "");
            }

            public ICollection<EventType> GetSupportedEvents()
            {
                return new HashSet<EventType> { EventType.RENDER_TEXT };
            }
        }

        private sealed class TextOpItemCollector : IEventListener
        {
            private readonly List<TextOpItem> _items;

            public TextOpItemCollector(List<TextOpItem> items)
            {
                _items = items;
            }

            public void EventOccurred(IEventData data, EventType type)
            {
                if (type != EventType.RENDER_TEXT) return;
                if (data is not TextRenderInfo tri) return;
                var text = tri.GetText() ?? "";
                if (string.IsNullOrEmpty(text))
                {
                    try
                    {
                        var pdfString = tri.GetPdfString();
                        var font = tri.GetFont();
                        if (pdfString != null && font != null)
                            text = font.Decode(pdfString) ?? "";
                    }
                    catch
                    {
                        text = tri.GetText() ?? "";
                    }
                }

                var spaced = BuildTextFromChars(tri);
                if (!string.IsNullOrWhiteSpace(spaced) && CountSpaces(spaced) > CountSpaces(text))
                    text = spaced;

                var ascent = tri.GetAscentLine();
                var descent = tri.GetDescentLine();
                var a0 = ascent.GetStartPoint();
                var a1 = ascent.GetEndPoint();
                var d0 = descent.GetStartPoint();
                var d1 = descent.GetEndPoint();

                double minX = Math.Min(Math.Min(a0.Get(Vector.I1), a1.Get(Vector.I1)), Math.Min(d0.Get(Vector.I1), d1.Get(Vector.I1)));
                double maxX = Math.Max(Math.Max(a0.Get(Vector.I1), a1.Get(Vector.I1)), Math.Max(d0.Get(Vector.I1), d1.Get(Vector.I1)));
                double minY = Math.Min(Math.Min(a0.Get(Vector.I2), a1.Get(Vector.I2)), Math.Min(d0.Get(Vector.I2), d1.Get(Vector.I2)));
                double maxY = Math.Max(Math.Max(a0.Get(Vector.I2), a1.Get(Vector.I2)), Math.Max(d0.Get(Vector.I2), d1.Get(Vector.I2)));

                var hasBox = !double.IsNaN(minX) && !double.IsNaN(maxX) && !double.IsNaN(minY) && !double.IsNaN(maxY);
                _items.Add(new TextOpItem(text ?? "", minX, maxX, minY, maxY, hasBox));
            }

            public ICollection<EventType> GetSupportedEvents()
            {
                return new HashSet<EventType> { EventType.RENDER_TEXT };
            }
        }

        private static string BuildTextFromChars(TextRenderInfo tri)
        {
            try
            {
                var chars = tri.GetCharacterRenderInfos();
                if (chars == null || chars.Count == 0) return "";
                var sb = new StringBuilder();
                double spaceWidth = tri.GetSingleSpaceWidth();
                if (spaceWidth <= 0) spaceWidth = 0;
                var widths = new List<double>(chars.Count);
                foreach (var ch in chars)
                {
                    var baseLine = ch.GetBaseline();
                    var start = baseLine.GetStartPoint();
                    var end = baseLine.GetEndPoint();
                    var w = Math.Abs(end.Get(Vector.I1) - start.Get(Vector.I1));
                    if (w > 0)
                        widths.Add(w);
                }
                double medianWidth = 0;
                if (widths.Count > 0)
                {
                    widths.Sort();
                    medianWidth = widths[widths.Count / 2];
                }
                var gapThreshold = spaceWidth > 0
                    ? Math.Max(spaceWidth * 0.4, medianWidth > 0 ? medianWidth * 0.6 : 0)
                    : (medianWidth > 0 ? Math.Max(1.0, medianWidth * 0.6) : 1.5);
                double? prevX1 = null;
                foreach (var ch in chars)
                {
                    var t = ch.GetText();
                    if (string.IsNullOrEmpty(t)) continue;
                    var baseLine = ch.GetBaseline();
                    var start = baseLine.GetStartPoint();
                    var end = baseLine.GetEndPoint();
                    double x0 = start.Get(Vector.I1);
                    double x1 = end.Get(Vector.I1);
                    if (prevX1.HasValue)
                    {
                        var gap = x0 - prevX1.Value;
                        if (gap > gapThreshold)
                            sb.Append(' ');
                    }
                    sb.Append(t);
                    prevX1 = x1;
                }
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }

        private static int CountSpaces(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0;
            foreach (var c in text)
                if (c == ' ') count++;
            return count;
        }

        private sealed class FormXObjectDoHandler : IXObjectDoHandler
        {
            private readonly PdfResources _parentResources;

            public FormXObjectDoHandler(PdfResources parentResources)
            {
                _parentResources = parentResources ?? new PdfResources(new PdfDictionary());
            }

            public void HandleXObject(PdfCanvasProcessor processor, Stack<CanvasTag> canvasTagHierarchy, PdfStream xObjectStream, PdfName resourceName)
            {
                if (xObjectStream == null) return;
                var resDict = xObjectStream.GetAsDictionary(PdfName.Resources);
                var res = resDict != null ? new PdfResources(resDict) : _parentResources;
                processor.ProcessContent(xObjectStream.GetBytes(), res ?? new PdfResources(new PdfDictionary()));
            }
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
                    if (item is PdfStream ss) yield return ss;
            }
        }
    }
}
