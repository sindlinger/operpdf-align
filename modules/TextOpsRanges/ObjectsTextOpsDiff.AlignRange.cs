using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Encodings.Web;
using DiffMatchPatch;
using Obj.Models;
using Obj.Utils;
using Obj.TjpbDespachoExtractor.Utils;
using iText.Kernel.Pdf;
using PdfTextExtraction = Obj.Commands.PdfTextExtraction;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.Align
{
    internal static partial class ObjectsTextOpsDiff
    {
        internal sealed class FullTextDiffReport
        {
            public string Range { get; set; } = "";
            public string Roi { get; set; } = "";
            public List<(string File, string OpRange)> Ranges { get; set; } = new();
            public List<(string File, string Text, int Len)> RangeTexts { get; set; } = new();
            public List<FullTextDiffPair> Pairs { get; set; } = new();
        }

        internal sealed class FullTextDiffPair
        {
            public string A { get; set; } = "";
            public string B { get; set; } = "";
            public List<FullTextDiffItem> Items { get; set; } = new();
        }

        internal sealed class FullTextDiffItem
        {
            public string Kind { get; set; } = ""; // EQ|DEL|INS
            public string File { get; set; } = "";
            public string OpRange { get; set; } = "";
            public string Text { get; set; } = "";
            public int Len { get; set; }
        }

        private static string NormalizeForSimilarity(string text)
        {
            text = CollapseSpaces(NormalizeBlockToken(text));
            var useSemanticHint = HasExplicitAnchorMarker(text) || IsAnchorModelCue(text);
            if (useSemanticHint)
            {
                var anchorHint = TryAnchorSemanticHint(text);
                if (!string.IsNullOrWhiteSpace(anchorHint))
                    text = anchorHint;
            }
            if (string.IsNullOrEmpty(text))
                return "";
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (char.IsDigit(c))
                    sb.Append('#');
                else
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static bool HasExplicitAnchorMarker(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (Regex.IsMatch(text, "<\\s*[A-Za-z_][A-Za-z0-9_]*\\s*>", RegexOptions.CultureInvariant))
                return true;

            return text.IndexOf("ANC_", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("[ANC", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("ANC ", StringComparison.OrdinalIgnoreCase) == 0;
        }

        private static string TryAnchorSemanticHint(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var s = RemoveDiacritics(text).ToUpperInvariant();
            var compact = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '_')
                    compact.Append(c);
            }

            var token = compact.ToString();
            if (string.IsNullOrWhiteSpace(token))
                return "";

            // Captura variantes como [ANC_*], ANC_*, ANC*** com espaçamento/OCR ruidoso.
            var idx = token.IndexOf("ANC", StringComparison.Ordinal);
            if (idx >= 0)
                token = token.Substring(idx + 3).Trim('_');

            if (token.Length == 0)
                return "";

            if (token.Contains("PROCESSOADMIN", StringComparison.Ordinal) || token.Contains("PROCESSO_ADMIN", StringComparison.Ordinal))
                return "processo administrativo";
            if (token.Contains("PROCESSOJUDICIAL", StringComparison.Ordinal) || token.Contains("PROCESSO_JUDICIAL", StringComparison.Ordinal))
                return "processo judicial";
            if (token.Contains("VARA", StringComparison.Ordinal))
                return "vara";
            if (token.Contains("COMARCA", StringComparison.Ordinal))
                return "comarca";
            if (token.Contains("CPF", StringComparison.Ordinal))
                return "cpf";
            if (token.Contains("PERITO", StringComparison.Ordinal))
                return "perito";
            if (token.Contains("PROMOVENTE", StringComparison.Ordinal))
                return "movido por";
            if (token.Contains("PROMOVIDO", StringComparison.Ordinal))
                return "em face de";
            if (token.Contains("ESPECIALIDADE", StringComparison.Ordinal))
                return "especialidade";
            if (token.Contains("ESPECIE", StringComparison.Ordinal) || token.Contains("PERICIA", StringComparison.Ordinal))
                return "pericia";
            if (token.Contains("VALOR", StringComparison.Ordinal))
                return "valor r";
            if (token.Contains("TRIBUNAL", StringComparison.Ordinal))
                return "tribunal de justica";
            if (token.Contains("DIRETORIA", StringComparison.Ordinal))
                return "diretoria especial";
            if (token.Contains("ASSINATURA", StringComparison.Ordinal))
                return "assinatura";
            if (token.Contains("ENCAMINHE", StringComparison.Ordinal))
                return "encaminhe se";
            if (token.Contains("DATA", StringComparison.Ordinal))
                return "data";

            return "";
        }

        private static bool IsAnchorModelCue(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var s = RemoveDiacritics(text).ToLowerInvariant();
            s = Regex.Replace(s, @"[^a-z0-9_ ]", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            if (s.Length == 0)
                return false;

            if (s.Contains("anc", StringComparison.Ordinal))
                return true;
            if (s.Length > 44)
                return false;

            return s == "vara"
                || s == "comarca"
                || s == "cpf"
                || s == "perito"
                || s == "especialidade"
                || s == "pericia"
                || s == "processo administrativo"
                || s == "processo judicial"
                || s == "movido por"
                || s == "em face de"
                || s == "valor r"
                || s == "data";
        }

        private static double ComputeSimilarity(string a, string b)
        {
            if (a.Length == 0 && b.Length == 0)
                return 1.0;
            if (a.Length == 0 || b.Length == 0)
                return 0.0;

            var dmp = new diff_match_patch();
            var diffs = dmp.diff_main(a, b, false);
            var dist = dmp.diff_levenshtein(diffs);
            var maxLen = Math.Max(a.Length, b.Length);
            if (maxLen == 0)
                return 0.0;
            var textSim = 1.0 - (double)dist / maxLen;
            var lenSim = 1.0 - (double)Math.Abs(a.Length - b.Length) / maxLen;
            return (textSim * 0.7) + (lenSim * 0.3);
        }

        private static double ComputeAlignmentSimilarity(string a, string b)
        {
            var sim = ComputeSimilarity(a, b);
            var labelA = AnalyzeLeadingFieldLabel(a);
            var labelB = AnalyzeLeadingFieldLabel(b);

            if (labelA.Label.Length > 0 && labelB.Label.Length > 0)
            {
                if (string.Equals(labelA.Label, labelB.Label, StringComparison.Ordinal))
                {
                    var c = Math.Min(labelA.Confidence, labelB.Confidence);
                    sim += c >= 0.9 ? 0.24 : 0.16;
                }
                else
                {
                    var c = Math.Min(labelA.Confidence, labelB.Confidence);
                    var critical = IsCriticalFieldLabel(labelA.Label) && IsCriticalFieldLabel(labelB.Label);
                    // Prioriza casamento de campo correto e evita cruzamento entre campos estruturados.
                    if (critical && c >= 0.9) sim -= 0.64;
                    else if (critical && c >= 0.75) sim -= 0.44;
                    else if (c >= 0.9) sim -= 0.34;
                    else if (c >= 0.7) sim -= 0.24;
                    else sim -= 0.14;
                }
            }
            else if (labelA.Label.Length > 0 || labelB.Label.Length > 0)
            {
                // Evita casar bloco de campo estruturado com frase corrida.
                var c = Math.Max(labelA.Confidence, labelB.Confidence);
                sim -= c >= 0.9 ? 0.16 : 0.10;
            }

            if (sim > 1.0) return 1.0;
            if (sim < -1.0) return -1.0;
            return sim;
        }

        private static bool ShouldRejectAsAnchorMismatch(string a, string b)
        {
            var labelA = AnalyzeLeadingFieldLabel(a);
            var labelB = AnalyzeLeadingFieldLabel(b);
            if (labelA.Label.Length == 0 || labelB.Label.Length == 0)
                return false;
            if (string.Equals(labelA.Label, labelB.Label, StringComparison.Ordinal))
                return false;
            if (!IsCriticalFieldLabel(labelA.Label) || !IsCriticalFieldLabel(labelB.Label))
                return false;
            // Bloqueia ancoragem somente em conflito forte de campos estruturados.
            return labelA.Confidence >= 0.9 && labelB.Confidence >= 0.9;
        }

        private readonly struct LabelInfo
        {
            public LabelInfo(string label, double confidence)
            {
                Label = label;
                Confidence = confidence;
            }

            public string Label { get; }
            public double Confidence { get; }
        }

        private static LabelInfo AnalyzeLeadingFieldLabel(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new LabelInfo("", 0.0);

            var t = text.Trim();
            t = TextUtils.NormalizeWhitespace(TextUtils.CollapseSpacedLettersText(t));
            t = Regex.Replace(t, "(?<=[a-záâãàéêíóôõúç])(?=[A-ZÁÂÃÀÉÊÍÓÔÕÚÇ])", " ");
            t = TextUtils.NormalizeWhitespace(t);
            var best = new LabelInfo("", 0.0);
            var colon = t.IndexOf(':');
            if (colon > 0 && colon <= 60)
            {
                var head = t.Substring(0, colon).Trim();
                var label = CanonicalizeLabel(head);
                if (label.Length > 0)
                    best = new LabelInfo(label, 0.95);
            }

            var dash = t.IndexOfAny(new[] { '-', '–', '—', ',' });
            if (dash > 0 && dash <= 40)
            {
                var head = t.Substring(0, dash).Trim();
                var label = CanonicalizeLabel(head);
                if (label.Length > 0)
                    best = best.Confidence >= 0.75 ? best : new LabelInfo(label, 0.75);
            }

            var probe = t.Length > 48 ? t.Substring(0, 48) : t;
            var fallback = CanonicalizeLabel(probe);
            if (fallback.Length > 0)
            {
                var conf = (fallback.StartsWith("processo", StringComparison.Ordinal) ||
                            fallback == "cpf" || fallback == "cnpj")
                    ? 0.70
                    : 0.60;
                if (conf > best.Confidence)
                    best = new LabelInfo(fallback, conf);
            }

            var cue = DetectSemanticFieldCue(t);
            if (cue.Confidence > best.Confidence)
                best = cue;

            return best;
        }

        private static string CanonicalizeLabel(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            var s = RemoveDiacritics(input).ToLowerInvariant();
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                    sb.Append(c);
                else
                    sb.Append(' ');
            }

            s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
            if (s.Length == 0)
                return "";
            var compact = s.Replace(" ", "");

            if (s.StartsWith("processo administrativo", StringComparison.Ordinal) ||
                compact.StartsWith("processoadministrativo", StringComparison.Ordinal) ||
                s.StartsWith("sei", StringComparison.Ordinal) ||
                s.StartsWith("adme", StringComparison.Ordinal))
                return "processo_administrativo";
            if (s.StartsWith("processo judicial", StringComparison.Ordinal) ||
                compact.StartsWith("processojudicial", StringComparison.Ordinal) ||
                s.StartsWith("acao", StringComparison.Ordinal) ||
                compact.StartsWith("acaon", StringComparison.Ordinal))
                return "processo_judicial";
            if (s.StartsWith("requerente", StringComparison.Ordinal) || compact.StartsWith("requerente", StringComparison.Ordinal))
                return "requerente";
            if (s.StartsWith("interessad", StringComparison.Ordinal) || compact.StartsWith("interessad", StringComparison.Ordinal))
                return "interessado";
            if (s.StartsWith("promovent", StringComparison.Ordinal) || compact.StartsWith("promovent", StringComparison.Ordinal))
                return "promovente";
            if (s.StartsWith("promovid", StringComparison.Ordinal) || compact.StartsWith("promovid", StringComparison.Ordinal))
                return "promovido";
            if (s.StartsWith("assunto", StringComparison.Ordinal) || compact.StartsWith("assunto", StringComparison.Ordinal))
                return "assunto";
            if (s.StartsWith("processo", StringComparison.Ordinal) || compact.StartsWith("processo", StringComparison.Ordinal))
                return "processo";
            if (s.StartsWith("cpf", StringComparison.Ordinal) || compact.StartsWith("cpf", StringComparison.Ordinal))
                return "cpf";
            if (s.StartsWith("cnpj", StringComparison.Ordinal) || compact.StartsWith("cnpj", StringComparison.Ordinal))
                return "cnpj";
            if (s.StartsWith("perito", StringComparison.Ordinal) || compact.StartsWith("perito", StringComparison.Ordinal) ||
                s.StartsWith("perita", StringComparison.Ordinal) || compact.StartsWith("perita", StringComparison.Ordinal))
                return "perito";
            if (s.StartsWith("vara", StringComparison.Ordinal) || compact.StartsWith("vara", StringComparison.Ordinal))
                return "vara";
            if (s.StartsWith("comarca", StringComparison.Ordinal) || compact.StartsWith("comarca", StringComparison.Ordinal))
                return "comarca";
            if (s.StartsWith("especialidade", StringComparison.Ordinal) || compact.StartsWith("especialidade", StringComparison.Ordinal))
                return "especialidade";
            if (s.StartsWith("pericia", StringComparison.Ordinal) || compact.StartsWith("pericia", StringComparison.Ordinal))
                return "especie_da_pericia";
            if (s.StartsWith("adiantamento", StringComparison.Ordinal) || compact.StartsWith("adiantamento", StringComparison.Ordinal))
                return "adiantamento";
            if (s.StartsWith("parcela", StringComparison.Ordinal) || compact.StartsWith("parcela", StringComparison.Ordinal))
                return "parcela";
            if (s.StartsWith("percentual", StringComparison.Ordinal) || compact.StartsWith("percentual", StringComparison.Ordinal))
                return "percentual";
            if (s.StartsWith("valor", StringComparison.Ordinal) || compact.StartsWith("valor", StringComparison.Ordinal))
                return "valor";
            if (s.StartsWith("data", StringComparison.Ordinal) || compact.StartsWith("data", StringComparison.Ordinal))
                return "data";

            return "";
        }

        private static LabelInfo DetectSemanticFieldCue(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new LabelInfo("", 0.0);

            var s = RemoveDiacritics(text).ToLowerInvariant();
            s = TextUtils.NormalizeWhitespace(s);
            var compact = s.Replace(" ", "");
            var hasMoney = Regex.IsMatch(s, @"\br\$\s*\d");
            var hasDate = Regex.IsMatch(s, @"\b\d{1,2}[./-]\d{1,2}[./-]\d{2,4}\b") ||
                          Regex.IsMatch(s, @"\b\d{1,2}\s+de\s+[a-zçãõáéíóúâêô]+\s+de\s+\d{4}\b");
            var hasCnj = Regex.IsMatch(compact, @"\d{7}-?\d{2}\.?\d{4}\.?\d\.?\d{2}\.?\d{4}");
            var hasPa = Regex.IsMatch(compact, @"\d{6,7}-?\d{2}\.?\d{4}\.?\d\.?\d{2}(?:\.?\d{4})?");

            if (s.Contains("processo administrativo") || Regex.IsMatch(s, @"\bsei\b"))
                return new LabelInfo("processo_administrativo", 0.93);
            if ((s.Contains("processo judicial") || s.Contains("acao n")) && hasCnj)
                return new LabelInfo("processo_judicial", 0.93);
            if (s.StartsWith("processo") && hasCnj)
                return new LabelInfo("processo_judicial", 0.90);
            if (s.StartsWith("processo") && hasPa)
                return new LabelInfo("processo_administrativo", 0.88);

            if (Regex.IsMatch(s, @"\brequerente\b"))
                return new LabelInfo("requerente", 0.92);
            if (Regex.IsMatch(s, @"\binteressad[oa]\b"))
                return new LabelInfo("interessado", 0.92);
            if (Regex.IsMatch(s, @"\bpromovent[eo]\b"))
                return new LabelInfo("promovente", 0.90);
            if (Regex.IsMatch(s, @"\bpromovid[oa]\b"))
                return new LabelInfo("promovido", 0.90);
            if (Regex.IsMatch(s, @"\bassunto\b"))
                return new LabelInfo("assunto", 0.88);

            if (Regex.IsMatch(s, @"\bcpf\b") && Regex.IsMatch(s, @"\bperit[oa]\b"))
                return new LabelInfo("cpf_perito", 0.90);
            if (Regex.IsMatch(s, @"\bcpf\b"))
                return new LabelInfo("cpf", 0.80);
            if (Regex.IsMatch(s, @"\bcnpj\b"))
                return new LabelInfo("cnpj", 0.88);

            if (Regex.IsMatch(s, @"\bperit[oa]\b"))
                return new LabelInfo("perito", 0.86);
            if (Regex.IsMatch(s, @"\bespecialidade\b"))
                return new LabelInfo("especialidade", 0.86);
            if (Regex.IsMatch(s, @"\bpericia\b"))
                return new LabelInfo("especie_da_pericia", 0.74);

            if (Regex.IsMatch(s, @"\bcomarca\b"))
                return new LabelInfo("comarca", 0.86);
            if (Regex.IsMatch(s, @"\bvara\b|\bjuizo\b|\bjuizado\b"))
                return new LabelInfo("vara", 0.84);

            if (Regex.IsMatch(s, @"\badiantamento\b"))
                return new LabelInfo("adiantamento", 0.86);
            if (Regex.IsMatch(s, @"\bparcela\b"))
                return new LabelInfo("parcela", 0.86);
            if (Regex.IsMatch(s, @"\bpercentual\b|%"))
                return new LabelInfo("percentual", 0.82);

            if (hasMoney)
            {
                if (s.Contains("conselho da magistratura") || compact.Contains("valorarbitradocm"))
                    return new LabelInfo("valor_arbitrado_cm", 0.84);
                if (s.Contains("honorario") || s.Contains("arbitrad"))
                    return new LabelInfo("valor_arbitrado", 0.80);
                return new LabelInfo("valor", 0.70);
            }

            if (hasDate)
                return new LabelInfo("data", 0.72);

            return new LabelInfo("", 0.0);
        }

        private static bool IsCriticalFieldLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return false;

            return label.Equals("processo_administrativo", StringComparison.Ordinal) ||
                   label.Equals("processo_judicial", StringComparison.Ordinal) ||
                   label.Equals("requerente", StringComparison.Ordinal) ||
                   label.Equals("interessado", StringComparison.Ordinal) ||
                   label.Equals("promovente", StringComparison.Ordinal) ||
                   label.Equals("promovido", StringComparison.Ordinal) ||
                   label.Equals("assunto", StringComparison.Ordinal) ||
                   label.Equals("perito", StringComparison.Ordinal) ||
                   label.Equals("cpf_perito", StringComparison.Ordinal) ||
                   label.Equals("cpf", StringComparison.Ordinal) ||
                   label.Equals("cnpj", StringComparison.Ordinal) ||
                   label.Equals("comarca", StringComparison.Ordinal) ||
                   label.Equals("vara", StringComparison.Ordinal);
        }

        private static string RemoveDiacritics(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "";

            var normalized = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static void AlignBlocks(string aPath, string bPath, int objId, HashSet<string> opFilter, bool useLargestContents, int contentsPage)
        {
            using var docA = new PdfDocument(new PdfReader(aPath));
            using var docB = new PdfDocument(new PdfReader(bPath));

            int pageA = contentsPage;
            int pageB = contentsPage;
            if (useLargestContents)
            {
                if (pageA <= 0 || pageB <= 0)
                {
                    Console.WriteLine("Informe --page N ao usar --contents.");
                    return;
                }
            }
            var foundA = useLargestContents
                ? FindLargestStreamOnPage(docA, pageA)
                : FindStreamAndResourcesByObjId(docA, objId);
            var foundB = useLargestContents
                ? FindLargestStreamOnPage(docB, pageB)
                : FindStreamAndResourcesByObjId(docB, objId);
            if (foundA.Stream == null || foundA.Resources == null)
            {
                Console.WriteLine(useLargestContents
                    ? $"Contents nao encontrado na pagina {pageA}: {aPath}"
                    : $"Objeto {objId} nao encontrado em: {aPath}");
                return;
            }
            if (foundB.Stream == null || foundB.Resources == null)
            {
                Console.WriteLine(useLargestContents
                    ? $"Contents nao encontrado na pagina {pageB}: {bPath}"
                    : $"Objeto {objId} nao encontrado em: {bPath}");
                return;
            }

            var blocksA = ExtractSelfBlocks(foundA.Stream, foundA.Resources, opFilter);
            var blocksB = ExtractSelfBlocks(foundB.Stream, foundB.Resources, opFilter);
            if (blocksA.Count == 0 || blocksB.Count == 0)
            {
                Console.WriteLine("Nenhum bloco encontrado para alinhar.");
                return;
            }

            var normA = blocksA.Select(b => NormalizeForSimilarity(b.Text ?? "")).ToList();
            var normB = blocksB.Select(b => NormalizeForSimilarity(b.Text ?? "")).ToList();

            int n = blocksA.Count;
            int m = blocksB.Count;
            const double gap = -0.35;
            const double minScore = 0.30;

            var dp = new double[n + 1, m + 1];
            var move = new byte[n + 1, m + 1];

            for (int i = 1; i <= n; i++)
            {
                dp[i, 0] = dp[i - 1, 0] + gap;
                move[i, 0] = 1;
            }
            for (int j = 1; j <= m; j++)
            {
                dp[0, j] = dp[0, j - 1] + gap;
                move[0, j] = 2;
            }

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    var sim = ComputeAlignmentSimilarity(normA[i - 1], normB[j - 1]);
                    var scoreDiag = dp[i - 1, j - 1] + sim;
                    var scoreUp = dp[i - 1, j] + gap;
                    var scoreLeft = dp[i, j - 1] + gap;

                    if (scoreDiag >= scoreUp && scoreDiag >= scoreLeft)
                    {
                        dp[i, j] = scoreDiag;
                        move[i, j] = 0;
                    }
                    else if (scoreUp >= scoreLeft)
                    {
                        dp[i, j] = scoreUp;
                        move[i, j] = 1;
                    }
                    else
                    {
                        dp[i, j] = scoreLeft;
                        move[i, j] = 2;
                    }
                }
            }

            var alignments = new List<(int ai, int bi, double score)>();
            int x = n;
            int y = m;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0 && move[x, y] == 0)
                {
                    var sim = ComputeAlignmentSimilarity(normA[x - 1], normB[y - 1]);
                    alignments.Add((x - 1, y - 1, sim));
                    x--;
                    y--;
                }
                else if (x > 0 && (y == 0 || move[x, y] == 1))
                {
                    alignments.Add((x - 1, -1, 0));
                    x--;
                }
                else
                {
                    alignments.Add((-1, y - 1, 0));
                    y--;
                }
            }
            alignments.Reverse();

            var nameA = Path.GetFileName(aPath);
            var nameB = Path.GetFileName(bPath);
            Console.WriteLine($"OBJ - ALIGN (blocos por ordem/tamanho/texto)");
            Console.WriteLine($"{nameA} <-> {nameB}");
            Console.WriteLine();

            foreach (var (ai, bi, score) in alignments)
            {
                if (ai >= 0 && bi >= 0 && score < minScore)
                    continue;

                if (ai >= 0 && bi >= 0)
                {
                    var a = blocksA[ai];
                    var b = blocksB[bi];
                    var textA = CollapseSpaces(NormalizeBlockToken(a.Text ?? ""));
                    var textB = CollapseSpaces(NormalizeBlockToken(b.Text ?? ""));
                    var aRange = a.StartOp == a.EndOp ? $"{a.StartOp}" : $"{a.StartOp}-{a.EndOp}";
                    var bRange = b.StartOp == b.EndOp ? $"{b.StartOp}" : $"{b.StartOp}-{b.EndOp}";
                    Console.WriteLine($"score={score:F2}  {nameA} op{aRange}  \"{EscapeBlockText(textA)}\"");
                    Console.WriteLine($"           {nameB} op{bRange}  \"{EscapeBlockText(textB)}\"");
                    PrintFixedVarSegments(textA, textB);
                    Console.WriteLine();
                }
                else if (ai >= 0)
                {
                    var a = blocksA[ai];
                    var textA = CollapseSpaces(NormalizeBlockToken(a.Text ?? ""));
                    var aRange = a.StartOp == a.EndOp ? $"{a.StartOp}" : $"{a.StartOp}-{a.EndOp}";
                    Console.WriteLine($"score=--   {nameA} op{aRange}  \"{EscapeBlockText(textA)}\"");
                    Console.WriteLine($"           {nameB} (sem equivalente)");
                    Console.WriteLine();
                }
                else if (bi >= 0)
                {
                    var b = blocksB[bi];
                    var textB = CollapseSpaces(NormalizeBlockToken(b.Text ?? ""));
                    var bRange = b.StartOp == b.EndOp ? $"{b.StartOp}" : $"{b.StartOp}-{b.EndOp}";
                    Console.WriteLine($"score=--   {nameA} (sem equivalente)");
                    Console.WriteLine($"           {nameB} op{bRange}  \"{EscapeBlockText(textB)}\"");
                    Console.WriteLine();
                }
            }
        }

        internal sealed class AlignRangeValue
        {
            public int Page { get; set; }
            public int StartOp { get; set; }
            public int EndOp { get; set; }
            public string ValueFull { get; set; } = "";
        }

        internal sealed class AlignRangeResult
        {
            public AlignRangeResult(AlignRangeValue frontA, AlignRangeValue frontB, AlignRangeValue backA, AlignRangeValue backB)
            {
                FrontA = frontA;
                FrontB = frontB;
                BackA = backA;
                BackB = backB;
            }

            public AlignRangeValue FrontA { get; }
            public AlignRangeValue FrontB { get; }
            public AlignRangeValue BackA { get; }
            public AlignRangeValue BackB { get; }
        }

        internal sealed class PageObjSelection
        {
            public int Page { get; set; }
            public int Obj { get; set; }
        }

        internal static AlignRangeResult? ComputeAlignRangesForSelections(
            string aPath,
            string bPath,
            HashSet<string> opFilter,
            PageObjSelection frontA,
            PageObjSelection backA,
            PageObjSelection frontB,
            PageObjSelection backB,
            int backoff)
        {
            if (string.IsNullOrWhiteSpace(aPath) || string.IsNullOrWhiteSpace(bPath))
                return null;

            using var docA = new PdfDocument(new PdfReader(aPath));
            using var docB = new PdfDocument(new PdfReader(bPath));

            var nameA = Path.GetFileName(aPath);
            var nameB = Path.GetFileName(bPath);

            var front = BuildAlignRangeForObj(docA, docB, frontA, frontB, opFilter, backoff, nameA, nameB, "front_head");
            var back = BuildAlignRangeForObj(docA, docB, backA, backB, opFilter, backoff, nameA, nameB, "back_tail");

            return new AlignRangeResult(front.A, front.B, back.A, back.B);
        }

        internal static AlignRangeValue? ComputeFullRangeForSelection(string pdfPath, PageObjSelection sel, HashSet<string> opFilter)
        {
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
                return null;

            using var doc = new PdfDocument(new PdfReader(pdfPath));
            return BuildFullRangeForObj(doc, sel, opFilter);
        }

        internal static AlignRangeResult? ComputeFrontAlignRangeForSelections(
            string aPath,
            string bPath,
            HashSet<string> opFilter,
            PageObjSelection frontA,
            PageObjSelection frontB,
            int backoff,
            int backPageA,
            int backPageB)
        {
            if (string.IsNullOrWhiteSpace(aPath) || string.IsNullOrWhiteSpace(bPath))
                return null;

            using var docA = new PdfDocument(new PdfReader(aPath));
            using var docB = new PdfDocument(new PdfReader(bPath));

            var nameA = Path.GetFileName(aPath);
            var nameB = Path.GetFileName(bPath);

            var front = BuildAlignRangeForObj(docA, docB, frontA, frontB, opFilter, backoff, nameA, nameB, "front_head");
            var backA = new AlignRangeValue { Page = backPageA, StartOp = 0, EndOp = 0, ValueFull = "" };
            var backB = new AlignRangeValue { Page = backPageB, StartOp = 0, EndOp = 0, ValueFull = "" };

            return new AlignRangeResult(front.A, front.B, backA, backB);
        }

        private sealed class BlockAlignment
        {
            public BlockAlignment(int aIndex, int bIndex, double score)
            {
                AIndex = aIndex;
                BIndex = bIndex;
                Score = score;
            }

            public int AIndex { get; }
            public int BIndex { get; }
            public double Score { get; }
        }

        private struct VariableRange
        {
            public bool HasValue;
            public int FirstStartOp;
            public int LastEndOp;
        }

        private static (AlignRangeValue A, AlignRangeValue B) BuildAlignRangeForObj(
            PdfDocument docA,
            PdfDocument docB,
            PageObjSelection selA,
            PageObjSelection selB,
            HashSet<string> opFilter,
            int backoff,
            string nameA,
            string nameB,
            string label)
        {
            var resultA = new AlignRangeValue { Page = selA.Page };
            var resultB = new AlignRangeValue { Page = selB.Page };

            if (selA.Page < 1 || selA.Page > docA.GetNumberOfPages())
            {
                Console.WriteLine($"Pagina invalida ({label}) em {nameA}: {selA.Page}");
                return (resultA, resultB);
            }
            if (selB.Page < 1 || selB.Page > docB.GetNumberOfPages())
            {
                Console.WriteLine($"Pagina invalida ({label}) em {nameB}: {selB.Page}");
                return (resultA, resultB);
            }
            if (selA.Obj <= 0)
            {
                Console.WriteLine($"Obj invalido ({label}) em {nameA}: {selA.Obj}");
                return (resultA, resultB);
            }
            if (selB.Obj <= 0)
            {
                Console.WriteLine($"Obj invalido ({label}) em {nameB}: {selB.Obj}");
                return (resultA, resultB);
            }

            var foundA = FindStreamAndResourcesByObjId(docA, selA.Obj);
            var foundB = FindStreamAndResourcesByObjId(docB, selB.Obj);

            if (foundA.Stream == null || foundA.Resources == null)
            {
                Console.WriteLine($"Obj {selA.Obj} nao encontrado na pagina {selA.Page} ({label}) em {nameA}");
                return (resultA, resultB);
            }
            if (foundB.Stream == null || foundB.Resources == null)
            {
                Console.WriteLine($"Obj {selB.Obj} nao encontrado na pagina {selB.Page} ({label}) em {nameB}");
                return (resultA, resultB);
            }

            var blocksA = ExtractSelfBlocks(foundA.Stream, foundA.Resources, opFilter);
            var blocksB = ExtractSelfBlocks(foundB.Stream, foundB.Resources, opFilter);
            if (blocksA.Count == 0 || blocksB.Count == 0)
            {
                Console.WriteLine($"Nenhum bloco encontrado para alinhar ({label}).");
                return (resultA, resultB);
            }

            var alignments = BuildBlockAlignments(blocksA, blocksB, out var normA, out var normB, out _);

            var rangeA = new VariableRange();
            var rangeB = new VariableRange();
            ApplyAlignmentToRanges(alignments, blocksA, blocksB, normA, normB, ref rangeA, ref rangeB);

            if (label == "back_tail")
            {
                if (!rangeA.HasValue)
                    rangeA = FallbackRange(blocksA);
                else
                    rangeA.LastEndOp = blocksA[^1].EndOp;

                if (!rangeB.HasValue)
                    rangeB = FallbackRange(blocksB);
                else
                    rangeB.LastEndOp = blocksB[^1].EndOp;
            }

            if (!rangeA.HasValue)
                rangeA = FallbackRange(blocksA);
            if (!rangeB.HasValue)
                rangeB = FallbackRange(blocksB);

            if (label == "back_tail")
            {
                var totalA = PdfTextExtraction.CollectTextOperatorTexts(foundA.Stream, foundA.Resources).Count;
                if (totalA > 0 && rangeA.LastEndOp < totalA)
                    rangeA.LastEndOp = totalA;

                var totalB = PdfTextExtraction.CollectTextOperatorTexts(foundB.Stream, foundB.Resources).Count;
                if (totalB > 0 && rangeB.LastEndOp < totalB)
                    rangeB.LastEndOp = totalB;
            }

            var (startA, endA) = ApplyBackoff(rangeA, backoff);
            var (startB, endB) = ApplyBackoff(rangeB, backoff);

            resultA.StartOp = startA;
            resultA.EndOp = endA;
            resultA.ValueFull = ExtractValueFull(foundA.Stream, foundA.Resources, opFilter, startA, endA);

            resultB.StartOp = startB;
            resultB.EndOp = endB;
            resultB.ValueFull = ExtractValueFull(foundB.Stream, foundB.Resources, opFilter, startB, endB);

            return (resultA, resultB);
        }

        private static AlignRangeValue? BuildFullRangeForObj(PdfDocument doc, PageObjSelection sel, HashSet<string> opFilter)
        {
            if (doc == null || sel.Obj <= 0 || sel.Page < 1 || sel.Page > doc.GetNumberOfPages())
                return null;

            var found = FindStreamAndResourcesByObjId(doc, sel.Obj);
            if (found.Stream == null || found.Resources == null)
                return null;

            var blocks = ExtractSelfBlocks(found.Stream, found.Resources, opFilter);
            if (blocks.Count == 0)
                return null;

            var start = blocks[0].StartOp;
            var end = blocks[^1].EndOp;
            var value = ExtractValueFull(found.Stream, found.Resources, opFilter, start, end);
            return new AlignRangeValue
            {
                Page = sel.Page,
                StartOp = start,
                EndOp = end,
                ValueFull = value
            };
        }

        private static List<BlockAlignment> BuildBlockAlignments(
            List<SelfBlock> blocksA,
            List<SelfBlock> blocksB,
            out List<string> normA,
            out List<string> normB,
            out List<AnchorPair> anchors,
            double minSim = 0.0,
            int band = 0,
            double minLenRatio = 0.05,
            double lenPenalty = 0.0,
            double anchorMinSim = 0.0,
            double anchorMinLenRatio = 0.0,
            double gapPenalty = -0.35)
        {
            normA = blocksA.Select(b => NormalizeForSimilarity(b.Text ?? "")).ToList();
            normB = blocksB.Select(b => NormalizeForSimilarity(b.Text ?? "")).ToList();
            anchors = new List<AnchorPair>();

            double autoMaxSim = 0.0;
            if (anchorMinSim > 0 || anchorMinLenRatio > 0)
            {
                anchors = BuildAnchorPairsExplicit(normA, normB, anchorMinSim, anchorMinLenRatio);
            }
            else
            {
                // Auto anchors (mutual best) to avoid random alignments.
                var anchorCueRateA = normA.Count == 0
                    ? 0.0
                    : normA.Count(IsAnchorModelCue) / (double)normA.Count;
                var anchorMode = anchorCueRateA >= 0.25;
                var minLen = minLenRatio > 0
                    ? (anchorMode ? Math.Max(0.02, minLenRatio * 0.35) : Math.Max(0.3, minLenRatio))
                    : (anchorMode ? 0.08 : 0.3);
                anchors = BuildAnchorPairsAuto(normA, normB, minLen, out autoMaxSim);
            }

            if (anchors.Count == 0)
            {
                var effectiveMinSim = minSim;
                if (effectiveMinSim <= 0)
                    effectiveMinSim = autoMaxSim > 0 ? Math.Max(0.12, autoMaxSim * 0.6) : 0.12;
                return BuildSegmentAlignments(normA, normB, 0, blocksA.Count, 0, blocksB.Count, effectiveMinSim, band, minLenRatio, lenPenalty, gapPenalty);
            }

            var result = new List<BlockAlignment>();
            int prevA = 0;
            int prevB = 0;
            foreach (var a in anchors)
            {
                if (a.AIndex > prevA || a.BIndex > prevB)
                {
                    result.AddRange(BuildSegmentAlignments(normA, normB, prevA, a.AIndex, prevB, a.BIndex, minSim, band, minLenRatio, lenPenalty, gapPenalty));
                }
                result.Add(new BlockAlignment(a.AIndex, a.BIndex, a.Score));
                prevA = a.AIndex + 1;
                prevB = a.BIndex + 1;
            }
            if (prevA < blocksA.Count || prevB < blocksB.Count)
            {
                result.AddRange(BuildSegmentAlignments(normA, normB, prevA, blocksA.Count, prevB, blocksB.Count, minSim, band, minLenRatio, lenPenalty, gapPenalty));
            }
            return result;
        }

        private sealed class AnchorPair
        {
            public int AIndex { get; set; }
            public int BIndex { get; set; }
            public double Score { get; set; }
        }

        private static List<AnchorPair> BuildAnchorPairsExplicit(List<string> normA, List<string> normB, double minSim, double minLenRatio)
        {
            var anchors = new List<AnchorPair>();
            if (minSim <= 0 && minLenRatio <= 0)
                return anchors;

            var candidates = new List<AnchorPair>();
            var bestSimA = new double[normA.Count];
            var bestIdxA = new int[normA.Count];
            var bestSimB = new double[normB.Count];
            var bestIdxB = new int[normB.Count];
            for (int i = 0; i < bestIdxA.Length; i++) bestIdxA[i] = -1;
            for (int i = 0; i < bestIdxB.Length; i++) bestIdxB[i] = -1;

            for (int i = 0; i < normA.Count; i++)
            {
                for (int j = 0; j < normB.Count; j++)
                {
                    if (ShouldRejectAsAnchorMismatch(normA[i], normB[j]))
                        continue;
                    var sim = ComputeAlignmentSimilarity(normA[i], normB[j]);
                    if (sim > bestSimA[i])
                    {
                        bestSimA[i] = sim;
                        bestIdxA[i] = j;
                    }
                    if (sim > bestSimB[j])
                    {
                        bestSimB[j] = sim;
                        bestIdxB[j] = i;
                    }
                    if (minSim > 0 && sim < minSim)
                        continue;
                    var lenRatio = ComputeLenRatio(normA[i], normB[j]);
                    var anchorCue = IsAnchorModelCue(normA[i]) || IsAnchorModelCue(normB[j]);
                    if (minLenRatio > 0 && lenRatio < minLenRatio && !anchorCue)
                        continue;
                    candidates.Add(new AnchorPair { AIndex = i, BIndex = j, Score = sim });
                }
            }
            if (candidates.Count == 0)
                return anchors;

            // Keep only mutual best pairs to avoid noisy anchors.
            candidates = candidates
                .Where(c => bestIdxA[c.AIndex] == c.BIndex && bestIdxB[c.BIndex] == c.AIndex)
                .OrderBy(c => c.AIndex)
                .ThenBy(c => c.BIndex)
                .ToList();
            if (candidates.Count == 0)
                return anchors;

            var dp = new double[candidates.Count];
            var prev = new int[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                dp[i] = candidates[i].Score;
                prev[i] = -1;
                for (int j = 0; j < i; j++)
                {
                    if (candidates[j].AIndex < candidates[i].AIndex &&
                        candidates[j].BIndex < candidates[i].BIndex)
                    {
                        var cand = dp[j] + candidates[i].Score;
                        if (cand > dp[i])
                        {
                            dp[i] = cand;
                            prev[i] = j;
                        }
                    }
                }
            }

            int best = 0;
            for (int i = 1; i < dp.Length; i++)
            {
                if (dp[i] > dp[best])
                    best = i;
            }

            var stack = new Stack<AnchorPair>();
            int cur = best;
            while (cur >= 0)
            {
                stack.Push(candidates[cur]);
                cur = prev[cur];
            }
            anchors.AddRange(stack);
            return anchors;
        }

        private static List<AnchorPair> BuildAnchorPairsAuto(List<string> normA, List<string> normB, double minLenRatio, out double maxSim)
        {
            var anchors = new List<AnchorPair>();
            maxSim = 0.0;
            if (normA.Count == 0 || normB.Count == 0)
                return anchors;

            var bestSimA = new double[normA.Count];
            var bestIdxA = new int[normA.Count];
            var bestSimB = new double[normB.Count];
            var bestIdxB = new int[normB.Count];
            for (int i = 0; i < bestIdxA.Length; i++) bestIdxA[i] = -1;
            for (int i = 0; i < bestIdxB.Length; i++) bestIdxB[i] = -1;

            maxSim = 0.0;
            for (int i = 0; i < normA.Count; i++)
            {
                for (int j = 0; j < normB.Count; j++)
                {
                    if (ShouldRejectAsAnchorMismatch(normA[i], normB[j]))
                        continue;
                    var sim = ComputeAlignmentSimilarity(normA[i], normB[j]);
                    if (sim > maxSim) maxSim = sim;
                    if (sim > bestSimA[i])
                    {
                        bestSimA[i] = sim;
                        bestIdxA[i] = j;
                    }
                    if (sim > bestSimB[j])
                    {
                        bestSimB[j] = sim;
                        bestIdxB[j] = i;
                    }
                }
            }

            var anchorCueRateA = normA.Count == 0
                ? 0.0
                : normA.Count(IsAnchorModelCue) / (double)normA.Count;
            var anchorMode = anchorCueRateA >= 0.25;
            var threshold = anchorMode
                ? Math.Max(0.10, maxSim - 0.30)
                : Math.Max(0.35, maxSim - 0.15);
            var candidates = new List<AnchorPair>();
            for (int i = 0; i < normA.Count; i++)
            {
                var j = bestIdxA[i];
                if (j < 0 || j >= normB.Count)
                    continue;
                if (bestIdxB[j] != i)
                    continue;
                var sim = bestSimA[i];
                var anchorCue = IsAnchorModelCue(normA[i]) || IsAnchorModelCue(normB[j]);
                var localThreshold = anchorCue ? Math.Min(threshold, 0.12) : threshold;
                if (sim < localThreshold)
                    continue;
                var lenRatio = ComputeLenRatio(normA[i], normB[j]);
                if (lenRatio < minLenRatio && !anchorCue)
                    continue;
                candidates.Add(new AnchorPair { AIndex = i, BIndex = j, Score = sim });
            }

            if (candidates.Count == 0)
                return anchors;

            candidates = candidates
                .OrderBy(c => c.AIndex)
                .ThenBy(c => c.BIndex)
                .ToList();

            var dp = new double[candidates.Count];
            var prev = new int[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                dp[i] = candidates[i].Score;
                prev[i] = -1;
                for (int j = 0; j < i; j++)
                {
                    if (candidates[j].AIndex < candidates[i].AIndex &&
                        candidates[j].BIndex < candidates[i].BIndex)
                    {
                        var cand = dp[j] + candidates[i].Score;
                        if (cand > dp[i])
                        {
                            dp[i] = cand;
                            prev[i] = j;
                        }
                    }
                }
            }

            int best = 0;
            for (int i = 1; i < dp.Length; i++)
            {
                if (dp[i] > dp[best])
                    best = i;
            }

            var stack = new Stack<AnchorPair>();
            int cur = best;
            while (cur >= 0)
            {
                stack.Push(candidates[cur]);
                cur = prev[cur];
            }
            anchors.AddRange(stack);
            return anchors;
        }

        private static List<BlockAlignment> BuildSegmentAlignments(
            List<string> normA,
            List<string> normB,
            int startA,
            int endA,
            int startB,
            int endB,
            double minSim,
            int band,
            double minLenRatio,
            double lenPenalty,
            double gapPenalty)
        {
            int n = Math.Max(0, endA - startA);
            int m = Math.Max(0, endB - startB);
            if (n == 0 && m == 0)
                return new List<BlockAlignment>();

            const double negInf = -1e9;

            var dp = new double[n + 1, m + 1];
            var move = new byte[n + 1, m + 1];
            for (int i = 0; i <= n; i++)
            {
                for (int j = 0; j <= m; j++)
                    dp[i, j] = negInf;
            }
            dp[0, 0] = 0;
            for (int i = 1; i <= n; i++)
            {
                dp[i, 0] = dp[i - 1, 0] + gapPenalty;
                move[i, 0] = 1;
            }
            for (int j = 1; j <= m; j++)
            {
                dp[0, j] = dp[0, j - 1] + gapPenalty;
                move[0, j] = 2;
            }

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    var withinBand = band <= 0 || Math.Abs(i - j) <= band;
                    var aIdx = startA + (i - 1);
                    var bIdx = startB + (j - 1);
                    var anchorCueA = IsAnchorModelCue(normA[aIdx]);
                    var sim = ComputeAlignmentSimilarity(normA[aIdx], normB[bIdx]);
                    var lenRatio = ComputeLenRatio(normA[aIdx], normB[bIdx]);
                    var scoreDiag = negInf;
                    var effectiveMinSim = anchorCueA ? Math.Min(minSim, 0.08) : minSim;
                    var lenOk = (minLenRatio <= 0 || lenRatio >= minLenRatio || anchorCueA);
                    if (withinBand &&
                        sim >= effectiveMinSim &&
                        lenOk)
                    {
                        var penalty = (lenPenalty > 0 && !anchorCueA) ? (1.0 - lenRatio) * lenPenalty : 0.0;
                        scoreDiag = dp[i - 1, j - 1] + sim - penalty;
                    }
                    var scoreUp = dp[i - 1, j] + gapPenalty;
                    var scoreLeft = dp[i, j - 1] + gapPenalty;

                    if (scoreDiag >= scoreUp && scoreDiag >= scoreLeft)
                    {
                        dp[i, j] = scoreDiag;
                        move[i, j] = 0;
                    }
                    else if (scoreUp >= scoreLeft)
                    {
                        dp[i, j] = scoreUp;
                        move[i, j] = 1;
                    }
                    else
                    {
                        dp[i, j] = scoreLeft;
                        move[i, j] = 2;
                    }
                }
            }

            var alignments = new List<BlockAlignment>();
            int x = n;
            int y = m;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0 && move[x, y] == 0)
                {
                    var aIdx = startA + (x - 1);
                    var bIdx = startB + (y - 1);
                    var sim = ComputeAlignmentSimilarity(normA[aIdx], normB[bIdx]);
                    alignments.Add(new BlockAlignment(aIdx, bIdx, sim));
                    x--;
                    y--;
                }
                else if (x > 0 && (y == 0 || move[x, y] == 1))
                {
                    alignments.Add(new BlockAlignment(startA + (x - 1), -1, 0));
                    x--;
                }
                else
                {
                    alignments.Add(new BlockAlignment(-1, startB + (y - 1), 0));
                    y--;
                }
            }
            alignments.Reverse();
            return alignments;
        }

        private static List<BlockAlignment> BuildAllGaps(int countA, int countB)
        {
            var list = new List<BlockAlignment>();
            for (int i = 0; i < countA; i++)
                list.Add(new BlockAlignment(i, -1, 0));
            for (int j = 0; j < countB; j++)
                list.Add(new BlockAlignment(-1, j, 0));
            return list;
        }

        private static double ComputeLenRatio(string a, string b)
        {
            var la = a?.Length ?? 0;
            var lb = b?.Length ?? 0;
            if (la <= 0 || lb <= 0) return 0;
            var min = Math.Min(la, lb);
            var max = Math.Max(la, lb);
            if (max == 0) return 0;
            return min / (double)max;
        }

        private static void ApplyAlignmentToRanges(
            List<BlockAlignment> alignments,
            List<SelfBlock> blocksA,
            List<SelfBlock> blocksB,
            List<string> normA,
            List<string> normB,
            ref VariableRange rangeA,
            ref VariableRange rangeB)
        {
            foreach (var align in alignments)
            {
                if (align.AIndex >= 0 && align.BIndex >= 0)
                {
                    bool diff = !string.Equals(normA[align.AIndex], normB[align.BIndex], StringComparison.Ordinal);
                    if (diff)
                    {
                        AddBlockToRange(ref rangeA, blocksA[align.AIndex]);
                        AddBlockToRange(ref rangeB, blocksB[align.BIndex]);
                    }
                }
                else if (align.AIndex >= 0)
                {
                    AddBlockToRange(ref rangeA, blocksA[align.AIndex]);
                }
                else if (align.BIndex >= 0)
                {
                    AddBlockToRange(ref rangeB, blocksB[align.BIndex]);
                }
            }
        }

        private static void AddBlockToRange(ref VariableRange range, SelfBlock block)
        {
            if (!range.HasValue)
            {
                range.HasValue = true;
                range.FirstStartOp = block.StartOp;
                range.LastEndOp = block.EndOp;
                return;
            }
            range.LastEndOp = block.EndOp;
        }

        private static VariableRange FallbackRange(List<SelfBlock> blocks)
        {
            if (blocks.Count == 0)
                return new VariableRange();
            return new VariableRange
            {
                HasValue = true,
                FirstStartOp = blocks[0].StartOp,
                LastEndOp = blocks[^1].EndOp
            };
        }

        private static (int Start, int End) ApplyBackoff(VariableRange range, int backoff)
        {
            if (!range.HasValue)
                return (0, 0);
            int start = range.FirstStartOp - Math.Max(0, backoff);
            if (start < 1) start = 1;
            int end = range.LastEndOp;
            if (end < start) end = start;
            return (start, end);
        }

        private static string ExtractValueFull(PdfStream stream, PdfResources resources, HashSet<string> opFilter, int startOp, int endOp)
        {
            if (stream == null || resources == null || startOp <= 0 || endOp <= 0)
                return "";

            var full = ExtractFullTextWithOps(stream, resources, opFilter,
                includeLineBreaks: true,
                includeTdLineBreaks: true,
                includeTmLineBreaks: true,
                lineBreakAsSpace: true);

            var sliced = SliceFullTextByOpRange(full, startOp, endOp);
            var text = sliced.Text ?? "";
            text = NormalizeBlockToken(text);
            return CollapseSpaces(text);
        }

        private static void PrintFixedVarSegments(string textA, string textB)
        {
            if (string.IsNullOrWhiteSpace(textA) && string.IsNullOrWhiteSpace(textB))
                return;

            var dmp = new diff_match_patch();
            var diffs = dmp.diff_main(textA ?? "", textB ?? "", false);
            dmp.diff_cleanupSemantic(diffs);

            var varA = new StringBuilder();
            var varB = new StringBuilder();
            const int minFixedLen = 2;

            void FlushVar()
            {
                if (varA.Length == 0 && varB.Length == 0)
                    return;

                var a = varA.ToString().Trim();
                var b = varB.ToString().Trim();
                if (a.Length == 0 && b.Length == 0)
                {
                    varA.Clear();
                    varB.Clear();
                    return;
                }

                Console.WriteLine($"  VAR A: \"{EscapeBlockText(a)}\"");
                Console.WriteLine($"  VAR B: \"{EscapeBlockText(b)}\"");
                varA.Clear();
                varB.Clear();
            }

            foreach (var diff in diffs)
            {
                if (diff.operation == Operation.EQUAL)
                {
                    FlushVar();
                    var fixedText = diff.text.Trim();
                    if (fixedText.Length >= minFixedLen)
                        Console.WriteLine($"  FIXO: \"{EscapeBlockText(fixedText)}\"");
                    continue;
                }
                if (diff.operation == Operation.DELETE)
                {
                    varA.Append(diff.text);
                    continue;
                }
                if (diff.operation == Operation.INSERT)
                {
                    varB.Append(diff.text);
                }
            }

            FlushVar();
        }

        private static int GetBlockMaxTokenLen(VarBlockSlots block, List<string> baseTokens, List<List<string>> tokenLists, List<TokenAlignment> alignments)
        {
            int maxLen = 0;
            int pdfCount = tokenLists.Count;
            for (int i = 0; i < pdfCount; i++)
            {
                maxLen = Math.Max(maxLen, GetBlockMaxTokenLenForPdf(block, i, baseTokens, tokenLists, alignments));
            }
            return maxLen;
        }

        private static int GetBlockMaxTokenLenForPdf(VarBlockSlots block, int pdfIndex, List<string> baseTokens, List<List<string>> tokenLists, List<TokenAlignment> alignments)
        {
            int maxLen = 0;
            var maxSlot = block.EndSlot;
            for (int slot = block.StartSlot; slot <= maxSlot; slot++)
            {
                if (slot % 2 == 0)
                {
                    int gap = slot / 2;
                    if (pdfIndex == 0) continue;
                    var alignment = alignments[pdfIndex - 1];
                    var otherTokens = tokenLists[pdfIndex];
                    foreach (var tokenIdx in alignment.Insertions[gap])
                    {
                        if (tokenIdx >= 0 && tokenIdx < otherTokens.Count)
                            maxLen = Math.Max(maxLen, otherTokens[tokenIdx].Length);
                    }
                }
                else
                {
                    int tokenIdx = (slot - 1) / 2;
                    if (tokenIdx < 0 || tokenIdx >= baseTokens.Count)
                        continue;
                    if (pdfIndex == 0)
                    {
                        maxLen = Math.Max(maxLen, baseTokens[tokenIdx].Length);
                    }
                    else
                    {
                        var alignment = alignments[pdfIndex - 1];
                        var otherTokens = tokenLists[pdfIndex];
                        var otherIdx = alignment.BaseToOther[tokenIdx];
                        if (otherIdx >= 0 && otherIdx < otherTokens.Count)
                            maxLen = Math.Max(maxLen, otherTokens[otherIdx].Length);
                    }
                }
            }
            return maxLen;
        }

        private static string BuildBlockOpLabel(VarBlockSlots block, int pdfIndex, List<List<int>> tokenOpStartLists, List<List<int>> tokenOpEndLists, List<List<string>> tokenOpNames, List<TokenAlignment> alignments)
        {
            int minOp = int.MaxValue;
            int maxOp = -1;
            var ops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddOpRange(int startOp, int endOp, string opName)
            {
                if (startOp <= 0 && endOp <= 0) return;
                if (startOp > 0)
                {
                    minOp = Math.Min(minOp, startOp);
                    maxOp = Math.Max(maxOp, startOp);
                }
                if (endOp > 0)
                {
                    minOp = Math.Min(minOp, endOp);
                    maxOp = Math.Max(maxOp, endOp);
                }
                if (!string.IsNullOrWhiteSpace(opName))
                    ops.Add(opName);
            }

            var maxSlot = block.EndSlot;
            for (int slot = block.StartSlot; slot <= maxSlot; slot++)
            {
                if (slot % 2 == 0)
                {
                    if (pdfIndex == 0)
                        continue;
                    int gap = slot / 2;
                    var alignment = alignments[pdfIndex - 1];
                    var otherOpStarts = tokenOpStartLists[pdfIndex];
                    var otherOpEnds = tokenOpEndLists[pdfIndex];
                    var otherNames = tokenOpNames[pdfIndex];
                    foreach (var tokenIdx in alignment.Insertions[gap])
                    {
                        if (tokenIdx >= 0 && tokenIdx < otherOpStarts.Count)
                            AddOpRange(otherOpStarts[tokenIdx], tokenIdx < otherOpEnds.Count ? otherOpEnds[tokenIdx] : otherOpStarts[tokenIdx], otherNames[tokenIdx]);
                    }
                }
                else
                {
                    int tokenIdx = (slot - 1) / 2;
                    if (tokenIdx < 0 || tokenIdx >= tokenOpStartLists[0].Count)
                        continue;

                    if (pdfIndex == 0)
                    {
                        var baseStart = tokenOpStartLists[0][tokenIdx];
                        var baseEnd = tokenIdx < tokenOpEndLists[0].Count ? tokenOpEndLists[0][tokenIdx] : baseStart;
                        AddOpRange(baseStart, baseEnd, tokenOpNames[0][tokenIdx]);
                    }
                    else
                    {
                        var alignment = alignments[pdfIndex - 1];
                        var otherIdx = alignment.BaseToOther[tokenIdx];
                        if (otherIdx >= 0 && otherIdx < tokenOpStartLists[pdfIndex].Count)
                        {
                            var otherStart = tokenOpStartLists[pdfIndex][otherIdx];
                            var otherEnd = otherIdx < tokenOpEndLists[pdfIndex].Count ? tokenOpEndLists[pdfIndex][otherIdx] : otherStart;
                            AddOpRange(otherStart, otherEnd, tokenOpNames[pdfIndex][otherIdx]);
                        }
                    }
                }
            }

            if (maxOp < 0)
                return "-";

            string range = minOp == maxOp ? $"op{minOp}" : $"op{minOp}-{maxOp}";
            if (ops.Count > 0)
            {
                var label = string.Join("/", ops.OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
                range += $"[{label}]";
            }

            return range;
        }

        private static string EscapeBlockText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text
                .Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t")
                .Replace("\"", "\\\"");
        }

        private static void PrintFullTextDiffWithRange(
            List<string> inputs,
            List<FullTextOpsResult> fullResults,
            bool diffLineMode,
            bool cleanupSemantic,
            bool cleanupLossless,
            bool cleanupEfficiency,
            string rangeStartRegex,
            string rangeEndRegex,
            int? rangeStartOp,
            int? rangeEndOp,
            bool dumpRangeText,
            string roiDoc,
            int objId,
            bool showEqual)
        {
            if (inputs.Count < 2)
                return;

            FullTextDiffReport? report = null;
            if (ReturnUtils.IsEnabled())
                report = new FullTextDiffReport();

            var startOps = new List<int>();
            var endOps = new List<int>();
            var rangesPerFile = new List<(string Name, int Start, int End)>();
            var hasExplicitRange = rangeStartOp.HasValue
                || rangeEndOp.HasValue
                || !string.IsNullOrWhiteSpace(rangeStartRegex)
                || !string.IsNullOrWhiteSpace(rangeEndRegex);
            TextOpsRoiFile? roi = null;
            string roiPath = "";
            if (!hasExplicitRange)
            {
                roiPath = ResolveRoiPath(roiDoc, objId);
                if (string.IsNullOrWhiteSpace(roiPath))
                    roiPath = ResolveAnyRoiPath(objId);
                roi = LoadRoi(roiPath);
            }

            for (int i = 0; i < inputs.Count; i++)
            {
                var name = Path.GetFileName(inputs[i]);
                var full = fullResults[i];

                if (!hasExplicitRange && roi != null)
                {
                    if (!TryResolveRangeFromRoi(roi, name, out var startRoi, out var endRoi, out var reasonRoi))
                    {
                        Console.WriteLine($"Range invalido para {name}: {reasonRoi}");
                        return;
                    }
                    startOps.Add(startRoi);
                    endOps.Add(endRoi);
                    rangesPerFile.Add((name, startRoi, endRoi));
                    continue;
                }

                // Sem range explícito e sem ROI: usa o range total disponível no texto.
                if (!hasExplicitRange && roi == null)
                {
                    if (full.OpIndexes == null || full.OpIndexes.Count == 0)
                    {
                        Console.WriteLine($"Range invalido para {name}: sem ops disponiveis");
                        return;
                    }
                    var validOps = full.OpIndexes.Where(o => o > 0).ToList();
                    if (validOps.Count == 0)
                    {
                        Console.WriteLine($"Range invalido para {name}: ops invalidos");
                        return;
                    }
                    var startFull = validOps.Min();
                    var endFull = validOps.Max();
                    startOps.Add(startFull);
                    endOps.Add(endFull);
                    rangesPerFile.Add((name, startFull, endFull));
                    continue;
                }

                if (!TryResolveRange(full, rangeStartRegex, rangeEndRegex, rangeStartOp, rangeEndOp, out var start, out var end, out var reason))
                {
                    Console.WriteLine($"Range invalido para {name}: {reason}");
                    return;
                }

                startOps.Add(start);
                endOps.Add(end);
                rangesPerFile.Add((name, start, end));
            }

            int globalStart = startOps.Min();
            int globalEnd = endOps.Max();
            if (globalEnd < globalStart)
            {
                Console.WriteLine("Range global invalido (fim < inicio).");
                return;
            }

            if (report != null)
            {
                report.Range = $"op{globalStart}-op{globalEnd}";
                report.Roi = (!hasExplicitRange && roi != null && !string.IsNullOrWhiteSpace(roiPath)) ? roiPath : "";
                foreach (var r in rangesPerFile)
                    report.Ranges.Add((r.Name, $"op{r.Start}-op{r.End}"));
            }

            ReportUtils.WriteSummary("TEXTOPS DIFF", new List<(string Key, string Value)>
            {
                ("inputs", inputs.Count.ToString(CultureInfo.InvariantCulture)),
                ("range", $"op{globalStart}-op{globalEnd}"),
                ("roi", (!hasExplicitRange && roi != null && !string.IsNullOrWhiteSpace(roiPath)) ? roiPath : "-")
            });
            Console.WriteLine();

            var rangeRows = rangesPerFile.Select(r => new[]
            {
                r.Name,
                $"op{r.Start}-op{r.End}"
            });
            ReportUtils.WriteTable("RANGES", new[] { "file", "op_range" }, rangeRows);
            Console.WriteLine();

            var slicedTexts = new List<string>();
            var slicedOps = new List<List<int>>();
            var slicedOpNames = new List<List<string>>();

            for (int i = 0; i < fullResults.Count; i++)
            {
                var range = rangesPerFile[i];
                var sliced = SliceFullTextByOpRange(fullResults[i], range.Start, range.End);
                slicedTexts.Add(sliced.Text);
                slicedOps.Add(sliced.OpIndexes);
                slicedOpNames.Add(sliced.OpNames);
            }

            if (dumpRangeText)
            {
                var textRows = new List<string[]>();
                for (int i = 0; i < inputs.Count; i++)
                {
                    var name = Path.GetFileName(inputs[i]);
                    var text = slicedTexts[i];
                    var display = EscapeBlockText(text);
                    textRows.Add(new[] { name, display, text.Length.ToString(CultureInfo.InvariantCulture) });
                    if (report != null)
                        report.RangeTexts.Add((name, text, text.Length));
                }
                ReportUtils.WriteTable("RANGE TEXT", new[] { "file", "text", "len" }, textRows);
                Console.WriteLine();
            }

            PrintFullTextDiff(inputs, slicedTexts, slicedOps, slicedOpNames, diffLineMode, cleanupSemantic, cleanupLossless, cleanupEfficiency, showEqual, report);

            if (report != null)
            {
                var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                Console.WriteLine(json);
            }
        }

        private static bool TryResolveRange(
            FullTextOpsResult full,
            string rangeStartRegex,
            string rangeEndRegex,
            int? rangeStartOp,
            int? rangeEndOp,
            out int startOp,
            out int endOp,
            out string reason)
        {
            startOp = 0;
            endOp = 0;
            reason = "";

            var hasOps = full.OpIndexes != null && full.OpIndexes.Count > 0;
            var validOps = hasOps ? full.OpIndexes.Where(o => o > 0).ToList() : new List<int>();
            var hasValidOps = validOps.Count > 0;
            var minOp = hasValidOps ? validOps.Min() : 0;
            var maxOp = hasValidOps ? validOps.Max() : 0;

            if (rangeStartOp.HasValue)
                startOp = rangeStartOp.Value;
            else if (!string.IsNullOrWhiteSpace(rangeStartRegex))
            {
                if (!TryFindOpByRegex(full, rangeStartRegex, true, out startOp))
                {
                    reason = $"range-start regex nao encontrado: {rangeStartRegex}";
                    return false;
                }
            }
            else
            {
                if (!hasValidOps)
                {
                    reason = "range-start nao definido";
                    return false;
                }
                startOp = minOp;
            }

            if (rangeEndOp.HasValue)
                endOp = rangeEndOp.Value;
            else if (!string.IsNullOrWhiteSpace(rangeEndRegex))
            {
                if (!TryFindOpByRegex(full, rangeEndRegex, false, out endOp))
                {
                    reason = $"range-end regex nao encontrado: {rangeEndRegex}";
                    return false;
                }
            }
            else
            {
                if (!hasValidOps)
                {
                    reason = "range-end nao definido";
                    return false;
                }
                endOp = maxOp;
            }

            return true;
        }

        private static bool TryResolveRangeFromRoi(TextOpsRoiFile roi, string fileName, out int startOp, out int endOp, out string reason)
        {
            startOp = 0;
            endOp = 0;
            reason = "";

            if (roi == null)
            {
                reason = "ROI nao carregado";
                return false;
            }

            var range = FindRoiRange(roi.FrontHead, fileName) ?? FindRoiRange(roi.BackTail, fileName);
            if (range == null)
            {
                reason = "ROI nao encontrado para o arquivo";
                return false;
            }

            startOp = range.StartOp;
            endOp = range.EndOp;
            if (startOp <= 0 || endOp <= 0)
            {
                reason = "ROI invalido (start/end <= 0)";
                return false;
            }

            return true;
        }

        private static TextOpsRoiRange? FindRoiRange(TextOpsRoiSection? section, string fileName)
        {
            if (section == null || section.Ranges.Count == 0)
                return null;

            var name = Path.GetFileName(fileName ?? "");
            var match = section.Ranges.FirstOrDefault(r =>
                !string.IsNullOrWhiteSpace(r.SourceFile) &&
                string.Equals(r.SourceFile, name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;

            match = section.Ranges.FirstOrDefault(r =>
                string.IsNullOrWhiteSpace(r.SourceFile) ||
                r.SourceFile == "*" ||
                string.Equals(r.SourceFile, "default", StringComparison.OrdinalIgnoreCase));

            return match;
        }

        private static bool TryFindOpByRegex(FullTextOpsResult full, string pattern, bool first, out int opIndex)
        {
            opIndex = 0;
            if (string.IsNullOrWhiteSpace(pattern))
                return false;

            try
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var matches = regex.Matches(full.Text ?? "");
                if (matches.Count == 0)
                    return false;

                var match = first ? matches[0] : matches[^1];
                if (match.Length == 0)
                    return false;

                return TryGetOpFromSpan(full.OpIndexes, match.Index, match.Length, first, out opIndex);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetOpFromSpan(List<int> opIndexes, int start, int length, bool first, out int op)
        {
            op = 0;
            if (opIndexes == null || opIndexes.Count == 0)
                return false;
            int end = Math.Min(opIndexes.Count, start + length);
            if (start < 0 || start >= end)
                return false;

            if (first)
            {
                for (int i = start; i < end; i++)
                {
                    if (opIndexes[i] > 0)
                    {
                        op = opIndexes[i];
                        return true;
                    }
                }
            }
            else
            {
                for (int i = end - 1; i >= start; i--)
                {
                    if (opIndexes[i] > 0)
                    {
                        op = opIndexes[i];
                        return true;
                    }
                }
            }

            return false;
        }

        private static FullTextOpsResult SliceFullTextByOpRange(FullTextOpsResult full, int startOp, int endOp)
        {
            if (startOp < 1) startOp = 1;
            if (endOp < startOp) endOp = startOp;

            var sb = new StringBuilder();
            var ops = new List<int>();
            var opNames = new List<string>();

            for (int i = 0; i < full.Text.Length && i < full.OpIndexes.Count && i < full.OpNames.Count; i++)
            {
                var op = full.OpIndexes[i];
                if (op >= startOp && op <= endOp)
                {
                    sb.Append(full.Text[i]);
                    ops.Add(op);
                    opNames.Add(full.OpNames[i]);
                }
            }

            return new FullTextOpsResult(sb.ToString(), ops, opNames);
        }

        private static void PrintFullTextDiff(
            List<string> inputs,
            List<string> texts,
            List<List<int>> tokenOpLists,
            List<List<string>> tokenOpNames,
            bool diffLineMode,
            bool cleanupSemantic,
            bool cleanupLossless,
            bool cleanupEfficiency,
            bool showEqual = false,
            FullTextDiffReport? report = null)
        {
            if (inputs.Count < 2)
                return;

            var baseName = Path.GetFileName(inputs[0]);
            var baseText = texts.Count > 0 ? texts[0] : "";
            var baseOps = tokenOpLists[0];
            var baseOpNames = tokenOpNames[0];

            for (int i = 1; i < inputs.Count; i++)
            {
                var otherName = Path.GetFileName(inputs[i]);
                var otherText = texts[i];
                var otherOps = tokenOpLists[i];
                var otherOpNames = tokenOpNames[i];

                FullTextDiffPair? pair = null;
                if (report != null)
                {
                    pair = new FullTextDiffPair { A = baseName, B = otherName };
                    report.Pairs.Add(pair);
                }

                ReportUtils.WriteSummary("DIFF", new List<(string Key, string Value)>
                {
                    ("A", baseName),
                    ("B", otherName)
                });

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
                            var display = EscapeBlockText(diff.text);
                            Console.WriteLine($"EQ  {label}\t{baseName}\t\"{display}\" (len={len})");
                            pair?.Items.Add(new FullTextDiffItem
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
                        var display = EscapeBlockText(diff.text);
                        Console.WriteLine($"DEL {label}\t{baseName}\t\"{display}\" (len={len})");
                        pair?.Items.Add(new FullTextDiffItem
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
                        var display = EscapeBlockText(diff.text);
                        Console.WriteLine($"INS {label}\t{otherName}\t\"{display}\" (len={len})");
                        pair?.Items.Add(new FullTextDiffItem
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

                Console.WriteLine();
            }
        }

        private static string BuildOpRangeLabel(List<int> opIndexes, List<string> opNames, int start, int length)
        {
            if (length <= 0 || start < 0 || start >= opIndexes.Count)
                return "op?";

            int minOp = int.MaxValue;
            int maxOp = -1;
            var ops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int end = Math.Min(opIndexes.Count, start + length);
            for (int i = start; i < end; i++)
            {
                var op = opIndexes[i];
                if (op <= 0) continue;
                minOp = Math.Min(minOp, op);
                maxOp = Math.Max(maxOp, op);
                if (i < opNames.Count && !string.IsNullOrWhiteSpace(opNames[i]))
                    ops.Add(opNames[i]);
            }

            if (maxOp < 0)
                return "op?";

            string range = minOp == maxOp ? $"op{minOp}" : $"op{minOp}-{maxOp}";
            if (ops.Count > 0)
                range += $"[{string.Join("/", ops.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))}]";
            return range;
        }

        private static string FormatBlockRange(int startSlot, int endSlot)
        {
            int? startOp = null;
            int? endOp = null;
            int? startGap = null;
            int? endGap = null;

            for (int slot = startSlot; slot <= endSlot; slot++)
            {
                if (slot % 2 == 0)
                {
                    int gap = slot / 2;
                    startGap ??= gap;
                    endGap = gap;
                }
                else
                {
                    int op = (slot - 1) / 2 + 1;
                    startOp ??= op;
                    endOp = op;
                }
            }

            if (startOp.HasValue)
            {
                if (startOp.Value == endOp)
                    return $"textop {startOp}";
                return $"textops {startOp}-{endOp}";
            }

            if (startGap.HasValue)
            {
                if (startGap.Value == endGap)
                    return $"gap {startGap}";
                return $"gaps {startGap}-{endGap}";
            }

            return $"slots {startSlot}-{endSlot}";
        }

        private sealed class VarBlockSlots
        {
            public VarBlockSlots(int startSlot, int endSlot)
            {
                StartSlot = startSlot;
                EndSlot = endSlot;
            }

            public int StartSlot { get; }
            public int EndSlot { get; }
        }


    }
}
