using System;
using System.Collections.Generic;
using System.Text;
using Obj.Commands;
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

            var mode = (args[0] ?? "").Trim().ToLowerInvariant();
            var rest = args.Length > 1 ? args[1..] : Array.Empty<string>();
            if (string.Equals(mode, "textopsalign", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsalign", args);
                ObjectsTextOpsAlign.Execute(StripDocArgs(rest));
                return 0;
            }

            if (string.Equals(mode, "textopsalign-despacho", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "align-despacho", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsalign", args);
                ObjectsTextOpsAlign.Execute(ForceDoc(rest, "despacho"));
                return 0;
            }

            if (string.Equals(mode, "textopsalign-certidao", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "align-certidao", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsalign", args);
                ObjectsTextOpsAlign.Execute(ForceDoc(rest, "certidao_conselho"));
                return 0;
            }

            if (string.Equals(mode, "textopsalign-requerimento", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "align-requerimento", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsalign", args);
                ObjectsTextOpsAlign.Execute(ForceDoc(rest, "requerimento_honorarios"));
                return 0;
            }

            if (string.Equals(mode, "textopsvar", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsvar", args);
                ObjectsTextOpsAlign.ExecuteWithMode(StripDocArgs(rest), ObjectsTextOpsAlign.OutputMode.VariablesOnly);
                return 0;
            }

            if (string.Equals(mode, "textopsvar-despacho", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsvar", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDoc(rest, "despacho"), ObjectsTextOpsAlign.OutputMode.VariablesOnly);
                return 0;
            }

            if (string.Equals(mode, "textopsvar-certidao", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsvar", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDoc(rest, "certidao_conselho"), ObjectsTextOpsAlign.OutputMode.VariablesOnly);
                return 0;
            }

            if (string.Equals(mode, "textopsvar-requerimento", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsvar", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDoc(rest, "requerimento_honorarios"), ObjectsTextOpsAlign.OutputMode.VariablesOnly);
                return 0;
            }

            if (string.Equals(mode, "textopsfixed", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsfixed", args);
                ObjectsTextOpsAlign.ExecuteWithMode(StripDocArgs(rest), ObjectsTextOpsAlign.OutputMode.FixedOnly);
                return 0;
            }

            if (string.Equals(mode, "textopsfixed-despacho", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsfixed", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDoc(rest, "despacho"), ObjectsTextOpsAlign.OutputMode.FixedOnly);
                return 0;
            }

            if (string.Equals(mode, "textopsfixed-certidao", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsfixed", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDoc(rest, "certidao_conselho"), ObjectsTextOpsAlign.OutputMode.FixedOnly);
                return 0;
            }

            if (string.Equals(mode, "textopsfixed-requerimento", StringComparison.OrdinalIgnoreCase))
            {
                PrintCodePreviewIfNeeded("textopsfixed", args);
                ObjectsTextOpsAlign.ExecuteWithMode(ForceDoc(rest, "requerimento_honorarios"), ObjectsTextOpsAlign.OutputMode.FixedOnly);
                return 0;
            }

            if (string.Equals(mode, "build-anchor-model-despacho", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "anchor-model-despacho", StringComparison.OrdinalIgnoreCase))
            {
                BuildAnchorModelDespacho.Execute(rest);
                return 0;
            }

            Console.Error.WriteLine($"Comando não suportado neste projeto: {mode}");
            ShowHelp();
            return 1;
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
        }
    }
}
