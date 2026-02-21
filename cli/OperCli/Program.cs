using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Obj.Commands;
using Obj.DocDetector;
using Obj.Utils;
using Obj.Logging;

namespace Obj.OperCli
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            EnvLoader.LoadDefaults();

            if (args.Length == 0 || IsHelp(args[0]))
            {
                ShowHelp();
                return 0;
            }

            var rawMode = (args[0] ?? "").Trim();
            var rawRest = args.Length > 1 ? args[1..] : Array.Empty<string>();
            if (IsBuildAlignExeMode(rawMode))
                return ExecuteBuildAlignExe(rawRest);

            InitLogger(args);
            args = ApplyGlobalOutputConfig(args);
            args = PathUtils.NormalizeArgs(args);
            if (args.Length == 0)
            {
                ShowHelp();
                return 1;
            }

            if (!ReturnUtils.IsEnabled())
                InputPreview.PrintPlannedInputs(args);
            Preflight.Run(args);
            Environment.ExitCode = 0;

            var mode = (args[0] ?? "").Trim().ToLowerInvariant();
            var rest = args.Length > 1 ? args[1..] : Array.Empty<string>();
            if (string.Equals(mode, "textopsalign", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsalign", args);
                ObjectsTextOpsAlign.Execute(StripDocArgs(rest));
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsalign-despacho", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "align-despacho", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsalign", args);
                ObjectsTextOpsAlign.Execute(ForceDoc(rest, "despacho"));
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsalign-certidao", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "align-certidao", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsalign", args);
                ObjectsTextOpsAlign.Execute(ForceDoc(rest, "certidao_conselho"));
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsalign-requerimento", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "align-requerimento", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsalign", args);
                ObjectsTextOpsAlign.Execute(ForceDoc(rest, "requerimento_honorarios"));
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsvar", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsvar", args);
                ObjectsTextOpsAlign.ExecuteWithMode(StripDocArgs(rest), ObjectsTextOpsAlign.OutputMode.VariablesOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsvar-despacho", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsvar", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDoc(rest, "despacho"), ObjectsTextOpsAlign.OutputMode.VariablesOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsvar-certidao", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsvar", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDoc(rest, "certidao_conselho"), ObjectsTextOpsAlign.OutputMode.VariablesOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsvar-requerimento", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsvar", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDoc(rest, "requerimento_honorarios"), ObjectsTextOpsAlign.OutputMode.VariablesOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsfixed", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsfixed", args);
                ObjectsTextOpsAlign.ExecuteWithMode(StripDocArgs(rest), ObjectsTextOpsAlign.OutputMode.FixedOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsfixed-despacho", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsfixed", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDoc(rest, "despacho"), ObjectsTextOpsAlign.OutputMode.FixedOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsfixed-certidao", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsfixed", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDoc(rest, "certidao_conselho"), ObjectsTextOpsAlign.OutputMode.FixedOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsfixed-requerimento", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsfixed", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDoc(rest, "requerimento_honorarios"), ObjectsTextOpsAlign.OutputMode.FixedOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "build-anchor-model-despacho", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "anchor-model-despacho", StringComparison.OrdinalIgnoreCase))
            {
                BuildAnchorModelDespacho.Execute(rest);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "build-merged-page", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "merge-pages", StringComparison.OrdinalIgnoreCase))
            {
                BuildMergedPagePdf.Execute(rest);
                return ResolveProcessExitCode();
            }

            Console.Error.WriteLine($"Comando não suportado neste projeto: {mode}");
            ShowHelp();
            return 1;
        }

        private static int ResolveProcessExitCode()
        {
            if (ObjectsTextOpsAlign.LastExitCode != 0)
                return ObjectsTextOpsAlign.LastExitCode;
            return Environment.ExitCode != 0 ? Environment.ExitCode : 0;
        }

        private static bool IsBuildAlignExeMode(string mode)
        {
            return string.Equals(mode, "build-align-exe", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mode, "build-exe", StringComparison.OrdinalIgnoreCase);
        }

        private static int ExecuteBuildAlignExe(string[] args)
        {
            var rid = "win-x64";
            var config = "Release";
            var noRestore = false;
            string? repoRootArg = null;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = (args[i] ?? "").Trim();
                if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) || arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
                {
                    ShowBuildAlignExeHelp();
                    return 0;
                }
                if (arg.Equals("--rid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    rid = (args[++i] ?? "").Trim();
                    continue;
                }
                if (arg.StartsWith("--rid=", StringComparison.OrdinalIgnoreCase))
                {
                    rid = (arg.Split('=', 2)[1] ?? "").Trim();
                    continue;
                }
                if (arg.Equals("--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    config = (args[++i] ?? "").Trim();
                    continue;
                }
                if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
                {
                    config = (arg.Split('=', 2)[1] ?? "").Trim();
                    continue;
                }
                if (arg.Equals("--repo-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    repoRootArg = args[++i];
                    continue;
                }
                if (arg.StartsWith("--repo-root=", StringComparison.OrdinalIgnoreCase))
                {
                    repoRootArg = arg.Split('=', 2)[1];
                    continue;
                }
                if (arg.Equals("--no-restore", StringComparison.OrdinalIgnoreCase))
                {
                    noRestore = true;
                    continue;
                }

                Console.Error.WriteLine($"Argumento não suportado: {arg}");
                ShowBuildAlignExeHelp();
                return 1;
            }

            var repoRoot = ResolveRepoRoot(repoRootArg);
            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                Console.Error.WriteLine("Não foi possível localizar a raiz do repositório (arquivo cli/OperCli/OperCli.csproj).");
                return 2;
            }

            var csprojPath = Path.Combine(repoRoot, "cli", "OperCli", "OperCli.csproj");
            if (!File.Exists(csprojPath))
            {
                Console.Error.WriteLine($"Projeto não encontrado: {csprojPath}");
                return 2;
            }

            var publishOutDir = Path.Combine(repoRoot, "cli", "OperCli", "publish", rid);
            Directory.CreateDirectory(publishOutDir);
            Console.WriteLine("[BUILD-ALIGN-EXE] iniciando publish");
            Console.WriteLine($"  repo_root: {repoRoot}");
            Console.WriteLine($"  project:   {csprojPath}");
            Console.WriteLine($"  rid:       {rid}");
            Console.WriteLine($"  config:    {config}");
            Console.WriteLine($"  out:       {publishOutDir}");

            var publishArgs = new List<string>
            {
                "publish",
                Quote(csprojPath),
                "-c", Quote(config),
                "-r", Quote(rid),
                "--self-contained", "true",
                "-p:PublishSingleFile=true",
                "-o", Quote(publishOutDir),
                "--ignore-failed-sources",
                "/p:RestoreIgnoreFailedSources=true",
                "/p:NuGetAudit=false",
                "/p:OPERPDF_SKIP_ROOT_COPY=1"
            };
            if (noRestore)
                publishArgs.Add("--no-restore");

            var code = RunProcess(
                "dotnet",
                string.Join(" ", publishArgs),
                repoRoot);
            if (code != 0)
            {
                Console.Error.WriteLine($"Publish falhou com código {code}.");
                return code;
            }

            var publishedExe = Path.Combine(publishOutDir, "operpdf.exe");
            if (!File.Exists(publishedExe))
            {
                Console.Error.WriteLine($"Executável não encontrado após publish: {publishedExe}");
                return 3;
            }

            var alignRoot = Path.Combine(repoRoot, "align.exe");
            var operpdfRoot = Path.Combine(repoRoot, "operpdf.exe");
            var alignCli = Path.Combine(repoRoot, "cli", "align.exe");
            var operpdfCli = Path.Combine(repoRoot, "cli", "operpdf.exe");

            Directory.CreateDirectory(Path.Combine(repoRoot, "cli"));
            CopyWithFallback(publishedExe, alignRoot, "align.exe");
            CopyWithFallback(publishedExe, operpdfRoot, "operpdf.exe");
            CopyWithFallback(publishedExe, alignCli, "cli/align.exe");
            CopyWithFallback(publishedExe, operpdfCli, "cli/operpdf.exe");

            Console.WriteLine("[BUILD-ALIGN-EXE] concluído");
            Console.WriteLine($"  align.exe: {alignRoot}");
            return 0;
        }

        private static string ResolveRepoRoot(string? repoRootArg)
        {
            if (!string.IsNullOrWhiteSpace(repoRootArg))
            {
                try
                {
                    var full = Path.GetFullPath(repoRootArg);
                    if (File.Exists(Path.Combine(full, "cli", "OperCli", "OperCli.csproj")))
                        return full;
                }
                catch
                {
                    return "";
                }
            }

            var probes = new List<string>();
            try
            {
                probes.Add(Directory.GetCurrentDirectory());
            }
            catch
            {
                // ignore
            }
            try
            {
                probes.Add(AppContext.BaseDirectory);
            }
            catch
            {
                // ignore
            }

            foreach (var probe in probes)
            {
                var found = FindRepoRootFromPath(probe);
                if (!string.IsNullOrWhiteSpace(found))
                    return found;
            }

            return "";
        }

        private static string FindRepoRootFromPath(string? startPath)
        {
            if (string.IsNullOrWhiteSpace(startPath))
                return "";

            DirectoryInfo? dir;
            try
            {
                dir = new DirectoryInfo(Path.GetFullPath(startPath));
                if (File.Exists(dir.FullName))
                    dir = dir.Parent;
            }
            catch
            {
                return "";
            }

            while (dir != null)
            {
                var csproj = Path.Combine(dir.FullName, "cli", "OperCli", "OperCli.csproj");
                if (File.Exists(csproj))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return "";
        }

        private static void CopyWithFallback(string sourcePath, string destinationPath, string label)
        {
            try
            {
                File.Copy(sourcePath, destinationPath, true);
                Console.WriteLine($"  [ok] {label}");
            }
            catch (IOException ex)
            {
                var fallback = destinationPath + ".new";
                File.Copy(sourcePath, fallback, true);
                Console.WriteLine($"  [warn] não foi possível sobrescrever {label}: {ex.Message}");
                Console.WriteLine($"  [warn] salvo em: {fallback}");
            }
        }

        private static int RunProcess(string fileName, string arguments, string workingDirectory)
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.WorkingDirectory = workingDirectory;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = false;
            process.StartInfo.RedirectStandardError = false;

            process.Start();
            process.WaitForExit();
            return process.ExitCode;
        }

        private static string Quote(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "\"\"";
            if (!text.Contains(" ", StringComparison.Ordinal) && !text.Contains("\t", StringComparison.Ordinal))
                return text;
            return $"\"{text.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }

        private static bool IsHelp(string arg)
        {
            return arg.Equals("help", StringComparison.OrdinalIgnoreCase) ||
                   arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                   arg.Equals("-h", StringComparison.OrdinalIgnoreCase);
        }

        private static string[] ForceDoc(string[] args, string forcedDoc)
        {
            var result = new List<string>(StripDocArgs(args));

            result.Add("--doc");
            result.Add(forcedDoc);
            return result.ToArray();
        }

        private static string[] StripDocArgs(string[] args)
        {
            var result = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i] ?? "";
                if (arg.Equals("--doc", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        i++;
                    continue;
                }
                if (arg.StartsWith("--doc=", StringComparison.OrdinalIgnoreCase))
                    continue;
                result.Add(arg);
            }
            return result.ToArray();
        }

        private static string[] ApplyGlobalOutputConfig(string[] args)
        {
            var config = new OutputConfig();
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith("--return=", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("return=", StringComparison.OrdinalIgnoreCase))
                {
                    var split = arg.Split('=', 2);
                    var fileName = split.Length == 2 ? split[1] : "";
                    ReturnUtils.Enable(fileName);
                    continue;
                }

                if (string.Equals(arg, "--return", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "return", StringComparison.OrdinalIgnoreCase))
                {
                    string fileName = "";
                    if (i + 1 < args.Length)
                    {
                        var next = args[i + 1] ?? "";
                        if (!next.StartsWith("-", StringComparison.Ordinal))
                        {
                            fileName = next;
                            i++;
                        }
                    }

                    ReturnUtils.Enable(fileName);
                    continue;
                }
                if (string.Equals(arg, "--pager", StringComparison.OrdinalIgnoreCase))
                {
                    config.Pager = true;
                    continue;
                }
                if (string.Equals(arg, "--page-size", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out var size) && size > 0)
                        config.PageSize = size;
                    i++;
                    continue;
                }
                if (string.Equals(arg, "--tee", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    config.TeePath = args[i + 1];
                    i++;
                    continue;
                }
                rest.Add(arg);
            }

            OutputManager.Init(config);
            return rest.ToArray();
        }

        private static void PrintCodePreviewIfNeeded(string command, string[] args)
        {
            if (ReturnUtils.IsEnabled())
                return;
            CodePreview.PrintPlannedCode(command, args);
        }

        private static void InitLogger(string[] args)
        {
            if (args == null || args.Length == 0)
                return;

            var hasLog = false;
            foreach (var a in args)
            {
                if (string.Equals(a, "--log", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "log", StringComparison.OrdinalIgnoreCase))
                {
                    hasLog = true;
                    break;
                }
            }

            if (!hasLog)
            {
                var env = Environment.GetEnvironmentVariable("OPERPDF_LOG");
                if (!string.IsNullOrWhiteSpace(env) && env.Trim() == "1")
                    hasLog = true;
            }

            Logger.Enable(hasLog);
        }

        private static void ShowHelp()
        {
            Console.WriteLine("operpdf-textopsalign");
            Console.WriteLine("Uso: operpdf <comando> [opcoes]");
            Console.WriteLine();
            Console.WriteLine("Comando disponível");
            Console.WriteLine("  textopsalign               alinha blocos (padrão despacho)");
            Console.WriteLine("  textopsalign-despacho      pipeline dedicado despacho");
            Console.WriteLine("  textopsalign-certidao      pipeline dedicado certidão");
            Console.WriteLine("  textopsalign-requerimento  pipeline dedicado requerimento");
            Console.WriteLine("  textopsvar                 variáveis (padrão despacho)");
            Console.WriteLine("  textopsvar-despacho        variáveis despacho");
            Console.WriteLine("  textopsvar-certidao        variáveis certidão");
            Console.WriteLine("  textopsvar-requerimento    variáveis requerimento");
            Console.WriteLine("  textopsfixed               fixos (padrão despacho)");
            Console.WriteLine("  textopsfixed-despacho      fixos despacho");
            Console.WriteLine("  textopsfixed-certidao      fixos certidão");
            Console.WriteLine("  textopsfixed-requerimento  fixos requerimento");
            Console.WriteLine("  build-anchor-model-despacho  gera PDF de âncoras do modelo de despacho");
            Console.WriteLine("  build-merged-page          gera PDF com duas páginas combinadas em uma página grande");
            Console.WriteLine("  build-align-exe            publica e atualiza align.exe na raiz");
            Console.WriteLine();
            Console.WriteLine("Global");
            Console.WriteLine("  return/--return [arquivo.json]  JSON puro + salva em io/arquivo.json");
            Console.WriteLine("  --pager          pagina saída");
            Console.WriteLine("  --page-size N    linhas por página");
            Console.WriteLine("  --tee arquivo    salva saída");
            Console.WriteLine();
            Console.WriteLine("Exemplo");
            Console.WriteLine("  operpdf textopsalign-despacho --inputs :D20 --inputs :Q200");
            Console.WriteLine("  operpdf textopsalign-certidao --inputs :D20 --inputs :Q200");
            Console.WriteLine("  operpdf textopsalign-requerimento --inputs :D20 --inputs :Q200");
            Console.WriteLine("  operpdf textopsvar-despacho --inputs :D20 --inputs :Q200");
            Console.WriteLine("  operpdf textopsfixed-despacho --inputs :D20 --inputs :Q200");
            Console.WriteLine("  operpdf build-anchor-model-despacho --model reference/models/tjpb_despacho_model.pdf --out reference/models/tjpb_despacho_anchor_model.pdf");
            Console.WriteLine("  operpdf build-merged-page --input models/nossos/despacho_p1-2.pdf --page-a 1 --page-b 2 --layout vertical");
            Console.WriteLine("  operpdf build-align-exe");
            Console.WriteLine("  operpdf build-align-exe --rid win-x64 --config Release");
        }

        private static void ShowBuildAlignExeHelp()
        {
            Console.WriteLine("Uso: operpdf build-align-exe [opções]");
            Console.WriteLine("Alias: build-exe");
            Console.WriteLine();
            Console.WriteLine("Faz publish e atualiza os executáveis:");
            Console.WriteLine("  ./align.exe");
            Console.WriteLine("  ./operpdf.exe");
            Console.WriteLine("  ./cli/align.exe");
            Console.WriteLine("  ./cli/operpdf.exe");
            Console.WriteLine();
            Console.WriteLine("Opções:");
            Console.WriteLine("  --rid <RID>             padrão: win-x64");
            Console.WriteLine("  --config <CONFIG>       padrão: Release");
            Console.WriteLine("  --repo-root <caminho>   força raiz do repositório");
            Console.WriteLine("  --no-restore            publica sem restore");
        }
    }
}
