using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Obj.Commands;
using Obj.Models;
using Obj.Utils;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Extraction;
using Obj.TjpbDespachoExtractor.Models;
using Obj.TjpbDespachoExtractor.Utils;
using Obj.ValidatorModule;

namespace Obj.TjpbDespachoExtractor.Commands
{
    public class TjpbDespachoExtractorCommand : Command
    {
        public override string Name => "despacho extract";
        public override string Description => "Extrai despachos DIESP do TJPB com segmentacao e campos estruturados";

        public override void Execute(string[] args)
        {
            if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
            {
                ShowHelp();
                return;
            }

            if (args[0].Equals("extract", StringComparison.OrdinalIgnoreCase))
            {
                RunExtract(args.Skip(1).ToArray());
                return;
            }
            if (args[0].Equals("bookmarks", StringComparison.OrdinalIgnoreCase))
            {
                RunBookmarks(args.Skip(1).ToArray());
                return;
            }
            if (args[0].Equals("report", StringComparison.OrdinalIgnoreCase))
            {
                RunReport(args.Skip(1).ToArray());
                return;
            }

            Console.Error.WriteLine("Comando invalido. Use: despacho extract ...");
        }

        private void RunExtract(string[] args)
        {
            string inbox = "data/inbox";
            string configPath = "config.yaml";
            string? outDir = null;
            string? processFilter = null;
            bool dump = false;
            bool verbose = false;
            string pgUri = PgDocStore.DefaultPgUri;
            bool skipLoad = false;
            int limit = 100;
            int offset = 0;
            string? bookmarkContains = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--inbox":
                        if (i + 1 < args.Length) inbox = args[++i];
                        break;
                    case "--config":
                        if (i + 1 < args.Length) configPath = args[++i];
                        break;
                    case "--out":
                        if (i + 1 < args.Length) outDir = args[++i];
                        break;
                    case "--dump":
                        dump = true;
                        break;
                    case "--verbose":
                        verbose = true;
                        break;
                    case "--process":
                        if (i + 1 < args.Length) processFilter = args[++i];
                        break;
                    case "--pg":
                        if (i + 1 < args.Length) pgUri = args[++i];
                        break;
                    case "--skip-load":
                        skipLoad = true;
                        break;
                    case "--bookmark-contains":
                        if (i + 1 < args.Length) bookmarkContains = args[++i];
                        break;
                    case "--limit":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var lim)) limit = lim;
                        break;
                    case "--offset":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var off)) offset = off;
                        break;
                }
            }

            var cfg = TjpbDespachoConfig.Load(configPath);
            var extractor = new DespachoExtractor(cfg);
            var options = new ExtractionOptions { Dump = dump, DumpDir = outDir, Verbose = verbose, BookmarkContains = bookmarkContains };
            PgDocStore.DefaultPgUri = pgUri;

            if (!skipLoad)
            {
                if (Directory.Exists(inbox))
                {
                    Console.WriteLine($"[despacho extract] carregando PDFs do inbox '{inbox}'...");
                    var loader = new FpdfLoadCommand();
                    loader.Execute(new[] { "--input-dir", inbox });
                }
                else
                {
                    Console.Error.WriteLine($"Inbox nao encontrado: {inbox}");
                }
            }

            var rows = PgAnalysisLoader.ListRawProcesses(pgUri, inbox, limit, offset);
            if (!string.IsNullOrWhiteSpace(processFilter))
                rows = rows.Where(r => string.Equals(r.ProcessNumber, processFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (rows.Count == 0)
            {
                Console.WriteLine("Nenhum processo encontrado em raw_processes.");
                return;
            }

            foreach (var row in rows)
            {
                try
                {
                    var analysis = PgAnalysisLoader.Deserialize(row.Json);
                    if (analysis == null)
                    {
                        Console.Error.WriteLine($"[despacho extract] WARN {row.ProcessNumber}: raw_json invalido");
                        continue;
                    }

                    var processNumber = row.ProcessNumber ?? "";
                    if (string.IsNullOrWhiteSpace(processNumber))
                        processNumber = Path.GetFileNameWithoutExtension(row.Source ?? "output");

                    options.ProcessNumber = processNumber;
                    var footerInfo = PgAnalysisLoader.GetFooterInfo(processNumber, pgUri);
                    if (footerInfo == null || (footerInfo.Signers.Count == 0 && string.IsNullOrWhiteSpace(footerInfo.SignatureRaw)))
                        footerInfo = BuildFooterInfoFromAnalysis(analysis);
                    options.FooterSigners = footerInfo?.Signers ?? new List<string>();
                    options.FooterSignatureRaw = footerInfo?.SignatureRaw;
                    if (verbose)
                    {
                        Console.WriteLine($"[despacho extract] {processNumber} footer_signers={options.FooterSigners.Count} footer_raw_len={(options.FooterSignatureRaw?.Length ?? 0)}");
                    }
                    var output = extractor.Extract(analysis, row.Source ?? "", options, entry =>
                    {
                        if (verbose)
                        {
                            Console.WriteLine($"[{entry.Level}] {entry.Message} {JsonConvert.SerializeObject(entry.Data)}");
                        }
                    });

                    var json = JsonConvert.SerializeObject(output, Formatting.Indented);

                    PgDocStore.UpsertProcess(pgUri, row.Source ?? "", analysis, new BookmarkClassifier(),
                        storeJson: true, storeDocuments: false, jsonPayload: json);

                    if (!string.IsNullOrWhiteSpace(outDir))
                    {
                        Directory.CreateDirectory(outDir);
                        var fileName = SanitizeFileName(processNumber);
                        var outPath = Path.Combine(outDir, fileName + ".json");
                        File.WriteAllText(outPath, json);
                    }

                    if (dump)
                    {
                        var dumpDir = outDir ?? Path.Combine(Directory.GetCurrentDirectory(), "dumps");
                        DumpOutput(output, dumpDir);
                    }

                    Console.WriteLine($"[despacho extract] OK {processNumber}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[despacho extract] ERROR {row.ProcessNumber ?? "processo_desconhecido"}: {ex.Message}");
                }
            }
        }

        private void RunBookmarks(string[] args)
        {
            string inbox = "data/inbox";
            string? processFilter = null;
            string? titleContains = null;
            bool skipLoad = false;
            bool json = false;
            int limit = 100;
            int offset = 0;
            string pgUri = PgDocStore.DefaultPgUri;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--inbox":
                        if (i + 1 < args.Length) inbox = args[++i];
                        break;
                    case "--process":
                        if (i + 1 < args.Length) processFilter = args[++i];
                        break;
                    case "--contains":
                        if (i + 1 < args.Length) titleContains = args[++i];
                        break;
                    case "--pg":
                        if (i + 1 < args.Length) pgUri = args[++i];
                        break;
                    case "--skip-load":
                        skipLoad = true;
                        break;
                    case "--json":
                        json = true;
                        break;
                    case "--limit":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var lim)) limit = lim;
                        break;
                    case "--offset":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var off)) offset = off;
                        break;
                }
            }

            PgDocStore.DefaultPgUri = pgUri;

            if (!skipLoad)
            {
                if (Directory.Exists(inbox))
                {
                    Console.WriteLine($"[tjpb-bookmarks] carregando PDFs do inbox '{inbox}'...");
                    var loader = new FpdfLoadCommand();
                    loader.Execute(new[] { "--input-dir", inbox });
                }
                else
                {
                    Console.Error.WriteLine($"Inbox nao encontrado: {inbox}");
                }
            }

            var rows = PgAnalysisLoader.ListRawProcesses(pgUri, inbox, limit, offset);
            if (!string.IsNullOrWhiteSpace(processFilter))
                rows = rows.Where(r => string.Equals(r.ProcessNumber, processFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            if (rows.Count == 0)
            {
                Console.WriteLine("Nenhum processo encontrado em raw_processes.");
                return;
            }

            foreach (var row in rows)
            {
                var analysis = PgAnalysisLoader.Deserialize(row.Json);
                if (analysis == null)
                {
                    Console.Error.WriteLine($"[tjpb-bookmarks] WARN {row.ProcessNumber}: raw_json invalido");
                    continue;
                }

                var totalPages = analysis.DocumentInfo?.TotalPages ?? analysis.Pages.Count;
                var flat = FlattenBookmarks(analysis.Bookmarks?.RootItems ?? new List<BookmarkItem>());
                var ordered = flat.Where(b => b.Page1 > 0).OrderBy(b => b.Page1).ThenBy(b => b.Level).ToList();

                var ranges = new List<object>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var start = ordered[i].Page1;
                    var end = (i + 1 < ordered.Count) ? Math.Max(start, ordered[i + 1].Page1 - 1) : totalPages;
                    var title = ordered[i].Title ?? "";
                    if (!string.IsNullOrWhiteSpace(titleContains) &&
                        !title.Contains(titleContains, StringComparison.OrdinalIgnoreCase))
                        continue;
                    ranges.Add(new
                    {
                        title,
                        level = ordered[i].Level,
                        page1 = ordered[i].Page1,
                        page0 = ordered[i].Page1 - 1,
                        startPage1 = start,
                        endPage1 = end
                    });
                }

                if (json)
                {
                    var payload = new
                    {
                        process = row.ProcessNumber,
                        source = row.Source,
                        pages = totalPages,
                        bookmarks = ranges
                    };
                    Console.WriteLine(JsonConvert.SerializeObject(payload, Formatting.Indented));
                }
                else
                {
                    Console.WriteLine($"[tjpb-bookmarks] {row.ProcessNumber} ({Path.GetFileName(row.Source ?? "")}) pages={totalPages}");
                    foreach (dynamic r in ranges)
                    {
                        Console.WriteLine($"  p{r.startPage1}-{r.endPage1} [L{r.level}] {r.title}");
                    }
                }
            }
        }

        private static PgAnalysisLoader.FooterInfo BuildFooterInfoFromAnalysis(PDFAnalysisResult analysis)
        {
            var info = new PgAnalysisLoader.FooterInfo();
            if (analysis?.Pages == null || analysis.Pages.Count == 0) return info;
            var last = analysis.Pages[analysis.Pages.Count - 1];
            var lines = (last.TextInfo?.PageText ?? "")
                .Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .ToList();
            if (lines.Count == 0) return info;
            var tail = lines.Count > 12 ? lines.Skip(lines.Count - 12).ToList() : lines;
            info.SignatureRaw = string.Join("\n", tail);
            info.Signers = ExtractSignersFromLines(tail);
            return info;
        }

        private static List<string> ExtractSignersFromLines(IEnumerable<string> lines)
        {
            var signers = new List<string>();
            var catalog = SignaturePatternCatalog.Current;
            var nameRegex = catalog.SignerName;
            var pjeRegex = catalog.SignerPje;

            foreach (var line in lines)
            {
                var m = nameRegex.Match(line);
                if (m.Success)
                {
                    var name = m.Value.Trim();
                    if (!signers.Contains(name))
                        signers.Add(name);
                }
                var pje = pjeRegex.Match(line.Replace(" ", ""));
                if (pje.Success)
                {
                    var name = pje.Groups[1].Value.Trim();
                    if (!signers.Contains(name))
                        signers.Add(name);
                }
            }
            return signers;
        }

        private void RunReport(string[] args)
        {
            string configPath = "config.yaml";
            string pgUri = PgDocStore.DefaultPgUri;
            string? processFilter = null;
            string? sourceContains = null;
            int limit = 100;
            int offset = 0;
            bool json = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config":
                        if (i + 1 < args.Length) configPath = args[++i];
                        break;
                    case "--pg":
                        if (i + 1 < args.Length) pgUri = args[++i];
                        break;
                    case "--process":
                        if (i + 1 < args.Length) processFilter = args[++i];
                        break;
                    case "--source-contains":
                        if (i + 1 < args.Length) sourceContains = args[++i];
                        break;
                    case "--limit":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var lim)) limit = lim;
                        break;
                    case "--offset":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var off)) offset = off;
                        break;
                    case "--json":
                        json = true;
                        break;
                }
            }

            var cfg = TjpbDespachoConfig.Load(configPath);
            var rows = PgAnalysisLoader.ListProcesses(pgUri);
            if (!string.IsNullOrWhiteSpace(processFilter))
                rows = rows.Where(r => string.Equals(r.ProcessNumber, processFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrWhiteSpace(sourceContains))
                rows = rows.Where(r => (r.Source ?? "").Contains(sourceContains, StringComparison.OrdinalIgnoreCase)).ToList();

            rows = rows.Skip(Math.Max(0, offset)).Take(limit).ToList();
            if (rows.Count == 0)
            {
                Console.WriteLine("Nenhum processo encontrado na tabela processes.");
                return;
            }

            var stats = new ReportStats(cfg);
            foreach (var row in rows)
            {
                ExtractionResult? result = null;
                try
                {
                    result = JsonConvert.DeserializeObject<ExtractionResult>(row.Json);
                }
                catch
                {
                    stats.InvalidJson++;
                    continue;
                }

                if (result == null || result.Documents == null || result.Documents.Count == 0)
                {
                    stats.NoDocuments++;
                    continue;
                }

                stats.TotalPdfs++;
                foreach (var doc in result.Documents)
                {
                    stats.AddDoc(row.ProcessNumber ?? "", doc);
                }
            }

            if (json)
            {
                Console.WriteLine(JsonConvert.SerializeObject(stats.ToJsonObject(), Formatting.Indented));
                return;
            }

            Console.WriteLine(stats.ToText());
        }

        private List<BookmarkFlatItem> FlattenBookmarks(List<BookmarkItem> items)
        {
            var list = new List<BookmarkFlatItem>();
            void Walk(IEnumerable<BookmarkItem> nodes)
            {
                foreach (var n in nodes)
                {
                    if (n.Destination?.PageNumber > 0)
                    {
                        list.Add(new BookmarkFlatItem
                        {
                            Title = n.Title ?? "",
                            Page1 = n.Destination.PageNumber,
                            Level = n.Level
                        });
                    }
                    if (n.Children != null && n.Children.Count > 0)
                        Walk(n.Children);
                }
            }
            Walk(items);
            return list;
        }

        private class BookmarkFlatItem
        {
            public string Title { get; set; } = "";
            public int Page1 { get; set; }
            public int Level { get; set; }
        }

        private void DumpOutput(ExtractionResult result, string rootDir)
        {
            var name = SanitizeFileName(result.Pdf.FileName);
            var dir = Path.Combine(rootDir, name);
            Directory.CreateDirectory(dir);

            var pagesDir = Path.Combine(dir, "pages");
            Directory.CreateDirectory(pagesDir);
            var bandOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "header", 1 },
                { "subheader", 2 },
                { "title", 3 },
                { "body", 4 },
                { "footer", 5 }
            };
            var bandByPage = result.Documents.SelectMany(d => d.Bands)
                .GroupBy(b => b.Page1)
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var group in bandByPage)
            {
                var content = string.Join("\n\n", group.OrderBy(b => bandOrder.TryGetValue(b.Band, out var o) ? o : 9).Select(b => b.Text));
                File.WriteAllText(Path.Combine(pagesDir, $"page_{group.Key:000}.txt"), content);
            }

            File.WriteAllText(Path.Combine(dir, "bands.json"), JsonConvert.SerializeObject(result.Documents.SelectMany(d => d.Bands), Formatting.Indented));
            File.WriteAllText(Path.Combine(dir, "paragraphs.json"), JsonConvert.SerializeObject(result.Documents.SelectMany(d => d.Paragraphs), Formatting.Indented));
            File.WriteAllText(Path.Combine(dir, "fields.json"), JsonConvert.SerializeObject(result.Documents.Select(d => d.Fields), Formatting.Indented));
        }

        private string SanitizeFileName(string? name)
        {
            var n = name ?? "";
            foreach (var c in Path.GetInvalidFileNameChars())
                n = n.Replace(c, '_');
            return string.IsNullOrWhiteSpace(n) ? "output" : n;
        }

        public override void ShowHelp()
        {
            Console.WriteLine("despacho extract --inbox <dir> --config <config.yaml> [--out <dir>] [--dump] [--verbose] [--process <num>] [--bookmark-contains <text>] [--limit <n>] [--offset <n>] [--pg <uri>] [--skip-load]");
            Console.WriteLine("despacho bookmarks --inbox <dir> [--process <num>] [--contains <text>] [--json] [--limit <n>] [--offset <n>] [--pg <uri>] [--skip-load]");
            Console.WriteLine("despacho report --config <config.yaml> [--pg <uri>] [--limit <n>] [--offset <n>] [--process <num>] [--source-contains <text>] [--json]");
            Console.WriteLine("--inbox: pasta com PDFs (default: data/inbox)");
            Console.WriteLine("--config: caminho do config.yaml (default: ./config.yaml)");
            Console.WriteLine("--out: salva JSON por PDF (export eventual)");
            Console.WriteLine("--dump: gera dumps de paginas/bandas/paragrafos (opcional)");
            Console.WriteLine("--process: filtra por numero de processo no Postgres");
            Console.WriteLine("--pg: string de conexao Postgres");
            Console.WriteLine("--skip-load: nao ingere inbox; usa apenas raw_processes");
        }

        private class ReportStats
        {
            private readonly TjpbDespachoConfig _cfg;
            private readonly Regex _cnj;
            private readonly Regex _cpf;
            private readonly Regex _money;
            private readonly Regex _date;
            private readonly Regex _dateIso;
            private readonly HashSet<string> _fields;

            public int TotalPdfs { get; set; }
            public int NoDocuments { get; set; }
            public int InvalidJson { get; set; }
            public int TotalDocs { get; set; }

            private readonly Dictionary<string, FieldStat> _fieldStats = new Dictionary<string, FieldStat>(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _warnings = new List<string>();

            public ReportStats(TjpbDespachoConfig cfg)
            {
                _cfg = cfg;
                _cnj = new Regex(cfg.Regex.ProcessoCnj, RegexOptions.Compiled);
                _cpf = new Regex(cfg.Regex.Cpf, RegexOptions.Compiled);
                _money = new Regex(cfg.Regex.Money, RegexOptions.Compiled);
                _date = new Regex($"{cfg.Regex.DatePt}|{cfg.Regex.DateSlash}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
                _dateIso = new Regex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);
                _fields = new HashSet<string>(new[]
                {
                    "PROCESSO_ADMINISTRATIVO","PROCESSO_JUDICIAL","VARA","COMARCA",
                    "PROMOVENTE","PROMOVIDO","PERITO","CPF_PERITO","ESPECIALIDADE",
                    "ESPECIE_DA_PERICIA","VALOR_ARBITRADO_JZ","VALOR_ARBITRADO_DE",
                    "VALOR_ARBITRADO_CM","VALOR_TABELADO_ANEXO_I","ADIANTAMENTO",
                    "PERCENTUAL","PARCELA","DATA","ASSINANTE","NUM_PERITO"
                }, StringComparer.OrdinalIgnoreCase);
            }

            public void AddDoc(string processNumber, DespachoDocumentInfo doc)
            {
                TotalDocs++;
                var fields = doc.Fields ?? new Dictionary<string, FieldInfo>();

                foreach (var key in _fields)
                {
                    if (!_fieldStats.TryGetValue(key, out var fs))
                    {
                        fs = new FieldStat(key);
                        _fieldStats[key] = fs;
                    }

                    fields.TryGetValue(key, out var f);
                    fs.Add(f, processNumber, IsSuspect(key, f, fields));
                }

                if (fields.TryGetValue("PROCESSO_ADMINISTRATIVO", out var pa) &&
                    fields.TryGetValue("PROCESSO_JUDICIAL", out var pj) &&
                    !IsMissing(pa) && !IsMissing(pj))
                {
                    if (Normalize(pa.Value) == Normalize(pj.Value))
                        _warnings.Add($"{processNumber}: PROCESSO_ADMINISTRATIVO == PROCESSO_JUDICIAL");
                }
            }

            private bool IsSuspect(string key, FieldInfo? f, Dictionary<string, FieldInfo> all)
            {
                if (IsMissing(f)) return false;
                var value = f!.Value ?? "";
                var norm = Normalize(value);

                if (key.Equals("CPF_PERITO", StringComparison.OrdinalIgnoreCase))
                {
                    var digits = new string(value.Where(char.IsDigit).ToArray());
                    if (digits.Length != 11) return true;
                    if (digits.Distinct().Count() == 1) return true;
                }
                if (key.Equals("PROCESSO_JUDICIAL", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_cnj.IsMatch(value)) return true;
                }
                if (key.Equals("PROCESSO_ADMINISTRATIVO", StringComparison.OrdinalIgnoreCase))
                {
                    if (_cnj.IsMatch(value)) return true;
                }
                if (key.StartsWith("VALOR_", StringComparison.OrdinalIgnoreCase) || key.Equals("ADIANTAMENTO", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_money.IsMatch(value)) return true;
                }
                if (key.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_date.IsMatch(value) && !_dateIso.IsMatch(value)) return true;
                }
                if (key is "PROMOVENTE" or "PROMOVIDO" or "PERITO" or "ASSINANTE")
                {
                    if (LooksLikeInstitution(norm)) return true;
                    if (HasDigits(norm)) return true;
                }
                if (key is "VARA" or "COMARCA")
                {
                    if (!norm.Contains("vara") && !norm.Contains("comarca") && norm.Length < 6) return true;
                }
                return false;
            }

            private static bool HasDigits(string value) => value.Any(char.IsDigit);
            private static bool IsMissing(FieldInfo? f) => f == null || f.Method == "not_found" || string.IsNullOrWhiteSpace(f.Value) || f.Value.Trim() == "-";
            private static string Normalize(string value) => Regex.Replace(value ?? "", "\\s+", " ").Trim().ToLowerInvariant();

            private static bool LooksLikeInstitution(string norm)
            {
                return ValidatorRules.ContainsInstitutional(norm);
            }

            public object ToJsonObject()
            {
                return new
                {
                    total_pdfs = TotalPdfs,
                    total_docs = TotalDocs,
                    no_documents = NoDocuments,
                    invalid_json = InvalidJson,
                    fields = _fieldStats.Values.Select(f => f.ToJson()).ToList(),
                    warnings = _warnings.Take(20).ToList()
                };
            }

            public string ToText()
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("RELATORIO TJPB - DESPACHO");
                sb.AppendLine($"PDFs analisados: {TotalPdfs}");
                sb.AppendLine($"Documentos encontrados: {TotalDocs}");
                if (NoDocuments > 0) sb.AppendLine($"Sem documentos: {NoDocuments}");
                if (InvalidJson > 0) sb.AppendLine($"JSON invalidos: {InvalidJson}");
                sb.AppendLine();
                sb.AppendLine("Cobertura por campo (filled/total | low_conf | suspect | methods)");
                foreach (var fs in _fieldStats.Values.OrderBy(f => f.Name))
                {
                    sb.AppendLine(fs.ToText(TotalDocs));
                }
                if (_warnings.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Alertas de consistencia (amostra):");
                    foreach (var w in _warnings.Take(10))
                        sb.AppendLine($"- {w}");
                }
                return sb.ToString();
            }
        }

        private class FieldStat
        {
            public string Name { get; }
            public int Filled { get; private set; }
            public int Missing { get; private set; }
            public int LowConf { get; private set; }
            public int Suspect { get; private set; }
            public int FilenameFallback { get; private set; }
            public Dictionary<string, int> MethodCounts { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public List<string> SuspectExamples { get; } = new List<string>();

            public FieldStat(string name) { Name = name; }

            public void Add(FieldInfo? f, string processNumber, bool isSuspect)
            {
                if (f == null || f.Method == "not_found" || string.IsNullOrWhiteSpace(f.Value) || f.Value.Trim() == "-")
                {
                    Missing++;
                    return;
                }
                Filled++;
                if (f.Confidence < 0.6) LowConf++;
                if (isSuspect)
                {
                    Suspect++;
                    if (SuspectExamples.Count < 5)
                        SuspectExamples.Add($"{processNumber}: {f.Value}");
                }
                if (!MethodCounts.ContainsKey(f.Method)) MethodCounts[f.Method] = 0;
                MethodCounts[f.Method]++;
                if (f.Method == "filename_fallback") FilenameFallback++;
            }

            public object ToJson()
            {
                return new
                {
                    field = Name,
                    filled = Filled,
                    missing = Missing,
                    low_conf = LowConf,
                    suspect = Suspect,
                    filename_fallback = FilenameFallback,
                    methods = MethodCounts,
                    examples = SuspectExamples
                };
            }

            public string ToText(int totalDocs)
            {
                var methods = string.Join(", ", MethodCounts.OrderByDescending(k => k.Value).Take(4).Select(k => $"{k.Key}:{k.Value}"));
                return $"{Name}: {Filled}/{totalDocs} | low_conf={LowConf} | suspect={Suspect} | methods={methods}";
            }
        }
    }
}
