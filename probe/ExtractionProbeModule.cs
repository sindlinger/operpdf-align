using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace Obj.RootProbe
{
    internal static class ExtractionProbeModule
    {
        internal static Dictionary<string, object> Run(
            string pdfPath,
            int page,
            IDictionary<string, string>? values,
            string sideLabel,
            int maxFields = 0)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = true,
                ["module"] = "Obj.RootProbe.ExtractionProbeModule",
                ["status"] = "ok",
                ["pdf"] = pdfPath ?? "",
                ["page"] = page,
                ["side"] = sideLabel ?? "",
                ["fields_total"] = values?.Count ?? 0,
                ["fields_checked"] = 0,
                ["found"] = 0,
                ["missing"] = 0,
                ["items"] = new List<Dictionary<string, object>>()
            };

            var items = (List<Dictionary<string, object>>)result["items"];
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                result["status"] = "file_not_found";
                return result;
            }

            if (page <= 0)
            {
                result["status"] = "invalid_page";
                return result;
            }

            try
            {
                using var doc = new PdfDocument(new PdfReader(pdfPath));
                if (page > doc.GetNumberOfPages())
                {
                    result["status"] = "page_out_of_range";
                    result["pages_total"] = doc.GetNumberOfPages();
                    return result;
                }

                var rawPageText = PdfTextExtractor.GetTextFromPage(doc.GetPage(page), new LocationTextExtractionStrategy()) ?? "";
                var normPageText = NormalizeForSearch(rawPageText);
                var compactPageText = CompactAlphaNum(normPageText);
                var digitsPageText = KeepDigits(normPageText);

                var pairs = (values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
                    .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (maxFields > 0)
                    pairs = pairs.Take(maxFields).ToList();

                var foundCount = 0;
                var checkedCount = 0;
                foreach (var (field, rawValue) in pairs)
                {
                    checkedCount++;
                    var value = rawValue ?? "";
                    var normValue = NormalizeForSearch(value);
                    var compactValue = CompactAlphaNum(normValue);
                    var digitsValue = KeepDigits(normValue);

                    var found = false;
                    var method = "";
                    var firstIndex = -1;
                    var occurrences = 0;
                    var snippet = "";

                    if (!string.IsNullOrWhiteSpace(normValue))
                    {
                        firstIndex = normPageText.IndexOf(normValue, StringComparison.OrdinalIgnoreCase);
                        if (firstIndex >= 0)
                        {
                            found = true;
                            method = "normalized_contains";
                            occurrences = CountOccurrences(normPageText, normValue);
                            snippet = BuildSnippet(normPageText, firstIndex, normValue.Length);
                        }
                    }

                    if (!found && digitsValue.Length >= 6)
                    {
                        firstIndex = digitsPageText.IndexOf(digitsValue, StringComparison.OrdinalIgnoreCase);
                        if (firstIndex >= 0)
                        {
                            found = true;
                            method = "digits_contains";
                            occurrences = CountOccurrences(digitsPageText, digitsValue);
                        }
                    }

                    if (!found && compactValue.Length >= 6)
                    {
                        firstIndex = compactPageText.IndexOf(compactValue, StringComparison.OrdinalIgnoreCase);
                        if (firstIndex >= 0)
                        {
                            found = true;
                            method = "compact_contains";
                            occurrences = CountOccurrences(compactPageText, compactValue);
                        }
                    }

                    if (found)
                        foundCount++;

                    items.Add(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["field"] = field,
                        ["value"] = value,
                        ["found"] = found,
                        ["method"] = method,
                        ["matches"] = Math.Max(occurrences, found ? 1 : 0),
                        ["first_index"] = firstIndex,
                        ["snippet"] = snippet
                    });
                }

                result["fields_checked"] = checkedCount;
                result["found"] = foundCount;
                result["missing"] = checkedCount - foundCount;
                result["page_text_len"] = normPageText.Length;
                result["page_text_sample"] = BuildSnippet(normPageText, 0, Math.Min(180, normPageText.Length));
                return result;
            }
            catch (Exception ex)
            {
                result["status"] = "error";
                result["error"] = ex.GetType().Name + ": " + ex.Message;
                return result;
            }
        }

        private static string NormalizeForSearch(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var s = RemoveDiacritics(text).ToLowerInvariant();
            var sb = new StringBuilder(s.Length);
            var prevSpace = false;
            foreach (var c in s)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!prevSpace)
                    {
                        sb.Append(' ');
                        prevSpace = true;
                    }
                    continue;
                }

                prevSpace = false;
                if (char.IsLetterOrDigit(c) || c == '/' || c == '.' || c == '-' || c == ',' || c == ':' || c == '$' || c == '%' || c == 'º' || c == 'ª')
                    sb.Append(c);
                else
                    sb.Append(' ');
            }

            return sb.ToString().Trim();
        }

        private static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static string KeepDigits(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (char.IsDigit(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string CompactAlphaNum(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
                return 0;
            var count = 0;
            var idx = 0;
            while (idx < haystack.Length)
            {
                idx = haystack.IndexOf(needle, idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    break;
                count++;
                idx += Math.Max(needle.Length, 1);
            }
            return count;
        }

        private static string BuildSnippet(string text, int startIndex, int tokenLen, int radius = 44)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";
            if (startIndex < 0)
                return "";
            var start = Math.Max(0, startIndex - radius);
            var end = Math.Min(text.Length, startIndex + Math.Max(tokenLen, 1) + radius);
            var chunk = text.Substring(start, end - start).Trim();
            if (chunk.Length == 0)
                return "";
            return chunk;
        }
    }
}
