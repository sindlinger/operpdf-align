using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Obj.Align;
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
                ObjectsTextOpsAlign.Execute(ForceDocAndTypedModel(rest, "despacho", "@M-DESP"));
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsalign-certidao", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "align-certidao", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsalign", args);
                ObjectsTextOpsAlign.Execute(ForceDocAndTypedModel(rest, "certidao_conselho", "@M-CER"));
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsalign-requerimento", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "align-requerimento", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsalign", args);
                ObjectsTextOpsAlign.Execute(ForceDocAndTypedModel(rest, "requerimento_honorarios", "@M-REQ"));
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
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDocAndTypedModel(rest, "despacho", "@M-DESP"), ObjectsTextOpsAlign.OutputMode.VariablesOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsvar-certidao", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsvar", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDocAndTypedModel(rest, "certidao_conselho", "@M-CER"), ObjectsTextOpsAlign.OutputMode.VariablesOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsvar-requerimento", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsvar", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDocAndTypedModel(rest, "requerimento_honorarios", "@M-REQ"), ObjectsTextOpsAlign.OutputMode.VariablesOnly);
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
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDocAndTypedModel(rest, "despacho", "@M-DESP"), ObjectsTextOpsAlign.OutputMode.FixedOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsfixed-certidao", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsfixed", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDocAndTypedModel(rest, "certidao_conselho", "@M-CER"), ObjectsTextOpsAlign.OutputMode.FixedOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsfixed-requerimento", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsfixed", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDocAndTypedModel(rest, "requerimento_honorarios", "@M-REQ"), ObjectsTextOpsAlign.OutputMode.FixedOnly);
                return ResolveProcessExitCode();
            }

            if (string.Equals(mode, "textopsrun-despacho", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "run-despacho", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsrun", args);
                return ExecuteOrchestratedRun(rest, "despacho", "@M-DESP", "despacho");
            }

            if (string.Equals(mode, "textopsrun-certidao", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "run-certidao", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsrun", args);
                return ExecuteOrchestratedRun(rest, "certidao_conselho", "@M-CER", "certidao");
            }

            if (string.Equals(mode, "textopsrun-requerimento", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "run-requerimento", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsrun", args);
                return ExecuteOrchestratedRun(rest, "requerimento_honorarios", "@M-REQ", "requerimento");
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

        private static int ExecuteOrchestratedRun(string[] rest, string forcedDoc, string typedModelAlias, string docLabel)
        {
            var withLegacyObjDiff = false;
            var filteredRest = new List<string>(rest.Length);
            for (var i = 0; i < rest.Length; i++)
            {
                var arg = (rest[i] ?? "").Trim();
                if (string.Equals(arg, "--with-objdiff", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--objdiff", StringComparison.OrdinalIgnoreCase))
                {
                    withLegacyObjDiff = true;
                    continue;
                }

                filteredRest.Add(rest[i]);
            }

            var normalizedArgs = ForceDocAndTypedModel(filteredRest.ToArray(), forcedDoc, typedModelAlias);
            var runDir = Path.Combine(Directory.GetCurrentDirectory(), "run");
            var ioDir = Path.Combine(runDir, "io");
            Directory.CreateDirectory(runDir);
            Directory.CreateDirectory(ioDir);

            var sessionId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + $"__{docLabel}";
            var sessionDir = Path.Combine(ioDir, sessionId);
            Directory.CreateDirectory(sessionDir);
            var anchorBridgePath = Path.Combine(sessionDir, "anchors.latest.json");

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            Console.WriteLine($"[RUN] sessão: {sessionId}");
            Console.WriteLine($"[RUN] logs: {sessionDir}");

            var steps = new List<OrchestratedStep>();
            if (withLegacyObjDiff)
            {
                steps.Add(new OrchestratedStep(
                    "objdiff-legacy-" + docLabel,
                    "Obj.Align.ObjectsTextOpsDiff(DiffMode=Both)"));
            }
            steps.Add(new OrchestratedStep("textopsalign-" + docLabel, "Obj.Commands.ObjectsTextOpsAlign(OutputMode=All)"));
            steps.Add(new OrchestratedStep("textopsvar-" + docLabel, "Obj.Commands.ObjectsTextOpsAlign(OutputMode=VariablesOnly)"));
            steps.Add(new OrchestratedStep("textopsfixed-" + docLabel, "Obj.Commands.ObjectsTextOpsAlign(OutputMode=FixedOnly)"));

            var manifest = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["session_id"] = sessionId,
                ["doc"] = forcedDoc,
                ["model_alias"] = typedModelAlias,
                ["with_legacy_objdiff"] = withLegacyObjDiff,
                ["anchor_bridge_file"] = anchorBridgePath,
                ["created_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["args"] = normalizedArgs,
                ["steps"] = new List<Dictionary<string, object>>()
            };
            var manifestSteps = (List<Dictionary<string, object>>)manifest["steps"];

            var hasFailure = false;
            for (var i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                var stepNumber = i + 1;
                var request = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sequence"] = stepNumber,
                    ["command"] = step.CommandName,
                    ["module"] = step.ModuleName,
                    ["forced_doc"] = forcedDoc,
                    ["typed_model_alias"] = typedModelAlias,
                    ["anchor_bridge_file"] = anchorBridgePath,
                    ["args"] = normalizedArgs,
                    ["started_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };

                var requestPath = Path.Combine(sessionDir, $"{stepNumber:D2}_{SanitizeFileToken(step.CommandName)}__request.json");
                File.WriteAllText(requestPath, JsonSerializer.Serialize(request, jsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                Console.WriteLine($"[RUN] step {stepNumber}/{steps.Count} -> {step.CommandName}");
                var stopwatch = Stopwatch.StartNew();
                var (exitCode, stdout, stderr) = ExecuteOrchestratedStep(step.CommandName, normalizedArgs, forcedDoc, anchorBridgePath, sessionDir, stepNumber);
                stopwatch.Stop();

                var stdoutPath = Path.Combine(sessionDir, $"{stepNumber:D2}_{SanitizeFileToken(step.CommandName)}__stdout.log");
                var stderrPath = Path.Combine(sessionDir, $"{stepNumber:D2}_{SanitizeFileToken(step.CommandName)}__stderr.log");
                File.WriteAllText(stdoutPath, stdout, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.WriteAllText(stderrPath, stderr, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                var response = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sequence"] = stepNumber,
                    ["command"] = step.CommandName,
                    ["module"] = step.ModuleName,
                    ["exit_code"] = exitCode,
                    ["duration_ms"] = stopwatch.ElapsedMilliseconds,
                    ["finished_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["stdout_path"] = stdoutPath,
                    ["stderr_path"] = stderrPath,
                    ["stdout_len"] = stdout.Length,
                    ["stderr_len"] = stderr.Length
                };

                var responsePath = Path.Combine(sessionDir, $"{stepNumber:D2}_{SanitizeFileToken(step.CommandName)}__response.json");
                File.WriteAllText(responsePath, JsonSerializer.Serialize(response, jsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                manifestSteps.Add(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sequence"] = stepNumber,
                    ["command"] = step.CommandName,
                    ["module"] = step.ModuleName,
                    ["request_path"] = requestPath,
                    ["response_path"] = responsePath,
                    ["stdout_path"] = stdoutPath,
                    ["stderr_path"] = stderrPath,
                    ["exit_code"] = exitCode
                });

                if (exitCode != 0)
                {
                    hasFailure = true;
                    Console.WriteLine($"[RUN] falha em {step.CommandName} (exit={exitCode}).");
                    break;
                }
            }

            manifest["status"] = hasFailure ? "fail" : "ok";
            manifest["finished_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var manifestPath = Path.Combine(sessionDir, "manifest.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, jsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var latestPath = Path.Combine(ioDir, "latest_session.txt");
            File.WriteAllText(latestPath, sessionDir, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine($"[RUN] manifest: {manifestPath}");

            Environment.ExitCode = hasFailure ? 1 : 0;
            return hasFailure ? 1 : 0;
        }

        private static (int ExitCode, string Stdout, string Stderr) ExecuteOrchestratedStep(
            string commandName,
            string[] args,
            string forcedDoc,
            string anchorBridgePath,
            string sessionDir,
            int stepNumber)
        {
            var stdoutCapture = new StringWriter(CultureInfo.InvariantCulture);
            var stderrCapture = new StringWriter(CultureInfo.InvariantCulture);
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var teeOut = new TeeTextWriter(originalOut, stdoutCapture);
            using var teeErr = new TeeTextWriter(originalErr, stderrCapture);

            Console.SetOut(teeOut);
            Console.SetError(teeErr);

            var exitCode = 0;
            var prevAnchorBridge = Environment.GetEnvironmentVariable("OBJ_TEXTOPSALIGN_ANCHOR_BRIDGE");
            var prevRunSessionDir = Environment.GetEnvironmentVariable("OBJ_TEXTOPSALIGN_RUN_SESSION_DIR");
            var prevRunStep = Environment.GetEnvironmentVariable("OBJ_TEXTOPSALIGN_RUN_STEP");
            try
            {
                if (!string.IsNullOrWhiteSpace(anchorBridgePath))
                    Environment.SetEnvironmentVariable("OBJ_TEXTOPSALIGN_ANCHOR_BRIDGE", anchorBridgePath);
                if (!string.IsNullOrWhiteSpace(sessionDir))
                    Environment.SetEnvironmentVariable("OBJ_TEXTOPSALIGN_RUN_SESSION_DIR", sessionDir);
                Environment.SetEnvironmentVariable("OBJ_TEXTOPSALIGN_RUN_STEP", stepNumber.ToString(CultureInfo.InvariantCulture));

                Environment.ExitCode = 0;
                if (commandName.StartsWith("textopsalign-", StringComparison.OrdinalIgnoreCase))
                {
                    ObjectsTextOpsAlign.Execute(args);
                }
                else if (commandName.StartsWith("textopsvar-", StringComparison.OrdinalIgnoreCase))
                {
                    ObjectsTextOpsAlign.ExecuteWithMode(args, ObjectsTextOpsAlign.OutputMode.VariablesOnly);
                }
                else if (commandName.StartsWith("textopsfixed-", StringComparison.OrdinalIgnoreCase))
                {
                    ObjectsTextOpsAlign.ExecuteWithMode(args, ObjectsTextOpsAlign.OutputMode.FixedOnly);
                }
                else if (commandName.StartsWith("objdiff-legacy-", StringComparison.OrdinalIgnoreCase))
                {
                    var diffArgs = BuildLegacyObjDiffArgs(args, forcedDoc);
                    if (diffArgs.Length == 0)
                    {
                        Console.Error.WriteLine("Não foi possível montar args do objdiff legado (modelo/alvo ausentes).");
                        Environment.ExitCode = 2;
                    }
                    else
                    {
                        ObjectsTextOpsDiff.Execute(diffArgs, ObjectsTextOpsDiff.DiffMode.Both);
                    }
                }
                else
                {
                    Console.Error.WriteLine($"Comando orquestrado não suportado: {commandName}");
                    Environment.ExitCode = 1;
                }

                exitCode = ResolveProcessExitCode();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Erro ao executar etapa orquestrada {commandName}: {ex}");
                exitCode = 1;
            }
            finally
            {
                Environment.SetEnvironmentVariable("OBJ_TEXTOPSALIGN_ANCHOR_BRIDGE", prevAnchorBridge);
                Environment.SetEnvironmentVariable("OBJ_TEXTOPSALIGN_RUN_SESSION_DIR", prevRunSessionDir);
                Environment.SetEnvironmentVariable("OBJ_TEXTOPSALIGN_RUN_STEP", prevRunStep);
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }

            return (exitCode, stdoutCapture.ToString(), stderrCapture.ToString());
        }

        private static string[] BuildLegacyObjDiffArgs(string[] args, string forcedDoc)
        {
            var inputs = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = (args[i] ?? "").Trim();
                if (arg.Equals("--inputs", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    AddInputTokens(args[++i], inputs);
                    continue;
                }

                if (arg.StartsWith("--inputs=", StringComparison.OrdinalIgnoreCase))
                {
                    var split = arg.Split('=', 2);
                    AddInputTokens(split.Length == 2 ? split[1] : "", inputs);
                    continue;
                }
            }

            if (inputs.Count < 2)
                return Array.Empty<string>();

            var model = inputs[0];
            var target = inputs[1];
            return new[]
            {
                "--inputs", $"{model},{target}",
                "--op", "Tj,TJ",
                "--doc", forcedDoc
            };
        }

        private static string SanitizeFileToken(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "item";

            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw!)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')
                    sb.Append(c);
                else
                    sb.Append('_');
            }

            var text = sb.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(text) ? "item" : text;
        }

        private readonly struct OrchestratedStep
        {
            public OrchestratedStep(string commandName, string moduleName)
            {
                CommandName = commandName;
                ModuleName = moduleName;
            }

            public string CommandName { get; }
            public string ModuleName { get; }
        }

        private sealed class TeeTextWriter : TextWriter
        {
            private readonly TextWriter _left;
            private readonly TextWriter _right;

            public TeeTextWriter(TextWriter left, TextWriter right)
            {
                _left = left;
                _right = right;
            }

            public override Encoding Encoding => _left.Encoding;

            public override void Write(char value)
            {
                _left.Write(value);
                _right.Write(value);
            }

            public override void Write(string? value)
            {
                _left.Write(value);
                _right.Write(value);
            }

            public override void WriteLine(string? value)
            {
                _left.WriteLine(value);
                _right.WriteLine(value);
            }

            public override void Flush()
            {
                _left.Flush();
                _right.Flush();
            }
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

        private static string[] ForceDocAndTypedModel(string[] args, string forcedDoc, string typedModelAlias)
        {
            var stripped = StripDocArgs(args);
            var passthrough = new List<string>(stripped.Length + 4);
            var inputTokens = new List<string>();

            for (var i = 0; i < stripped.Length; i++)
            {
                var arg = stripped[i] ?? "";
                if (arg.Equals("--inputs", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < stripped.Length)
                    {
                        AddInputTokens(stripped[++i], inputTokens);
                    }
                    continue;
                }

                if (arg.StartsWith("--inputs=", StringComparison.OrdinalIgnoreCase))
                {
                    var split = arg.Split('=', 2);
                    AddInputTokens(split.Length == 2 ? split[1] : "", inputTokens);
                    continue;
                }

                passthrough.Add(arg);
            }

            NormalizeTypedModelTokens(inputTokens, typedModelAlias);

            var result = new List<string>(passthrough.Count + (inputTokens.Count * 2) + 4);
            foreach (var token in inputTokens)
            {
                result.Add("--inputs");
                result.Add(token);
            }
            result.AddRange(passthrough);
            result.Add("--doc");
            result.Add(forcedDoc);
            return result.ToArray();
        }

        private static void AddInputTokens(string? raw, List<string> inputTokens)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return;

            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim();
                if (token.Length == 0)
                    continue;
                inputTokens.Add(token);
            }
        }

        private static void NormalizeTypedModelTokens(List<string> inputTokens, string typedModelAlias)
        {
            for (var i = 0; i < inputTokens.Count; i++)
            {
                if (IsGlobalModelAliasToken(inputTokens[i]))
                    inputTokens[i] = typedModelAlias;
            }

            if (inputTokens.Count == 0)
            {
                inputTokens.Add(typedModelAlias);
                return;
            }

            if (inputTokens.Count == 1)
            {
                var only = inputTokens[0];
                if (IsAnyTypedModelAliasToken(only))
                {
                    inputTokens[0] = typedModelAlias;
                }
                else
                {
                    inputTokens.Insert(0, typedModelAlias);
                }
                return;
            }

            inputTokens[0] = typedModelAlias;
        }

        private static bool IsGlobalModelAliasToken(string? token)
        {
            return string.Equals((token ?? "").Trim(), "@MODEL", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAnyTypedModelAliasToken(string? token)
        {
            var t = (token ?? "").Trim();
            return t.StartsWith("@M-", StringComparison.OrdinalIgnoreCase);
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
            Console.WriteLine("  textopsrun-despacho        orquestra align/var/fixed (despacho) + trilha IO em run/io");
            Console.WriteLine("  textopsrun-certidao        orquestra align/var/fixed (certidão) + trilha IO em run/io");
            Console.WriteLine("  textopsrun-requerimento    orquestra align/var/fixed (requerimento) + trilha IO em run/io");
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
            Console.WriteLine("  operpdf textopsrun-despacho run 1-8 --inputs @M-DESP --inputs :Q22");
            Console.WriteLine("  operpdf textopsrun-despacho run 1-8 --inputs @M-DESP --inputs :Q22 --with-objdiff");
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
