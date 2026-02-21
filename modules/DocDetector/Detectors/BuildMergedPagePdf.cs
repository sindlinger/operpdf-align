using System;
using System.Globalization;
using System.IO;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using Path = System.IO.Path;

namespace Obj.DocDetector
{
    internal static class BuildMergedPagePdf
    {
        private enum LayoutMode
        {
            Vertical,
            Horizontal
        }

        public static void Execute(string[] args)
        {
            if (!ParseArgs(args, out var inputPath, out var outputPath, out var pageA, out var pageB, out var gap, out var layout))
            {
                ShowHelp();
                return;
            }

            if (string.IsNullOrWhiteSpace(inputPath))
            {
                Console.WriteLine("Erro: informe o PDF de entrada com --input <arquivo.pdf>.");
                Environment.ExitCode = 2;
                return;
            }

            if (!File.Exists(inputPath))
            {
                Console.WriteLine($"Erro: arquivo não encontrado: {inputPath}");
                Environment.ExitCode = 2;
                return;
            }

            if (pageA <= 0 || pageB <= 0)
            {
                Console.WriteLine("Erro: --page-a e --page-b devem ser >= 1.");
                Environment.ExitCode = 2;
                return;
            }

            gap = Math.Max(0f, gap);

            try
            {
                using var src = new PdfDocument(new PdfReader(inputPath));
                var totalPages = src.GetNumberOfPages();
                if (pageA > totalPages || pageB > totalPages)
                {
                    Console.WriteLine($"Erro: páginas fora do intervalo. total={totalPages}, solicitado={pageA},{pageB}");
                    Environment.ExitCode = 2;
                    return;
                }

                var pA = src.GetPage(pageA);
                var pB = src.GetPage(pageB);
                var sizeA = pA.GetPageSize();
                var sizeB = pB.GetPageSize();

                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    var baseName = Path.GetFileNameWithoutExtension(inputPath);
                    outputPath = Path.Combine(
                        "outputs",
                        "merged_pages",
                        $"{baseName}__p{pageA}_p{pageB}_merged_{layout.ToString().ToLowerInvariant()}.pdf");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

                using var dst = new PdfDocument(new PdfWriter(outputPath));
                var outputSize = layout == LayoutMode.Horizontal
                    ? new PageSize(sizeA.GetWidth() + gap + sizeB.GetWidth(), Math.Max(sizeA.GetHeight(), sizeB.GetHeight()))
                    : new PageSize(Math.Max(sizeA.GetWidth(), sizeB.GetWidth()), sizeA.GetHeight() + gap + sizeB.GetHeight());

                var dstPage = dst.AddNewPage(outputSize);
                var canvas = new PdfCanvas(dstPage);

                PdfFormXObject formA = pA.CopyAsFormXObject(dst);
                PdfFormXObject formB = pB.CopyAsFormXObject(dst);

                if (layout == LayoutMode.Horizontal)
                {
                    var yA = (outputSize.GetHeight() - sizeA.GetHeight()) / 2f;
                    var yB = (outputSize.GetHeight() - sizeB.GetHeight()) / 2f;
                    canvas.AddXObjectAt(formA, 0f, yA);
                    canvas.AddXObjectAt(formB, sizeA.GetWidth() + gap, yB);
                }
                else
                {
                    var xA = (outputSize.GetWidth() - sizeA.GetWidth()) / 2f;
                    var xB = (outputSize.GetWidth() - sizeB.GetWidth()) / 2f;
                    canvas.AddXObjectAt(formA, xA, sizeB.GetHeight() + gap);
                    canvas.AddXObjectAt(formB, xB, 0f);
                }

                Console.WriteLine("[MERGED_PAGE_PDF] ok");
                Console.WriteLine($"  input: {inputPath}");
                Console.WriteLine($"  pages: {pageA}+{pageB} of {totalPages}");
                Console.WriteLine($"  layout: {layout.ToString().ToLowerInvariant()}");
                Console.WriteLine($"  gap: {gap.ToString("0.##", CultureInfo.InvariantCulture)}");
                Console.WriteLine($"  out: {Path.GetFullPath(outputPath)}");
                Console.WriteLine($"  out_page_size: {outputSize.GetWidth().ToString("0.##", CultureInfo.InvariantCulture)} x {outputSize.GetHeight().ToString("0.##", CultureInfo.InvariantCulture)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Erro ao gerar PDF unificado: " + ex.Message);
                Environment.ExitCode = 1;
            }
        }

        private static bool ParseArgs(
            string[] args,
            out string inputPath,
            out string outputPath,
            out int pageA,
            out int pageB,
            out float gap,
            out LayoutMode layout)
        {
            inputPath = "";
            outputPath = "";
            pageA = 1;
            pageB = 2;
            gap = 0f;
            layout = LayoutMode.Vertical;

            if (args == null)
                return true;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = (args[i] ?? "").Trim();
                if (arg.Length == 0)
                    continue;
                if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
                    return false;

                if ((arg.Equals("--input", StringComparison.OrdinalIgnoreCase) || arg.Equals("--pdf", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    inputPath = (args[++i] ?? "").Trim();
                    continue;
                }
                if (arg.StartsWith("--input=", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("--pdf=", StringComparison.OrdinalIgnoreCase))
                {
                    var split = arg.Split('=', 2);
                    inputPath = split.Length == 2 ? (split[1] ?? "").Trim() : "";
                    continue;
                }

                if (arg.Equals("--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    outputPath = (args[++i] ?? "").Trim();
                    continue;
                }
                if (arg.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
                {
                    var split = arg.Split('=', 2);
                    outputPath = split.Length == 2 ? (split[1] ?? "").Trim() : "";
                    continue;
                }

                if ((arg.Equals("--page-a", StringComparison.OrdinalIgnoreCase) || arg.Equals("--p1", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out pageA);
                    continue;
                }
                if ((arg.Equals("--page-b", StringComparison.OrdinalIgnoreCase) || arg.Equals("--p2", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    int.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out pageB);
                    continue;
                }
                if (arg.Equals("--gap", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    float.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out gap);
                    continue;
                }
                if (arg.Equals("--layout", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var raw = (args[++i] ?? "").Trim().ToLowerInvariant();
                    layout = raw == "horizontal" ? LayoutMode.Horizontal : LayoutMode.Vertical;
                    continue;
                }

                if (!arg.StartsWith("-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(inputPath))
                {
                    inputPath = arg;
                }
            }

            return true;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("operpdf build-merged-page --input <arquivo.pdf> [--out <arquivo.pdf>] [--page-a N] [--page-b N] [--layout vertical|horizontal] [--gap N]");
            Console.WriteLine("exemplo:");
            Console.WriteLine("  operpdf build-merged-page --input models/nossos/despacho_p1-2.pdf --page-a 1 --page-b 2 --layout vertical");
        }
    }
}
