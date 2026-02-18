using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using iText.Kernel.Pdf;
using iText.Signatures;
using iText.Forms;
using iText.Kernel.Geom;
using Obj.TjpbDespachoExtractor.Models;

namespace Obj.TjpbDespachoExtractor.Utils
{
    public class SignatureMeta
    {
        public string SignerName { get; set; } = "";
        public string FieldName { get; set; } = "";
        public DateTime? SignDate { get; set; }
    }

    public static class SignatureExtractor
    {
        public static List<Obj.TjpbDespachoExtractor.Models.SignatureInfo> ExtractSignatures(string filePath)
        {
            var list = new List<Obj.TjpbDespachoExtractor.Models.SignatureInfo>();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return list;

            try
            {
                using var reader = new PdfReader(filePath);
                using var pdfDoc = new PdfDocument(reader);
                var util = new SignatureUtil(pdfDoc);
                var names = util.GetSignatureNames();
                if (names == null || names.Count == 0)
                    return list;

                var acroForm = PdfAcroForm.GetAcroForm(pdfDoc, false);
                foreach (var fieldName in names)
                {
                    var pkcs7 = util.ReadSignatureData(fieldName);
                    var signerName = NormalizeSignerName(pkcs7?.GetSignName() ?? "");
                    if (string.IsNullOrWhiteSpace(signerName))
                    {
                        var cert = pkcs7?.GetSigningCertificate();
                        if (cert != null)
                            signerName = NormalizeSignerName(cert.SubjectDN?.ToString() ?? "");
                    }

                    var reason = pkcs7?.GetReason() ?? "";
                    var location = pkcs7?.GetLocation() ?? "";
                    var signDate = pkcs7?.GetSignDate();

                    var field = acroForm?.GetField(fieldName);
                    var widgets = field?.GetWidgets();
                    if (widgets == null || widgets.Count == 0)
                    {
                        list.Add(new Obj.TjpbDespachoExtractor.Models.SignatureInfo
                        {
                            Method = "digital",
                            FieldName = fieldName,
                            SignerName = signerName,
                            Reason = reason ?? "",
                            Location = location ?? "",
                            SignDate = signDate?.ToUniversalTime().ToString("o"),
                            Page1 = 0,
                            BBoxN = null
                        });
                        continue;
                    }

                    foreach (var widget in widgets)
                    {
                        var page = widget.GetPage();
                        var page1 = page != null ? pdfDoc.GetPageNumber(page) : 0;
                        var bboxN = NormalizeRect(widget.GetRectangle()?.ToRectangle(), page?.GetPageSize());

                        list.Add(new Obj.TjpbDespachoExtractor.Models.SignatureInfo
                        {
                            Method = "digital",
                            FieldName = fieldName,
                            SignerName = signerName,
                            Reason = reason ?? "",
                            Location = location ?? "",
                            SignDate = signDate?.ToUniversalTime().ToString("o"),
                            Page1 = page1,
                            BBoxN = bboxN
                        });
                    }
                }
            }
            catch
            {
                return list;
            }

            return list;
        }

        public static List<Obj.TjpbDespachoExtractor.Models.SignatureInfo> ExtractTextSignatures(IEnumerable<BandInfo> bands)
        {
            var list = new List<Obj.TjpbDespachoExtractor.Models.SignatureInfo>();
            if (bands == null) return list;
            var catalog = SignaturePatternCatalog.Current;
            foreach (var band in bands)
            {
                var text = band.Text ?? "";
                if (string.IsNullOrWhiteSpace(text)) continue;
                var m = catalog.AssinanteMain.Match(text);
                if (m.Success)
                {
                    var name = NormalizeSignerName(m.Groups["name"].Success ? m.Groups["name"].Value.Trim() : m.Groups[1].Value.Trim());
                    var signDate = TryExtractDate(text);
                    list.Add(new Obj.TjpbDespachoExtractor.Models.SignatureInfo
                    {
                        Method = "text_anchor",
                        FieldName = "",
                        SignerName = name,
                        SignDate = signDate,
                        Page1 = band.Page1,
                        BBoxN = band.BBoxN,
                        Snippet = TextUtils.SafeSnippet(text, Math.Max(0, m.Index - 40), Math.Min(text.Length - Math.Max(0, m.Index - 40), 160))
                    });
                    continue;
                }

                var mDir = catalog.Diretor.Match(text);
                if (mDir.Success)
                {
                    var name = NormalizeSignerName(mDir.Groups["name"].Success ? mDir.Groups["name"].Value.Trim() : mDir.Groups[1].Value.Trim());
                    var signDate = TryExtractDate(text);
                    list.Add(new Obj.TjpbDespachoExtractor.Models.SignatureInfo
                    {
                        Method = "text_anchor",
                        FieldName = "",
                        SignerName = name,
                        SignDate = signDate,
                        Page1 = band.Page1,
                        BBoxN = band.BBoxN,
                        Snippet = TextUtils.SafeSnippet(text, Math.Max(0, mDir.Index - 40), Math.Min(text.Length - Math.Max(0, mDir.Index - 40), 160))
                    });
                }
            }
            return list;
        }

        public static SignatureMeta? TryExtractSigner(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                var list = ExtractSignatures(filePath);
                if (list.Count == 0) return null;
                var last = list[list.Count - 1];
                if (string.IsNullOrWhiteSpace(last.SignerName)) return null;
                return new SignatureMeta
                {
                    SignerName = last.SignerName,
                    FieldName = last.FieldName,
                    SignDate = DateTime.TryParse(last.SignDate, out var dt) ? dt : null
                };
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeSignerName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var v = value;
            var catalog = SignaturePatternCatalog.Current;
            var m = catalog.Cn.Match(v);
            if (m.Success)
                v = m.Groups[1].Value;
            v = TextUtils.CollapseSpacedLettersText(v);
            v = Regex.Replace(v, @"[^A-Za-zÁÂÃÀÉÊÍÓÔÕÚÇ\s'-]+", " ");
            return TextUtils.NormalizeWhitespace(v);
        }

        private static string? TryExtractDate(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var catalog = SignaturePatternCatalog.Current;
            var m = catalog.DatePt.Match(text);
            if (m.Success && TextUtils.TryParseDate(m.Groups[1].Value, out var iso))
                return iso;
            var m2 = catalog.DateSlash.Match(text);
            if (m2.Success && TextUtils.TryParseDate(m2.Groups[1].Value, out var iso2))
                return iso2;
            return null;
        }

        private static BBoxN? NormalizeRect(Rectangle? rect, Rectangle? pageRect)
        {
            if (rect == null || pageRect == null) return null;
            var width = pageRect.GetWidth();
            var height = pageRect.GetHeight();
            if (width <= 0 || height <= 0) return null;
            var x0 = rect.GetLeft() / width;
            var y0 = rect.GetBottom() / height;
            var x1 = rect.GetRight() / width;
            var y1 = rect.GetTop() / height;
            return new BBoxN
            {
                X0 = Math.Max(0, Math.Min(1, x0)),
                Y0 = Math.Max(0, Math.Min(1, y0)),
                X1 = Math.Max(0, Math.Min(1, x1)),
                Y1 = Math.Max(0, Math.Min(1, y1))
            };
        }
    }
}
